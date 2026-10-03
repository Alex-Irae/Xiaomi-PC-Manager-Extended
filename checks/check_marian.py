"""Compare the optimized decoder with the preserved decoder in separate processes.

Dependencies: existing translator Python and offline model bundle, no installations.
Outputs: immutable config, outputs and summary in a new results directory.
Command: private-python -B checks/check_marian.py --baseline OLD_ROOT --candidate SOURCE_ROOT --models MODEL_ROOT --cache PROFILE --output NEW_RUN
"""
import argparse
import json
import subprocess
import sys
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("baseline", "candidate", "models", "cache", "output"):
        parser.add_argument("--" + name, required=True, type=Path, help=name + " directory")
    parser.add_argument("--candidate-device", default="CPU", choices=("CPU", "GPU", "NPU"), help="Candidate translation device; baseline always uses CPU")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    corpus = json.loads((args.candidate / "screen_translator/corpus.json").read_text(encoding="utf-8"))
    cases = [row["source"] for row in corpus] + ["请选择要打开的文件。", "此功能完全在本机运行。", "设备连接成功。", "正在检查更新。", "网络连接出现问题，请稍后重试。", "汉字，标点：数字 123；English USB。", "你好！", "保存文件并关闭窗口。", "我们正在比较翻译结果。", "这是一个较长的中文句子，用来检查缓存解码和批量处理是否与原来的翻译结果完全相同。"]
    batches = [cases[i:i + 4] for i in range(0, len(cases), 4)]
    (output / "inputs.json").write_text(json.dumps(batches, ensure_ascii=False, indent=2), encoding="utf-8")
    (output / "config.json").write_text(json.dumps({"seed": 0, "baseline": str(args.baseline.resolve()), "candidate": str(args.candidate.resolve()), "models": str(args.models.resolve()), "baseline_device": "CPU", "candidate_device": args.candidate_device, "batch_size": 4, "cases": len(cases), "protocol": "Exact translation equality against the baseline decoder; separate interpreters"}, indent=2), encoding="utf-8")
    code = """import sys,os,json,time
from pathlib import Path
sys.path.insert(0,sys.argv[1]);os.environ['SCREEN_TRANSLATOR_DATA']=sys.argv[3]
from screen_translator.bootstrap import prefer_system_runtime
prefer_system_runtime()
from screen_translator.runtime import Translator
from screen_translator.models import validate
start=time.perf_counter();model=Translator(sys.argv[2],sys.argv[6],validate(sys.argv[2]))
load=time.perf_counter()-start
batches=json.loads(Path(sys.argv[4]).read_text(encoding='utf-8'))
outputs=[model.batch(batch) for batch in batches]
Path(sys.argv[5]).write_text(json.dumps({'outputs':outputs,'load_seconds':load,'torch_imported':'torch' in sys.modules,'info':model.info},ensure_ascii=False,indent=2),encoding='utf-8')
"""
    reports = {}
    for name in ("baseline", "candidate"):
        print("STEP: " + name + " decoder", flush=True)
        with (output / (name + ".log")).open("w", encoding="utf-8") as log:
            device = args.candidate_device if name == "candidate" else "CPU"
            subprocess.run([sys.executable, "-X", "pycache_prefix=" + str(args.cache.resolve() / "cache/python"), "-c", code, str(getattr(args, name).resolve()), str(args.models.resolve()), str(args.cache.resolve()), str(output / "inputs.json"), str(output / (name + ".json")), device], stdout=log, stderr=log, check=True, timeout=180)
        reports[name] = json.loads((output / (name + ".json")).read_text(encoding="utf-8"))
    equal = reports["baseline"]["outputs"] == reports["candidate"]["outputs"]
    summary = {"passed": equal and not reports["candidate"]["torch_imported"], "exact_outputs_equal": equal, "cases": len(cases), "candidate_without_torch": not reports["candidate"]["torch_imported"], "load_seconds": {name: report["load_seconds"] for name, report in reports.items()}}
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=2), flush=True)
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
