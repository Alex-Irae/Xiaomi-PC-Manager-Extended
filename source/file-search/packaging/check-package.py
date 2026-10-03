"""Validate a relocated installed package with its bundled Python runtime.
Dependencies: the offline package, Windows DPAPI and bundled OpenVINO CPU model.
Outputs: new fixture, protected index, logs and validation.json under --output.
Command: APP/runtime/python/python.exe packaging/check-package.py --root APP
         --output results/NEW_RUN/package-check
No production profile is modified; expected labels are not model inputs.
"""
import argparse
import hashlib
import json
import logging
from pathlib import Path
import subprocess
import sys
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True, type=Path, help='Relocated package root')
    parser.add_argument('--output', required=True, type=Path, help='New evidence directory')
    args = parser.parse_args()
    root = args.root.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
    logging.basicConfig(level=logging.INFO, format='%(asctime)s %(message)s')
    manifest = json.loads((root/'package-manifest.json').read_text(encoding='utf-8-sig'))
    for file in manifest['files']:
        with (root/file['path']).open('rb') as stream:
            assert hashlib.file_digest(stream, 'sha256').hexdigest().upper() == file['sha256']
    print(f"PASS: {len(manifest['files'])} relocated file checksums", flush=True)
    sys.path.insert(0, str(root))
    from xiaomi_search.config import load
    from xiaomi_search.service import Service
    import xiaomi_search
    assert Path(xiaomi_search.__file__).resolve().is_relative_to(root)
    corpus = args.output/'corpus'
    corpus.mkdir()
    (corpus/'night-garden.txt').write_text('A gardener waters flowers after sunset while the city sleeps.', encoding='utf-8')
    (corpus/'game-options.yaml').write_text('client:\n  language: en_US\n  server_region: Europe\n  crash_reports: false\n', encoding='utf-8')
    cfg = load(root/'config.example.json')
    cfg.update(roots=[str(corpus.resolve())], excluded_folders=[], model_path=str(root/'models/qwen3-embedding'),
               preferred_device='cpu', devices=['CPU'], indexing_frequency='manual', indexing_mode='normal')
    config = args.output/'config.json'
    config.write_text(json.dumps(cfg, indent=2), encoding='utf-8')
    data = args.output/'data'
    begin = time.perf_counter()
    service = Service(cfg, data, config)
    report = {'seed': 42, 'configuration': dict(cfg), 'python': sys.executable, 'source': xiaomi_search.__file__,
              'files_verified': len(manifest['files']), 'isolated': sys.flags.isolated}
    try:
        service.indexer.start(watch=False)
        service.indexer.request_scan()
        service.indexer.wait_complete()
        report['index_ms'] = (time.perf_counter()-begin)*1000
        report['counts'] = service.store.counts()
        cfg.update(name_enabled=False, content_enabled=False)
        reply = service.search({'text': 'Settings for the language and geographic game server', 'semantic': True})
        assert reply['results'][0]['name'] == 'game-options.yaml'
        assert reply['results'][0]['matches'] == ['Semantic']
        assert not reply['warnings'] and service.embedder.device == 'CPU'
        report['semantic'] = {'first': reply['results'][0]['name'], 'timing': reply['timing'], 'device': service.embedder.device}
    finally:
        service.close()
    assert (data/'index.sqlite3.dpapi').is_file() and not (data/'index.sqlite3').exists()
    print('PASS: bundled CPU inference, meaning retrieval and protected index', flush=True)
    # Hidden native checks exercise the wrapper, bundled .NET and private backend.
    native_cfg = {**cfg, 'semantic_enabled': False, 'name_enabled': True, 'content_enabled': True}
    config.write_text(json.dumps(native_cfg, indent=2), encoding='utf-8')
    for check in ('frame', 'lifecycle'):
        command = [str(root/'AI Center.exe'), '--check-'+check, '--config', str(config.resolve()),
                   '--data', str((args.output/('native-'+check)).resolve())]
        process = subprocess.run(command, timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
        assert process.returncode == 0
        evidence = json.loads((args.output/('native-'+check)/(check+'-check.json')).read_text(encoding='utf-8-sig'))
        assert evidence['passed']
        report[check] = evidence
        print('PASS: relocated native '+check, flush=True)
    report['passed'] = True
    with (args.output/'validation.json').open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
    return 0


if __name__ == '__main__':
    sys.exit(main())
