"""Bundle installed runtimes/dependencies and compile an offline EXE/installer, without installing anything.

Dependencies: Windows Python 3.12 with root requirements already installed, existing .NET 8 SDK,
WebView2 reference DLLs, Windows .NET Framework C# compiler. Outputs: new artifacts/NNN_timestamp_seed0.
Command: python tools/build_app.py --sdk C:/path/dotnet.exe --webview C:/path/WebView2
"""
import argparse
from datetime import datetime,timezone
import hashlib
import importlib.metadata as metadata
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

from packaging.requirements import Requirement

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from screen_translator.cli import result_directory
from screen_translator.models import digest,validate


def run(command):
    print("Running: "+str(command[0])+" "+str(command[1]),flush=True)
    subprocess.run([str(value) for value in command],check=True,cwd=ROOT)


def dependencies():
    """Resolve installed distribution metadata, including shared optimum namespace files."""
    pending=["openvino","optimum-intel","optimum","transformers","torch","numpy","Pillow",
             "opencv-python","rapidocr-onnxruntime","sentencepiece","onnx"]
    selected={}
    while pending:
        name=pending.pop();dist=metadata.distribution(name);canonical=dist.metadata["Name"].lower().replace("_","-")
        if canonical in selected:continue
        selected[canonical]=dist
        for text in dist.requires or []:
            requirement=Requirement(text)
            if requirement.marker and not requirement.marker.evaluate({"extra":""}):continue
            pending.append(requirement.name)
    return selected


def copy_runtime(destination,selected):
    base=Path(sys.base_prefix);python=destination/"runtime/python";python.mkdir(parents=True)
    print("Copying Python standard library and native modules",flush=True)
    for name in ("python.exe","pythonw.exe","python3.dll","python312.dll","vcruntime140.dll","vcruntime140_1.dll","LICENSE.txt"):
        shutil.copy2(base/name,python/name)
    for name in ("Lib","DLLs"):
        shutil.copytree(base/name,python/name,ignore=shutil.ignore_patterns("site-packages","__pycache__","test","tests","*.pyc"))
    site=python/"Lib/site-packages";site.mkdir(parents=True,exist_ok=True)
    for name,dist in sorted(selected.items()):
        print(f"Bundling {name} {dist.version}",flush=True)
        source=Path(dist.locate_file("")).resolve()
        for relative in dist.files or []:
            file=Path(dist.locate_file(relative)).resolve()
            if not file.is_relative_to(source) or not file.is_file():continue
            rel=file.relative_to(source)
            if "__pycache__" in rel.parts or file.suffix==".pyc":continue
            target=site/rel;target.parent.mkdir(parents=True,exist_ok=True)
            if not target.exists():shutil.copy2(file,target)


