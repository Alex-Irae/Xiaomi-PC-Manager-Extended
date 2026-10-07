"""Check that incidental name substrings do not hide stronger content/meaning.
Dependencies: installed Python/NumPy and project sources. Outputs: new synthetic
fixture, index and validation.json; no private documents are read or deleted.
Command: APP/runtime/python/python.exe tests/check_filename_rank.py --output results/NEW/ranking
"""
import argparse
import json
from pathlib import Path
import sys
import numpy as np

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import load
from xiaomi_search.service import Service
from xiaomi_search.store import COARSE_DIMENSION


def unit(*leading):
    """Synthetic vector as long as the index's fast-scan prefix; the scan skips shorter ones."""
    vector = np.zeros(COARSE_DIMENSION, dtype=np.float32)  # shape: [C]
    vector[:len(leading)] = leading
    return vector


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True,help='New synthetic fixture folder')
    args=parser.parse_args()
    args.output=args.output.resolve()
    args.output.mkdir(parents=True,exist_ok=False)
    corpus=args.output/'corpus'
    corpus.mkdir()
    cfg=load('config.example.json')
    cfg.update(roots=[str(corpus.resolve())],excluded_folders=[],index_protection='none',semantic_enabled=False,indexing_frequency='manual')
    service=Service(cfg,args.output/'data',args.output/'config.json')
    class Vector:
        config={'semantic_threshold':.3}
        def identity(self):return 'filename-regression'
        def encode(self,texts,query=False):return unit(1)[None]  # shape: [1,C]
    try:
        paper=corpus/'research.txt'
        paper.write_text('EFS generates samples without fitting a score model.',encoding='utf-8')
        (corpus/'refs').mkdir()
        for path in (paper,corpus/'refs'):
            service.indexer._file(path)
        row=service.store.get_file(path=str(paper.resolve()))
        for passage in service.store.pending_vectors(row['id'],'filename-regression'):
            service.store.put_vector(passage['id'],unit(1),'filename-regression')  # shape: [C]
        reply=service.store.search('EFS',Vector())
        assert reply['results'][0]['name']=='research.txt'
        assert set(reply['results'][0]['matches'])=={'Content','Semantic'}
        exact=corpus/'EFS.txt'
        exact.write_text('Unrelated text.',encoding='utf-8')
        service.indexer._file(exact)
        assert service.store.search('EFS',Vector())['results'][0]['name']=='EFS.txt'
        (args.output/'validation.json').write_text(json.dumps({'configuration':cfg,'substring_does_not_dominate':True,'exact_stem_priority':True,'passed':True},indent=2),encoding='utf-8')
        print('PASS: substring refs loses to content/meaning; exact EFS filename remains first')
    finally:service.close()


if __name__=='__main__':main()
