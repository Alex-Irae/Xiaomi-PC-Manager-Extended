"""Regenerate the adapted modern Xiaomi bundle from the preserved original.

Dependencies: Python 3.12 standard library. Outputs: frontend/xiaomi/assets/index.js
and research/frontend-adaptation.json. Leaves preserved originals untouched.
Command from project root: python prepare_frontend.py
Run only when explicitly rebuilding translations/bridge integration. Generated
bundle edits are replaced; edit english.json, local-host.js or reskin.css instead.
"""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def main():
    source_path = ROOT / "vendor/xiaomi-original-copy/assets/index.js"
    source = source_path.read_text(encoding="utf-8-sig")
    manifest = json.loads((ROOT / "vendor/manifest.json").read_text(encoding="utf-8-sig"))
    expected = next(item["sha256"] for item in manifest["files"] if item["path"] == "assets/index.js")
    digest = hashlib.sha256(source_path.read_bytes()).hexdigest()
    if digest != expected:
        raise ValueError("Preserved frontend hash changed; refusing to adapt an unknown bundle")
    translations = json.loads((ROOT / "frontend/english.json").read_text(encoding="utf-8-sig"))
    for original, english in translations.items():
        source = source.replace(json.dumps(original, ensure_ascii=False), json.dumps(english, ensure_ascii=False))
    if "window.chrome.webview" not in source or "maxLength:30" not in source:
        raise ValueError("Frontend no longer matches the inspected bridge/input contract")
    source = source.replace("window.chrome.webview", "window.localBridge.native").replace("maxLength:30", "maxLength:2048")
    # Ignore a response if the user has already issued a newer query, including
    # searches on the same local device. Device checks alone do not cover that.
    replacements = {
        "if(m!==r().searchDeviceId){": "if(s.current!==M)return;if(m!==r().searchDeviceId){",
        "if(w!==r().searchDeviceId){": "if(s.current!==P)return;if(w!==r().searchDeviceId){",
    }
    for original, replacement in replacements.items():
        if source.count(original) != 1:
            raise ValueError("Frontend query guard no longer matches the inspected version")
        source = source.replace(original, replacement)
    source += "\nwindow.__xiaomiSearchState=ln;\n"
    target = ROOT / "frontend/xiaomi/assets/index.js"
    target.write_text(source, encoding="utf-8", newline="")
    report = {"source": "vendor/xiaomi-original-copy/assets/index.js", "translation_count": len(translations), "changes": ["English literal replacements", "maxLength 30 to 2048", "chrome.webview calls redirected to localBridge.native", "exposed existing Zustand search state for keyboard actions", "ignore stale responses for newer queries"], "upstream_sha256": digest, "adapted_sha256": hashlib.sha256(target.read_bytes()).hexdigest()}
    (ROOT / "research/frontend-adaptation.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print("Adapted inspected Xiaomi frontend; originals preserved")


if __name__ == "__main__":
    main()
