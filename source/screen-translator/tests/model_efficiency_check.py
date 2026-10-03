"""Compare separate local model variants on CPU/GPU/NPU with Windows package energy.

Dependencies: root requirements.txt, provisioned candidate bundles, Windows PDH counters.
Outputs: new results/efficiency/ directory with full config, raw timings, outputs and figure.
Command: python tests/model_efficiency_check.py --bundle models/zh-en --stage ocr --seconds 6
Uses an explicitly generated fixture, never captures the desktop or changes active settings.
"""
import argparse
import csv
import ctypes as ct
from ctypes import wintypes as wt
import json
from pathlib import Path
import statistics
import sys
from time import perf_counter,sleep

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from screen_translator.bootstrap import prefer_system_runtime
prefer_system_runtime()
from screen_translator.cli import result_directory,save_json,environment
from screen_translator.models import validate,digest

ROOT=Path(__file__).resolve().parents[1]
sys.stdout.reconfigure(encoding="utf-8",errors="backslashreplace")
sys.stderr.reconfigure(encoding="utf-8",errors="backslashreplace")


class CounterValue(ct.Structure):
    _fields_=[("status",wt.DWORD),("value",ct.c_double)]


class PackageEnergy:
    """Read cumulative Windows EMI package energy, without installing a driver.

    The counter explanation must explicitly confirm picowatt-hours. Energy includes
    all package activity, including other applications, not isolated accelerator power.
    """
    def __init__(self):
        self.api=ct.WinDLL("pdh");self.query=wt.HANDLE();self.counter=wt.HANDLE()
        self.api.PdhOpenQueryW.argtypes=[wt.LPCWSTR,ct.c_size_t,ct.POINTER(wt.HANDLE)]
        self.api.PdhAddEnglishCounterW.argtypes=[wt.HANDLE,wt.LPCWSTR,ct.c_size_t,ct.POINTER(wt.HANDLE)]
        self.api.PdhCollectQueryData.argtypes=[wt.HANDLE]
        self.api.PdhGetFormattedCounterValue.argtypes=[wt.HANDLE,wt.DWORD,ct.POINTER(wt.DWORD),ct.POINTER(CounterValue)]
        self.api.PdhGetCounterInfoW.argtypes=[wt.HANDLE,wt.BOOL,ct.POINTER(wt.DWORD),ct.c_void_p]
        self.api.PdhCloseQuery.argtypes=[wt.HANDLE]
        for call in (lambda:self.api.PdhOpenQueryW(None,0,ct.byref(self.query)),
                     lambda:self.api.PdhAddEnglishCounterW(self.query,r"\Energy Meter(RAPL_Package0_PKG)\Energy",0,ct.byref(self.counter))):
            if call()!=0:raise RuntimeError("Windows package energy counter unavailable")
        length=wt.DWORD();self.api.PdhGetCounterInfoW(self.counter,True,ct.byref(length),None)
        buffer=ct.create_string_buffer(length.value)
        if self.api.PdhGetCounterInfoW(self.counter,True,ct.byref(length),buffer)!=0:raise RuntimeError("Cannot verify energy counter metadata")
        # PDH_COUNTER_INFO_W on x64: six DWORDs, two DWORD_PTRs, full-path pointer,
        # six-field path-elements union (48 bytes), then the explanation pointer.
        pointer=ct.c_void_p.from_buffer(buffer,96).value
        self.explanation=ct.wstring_at(pointer) if pointer else ""
        if "picowatt" not in self.explanation.lower():raise RuntimeError(f"Energy counter unit unverified: {self.explanation}")
        self.read();sleep(.25)

    def read(self):
        if self.api.PdhCollectQueryData(self.query)!=0:raise RuntimeError("Cannot collect package energy")
        value=CounterValue()
        status=self.api.PdhGetFormattedCounterValue(self.counter,0x200,None,ct.byref(value))
        if status or value.status not in (0,1):raise RuntimeError("Invalid energy counter sample")
        return value.value

    def close(self):self.api.PdhCloseQuery(self.query)


