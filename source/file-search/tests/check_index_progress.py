"""Check progressive embeddings, nonblocking index commands and fast preferences.
Dependencies: existing Python environment and NumPy. Outputs: PASS, temp fixture.
Command from project root: .venv/Scripts/python.exe tests/check_index_progress.py
"""
import json
import sys
import tempfile
import threading
import time
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import PROJECT, load
from xiaomi_search.service import Service


class FixtureEmbedder:
    pipeline = None
    device = None
    error = None
    failed_devices = set()
    def __init__(self):
        self.entered = threading.Event()
        self.release = threading.Event()
        self.calls = 0
    def identity(self):
        return 'fixture-progress'
    def encode(self, texts):
        self.calls += 1
        self.entered.set()
        assert self.release.wait(5), 'Fixture was not released'
        return np.ones((len(texts), 3), dtype=np.float32)  # shape: [B, D]
    def schedule_release(self):
        pass
    def unload(self):
        pass


def main():
    with tempfile.TemporaryDirectory(prefix='ai-index-progress-') as temporary:
        root = Path(temporary)
        corpus = root / 'corpus'
        corpus.mkdir()
        for index in range(64):
            (corpus / f'{index:03}.txt').write_text('A document about estimation free quantum energy measurement.', encoding='utf-8')
        config = json.loads((PROJECT / 'config.example.json').read_text(encoding='utf-8-sig'))
        config.update(roots=[str(corpus)], excluded_folders=[], indexing_frequency='manual', indexing_mode='paused', index_protection='none')
        path = root / 'config.json'
        path.write_text(json.dumps(config), encoding='utf-8')
        service = Service(load(path), root / 'data', path)
        fixture = FixtureEmbedder()
        service.embedder = service.indexer.embedder = fixture
        try:
            begin = time.perf_counter()
            response = service.dispatch('local_index_now', {'force': True, 'wait': False})
            assert response['accepted'] and time.perf_counter() - begin < 1
            assert fixture.entered.wait(3), 'No embeddings during discovery'
            counts = service.store.counts()
            assert 0 < counts['files'] < 64, 'Embedding was postponed until the full crawl'
            assert service.activity()['busy'], 'Native lifetime was not pinned'
            begin = time.perf_counter()
            service.dispatch('local_save_config', {'theme': 'dark'})
            assert time.perf_counter() - begin < 1, 'Appearance blocked on filesystem reconciliation'
            try:
                service.dispatch('local_index_now', {'wait': False})
                raise AssertionError('Overlapping maintenance accepted')
            except RuntimeError:
                pass
            fixture.release.set()
            service.maintenance_thread.join(8)
            assert not service.maintenance_thread.is_alive(), 'Index command did not complete'
            assert service.store.counts()['vectors'] == 64
            assert service.indexer.mode == 'paused'
            assert not service.activity()['busy']
            calls = fixture.calls
            service.dispatch('local_index_now', {'force': True})
            assert fixture.calls == calls, 'Unchanged snapshots repeated embedding inference'
            # Setting scope alone must not request indexing.
            service.dispatch('local_save_config', {'roots': []})
            assert not service.indexer.jobs
            assert not service.search({'text': 'quantum', 'semantic': False})['results']
            assert [line for line in (root / 'included-folders.txt').read_text().splitlines() if line and not line.startswith('#')] == []
            print('PASS: immediate index ACK, embeddings before full crawl, responsive theme save, maintenance pin, overlap rejection, full completion and roots-only scope change')
        finally:
            fixture.release.set()
            service.close()


if __name__ == '__main__':
    main()
