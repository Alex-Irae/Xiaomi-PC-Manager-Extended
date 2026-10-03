"""Explicit device profiles and local calibration. Never silently selects CPU inference."""
import hashlib
import json
import platform
import statistics
import os
from pathlib import Path
from time import perf_counter

MODES = {"Balanced": ["NPU", "GPU", "GPU"],
         "Maximum performance": ["GPU", "GPU", "GPU"],
         "NPU OCR": ["NPU", "NPU", "GPU"],
         "Low power": ["NPU", "NPU", "NPU"],
         "Compatibility": ["CPU", "CPU", "CPU"]}

AUTO_POLICY = "native-v4-fixed-ctc-top1-1280-det-f32-validated"


def automatic_pipeline(root, batch_size=8, incremental=True, progress=print):
    """Select actual stage devices by warm local model timings, cache by hardware/model identity.

    Uses generated model inputs and public Chinese sentences, never a screen capture.
    These timings rank devices for this workload; they are not live overlay latency.
    """
    import gc
    import numpy as np
    import openvino as ov
    from .cli import result_directory, save_json, environment
    from .core import Pipeline
    from .models import validate, digest
    from .ocr import OCR
    from .runtime import compile_model, Translator, strict_offline

    strict_offline()
    root = Path(root).resolve()
    manifest = validate(root)
    hardware = identity()
    signature = {"policy": AUTO_POLICY, "hardware": hardware["fingerprint"],
                 "manifest": digest(root/"manifest.json"), "batch_size": batch_size}
    config = {**manifest["ocr"], "detector_side":1280,"tile_overlap":192}
    data = Path(os.environ["SCREEN_TRANSLATOR_DATA"]) if os.environ.get("SCREEN_TRANSLATOR_DATA") else root/"model_cache"
    cache = data/"model_cache"/signature["manifest"]/"native-device-choice.json" if os.environ.get("SCREEN_TRANSLATOR_DATA") else data/"native-device-choice.json"
    core = ov.Core()
    candidates = sorted(core.available_devices, key=lambda d: (d.split(".")[0] not in ("NPU", "GPU"), d))
    if not candidates:
        raise RuntimeError("OpenVINO exposes no usable device")
    cached = None
    seed=root.parents[1]/"calibration-seed.json"
    if not cache.exists() and seed.is_file():
        saved=json.loads(seed.read_text(encoding="utf-8"))
        if saved.get("signature")==signature:
            cache.parent.mkdir(parents=True,exist_ok=True)
            cache.write_text(json.dumps(saved,indent=2),encoding="utf-8")
    if cache.is_file():
        try:
            saved = json.loads(cache.read_text(encoding="utf-8"))
            if saved["signature"] == signature and all(device in candidates for device in saved["devices"]):
                cached = saved["devices"]
        except (ValueError, KeyError, TypeError):
            pass
    rows = []

    def choose(stage, build, infer, preferred=None):
        devices = [preferred] if preferred else candidates
        winner, best, record = None, float("inf"), None
        for device in devices:
            progress(f"Automatic hardware: {stage} on {device}")
            model = None
            try:
                model = build(device)
                infer(model)  # discard warm-up / initial kernel specialization
                durations = []
                for _ in range(3):
                    start = perf_counter()
                    infer(model)
                    durations.append((perf_counter()-start)*1000)
                median = statistics.median(durations)
                item = {"stage": stage, "device": device, "ok": True, "median_ms": median, "samples_ms": durations}
                rows.append(item)
                progress(f"  {stage}: {median:.1f} ms on {device}")
                if median < best:
                    winner, best, record = model, median, item
            except Exception as exc:
                rows.append({"stage": stage, "device": device, "ok": False, "error": str(exc)})
                progress(f"  {stage} unavailable on {device}: {type(exc).__name__}: {exc}")
            model = None
            gc.collect()
        if winner is None and preferred:
            progress(f"Cached {stage} device failed; measuring available devices again")
            return choose(stage, build, infer)
        if winner is None:
            raise RuntimeError(f"No available device runs {stage}. Details: {rows}")
        return winner, record["device"]

    # Match the fixed graph shapes to avoid repeated dynamic-width GPU compilation.
    side, height, width = config["detector_side"], *config["recognition_hw"]
    from PIL import Image, ImageDraw, ImageFont
    fixture = Image.new("RGB", (side,side), "white")
    draw=ImageDraw.Draw(fixture)
    font_path=Path("C:/Windows/Fonts/msyh.ttc")
    if not font_path.is_file():
        raise RuntimeError("Automatic OCR validation needs the Windows Microsoft YaHei font")
    font=ImageFont.truetype(str(font_path),32)
    validation_lines=[(60,60,"设置 Settings"),(700,60,"请选择要打开的文件"),
                      (60,600,"网络和设备"),(700,600,"English 2026"),(60,1100,"检查更新")]
    for x,y,text in validation_lines:
        draw.text((x,y),text,font=font,fill="black")
    # Compare candidate segmentation on real generated glyphs, not a blank tensor.
    normalized=np.asarray(fixture).astype(np.float32)/127.5-1  # [S,S,3]
    det_input=normalized[:,:,::-1].transpose(2,0,1)[None].copy()  # [1,3,S,S], BGR
    rec_input = np.zeros((config["recognition_batch"], 3, height, width), np.float32)  # [B,3,H,W]
    progress("Validating detector candidates against the CPU reference")
    reference, _ = compile_model(core,root/"ocr/det.onnx","CPU",list(det_input.shape))
    reference_mask=reference([det_input])[0]>config["detection_threshold"]  # [1,1,S,S]
    if not reference_mask.any():
        raise RuntimeError("Reference detector missed the generated validation text")
    del reference
    def detect(pair):
        output=pair[0]([det_input])[0]  # [1,1,S,S], probability
        mask=output>config["detection_threshold"]
        # Intersection-over-union tests foreground agreement without empty-background dominance.
        union=np.logical_or(mask,reference_mask).sum()
        agreement=float(np.logical_and(mask,reference_mask).sum()/max(1,union))
        if not np.isfinite(output).all() or agreement<.9:
            raise RuntimeError(f"Detector foreground agreement {agreement:.3f} is below 0.90")
        pair[1]["validation_foreground_iou"]=agreement
    det, detector_device = choose("text detection", lambda d: compile_model(core, root/"ocr/det.onnx", d,
        list(det_input.shape), precision="f32"), detect, cached[0] if cached else None)
    rec, recognizer_device = choose("text recognition", lambda d: compile_model(core, root/"ocr/rec.onnx", d,
        list(rec_input.shape), ctc_top1=True), lambda pair: pair[0]([rec_input]), cached[1] if cached else None)
    # A small fixed public workload includes short UI language and a complete sentence.
    texts = ["请选择要打开的文件。", "此功能完全在本机运行。", "正在检查更新。", "设备连接成功。"]
    def translate(model):
        outputs = model.batch(texts)
        if len(outputs) != len(texts) or any(not text.strip() for text in outputs):
            raise RuntimeError("Translation warm-up returned an incomplete result")
    translator, translator_device = choose("translation", lambda d: Translator(root, d, manifest, progress),
                                          translate, cached[2] if cached else None)
    ocr = OCR(root, detector_device, recognizer_device, config, progress, compiled=(*det, *rec))
    ocr.warmup()
    devices = [detector_device, recognizer_device, translator_device]
    if not cached or devices != cached:
        directory = result_directory(data/"results/automatic" if os.environ.get("SCREEN_TRANSLATOR_DATA") else Path(__file__).resolve().parents[1]/"results/automatic")
        save_json(directory/"config.json", {"seed": 0, "signature": signature, "hardware": hardware,
            "model_manifest": manifest, "ocr": config, "batch_size": batch_size, "repeats": 3,
            "candidates": candidates, "detector_input": list(det_input.shape), "recognizer_input": list(rec_input.shape),
            "translation_inputs": texts, "environment": environment(),
            "detector_validation": {"font":str(font_path),"font_size":32,"reference":"CPU", "minimum_foreground_iou":.9,"lines":validation_lines},
            "protocol": "Warm per-stage model inference with generated glyph tensors/public text; detector foreground validated against CPU; no capture, layout or rendering"})
        summary = {"signature": signature, "devices": devices, "stages": rows}
        save_json(directory/"summary.json", summary)
        # A local SVG exposes each successful stage's relative warm timing; failures stay in JSON.
        bars = [row for row in rows if row["ok"]]
        maximum = max([1]+[row["median_ms"] for row in bars])
        svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="850" height="'+str(70+35*len(bars))+'">',
               '<rect width="100%" height="100%" fill="white"/><text x="20" y="25">Warm stage inference (ms), lower is faster. Not live translation latency.</text>']
        for index, row in enumerate(bars):
            y = 45+35*index
            svg.append(f'<text x="20" y="{y+16}">{row["stage"]} / {row["device"]}</text><rect x="260" y="{y}" width="{row["median_ms"]/maximum*480:.1f}" height="22" fill="#3482ff"/><text x="750" y="{y+16}">{row["median_ms"]:.1f}</text>')
        svg.append('</svg>')
        (directory/"latency.svg").write_text("\n".join(svg), encoding="utf-8")
        cache.parent.mkdir(parents=True,exist_ok=True)
        cache.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    progress("Automatic device choice: " + " / ".join(devices))
    pipeline = Pipeline(ocr, translator, batch_size=batch_size, incremental=incremental,
                        identity=signature["manifest"]+AUTO_POLICY)
    return pipeline, {"ocr": ocr.info, "translator": translator.info, "models": manifest["models"],
                      "automatic": {"devices": devices, "cached": bool(cached), "stages": rows}, "preprocessing": config}


