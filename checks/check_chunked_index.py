"""Test streamed encryption, old-index migration and real growth past 2 GiB.

Dependencies: existing Windows x64 Python/SQLite. Outputs: new --output fixtures
and summary.json. Command: private-python -B checks/check_chunked_index.py
--output results/NEW/chunks [--large]. Never opens the user's live index.
"""
import argparse
import io
import json
from pathlib import Path
import sqlite3
import sys
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', required=True, type=Path, help='New evidence directory')
parser.add_argument('--large', action='store_true', help='Also save/reopen/grow a real SQLite index larger than 2 GiB')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=False)
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'source/file-search'))
from xiaomi_search import protection as p

checks = {}
def check(name, condition):
    assert condition, name
    checks[name] = True
    print('PASS:', name, flush=True)

seed = sqlite3.connect(':memory:')
seed.execute('CREATE TABLE fixture(value BLOB)')
seed.execute('INSERT INTO fixture VALUES(zeroblob(200000))')
seed.execute('CREATE TABLE edgecases(text TEXT, number REAL, raw BLOB)')
seed.execute('INSERT INTO edgecases VALUES(?,?,?)',("prefix\x00suffix\n'quoted'",float('inf'),bytes(range(256))))
seed.execute('CREATE VIRTUAL TABLE docs USING fts5(body)')
text = "Chinese \u4e0a\u6d77 file\nEnglish 'quote'"
seed.execute('INSERT INTO docs VALUES(?)', (text,))
seed.commit()
old = p.seal(seed.serialize())
seed.close()
path = args.output / 'legacy.sqlite3'
Path(str(path)+'.dpapi').write_bytes(old)
db = p.ProtectedDatabase(path)
try:
    check('legacy-v2-loaded', db.db.execute('SELECT length(value) FROM fixture').fetchone()[0] == 200000)
    db.flush(force=True)
finally:
    db.close()
check('v3-encrypted-with-no-plaintext-file', Path(str(path)+'.dpapi').read_bytes().startswith(p.CHUNKED_MAGIC) and not path.exists())
p.CHUNK_BYTES = 65536
db = p.ProtectedDatabase(path)
try:
    check('fts-and-newline-restored', db.db.execute("SELECT body FROM docs WHERE docs MATCH 'English'").fetchone()[0] == text)
    check('nul-infinity-and-blob-preserved',tuple(db.db.execute('SELECT text,number,raw FROM edgecases').fetchone())==("prefix\x00suffix\n'quoted'",float('inf'),bytes(range(256))))
    out = io.BytesIO()
    p.write_database(out, db.db)
finally:
    db.close()
cipher = out.getvalue()
head = len(p.CHUNKED_MAGIC)+4+int.from_bytes(cipher[len(p.CHUNKED_MAGIC):len(p.CHUNKED_MAGIC)+4], 'little')
end1 = head+44+int.from_bytes(cipher[head+12:head+16], 'little')
end2 = end1+44+int.from_bytes(cipher[end1+12:end1+16], 'little')
mutations = {
    'header-tamper-rejected': cipher[:head-1]+bytes([cipher[head-1]^1])+cipher[head:],
    'cipher-tamper-rejected': cipher[:-1]+bytes([cipher[-1]^1]),
    'truncation-rejected': cipher[:-50],
    'trailing-data-rejected': cipher+b'extra',
    'chunk-reorder-rejected': cipher[:head]+cipher[end1:end2]+cipher[head:end1]+cipher[end2:],
    'record-boundary-truncation-rejected': cipher[:end1],
}
for name, value in mutations.items():
    target = sqlite3.connect(':memory:')
    try:
        try:
            p.restore_database(io.BytesIO(value), target)
        except (OSError, ValueError):
            check(name, True)
        else:
            raise AssertionError(name)
    finally:
        target.close()
p.CHUNK_BYTES = 16*1024*1024

if args.large:
    path = args.output / 'large.sqlite3'
    watch = time.perf_counter()
    db = p.ProtectedDatabase(path)
    try:
        with db.lock:
            db.db.execute('CREATE TABLE fixture(id INTEGER PRIMARY KEY,value BLOB)')
            for number in range(137):
                db.db.execute('INSERT INTO fixture VALUES(?,zeroblob(?))', (number,16*1024*1024))
            db.db.commit()
        db.flush(force=True)
        logical = db.db.execute('PRAGMA page_count').fetchone()[0]*db.db.execute('PRAGMA page_size').fetchone()[0]
        check('saved-real-index-over-2gib', logical > 2**31)
    finally:
        db.close()
    print('STEP: reopen the large encrypted index and add another 16 MiB row', flush=True)
    db = p.ProtectedDatabase(path)
    try:
        with db.lock:
            check('large-rows-intact', db.db.execute('SELECT count(*),sum(length(value)) FROM fixture').fetchone()[1] == 137*16*1024*1024)
            db.db.execute('INSERT INTO fixture VALUES(999,zeroblob(?))', (16*1024*1024,))
            db.db.commit()
        db.flush()
        check('large-reopen-can-grow', db.db.execute('SELECT count(*) FROM fixture').fetchone()[0] == 138)
    finally:
        db.close()
    checks.update(largeSeconds=round(time.perf_counter()-watch,3),logicalBytes=logical,encryptedBytes=Path(str(path)+'.dpapi').stat().st_size)

(args.output/'summary.json').write_text(json.dumps({'passed':True,'seed':0,'large':args.large,'checks':checks},indent=2),encoding='utf-8')
