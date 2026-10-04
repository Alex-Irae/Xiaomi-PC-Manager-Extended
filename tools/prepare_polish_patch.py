"""Prepare hash-verified replacements for the installed apps and offline packages.

Dependencies: existing Python stdlib and completed native builds. Outputs: new
changes.json and patch-plan.json. Command: private-python -B tools/prepare_polish_patch.py
--previous PREVIOUS_RELEASE --output NEW_PLAN_DIRECTORY.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import zipfile
sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_suite import public_source

ROOT = Path(__file__).resolve().parents[1]
INSTALLED = Path('C:/Program Files/Xiaomi Revamp')

def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--previous', required=True, type=Path, help='Verified previous packages')
    parser.add_argument('--output', required=True, type=Path, help='New plan directory')
    parser.add_argument('--installed-baseline', type=Path, help='Ownership manifest after earlier verified patches in this working session')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    manifest = json.loads((args.previous/'packages.json').read_text(encoding='utf-8-sig'))
    installed = json.loads(args.installed_baseline.read_text(encoding='utf-8-sig')) if args.installed_baseline else None
    changes, plan = {}, []
    for component, item in manifest['components'].items():
        folder = item['folder']
        archive = zipfile.ZipFile(args.previous/item['payload'])
        replacements = {}
        def add(relative, path):
            if item['files'].get(relative) != digest(path):
                if relative in item['files'] and path.suffix.lower() in {'.cs','.py','.js','.css','.json','.html','.svg','.cmd','.ps1','.md','.manifest','.txt'}:
                    if archive.read(relative).replace(b'\r\n',b'\n') == path.read_bytes().replace(b'\r\n',b'\n'):
                        return
                replacements[relative] = str(path.resolve())
        for scope in (component, 'shared'):
            for path in public_source(ROOT/'source'/scope):
                add('Development/source/'+scope+'/'+path.relative_to(ROOT/'source'/scope).as_posix(), path)
        build = ROOT/'build/polish-current'/folder
        for path in public_source(build):
            relative = path.relative_to(build).as_posix()
            runtime = relative if component == 'screen-translator' else 'runtime/dotnet/'+relative
            if runtime in item['files']:
                add(runtime, path)
        executable = {'pc-manager':'PCManager.exe', 'file-search':'AI Center.exe', 'screen-translator':'ScreenTranslator.exe'}[component]
        add(executable, ROOT/'build/polish-current/launchers'/executable)
        scopes = ['www'] if component == 'pc-manager' else ['xiaomi_search','frontend'] if component == 'file-search' else ['screen_translator','frontend']
        for scope in scopes:
            for path in public_source(ROOT/'source'/component/scope):
                relative = scope+'/'+path.relative_to(ROOT/'source'/component/scope).as_posix()
                if component == 'pc-manager': relative = 'runtime/dotnet/'+relative
                add(relative, path)
        if component == 'file-search': add('config.example.json', ROOT/'source/file-search/config.example.json')
        changes[component] = replacements
        archive.close()
        for relative, source in replacements.items():
            target = INSTALLED/folder/relative
            actual = digest(target) if target.exists() else None
            expected = installed[component]['files'].get(relative) if installed else item['files'].get(relative)
            new_hash = digest(Path(source))
            if actual not in (expected,new_hash):
                raise ValueError('Installed file changed outside the previous release: '+str(target))
            if actual != new_hash:
                plan.append({'component':component,'relative':relative,'source':source,'destination':str(target), 'previousHash':actual,'newHash':new_hash,'owned':True})
            if not relative.startswith('Development/'):
                for devrelative in ('Development/App/'+relative, 'Development/output/'+relative.removeprefix('runtime/dotnet/')):
                    devtarget = INSTALLED/folder/devrelative
                    # Output only contains the compiled native build, not Python or wrappers.
                    if devrelative.startswith('Development/output/') and relative not in [p.relative_to(build).as_posix() if component=='screen-translator' else 'runtime/dotnet/'+p.relative_to(build).as_posix() for p in public_source(build)]:
                        continue
                    if devtarget.exists() or (devrelative.startswith('Development/App/') and (INSTALLED/folder/'Development/App').is_dir()):
                        dev_hash = digest(devtarget) if devtarget.exists() else None
                        if dev_hash != new_hash:
                            plan.append({'component':component,'relative':devrelative,'source':source,'destination':str(devtarget),'previousHash':dev_hash,'newHash':new_hash,'owned':False})
    (args.output/'changes.json').write_text(json.dumps(changes, indent=2), encoding='utf-8')
    (args.output/'patch-plan.json').write_text(json.dumps(plan, indent=2), encoding='utf-8')
    print(json.dumps({'package_replacements':{k:len(v) for k,v in changes.items()},'installed_replacements':len(plan)}), flush=True)

if __name__ == '__main__': main()
