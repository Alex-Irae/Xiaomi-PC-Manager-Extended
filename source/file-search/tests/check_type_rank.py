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
        assert service.store.compact(cfg['name_only_extensions'])=={'removed_files':1,'name_only_files':1,'passages_without_vectors_now':0}
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
        # A file that changes again soon after it was read keeps its stored passages until its wait is over.
        assert cfg['reread_seconds_per_passage']==5 and '.log' in load('config.example.json')['name_only_extensions']
        diary=corpus/'diary.md';diary.write_text('the okapi lives in the forest',encoding='utf-8');service.indexer._file(diary)
        diary.write_text('the okapi lives in the forest, and so does the bongo',encoding='utf-8');service.indexer._file(diary)
        assert 'diary.md' in order('okapi') and 'diary.md' not in order('bongo') and str(diary.absolute()) in service.indexer.jobs
        assert service.store.stale_files()==[str(diary.resolve())],'a put-off visit must survive a change of worker'
        # "Free when idle": background embedding never loads the model by itself.
        service.config['model_standby']='free_idle'
        assert not service.indexer._may_embed()
        service.embedder.pipeline=object();assert service.indexer._may_embed()
        service.embedder.pipeline=None;service.indexer.catching_up=True;assert service.indexer._may_embed()
        service.indexer.catching_up=False;service.config['model_standby']='keep_loaded';assert service.indexer._may_embed()
        with service.store.connect() as db:db.execute("UPDATE files SET read_at=read_at-10 WHERE name='diary.md'")
        service.indexer._file(diary)
        assert 'diary.md' in order('bongo') and service.store.stale_files()==[]
        # A typed path: the place itself first, then what begins like it; a trailing separator lists the folder.
        paths=lambda text:[row['file_path'] for row in service.dispatch('local_path',{'text':text})['results']]
        assert paths(str(corpus))[0]==str(corpus) and all(row['typed_path'] for row in service.dispatch('local_path',{'text':str(corpus)})['results'])
        assert set(paths(str(corpus)+'\\'))>={str(corpus/'diary.md'),str(corpus/'display settings folder')} and paths(str(corpus)+'\\')[1]==str(corpus/'display settings folder'),'folders come first'
        assert paths(str(corpus/'zebra'))==[str(corpus/'zebra notes.md'),str(corpus/'zebra tool.py')]
        assert paths('~')==[str(Path.home())] and paths('%USERPROFILE%')[0]==str(Path.home()) and paths('diary.md')==[] and paths(str(corpus/'nothing here'))==[]
        opened=[];service.window_action=lambda action,params:opened.append((action,params['path']))
        service.dispatch('local_open_path',{'path':str(corpus)});service.dispatch('local_open_path',{'path':str(corpus/'diary.md')})
        assert opened==[('folder',str(corpus)),('reveal',str(corpus/'diary.md'))]
        for bad in ('diary.md',str(corpus/'nothing here'),''):
            try:service.dispatch('local_open_path',{'path':bad})
            except ValueError:continue
            raise AssertionError(f'opened {bad!r}')
        # A walk of the folders: unchanged files are recognised from the folder listing and not examined again;
        # a new file, a changed file and a deleted file are all still noticed.
        examined=[];real=service.indexer._file
        service.indexer._file=lambda path:(examined.append(Path(path).name),real(path))[1]
        service.indexer.running=True
        service.indexer._scan(corpus);first=set(examined);examined.clear()
        service.indexer._scan(corpus);assert examined==[],('unchanged files were examined again',examined)
        (corpus/'walk new.md').write_text('a gerenuk stands on two legs',encoding='utf-8')
        with service.store.connect() as db:db.execute("UPDATE files SET read_at=0 WHERE name='zebra notes.md'")
        (corpus/'zebra notes.md').write_text('the quagga and the kudu',encoding='utf-8');(corpus/'old report.md').unlink()
        service.indexer._scan(corpus)
        assert set(examined)=={'walk new.md','zebra notes.md'},examined
        assert 'walk new.md' in order('gerenuk') and 'zebra notes.md' in order('kudu') and 'old report.md' not in order('quagga')
        service.indexer._file=real
        # Excluded names and types are skipped by the walk as before.
        (corpus/'node_modules').mkdir();(corpus/'node_modules'/'inside.md').write_text('gerenuk',encoding='utf-8');(corpus/'picture.png').write_text('x',encoding='utf-8')
        service.indexer._scan(corpus);assert order('gerenuk')==['walk new.md'] and 'picture.png' not in order('picture')
        # Data files are searched as text and never queued for embedding; an unreadable file is left alone until it changes.
        assert load('config.example.json')['content_only_extensions']==['.json','.yaml','.yml','.toml','.csv']
        cfg['semantic_enabled']=True;cfg['content_only_extensions']=['.json'];service.store.content_only=['.json']
        service.embedder.identity=lambda:'check-model'
        (corpus/'state.json').write_text('{"animal": "tapir"}',encoding='utf-8');(corpus/'prose.md').write_text('a tapir has a short trunk',encoding='utf-8')
        service.indexer.vector_jobs.clear();service.indexer._file(corpus/'state.json');service.indexer._file(corpus/'prose.md')
        assert {'state.json','prose.md'}<=set(order('tapir'))
        waiting={service.store.get_file(file_id=fid)['name'] for fid in service.indexer.vector_jobs}
        assert waiting=={'prose.md'} and 'state.json' not in {service.store.get_file(file_id=fid)['name'] for fid in service.store.pending_vector_files()},waiting
        cfg['semantic_enabled']=False;service.indexer.vector_jobs.clear()
        huge=corpus/'huge.txt';huge.write_text('x '*(cfg['max_extracted_characters']//2+10),encoding='utf-8')
        tries=[];reading=service.indexer.store.record_error
        service.indexer.store.record_error=lambda *arguments:(tries.append(1),reading(*arguments))[1]
        service.indexer._file(huge);service.indexer._file(huge);service.indexer._scan(corpus)
        assert len(tries)==1,('an unreadable, unchanged file was read again',len(tries))
        # Once it changes it is tried again, after the hour that keeps a growing unreadable log from being read at each change.
        huge.write_text('small now',encoding='utf-8');service.indexer._scan(corpus);assert 'huge.txt' not in order('small')
        with service.store.connect() as db:db.execute("UPDATE files SET read_at=read_at-4000 WHERE name='huge.txt'")
        service.indexer._scan(corpus);assert 'huge.txt' in order('small')
        service.indexer.store.record_error=reading
        for wrong in ({'.pdf':'high'},{'.pdf':-1},{'p d f':1},[]):
            try:validate({**cfg,'type_weights':wrong},args.output/'config.json')
            except ValueError:continue
            raise AssertionError(f'accepted {wrong!r}')
        assert validate({**cfg,'type_weights':{'PDF':2,'default':1}},args.output/'config.json')['type_weights']=={'.pdf':2,'default':1}
        (args.output/'validation.json').write_text(json.dumps({'default_order':found,'images_left_out':True,'exact_name_first':True,'invalid_weights_rejected':True,'passed':True},indent=2),encoding='utf-8')
        print('PASS: preferred types first, code last, images left out, exact name first, weights validated')
    finally:service.close()


if __name__=='__main__':main()
