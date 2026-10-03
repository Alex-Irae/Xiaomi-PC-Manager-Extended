"""Verify migration on a copy of a retained encrypted index, never its live file.

Dependencies: existing Windows Python and DPAPI. Output: a new directory, encrypted
fixture and summary.json. Command: private-python -B checks/check_retained_index.py
--input BACKUP.dpapi --output NEW_RESULTS.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--input', required=True, type=Path, help='Read-only backup of the protected index')
parser.add_argument('--output', required=True, type=Path, help='New results directory')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=False)
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'source/file-search'))
from xiaomi_search import protection as p

def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def evidence(db):
    tables = [row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")]
    counts = {name: db.execute('SELECT count(*) FROM "'+name.replace('"','""')+'"').fetchone()[0] for name in tables}
    samples = {}
    for name in tables:
        checksum = hashlib.sha256()
        for row in db.execute('SELECT * FROM "'+name.replace('"','""')+'" LIMIT 12'):
            for value in row:
                checksum.update(type(value).__name__.encode())
                checksum.update(value if isinstance(value, bytes) else repr(value).encode('utf-8'))
                checksum.update(b'\0')
        samples[name] = checksum.hexdigest()
    assert db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
    return {'counts': counts, 'samples': samples}

original = digest(args.input)
target = args.output / 'index.sqlite3'
shutil.copy2(args.input, Path(str(target)+'.dpapi'))
watch = time.perf_counter()
print('STEP: open the retained encrypted index copy', flush=True)
database = p.ProtectedDatabase(target)
# Keep this isolated validation from starting periodic checkpoints during comparison.
database.stopping.set()
database.thread.join()
try:
    before = evidence(database.db)
    print('PASS: source integrity; STEP: migrate the protected checkpoint', flush=True)
    database.flush(force=True)
finally:
    database.close()
print('STEP: reopen the streamed checkpoint and compare all table counts and samples', flush=True)
database = p.ProtectedDatabase(target)
database.stopping.set()
database.thread.join()
try:
    after = evidence(database.db)
    assert before == after, 'Retained data changed during migration'
finally:
    database.close()
assert digest(args.input) == original, 'Input backup changed'
summary = {'passed': True, 'seconds': time.perf_counter()-watch, 'input_sha256': original,
           'output_bytes': Path(str(target)+'.dpapi').stat().st_size, **after}
(args.output/'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
print('PASS: retained index migration, integrity, table counts, samples and unchanged input', flush=True)
