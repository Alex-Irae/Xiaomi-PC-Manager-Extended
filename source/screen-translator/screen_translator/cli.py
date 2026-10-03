"""Local hardware/pipeline benchmark and model inventory.

Dependencies: root requirements.txt. Outputs: new results/NNN_timestamp_seed0/.
Commands (from the project root):
  python -m screen_translator.cli probe
  python -m screen_translator.cli benchmark --models models/zh-en --image fixture.png
  python -m screen_translator.cli pipeline --models models/zh-en --image fixture.png --devices NPU NPU GPU
  python -m screen_translator.cli seal --models models/zh-en --metadata metadata.json
No automatic downloads. Screenshot output exists only for an explicitly supplied fixture.
"""
import argparse
import csv
from datetime import datetime, timezone
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import re
import statistics
from time import perf_counter

from .models import digest, validate, seal


def progress(message):
    print(message, flush=True)


def save_json(path, payload):
    with Path(path).open("x", encoding="utf-8") as stream:
        json.dump(payload, stream, ensure_ascii=False, indent=2)


def result_directory(root):
    root = Path(root)
    root.mkdir(parents=True, exist_ok=True)
    index = max([int(p.name.split("_", 1)[0]) for p in root.iterdir()
                 if p.is_dir() and re.match(r"^\d+_", p.name)] or [0])+1
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    while True:
        directory = root/f"{index:03d}_{stamp}_seed0"
        try:
            directory.mkdir()
            return directory
        except FileExistsError:
            index += 1


def environment():
    packages = {}
    for name in ("openvino", "optimum-intel", "transformers", "torch", "PyQt5", "rapidocr-onnxruntime"):
        try:
            packages[name] = importlib.metadata.version(name)
        except importlib.metadata.PackageNotFoundError:
            packages[name] = "not installed"
    return {"python": platform.python_version(), "platform": platform.platform(), "packages": packages}


def percentile(values, fraction=.95):
    values = sorted(values)
    return values[max(0, min(len(values)-1, int(len(values)*fraction+.999)-1))]


def character_accuracy(expected, actual):
    """1 minus CER, whitespace ignored. Insertions can make accuracy negative."""
    expected, actual = re.sub(r"\s", "", expected), re.sub(r"\s", "", actual)
    if not expected:
        raise ValueError("Ground truth must contain characters")
    previous = list(range(len(actual)+1))
    for i, a in enumerate(expected, 1):
        row = [i]
        for j, b in enumerate(actual, 1):
            row.append(min(row[-1]+1, previous[j]+1, previous[j-1]+(a != b)))
        previous = row
    return 1-previous[-1]/len(expected)


def make_pipeline(root, devices, batch_size=8, incremental=True, progress=progress):
    progress("Importing local inference engines")
    from .core import Pipeline
    from .ocr import OCR
    from .runtime import Translator, strict_offline
    strict_offline()
    progress("Validating local model checksums")
    manifest = validate(root)
    progress(f"Compiling OCR detector on {devices[0]}, recognizer on {devices[1]}")
    start = perf_counter()
    ocr = OCR(root, devices[0], devices[1], manifest["ocr"], progress=progress)
    progress(f"Compiling translator on {devices[2]}")
    translator = Translator(root, devices[2], manifest=manifest, progress=progress)
    progress("Warming resident models")
    ocr.warmup()
    translator.batch(["这是本地翻译测试。"]*batch_size)
    pipeline = Pipeline(ocr, translator, batch_size=batch_size, incremental=incremental,
                        identity=digest(Path(root)/"manifest.json"))
    return pipeline, {"model_initialization_ms": (perf_counter()-start)*1000,
                      "ocr": ocr.info, "translator": translator.info, "models": manifest["models"]}


