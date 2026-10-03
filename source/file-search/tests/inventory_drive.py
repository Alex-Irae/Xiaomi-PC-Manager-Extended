"""Count a selected drive using the application's actual exclusion policy.
Dependencies: installed Python and xiaomi_search; no inference or index writes.
Outputs: configuration and aggregate inventory.json in a new --output folder.
Command: APP/runtime/python/python.exe tests/inventory_drive.py --root C:/
         --config LOCALAPPDATA/LocalAICenter/config.json --output results/NEW/inventory
"""
import argparse
from collections import Counter
import json
import logging
import os
from pathlib import Path
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import load
from xiaomi_search.extract import SUPPORTED
from xiaomi_search.indexer import Indexer


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True, help='Drive or folder to examine')
    parser.add_argument('--config', required=True, help='Existing application configuration')
    parser.add_argument('--output', required=True, type=Path, help='New evidence folder')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    config = load(args.config)
    config['roots'] = [str(Path(args.root).resolve())]
    (args.output/'config.json').write_text(json.dumps(config, indent=2), encoding='utf-8')
    indexer = Indexer(config, None, None, args.output/'data')
    root = Path(config['roots'][0])
    assert indexer.allowed(root), 'The selected drive must not be excluded by its own hidden/system attributes'
    counts = Counter()
    extensions = Counter()
    top = Counter()
    errors = Counter()
    start = last = time.perf_counter()
    def failed(error):
        errors[type(error).__name__] += 1
    for directory, children, names in os.walk(root, followlinks=False, onerror=failed):
        folder = Path(directory)
        if not indexer.allowed(folder):
            children[:] = []
            continue
        counts['folders'] += 1
        eligible_children = [name for name in children if indexer.allowed(folder/name)]
        counts['excluded_folders'] += len(children)-len(eligible_children)
        children[:] = eligible_children
        for name in names:
            path = folder/name
            if not indexer.allowed(path):
                counts['excluded_files'] += 1
                continue
            try:
                size = path.stat().st_size
            except OSError as error:
                failed(error)
                continue
            counts['files'] += 1
            counts['bytes'] += size
            extensions[path.suffix.lower() or '(none)'] += 1
            top[path.relative_to(root).parts[0]] += 1
            if path.suffix.lower() in SUPPORTED and size <= config['max_file_mb']*1024*1024:
                counts['content_candidates'] += 1
                counts['content_candidate_bytes'] += size
        now = time.perf_counter()
        if now-last >= 5:
            print(f"SCAN {counts['files']} eligible files, {counts['folders']} folders, {counts['content_candidates']} content candidates, {now-start:.1f}s", flush=True)
            last = now
    report = {'seed': 42, 'configuration': config, 'counts': dict(counts),
              'seconds': time.perf_counter()-start, 'errors': dict(errors),
              'extensions': dict(extensions.most_common()), 'top_level': dict(top.most_common()),
              'metadata_discovery_complete': True, 'semantic_index_built': False}
    (args.output/'inventory.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('DONE '+json.dumps({k: report[k] for k in ('counts','seconds','errors')}), flush=True)


if __name__ == '__main__':
    logging.basicConfig(level=logging.INFO)
    main()
