"""Verify the packaged EXE/private backend and measure resident idle activity, without desktop capture.

Dependencies: existing psutil, Windows energy counter helper; bundled application runtimes.
Outputs: new results/packaged/ configuration, idle summary and figure, isolated per-run user data.
Command: python tests/packaged_startup_check.py --app artifacts/NNN_timestamp_seed0/app --seconds 10
"""
import argparse
import ctypes
import json
from pathlib import Path
import subprocess
import sys
from time import perf_counter,sleep
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import psutil
from screen_translator.cli import result_directory,save_json
from model_efficiency_check import PackageEnergy


def main():
    parser=argparse.ArgumentParser(description="Launch the packaged tray app with isolated data and no startup registration or desktop capture")
    parser.add_argument("--app",type=Path,required=True,help="Directory containing the packaged ScreenTranslator.exe")
    parser.add_argument("--seconds",type=float,default=10,help="Seconds of idle sampling after readiness")
    args=parser.parse_args();root=Path(__file__).resolve().parents[1];app=args.app.resolve();directory=result_directory(root/"results/packaged")
    save_json(directory/"config.json",{"seed":0,"app":str(app),"seconds":args.seconds,"screen_captured":False,"startup_registration":False,"protocol":"Cold packaged EXE launch with user site disabled; wait for model readiness, measure own process-tree CPU and whole-package idle energy; no translation target"})
    print(f"Packaged startup: {directory}",flush=True);started=perf_counter()
    process=subprocess.Popen([str(app/"ScreenTranslator.exe"),"--tray","--no-startup","--data-dir",str(directory/"data")],cwd=app)
    root_process=psutil.Process(process.pid)
    try:
        log=directory/"data/native.log";last="";deadline=perf_counter()+240
        while perf_counter()<deadline:
            if process.poll() is not None:raise RuntimeError(f"Packaged app stopped: {process.returncode}")
            content=log.read_text(encoding="utf-8-sig") if log.exists() else ""
            if content!=last:
                lines=content[len(last):].splitlines();print("\n".join(lines[-5:]),flush=True);last=content
            if "Models ready · fully local" in content:break
            if "Action failed:" in content:raise RuntimeError(content.splitlines()[-1])
            sleep(1)
        else:raise TimeoutError("Packaged models did not become ready in 240 seconds")
        readiness=perf_counter()-started;processes=[root_process,*root_process.children(recursive=True)]
        def cpu():
            total=0
            for child in processes:
                try:t=child.cpu_times();total+=t.user+t.system
                except psutil.NoSuchProcess:pass
            return total
        baseline=cpu();energy=None;error=None
        try:energy=PackageEnergy()
        except Exception as exception:error=str(exception)
        before=energy.read() if energy else None;watch=perf_counter();sleep(args.seconds);elapsed=perf_counter()-watch;after=energy.read() if energy else None
        cpu_s=cpu()-baseline;joules=(after-before)*3.6e-9 if energy else None
        tree=[]
        for child in processes:
            try:tree.append({"pid":child.pid,"exe":child.exe(),"command":child.cmdline(),"rss_mb":child.memory_info().rss/1e6})
            except psutil.NoSuchProcess:pass
        summary={"ready":True,"readiness_s":readiness,"idle_elapsed_s":elapsed,"own_tree_cpu_s":cpu_s,"own_tree_one_core_percent":cpu_s/elapsed*100,"whole_package_idle_watts":joules/elapsed if joules else None,"energy_error":error,"processes":tree,"screen_captured":False,"limitations":"Package power includes every running application and is not an attributable app or laptop/battery measurement. Warm resident RSS includes shared pages. Readiness is not live translation acceptance."}
        save_json(directory/"summary.json",summary)
        if energy:energy.close()
        figure=f'<svg xmlns="http://www.w3.org/2000/svg" width="820" height="180"><rect width="100%" height="100%" fill="white"/><g font-family="Segoe UI" fill="#24324a"><text x="20" y="30">Packaged resident idle, no translation target</text><text x="20" y="75">App process tree: {cpu_s:.3f} CPU seconds / {elapsed:.1f} seconds ({cpu_s/elapsed*100:.2f}% of one core)</text><text x="20" y="110">Whole-package idle: {summary["whole_package_idle_watts"]} W, includes background apps</text><text x="20" y="150">Ready in {readiness:.1f} seconds; no screen captured</text></g></svg>'
        (directory/"idle.svg").write_text(figure,encoding="utf-8");print(json.dumps({k:v for k,v in summary.items() if k!="processes"}),flush=True)
    finally:
        user=ctypes.WinDLL("user32");user.RegisterWindowMessageW.argtypes=[ctypes.c_wchar_p];user.PostMessageW.argtypes=[ctypes.c_void_p,ctypes.c_uint,ctypes.c_void_p,ctypes.c_void_p]
        user.PostMessageW(0xffff,user.RegisterWindowMessageW("LocalScreenTranslator.Quit"),None,None)
        try:process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            for child in root_process.children(recursive=True):child.terminate()
            process.terminate();process.wait(timeout=5)
    return 0


if __name__=="__main__":raise SystemExit(main())
