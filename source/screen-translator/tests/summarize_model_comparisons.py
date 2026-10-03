"""Combine preserved public model comparisons into tables, OCR character-error diagnostics and a figure.

Dependencies: standard library and existing project result helpers. Outputs: new results/comparison/.
Command: python tests/summarize_model_comparisons.py --runs results/efficiency/NNN_* [more runs]
Reads evidence only; never loads models, captures the desktop or changes selected models.
"""
import argparse
import csv
import html
import json
from pathlib import Path
import re
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from screen_translator.cli import result_directory,save_json,character_accuracy


def read(path):return json.loads(path.read_text(encoding="utf-8"))


def ocr_quality(directory,device,fixture):
    truth=read(fixture/"config.json")["truth"];regions=read(directory/f"{device}-outputs.json");rows=[]
    for item in truth:
        left,top,right,bottom=item["bounds"];selected=[]
        for region in regions:
            points=region["polygon"];x1=min(p[0] for p in points);x2=max(p[0] for p in points);y1=min(p[1] for p in points);y2=max(p[1] for p in points)
            if min(right,x2)>max(left,x1) and abs((top+bottom-y1-y2)/2)<max(bottom-top,y2-y1)*.6:selected.append((x1,region["source"]))
        detected="".join(text for _,text in sorted(selected));expected=item["source"]
        # Weight each line's edit distance by its true character count, not by equal-length assumptions.
        characters=len(re.sub(r"\s","",expected));accuracy=character_accuracy(expected,detected)
        rows.append({"source":expected,"recognized":detected,"font_size":item["font_size"],"exact":expected==detected,"characters":characters,"edit_distance":round((1-accuracy)*characters)})
    cer=sum(row["edit_distance"] for row in rows)/sum(row["characters"] for row in rows)
    return {"character_error_rate":cer,"line_checks":rows,"exact_by_size":{str(size):sum(row["exact"] for row in rows if row["font_size"]==size) for size in sorted({r["font_size"] for r in rows})}}


