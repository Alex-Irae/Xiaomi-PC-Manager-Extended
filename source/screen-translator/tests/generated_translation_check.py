"""Exercise actual local OCR/NMT on a public generated mixed-language screen.

Dependencies: root requirements.txt, Windows Microsoft YaHei font, provisioned models.
Outputs: new numbered results/generated/ directory, own fixture/regions/timings/figure.
Command from project root: python tests/generated_translation_check.py
No desktop capture, package installation or model downloads. Run only with authorization.
"""
import csv
import argparse
import json
from pathlib import Path
import statistics
import sys
from time import perf_counter

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from screen_translator.bootstrap import prefer_system_runtime
prefer_system_runtime()
from PIL import Image, ImageDraw, ImageFont
from screen_translator.cli import result_directory, save_json, character_accuracy
from screen_translator.core import serialize
from screen_translator.hardware import automatic_pipeline
from screen_translator.runtime import UI_LABELS


def main():
    parser=argparse.ArgumentParser(description="Check local OCR/NMT on public generated mixed-language content, without desktop capture")
    parser.add_argument("--dense",action="store_true",help="Use 18/22/26 physical-pixel text on light/dark panels instead of the original 40-pixel fixture")
    args=parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    directory = result_directory(root/"results/generated")
    print(f"Generated fixture results: {directory}", flush=True)
    labels = ["设置", "确认", "取消", "打开", "保存", "搜索", "语言", "设备", "首页",
              "通知", "文件", "下载", "网络", "关于", "更新", "安装", "复制", "粘贴",
              "请选择要打开的文件。", "此功能完全在本机运行。", "正在检查更新。", "设备连接成功。"]
    image = Image.new("RGB", (3120, 2080), "white")
    draw = ImageDraw.Draw(image)
    font_path = Path("C:/Windows/Fonts/msyh.ttc")
    truth = []
    sizes=[18,22,26] if args.dense else [40]
    if args.dense:draw.rectangle((1040,0,2079,2079),fill=(34,36,40))
    for column,size in enumerate(sizes):
        font = ImageFont.truetype(str(font_path), size)
        latin = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", size)
        for index,text in enumerate(labels):
            x,y = (60+column*1040,120+index*78) if args.dense else (160+(index%2)*1460,120+(index//2)*165)
            colour=(235,238,242) if args.dense and column==1 else (30,32,36)
            draw.text((x,y),text,font=font,fill=colour)
            draw.text((x+(700 if args.dense else 820),y),"English 2026",font=latin,fill=colour)
            truth.append({"source":text,"expected_label":UI_LABELS.get(text),"position":[x,y],"bounds":list(draw.textbbox((x,y),text,font=font)),"font_size":size,"latin":"English 2026"})
    image.save(directory/"input.png")
    save_json(directory/"config.json", {"seed":0, "mode":"Managed", "models":str(root/"models/zh-en"),
        "image_size":list(image.size), "font":str(font_path), "font_sizes":sizes, "dense":args.dense, "truth":truth,
        "repeats":3, "batch_size":8, "incremental":True, "cache_protocol":"3 uncached passes then identical cached frame",
        "screen_captured":False, "protocol":"Generated mixed-language light/dark UI; geometry-matched exact OCR labels and unchanged Latin; NMT sentences for human review",
        "acceptance":{"all_exact_labels":True,"latin_preserved":True,"cache_hit":True,"latency_target_ms":2000}})
    pipeline, info = automatic_pipeline(root/"models/zh-en", progress=lambda m:print(m,flush=True))
    timings = []
    for iteration in range(3):
        regions, timing = pipeline.run(image,use_cache=False)
        timings.append(timing)
        print(f"Fixture {iteration+1}/3: {timing['total_ms']:.1f} ms; {len(regions)} regions",flush=True)
    regions, _ = pipeline.run(image,use_cache=True)
    _, cached = pipeline.run(image,use_cache=True)
    checks = []
    for item in truth:
        a=item["bounds"]
        def matches(region):
            b=region.bounds
            overlap=max(0,min(a[2],b[2])-max(a[0],b[0]))*max(0,min(a[3],b[3])-max(a[1],b[1]))
            return overlap>0 and abs((a[1]+a[3]-b[1]-b[3])/2)<max(a[3]-a[1],b[3]-b[1])*.6
        candidates = [r for r in regions if matches(r)]
        detected = "".join(r.source for r in candidates)
        checks.append({**item, "recognized":detected, "exact":detected==item["source"],
                       "character_accuracy":character_accuracy(item["source"],detected),
                       "translations":[r.translated for r in candidates],
                       "label_ok":item["expected_label"] is None or [r.translated for r in candidates]==[item["expected_label"]]})
    latin_regions = [r for r in regions if "English" in r.source]
    latin_preserved = len(latin_regions)==len(truth) and all(r.source==r.translated for r in latin_regions)
    summary = {"info":info,"checks":checks,"timings":timings,"cached":cached,
               "median_ms":statistics.median(t['total_ms'] for t in timings),
               "all_exact_ocr":all(c["exact"] for c in checks),"exact_labels_ok":all(c["label_ok"] for c in checks),
               "latin_preserved":latin_preserved,
               "limitations":"Generated flat UI only. Sentence translations require human review. Excludes capture, bridge, native rendering, hotkey scheduling and real-screen acceptance."}
    save_json(directory/"summary.json",summary)
    save_json(directory/"regions.json",serialize(regions))
    with (directory/"timings.csv").open("x",newline="",encoding="utf-8") as stream:
        keys=["total_ms","detection_ms","recognition_ms","translation_ms"]
        writer=csv.DictWriter(stream,fieldnames=["iteration",*keys]);writer.writeheader()
        for i,timing in enumerate(timings):writer.writerow({"iteration":i+1,**{k:timing[k] for k in keys}})
    maximum=max(t['total_ms'] for t in timings)
    svg=['<svg xmlns="http://www.w3.org/2000/svg" width="760" height="190"><rect width="100%" height="100%" fill="white"/><text x="20" y="25">Generated 3120 x 2080 fixture pipeline (ms), excludes native capture/render.</text>']
    for i,timing in enumerate(timings):
        y=45+i*40
        svg.append(f'<text x="20" y="{y+16}">Pass {i+1}</text><rect x="90" y="{y}" width="{timing["total_ms"]/maximum*500:.1f}" height="24" fill="#3482ff"/><text x="610" y="{y+16}">{timing["total_ms"]:.1f} ms</text>')
    (directory/"latency.svg").write_text("\n".join(svg)+"</svg>",encoding="utf-8")
    print(json.dumps({k:summary[k] for k in ["median_ms","all_exact_ocr","exact_labels_ok","latin_preserved"]}),flush=True)
    print(f"Cached: {cached['total_ms']:.1f} ms; output: {directory}",flush=True)
    return 0 if summary["all_exact_ocr"] and summary["exact_labels_ok"] and latin_preserved and cached["frame_cache_hit"] else 1


if __name__=="__main__":
    raise SystemExit(main())
