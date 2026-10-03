"""Diagnose an isolated copy of the drive index with real local embeddings.
Dependencies: existing Python/OpenVINO model. Outputs: report.json and progress.log.
Command: .venv/Scripts/python.exe tests/diagnose_index.py --config PATH --data PATH --output NEW_PATH --scan-seconds 60
Original documents and the installed index are never modified. Full-drive
completion is reported separately from this bounded responsiveness check.
"""
import argparse
import json
import logging
import sys
import time
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import load
from xiaomi_search.service import Service


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, help='Isolated diagnostic configuration')
    parser.add_argument('--data', required=True, help='Isolated copied index directory')
    parser.add_argument('--output', required=True, type=Path, help='New diagnostic report directory')
    parser.add_argument('--scan-seconds', type=int, default=60, help='Seconds to observe an actual C-drive incremental scan')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    config = load(args.config)
    logging.basicConfig(level=logging.INFO, format='%(asctime)s %(message)s', handlers=[logging.StreamHandler(),logging.FileHandler(args.output/'progress.log',encoding='utf-8')])
    report = {'configuration':config.copy(), 'scan_seconds':args.scan_seconds, 'fixture':'existing research PDF plus C-drive scan', 'full_drive_complete':False}
    (args.output/'config.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    begin = time.perf_counter()
    service = Service(config, args.data, args.config)
    try:
        service.indexer.start(watch=False)
        report['startup_seconds']=time.perf_counter()-begin
        report['before']=service.store.counts()
        print('STARTUP',report['startup_seconds'],report['before'],flush=True)
        paper=Path(r'C:\Users\Irae\Documents\Documents\Xiaomi Rebuild\clickntranslate-1.8.1\clickntranslate-1.8.1\icons\DATA GENERATION WITHOUT FUNCTION ESTIMATION.pdf')
        assert service.indexer.allowed(paper)
        service.indexer.set_mode('normal')
        service.indexer._file(paper)
        row=service.store.get_file(path=paper)
        for batch in range(3):
            service.indexer._vectors(row['id'])
            print('PDF batch',batch+1,service.store.counts()['vectors'],'passages',flush=True)
            assert not service.indexer.semantic_error,service.indexer.semantic_error
        service.indexer.vector_jobs.discard(row['id'])
        report['queries']=[]
        for text in ('EFS','estimation-free','research about synthesizing data without fitting a function'):
            for repeat in range(2):
                result=service.search({'text':text})
                first=result['results'][0] if result['results'] else {}
                item={'text':text,'repeat':repeat,'first':first.get('name'),'matches':first.get('matches'),'timing':result['timing'],'warnings':result['warnings']}
                report['queries'].append(item)
                print('QUERY',json.dumps(item),flush=True)
                assert first.get('name')==paper.name, item
                assert 'Semantic' in first['matches'], item
        service.dispatch('local_save_config',{'name_enabled':False,'content_enabled':False})
        result=service.search({'text':'research about synthesizing data without fitting a function'})
        assert result['results'][0]['name']==paper.name
        assert result['results'][0]['matches']==['Semantic']
        report['semantic_only_paraphrase']=True
        service.dispatch('local_save_config',{'name_enabled':True,'content_enabled':True})
        report['before_drive_scan']=service.store.counts()
        begin=time.perf_counter()
        ack=service.dispatch('local_index_now',{'force':True,'wait':False})
        report['index_ack_ms']=round((time.perf_counter()-begin)*1000,2)
        assert ack['accepted']
        deadline=time.monotonic()+args.scan_seconds
        report['samples']=[]
        while time.monotonic()<deadline and service.maintenance_active:
            time.sleep(5)
            begin=time.perf_counter()
            service.dispatch('local_save_config',{'theme':'dark'})
            save_ms=(time.perf_counter()-begin)*1000
            sample={'counts':service.store.counts(),'activity':service.activity(),'theme_save_ms':round(save_ms,2)}
            report['samples'].append(sample)
            print('PROGRESS',json.dumps(sample),flush=True)
        report['full_drive_complete']=not service.maintenance_active and not service.indexer.semantic_error
        service.indexer.set_mode('paused')
        report['after']=service.store.counts()
        report['semantic_error']=service.indexer.semantic_error
        report['passed']=not service.indexer.semantic_error and report['after']['vectors']>report['before_drive_scan']['vectors'] and all(item['theme_save_ms']<1000 for item in report['samples'])
    finally:
        service.close()
        (args.output/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print('DONE',report['passed'],'Full drive complete:',report['full_drive_complete'],flush=True)
    if not report['passed']:
        raise SystemExit(1)


if __name__=='__main__':
    main()
