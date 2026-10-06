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
        for wrong in ({'.pdf':'high'},{'.pdf':-1},{'p d f':1},[]):
            try:validate({**cfg,'type_weights':wrong},args.output/'config.json')
            except ValueError:continue
            raise AssertionError(f'accepted {wrong!r}')
        assert validate({**cfg,'type_weights':{'PDF':2,'default':1}},args.output/'config.json')['type_weights']=={'.pdf':2,'default':1}
        (args.output/'validation.json').write_text(json.dumps({'default_order':found,'images_left_out':True,'exact_name_first':True,'invalid_weights_rejected':True,'passed':True},indent=2),encoding='utf-8')
        print('PASS: preferred types first, code last, images left out, exact name first, weights validated')
    finally:service.close()


if __name__=='__main__':main()
