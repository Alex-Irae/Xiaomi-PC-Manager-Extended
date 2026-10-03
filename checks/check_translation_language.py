"""Check script filtering, stale caches and real OCR on generated desktop labels.

Dependencies: existing translator private Python/model bundle, Pillow, OpenVINO.
Outputs: a new numbered result with configuration, generated fixtures and summary.
Command: python checks/check_translation_language.py --models PATH --devices NPU GPU
Uses generated images only; never captures the user's desktop.
"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import sys

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "source" / "screen-translator"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--models", type=Path, required=True, help="Existing zh-en model folder")
    parser.add_argument("--source", type=Path, help="Optional installed translator folder to test its actual Python modules")
    parser.add_argument("--devices", nargs=2, default=["NPU", "GPU"], help="OCR detector and recognizer devices")
    args = parser.parse_args()
    if args.source:
        sys.path.insert(0, str(args.source.resolve()))
    results = ROOT / "results"
    highest = max([int(p.name.split("_")[0]) for p in results.iterdir() if p.is_dir() and p.name.split("_")[0].isdigit()] + [0])
    run = results / f"{highest+1:03d}_{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}_seed0"
    run.mkdir()
    (run / "config.json").write_text(json.dumps({"seed": 0, "models": str(args.models.resolve()), "devices": args.devices,
        "confidenceThreshold": 0.85, "mode": "Chinese-only-filter", "desktopCaptured": False,
        "fontSizes": [18, 26], "colors": ["light", "dark"],
        "source": str(args.source.resolve()) if args.source else str(ROOT / "source/screen-translator")}, indent=2), encoding="utf-8")
    from PIL import Image, ImageDraw, ImageFont
    from screen_translator.core import Pipeline, TextRegion, translation_eligible
    from screen_translator.models import validate
    from screen_translator.ocr import OCR

    def region(text, confidence=0.99, x=0):
        return TextRegion([[x, 0], [x+30, 0], [x+30, 20], [x, 20]], text, confidence)

    cases = [("PC Manager", .99, False), ("Microsoft Edge", .99, False), ("Downloads", .99, False),
             ("1234 @#$", .99, False), ("Привет", .99, False), ("مرحبا", .99, False),
             ("設定アプリ", .99, False), ("设置 Edge", .99, False), ("微Microsoft", .99, False),
             ("设置", .84, False), ("设置", .99, True), ("打开文件", .99, True)]
    for text, confidence, expected in cases:
        assert translation_eligible(region(text, confidence)) == expected, text

    class Recorder:
        last_tokens = 0
        def __init__(self): self.calls = []
        def batch(self, texts):
            self.calls.extend(texts)
            return ["Settings" if t == "设置" else "Open file" for t in texts]

    units = [region(text, confidence, i*40) for i, (text, confidence, _) in enumerate(cases)]
    recorder = Recorder()
    pipeline = Pipeline(lambda image: units, recorder)
    # A prior cache entry must never bypass the new eligibility rule.
    pipeline.cache[(pipeline.identity, "PC Manager")] = "Wrong old translation"
    output, _ = pipeline.run(Image.new("RGB", (1000, 40), "white"))
    assert recorder.calls == ["设置", "打开文件"], recorder.calls
    assert all(r.translated == r.source for r in output if not translation_eligible(r))
    pipeline.run(Image.new("RGB", (1000, 40), "white"))
    assert len(recorder.calls) == 2
    print("PASS: non-Chinese/mixed/uncertain units stay unchanged, including stale caches", flush=True)

    manifest = validate(args.models)
    ocr = OCR(args.models, *args.devices, manifest["ocr"], progress=lambda text: print(text, flush=True))
    ocr.warmup()
    records = []
    labels = ["PC Manager", "Screen Translator", "AI Center", "Microsoft Edge", "Google Chrome", "Recycle Bin",
              "Documents", "Downloads", "Visual Studio Code", "Steam", "Firefox", "Discord", "résumé", "1234 @#$"]
    for size in [18, 26]:
        for dark in [False, True]:
            image = Image.new("RGB", (1100, 780), "#181a20" if dark else "#fafafa")
            draw = ImageDraw.Draw(image)
            font = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", size)
            for index, label in enumerate(labels):
                x, y = 40 + (index % 2)*530, 30 + (index//2)*85
                # Public colored desktop-like icon beside each Latin label.
                draw.rounded_rectangle((x, y, x+32, y+32), radius=6, fill="#2979ff")
                draw.line((x+8, y+16, x+24, y+16), fill="white", width=3)
                draw.text((x+48, y+4), label, font=font, fill="white" if dark else "black")
            fixture = run / f"desktop-labels-{size}-{'dark' if dark else 'light'}.png"
            image.save(fixture)
            model = Recorder()
            recognized, timing = Pipeline(ocr, model).run(image)
            records.append({"fixture": fixture.name, "regions": [{"text": r.source, "confidence": r.confidence,
                "eligible": translation_eligible(r)} for r in recognized], "timing": timing})
            (run / "ocr-records.json").write_text(json.dumps(records, indent=2, ensure_ascii=False), encoding="utf-8")
            assert not model.calls, (fixture.name, model.calls)
            assert all(r.source == r.translated for r in recognized)
            print(f"PASS: {fixture.name}, {len(recognized)} OCR boxes, zero translations", flush=True)
    image = Image.new("RGB", (400, 120), "white")
    draw = ImageDraw.Draw(image)
    draw.text((30, 25), "设置", font=ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 32), fill="black")
    control = ocr(image)
    assert any(r.source == "设置" and translation_eligible(r) for r in control), control
    (run / "summary.json").write_text(json.dumps({"passed": True, "policyCases": len(cases), "englishFixtures": records,
        "chineseControlRecognized": True, "mixedBoxesPreserved": True, "desktopCaptured": False}, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"PASS: language-filter evidence at {run}", flush=True)


if __name__ == "__main__":
    main()
