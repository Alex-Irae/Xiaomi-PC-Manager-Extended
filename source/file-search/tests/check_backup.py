"""Verify complete encrypted and SQLite backups while the source index changes.

Dependencies: existing search runtime, Windows for DPAPI. Outputs: new fixture
and JSON report; never opens the installed index. Command from project root:
runtime/python/python.exe -B tests/check_backup.py --output results/NEW_RUN
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from xiaomi_search.backup import create_backup
from xiaomi_search.store import Store
from xiaomi_search.service import Service


def check(output, protection):
    case = output / protection
    data = case / 'data'
    data.mkdir(parents=True)
    parent = case / 'backups'
    parent.mkdir()
    document = case / 'source.txt'
    document.write_text('A meaningful indexed document', encoding='utf-8')
    store = Store(data / 'index.sqlite3', protection)
    try:
        row = store.discover(document, document.stat())
        store.save(row['id'], document.stat(), 'content-digest', [('page1', 'meaningful indexed document')], 'extractor-1')
        vector = b'\x00\x00\x80?\x00\x00\x00@'
        with store.connect() as db:
            db.execute("UPDATE chunks SET vector=?,model_id='model-checksum',dimension=2", (vector,))
            db.execute("INSERT OR REPLACE INTO meta VALUES('embedding_model_hash','model-checksum')")
            original = [tuple(r) for r in db.execute('SELECT * FROM files')]
            chunks = [tuple(r) for r in db.execute('SELECT * FROM chunks')]
        phases = []
        def progress(phase):
            phases.append(phase)
            # This write occurs after checkpoint capture and must not alter the
            # backup. It also proves copying does not hold the live DB lock.
            if phase.startswith('Copying') and protection == 'windows':
                with store.connect() as db:
                    db.execute("UPDATE files SET digest='later-live-change'")
        result = create_backup(store, parent, {'roots': [str(case)], 'model_path': 'matching-model'}, progress)
        folder = Path(result['path'])
        manifest = json.loads((folder / 'manifest.json').read_text())
        with (folder / manifest['index']).open('rb') as stream:
            assert hashlib.file_digest(stream, 'sha256').hexdigest() == manifest['sha256']
        restore = case / 'restored'
        restore.mkdir()
        shutil.copy2(folder / manifest['index'], restore / manifest['index'])
        reopened = Store(restore / 'index.sqlite3', protection)
        try:
            with reopened.connect() as db:
                assert [tuple(r) for r in db.execute('SELECT * FROM files')] == original
                assert [tuple(r) for r in db.execute('SELECT * FROM chunks')] == chunks
                assert db.execute("SELECT value FROM meta WHERE key='embedding_model_hash'").fetchone()[0] == 'model-checksum'
                assert db.execute("SELECT count(*) FROM chunk_fts WHERE chunk_fts MATCH 'meaningful'").fetchone()[0] == 1
                assert db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
            reopened.discover(document.with_name('after-restore.txt'), document.stat())
            reopened.flush()
        finally:
            reopened.close()
        first = (folder / 'manifest.json').read_bytes()
        second = create_backup(store, parent, {}, lambda phase: None)
        assert second['path'] != result['path']
        assert (folder / 'manifest.json').read_bytes() == first
        try:
            create_backup(store, data, {})
            raise AssertionError('Backup inside application data accepted')
        except ValueError:
            pass
        def failed(phase):
            if phase == 'Verifying backup':
                raise OSError('Simulated full backup drive')
        try:
            create_backup(store, parent, {}, failed)
            raise AssertionError('Failed backup reported success')
        except OSError:
            pass
        assert len(list(parent.glob('*/manifest.json'))) == 2
        assert len(list(parent.iterdir())) == 3
        return {'protection': protection, 'passed': True, 'metadata_vectors_fts_restored': True,
                'restored_database_writable': True, 'no_overwrite': True,
                'failed_backup_unpublished': True, 'phases': phases}
    finally:
        store.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True, help='New isolated fixture/results directory')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    # Check asynchronous command handling without creating another model runtime.
    service = Service.__new__(Service)
    import threading
    service.backup_lock = threading.Lock()
    service.backup_state = {'active': False, 'phase': ''}
    service.window_action = lambda action, params: None
    assert service.backup_index() == {'cancelled': True}
    assert not service.backup_lock.locked()
    service.backup_lock.acquire()
    try:
        service.backup_index()
        raise AssertionError('Concurrent backup accepted')
    except RuntimeError:
        pass
    service.backup_lock.release()
    profile = args.output / 'profile'
    profile.mkdir()
    service.config_path = profile / 'config.json'
    service.window_action = lambda action, params: str(profile)
    try:
        service.backup_index()
        raise AssertionError('Backup inside the removable profile accepted')
    except ValueError:
        pass
    assert not service.backup_lock.locked()
    reports = []
    for protection in ['none', 'windows']:
        print('STEP: ' + protection + ' backup and restore', flush=True)
        reports.append(check(args.output, protection))
        print('PASS: ' + protection + ' metadata, vectors, FTS, writable restore and failure recovery', flush=True)
    (args.output / 'report.json').write_text(json.dumps({'passed': True, 'checks': reports, 'cancel_and_busy_checked': True}, indent=2))


if __name__ == '__main__':
    main()
