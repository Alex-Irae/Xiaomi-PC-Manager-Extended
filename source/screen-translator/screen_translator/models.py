"""Validate an already provisioned local bundle; no downloader or network path."""
import hashlib
import json
import re
from pathlib import Path

DEFAULT_OCR = {"detector_side": 960, "recognition_batch": 8,
               "recognition_hw": [48, 1024], "detection_threshold": .3,
               "box_threshold": .5, "unclip_ratio": 1.6, "recognition_threshold": .5}
COMMON = ("ocr/det.onnx", "ocr/rec.onnx", "translation/config.json", "translation/tokenizer_config.json")


def required_files(manifest):
    metadata = manifest["models"]
    files = list(COMMON)
    if metadata["recognizer"].get("dictionary_source") != "onnx_metadata:character":
        files.append("ocr/keys.txt")
    if metadata["translator"].get("format", "openvino_ir") == "onnx":
        files += ["translation/encoder_model.onnx", "translation/decoder_model.onnx",
                  "translation/decoder_with_past_model.onnx", "translation/source.spm",
                  "translation/target.spm", "translation/vocab.json"]
    else:
        files += [f"translation/openvino_{name}_model.{extension}"
                  for name in ("encoder", "decoder") for extension in ("xml", "bin")]
    return files


def digest(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate(root):
    root = Path(root).resolve()
    if not (root/"manifest.json").is_file():
        raise FileNotFoundError("Models are not provisioned. Run: python -m screen_translator.provision")
    manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8-sig"))
    validate_metadata(manifest)
    if not isinstance(manifest.get("sha256"), dict) or not manifest["sha256"]:
        raise ValueError("Manifest must contain model checksums")
    for relative, expected in manifest["sha256"].items():
        if not isinstance(relative, str) or not isinstance(expected, str) or not re.fullmatch(r"[0-9a-f]{64}", expected):
            raise ValueError("Manifest requires relative filenames and lowercase SHA256 digests")
        path = (root / relative).resolve()
        if not path.is_relative_to(root) or not path.is_file():
            raise ValueError(f"Missing or unsafe model path: {relative}")
        if digest(path) != expected:
            raise ValueError(f"Model checksum mismatch: {relative}")
    for required in required_files(manifest):
        if required not in manifest["sha256"]:
            raise ValueError(f"Manifest does not cover required file: {required}")
    # Reject untracked files that could alter model/tokenizer loading.
    for path in root.rglob("*"):
        if path.is_file() and path != root/"manifest.json" and "model_cache" not in path.relative_to(root).parts:
            if path.relative_to(root).as_posix() not in manifest["sha256"]:
                raise ValueError(f"Untracked model file: {path.name}")
    return manifest


def validate_metadata(manifest):
    if manifest.get("schema") != 1 or manifest.get("languages") != ["zh", "en"]:
        raise ValueError("Unsupported model manifest or language pair")
    for name in ("detector", "recognizer", "translator"):
        metadata = manifest.get("models", {}).get(name, {})
        for key in ("name", "version", "source", "license", "precision", "input_format"):
            value = metadata.get(key)
            if not isinstance(value, str) or not value.strip() or "REPLACE_" in value:
                raise ValueError(f"Incomplete model metadata: {name}.{key}")
    config = manifest.get("ocr", {})
    if not isinstance(config, dict) or set(config) != set(DEFAULT_OCR):
        raise ValueError("Manifest OCR configuration must specify every preprocessing parameter")
    for key in ("detection_threshold", "box_threshold", "recognition_threshold"):
        if not isinstance(config[key], (int, float)) or not 0 < config[key] <= 1:
            raise ValueError(f"Invalid {key}")
    if not isinstance(config["unclip_ratio"], (int, float)) or not 0 < config["unclip_ratio"] <= 5:
        raise ValueError("Invalid unclip ratio")
    dimensions = [config["detector_side"], config["recognition_batch"]]
    if not isinstance(config["recognition_hw"], list) or len(config["recognition_hw"]) != 2:
        raise ValueError("Recognition dimensions must be [height, width]")
    dimensions += config["recognition_hw"]
    if any(type(value) is not int or value <= 0 for value in dimensions):
        raise ValueError("OCR dimensions must be positive integers")
    if config["detector_side"] % 32 or config["recognition_hw"][0] != 48:
        raise ValueError("PP-OCRv4 requires detector side divisible by 32 and recognition height 48")


def seal(root, metadata_path):
    """Hash local files using supplied, truthful metadata; never overwrite a manifest."""
    root = Path(root).resolve()
    metadata = json.loads(Path(metadata_path).read_text(encoding="utf-8"))
    manifest = {"schema": 1, "languages": ["zh", "en"], **metadata}
    validate_metadata(manifest)
    files = {}
    for path in root.rglob("*"):
        if path.is_file() and path != root/"manifest.json" and "model_cache" not in path.relative_to(root).parts:
            if not path.resolve().is_relative_to(root):
                raise ValueError("Model bundle contains an external symbolic link")
            files[path.relative_to(root).as_posix()] = digest(path)
    manifest["sha256"] = files
    if any(required not in files for required in required_files(manifest)):
        raise ValueError("Bundle is missing required OCR, tokenizer or translation model files")
    with (root/"manifest.json").open("x", encoding="utf-8") as stream:
        json.dump(manifest, stream, ensure_ascii=False, indent=2)
    return validate(root)
