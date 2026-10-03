"""Check live exclusion-file edits against real discovery and lexical retrieval.

Dependencies: existing Python environment. Outputs: PASS, disposable fixture only.
Command from project root: .venv/Scripts/python.exe tests/check_exclusions.py
"""
import json
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import PROJECT, load
from xiaomi_search.exclusions import read_paths
from xiaomi_search.service import Service


def main():
    with tempfile.TemporaryDirectory(prefix="ai-exclusions-") as temporary:
        root = Path(temporary)
        corpus = root / 'corpus'
        excluded = corpus / 'excluded'
        excluded.mkdir(parents=True)
        (excluded / 'private.txt').write_text('secret nebula material', encoding='utf-8')
        (corpus / 'public.txt').write_text('public nebula material', encoding='utf-8')
        (corpus / 'ignore.dll').write_text('nebula', encoding='utf-8')
        config = json.loads((PROJECT / 'config.example.json').read_text(encoding='utf-8-sig'))
        config.update(roots=[str(corpus)], excluded_folders=[str(excluded)], semantic_enabled=False,
                      indexing_frequency='manual', index_protection='none')
        path = root / 'config.json'
        path.write_text(json.dumps(config), encoding='utf-8')
        service = Service(load(path), root / 'data', path)
        try:
            # The old deployment wrote escape characters instead of line endings.
            file = root / 'excluded-folders.txt'
            file.write_text('# Legacy exclusions\\r\\n'+str(excluded)+'\\r\\n', encoding='utf-8')
            assert read_paths(file) == [str(excluded)]
            assert '\\r\\n' not in file.read_text(encoding='utf-8')
            assert (root/'included-folders.txt').read_text().splitlines()[1] == str(corpus)
            service.indexer.start(watch=False)
            service.dispatch('local_index_now', {'force': True})
            search = lambda: service.dispatch('local_search', {'text': 'nebula', 'semantic': False})['results']
            assert [row['name'] for row in search()] == ['public.txt']
            file = root / 'excluded-folders.txt'
            file.write_text('# allow the formerly excluded folder\n', encoding='utf-8')
            service.dispatch('local_index_now', {'force': True})
            assert {row['name'] for row in search()} == {'public.txt', 'private.txt'}
            file.write_text(str(excluded)+'\n', encoding='utf-8')
            assert [row['name'] for row in search()] == ['public.txt']
            file.write_text('relative/path\n', encoding='utf-8')
            try:
                read_paths(file)
                raise AssertionError('Relative exclusion accepted')
            except ValueError:
                pass
            file.write_text(str(excluded)+'\n', encoding='utf-8')
            # Changing scope updates live notifications without scanning old files.
            another = root / 'another'
            another.mkdir()
            service.dispatch('local_save_config', {'roots': [str(another)], 'indexing_frequency': 'realtime'})
            assert not service.indexer.jobs
            (another / 'added.txt').write_text('watcher nebula', encoding='utf-8')
            deadline = time.monotonic() + 8
            while time.monotonic() < deadline and not search():
                time.sleep(0.1)
            assert [row['name'] for row in search()] == ['added.txt'], 'New root notifications were lost'
            # Reset stops and recreates an observer, not an already-started Thread.
            service.dispatch('local_reset_index', {'force': True})
            assert service.indexer.observer.is_alive()
            assert [row['name'] for row in search()] == ['added.txt']
            print('PASS: live root watcher replacement and reset recovery')
            print('PASS: literal-CRLF repair, included file migration, live edits, unexclude/reindex, type exclusion, invalid-path rejection')
        finally:
            service.close()


if __name__ == '__main__':
    main()
