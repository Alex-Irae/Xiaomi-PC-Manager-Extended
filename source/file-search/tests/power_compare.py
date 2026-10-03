"""Compare GPU/NPU idle, query embedding and document embedding power on battery.

Dependencies: existing OpenVINO GenAI export, Intel graphics driver ControlLib.dll,
PowerShell 7, readable Windows RAPL EnergyMeter WMI counters. No installation.
Outputs: a NEW numbered results folder with protocol, raw samples, summary and SVG.
Command: .venv/Scripts/python.exe tests/power_compare.py --phase-seconds 45 --repeats 2
GPU watts come from IGCL energy deltas; package watts from Windows RAPL. These
are different domains, not additive or per-process attribution. Independent
NPU watts are unavailable. Whole-machine battery discharge comes from
Windows BatteryStatus while unplugged. Existing apps are not stopped. This
measures embedding work, excluding extraction, ranking and index checkpoints.
"""
import argparse
import ctypes
import csv
import json
import math
import os
import shutil
import statistics
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import PROJECT, load


def save(path,value):
    path.write_text(json.dumps(value,indent=2,ensure_ascii=False),encoding="utf-8")


class GpuPower:
    """Read validated Intel IGCL double-valued energy/time counters, no writes."""
    def __init__(self):
        self.api=ctypes.c_void_p()
        self.previous=None
        self.error=None
        self.library=None
        try:
            library=ctypes.WinDLL(str(Path(os.environ["SystemRoot"])/"System32/ControlLib.dll"))
            self.library=library
            library.ctlInit.argtypes=[ctypes.c_void_p,ctypes.POINTER(ctypes.c_void_p)]
            library.ctlInit.restype=ctypes.c_int
            library.ctlEnumerateDevices.argtypes=[ctypes.c_void_p,ctypes.POINTER(ctypes.c_uint),ctypes.c_void_p]
            library.ctlEnumerateDevices.restype=ctypes.c_int
            library.ctlPowerTelemetryGet.argtypes=[ctypes.c_void_p,ctypes.c_void_p]
            library.ctlPowerTelemetryGet.restype=ctypes.c_int
            library.ctlClose.argtypes=[ctypes.c_void_p]
            args=ctypes.create_string_buffer(36)
            ctypes.c_uint.from_buffer(args,0).value=36
            ctypes.c_uint.from_buffer(args,8).value=1<<16
            assert library.ctlInit(args,ctypes.byref(self.api))==0,"IGCL initialization unavailable"
            count=ctypes.c_uint()
            assert library.ctlEnumerateDevices(self.api,ctypes.byref(count),None)==0 and count.value>0,"No IGCL adapters"
            devices=(ctypes.c_void_p*count.value)()
            assert library.ctlEnumerateDevices(self.api,ctypes.byref(count),devices)==0
            self.device=devices[0]
        except Exception as exc:
            self.error=str(exc)

    def read(self):
        if self.error:return None
        try:
            buffer=ctypes.create_string_buffer(512)
            ctypes.c_uint.from_buffer(buffer,0).value=512
            assert self.library.ctlPowerTelemetryGet(self.device,buffer)==0,"IGCL telemetry unavailable"
            # Validate the supported flag, units and data type before reading any
            # double. Layout comes from the inspected PC Manager implementation.
            for offset,unit in [(8,7),(32,6)]:
                values=[ctypes.c_uint.from_buffer(buffer,offset+i*4).value for i in range(3)]
                assert values==[1,unit,9],f"Unexpected IGCL layout {values} at {offset}"
            stamp=ctypes.c_double.from_buffer(buffer,24).value
            energy=ctypes.c_double.from_buffer(buffer,48).value
            previous=self.previous
            self.previous=(stamp,energy)
            if previous is None or stamp<=previous[0] or energy<previous[1]:return None
            # Joules divided by seconds gives average GPU watts over the interval.
            watts=(energy-previous[1])/(stamp-previous[0])
            return watts if math.isfinite(watts) and watts>=0 else None
        except Exception as exc:
            self.error=str(exc)
            return None

    def close(self):
        if self.library and self.api.value:
            self.library.ctlClose(self.api)
            self.api.value=None


