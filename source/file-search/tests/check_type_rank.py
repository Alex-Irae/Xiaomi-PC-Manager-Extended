"""Check that file-type weights order comparable matches and leave exact names first.
Dependencies: installed Python and project sources. Outputs: new synthetic fixture,
index and validation.json; no private documents are read or deleted.
Command: APP/runtime/python/python.exe tests/check_type_rank.py --output results/NEW/type-rank
"""
import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import load, validate
from xiaomi_search.service import Service


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True,help='New synthetic fixture folder')
    args=parser.parse_args()
    args.output=args.output.resolve()
    args.output.mkdir(parents=True,exist_ok=False)
    corpus=args.output/'corpus'
    corpus.mkdir()
    cfg=load('config.example.json')
    assert cfg['type_weights']['.pdf']>cfg['type_weights']['.json']>cfg['type_weights']['.mp3']>cfg['type_weights']['default']>cfg['type_weights']['.py']
    assert '.png' in cfg['excluded_extensions'] and '.mp3' not in cfg['excluded_extensions'] and 'Program Files' not in cfg['excluded_names']
    cfg.update(roots=[str(corpus.resolve())],excluded_folders=[],index_protection='none',semantic_enabled=False,indexing_frequency='manual')
    service=Service(cfg,args.output/'data',args.output/'config.json')
    try:
        names=['display settings.py','display settings.json','display settings.xyz','display settings.png','settings.py','display settings folder']
        # The text shares no word with the queries: a readable type must not win through its contents here.
        for name in names[:-1]:(corpus/name).write_text('unrelated',encoding='utf-8')
        (corpus/names[-1]).mkdir()
        for name in names:
            if service.indexer.allowed(corpus/name):service.indexer._file(corpus/name)
        order=lambda query:[row['name'] for row in service.store.search(query)['results']]
        found=order('display settings')
        assert 'display settings.png' not in found,found
        files=[name for name in found if name.startswith('display settings.')]
        assert files==['display settings.json','display settings.xyz','display settings.py'],found
        # An exact name is what was asked for, whatever its type.
        assert order('settings')[0]=='settings.py',order('settings')
        service.store.type_weights={'default':1,'.py':2}
        assert order('display settings')[0]=='display settings.py'
        service.store.type_weights={}
        assert set(order('display settings'))==set(found)
        # Source code is found by name only: its contents are never read. A code file indexed before the
        # rule loses its passages at the next visit, and compact() does the same for the whole index at once.
        for name in ('zebra notes.md','zebra tool.py','old report.md'):(corpus/name).write_text('the quagga is a striped animal',encoding='utf-8')
        cfg['name_only_extensions']=[]
        for name in ('zebra notes.md','zebra tool.py','old report.md'):service.indexer._file(corpus/name)
        assert {'zebra notes.md','zebra tool.py','old report.md'}<=set(order('quagga'))
        cfg['name_only_extensions']=['.py']
        service.store.mark_missing(corpus/'old report.md')
        passages=lambda:service.store.connect().__enter__().execute('SELECT count(*) FROM chunks').fetchone()[0]
        before=passages()
        assert service.store.compact(cfg['name_only_extensions'])=={'removed_files':1,'name_only_files':1}
        assert passages()==before-2 and order('quagga')==['zebra notes.md'] and 'zebra tool.py' in order('zebra')
        service.indexer._file(corpus/'old report.md')
        assert set(order('quagga'))=={'zebra notes.md','old report.md'},'a returning file is indexed afresh'
        (corpus/'late.py').write_text('the quagga again',encoding='utf-8');service.indexer._file(corpus/'late.py')
        assert 'late.py' not in order('quagga') and 'late.py' in order('late')
        # The Settings field saves the list; a type taken off it is read again at the file's next visit.
        service.dispatch('local_save_config',{'name_only_extensions':['JS']})
        assert service.config['name_only_extensions']==['.js'] and json.loads((args.output/'config.json').read_text(encoding='utf-8'))['name_only_extensions']==['.js']
        service.indexer._file(corpus/'late.py')
        assert 'late.py' in order('quagga')
        for wrong in ({'.pdf':'high'},{'.pdf':-1},{'p d f':1},[]):
            try:validate({**cfg,'type_weights':wrong},args.output/'config.json')
            except ValueError:continue
            raise AssertionError(f'accepted {wrong!r}')
        assert validate({**cfg,'type_weights':{'PDF':2,'default':1}},args.output/'config.json')['type_weights']=={'.pdf':2,'default':1}
        (args.output/'validation.json').write_text(json.dumps({'default_order':found,'images_left_out':True,'exact_name_first':True,'invalid_weights_rejected':True,'passed':True},indent=2),encoding='utf-8')
        print('PASS: preferred types first, code last, images left out, exact name first, weights validated')
    finally:service.close()


if __name__=='__main__':main()
