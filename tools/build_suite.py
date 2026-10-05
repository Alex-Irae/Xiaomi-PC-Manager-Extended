"""Build an independent offline suite from copied source and already-installed dependencies.

Dependencies: Windows, Python 3.12 with translator packages already installed, existing .NET 8 SDK,
existing NuGet cache, installed search runtime/models, .NET Framework compiler and WebView2 assemblies.
Outputs: install/ application folders, numbered packages/ releases, installers and build evidence.
Command: python tools/build_suite.py --sdk PATH --package-cache PATH --webview PATH --search-install PATH
No package installation, environment creation, model download, application launch or Git command occurs.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.metadata as metadata
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'source'
INSTALL = ROOT / 'install'
SKIP = shutil.ignore_patterns('__pycache__', '*.pyc', '*.pyo', 'bin', 'obj', 'test-data', 'model_cache')
PRIVATE = {'bin', 'obj', '__pycache__', 'model_cache', '.git', '.venv', 'data', 'results', '.test-environment'}
PRIVATE_FILES = {'config.json', 'included-folders.txt', 'excluded-folders.txt'}
RELEASE = '0.3.0'
VERSIONS = {'pc-manager': '0.2.1', 'file-search': '0.3.0', 'screen-translator': '0.2.1'}


def public_source(directory):
    return sorted(path for path in directory.rglob('*') if path.is_file()
                  and not PRIVATE.intersection(path.relative_to(directory).parts)
                  and path.name not in PRIVATE_FILES and path.suffix not in {'.pyc', '.pyo'})


def development_copy(app, component, executable):
    """Preserve a buildable copy; user edits stay separate from installed application files."""
    dev = app / 'Development'
    for directory in [SOURCE / component, SOURCE / 'shared']:
        for path in public_source(directory):
            destination = dev / 'source' / directory.name / path.relative_to(directory)
            destination.parent.mkdir(parents=True, exist_ok=True)
            copy_file(path, destination)
    copy_file(SOURCE / 'NuGet.Config', dev / 'source' / 'NuGet.Config')
    copy_file(ROOT / 'tools' / 'launch_development.ps1', dev / 'Launch.ps1')
    (dev / 'Launch.cmd').write_text('@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launch.ps1" %*\r\nif errorlevel 1 pause\r\n', encoding='ascii')
    project = {'pc-manager': 'XiaomiAIManager.csproj', 'file-search': 'native/AICenter.csproj', 'screen-translator': 'native/ScreenTranslator.csproj'}[component]
    info = {'name': app.name, 'component': component, 'executable': executable,
            'project': f'source/{component}/{project}', 'version': VERSIONS[component]}
    (dev / 'development.json').write_text(json.dumps(info, indent=2), encoding='utf-8')
    (dev / 'README.md').write_text(f'''# {app.name} development

Edit `source/{component}` and double-click `Launch.cmd`. This rebuilds your changes and launches the editable copy.
The first launch copies the installed offline runtimes and models into `App`. No package or model download is needed.
The existing installed user profile and shared shortcuts are used. Launch stops the installed copy of this app first.
PC Manager still requires Windows administrator approval for hardware controls.

For a build without launching: `powershell -NoProfile -File .\\Launch.ps1 -BuildOnly`.
Keep this Development folder inside its app folder. If obtained from GitHub, extract it there and extract
`development-toolchain.zip` into `Development/toolchain`. The toolchain contains the existing .NET 8 SDK,
offline NuGet cache and WebView2 references. Windows 11 x64 and WebView2 Runtime are required.
Application outputs go to `output` and `App`; settings, history and indexes remain at the installer-selected data paths.
`Archives` holds local original sources and data retained during migration and is never uploaded to GitHub.
Do not run the installed and development copies simultaneously. To return to the installed copy, quit the development
app and start the app EXE in the parent folder.
''', encoding='utf-8')


def copy_file(source, destination):
    target = Path(destination)
    if not target.resolve().is_relative_to(ROOT):
        raise ValueError('Build copies must remain inside the suite')
    if target.is_file():
        target.chmod(target.stat().st_mode | stat.S_IWRITE)
    return shutil.copy2(source, target)


def copy_tree(source, target):
    """Copy files into our own deployment; never link to or modify the origin."""
    shutil.copytree(source, target, dirs_exist_ok=True, ignore=SKIP, copy_function=copy_file)


def run(arguments):
    print('BUILD:', ' '.join(str(value) for value in arguments), flush=True)
    subprocess.run([str(value) for value in arguments], check=True, cwd=ROOT)


def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def translator_dependencies():
    """Collect installed distribution files and transitive dependencies, without installing anything."""
    from packaging.requirements import Requirement
    pending = ['openvino', 'optimum-intel', 'optimum', 'transformers', 'torch', 'numpy',
               'Pillow', 'opencv-python', 'rapidocr-onnxruntime', 'sentencepiece', 'onnx']
    selected = {}
    while pending:
        distribution = metadata.distribution(pending.pop())
        name = distribution.metadata['Name'].lower().replace('_', '-')
        if name in selected:
            continue
        selected[name] = distribution
        for text in distribution.requires or []:
            requirement = Requirement(text)
            if not requirement.marker or requirement.marker.evaluate({'extra': ''}):
                pending.append(requirement.name)
    return selected


def copy_translator_python(target):
    base = Path(sys.base_prefix)
    target.mkdir(parents=True, exist_ok=True)
    for name in ['python.exe', 'pythonw.exe', 'python3.dll', 'python312.dll', 'LICENSE.txt']:
        shutil.copy2(base / name, target / name)
    for name in ['Lib', 'DLLs']:
        shutil.copytree(base / name, target / name, dirs_exist_ok=True,
                        ignore=shutil.ignore_patterns('site-packages', '__pycache__', '*.pyc', 'test', 'tests'))
    site = target / 'Lib' / 'site-packages'
    site.mkdir(parents=True, exist_ok=True)
    selected = translator_dependencies()
    for name, distribution in sorted(selected.items()):
        print(f'COPY dependency: {name} {distribution.version}', flush=True)
        base = Path(distribution.locate_file('')).resolve()
        for relative in distribution.files or []:
            file = Path(distribution.locate_file(relative)).resolve()
            if not file.is_relative_to(base) or not file.is_file() or '__pycache__' in file.parts or file.suffix == '.pyc':
                continue
            destination = site / file.relative_to(base)
            destination.parent.mkdir(parents=True, exist_ok=True)
            if not destination.exists():
                shutil.copy2(file, destination)
    return {name: distribution.version for name, distribution in selected.items()}


def compile_launcher(csc, destination, define, icon):
    run([csc, '/nologo', '/target:winexe', '/platform:x64', '/reference:System.Windows.Forms.dll', '/reference:Microsoft.CSharp.dll',
         f'/define:{define}', f'/win32icon:{icon}', f'/out:{destination}', SOURCE / 'shared' / 'Launcher.cs'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sdk', type=Path, required=True, help='Existing .NET 8 SDK dotnet.exe')
    parser.add_argument('--package-cache', type=Path, required=True, help='Existing complete NuGet package cache; copied before compiling')
    parser.add_argument('--webview', type=Path, required=True, help='Existing WebView2 reference assemblies and native loader')
    parser.add_argument('--search-install', type=Path, required=True, help='Existing installed search runtime/models, read only')
    parser.add_argument('--translator-models', type=Path, required=True, help='Existing working zh-en bundle, copied into the suite')
    parser.add_argument('--native-only', action='store_true', help='Recompile changed native code without recopying runtimes or sealing packages')
    parser.add_argument('--seal', action='store_true', help='Seal a new numbered release after --native-only; reuse the already-copied private runtimes')
    args = parser.parse_args()
    sdk = args.sdk.resolve()
    toolchain = ROOT / 'toolchain'
    webview = toolchain / 'webview'
    packages = toolchain / 'packages'
    webview.mkdir(parents=True, exist_ok=True)
    for name in ['Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll']:
        if (args.webview / name).resolve() != (webview / name).resolve():
            shutil.copy2(args.webview / name, webview / name)
    loader = args.webview / 'runtimes' / 'win-x64' / 'native' / 'WebView2Loader.dll'
    destination = webview / 'runtimes' / 'win-x64' / 'native' / 'WebView2Loader.dll'
    destination.parent.mkdir(parents=True, exist_ok=True)
    if loader.resolve() != destination.resolve():
        shutil.copy2(loader, destination)
    if not packages.exists():
        print('COPY existing build packages', flush=True)
        copy_tree(args.package_cache, packages)
    os.environ.update(NUGET_PACKAGES=str(packages), DOTNET_CLI_HOME=str(toolchain / 'cli'),
                      DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1',
                      DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0', DOTNET_GENERATE_ASPNET_CERTIFICATE='0')
    projects = [('pc-manager', SOURCE / 'pc-manager' / 'XiaomiAIManager.csproj'),
                ('file-search', SOURCE / 'file-search' / 'native' / 'AICenter.csproj'),
                ('screen-translator', SOURCE / 'screen-translator' / 'native' / 'ScreenTranslator.csproj')]
    outputs = {}
    for component, project in projects:
        outputs[component] = ROOT / 'build' / component
        run([sdk, 'build', project, '-c', 'Release', '--configfile', SOURCE / 'NuGet.Config',
             '-p:NuGetAudit=false', '-p:RestoreSources=', f'-p:WebViewReferenceDir={webview}', '-o', outputs[component]])
    INSTALL.mkdir(parents=True, exist_ok=True)
    marker = INSTALL / 'suite-install.json'
    if not marker.exists():
        marker.write_text(json.dumps({'schema': 1, 'profileId': 'preview', 'portable': True}, indent=2), encoding='utf-8')
    csc = Path(os.environ['SystemRoot']) / 'Microsoft.NET' / 'Framework64' / 'v4.0.30319' / 'csc.exe'
    folder_names = {'pc-manager': 'PC Manager', 'file-search': 'AI Center', 'screen-translator': 'Screen Translator'}
    dependency_versions = {}
    if args.native_only:
        prior = sorted((ROOT / 'packages').glob('*/build-config.json'))
        if prior:
            dependency_versions = json.loads(prior[-1].read_text(encoding='utf-8')).get('translatorDependencies', {})
    for component, folder_name in folder_names.items():
        print('STAGE:', component, flush=True)
        app = INSTALL / folder_name
        app.mkdir(parents=True, exist_ok=True)
        runtime = app / 'runtime' / 'dotnet'
        if not args.native_only:
            copy_tree(args.search_install / 'runtime' / 'dotnet', runtime)
        if component == 'pc-manager':
            copy_tree(outputs[component], runtime)
            icon = SOURCE / component / 'assets' / 'app.ico'
            compile_launcher(csc, app / 'PCManager.exe', 'PC_MANAGER', icon)
            shutil.copy2(SOURCE / component / 'LICENSE', app / 'LICENSE')
        elif component == 'file-search':
            for file in outputs[component].iterdir():
                if file.is_file():
                    shutil.copy2(file, runtime / file.name)
            for name in ['frontend', 'xiaomi_search']:
                copy_tree(SOURCE / component / name, app / name)
            template = json.loads((SOURCE / component / 'config.example.json').read_text(encoding='utf-8-sig'))
            template.update(roots=[], excluded_folders=[], model_path='models/qwen3-embedding', run_at_startup=False)
            (app / 'config.example.json').write_text(json.dumps(template, indent=2), encoding='utf-8')
            if not args.native_only:
                copy_tree(args.search_install / 'runtime' / 'python', app / 'runtime' / 'python')
                copy_tree(args.search_install / 'models', app / 'models')
            notices = SOURCE / component / 'packaging' / 'notices'
            if notices.exists():
                copy_tree(notices, app / 'notices')
            icon = SOURCE / component / 'native' / 'assets' / 'app.ico'
            compile_launcher(csc, app / 'AI Center.exe', 'FILE_SEARCH', icon)
            (app / 'package-manifest.json').write_text(json.dumps({'appId': 'XiaomiRevamp.AICenter', 'version': VERSIONS[component]}), encoding='utf-8')
        else:
            copy_tree(outputs[component], app)
            for name in ['frontend', 'screen_translator']:
                copy_tree(SOURCE / component / name, app / name)
            if not args.native_only:
                copy_tree(args.translator_models, app / 'models' / 'zh-en')
                dependency_versions = copy_translator_python(app / 'runtime' / 'python')
            for name in ['LICENSE', 'NOTICE.md']:
                shutil.copy2(SOURCE / component / name, app / name)
            (app / 'packaged.json').write_text(json.dumps({'name': 'Screen Translator', 'version': VERSIONS[component], 'offline': True}), encoding='utf-8')
            icon = SOURCE / component / 'frontend' / 'logo.ico'
            compile_launcher(csc, app / 'ScreenTranslator.exe', 'SCREEN_TRANSLATOR', icon)
            # A private import path prevents accidental imports from developer or installed app folders.
            (app / 'runtime' / 'python' / 'python312._pth').write_text('.\nLib\nDLLs\nLib\\site-packages\n..\\..\nimport site\n', encoding='ascii')
            for name in ['msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'concrt140.dll']:
                original = Path(os.environ['SystemRoot']) / 'System32' / name
                if original.exists() and not args.native_only:
                    shutil.copy2(original, app / 'runtime' / 'python' / name)
        shutil.copy2(SOURCE / component / 'requirements.txt', app / 'requirements.txt') if (SOURCE / component / 'requirements.txt').exists() else None
        (app / 'suite-component.json').write_text(json.dumps({'schema': 1, 'component': component}), encoding='utf-8')
        development_copy(app, component, {'pc-manager': 'PCManager.exe', 'file-search': 'AI Center.exe', 'screen-translator': 'ScreenTranslator.exe'}[component])
    if args.native_only and not args.seal:
        print('PASS: native deployment refreshed; existing release packages were preserved.', flush=True)
        return
    release_root = ROOT / 'packages'
    release_root.mkdir(exist_ok=True)
    highest = max([int(path.name.split('_')[0]) for path in release_root.iterdir() if path.is_dir() and path.name.split('_')[0].isdigit()] + [0])
    release = release_root / f'{highest+1:03d}_{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}'
    release.mkdir()
    manifest = {'schema': 1, 'version': RELEASE, 'created': datetime.now(timezone.utc).isoformat(), 'components': {}}
    # Reuse verified local tools; never install dependencies as part of the build.
    tool_payload = release / 'development-toolchain.zip'
    with zipfile.ZipFile(tool_payload, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for name, directory in [('sdk', sdk.parent), ('packages', packages), ('webview', webview)]:
            for path in sorted(path for path in directory.rglob('*') if path.is_file() and not {'.git', '__pycache__'}.intersection(path.parts)):
                archive.write(path, str(Path(name) / path.relative_to(directory)).replace('\\', '/'))
    manifest['developmentToolchain'] = {'payload': tool_payload.name, 'sha256': sha256(tool_payload)}
    for component, folder_name in folder_names.items():
        app = INSTALL / folder_name
        payload = release / (component + '.zip')
        files = sorted(path for path in app.rglob('*') if path.is_file() and not {'__pycache__', 'model_cache', 'results'}.intersection(path.parts) and path.suffix != '.pyc')
        print(f'PACKAGE: {component}, {len(files)} files', flush=True)
        hashes = {str(file.relative_to(app)).replace('\\', '/'): sha256(file) for file in files}
        with zipfile.ZipFile(payload, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for file in files:
                archive.write(file, str(file.relative_to(app)).replace('\\', '/'))
        manifest['components'][component] = {'folder': folder_name, 'payload': payload.name, 'sha256': sha256(payload), 'files': hashes, 'installedBytes': sum(file.stat().st_size for file in files)}
        with zipfile.ZipFile(release / (component + '-development.zip'), 'x', compression=zipfile.ZIP_DEFLATED) as archive:
            for path in public_source(app / 'Development'):
                archive.write(path, str(Path('Development') / path.relative_to(app / 'Development')).replace('\\', '/'))
    (release / 'packages.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    shutil.copy2(ROOT / 'README.md', release / 'README.md')
    source_files = [ROOT / 'README.md']
    for directory in [SOURCE, ROOT / 'tools', ROOT / 'checks']:
        source_files.extend(public_source(directory))
    with zipfile.ZipFile(release / 'xiaomi-revamp-source.zip', 'x', compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(source_files):
            archive.write(path, str(path.relative_to(ROOT)).replace('\\', '/'))
    for name, define in [('Xiaomi-Revamp-Setup.exe', 'SUITE_SETUP'), ('AI-Center-Setup.exe', 'SEARCH_SETUP'), ('Screen-Translator-Setup.exe', 'TRANSLATOR_SETUP'), ('Uninstall.exe', 'UNINSTALL')]:
        run([csc, '/nologo', '/target:winexe', '/platform:x64', '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
             '/reference:System.Web.Extensions.dll', '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll',
             f'/define:{define}', f'/win32manifest:{SOURCE / "shared" / "Setup.manifest"}', f'/out:{release / name}', SOURCE / 'shared' / 'Setup.cs'])
    (release / 'build-config.json').write_text(json.dumps({'sdk': str(sdk), 'privateRuntimes': True, 'translatorDependencies': dependency_versions, 'installMirror': str(INSTALL)}, indent=2), encoding='utf-8')
    print('PASS: independent applications and offline installers at', release, flush=True)


if __name__ == '__main__':
    main()
