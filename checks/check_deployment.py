"""Verify the sealed payloads and private Python module/dependency resolution.

Dependencies: Python stdlib, copied component runtimes. Outputs: numbered deployment-check evidence.
Command: python checks/check_deployment.py --release packages/NNN_UTC
No model inference, desktop capture, installation, package download or Git operation occurs.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release', type=Path, required=True, help='Sealed release directory to validate against install/')
    args = parser.parse_args()
    release = args.release.resolve()
    manifest = json.loads((release / 'packages.json').read_text(encoding='utf-8'))
    runs = ROOT / 'results'
    highest = max([int(path.name.split('_')[0]) for path in runs.iterdir()
                   if path.is_dir() and path.name.split('_')[0].isdigit()] + [0])
    output = runs / f'{highest+1:03d}_{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}_seed0'
    output.mkdir()
    (output / 'config.json').write_text(json.dumps({'seed': 0, 'mode': 'deployment-integrity',
        'release': str(release), 'mirror': str(ROOT / 'install'), 'modelsInitialized': False,
        'desktopCaptured': False, 'archiveHashAlgorithm': 'sha256', 'verifyEveryFile': True}, indent=2), encoding='utf-8')
    checked = {}
    for component, item in manifest['components'].items():
        payload = release / item['payload']
        assert digest(payload) == item['sha256'], f'Archive hash failed: {component}'
        with zipfile.ZipFile(payload) as archive:
            assert len(archive.namelist()) == len(item['files'])
            assert set(archive.namelist()) == set(item['files'])
            for name, expected in item['files'].items():
                relative = Path(name)
                assert not relative.is_absolute() and '..' not in relative.parts
                assert not {'__pycache__', 'model_cache', 'results'}.intersection(relative.parts)
                with archive.open(name) as stream:
                    assert hashlib.file_digest(stream, 'sha256').hexdigest() == expected, name
                assert digest(ROOT / 'install' / item['folder'] / relative) == expected, name
        checked[component] = len(item['files'])
        print(f'PASS: {component}, {checked[component]} sealed files match the manifest and install mirror', flush=True)
    imports = {}
    environment = os.environ.copy()
    environment['PYTHONDONTWRITEBYTECODE'] = '1'
    for folder, package, libraries in [
        ('AI Center', 'xiaomi_search.store', ['openvino_genai']),
        ('Screen Translator', 'screen_translator.backend', ['openvino', 'numpy', 'cv2']),
    ]:
        app = ROOT / 'install' / folder
        python = app / 'runtime' / 'python' / 'python.exe'
        code = ('import importlib,json,pathlib,sys; '
                f'module=importlib.import_module({package!r}); '
                f'assert pathlib.Path(module.__file__).resolve().is_relative_to(pathlib.Path({str(app)!r})); '
                f'libraries={libraries!r}; '
                'print(json.dumps({"executable":sys.executable,"module":module.__file__,"versions":'
                '{name:getattr(importlib.import_module(name),"__version__","unknown") for name in libraries}}))')
        completed = subprocess.run([str(python), '-c', code], cwd=ROOT, env=environment,
                                   check=True, capture_output=True, text=True, timeout=45)
        imports[folder] = json.loads(completed.stdout)
        print(f'PASS: {folder} imports its own modules and native dependencies through its private runtime', flush=True)
    summary = {'passed': True, 'release': str(release), 'sealedFiles': checked,
               'privateImports': imports, 'installerExecuted': False, 'modelsInitialized': False}
    (output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print('PASS: deployment evidence at', output, flush=True)


if __name__ == '__main__':
    main()
