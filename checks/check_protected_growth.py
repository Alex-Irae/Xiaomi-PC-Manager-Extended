"""Reproduce SQLite's memdb limit and verify encrypted index growth after reload.

Dependencies: Windows, suite private Python 3.12/SQLite, standard library only.
Outputs: new encrypted fixture and summary.json inside --output; no user index modification.
Command: private-python -B checks/check_protected_growth.py --output results/NEW/growth [--app-root INSTALLED_APP]
"""
import argparse
import ctypes
import json
from pathlib import Path
import sqlite3
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', required=True, type=Path, help='New isolated evidence directory')
parser.add_argument('--app-root', type=Path, help='Installed app to test; defaults to copied search source')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=False)
app = args.app_root or Path(__file__).resolve().parents[1] / 'source/file-search'
sys.path.insert(0, str(app))
from xiaomi_search.protection import ProtectedDatabase, ENVELOPE_MAGIC, unseal

# Restrict only this test process's deserialize pager; no connections exist yet.
api = ctypes.CDLL(str(Path(sys.base_prefix) / 'DLLs/sqlite3.dll'))
assert api.sqlite3_shutdown() == 0
assert api.sqlite3_config(29, ctypes.c_longlong(2 * 1024 * 1024)) == 0
assert api.sqlite3_initialize() == 0
seed = sqlite3.connect(':memory:')
seed.execute('CREATE TABLE fixture(value BLOB)')
seed.commit()
snapshot = seed.serialize()
seed.close()
capped = sqlite3.connect(':memory:')
capped.deserialize(snapshot)
try:
    capped.execute('INSERT INTO fixture VALUES(zeroblob(?))', (4 * 1024 * 1024,))
except sqlite3.OperationalError as error:
    assert 'full' in str(error)
    print('PASS: reproduced database-full failure in the original deserialize pager', flush=True)
else:
    raise AssertionError('The configured pager limit did not reproduce the failure')
finally:
    capped.close()

path = args.output / 'index.sqlite3'
first = ProtectedDatabase(path)
first.db.execute('CREATE TABLE fixture(value BLOB)')
first.db.commit()
first.close()
reopened = ProtectedDatabase(path)
try:
    reopened.db.execute('INSERT INTO fixture VALUES(zeroblob(?))', (4 * 1024 * 1024,))
    reopened.db.commit()
    assert reopened.db.execute('SELECT length(value) FROM fixture').fetchone()[0] == 4 * 1024 * 1024
finally:
    reopened.close()
cipher = Path(str(path) + '.dpapi').read_bytes()
assert cipher.startswith(ENVELOPE_MAGIC) and not path.exists()
assert len(unseal(cipher)) > 4 * 1024 * 1024
summary = {'passed': True, 'seed': 0, 'memdbLimitBytes': 2 * 1024 * 1024, 'growthBytes': 4 * 1024 * 1024,
           'baselineFailureReproduced': True, 'encryptedCheckpoint': True, 'plaintextWorkingFile': False, 'app': str(app)}
(args.output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
print('PASS: reloaded protected index grows beyond the deserialize limit and saves an authenticated encrypted checkpoint', flush=True)
