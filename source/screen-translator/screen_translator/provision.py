"""Explicit online model provisioning, separate from offline inference.

Dependencies: Python standard library only; never installs packages or runs models.
Outputs: pinned model files and models/zh-en/manifest.json. Existing files are verified.
Command from project root: python -m screen_translator.provision
The download catalog pins revisions and upstream checksums; no account is required.
"""
import argparse
import hashlib
import json
from pathlib import Path
from time import monotonic
from urllib.request import Request, urlopen

from .models import DEFAULT_OCR, digest, validate

ROOT = Path(__file__).resolve().parents[1]


def catalog():
    return json.loads(Path(__file__).with_name("model-catalog.json").read_text(encoding="utf-8-sig"))


def asset_matches(path, asset):
    if not path.is_file() or path.stat().st_size != asset["size"]:
        return False
    if "sha256" in asset:
        return digest(path) == asset["sha256"]
    # Git's blob hash authenticates small files against the pinned repository revision.
    content = path.read_bytes()
    header = f"blob {len(content)}\0".encode("ascii")
    return hashlib.sha1(header+content).hexdigest() == asset["git_blob_sha1"]


def fetch_asset(root, asset, progress=print):
    root = Path(root).resolve()
    target = (root/asset["path"]).resolve()
    if not target.is_relative_to(root):
        raise ValueError("Unsafe model catalog path")
    if not asset["url"].startswith("https://huggingface.co/"):
        raise ValueError("Unsupported model download source")
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        if not asset_matches(target, asset):
            raise ValueError(f"Existing file differs from pinned model: {asset['path']}; use a new bundle folder")
        progress(f"Verified {asset['path']}")
        return
    partial = target.with_name(target.name+".part")
    offset = partial.stat().st_size if partial.exists() else 0
    if offset == asset["size"]:
        if asset_matches(partial, asset):
            partial.replace(target)
            return
        raise ValueError(f"Incomplete download has an invalid checksum: {partial}; choose a new bundle folder")
    headers = {"User-Agent": "ScreenTranslator-model-provisioning/1.0"}
    if offset:
        headers["Range"] = f"bytes={offset}-"
    progress(f"Downloading {asset['path']} ({asset['size']/1_000_000:.1f} MB)")
    request = Request(asset["url"], headers=headers)
    with urlopen(request, timeout=60) as response:
        if offset and response.status != 206:
            offset = 0  # Server ignored Range; restart only this incomplete download.
        written = offset
        last_log = monotonic()
        with partial.open("ab" if offset else "wb") as stream:
            while True:
                chunk = response.read(1024*1024)
                if not chunk:
                    break
                written += len(chunk)
                if written > asset["size"]:
                    raise ValueError("Remote model exceeds its pinned size")
                stream.write(chunk)
                if monotonic()-last_log >= 1:
                    progress(f"  {asset['path']}: {written/asset['size']:.0%}")
                    last_log = monotonic()
    if not asset_matches(partial, asset):
        raise ValueError(f"Downloaded file failed integrity check: {asset['path']}")
    partial.replace(target)
    progress(f"Verified {asset['path']}")


def model_manifest(root, plan):
    """Create actual, non-placeholder metadata only for verified complete assets."""
    root = Path(root)
    for asset in plan["assets"]:
        if not asset_matches(root/asset["path"], asset):
            raise ValueError(f"Missing or invalid model asset: {asset['path']}")
    common = {"license": "Apache-2.0", "precision": "FP32 model weights",
              "version": plan["revisions"]["ocr"], "source": "https://huggingface.co/SWHL/RapidOCR",
              "supported_devices": []}
    metadata = {
        "detector": {**common, "name": "PP-OCRv4 Chinese mobile detector",
                     "input_format": "BGR float32 NCHW, letterbox, normalized [-1,1]", "format": "onnx"},
        "recognizer": {**common, "name": "PP-OCRv4 Chinese recognizer",
                       "input_format": "BGR float32 NCHW, height 48, normalized [-1,1], CTC",
                       "format": "onnx", "dictionary_source": "onnx_metadata:character"},
        "translator": {"name": "Helsinki-NLP/opus-mt-zh-en (Xenova ONNX export)",
                       "version": plan["revisions"]["translation"], "precision": "FP32 model weights",
                       "source": "https://huggingface.co/Xenova/opus-mt-zh-en", "license": "CC-BY-4.0",
                       "input_format": "Marian input IDs, attention mask, autoregressive KV cache",
                       "format": "onnx", "supported_devices": []}}
    return {"schema": 1, "languages": ["zh", "en"], "models": metadata, "ocr": DEFAULT_OCR,
            "sha256": {asset["path"]: digest(root/asset["path"]) for asset in plan["assets"]},
            "provisioning": {"catalog_revision": plan["revisions"],
                             "runtime": "OpenVINO; ONNX is a model format, not an ONNX Runtime backend"}}


def download(root=ROOT/"models/zh-en", progress=lambda text: print(text, flush=True)):
    root = Path(root).resolve()
    plan = catalog()
    for asset in plan["assets"]:
        fetch_asset(root, asset, progress)
    manifest = model_manifest(root, plan)
    path = root/"manifest.json"
    if path.exists():
        existing = json.loads(path.read_text(encoding="utf-8-sig"))
        if existing["sha256"] != manifest["sha256"]:
            raise ValueError("Existing manifest differs; choose a new bundle folder")
    else:
        with path.open("x", encoding="utf-8") as stream:
            json.dump(manifest, stream, ensure_ascii=False, indent=2)
    validate(root)
    progress(f"Models ready: {root}")
    return str(root)


def main():
    parser = argparse.ArgumentParser(description="Explicitly download pinned offline models; never installs or executes an engine")
    parser.add_argument("--models", type=Path, default=ROOT/"models/zh-en", help="Local destination for models and generated manifest")
    args = parser.parse_args()
    download(args.models)


if __name__ == "__main__":
    main()