def worker(args):
    import openvino_genai as genai
    directory=Path(args.output)
    protocol=json.loads((directory/"config.json").read_text(encoding="utf-8"))
    settings=protocol["settings"]
    stem=args.worker+"_"+str(args.repeat)
    phase_path=directory/(stem+".phase")
    result={"device":args.worker,"repeat":args.repeat,"pid":os.getpid(),"fallback":False,"samples":[]}
    power=GpuPower()
    options=genai.TextEmbeddingPipeline.Config()
    options.max_length=settings["max_tokens"];options.batch_size=1;options.pad_to_max_length=True;options.padding_side="left"
    options.pooling_type=genai.TextEmbeddingPipeline.PoolingType.LAST_TOKEN;options.normalize=True;options.query_instruction=settings["query_instruction"]
    cache=PROJECT/"data/model_cache"/args.worker
    cache.mkdir(parents=True,exist_ok=True)
    try:
        phase_path.write_text('unloaded_idle',encoding='utf-8')
        result['phase_times']={};result['operations']=[]
        begin=datetime.now(timezone.utc);result['phase_times']['unloaded_idle']={'start':begin.isoformat()}
        print(f'{stem}: unloaded_idle {args.phase_seconds}s',flush=True)
        deadline=time.monotonic()+args.phase_seconds
        while time.monotonic()<deadline:
            result['samples'].append({'phase':'unloaded_idle','time':datetime.now(timezone.utc).isoformat(),'gpu_watts':power.read()});time.sleep(.25)
        result['phase_times']['unloaded_idle']['end']=datetime.now(timezone.utc).isoformat()
        phase_path.write_text("compile",encoding="utf-8")
        print(f"{stem}: compiling explicit {args.worker} pipeline with existing app cache",flush=True)
        begin=time.perf_counter()
        pipeline=genai.TextEmbeddingPipeline(settings["model_path"],args.worker,options,CACHE_DIR=str(cache))
        result["load_seconds"]=time.perf_counter()-begin
        pipeline.embed_query(protocol["query"])  # Real warmup, excluded from measured phases.
        for phase in ["loaded_idle","periodic_queries","document_embeddings"]:
            phase_path.write_text(phase,encoding="utf-8")
            result['phase_times'][phase]={'start':datetime.now(timezone.utc).isoformat()}
            print(f"{stem}: {phase} {args.phase_seconds}s",flush=True)
            deadline=time.monotonic()+args.phase_seconds
            next_query=time.monotonic()
            phase_begin=time.monotonic()
            query_count=0
            while time.monotonic()<deadline:
                if phase=="periodic_queries" and time.monotonic()>=next_query:
                    stamp=datetime.now(timezone.utc).isoformat()
                    begin=time.perf_counter()
                    pipeline.embed_query(protocol["query"])
                    result.setdefault("queries_ms",[]).append((time.perf_counter()-begin)*1000)
                    query_count+=1;next_query=phase_begin+query_count*args.query_interval
                    result['operations'].append({'phase':phase,'start':stamp,'ms':(time.perf_counter()-begin)*1000})
                elif phase=='document_embeddings':
                    stamp=datetime.now(timezone.utc).isoformat();begin=time.perf_counter()
                    pipeline.embed_documents([protocol['document_samples'][query_count%len(protocol['document_samples'])]['text']])
                    query_count+=1
                    result['operations'].append({'phase':phase,'start':stamp,'ms':(time.perf_counter()-begin)*1000})
                result["samples"].append({"phase":phase,"time":datetime.now(timezone.utc).isoformat(),"gpu_watts":power.read()})
                if phase!='document_embeddings':time.sleep(.25)
            result['phase_times'][phase]['end']=datetime.now(timezone.utc).isoformat()
        result.update(success=True,gpu_telemetry_error=power.error)
    except Exception as exc:
        result.update(success=False,error=str(exc),gpu_telemetry_error=power.error)
    finally:
        phase_path.write_text("finished",encoding="utf-8")
        power.close()
        save(directory/(stem+".json"),result)
    return 0 if result["success"] else 1


