"""Preserve the active bundle and provision pinned alternative models separately.

Dependencies: standard library and project provisioning helpers; explicit network access.
Outputs: models/candidates/{ppocr-v5,opus-int8}/ with sealed manifests; never edits active models.
Command from project root: python tests/provision_candidates.py --variant all
"""
import argparse
from copy import deepcopy
import json
from pathlib import Path
import shutil
import sys
from concurrent.futures import ThreadPoolExecutor
from time import sleep
from urllib.request import urlopen

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from screen_translator.models import digest,validate
from screen_translator.provision import fetch_asset

ROOT=Path(__file__).resolve().parents[1]
VARIANTS={
    "ppocr-v5-server":("monkt/paddleocr-onnx","7b02d0a30a07ba2b92ad1ff5a8941ae2c633de65",{
        "ocr/det.onnx":"detection/v5/det.onnx","ocr/rec.onnx":"languages/chinese/rec.onnx","ocr/keys.txt":"languages/chinese/dict.txt"}),
    "ppocr-v5":("webnn/PP-OCRv5-ONNX","006b4419a415072fbb16a1d7d0d8548f789a8fae",{
        "ocr/det.onnx":"ch_PP-OCRv5_det.onnx","ocr/rec.onnx":"ch_PP-OCRv5_rec.onnx","ocr/keys.txt":"ch_PP-OCRv5_dict.txt"}),
    "opus-int8":("Xenova/opus-mt-zh-en","39d480d52a9ea3065a1f117adfe4dbc55de10e6f",{
        f"translation/{name}_model.onnx":f"onnx/{name}_model_quantized.onnx" for name in ("encoder","decoder","decoder_with_past")})}


def provision(name,baseline):
    """Copy unchanged sealed assets, download replacements, then validate a separate bundle."""
    repo,revision,replacements=VARIANTS[name]
    for attempt in range(3):
        try:
            with urlopen(f"https://huggingface.co/api/models/{repo}/revision/{revision}?blobs=true",timeout=60) as response:remote=json.load(response)
            break
        except OSError:
            if attempt==2:raise
            print(f"Retrying public metadata ({attempt+2}/3)",flush=True);sleep(2)
    if remote["sha"]!=revision:raise ValueError("Unexpected upstream revision")
    remote_files={file["rfilename"]:file for file in remote["siblings"]}
    target=ROOT/"models/candidates"/name
    target.mkdir(parents=True,exist_ok=True)
    manifest=deepcopy(baseline)
    for relative,expected in baseline["sha256"].items():
        if relative in replacements:continue
        destination=target/relative;destination.parent.mkdir(parents=True,exist_ok=True)
        if not destination.exists():shutil.copy2(ROOT/"models/zh-en"/relative,destination)
        if digest(destination)!=expected:raise ValueError(f"Existing candidate asset differs: {relative}")
    def fetch(replacement):
        relative,upstream=replacement
        file=remote_files[upstream]
        asset={"path":relative,"size":file["size"],"url":f"https://huggingface.co/{repo}/resolve/{revision}/{upstream}"}
        if file.get("lfs"):asset["sha256"]=file["lfs"]["sha256"]
        else:asset["git_blob_sha1"]=file["blobId"]
        fetch_asset(target,asset,lambda message:print(message,flush=True))
        return relative,digest(target/relative)
    with ThreadPoolExecutor(max_workers=3) as pool:
        manifest["sha256"].update(pool.map(fetch,replacements.items()))
    if name.startswith("ppocr-v5"):
        for key in ("detector","recognizer"):
            variant="server" if name.endswith("server") else "mobile"
            manifest["models"][key].update(name=f"PP-OCRv5 {variant} {key} (ONNX export)",version=revision,source=f"https://huggingface.co/{repo}")
        manifest["models"]["recognizer"]["dictionary_source"]="ocr/keys.txt"
    else:
        manifest["models"]["translator"].update(name="Helsinki-NLP opus-mt-zh-en (Xenova INT8 ONNX export)",precision="Upstream dynamic INT8 quantized weights")
    path=target/"manifest.json"
    if path.exists():
        if json.loads(path.read_text(encoding="utf-8"))!=manifest:raise ValueError("Candidate manifest differs; preserve it and choose another folder")
    else:
        with path.open("x",encoding="utf-8") as stream:json.dump(manifest,stream,ensure_ascii=False,indent=2)
    validate(target)
    print(f"Verified separate candidate: {target}",flush=True)


if __name__=="__main__":
    parser=argparse.ArgumentParser(description="Download pinned candidates without changing the working bundle or installing packages")
    parser.add_argument("--variant",choices=[*VARIANTS,"all"],default="all",help="New OCR model, quantized translation variant, or both")
    args=parser.parse_args();baseline=validate(ROOT/"models/zh-en")
    for variant in VARIANTS if args.variant=="all" else [args.variant]:provision(variant,baseline)