def latency_figure(directory, rows, calibration=False):
    """A simple native SVG: p95 warm complete pipeline latency, including layout/render."""
    successful = [r for r in rows if r.get("ok")]
    width, height = 850, 105+len(rows)*48
    maximum = max([2000]+[r["p95_ms"] for r in successful])
    scale = 560/maximum
    elements = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
                '<rect width="100%" height="100%" fill="#f5f6f8"/>',
                '<g font-family="Segoe UI, sans-serif" font-size="14" fill="#202124">',
                f'<text x="24" y="30" font-size="20">{"Calibration workload" if calibration else "Warm pipeline latency"} (p95, milliseconds)</text>',
                '<text x="24" y="53">OCR + screenshot translation + public NMT corpus. No acceptance threshold.</text>' if calibration else
                '<text x="24" y="53">Model initialization excluded. Orange line: 2000 ms acceptance target.</text>']
    for i, row in enumerate(rows):
        y = 85+i*48
        label = "/".join(row["devices"])
        elements.append(f'<text x="24" y="{y+17}">{label}</text>')
        if row["ok"]:
            length = row["p95_ms"]*scale
            elements.append(f'<rect x="225" y="{y}" width="{length:.1f}" height="26" rx="4" fill="#3276f5"/>')
            elements.append(f'<text x="{min(800, 232+length):.1f}" y="{y+18}">{row["p95_ms"]:.0f}</text>')
        else:
            elements.append(f'<text x="225" y="{y+18}" fill="#c53b32">Unsupported / failed. See summary.json.</text>')
    x = 225+2000*scale
    if not calibration:
        elements.append(f'<path d="M{x:.1f} 66 V{height-20}" stroke="#ff6900" stroke-width="2"/>')
    elements.append('</g></svg>')
    (directory/"latency.svg").write_text("\n".join(elements), encoding="utf-8")


def benchmark(args, directory):
    from PIL import Image
    from PyQt5 import QtWidgets
    from .core import serialize
    from .render import render_image
    # A QGuiApplication is required for font metrics even when saving image fixtures.
    os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
    application = QtWidgets.QApplication.instance() or QtWidgets.QApplication([])
    image = Image.open(args.image).convert("RGB")
    expected = Path(args.truth).read_text(encoding="utf-8") if args.truth else None
    corpus = json.loads(Path(args.corpus).read_text(encoding="utf-8"))
    if not corpus or any(not row.get("source") for row in corpus):
        raise ValueError("Corpus must contain nonempty source strings")
    reports, measurements = [], []
    profiles = [args.devices] if args.command == "pipeline" else [pair.split("/") for pair in args.pairs]
    for index, devices in enumerate(profiles):
        if len(devices) != 3:
            raise ValueError("Each device profile must be detector/recognizer/translator")
        label = " / ".join(devices)
        progress(f"[{index+1}/{len(profiles)}] {label}")
        pipeline = None
        try:
            pipeline, info = make_pipeline(args.models, devices, args.batch_size)
            warm = []
            for iteration in range(args.repeats):
                start = perf_counter()
                regions, timing = pipeline.run(image, use_cache=False)
                output, render_timing = render_image(image, regions)
                timing.update(render_timing)
                timing["pipeline_total_ms"] = (perf_counter()-start)*1000
                warm.append(timing["pipeline_total_ms"])
                measurements.append({"profile": index, "iteration": iteration, **{k: v for k, v in timing.items() if isinstance(v, (int, float))}})
                progress(f"  warm {iteration+1}/{args.repeats}: {warm[-1]:.1f} ms; {len(regions)} regions; {timing['clipped_regions']} clipped")
            if not output.save(str(directory/f"profile-{index}-translated.png")):
                raise RuntimeError("Cannot save translated fixture")
            save_json(directory/f"profile-{index}-regions.json", serialize(regions))
            # Exercise exact-frame cache independently of the uncached measurement protocol.
            pipeline.run(image, use_cache=True)
            _, cached = pipeline.run(image, use_cache=True)
            translations = []
            start = perf_counter()
            for offset in range(0, len(corpus), args.batch_size):
                batch = corpus[offset:offset+args.batch_size]
                text = pipeline.translator.batch([item["source"] for item in batch])
                if len(text) != len(batch):
                    raise RuntimeError("Corpus translator returned incomplete batch")
                translations.extend({**item, "translation": result,
                                     "numbers_preserved": sorted(re.findall(r"\d+", item["source"])) == sorted(re.findall(r"\d+", result))}
                                    for item, result in zip(batch, text))
            save_json(directory/f"profile-{index}-corpus.json", translations)
            report = {"devices": devices, "ok": True, "info": info, "median_ms": statistics.median(warm),
                      "p95_ms": percentile(warm), "cached_pipeline_ms": cached["total_ms"],
                      "clipped_regions": timing["clipped_regions"], "regions": len(regions),
                      "warm_latency_target_met": percentile(warm) < 2000,
                      "corpus_ms": (perf_counter()-start)*1000,
                      "numbers_preserved": all(item["numbers_preserved"] for item in translations),
                      "character_accuracy": character_accuracy(expected, "".join(r.source for r in regions)) if expected else None}
            report["chinese_strings"] = timing["translated_strings"]
            reports.append(report)
        except Exception as exc:
            progress(f"  FAILED: {type(exc).__name__}: {exc}")
            reports.append({"devices": devices, "ok": False, "error": f"{type(exc).__name__}: {exc}"})
        finally:
            # Benchmark recompilation is intentional; normal app operation retains one pipeline.
            pipeline = None
            import gc
            gc.collect()
    keys = sorted({key for row in measurements for key in row})
    with (directory/"metrics.csv").open("x", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=keys)
        writer.writeheader()
        writer.writerows(measurements)
    successful = [r for r in reports if r["ok"] and r["chinese_strings"] > 0]
    from .hardware import choose_profile, identity
    fastest = choose_profile(successful)
    summary = {"profiles": reports, "fastest_measured_devices": fastest["devices"] if fastest else None,
               "model_manifest_sha256": digest(Path(args.models)/"manifest.json"),
               "batch_size": args.batch_size, "image_size": image.size,
               "hardware": identity(),
               "acceptance": "Latency only. Spatial alignment, click-through and translation fidelity still require human inspection."}
    save_json(directory/"summary.json", summary)
    latency_figure(directory, reports)
    return bool(successful)


