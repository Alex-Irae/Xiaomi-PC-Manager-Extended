"""Compare CPU, GPU and NPU retrieval against the complete protected index.

Dependencies: existing requirements.txt environment, local model and completed index.
Outputs: new per-device JSON/logs, CSV and SVG under --output; source files untouched.
Command: .venv/Scripts/python.exe tests/benchmark_devices.py --output results/NEW_RUN
Model setup uses existing device compilation caches. No fallback is allowed.
"""
import argparse
import csv
import html
import json
import logging
import os
from pathlib import Path
import statistics
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import load
from xiaomi_search.service import Service


def worker(args):
    settings = load(args.config)
    settings.update(preferred_device='auto', devices=[args.device], indexing_frequency='manual', model_standby='keep_loaded')
    report = {'configuration': settings, 'seed': 42, 'repeats': args.repeats, 'device_requested': args.device,
              'protocol': 'isolated sequential processes, full persisted GPU document vectors, uncached hybrid then cached repeat', 'cases': []}
    print(f'START {args.device} pid={os.getpid()}', flush=True)
    service = None
    try:
        start = time.perf_counter()
        service = Service(settings, args.data, args.config)
        report['protected_index_load_ms'] = (time.perf_counter()-start)*1000
        identity = service.embedder.identity()
        with service.store.connect() as db:
            total, ready = db.execute("SELECT COUNT(*), SUM(c.vector IS NOT NULL AND c.model_id=?) FROM chunks c JOIN files f ON f.id=c.file_id WHERE f.active=1 AND f.status='indexed'", (identity,)).fetchone()
        report.update(counts=service.store.counts(), model_id=identity, coverage={'passages':total,'matching_vectors':ready})
        if not total or ready != total:
            raise AssertionError(f'Incomplete vectors: {ready}/{total}')
        report['compilation_cache_files_before'] = sum(1 for p in (Path(args.data)/'model_cache'/args.device).rglob('*') if p.is_file())
        start=time.perf_counter();service.embedder._load()
        report['model_setup_ms']=(time.perf_counter()-start)*1000
        report['device_actual']=service.embedder.device
        if service.embedder.device != args.device:
            raise AssertionError('Device fallback is forbidden')
        print(f"READY {args.device}: index={report['protected_index_load_ms']:.0f} ms, model={report['model_setup_ms']:.0f} ms, passages={total}", flush=True)
        for query in ['EFS','estimation-free','generate samples without estimating a score function']:
            for repeat in range(args.repeats):
                service._cache.clear()
                start=time.process_time();response=service.search({'text':query})
                processor_seconds=time.process_time()-start
                if response['warnings'] or service.embedder.device != args.device:
                    raise AssertionError(str(response['warnings']))
                cached=service.search({'text':query})
                rank=next((i+1 for i,r in enumerate(response['results']) if r['name']=='DATA GENERATION WITHOUT FUNCTION ESTIMATION.pdf'), None)
                record={'query':query,'repeat':repeat,'hybrid_ms':response['timing']['total_ms'],'cached_ms':cached['timing']['total_ms'],'processor_seconds':processor_seconds,'pdf_rank':rank,'cache_hit':cached['timing'].get('cache_hit'), 'timing':response['timing']}
                report['cases'].append(record)
                print(f"QUERY {args.device} {repeat+1}/{args.repeats} {query}: {record['hybrid_ms']:.1f} ms, cache {record['cached_ms']:.1f} ms, PDF rank {rank}", flush=True)
        report['passed']=all(r['pdf_rank'] is not None and r['cache_hit'] for r in report['cases'])
        report['warm_hybrid_median_ms']=statistics.median(r['hybrid_ms'] for r in report['cases'])
        report['cached_median_ms']=statistics.median(r['cached_ms'] for r in report['cases'])
    except Exception as error:
        logging.exception('Device benchmark failed');report.update(passed=False,error=str(error))
    finally:
        if service:service.close()
    with (args.output/(args.device+'.json')).open('x',encoding='utf-8') as stream:
        json.dump(report,stream,indent=2,ensure_ascii=False)
    return 0 if report.get('passed') else 1