def package_monitor(phase_path,output_path,seconds):
    # All substitutions are absolute paths with proper PowerShell literal quoting.
    quote=lambda value:"'"+str(value).replace("'","''")+"'"
    script="$deadline=[DateTime]::UtcNow.AddSeconds("+str(seconds)+"); while([DateTime]::UtcNow -lt $deadline){ $phase=if(Test-Path -LiteralPath "+quote(phase_path)+"){(Get-Content -LiteralPath "+quote(phase_path)+" -Raw).Trim()}else{'starting'}; if($phase -eq 'finished'){break}; $package=$null; try{$power=Get-CimInstance -ClassName Win32_PerfFormattedData_PowerMeterCounter_EnergyMeter -Filter \"Name='RAPL_Package0_PKG'\" -ErrorAction Stop; if($power){$package=[double]$power.Power/1000}}catch{}; $battery=$null;try{$battery=Get-CimInstance -Namespace root/wmi -ClassName BatteryStatus -ErrorAction Stop | Select-Object -First 1}catch{}; [pscustomobject]@{time=[DateTime]::UtcNow.ToString('o');phase=$phase;package_watts=$package;power_online=$battery.PowerOnline;discharging=$battery.Discharging;discharge_mw=$battery.DischargeRate;remaining_mwh=$battery.RemainingCapacity} | ConvertTo-Json -Compress; Start-Sleep -Milliseconds 1000 }"
    stream=output_path.open("w",encoding="utf-8")
    errors=output_path.with_suffix(".errors.txt").open("w",encoding="utf-8")
    process=subprocess.Popen([shutil.which("pwsh") or "powershell.exe","-NoProfile","-Command",script],stdout=stream,stderr=errors)
    return process,stream,errors


