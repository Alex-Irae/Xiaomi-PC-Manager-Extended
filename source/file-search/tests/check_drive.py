"""Try drive-wide encrypted metadata discovery and a bounded semantic sample.
Dependencies: installed Python/OpenVINO and the existing account-protected index.
Outputs: new --output/config.json, protected data, logs, aggregate summary and SVG.
Command: APP/runtime/python/python.exe tests/check_drive.py --config PROFILE/config.json
         --seed-index PROFILE/data/index.sqlite3.dpapi --output results/NEW/drive
All eligible names are discovered; new semantic coverage is limited by --samples.
Original files and the installed private profile are not modified.
"""
import argparse
import csv
import json
import logging
import random
from pathlib import Path
import shutil
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import load
from xiaomi_search.service import Service


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, help='Installed configuration')
    parser.add_argument('--seed-index', type=Path, required=True, help='Existing encrypted snapshot to preserve embeddings')
    parser.add_argument('--output', type=Path, required=True, help='New result folder')
    parser.add_argument('--samples', type=int, default=24, help='Maximum new small files to extract and embed')
    parser.add_argument('--reuse-discovery', action='store_true', help='Seed is already the completed C-drive metadata snapshot; do not walk the drive again')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    data = args.output/'data'
    data.mkdir()
    shutil.copy2(args.seed_index, data/'index.sqlite3.dpapi')
    cfg = load(args.config)
    cfg.update(roots=['C:\\'], indexing_frequency='manual', indexing_mode='normal')
    config = args.output/'config.json'
    config.write_text(json.dumps(cfg, indent=2), encoding='utf-8')
    logging.basicConfig(level=logging.INFO, format='%(asctime)s %(message)s',
                        handlers=[logging.StreamHandler(), logging.FileHandler(args.output/'progress.log', encoding='utf-8')])
    service = Service(cfg, data, config)
    report = {'seed': 42, 'configuration': cfg, 'sampling': 'Seeded random small pending text/Markdown/YAML/PDF files; this is a smoke check, not full semantic coverage', 'samples_limit': args.samples}
    try:
        assert service.indexer.allowed(Path('C:/'))
        assert not service.indexer.allowed(Path('C:/$Recycle.Bin'))
        with service.store.connect() as db:
            report['identity_query_plan'] = [tuple(r) for r in db.execute('EXPLAIN QUERY PLAN SELECT * FROM files WHERE file_key=? AND file_type=?', ('probe',64))]
        assert any('files_identity' in str(row) for row in report['identity_query_plan'])
        report['before'] = service.store.counts()
        start = time.perf_counter()
        if not args.reuse_discovery:
            service.indexer.running = True
            print('DRIVE: discovering all eligible C: names with existing exclusions', flush=True)
            service.indexer._scan(Path('C:/'))
            service.indexer.running = False
            service.store.flush()
        report['discovery_seconds'] = None if args.reuse_discovery else time.perf_counter()-start
        report['reused_discovery'] = args.reuse_discovery
        report['seed_index'] = str(args.seed_index.resolve())
        report['metadata'] = service.store.counts()
        report['scan_warning'] = service.indexer.scan_error
        print('DRIVE: metadata complete '+json.dumps(report['metadata']), flush=True)
        with service.store.connect() as db:
            candidates = [dict(r) for r in db.execute("SELECT id,path FROM files WHERE active=1 AND status='pending' AND size BETWEEN 1 AND 65536 AND extension IN ('.md','.txt','.yaml','.yml','.pdf')")]
        random.Random(42).shuffle(candidates)
        start = time.perf_counter()
        service.indexer.running = True  # The normal worker requires this during embedding.
        for n, row in enumerate(candidates[:args.samples], 1):
            service.indexer._file(Path(row['path']))
            service.indexer._vectors(row['id'])
            print(f'DRIVE: sample {n}/{min(args.samples,len(candidates))}', flush=True)
        service.indexer.running = False
        report['sample_seconds'] = time.perf_counter()-start
        report['samples_attempted'] = min(args.samples,len(candidates))
        report['semantic_error'] = service.indexer.semantic_error
        report['counts'] = service.store.counts()
        report['queries'] = []
        for query in ('EFS','estimation-free','readme'):
            for repeat in range(2):
                reply = service.search({'text': query})
                first = reply['results'][0] if reply['results'] else {}
                report['queries'].append({'query': query, 'repeat': repeat, 'name': first.get('name'), 'matches': first.get('matches'), 'timing': reply['timing'], 'warnings': reply['warnings']})
                assert first and not reply['warnings']
                if query != 'readme':
                    assert first['name'] == 'DATA GENERATION WITHOUT FUNCTION ESTIMATION.pdf' and 'Semantic' in first['matches']
                assert reply['timing']['cache_hit'] == bool(repeat)
                print(f"DRIVE: {query}, repeat={repeat}, {reply['timing']['total_ms']} ms", flush=True)
        report['device'] = service.embedder.device
        report['full_metadata_complete'] = True
        report['full_content_semantic_complete'] = report['counts'].get('pending',0) == 0
        report['passed'] = not report['semantic_error']
    finally:
        service.indexer.running = False
        service.close()
    with (args.output/'summary.json').open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
    with (args.output/'timings.csv').open('x', newline='', encoding='utf-8') as stream:
        writer = csv.writer(stream)
        writer.writerow(['query','repeat','milliseconds','cache_hit'])
        for row in report['queries']:
            writer.writerow([row['query'],row['repeat'],row['timing']['total_ms'],row['timing']['cache_hit']])
    rows = report['queries']
    maximum = max(row['timing']['total_ms'] for row in rows) or 1
    svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="760" height="390"><rect width="100%" height="100%" fill="#181b20"/><g font-family="Segoe UI" fill="#bec6d0"><text x="24" y="30" font-size="18">C: drive test: hybrid retrieval latency</text><text x="24" y="54" font-size="12">All eligible names; existing embeddings plus bounded new sample. Lower is faster.</text>']
    for n,row in enumerate(rows):
        y = 80+n*42
        label = row['query']+(' cached' if row['repeat'] else '')
        value = row['timing']['total_ms']
        # Scale each measured latency to the widest bar, preserving milliseconds.
        width = 440*value/maximum
        svg.append(f'<text x="24" y="{y+18}" font-size="12">{label}</text><rect x="180" y="{y}" width="{width:.2f}" height="26" rx="5" fill="#447fff"/><text x="{186+width:.2f}" y="{y+18}" font-size="12">{value:.1f} ms</text>')
    svg.append('<text x="24" y="370" font-size="12">Model loaded during sampling; query setup may remain. Not full-drive semantic recall.</text></g></svg>')
    (args.output/'latency.svg').write_text(''.join(svg), encoding='utf-8')
    print('DONE: drive discovery and bounded retrieval check', flush=True)


if __name__ == '__main__':
    main()
