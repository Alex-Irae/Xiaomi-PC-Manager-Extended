"""Explicit OpenVINO devices; no AUTO/MULTI or silent CPU fallback."""
import os
from pathlib import Path
from time import perf_counter
import numpy as np

# Exact UI labels avoid context-free NMT paraphrases. No substring substitutions.
UI_LABELS = {"设置": "Settings", "确认": "Confirm", "确定": "OK", "取消": "Cancel",
             "打开": "Open", "关闭": "Close", "保存": "Save", "搜索": "Search",
             "返回": "Back", "下一步": "Next", "上一步": "Back", "完成": "Done",
             "通知": "Notifications", "语言": "Language", "设备": "Devices", "首页": "Home",
             "文件": "File", "编辑": "Edit", "查看": "View", "帮助": "Help",
             "更新": "Update", "下载": "Download", "安装": "Install", "删除": "Delete",
             "复制": "Copy", "粘贴": "Paste", "刷新": "Refresh", "退出": "Exit",
             "连接": "Connect", "断开连接": "Disconnect", "网络": "Network",
             "关于": "About", "显示": "Display", "高级": "Advanced", "重试": "Retry"}


def strict_offline():
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    os.environ["DO_NOT_TRACK"] = "1"


def resolve_device(core, requested):
    """Allow a device family alias only when it identifies exactly one device."""
    available = list(core.available_devices)
    if requested in available:
        return requested
    matches = [name for name in available if name.split(".", 1)[0] == requested]
    if len(matches) == 1:
        return matches[0]
    raise RuntimeError(f"Requested {requested}; choose an explicit device from {available}")


def compile_options(device):
    options = {"PERFORMANCE_HINT": "LATENCY"}
    if device.split(".", 1)[0] == "GPU":
        # Use half-precision accelerator math; stored model weights remain FP32.
        options["INFERENCE_PRECISION_HINT"] = "f16"
    return options


def execution_info(compiled):
    devices = compiled.get_property("EXECUTION_DEVICES")
    # NPU 2026.4 returns one string; GPU returns a list. Never split "NPU" into letters.
    info = {"execution_devices": [devices] if isinstance(devices, str) else list(devices)}
    supported = {str(value) for value in compiled.get_property("SUPPORTED_PROPERTIES")}
    info["inference_precision"] = (str(compiled.get_property("INFERENCE_PRECISION_HINT"))
                                   if "INFERENCE_PRECISION_HINT" in supported else "not exposed by plugin")
    return info


def compile_model(core, path, device, shape=None, ctc_top1=False, precision=None):
    requested = device
    device = resolve_device(core, requested)
    start = perf_counter()
    source = core.read_model(str(path))
    if shape is not None:
        source.reshape({source.input(0): shape})
    classes = None
    if ctc_top1:
        import openvino as ov
        from openvino import opset13 as op
        classes = source.output(0).partial_shape[-1].get_length()
        # CTC only needs the winning class/probability at each timestep.
        # Reduce on the accelerator: [B,T,C] -> two [B,T] arrays, avoiding huge host copies.
        top = op.topk(source.get_results()[0].input_value(0), op.constant(1, np.int32), axis=-1,
                     mode="max", sort="none", index_element_type="i32")
        axis = op.constant([-1], np.int64)  # shape: [1], final singleton axis
        indices = op.squeeze(top.output(1), axis)  # [B,T], winning class IDs
        scores = op.squeeze(top.output(0), axis)  # [B,T], winning confidence
        # Rewire the original Result so no dangling sink remains attached to Softmax.
        source.get_results()[0].input(0).replace_source_output(indices.output(0))
        source.add_results([op.result(scores)])
        source.validate_nodes_and_infer_types()
    options = compile_options(device)
    # FP16 recognition can emit confident spurious Han text on the tested GPU.
    # Keep both CTC graph shapes in FP32 so English labels are recognized correctly.
    if ctc_top1 and precision is None:
        precision = "f32"
    if precision is not None and device.split(".",1)[0]=="GPU":
        options["INFERENCE_PRECISION_HINT"]=precision
    if os.environ.get("SCREEN_TRANSLATOR_DATA"):
        from .models import digest
        manifest=Path(path).parent.parent/"manifest.json"
        if manifest.is_file():
            # OpenVINO keys compiled blobs by graph, input shape, device and runtime version.
            cache=Path(os.environ["SCREEN_TRANSLATOR_DATA"])/"model_cache"/digest(manifest)/device/"ocr"
            cache.mkdir(parents=True,exist_ok=True)
            options["CACHE_DIR"]=str(cache)
    model = core.compile_model(source, device, options)
    return model, {"requested": requested, "compiled_device": device,
                   "device_name": core.get_property(device, "FULL_DEVICE_NAME"),
                   **execution_info(model), "compile_options": options,
                   "input_shape": str(model.input(0).partial_shape),
                   "compile_ms": (perf_counter()-start)*1000,
                   **({"ctc_classes": classes, "ctc_reduction": "accelerator top-1"} if ctc_top1 else {})}


def probe():
    import openvino as ov
    from openvino import opset13 as op
    core = ov.Core()
    results = {}
    for device in core.available_devices:
        start = perf_counter()
        try:
            x = op.parameter([1, 16], np.float32)  # shape: [1,16]
            model = core.compile_model(ov.Model([op.relu(x)], [x]), device)
            # Verify a known positive input survives ReLU on the requested device.
            output = model([np.ones((1, 16), np.float32)])[0]  # shape: [1,16]
            if not np.allclose(output, 1):
                raise RuntimeError("Incorrect probe output")
            results[device] = {"name": core.get_property(device, "FULL_DEVICE_NAME"),
                               **execution_info(model),
                               "compile_and_infer_ms": (perf_counter()-start)*1000,
                               "ok": True}
        except Exception as exc:
            results[device] = {"ok": False, "error": str(exc)}
    return {"openvino": ov.__version__, "devices": results}