def run(args):
    import openvino as ov
    prefix=max((int(p.name.split('_')[0]) for p in (PROJECT/"results").glob('[0-9][0-9][0-9]_*')),default=0)+1
    directory=PROJECT/"results"/f"{prefix:03d}_{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}_seed42_power"
    directory.mkdir(parents=True,exist_ok=False)
    settings=load(PROJECT/"config.json")
    from xiaomi_search.extract import chunks
    from xiaomi_search.indexer import on_battery
    if not on_battery():raise RuntimeError('Disconnect the charger before this battery-power comparison')
    documents=[]
    for source in sorted(Path(args.documents).glob('*.pdf')):
        texts=chunks(source,settings)
        if texts:
            location,text=texts[min(1,len(texts)-1)]
            documents.append({'path':str(source.resolve()),'location':location,'sha256':__import__('hashlib').sha256(source.read_bytes()).hexdigest(),'text':text})
    if not documents:raise RuntimeError('No extractable PDF document samples')
    protocol={"seed":42,"settings":settings,"document_samples":documents,"settle_seconds":args.settle_seconds,"devices":["GPU","NPU"],"repeat_order":["GPU","NPU","NPU","GPU"] if args.repeats==2 else [device for repeat in range(args.repeats) for device in ["GPU","NPU"]],
              "phase_seconds":args.phase_seconds,"query_interval_seconds":args.query_interval,"repeats":args.repeats,"device_timeout_seconds":args.timeout,
              "query":"exclude folders from indexing","hardware":{d:ov.Core().get_property(d,"FULL_DEVICE_NAME") for d in ov.Core().available_devices},
              "power_domains":{"package":"Windows RAPL_Package0_PKG milliwatts / 1000; coverage depends on platform","gpu":"Intel IGCL GPU energy counter delta / timestamp delta","npu":"No independent NPU watts sensor","whole_machine":"root/wmi BatteryStatus DischargeRate milliwatts / 1000 while offline and discharging"},
              "limitations":"Existing apps and broker remain running; power is not process-attributed. Indexing phase measures embedding work, excluding extraction/encrypted checkpoints. Query phase is uncached embedding, excluding ranking/rendering. Battery rate may be smoothed; paired repeated baselines reduce but do not eliminate drift."}
    save(directory/"config.json",protocol)
    print("RESULTS "+str(directory),flush=True)
    power=GpuPower()
    power.read();time.sleep(.4)
    print(f"IGCL initial GPU watts: {power.read()} | error: {power.error}",flush=True)
    power.close()
    summary={"protocol":str(directory/"config.json"),"trials":[],"npu_watts_available":False,"whole_machine_watts_available":False}
    counts={"GPU":0,"NPU":0}
    for device in protocol["repeat_order"]:
        counts[device]+=1;repeat=counts[device];stem=device+"_"+str(repeat)
        monitor,stream,errors=package_monitor(directory/(stem+".phase"),directory/(stem+"_package.jsonl"),args.timeout+5)
        with (directory/(stem+".log")).open("w",encoding="utf-8") as output:
            command=[sys.executable,str(Path(__file__).resolve()),"--worker",device,"--repeat",str(repeat),"--output",str(directory),"--phase-seconds",str(args.phase_seconds),"--query-interval",str(args.query_interval)]
            process=subprocess.Popen(command,stdout=output,stderr=subprocess.STDOUT)
            begin=time.monotonic();previous=0
            while process.poll() is None:
                time.sleep(1)
                content=(directory/(stem+".log")).read_text(encoding="utf-8",errors="replace")
                if len(content)>previous:print(content[previous:],end="",flush=True);previous=len(content)
                if time.monotonic()-begin>args.timeout:
                    subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],capture_output=True);process.wait();break
        try:monitor.wait(timeout=4)
        except subprocess.TimeoutExpired:monitor.kill();monitor.wait()
        stream.close();errors.close()
        path=directory/(stem+".json")
        trial=json.loads(path.read_text(encoding="utf-8")) if path.exists() else {"device":device,"repeat":repeat,"success":False,"error":"Native worker timeout/exit","exit_code":process.returncode}
        package=[]
        for line in (directory/(stem+"_package.jsonl")).read_text(encoding="utf-8-sig").splitlines():
            try:package.append(json.loads(line))
            except ValueError:pass
        trial["phases"]={}
        for phase in ["unloaded_idle","loaded_idle","periodic_queries","document_embeddings"]:
            bounds=trial.get('phase_times',{}).get(phase,{})
            start=datetime.fromisoformat(bounds['start']).timestamp()+args.settle_seconds if 'start' in bounds else float('inf')
            stop=datetime.fromisoformat(bounds['end']).timestamp() if 'end' in bounds else start
            selected=lambda rows:[r for r in rows if r['phase']==phase and start<=datetime.fromisoformat(r['time']).timestamp()<=stop]
            gpu=[r["gpu_watts"] for r in selected(trial.get("samples",[])) if r["gpu_watts"] is not None]
            rows=selected(package)
            watts=[r["package_watts"] for r in rows if r["package_watts"] is not None and math.isfinite(r["package_watts"]) and r["package_watts"]>0]
            battery=[r['discharge_mw']/1000 for r in rows if r.get('power_online') is False and r.get('discharging') is True and r.get('discharge_mw') is not None and 0<r['discharge_mw']<2147483647]
            operations=[r for r in trial.get('operations',[]) if r['phase']==phase and start<=datetime.fromisoformat(r['start']).timestamp()<=stop]
            duration=max(0,stop-start)
            # Mean whole-machine watts times measured seconds estimates joules.
            # Dividing by completed operations compares equal work despite throughput differences.
            energy=statistics.mean(battery)*duration if battery else None
            trial["phases"][phase]={"gpu_samples":len(gpu),"gpu_median_watts":statistics.median(gpu) if gpu else None,"gpu_mean_watts":statistics.mean(gpu) if gpu else None,
                                    "package_samples":len(watts),"package_median_watts":statistics.median(watts) if watts else None,"package_mean_watts":statistics.mean(watts) if watts else None,
                                    'battery_samples':len(battery),'battery_mean_watts':statistics.mean(battery) if battery else None,'battery_median_watts':statistics.median(battery) if battery else None,'on_battery_valid':bool(rows) and len(battery)==len(rows),'measured_seconds':duration,'operations':len(operations),'operation_median_ms':statistics.median(r['ms'] for r in operations) if operations else None,'whole_machine_joules':energy,'whole_machine_joules_per_operation':energy/len(operations) if energy is not None and operations else None}
        trial["package_sensor_error"]=(directory/(stem+"_package.errors.txt")).read_text(encoding="utf-8",errors="replace")
        summary["trials"].append(trial)
        save(directory/"summary.json",summary)
        print(stem+" summary "+json.dumps(trial["phases"]),flush=True)
    summary['whole_machine_watts_available']=all(p.get('on_battery_valid') and p.get('battery_samples',0)>=10 for t in summary['trials'] for p in t['phases'].values())
    for trial in summary['trials']:
        baseline=trial['phases']['loaded_idle']['battery_mean_watts']
        for phase in ['periodic_queries','document_embeddings']:
            value=trial['phases'][phase]
            delta=value['battery_mean_watts']-baseline if baseline is not None and value['battery_mean_watts'] is not None else None
            value['incremental_battery_watts']=delta
            value['incremental_joules_per_operation']=delta*value['measured_seconds']/value['operations'] if delta is not None and value['operations'] else None
    summary["conclusion"]="Compare paired battery baselines and energy per document, not GPU sensor watts alone. Negative active-minus-idle deltas or inconsistent repeats invalidate an efficiency claim. Loaded idle is memory retention, without inference."
    save(directory/"summary.json",summary)
    with (directory/"sensor_summary.csv").open("w",encoding="utf-8",newline="") as stream:
        fields=['device','repeat','phase',*summary['trials'][0]['phases']['document_embeddings'].keys()]
        writer=csv.DictWriter(stream,fieldnames=fields,extrasaction='ignore');writer.writeheader()
        for trial in summary["trials"]:
            for phase,values in trial["phases"].items():
                writer.writerow(dict(device=trial["device"],repeat=trial["repeat"],phase=phase,**values))
    bars=[(r["device"]+" #"+str(r["repeat"])+" "+phase,v["battery_mean_watts"]) for r in summary["trials"] for phase,v in r["phases"].items() if v["battery_mean_watts"] is not None]
    maximum=max((value for _,value in bars),default=1)
    height=120+47*len(bars)
    svg=[f'<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="{height}"><rect width="100%" height="100%" fill="#fff"/><g font-family="Segoe UI" fill="#535861"><text x="24" y="30" font-size="20">Observed battery discharge power</text><text x="24" y="55" font-size="12">Whole-machine mean watts. Balanced GPU/NPU/NPU/GPU trials, initial settling samples excluded.</text>']
    for index,(label,value) in enumerate(bars):
        y=80+index*47;svg.append(f'<text x="24" y="{y+18}" font-size="12">{label}</text><rect x="270" y="{y}" width="{460*value/maximum:.1f}" height="25" rx="5" fill="#3482ff"/><text x="{280+460*value/maximum:.1f}" y="{y+18}" font-size="12">{value:.2f} W</text>')
    svg.append(f'<text x="24" y="{height-15}" font-size="12">Lower draw alone is insufficient: compare joules per document and baseline drift in CSV.</text></g></svg>')
    (directory/"power.svg").write_text(''.join(svg),encoding="utf-8")
    print("COMPLETE "+str(directory),flush=True)