def main():
    sys.stdout.reconfigure(encoding='utf-8',errors='backslashreplace')
    os.environ.update(HF_HUB_OFFLINE='1',TRANSFORMERS_OFFLINE='1',HF_HUB_DISABLE_TELEMETRY='1')
    logging.basicConfig(level=logging.INFO,format='%(asctime)s %(levelname)s %(message)s')
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True,help='New output directory, refused if it exists')
    parser.add_argument('--config',default='config.json',help='Local configuration path')
    parser.add_argument('--data',default='data',help='Complete application index directory')
    parser.add_argument('--repeats',type=int,default=3,help='Uncached repetitions of each query per device')
    parser.add_argument('--timeout',type=int,default=300,help='Maximum seconds per isolated device worker')
    parser.add_argument('--device',choices=['CPU','GPU','NPU'],help=argparse.SUPPRESS)
    args=parser.parse_args()
    if args.device:return worker(args)
    args.output.mkdir(parents=True,exist_ok=False)
    with (args.output/'config.json').open('x',encoding='utf-8') as stream:
        json.dump({'settings':load(args.config),'seed':42,'repeats':args.repeats,'timeout_seconds':args.timeout,'devices':['CPU','GPU','NPU'],'data_path':str(Path(args.data).resolve())},stream,indent=2)
    reports=[]
    for device in ['CPU','GPU','NPU']:
        print('BENCHMARK '+device,flush=True)
        command=[sys.executable,str(Path(__file__).resolve()),'--device',device,'--output',str(args.output),'--config',args.config,'--data',args.data,'--repeats',str(args.repeats)]
        with (args.output/(device+'.log')).open('x',encoding='utf-8') as log:
            process=subprocess.Popen(command,stdout=log,stderr=subprocess.STDOUT)
            try:process.wait(timeout=args.timeout)
            except subprocess.TimeoutExpired:
                subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],capture_output=True)
                process.wait();print('TIMEOUT '+device,flush=True)
        file=args.output/(device+'.json')
        report=json.loads(file.read_text(encoding='utf-8')) if file.exists() else {'passed':False,'device_requested':device,'error':'worker timed out or exited without evidence'}
        reports.append(report)
        print(json.dumps({key:report.get(key) for key in ['device_requested','passed','model_setup_ms','protected_index_load_ms','warm_hybrid_median_ms','cached_median_ms','error']}),flush=True)
    with (args.output/'summary.json').open('x',encoding='utf-8') as stream:json.dump(reports,stream,indent=2,ensure_ascii=False)
    with (args.output/'latency.csv').open('x',newline='',encoding='utf-8') as stream:
        writer=csv.writer(stream);writer.writerow(['device','query','repeat','hybrid_ms','cached_ms','pdf_rank','processor_seconds'])
        for r in reports:
            for c in r.get('cases',[]):writer.writerow([r['device_requested'],c['query'],c['repeat'],c['hybrid_ms'],c['cached_ms'],c['pdf_rank'],c['processor_seconds']])
    maximum=max((r.get('warm_hybrid_median_ms',0) for r in reports),default=1) or 1
    svg=['<svg xmlns="http://www.w3.org/2000/svg" width="850" height="240"><rect width="100%" height="100%" fill="white"/><g font-family="Segoe UI" fill="#535861"><text x="24" y="30" font-size="20">Full-index warm hybrid retrieval</text><text x="24" y="52" font-size="12">Median milliseconds, lower is faster. Setup/decryption and UI debounce excluded.</text>']
    for i,r in enumerate(reports):
        value=r.get('warm_hybrid_median_ms',0);y=80+i*47
        svg.append(f'<text x="24" y="{y+19}">{html.escape(r["device_requested"])}</text><rect x="95" y="{y}" width="{value/maximum*570:.1f}" height="28" rx="6" fill="#3482ff"/><text x="{110+value/maximum*570:.1f}" y="{y+19}">{value:.1f} ms</text>')
    svg.append('</g></svg>')
    with (args.output/'latency.svg').open('x',encoding='utf-8') as stream:stream.write(''.join(svg))
    return 0 if all(r.get('passed') for r in reports) else 1


if __name__=='__main__':sys.exit(main())
