"""Evaluate real-file descriptions against the complete production index.
Dependencies: existing requirements.txt environment, completed local index/model.
Outputs: NEW JSON, CSV and SVG under --output, plus terminal progress.
Command: .venv/Scripts/python.exe tests/check_corpus_search.py --cases PATH.json
         --output results/NEW_RUN/full-index01
Expected basenames are evaluation labels only, never search inputs.
"""
import argparse
import csv
import html
import json
import logging
from pathlib import Path
import statistics
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import load
from xiaomi_search.service import Service


def main():
    sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
    logging.basicConfig(level=logging.INFO, format='%(asctime)s %(levelname)s %(message)s')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cases', required=True, type=Path, help='JSON descriptions and expected basenames')
    parser.add_argument('--output', required=True, type=Path, help='New output folder; existing folders are refused')
    parser.add_argument('--config', default='config.json', help='Application settings')
    parser.add_argument('--data', default='data', help='Completed application index')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    cfg = load(args.config)
    cases = json.loads(args.cases.read_text(encoding='utf-8-sig'))
    service = Service(cfg, args.data, args.config)
    report = {'seed': 42, 'configuration': dict(cfg), 'cases': [], 'summary': []}
    try:
        report['counts'] = service.store.counts()
        for mode, names, contents, semantic in [('lexical', True, True, False), ('hybrid', True, True, True), ('meaning', False, False, True)]:
            cfg.update(name_enabled=names, content_enabled=contents, semantic_enabled=semantic)
            for case in cases:
                reply = service.search({'text': case['query'], 'semantic': semantic})
                rows = reply['results']
                rank = next((i+1 for i, row in enumerate(rows) if row['name'] in case['expected']), None)
                record = {'mode': mode, **case, 'rank': rank, 'timing': reply['timing'], 'warnings': reply['warnings'],
                          'top5': [{key: row.get(key) for key in ('name', 'file_path', 'matches', 'location', 'score')} for row in rows[:5]]}
                report['cases'].append(record)
                print(f"RESULT {mode} rank={rank}, ms={reply['timing']['total_ms']}: {case['query']}", flush=True)
            measured = [row for row in report['cases'] if row['mode'] == mode]
            warm = measured[1:] if mode == 'hybrid' else measured
            report['summary'].append({'mode': mode, 'count': len(measured), 'top1': sum(row['rank'] == 1 for row in measured),
                                      'top5': sum(row['rank'] is not None and row['rank'] <= 5 for row in measured),
                                      'warm_median_ms': statistics.median(row['timing']['total_ms'] for row in warm)})
        report['device_actual'] = service.embedder.device
        report['success'] = not any(row['warnings'] for row in report['cases'])
    except Exception as error:
        logging.exception('Corpus evaluation failed')
        report.update(success=False, error=str(error))
    finally:
        service.close()
    with (args.output/'summary.json').open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2, ensure_ascii=False)
    with (args.output/'ranks.csv').open('x', encoding='utf-8', newline='') as stream:
        writer = csv.writer(stream)
        writer.writerow(['mode', 'query', 'rank', 'total_ms'])
        for row in report['cases']:
            writer.writerow([row['mode'], row['query'], row['rank'], row['timing']['total_ms']])
    svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="950" height="260"><rect width="100%" height="100%" fill="white"/><g font-family="Segoe UI" fill="#535861"><text x="24" y="30" font-size="20">Complete-index retrieval: intended file ranked first</text><text x="24" y="52" font-size="12">Same descriptions, all eligible Xiaomi Rebuild files. Higher is better; labels are not exhaustive relevance judgments.</text>']
    for i, row in enumerate(report['summary']):
        y = 80+i*48
        width = 450*row['top1']/max(1, row['count'])
        svg.append(f'<text x="24" y="{y+19}">{html.escape(row["mode"])}</text><rect x="180" y="{y}" width="{width}" height="28" rx="6" fill="#3482ff"/><text x="{195+width}" y="{y+19}">{row["top1"]}/{row["count"]}, top 5: {row["top5"]}/{row["count"]}</text>')
    svg.append('</g></svg>')
    with (args.output/'quality.svg').open('x', encoding='utf-8') as stream:
        stream.write(''.join(svg))
    print(json.dumps(report['summary'], indent=2), flush=True)
    return 0 if report.get('success') else 1


if __name__ == '__main__':
    sys.exit(main())