def main():
    sys.stdout.reconfigure(encoding="utf-8",errors="backslashreplace")
    os.environ.update(HF_HUB_OFFLINE="1",TRANSFORMERS_OFFLINE="1",HF_HUB_DISABLE_TELEMETRY="1")
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--phase-seconds",type=int,default=20,help="Seconds of loaded idle and periodic queries per trial")
    parser.add_argument("--repeats",type=int,default=2,help="Trials per device; two uses balanced GPU/NPU/NPU/GPU order")
    parser.add_argument("--query-interval",type=float,default=2,help="Seconds between uncached inference queries")
    parser.add_argument("--timeout",type=int,default=240,help="Maximum seconds per owned native worker")
    parser.add_argument("--worker",choices=["GPU","NPU"],help="Internal single-device worker, no fallback")
    parser.add_argument("--repeat",type=int,default=1,help="Internal trial number")
    parser.add_argument("--output",help="Existing result directory for internal worker")
    parser.add_argument('--documents',default='../screen-translator/ToRead',help='PDF folder for fixed real indexing samples')
    parser.add_argument('--settle-seconds',type=int,default=8,help='Initial seconds excluded from each phase to reduce transition artifacts')
    args=parser.parse_args()
    if min(args.phase_seconds,args.repeats,args.query_interval,args.timeout)<=0:parser.error("Timing/count values must be positive")
    if not 0<=args.settle_seconds<args.phase_seconds:parser.error('Settling time must be shorter than the phase')
    return worker(args) if args.worker else run(args)


if __name__=="__main__":
    sys.exit(main())