def main():
    parser=argparse.ArgumentParser(description="Summarize preserved serial model speed/energy/quality evidence without rerunning inference")
    parser.add_argument("--runs",nargs="+",type=Path,required=True,help="Complete efficiency result directories, excluding runs marked invalid")
    args=parser.parse_args();root=Path(__file__).resolve().parents[1];directory=result_directory(root/"results/comparison");rows=[];sources=[]
    for run in args.runs:
        run=run.resolve()
        if (run/"INVALID_REASON.txt").exists():raise ValueError("Invalid run cannot be used for ranking")
        config=read(run/"config.json");summary=read(run/"summary.json");sources.append(str(run))
        for item in summary["rows"]:
            row={"model":Path(summary["bundle"]).name,"stage":summary["stage"],"device":item["device"],"run":str(run),"ok":item["ok"],"median_ms":item.get("median_ms"),"package_joules_per_operation":item.get("package_joules_per_operation"),"average_package_watts":item.get("average_package_watts"),"quality":item.get("quality"),"error":item.get("error"),"export_preprocessing":config.get("export_preprocessing")}
            if row["model"]=="ppocr-v5-server":row["quality_ranking_eligible"]=False
            if item["ok"] and summary["stage"]=="ocr":row["ocr_quality"]=ocr_quality(run,item["device"],Path(config["fixture"]))
            rows.append(row)
    save_json(directory/"config.json",{"seed":0,"sources":sources,"protocol":"No new inference; geometric OCR line matching, edit distance weighted by true characters, tables and charts of previous measurements","energy_scope":"Entire processor package, not isolated accelerator/app or laptop energy. Background load and short sampling intervals limit small comparisons."})
    save_json(directory/"summary.json",{"rows":rows,"decision":"Keep the preserved PP-OCRv4/FP32 OPUS working model. PP-OCRv5 mobile is a promising OCR candidate; server/int8 do not justify promotion on this evidence.","translation_review":"24 public UI sentences are a spot check, not a blinded evaluation. Both models mistranslate the network-settings sentence; FP32 also mistranslates closing the window without exiting. Complete outputs do not establish semantic correctness."})
    columns=["model","stage","device","median_ms","package_joules_per_operation","average_package_watts","ok"]
    with (directory/"comparison.csv").open("x",newline="",encoding="utf-8") as stream:
        writer=csv.DictWriter(stream,fieldnames=columns);writer.writeheader();writer.writerows({key:row.get(key) for key in columns} for row in rows)
    text=["# Offline model comparison","","All inputs were generated public content. No desktop was captured. OCR is a 72-line 3120 x 2080 prose fixture, 14/18/24 physical pixels on light/dark/low-contrast panels. Translation is 24 public Chinese UI sentences in production batches of eight. Lower milliseconds, joules and character-error rate are better; exact line count is higher-is-better. OCR and translation operation energies cannot be compared directly.","","| Model | Stage | Device | Median ms | Package J/op | Exact OCR lines | Character error rate |","|---|---|---|---:|---:|---:|---:|"]
    for row in rows:
        q=row.get("quality") or {};cer=row.get("ocr_quality",{}).get("character_error_rate")
        latency=f"{row['median_ms']:.1f}" if row["ok"] else "failed";energy=f"{row['package_joules_per_operation']:.2f}" if row["ok"] and row.get("package_joules_per_operation") is not None else "-"
        label=row["model"]+(" (export input diagnostic)" if row.get("export_preprocessing") else "")
        text.append(f"| {label} | {row['stage']} | {row['device']} | {latency} | {energy} | {q.get('exact_chinese','-')} | {cer:.2%} |" if row["ok"] and cer is not None else f"| {label} | {row['stage']} | {row['device']} | {latency} | {energy} | - | - |")
    text.extend(["","The working model stays selected and its sealed snapshot is unchanged. PP-OCRv5 mobile improves this fixture modestly. The server model's extra latency and energy are disproportionate to its accuracy gain; INT8 translation is slower on CPU and its GPU output is incomplete.","","Meaning failures are visible in the saved translations: `无法连接到网络，请检查网络设置。` should ask the user to check network settings. FP32 outputs `Could not close temporary folder: %s`; INT8 outputs `Can not get message: %s %s`. These are incorrect. For `关闭窗口不会退出应用程序。`, FP32 says `without evading the application`; INT8's `without exiting the application` is better. Quantization changes wording in eight of 24 sentences, with mixed quality rather than a clear winner. No corpus-specific glossary was added to hide these failures.","","Auto/Performance/Eco currently change refresh cadence and skip unchanged frames, while hardware stays automatically validated. Automatically switching model families needs stronger quality and repeated battery evidence. NPU translation remains unsupported for this autoregressive graph on the tested compiler; previous dynamic-reshape failures are preserved in efficiency runs 006/007.","","Energy is a Windows cumulative processor-package counter over eight-second sustained workloads. It includes host preprocessing and background applications. Model downloading was running during part of the OCR comparison. Small energy differences are inconclusive. It excludes display, battery conversion and total laptop power. Figures compare measured stage operations, not hotkey-to-overlay latency or battery life.","","Source reports:",*[f"- `{source}`" for source in sources]])
    text.extend(["","## Server export input validation","","The server rows above are current-pipeline compatibility measurements, not a definitive native model-quality ranking. Its pinned export detector config declares RGB [0,1], while the production pipeline uses BGR [-1,1]. Its recognizer config declares height 32; a separate RGB/32-pixel diagnostic fails OpenVINO shape validation because the final pooling kernel is larger than its input. This metadata/graph mismatch needs reconciliation before promotion. The server's 62/72 result at height 48 does not establish its best supported accuracy or efficiency. The working pipeline remains unchanged.","","Sources: [pinned detector config](https://huggingface.co/monkt/paddleocr-onnx/blob/7b02d0a30a07ba2b92ad1ff5a8941ae2c633de65/detection/v5/config.json), [pinned recognizer config](https://huggingface.co/monkt/paddleocr-onnx/blob/7b02d0a30a07ba2b92ad1ff5a8941ae2c633de65/languages/chinese/config.json)."])
    (directory/"REPORT.md").write_text("\n".join(text),encoding="utf-8")
    # Separate stage panels prevent treating a 24-sentence batch as one OCR screen.
    svg=['<svg xmlns="http://www.w3.org/2000/svg" width="1250" height="'+str(120+len(rows)*42)+'"><rect width="100%" height="100%" fill="white"/><g font-family="Segoe UI" font-size="13" fill="#24324a"><text x="20" y="28">Public generated model comparisons: latency and whole-package energy, lower is better</text><text x="320" y="58">Latency, ms</text><text x="740" y="58">Package energy, J/operation</text>']
    for i,row in enumerate(rows):
        y=78+i*42;label=html.escape(f"{row['model']} / {row['stage']} / {row['device']}");svg.append(f'<text x="20" y="{y+18}">{label}</text>')
        if not row["ok"]:svg.append(f'<text x="320" y="{y+18}">Rejected: incomplete/unsupported output</text>');continue
        for x,key,colour in [(320,"median_ms","#3482ff"),(740,"package_joules_per_operation","#26a58a")]:
            value=row[key];maximum=max(r[key] for r in rows if r["ok"] and r["stage"]==row["stage"] and r.get(key))
            if value is not None:svg.append(f'<rect x="{x}" y="{y}" height="25" width="{value/maximum*260:.1f}" fill="{colour}"/><text x="{x+270}" y="{y+18}">{value:.2f}</text>')
        q=row.get("quality") or {};svg.append(f'<text x="1100" y="{y+18}">{q.get("exact_chinese","-")}/72 OCR</text>' if row["stage"]=="ocr" else f'<text x="1100" y="{y+18}">Meaning needs review</text>')
    svg.append('</g></svg>');(directory/"comparison.svg").write_text("\n".join(svg),encoding="utf-8");print(directory,flush=True)


if __name__=="__main__":main()