def identity():
    """Fingerprint the actual OpenVINO devices, exposed drivers and OS/runtime versions."""
    import openvino as ov
    core = ov.Core()
    devices = []
    for name in core.available_devices:
        item = {"id": name, "name": core.get_property(name, "FULL_DEVICE_NAME")}
        supported = {str(value) for value in core.get_property(name, "SUPPORTED_PROPERTIES")}
        if "DRIVER_VERSION" in supported:
            item["driver"] = str(core.get_property(name, "DRIVER_VERSION"))
        devices.append(item)
    details = {"openvino": ov.__version__, "os": platform.platform(), "devices": devices}
    fingerprint = hashlib.sha256(json.dumps(details, sort_keys=True).encode()).hexdigest()
    return {"fingerprint": fingerprint, **details}


def choose_profile(rows):
    """Pick fastest successful accelerator profile. CPU remains a comparison/explicit mode."""
    candidates = [row for row in rows if row.get("ok") and
                  any(device.split(".", 1)[0] in ("GPU", "NPU") for device in row["devices"])]
    return min(candidates, key=lambda row: row["p95_ms"]) if candidates else None


def calibrate(root, image, batch_size=8, repeats=3, results="results/calibration", progress=print):
    """Measure actual screenshot OCR plus a fixed public NMT corpus, save local timings.

    No screenshot, recognized text or translation is written. This is a user-triggered
    calibration, not a claim about live capture/rendering or battery consumption.
    """
    from .cli import make_pipeline, result_directory, save_json, percentile, latency_figure, environment
    from .models import validate, digest
    import gc
    root = Path(root).resolve()
    manifest = validate(root)
    hardware = identity()
    directory = result_directory(results)
    profiles = [MODES[mode] for mode in ("Compatibility", "Balanced", "Maximum performance", "NPU OCR", "Low power")]
    corpus = json.loads(Path(__file__).with_name("corpus.json").read_text(encoding="utf-8"))
    texts = [item["source"] for item in corpus]
    save_json(directory/"config.json", {"seed": 0, "model_manifest": manifest,
        "hardware": hardware, "devices": profiles, "batch_size": batch_size, "repeats": repeats,
        "image_size": image.size, "image_sha256": hashlib.sha256(image.tobytes()).hexdigest(),
        "corpus_sha256": digest(Path(__file__).with_name("corpus.json")), "environment": environment(),
        "protocol": "Resident warm models; uncached screenshot OCR/NMT plus the full public NMT corpus. No capture/layout/render timing. No screen data saved.",
        "tile_size": 256, "padding": 32, "incremental": False, "acceptance_threshold_ms": None})
    rows = []
    for index, devices in enumerate(profiles, 1):
        progress(f"[{index}/{len(profiles)}] Calibrating {' / '.join(devices)}")
        pipeline = None
        try:
            pipeline, info = make_pipeline(root, devices, batch_size, incremental=False, progress=progress)
            durations, stages = [], []
            for iteration in range(repeats):
                start = perf_counter()
                _, timing = pipeline.run(image, use_cache=False)
                corpus_start = perf_counter()
                for offset in range(0, len(texts), batch_size):
                    outputs = pipeline.translator.batch(texts[offset:offset+batch_size])
                    if len(outputs) != len(texts[offset:offset+batch_size]) or any(not text.strip() for text in outputs):
                        raise RuntimeError("Incomplete calibration translation")
                timing["fixed_corpus_ms"] = (perf_counter()-corpus_start)*1000
                durations.append((perf_counter()-start)*1000)
                stages.append(timing)
                progress(f"  warm {iteration+1}/{repeats}: {durations[-1]:.1f} ms")
            rows.append({"ok": True, "devices": devices, "info": info,
                         "median_ms": statistics.median(durations), "p95_ms": percentile(durations), "stages": stages})
        except Exception as exc:
            rows.append({"ok": False, "devices": devices, "error": f"{type(exc).__name__}: {exc}"})
            progress(f"  Failed: {type(exc).__name__}: {exc}")
        finally:
            pipeline = None
            gc.collect()
    winner = choose_profile(rows)
    summary = {"profiles": rows, "fastest_measured_devices": winner["devices"] if winner else None,
               "hardware": hardware, "model_manifest_sha256": digest(root/"manifest.json"),
               "batch_size": batch_size, "protocol": "Calibration workload, not a live acceptance run"}
    save_json(directory/"summary.json", summary)
    latency_figure(directory, rows, calibration=True)
    if winner is None:
        raise RuntimeError(f"No accelerator profile succeeded; CPU was not selected. Details: {directory/'summary.json'}")
    progress(f"Measured accelerator choice: {' / '.join(winner['devices'])}")
    return str((directory/"summary.json").resolve())
