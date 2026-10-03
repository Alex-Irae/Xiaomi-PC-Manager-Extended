"""Check remembered file choices respect scope, exclusions, filters and caching.
Dependencies: existing Python environment. Outputs: a new fixture and report.
Command: .venv/Scripts/python.exe tests/check_personal_ranking.py --output results/NEW_RUN/ranking
No source documents are modified and no external application is launched.
"""
import argparse
import json
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import load
from xiaomi_search.service import Service


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',required=True,type=Path,help='New fixture and evidence directory')
    root=parser.parse_args().output.resolve();root.mkdir(parents=True,exist_ok=False)
    corpus=root/'corpus';corpus.mkdir();private=corpus/'private';private.mkdir()
    exact=corpus/'efs.txt';a=corpus/'chosen.md';b=corpus/'other.md';secret=private/'secret.md';outside=root/'outside.md'
    for file in [exact,a,b,secret,outside]:file.write_text('EFS research and local file retrieval.',encoding='utf-8')
    config=load('config.json');config.update(roots=[str(corpus)],excluded_folders=[],semantic_enabled=False,index_protection='none',indexing_frequency='manual')
    path=root/'config.json';path.write_text(json.dumps(config,indent=2),encoding='utf-8')
    service=Service(config,root/'data',path)
    checks=[]
    try:
        for file in [exact,a,b,secret]:service.indexer._file(file)
        query=lambda paths,text='efs':service.search({'text':text,'semantic':False,'preferred_paths':[str(p) for p in paths]})
        assert query([])['results'][0]['name']=='efs.txt'
        result=query([a]);assert result['results'][0]['name']=='chosen.md' and 'Previously opened' in result['results'][0]['matches']
        assert query([a])['timing']['cache_hit']
        assert query([b])['results'][0]['name']=='other.md'
        checks.append('query-specific preference overrides normal ranking; changed preference invalidates cached ordering')
        service.dispatch('local_save_config',{'excluded_folders':[str(private)],'excluded_extensions':['.ini','.dll','.md']})
        assert query([a,secret,outside])['results'][0]['name']=='efs.txt'
        assert all(row['name'] not in ('chosen.md','secret.md','outside.md') for row in query([a,secret,outside])['results'])
        checks.append('excluded folders/types and out-of-scope files cannot be restored by preferences')
        service.dispatch('local_save_config',{'excluded_extensions':['.ini','.dll']})
        assert all(row['file_path'].endswith('.txt') for row in query([a],'efs ext:txt')['results'])
        missing=root/'missing.md';assert query([missing])['results'][0]['name']=='efs.txt'
        checks.append('query filters and missing-file checks constrain preferences')
        actions=[];service.window_action=lambda action,params:actions.append((action,params)) or {'opened':True}
        row=service.store.get_file(path=a)
        result=service.dispatch('open_file_with',{'file_id':row['id'],'query':'efs'})
        assert result['opened'] and actions[-1]==('open_with',{'path':str(a),'query':'efs'})
        try:service.dispatch('open_file_with',{'file_path':str(outside),'query':'efs'})
        except ValueError:pass
        else:raise AssertionError('Open with escaped indexed scope')
        try:service.dispatch('open_file',{'file_id':row['id'],'file_path':str(b),'query':'efs'})
        except ValueError:pass
        else:raise AssertionError('Stale file ID/path mismatch opened another file')
        checks.append('Open with uses the validated indexed path and forwards its query')
    finally:service.close()
    (root/'report.json').write_text(json.dumps({'passed':True,'configuration':config,'checks':checks},indent=2),encoding='utf-8')
    for check in checks:print('PASS: '+check,flush=True)


if __name__=='__main__':main()
