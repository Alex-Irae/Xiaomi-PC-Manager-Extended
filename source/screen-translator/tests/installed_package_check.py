"""Verify full payload extraction, relocated executable startup and actual scoped uninstall in the workspace.

Dependencies: stdlib, completed offline build, existing dependencies for startup helper.
Outputs: new results/installed/ and results/packaged/; owned test installation is uninstalled.
Command: python tests/installed_package_check.py --build artifacts/NNN_timestamp_seed0
No Program Files, startup entries, desktop capture or package installation is used.
"""
import argparse
import json
from pathlib import Path
import subprocess
import sys
from time import monotonic,sleep
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from screen_translator.cli import result_directory,save_json
from screen_translator.models import digest


def main():
    parser=argparse.ArgumentParser(description="Install the full offline app into an isolated workspace folder, verify relocated startup, then uninstall only its owned files")
    parser.add_argument("--build",type=Path,required=True,help="Completed build directory with setup/payload/checksum")
    args=parser.parse_args();root=Path(__file__).resolve().parents[1];directory=result_directory(root/"results/installed");app=directory/"custom location"/"Screen Translator"
    save_json(directory/"config.json",{"seed":0,"build":str(args.build.resolve()),"destination":str(app),"protocol":"Full payload checksum/extraction, relocated EXE and private runtimes, isolated user data, scoped actual uninstall preserving an unknown file","registration":False,"screen_captured":False})
    print(f"Extracting full offline installation into {app}",flush=True)
    subprocess.run([str(args.build.resolve()/"ScreenTranslator-Setup.exe"),"--quiet","--destination",str(app),"--no-register"],check=True)
    print("Full installer integrity verification passed; checking relocated startup",flush=True)
    subprocess.run([sys.executable,str(root/"tests/packaged_startup_check.py"),"--app",str(app),"--seconds","10"],check=True,cwd=root)
    files=json.loads((app/"installed-files.json").read_text(encoding="utf-8"))
    print("Checking installed files remained immutable during model startup",flush=True)
    immutable=all((app/name).is_file() and digest(app/name)==expected for name,expected in files.items())
    extras=[p.relative_to(app).as_posix() for p in app.rglob("*") if p.is_file() and p.relative_to(app).as_posix() not in files and p.name!="installed-files.json"]
    unknown=app/"keep-user-file.txt";unknown.write_text("unrelated user file",encoding="utf-8")
    print("Uninstalling the owned test installation; preserving the unknown file",flush=True)
    subprocess.run([str(app/"Uninstall.exe"),"--quiet","--no-register"],check=True)
    deadline=monotonic()+120
    while (app/"installed-files.json").exists() and monotonic()<deadline:sleep(.5)
    removed=all(not (app/name).exists() for name in files) and not (app/"installed-files.json").exists()
    preserved=unknown.is_file() and unknown.read_text(encoding="utf-8")=="unrelated user file"
    result={"full_install_succeeded":True,"relocated_startup_succeeded":True,"installed_assets_immutable":immutable,"unexpected_app_files":extras,"owned_files_removed":removed,"unknown_file_preserved":preserved,"screen_captured":False}
    result["ok"]=immutable and not extras and removed and preserved;save_json(directory/"summary.json",result);print(json.dumps(result),flush=True);return 0 if result["ok"] else 1


if __name__=="__main__":raise SystemExit(main())
