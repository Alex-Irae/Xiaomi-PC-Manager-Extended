"""Check indexing's model pin and atomic encrypted checkpoint failure recovery.

Dependencies: existing Windows Python and DPAPI. Outputs: new fixtures and summary.
Command: private-python -B checks/check_idle_and_checkpoint.py --output NEW_DIRECTORY.
"""
import argparse
import json
from pathlib import Path
import sys
import time
sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'source/file-search'))
from xiaomi_search.embedding import Embedder
from xiaomi_search import protection as p

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output',required=True,type=Path,help='New evidence directory')
args=parser.parse_args()
args.output.mkdir(parents=True,exist_ok=False)
checks={}
def check(name,condition):
    assert condition,name
    checks[name]=True
    print('PASS:',name,flush=True)

config={'model_standby':'idle_unload','idle_unload_seconds':.05}
model=Embedder(config,args.output)
marker=object();model.pipeline=marker;model.active=lambda:True
model.schedule_release();time.sleep(.10)
check('indexing-blocks-idle-timer',model.pipeline is marker and model.timer is None)
model.release_idle()
check('late-idle-callback-cannot-unload-active-indexer',model.pipeline is marker)
model.active=lambda:False
config['model_standby']='keep_loaded'
model.schedule_release();model.release_idle()
check('keep-loaded-does-not-unload',model.pipeline is marker and model.timer is None)
config['model_standby']='idle_unload';model.schedule_release()
time.sleep(.15)
check('optional-idle-unload-still-releases-after-work',model.pipeline is None)

database=p.ProtectedDatabase(args.output/'atomic.sqlite3')
database.stopping.set();database.thread.join()
try:
    database.db.execute('CREATE TABLE fixture(value TEXT)')
    database.db.execute("INSERT INTO fixture VALUES('old checkpoint')")
    database.db.commit();database.flush(force=True)
    old=database.path.read_bytes()
    database.db.execute("INSERT INTO fixture VALUES('new pending row')")
    database.db.commit()
    write=p.write_database
    def fail(stream,snapshot):
        stream.write(b'incomplete new ciphertext')
        raise OSError('Simulated checkpoint interruption')
    p.write_database=fail
    try:
        database.flush(force=True)
        raise AssertionError('Interrupted checkpoint claimed success')
    except OSError:
        pass
    finally:
        p.write_database=write
    check('failed-save-preserves-last-authenticated-checkpoint',database.path.read_bytes()==old)
    database.flush(force=True)
finally:
    database.close()
database=p.ProtectedDatabase(args.output/'atomic.sqlite3')
try:
    check('retry-saves-and-reopens-both-rows',database.db.execute('SELECT count(*) FROM fixture').fetchone()[0]==2)
finally:
    database.close()
(args.output/'summary.json').write_text(json.dumps({'passed':True,'checks':checks},indent=2),encoding='utf-8')
