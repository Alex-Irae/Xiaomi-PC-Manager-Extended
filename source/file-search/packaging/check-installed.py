"""Check installed files and retrieval against a migrated private profile.
Dependencies: installed bundled Python, OpenVINO and Windows account encryption.
Outputs: a new --output/validation.json and terminal progress; no indexing runs.
Command: APP/runtime/python/python.exe packaging/check-installed.py --root APP
         --profile LOCALAPPDATA/LocalAICenter --output results/NEW_RUN/installed
"""
import argparse
import hashlib
import json
import logging
from pathlib import Path
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True, help='Installed application folder')
    parser.add_argument('--profile', type=Path, required=True, help='Migrated private profile')
    parser.add_argument('--output', type=Path, required=True, help='New evidence folder')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
    logging.basicConfig(level=logging.INFO, format='%(asctime)s %(message)s')
    root = args.root.resolve()
    manifest = json.loads((root/'package-manifest.json').read_text(encoding='utf-8-sig'))
    for item in manifest['files']:
        with (root/item['path']).open('rb') as stream:
            assert hashlib.file_digest(stream, 'sha256').hexdigest().upper() == item['sha256'], item['path']
    print(f"PASS: {len(manifest['files'])} installed file checksums", flush=True)
    from xiaomi_search.config import load
    from xiaomi_search.service import Service
    import xiaomi_search
    assert Path(xiaomi_search.__file__).resolve().is_relative_to(root)
    config_path = args.profile/'config.json'
    cfg = load(config_path)
    service = Service(cfg, args.profile/'data', config_path)
    report = {'seed': 42, 'configuration': cfg, 'python': sys.executable,
              'source': xiaomi_search.__file__, 'files_verified': len(manifest['files']),
              'installation': json.loads((root/'install-report.json').read_text(encoding='utf-8-sig')),
              'cases': []}
    try:
        report['counts'] = service.store.counts()
        identity = service.embedder.identity()
        with service.store.connect() as db:
            report['matching_vectors'] = db.execute("SELECT count(*) FROM chunks c JOIN files f ON f.id=c.file_id WHERE f.active=1 AND f.status='indexed' AND c.vector IS NOT NULL AND c.model_id=?", (identity,)).fetchone()[0]
        assert report['matching_vectors'] == report['counts']['vectors'] > 0
        print(f"PASS: retained {report['matching_vectors']} compatible embeddings", flush=True)
        expected = 'DATA GENERATION WITHOUT FUNCTION ESTIMATION.pdf'
        for query in ('EFS', 'estimation-free'):
            for repeat in range(2):
                reply = service.search({'text': query})
                first = reply['results'][0]
                assert first['name'] == expected and 'Semantic' in first['matches']
                assert not reply['warnings'] and reply['timing']['cache_hit'] == bool(repeat)
                report['cases'].append({'query': query, 'repeat': repeat, 'first_name': first['name'],
                    'first_path': first['file_path'], 'matches': first['matches'], 'timing': reply['timing'], 'warnings': reply['warnings']})
                print(f"PASS: {query}, repeat={repeat}, {reply['timing']['total_ms']} ms", flush=True)
        report['device'] = service.embedder.device
    finally:
        service.close()
    assert (args.profile/'data/index.sqlite3.dpapi').is_file()
    assert not (args.profile/'data/index.sqlite3').exists()
    report['passed'] = True
    with (args.output/'validation.json').open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
    print('PASS: installed retrieval, cache and protected profile', flush=True)


if __name__ == '__main__':
    main()