def main():
    parser=argparse.ArgumentParser(description="Compare local stage latency, generated correctness and measured package joules per operation")
    parser.add_argument("--bundle",type=Path,required=True,help="Separate sealed model bundle, never modified")
    parser.add_argument("--stage",choices=["ocr","translation"],required=True,help="Stage to compare on each requested device")
    parser.add_argument("--devices",nargs="+",default=["GPU","NPU","CPU"],help="Explicit devices to compare, no silent fallback")
    parser.add_argument("--seconds",type=float,default=6,help="Minimum warm sustained interval per device, at least 4 seconds")
    parser.add_argument("--gpu-precision",choices=["f16","f32"],help="Translation GPU math override for diagnosing quantized export correctness; default preserves application settings")
    parser.add_argument("--fixture",type=Path,default=ROOT/"results/generated/007_20261002T101023Z_seed0",help="Explicit public generated fixture directory")
    parser.add_argument("--corpus",type=Path,help="Public UTF-8 JSON array of Chinese sentence strings for a broader translation comparison")
    parser.add_argument("--batch-size",type=int,default=8,help="Production text units per translation batch; default eight")
    parser.add_argument("--export-preprocessing",choices=["monkt-v5"],help="Diagnostic only: test the server export's declared RGB detector [0,1] and 32-pixel recognizer input instead of production PP-OCRv4 preprocessing")
    args=parser.parse_args()
    if args.seconds<4:parser.error("At least four seconds is required for cumulative energy sampling")
    if not 1<=args.batch_size<=64:parser.error("Batch size must be 1 to 64")
    root=args.bundle.resolve();manifest=validate(root)
    directory=result_directory(ROOT/"results/efficiency")
    from screen_translator.hardware import identity
    config={"seed":0,"stage":args.stage,"bundle":str(root),"manifest_sha256":digest(root/"manifest.json"),
        "manifest":manifest,"devices":args.devices,"seconds":args.seconds,"gpu_precision":args.gpu_precision,"fixture":str(args.fixture.resolve()),
        "corpus":str(args.corpus.resolve()) if args.corpus else None,"batch_size":args.batch_size,"export_preprocessing":args.export_preprocessing,
        "hardware":identity(),"environment":environment(),"protocol":"Serial devices, one warm-up, five latency repeats, separate sustained package-energy interval; no capture",
        "energy_scope":"Whole processor package, not laptop input power or isolated GPU/NPU; background load and meter precision can affect ranking"}
    print(f"Efficiency report: {directory}",flush=True)
    energy=None;energy_error=None
    try:energy=PackageEnergy();config["energy_explanation"]=energy.explanation
    except Exception as error:energy_error=str(error);print(f"Energy unavailable: {error}",flush=True)
    config["energy_error"]=energy_error
    save_json(directory/"config.json",config)
    rows=[]
    for device in args.devices:
        print(f"Loading {root.name} {args.stage} on {device}",flush=True)
        model=None;quality=None
        try:
            if args.stage=="ocr":
                from PIL import Image
                from screen_translator.ocr import OCR
                from screen_translator.core import serialize
                image=Image.open(args.fixture/"input.png").convert("RGB")
                truth=json.loads((args.fixture/"config.json").read_text(encoding="utf-8"))["truth"]
                preprocessing={**manifest["ocr"],"detector_side":1280,"tile_overlap":192}
                if args.export_preprocessing:
                    if manifest["models"]["detector"]["source"]!="https://huggingface.co/monkt/paddleocr-onnx":raise ValueError("This diagnostic applies only to the pinned monkt export")
                    preprocessing["recognition_hw"]=[32,preprocessing["recognition_hw"][1]]
                model=OCR(root,device,device,preprocessing,lambda m:print(m,flush=True))
                if args.export_preprocessing:
                    def adapt(compiled,detector=False):
                        def call(inputs):
                            # Convert the existing BGR NCHW preprocessing to the export's RGB order.
                            rgb=inputs[0][:,::-1,:,:].copy()  # [B,3,H,W]
                            # The export explicitly declares detector [0,1]; recognition normalization is unspecified.
                            if detector:rgb=(rgb+1)*.5  # [B,3,H,W], [-1,1] -> [0,1]
                            return compiled([rgb])
                        return call
                    model.detector=adapt(model.detector,True);model.recognizer=adapt(model.recognizer);model.narrow_recognizer=adapt(model.narrow_recognizer)
                    model.info["export_preprocessing_diagnostic"]={"detector":"RGB [0,1]","recognizer":"RGB [-1,1], H=32; normalization assumption needs upstream confirmation","source":"pinned export config.json"}
                model.warmup()
                infer=lambda:model(image)
                outputs=infer()
                # Match each expected string to an OCR region that overlaps its physical glyph bounds.
                def matches(expected,region):
                    a,b=expected["bounds"],region.bounds
                    return region.source==expected["source"] and min(a[2],b[2])>max(a[0],b[0]) and min(a[3],b[3])>max(a[1],b[1])
                exact=sum(any(matches(expected,r) for r in outputs) for expected in truth)
                quality={"exact_chinese":exact,"expected_chinese":len(truth),"regions":len(outputs),"exact":exact==len(truth)}
                save_json(directory/f"{device}-outputs.json",serialize(outputs))
            else:
                from screen_translator.runtime import Translator
                model=Translator(root,device,manifest,lambda m:print(m,flush=True),precision=args.gpu_precision)
                texts=["请选择要打开的文件。","此功能完全在本机运行。","正在检查更新。","设备连接成功。"]
                if args.corpus:
                    texts=json.loads(args.corpus.read_text(encoding="utf-8"))
                    if not isinstance(texts,list) or not texts or any(not isinstance(t,str) or not t.strip() for t in texts):raise ValueError("Corpus must be a nonempty array of sentence strings")
                infer=lambda:[output for offset in range(0,len(texts),args.batch_size) for output in model.batch(texts[offset:offset+args.batch_size])];outputs=infer()
                quality={"source":texts,"outputs":outputs,"complete":len(outputs)==len(texts) and all(outputs),"semantic_quality":"Requires review; completeness is not semantic correctness"}
                save_json(directory/f"{device}-outputs.json",quality)
                # Empty decoder output is not a usable translation, regardless of measured speed.
                if not quality["complete"]:raise RuntimeError("Incomplete generated translations; excluded from speed and efficiency ranking")
            times=[]
            for _ in range(5):
                started=perf_counter();infer();times.append((perf_counter()-started)*1000)
            # Integrate cumulative picowatt-hours over a sustained operation interval, then convert to joules.
            before=energy.read() if energy else None
            started=perf_counter();count=0
            while perf_counter()-started<args.seconds:
                infer();count+=1
            elapsed=perf_counter()-started;after=energy.read() if energy else None
            joules=(after-before)*3.6e-9 if energy else None
            if joules is not None and joules<=0:raise RuntimeError("Package energy did not advance during sustained inference")
            row={"device":device,"ok":True,"execution":model.info,"quality":quality,"samples_ms":times,"median_ms":statistics.median(times),
                 "operations":count,"elapsed_s":elapsed,"package_joules":joules,"average_package_watts":joules/elapsed if joules else None,
                 "package_joules_per_operation":joules/count if joules else None,"operations_per_joule":count/joules if joules else None}
            quality_log={key:value for key,value in quality.items() if key not in ("source","outputs")}
            print(f"{device}: {row['median_ms']:.1f} ms, {row['package_joules_per_operation']} package J/operation; quality {quality_log}",flush=True)
        except Exception as error:
            row={"device":device,"ok":False,"error":str(error),"quality":quality};print(f"{device} failed: {error}",flush=True)
        rows.append(row)
        save_json(directory/f"{device}-summary.json",row)
        del model
        import gc
        gc.collect();sleep(1)
    if energy:energy.close()
    save_json(directory/"summary.json",{"stage":args.stage,"bundle":str(root),"rows":rows,"energy_counter_explanation":config.get("energy_explanation"),"energy_error":energy_error,"limitations":config["energy_scope"]})
    with (directory/"timings.csv").open("x",newline="",encoding="utf-8") as stream:
        writer=csv.writer(stream);writer.writerow(["device","median_ms","average_package_watts","package_joules_per_operation","quality"])
        for row in rows:writer.writerow([row["device"],row.get("median_ms"),row.get("average_package_watts"),row.get("package_joules_per_operation"),json.dumps(row.get("quality"),ensure_ascii=False)])
    svg=['<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="260"><rect width="100%" height="100%" fill="white"/><text x="20" y="25">Warm stage latency (ms) and package energy (J/operation), lower is better. Quality must also pass.</text>']
    good=[row for row in rows if row["ok"]]
    for i,row in enumerate(good):
        y=55+i*55;svg.append(f'<text x="20" y="{y+15}">{row["device"]}</text>')
        for x,key,colour in [(90,"median_ms","#3482ff"),(550,"package_joules_per_operation","#26a58a")]:
            if row.get(key) is None:continue
            maximum=max(r[key] for r in good if r.get(key) is not None)
            svg.append(f'<rect x="{x}" y="{y}" width="{row[key]/maximum*290:.1f}" height="24" fill="{colour}"/><text x="{x+300}" y="{y+17}">{row[key]:.3f}</text>')
    svg.append('<text x="90" y="240">Latency, ms</text><text x="550" y="240">Whole-package energy, J/operation</text></svg>')
    (directory/"comparison.svg").write_text("\n".join(svg),encoding="utf-8")
    return 0 if good else 1


if __name__=="__main__":raise SystemExit(main())