class Translator:
    def __init__(self, root, device, manifest=None, progress=lambda message: None, precision=None):
        strict_offline()
        progress("Importing offline translation engine")
        import openvino as ov
        from .models import validate, digest
        manifest = manifest if manifest is not None else validate(root)
        path = Path(root) / "translation"
        import json
        config = json.loads((path / "config.json").read_text(encoding="utf-8"))
        self.native = None
        if manifest["models"]["translator"].get("format") == "onnx" and config.get("model_type") == "marian":
            from .marian import Marian
            core = ov.Core()
            requested = device
            device = resolve_device(core, device)
            options = compile_options(device)
            if precision is not None and device.split(".", 1)[0] == "GPU":
                options["INFERENCE_PRECISION_HINT"] = precision
            if os.environ.get("SCREEN_TRANSLATOR_DATA"):
                cache = Path(os.environ["SCREEN_TRANSLATOR_DATA"]) / "model_cache" / digest(Path(root) / "manifest.json") / device
                cache.mkdir(parents=True, exist_ok=True)
                options["CACHE_DIR"] = str(cache)
            self.native = Marian(path, core, device, options, progress)
            self.tokenizer = self.native.tokenizer
            components = self.native.components
            self.info = {"requested": requested, "compiled_device": device,
                         "execution_devices": {name: info["execution_devices"] for name, info in components.items()},
                         "components": components, "compile_options": options,
                         "device_name": core.get_property(device, "FULL_DEVICE_NAME"),
                         "load_compile_ms": self.native.load_compile_ms, "generation_backend": "OpenVINO / NumPy"}
            self.last_tokens = 0
            return
        from transformers import AutoTokenizer
        from optimum.intel.openvino import OVModelForSeq2SeqLM
        requested = device
        core = ov.Core()
        device = resolve_device(core, requested)
        start = perf_counter()
        path = str(Path(root) / "translation")
        from .models import validate
        manifest = manifest if manifest is not None else validate(root)
        onnx_input = manifest["models"]["translator"].get("format") == "onnx"
        progress("Loading local translation tokenizer")
        self.tokenizer = AutoTokenizer.from_pretrained(path, local_files_only=True,
                                                       trust_remote_code=False)
        options = compile_options(device)
        if precision is not None and device.split(".",1)[0]=="GPU":options["INFERENCE_PRECISION_HINT"]=precision
        if os.environ.get("SCREEN_TRANSLATOR_DATA"):
            from .models import digest
            cache=Path(os.environ["SCREEN_TRANSLATOR_DATA"])/"model_cache"/digest(Path(root)/"manifest.json")/device
            cache.mkdir(parents=True,exist_ok=True)
            options["CACHE_DIR"]=str(cache)
        progress("Reading local translation model graphs")
        self.model = OVModelForSeq2SeqLM.from_pretrained(
            path, device=device, local_files_only=True, export=False, from_onnx=onnx_input,
            trust_remote_code=False, ov_config=options.copy(), compile=False)
        progress(f"Compiling translation model graphs on {device}")
        self.model.compile()
        components = {}
        for name in ("encoder", "decoder", "decoder_with_past"):
            part = getattr(self.model, name, None)
            if part is None:
                continue
            request = part.request
            compiled = request.get_compiled_model() if hasattr(request, "get_compiled_model") else request
            components[name] = execution_info(compiled)
        if not components:
            raise RuntimeError("Cannot verify translator execution devices")
        self.info = {"requested": requested, "compiled_device": device,
                     "execution_devices": {name: info["execution_devices"] for name, info in components.items()},
                     "components": components, "compile_options": options,
                     "device_name": core.get_property(device, "FULL_DEVICE_NAME"),
                     "load_compile_ms": (perf_counter()-start)*1000}
        self.last_tokens = 0

    def batch(self, texts):
        self.last_tokens = 0
        if not texts:
            return []
        unknown = [text for text in texts if text not in UI_LABELS]
        generated = iter(self._generate(unknown))
        return [UI_LABELS[text] if text in UI_LABELS else next(generated) for text in texts]

    def _generate(self, texts):
        if self.native is not None:
            outputs = self.native.generate(texts)
            self.last_tokens = self.native.last_tokens
            return outputs
        import torch
        if not texts:
            return []
        # Never silently truncate a long OCR region.
        tokens = self.tokenizer(texts, padding=True, truncation=False, return_tensors="pt")  # each: [B,L]
        if tokens["input_ids"].shape[1] > 512:
            raise ValueError("Translation unit exceeds 512 tokens; split the source region")
        with torch.inference_mode():
            outputs = self.model.generate(**tokens, max_new_tokens=256, num_beams=1, do_sample=False)  # [B,T]
        # Reaching the generation budget without EOS is an incomplete translation.
        eos = self.model.generation_config.eos_token_id
        eos_ids = set(eos if isinstance(eos, list) else [eos])
        if any(not any(token in eos_ids for token in row[1:]) for row in outputs.tolist()):
            raise RuntimeError("Translation reached its token limit without completing")
        self.last_tokens = int(outputs.shape[0] * (outputs.shape[1]-1))
        return self.tokenizer.batch_decode(outputs, skip_special_tokens=True)
