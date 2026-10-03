"""Exercise real compiled installer/uninstaller with tiny owned fixtures inside new result folders.

Dependencies: stdlib, compiled Setup.exe/Uninstall.exe. Outputs: new results/installer/ evidence.
Command: python tests/installer_check.py --build artifacts/NNN_timestamp_seed0
Never registers startup/shortcuts, touches Program Files, or recursively removes a directory.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
from time import monotonic,sleep
import zipfile
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from screen_translator.cli import result_directory,save_json
from screen_translator.models import digest


def main():
    parser=argparse.ArgumentParser(description="Verify installer path/integrity/uninstaller boundaries using only tiny owned fixtures")
    parser.add_argument("--build",type=Path,required=True,help="Build folder containing compiled installer and app/Uninstall.exe")
    args=parser.parse_args();root=Path(__file__).resolve().parents[1];directory=result_directory(root/"results/installer");setup=directory/"setup";setup.mkdir()
    shutil.copy2(args.build/"ScreenTranslator-Setup.exe",setup/"ScreenTranslator-Setup.exe")
    fixture=directory/"fixture";fixture.mkdir();(fixture/"ScreenTranslator.exe").write_text("owned fixture, not executable",encoding="utf-8");(fixture/"packaged.json").write_text('{"name":"Screen Translator"}',encoding="utf-8")
    shutil.copy2(args.build/"app/Uninstall.exe",fixture/"Uninstall.exe")
    deep=Path("runtime")/"long-dependency-folder"/"nested-library-component"/"another-deep-package-component"/"module-with-a-long-name"/"more-nesting-for-long-path-validation"/"owned.txt"
    (fixture/deep).parent.mkdir(parents=True);(fixture/deep).write_text("long path fixture",encoding="utf-8")
    files={p.relative_to(fixture).as_posix():digest(p) for p in fixture.rglob("*") if p.is_file()};(fixture/"installed-files.json").write_text(json.dumps(files),encoding="utf-8")
    payload=setup/"payload.zip"
    with zipfile.ZipFile(payload,"w") as archive:
        for file in fixture.rglob("*"):
            if file.is_file():archive.write(file,file.relative_to(fixture).as_posix())
    (setup/"payload.sha256").write_text(digest(payload),encoding="ascii")
    destination=directory/"chosen application folder";save_json(directory/"config.json",{"seed":0,"build":str(args.build.resolve()),"fixture":True,"destination":str(destination),"registration":False,"protocol":"Compiled setup path with spaces, hash verification, nonempty refusal, scoped uninstall preserves unknown files; no real apps installed"})
    command=[str(setup/"ScreenTranslator-Setup.exe"),"--quiet","--destination",str(destination),"--no-register"]
    installed=subprocess.run(command).returncode==0 and all((destination/name).exists() for name in files)
    long_path=(destination/deep).exists() and len(str(destination/deep))>260
    refused=subprocess.run(command).returncode!=0
    unknown=destination/"user-file.txt";unknown.write_text("must be preserved",encoding="utf-8")
    process=subprocess.Popen([str(destination/"Uninstall.exe"),"--quiet","--no-register"]);process.wait(timeout=10)
    deadline=monotonic()+20
    while (destination/"Uninstall.exe").exists() and monotonic()<deadline:sleep(.1)
    removed=all(not (destination/name).exists() for name in files) and not (destination/"installed-files.json").exists()
    preserved=unknown.read_text(encoding="utf-8")=="must be preserved"
    # A correct ZIP checksum does not make traversal entries acceptable.
    with zipfile.ZipFile(payload,"w") as archive:archive.writestr("../escaped.txt","unsafe")
    (setup/"payload.sha256").write_text(digest(payload),encoding="ascii")
    unsafe=directory/"unsafe target";blocked=subprocess.run([str(setup/"ScreenTranslator-Setup.exe"),"--quiet","--destination",str(unsafe),"--no-register"]).returncode!=0 and not (directory/"escaped.txt").exists()
    (setup/"payload.sha256").write_text("0"*64,encoding="ascii")
    hash_blocked=subprocess.run([str(setup/"ScreenTranslator-Setup.exe"),"--quiet","--destination",str(directory/"bad hash"),"--no-register"]).returncode!=0
    result={"installed_to_custom_path_with_spaces":installed,"long_dependency_path_supported":long_path,"nonempty_target_refused":refused,"owned_files_removed":removed,"unknown_file_preserved":preserved,"traversal_blocked":blocked,"bad_hash_blocked":hash_blocked}
    result["ok"]=all(result.values());save_json(directory/"summary.json",result);print(json.dumps(result),flush=True);return 0 if result["ok"] else 1


if __name__=="__main__":raise SystemExit(main())
