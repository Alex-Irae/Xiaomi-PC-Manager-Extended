"""Measure the desktop backend's readiness on the drive test snapshot.
Dependencies: independent packaged Python and a completed encrypted drive index.
Outputs: new test copy, stdout/stderr and readiness.json; original index untouched.
Command: APP/runtime/python/python.exe tests/check_drive_startup.py --python APP/runtime/python/python.exe
         --config DRIVE/config.json --index DRIVE/data/index.sqlite3.dpapi --output results/NEW/startup
The owned test process is terminated if it cannot finish within --timeout seconds.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import time


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--python',required=True,help='Independent packaged Python executable')
    parser.add_argument('--config',required=True,help='Completed drive test configuration')
    parser.add_argument('--index',required=True,type=Path,help='Encrypted drive test snapshot')
    parser.add_argument('--output',required=True,type=Path,help='New evidence folder')
    parser.add_argument('--timeout',type=int,default=30,help='Maximum startup/shutdown measurement seconds')
    args=parser.parse_args()
    args.output=args.output.resolve()
    args.output.mkdir(parents=True,exist_ok=False)
    data=args.output/'data'
    data.mkdir()
    shutil.copy2(args.index,data/'index.sqlite3.dpapi')
    config=json.loads(Path(args.config).read_text(encoding='utf-8-sig'))
    (args.output/'config.json').write_text(json.dumps(config,indent=2),encoding='utf-8')
    command=[args.python,'-m','xiaomi_search.backend','--config',str(args.output/'config.json'),'--data',str(data),'--paused','--no-shortcut']
    start=time.perf_counter()
    process=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
    timed_out=False
    try:
        stdout,stderr=process.communicate('',timeout=args.timeout)
    except subprocess.TimeoutExpired:
        timed_out=True
        process.kill()
        stdout,stderr=process.communicate()
    report={'seed':42,'configuration':config,'command':command,'timeout_seconds':args.timeout,'seconds':time.perf_counter()-start,'ready':any('"kind": "ready"' in line for line in stdout.splitlines()),'timed_out':timed_out,'exit_code':process.returncode,'full_content_semantic_complete':False}
    (args.output/'stdout.txt').write_text(stdout,encoding='utf-8')
    (args.output/'stderr.txt').write_text(stderr,encoding='utf-8')
    (args.output/'readiness.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print('STARTUP '+json.dumps({key:report[key] for key in ('seconds','ready','timed_out','exit_code')}),flush=True)


if __name__=='__main__':main()