def main():
    parser=argparse.ArgumentParser(description="Package existing installed runtimes into a standalone offline Windows app")
    parser.add_argument("--sdk",type=Path,required=True,help="Existing .NET 8 SDK dotnet.exe")
    parser.add_argument("--webview",type=Path,required=True,help="Existing WebView2 reference assemblies directory")
    parser.add_argument("--output",type=Path,help="Existing staging directory for rebuilding code without recopying runtimes; initial runs create a new numbered directory")
    parser.add_argument("--stage-only",action="store_true",help="Refresh compiled app/source before verification, without sealing or compressing the deliverable")
    args=parser.parse_args()
    validate(ROOT/"models/zh-en")
    directory=args.output.resolve() if args.output else result_directory(ROOT/"artifacts")
    if not directory.is_relative_to(ROOT/"artifacts"):raise ValueError("Output must stay inside this project's artifacts directory")
    app=directory/"app";app.mkdir(parents=True,exist_ok=True)
    os.environ.update(DOTNET_CLI_HOME=str(ROOT/"native/.cli"),DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",DOTNET_CLI_TELEMETRY_OPTOUT="1")
    run([args.sdk,"build",ROOT/"native/ScreenTranslator.csproj","--no-restore","-c","Release",f"-p:WebViewReferenceDir={args.webview}","-p:NuGetAudit=false"])
    for file in (ROOT/"native/bin/Release/net8.0-windows").iterdir():
        if file.is_file() and file.suffix!=".pdb":shutil.copy2(file,app/file.name)
    for folder in ("frontend","screen_translator"):
        shutil.copytree(ROOT/folder,app/folder,dirs_exist_ok=True,ignore=shutil.ignore_patterns("__pycache__","*.pyc"))
    for name in ("README.md","LICENSE","NOTICE.md","requirements.txt"):shutil.copy2(ROOT/name,app/name)
    for folder in ("native","packaging","tools"):
        source=app/"source"/folder;source.mkdir(parents=True,exist_ok=True)
        for file in (ROOT/folder).iterdir():
            if file.is_file() and file.suffix in (".cs",".csproj",".Config",".py",".manifest"):
                shutil.copy2(file,source/file.name)
    bundle=app/"models/zh-en"
    if not bundle.exists():
        shutil.copytree(ROOT/"models/zh-en",bundle,ignore=shutil.ignore_patterns("model_cache"))
    seed=ROOT/"models/zh-en/model_cache/native-device-choice.json"
    if seed.is_file():shutil.copy2(seed,app/"calibration-seed.json")
    selected=dependencies()
    if not (app/"runtime/python").exists():copy_runtime(app,selected)
    dotnet=app/"runtime/dotnet"
    if not dotnet.exists():
        print("Bundling private .NET desktop runtime",flush=True)
        dotnet.mkdir(parents=True);sdk=args.sdk.resolve().parent
        shutil.copy2(sdk/"dotnet.exe",dotnet/"dotnet.exe")
        shutil.copytree(sdk/"host/fxr",dotnet/"host/fxr")
        for name in ("Microsoft.NETCore.App","Microsoft.WindowsDesktop.App"):
            shutil.copytree(sdk/"shared"/name,dotnet/"shared"/name)
        for name in ("LICENSE.txt","ThirdPartyNotices.txt"):
            if (sdk/name).exists():shutil.copy2(sdk/name,dotnet/name)
    csc=Path(os.environ["SystemRoot"])/"Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    compiler=[csc,"/nologo","/platform:x64","/reference:System.Windows.Forms.dll","/reference:System.Drawing.dll",f"/win32manifest:{ROOT/'packaging/app.manifest'}"]
    run([*compiler,f"/out:{directory/'IconBuilder.exe'}",ROOT/"packaging/IconBuilder.cs"])
    run([directory/"IconBuilder.exe",ROOT/"frontend/logo.ico"])
    shutil.copy2(ROOT/"frontend/logo.ico",app/"frontend/logo.ico")
    run([*compiler,"/target:winexe",f"/win32icon:{ROOT/'frontend/logo.ico'}",f"/out:{app/'ScreenTranslator.exe'}",ROOT/"packaging/Launcher.cs"])
    setup=[*compiler,"/target:winexe","/reference:System.IO.Compression.dll","/reference:System.IO.Compression.FileSystem.dll","/reference:System.Web.Extensions.dll",f"/win32icon:{ROOT/'frontend/logo.ico'}"]
    run([*setup,"/define:UNINSTALL",f"/out:{app/'Uninstall.exe'}",ROOT/"packaging/Setup.cs"])
    run([*setup,f"/out:{directory/'ScreenTranslator-Setup.exe'}",ROOT/"packaging/Setup.cs"])
    packaged={"name":"Screen Translator","version":"0.1.0","created_utc":datetime.now(timezone.utc).isoformat(),"offline":True,"models_sha256":digest(bundle/"manifest.json"),"python":sys.version,"dependencies":{name:dist.version for name,dist in selected.items()}}
    (app/"packaged.json").write_text(json.dumps(packaged,indent=2),encoding="utf-8")
    if args.stage_only:
        print(f"App staged for verification: {app}; complete build still required for the installer",flush=True)
        return
    print("Hashing owned application files",flush=True)
    files={file.relative_to(app).as_posix():digest(file) for file in sorted(app.rglob("*")) if file.is_file() and file.name!="installed-files.json"}
    (app/"installed-files.json").write_text(json.dumps(files,indent=2),encoding="utf-8")
    print("Compressing offline payload",flush=True)
    with zipfile.ZipFile(directory/"payload.zip","w",compression=zipfile.ZIP_DEFLATED,compresslevel=1,allowZip64=True) as archive:
        for file in app.rglob("*"):
            if file.is_file():archive.write(file,file.relative_to(app).as_posix())
    (directory/"payload.sha256").write_text(digest(directory/"payload.zip"),encoding="ascii")
    config={"seed":0,"build":"offline-windows-x64","sdk":str(args.sdk.resolve()),"webview":str(args.webview.resolve()),"python_source":sys.base_prefix,"dependencies":packaged["dependencies"],"model_manifest_sha256":packaged["models_sha256"],"files":len(files),"installed_bytes":sum(file.stat().st_size for file in app.rglob("*") if file.is_file()),"payload_bytes":(directory/"payload.zip").stat().st_size}
    (directory/"config.json").write_text(json.dumps(config,indent=2),encoding="utf-8")
    (directory/"INSTALL.txt").write_text("Keep ScreenTranslator-Setup.exe, payload.zip and payload.sha256 together. Run Setup and choose an empty dedicated folder. Default: Program Files/Screen Translator.\nThe app includes Python, .NET and the working models. WebView2 Runtime and compatible Intel graphics/NPU drivers must already be installed.\nCopilot toggles translation by default; Settings can record any available key/chord. Esc dismisses. Start menu opens controls. Closing hides to tray; Exit unloads.\nSave PNG in the toolbar exports translated screenshots to Pictures/Screen Translator, or the folder chosen in Settings.\nUninstall through Windows Settings or Uninstall.exe. User data and screenshots are retained.\n",encoding="utf-8")
    print(f"Built {directory}: {config['installed_bytes']/1e9:.2f} GB installed, {config['payload_bytes']/1e9:.2f} GB payload",flush=True)


if __name__=="__main__":main()