def main():
    parser = argparse.ArgumentParser(description="Strictly local Screen Translator tools; never downloads models")
    sub = parser.add_subparsers(dest="command", required=True)
    probe_parser = sub.add_parser("probe", help="Run a known-output OpenVINO operation on every enumerated device")
    probe_parser.add_argument("--results", default="results/benchmarks", help="Parent for a new numbered result directory")
    for command in ("inventory", "seal"):
        item = sub.add_parser(command, help="Validate local hashes" if command == "inventory" else "Create a checksum manifest from local exports")
        item.add_argument("--models", required=True, help="Local bundle root")
        if command == "seal":
            item.add_argument("--metadata", required=True, help="Edited model metadata JSON outside the bundle")
    for command in ("pipeline", "benchmark"):
        item = sub.add_parser(command, help="Translate and measure a supplied screenshot fixture")
        item.add_argument("--models", required=True, help="Local bundle root; use a separate bundle for each candidate")
        item.add_argument("--image", required=True, help="Explicit screenshot fixture; translated PNG and text will be saved locally")
        item.add_argument("--truth", help="Optional UTF-8 reading-order OCR ground truth")
        item.add_argument("--corpus", default=str(Path(__file__).with_name("corpus.json")), help="Representative source/reference JSON")
        item.add_argument("--repeats", type=int, default=5, help="Uncached warm pipeline measurements per profile")
        item.add_argument("--batch-size", type=int, default=8, help="Maximum translation strings per batch")
        item.add_argument("--results", default="results/benchmarks", help="Parent for new numbered result directories")
        if command == "pipeline":
            item.add_argument("--devices", nargs=3, default=["NPU", "GPU", "GPU"], metavar=("DETECTOR", "RECOGNIZER", "TRANSLATOR"), help="Explicit OpenVINO device IDs; default NPU detector, GPU recognizer and translator")
        else:
            item.add_argument("--pairs", nargs="+", default=["CPU/CPU/CPU", "GPU/GPU/GPU", "NPU/NPU/NPU", "NPU/NPU/GPU", "NPU/GPU/GPU"], help="Explicit detector/recognizer/translator profiles")
    args = parser.parse_args()
    if args.command in ("seal", "inventory"):
        payload = seal(args.models, args.metadata) if args.command == "seal" else validate(args.models)
        progress(json.dumps(payload, ensure_ascii=False, indent=2))
        return 0
    if getattr(args, "repeats", 1) < 1 or getattr(args, "batch_size", 1) < 1:
        parser.error("Repeats and batch size must be positive")
    directory = result_directory(args.results)
    config = {**vars(args), "seed": 0, "environment": environment(),
              "protocol": "Models compiled and warmed first; uncached warm inference, then exact frame cache; no capture latency",
              "acceptance_threshold_ms": 2000, "font_scale": 1.0, "tile_size": 256, "padding": 32}
    if args.command != "probe":
        config.update(image_sha256=digest(args.image), corpus_sha256=digest(args.corpus),
                      model_manifest=validate(args.models), truth_sha256=digest(args.truth) if args.truth else None)
    save_json(directory/"config.json", config)
    try:
        if args.command == "probe":
            from .runtime import probe
            result = probe()
            save_json(directory/"summary.json", result)
            progress(json.dumps(result, indent=2))
            ok = bool(result["devices"]) and any(item["ok"] for item in result["devices"].values())
        else:
            ok = benchmark(args, directory)
    except Exception as exc:
        save_json(directory/"failure.json", {"error": f"{type(exc).__name__}: {exc}"})
        raise
    progress(f"Results saved: {directory.resolve()}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
