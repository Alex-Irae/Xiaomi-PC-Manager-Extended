"""Check independent search channels, incremental metadata, and encrypted storage.
Dependencies: existing NumPy/Python environment, Windows DPAPI. Outputs: new
fixture files and privacy-check.json inside --output. Original corpus untouched.
Command: .venv/Scripts/python.exe tests/check_index_privacy.py --output results/NEW_RUN
"""
import argparse
import ctypes
import json
import os
import sys
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import load
from xiaomi_search.service import Service
from xiaomi_search.protection import MAGIC, ENVELOPE_MAGIC, protect, aes_gcm, seal, unseal
from xiaomi_search.store import Store


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',required=True,type=Path,help='Existing numbered run directory; a new fixture is created')
    parser.add_argument('--label',default='privacy-fixture',help='New fixture folder name, preserving previous attempts')
    args=parser.parse_args(); root=args.output.resolve()/args.label; root.mkdir()
    # Published AES-GCM zero-key/zero-nonce 128-bit known-answer vector.
    cipher,tag=aes_gcm(bytes(16),bytes(16),bytes(12))
    assert cipher.hex()=='0388dace60b6a392f328c2b971b2fe78'
    assert tag.hex()=='ab6e47d42cec13bdf53a67b21257bddf'
    assert aes_gcm(cipher,bytes(16),bytes(12),tag)[0]==bytes(16)
    assert unseal(MAGIC+protect(b'legacy compatibility'))==b'legacy compatibility'
    corpus=root/'corpus';corpus.mkdir();data=root/'data';data.mkdir()
    source=corpus/'particles.md';source.write_text('Repulsive particles draw new samples without fitting a neural score.',encoding='utf-8')
    cfg=load('config.json');cfg.update(roots=[str(corpus)],excluded_folders=[],semantic_enabled=False,indexing_frequency='manual',indexing_mode='normal')
    # Seed a legacy plaintext index, so migration itself is exercised.
    legacy=Store(data/'index.sqlite3');row=legacy.discover(source,source.stat());legacy.close()
    service=Service(cfg,data,root/'config.json')
    report={'seed':42,'configuration':cfg,'checks':[]}
    try:
        service.indexer._file(source)
        fid=service.store.get_file(path=source)['id']
        for chunk in service.store.pending_vectors(fid,'synthetic-channels'):
            service.store.put_vector(chunk['id'],np.asarray([1,0],dtype=np.float32),'synthetic-channels') # shape: [2]
        class Meaning:
            config={'semantic_threshold':.3}
            pipeline=None
            device=None
            error=None
            def identity(self):return 'synthetic-channels'
            def encode(self,texts,query=False):return np.asarray([[1,0]],dtype=np.float32) # shape: [1,2]
            def unload(self):pass
        service.embedder=Meaning()
        for names,contents,meaning in [(True,False,False),(False,True,False),(False,False,True),(True,True,True),(True,False,True),(False,True,True),(True,True,False)]:
            cfg.update(name_enabled=names,content_enabled=contents,semantic_enabled=meaning)
            hits=service.search({'text':'particles'})['results'];assert hits
            labels=set(hits[0]['matches'])
            assert ('Content' in labels)==contents
            assert ('Semantic' in labels)==meaning
            assert bool(labels & {'Filename','Path'})==names
        report['checks'].append('all seven nonempty channel combinations')
        cfg.update(name_enabled=True,content_enabled=True,semantic_enabled=False)
        with service.store.connect() as db:before=[tuple(r) for r in db.execute('SELECT id,vector FROM chunks WHERE file_id=?',(fid,))]
        revision=service.store.revision();service.indexer._file(source)
        assert service.store.revision()==revision,'Unchanged metadata must not invalidate the cache'
        renamed=source.with_name('renamed-particles.md');source.rename(renamed);service.indexer._file(renamed)
        assert service.store.get_file(path=renamed)['id']==fid
        with service.store.connect() as db:assert before==[tuple(r) for r in db.execute('SELECT id,vector FROM chunks WHERE file_id=?',(fid,))]
        stat=renamed.stat();os.utime(renamed,ns=(stat.st_atime_ns,stat.st_mtime_ns+10000000));service.indexer._file(renamed)
        with service.store.connect() as db:assert before==[tuple(r) for r in db.execute('SELECT id,vector FROM chunks WHERE file_id=?',(fid,))]
        renamed.write_text(renamed.read_text()+' Changed contents.',encoding='utf-8');service.indexer._file(renamed)
        with service.store.connect() as db:assert all(r[0] is None for r in db.execute('SELECT vector FROM chunks WHERE file_id=?',(fid,)))
        report['checks'].append('unchanged and timestamp-only updates reuse vectors; rename preserves ID/vectors; new content invalidates vectors')
        hidden=corpus/'AppData';hidden.mkdir();superhidden=corpus/'protected-system';superhidden.mkdir()
        api=ctypes.WinDLL('kernel32',use_last_error=True).SetFileAttributesW;api.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32];api.restype=ctypes.c_int
        assert api(str(hidden),2) and api(str(superhidden),6)
        assert service.indexer.allowed(hidden) and not service.indexer.allowed(superhidden)
        assert not service.indexer.allowed(corpus/'settings.ini') and not service.indexer.allowed(corpus/'library.dll')
        (hidden/'visible.md').write_text('Ordinary hidden folder is eligible.',encoding='utf-8')
        (superhidden/'secret.md').write_text('Protected folder must stay outside index.',encoding='utf-8')
        value=service.dispatch('local_index_now',{'force':True});assert not value['indexer']['queued_files']
        assert service.store.get_file(path=hidden/'visible.md') and not service.store.get_file(path=superhidden/'secret.md')
        report['checks'].append('ordinary hidden AppData allowed, hidden+system excluded, ini/dll excluded, forced incremental indexing')
        service.store.flush();cipher=Path(str(service.store.path)+'.dpapi').read_bytes()
        assert cipher.startswith(ENVELOPE_MAGIC) and b'Repulsive particles' not in cipher and b'renamed-particles.md' not in cipher
        damaged=bytearray(cipher);damaged[-1]^=1
        try:unseal(bytes(damaged))
        except OSError:pass
        else:raise AssertionError('Tampered ciphertext accepted')
        assert not service.store.path.exists(),'Plaintext working SQLite must be absent'
        report['checks'].append('encrypted names/content/vectors, authenticated tamper rejection, verified legacy migration')
        backup=service.store.reset();assert Path(backup).read_bytes().startswith(ENVELOPE_MAGIC) and service.store.counts()['files']==0 and renamed.exists()
        report['checks'].append('reset archives encrypted snapshot and preserves originals')
    finally:service.close()
    reopened=Store(data/'index.sqlite3','windows')
    try:assert reopened.counts()['files']==0
    finally:reopened.close()
    report['checks'].append('protected checkpoint reload')
    report['passed']=True
    with (args.output/'privacy-check.json').open('x',encoding='utf-8') as stream:json.dump(report,stream,indent=2)
    for check in report['checks']:print('PASS: '+check,flush=True)


if __name__=='__main__':main()
