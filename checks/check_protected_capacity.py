"""Check protected-index capacity rollback, checkpoint recovery and worker pause.

Dependencies: Windows, suite private Python and copied search dependencies.
Outputs: isolated encrypted fixture and summary.json in a new --output directory.
Command: private-python -B checks/check_protected_capacity.py --output results/NEW/capacity
"""
import argparse
import json
from pathlib import Path
import sys
import threading

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', required=True, type=Path, help='New isolated evidence directory')
parser.add_argument('--app-root', type=Path, help='Installed app; defaults to copied search source')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=False)
app = args.app_root or Path(__file__).resolve().parents[1] / 'source/file-search'
sys.path.insert(0, str(app))
from xiaomi_search import protection
from xiaomi_search.indexer import Indexer
from xiaomi_search.store import Store

# Simulate the same pager boundary with a small fixture rather than 2 GiB RAM.
protection.SNAPSHOT_MAX_BYTES = 2 * 1024 * 1024
path = args.output / 'index.sqlite3'
store = Store(path, protection='windows')
with store.connect() as db:
    db.execute('CREATE TABLE fixture(value BLOB)')
    db.execute('INSERT INTO fixture VALUES(zeroblob(?))', (256 * 1024,))
store.protected.flush()
before = Path(str(path) + '.dpapi').read_bytes()
try:
    with store.connect() as db:
        db.execute('INSERT INTO fixture VALUES(zeroblob(?))', (4 * 1024 * 1024,))
except protection.IndexCapacityError as exc:
    assert 'Indexing paused' in str(exc) and 'Existing search results' in str(exc)
else:
    raise AssertionError('Oversized write did not raise an explicit capacity error')
with store.connect() as db:
    assert db.execute('SELECT COUNT(*) FROM fixture').fetchone()[0] == 1
store.protected.flush(force=True)
store.protected.close()
restored = Store(path, protection='windows')
with restored.connect() as db:
    assert db.execute('SELECT length(value) FROM fixture').fetchone()[0] == 256 * 1024
restored.protected.close()
assert not path.exists() and before.startswith(protection.ENVELOPE_MAGIC)
print('PASS: capacity failure rolls back and preserves a readable encrypted checkpoint', flush=True)

settings = json.loads((app / 'config.example.json').read_text(encoding='utf-8-sig'))
settings.update(roots=[str(args.output)], indexing_mode='normal', semantic_enabled=False)
worker = Indexer(settings, None, None, args.output / 'data')
worker.running = True
worker.jobs[str(args.output / 'fixture.txt')] = 0
paused = threading.Event()

def capacity_failure(path):
    raise protection.capacity_error()

def notified():
    if worker.mode == 'paused':
        with worker.condition:
            worker.running = False
            worker.condition.notify_all()
        paused.set()

worker._file = capacity_failure
worker.notify = notified
thread = threading.Thread(target=worker._work)
thread.start()
assert paused.wait(5), 'Worker did not pause after the capacity error'
thread.join(5)
assert not thread.is_alive() and not worker.busy
assert worker.status()['mode'] == 'paused' and 'snapshot capacity' in worker.scan_error
summary = {'passed': True, 'seed': 0, 'app': str(app), 'capacityBytes': protection.SNAPSHOT_MAX_BYTES,
           'writeRollback': True, 'encryptedCheckpointReadable': True, 'workerPaused': True,
           'explicitCapacityError': worker.scan_error, 'plaintextWorkingFile': False}
(args.output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
print('PASS: actual indexing worker pauses with an actionable capacity error', flush=True)
