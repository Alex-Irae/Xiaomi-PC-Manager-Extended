"""Measure real native/Python process memory during the focused translator lifecycle check.

Dependencies: existing translator Python with psutil; no packages are installed.
Outputs: config.json, native.log, memory.csv and summary.json in a new run directory.
Command: private-python -B checks/model_lifecycle.py --app COMPONENT --cache-profile PROFILE --output NEW_DIRECTORY
"""
import argparse
import csv
import json
import subprocess
import time
from pathlib import Path
import psutil


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app',type=Path,required=True,help='Screen Translator component folder')
    parser.add_argument('--cache-profile',type=Path,required=True,help='Existing profile containing compiled model_cache')
    parser.add_argument('--output',type=Path,required=True,help='New evidence directory, must not exist')
    parser.add_argument('--source',type=Path,help='Optional editable source root; uses installed models and Python')
    parser.add_argument('--native',type=Path,help='Optional newly built native DLL')
    args=parser.parse_args(); app=args.app.resolve(); output=args.output.resolve();output.mkdir(parents=True,exist_ok=False)
    command=[str(app/'runtime/dotnet/dotnet.exe'),str(args.native.resolve() if args.native else app/'ScreenTranslator.dll'),'--root',str(args.source.resolve() if args.source else app),'--python',str(app/'runtime/python/python.exe'),'--data-dir',str(output),'--cache-data-dir',str(args.cache_profile.resolve()),'--check-model-lifecycle','--no-startup']
    (output/'config.json').write_text(json.dumps({'models':str(app/'models/zh-en')},indent=2),encoding='utf-8')
    (output/'run-config.json').write_text(json.dumps({'seed':0,'command':command,'protocol':'Hidden native startup, controls abort during loading, cancelled region selection, two cached loads and actual ten-second idle release','sample_seconds':.25},indent=2),encoding='utf-8')
    samples=[]; owned_python=set(); phase='startup'; started=time.perf_counter()
    with (output/'console.log').open('w',encoding='utf-8') as log:
        process=subprocess.Popen(command,stdout=log,stderr=log)
        root=psutil.Process(process.pid)
        try:
            while process.poll() is None:
                try:
                    value=json.loads((output/'lifecycle-progress.json').read_text(encoding='utf-8'))
                    if value['phase']!=phase:phase=value['phase'];print('STEP:',phase,flush=True)
                except (FileNotFoundError,json.JSONDecodeError):pass
                total=0; python_memory=0; resident=0; python_resident=0; pids=[]
                try:
                    family=[root,*root.children(recursive=True)]
                except psutil.NoSuchProcess:
                    # The native process can finish between poll() and enumeration.
                    process.wait(timeout=5)
                    break
                for item in family:
                    try:
                        memory=item.memory_info();total+=memory.private;resident+=memory.rss;pids.append(item.pid)
                        if item.name().lower()=='python.exe':owned_python.add(item.pid);python_memory+=memory.private;python_resident+=memory.rss
                    except (psutil.NoSuchProcess,psutil.AccessDenied):pass
                samples.append({'seconds':round(time.perf_counter()-started,3),'phase':phase,'private_bytes':total,'python_private_bytes':python_memory,'working_set_bytes':resident,'python_working_set_bytes':python_resident,'processes':len(pids)})
                if time.perf_counter()-started>420:raise TimeoutError('Lifecycle check exceeded seven minutes')
                time.sleep(.25)
        finally:
            if process.poll() is None:
                try: children=root.children(recursive=True)
                except psutil.NoSuchProcess: children=[]
                for child in children:
                    try:child.kill()
                    except psutil.NoSuchProcess:pass
                process.kill();process.wait()
    with (output/'memory.csv').open('w',newline='',encoding='utf-8') as stream:
        writer=csv.DictWriter(stream,fieldnames=samples[0].keys());writer.writeheader();writer.writerows(samples)
    summary_path=output/'lifecycle-summary.json'
    summary=json.loads(summary_path.read_text(encoding='utf-8')) if summary_path.exists() else {'passed':False,'error':'Native check exited before saving its report; see console.log'}
    idle=[row for row in samples if row['phase'].startswith('idle-')]
    summary.update(exit_code=process.returncode,peak_private_bytes=max(row['private_bytes'] for row in samples),peak_python_private_bytes=max(row['python_private_bytes'] for row in samples),idle_private_bytes=max(row['private_bytes'] for row in idle) if idle else None,idle_python_private_bytes=max(row['python_private_bytes'] for row in idle) if idle else None,owned_python_pids=sorted(owned_python),owned_python_exited=all(not psutil.pid_exists(pid) for pid in owned_python))
    summary['memory_by_phase']={name:{key:max(row[key] for row in samples if row['phase']==name) for key in ('private_bytes','python_private_bytes','working_set_bytes','python_working_set_bytes')} for name in sorted({row['phase'] for row in samples})}
    summary['passed']=summary['passed'] and process.returncode==0 and summary['owned_python_exited'] and summary['idle_python_private_bytes']==0
    (output/'summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8');print(json.dumps(summary,indent=2),flush=True)
    return 0 if summary['passed'] else 1


if __name__=='__main__':raise SystemExit(main())
