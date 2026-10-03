"""Windows-account encrypted SQLite snapshots with no plaintext working file.

Dependencies: Python stdlib, Windows DPAPI. Outputs: .dpapi snapshots and an
exclusive lock file. Command: used by python -m xiaomi_search (no standalone CLI).
RAM is plaintext while unlocked; this cannot protect against code running as you.
"""
import ctypes
import os
import sqlite3
import io
import zlib
import struct
import math
import threading
import time
from ctypes import wintypes
from pathlib import Path

MAGIC = b"XIAISEARCH-DPAPI-1\n"
ENVELOPE_MAGIC = b"XIAISEARCH-AESGCM-2\n"
CHUNKED_MAGIC = b"XIAISEARCH-AESGCM-3\n"
CHUNK_BYTES = 16 * 1024 * 1024


class IndexCapacityError(RuntimeError):
    """SQLite could not allocate another page for the protected index."""


def capacity_error():
    return IndexCapacityError(
        "Indexing paused: SQLite could not allocate another index page. "
        "Existing search results remain available. Check available memory and storage."
    )


def aes_gcm(data, key, nonce, tag=None, aad=b''):
    """Use Windows CNG AES-GCM, returning (bytes, authentication tag).

    key: 16/24/32 bytes; nonce: 12 bytes; tag: 16 bytes for decryption.
    No algorithm is implemented here, only ctypes bindings to Windows crypto.
    """
    class Auth(ctypes.Structure):
        _fields_=[('size',wintypes.ULONG),('version',wintypes.ULONG),
                  ('nonce',ctypes.c_void_p),('nonce_size',wintypes.ULONG),
                  ('aad',ctypes.c_void_p),('aad_size',wintypes.ULONG),
                  ('tag',ctypes.c_void_p),('tag_size',wintypes.ULONG),
                  ('mac',ctypes.c_void_p),('mac_size',wintypes.ULONG),
                  ('total_aad',wintypes.ULONG),('total_data',ctypes.c_ulonglong),('flags',wintypes.ULONG)]
    if len(key) not in (16,24,32) or len(nonce)!=12 or (tag is not None and len(tag)!=16):
        raise ValueError('Invalid AES-GCM key, nonce or tag length')
    api=ctypes.WinDLL('bcrypt');pointer=ctypes.c_void_p;ulong=wintypes.ULONG
    signatures={
        'BCryptOpenAlgorithmProvider':[ctypes.POINTER(pointer),ctypes.c_wchar_p,ctypes.c_wchar_p,ulong],
        'BCryptSetProperty':[pointer,ctypes.c_wchar_p,pointer,ulong,ulong],
        'BCryptGenerateSymmetricKey':[pointer,ctypes.POINTER(pointer),pointer,ulong,pointer,ulong,ulong],
        'BCryptEncrypt':[pointer,pointer,ulong,pointer,pointer,ulong,pointer,ulong,ctypes.POINTER(ulong),ulong],
        'BCryptDecrypt':[pointer,pointer,ulong,pointer,pointer,ulong,pointer,ulong,ctypes.POINTER(ulong),ulong],
        'BCryptDestroyKey':[pointer], 'BCryptCloseAlgorithmProvider':[pointer,ulong]}
    for name,arguments in signatures.items():
        function=getattr(api,name);function.argtypes=arguments;function.restype=ctypes.c_long
    def check(status):
        if status<0:raise OSError(f'Windows AES-GCM failed: NTSTATUS 0x{status & 0xffffffff:08X}')
    algorithm=pointer();handle=pointer()
    try:
        check(api.BCryptOpenAlgorithmProvider(ctypes.byref(algorithm),'AES',None,0))
        mode=ctypes.create_unicode_buffer('ChainingModeGCM')
        check(api.BCryptSetProperty(algorithm,'ChainingMode',mode,ctypes.sizeof(mode),0))
        secret=ctypes.create_string_buffer(key)
        # Windows 7+ allocates the key object when the object buffer is NULL.
        check(api.BCryptGenerateSymmetricKey(algorithm,ctypes.byref(handle),None,0,secret,len(key),0))
        nonce_buffer=ctypes.create_string_buffer(nonce);aad_buffer=ctypes.create_string_buffer(aad)
        tag_buffer=ctypes.create_string_buffer(tag or bytes(16),16)
        info=Auth();info.size=ctypes.sizeof(Auth);info.version=1
        info.nonce=ctypes.cast(nonce_buffer,pointer);info.nonce_size=len(nonce)
        info.aad=ctypes.cast(aad_buffer,pointer);info.aad_size=len(aad)
        info.tag=ctypes.cast(tag_buffer,pointer);info.tag_size=16
        source=ctypes.create_string_buffer(data);output=ctypes.create_string_buffer(max(1,len(data)));written=ulong()
        function=api.BCryptDecrypt if tag is not None else api.BCryptEncrypt
        check(function(handle,source,len(data),ctypes.byref(info),None,0,output,len(data),ctypes.byref(written),0))
        return output.raw[:written.value],tag_buffer.raw
    finally:
        if handle:api.BCryptDestroyKey(handle)
        if algorithm:api.BCryptCloseAlgorithmProvider(algorithm,0)


def seal(data):
    """Encrypt a fresh snapshot with AES-256-GCM and a DPAPI-wrapped random key."""
    key=os.urandom(32);nonce=os.urandom(12);wrapped=protect(key)
    cipher,tag=aes_gcm(data,key,nonce,aad=ENVELOPE_MAGIC)
    return ENVELOPE_MAGIC+len(wrapped).to_bytes(4,'little')+wrapped+nonce+tag+cipher


def unseal(payload):
    """Read authenticated snapshots, including the original bulk-DPAPI format."""
    if payload.startswith(CHUNKED_MAGIC):
        restored = sqlite3.connect(':memory:')
        target = sqlite3.connect(':memory:')
        try:
            restore_database(io.BytesIO(payload), restored)
            restored.backup(target)
            return target.serialize()
        finally:
            restored.close(); target.close()
    if payload.startswith(MAGIC):return protect(payload[len(MAGIC):],decrypt=True)
    if not payload.startswith(ENVELOPE_MAGIC):raise ValueError('Unknown protected index format')
    begin=len(ENVELOPE_MAGIC);length=int.from_bytes(payload[begin:begin+4],'little');begin+=4
    if not 32<=length<=65536 or len(payload)<begin+length+28:raise ValueError('Truncated protected index')
    key=protect(payload[begin:begin+length],decrypt=True);begin+=length
    plain,_=aes_gcm(payload[begin+28:],key,payload[begin:begin+12],payload[begin+12:begin+28],ENVELOPE_MAGIC)
    return plain


MAX_CHUNK_BYTES = 64 * 1024 * 1024


def write_database(stream, database):
    """Stream compressed SQL batches, authenticating order, sizes and completion.

    SQLite's single-allocation limit also applies to serialize(), even on x64.
    iterdump uses bounded rows; virtual-table shadow rows contain the FTS index.
    No plaintext working file or database-sized Python buffer is allocated.
    """
    key = os.urandom(32)
    wrapped = protect(key)
    header = CHUNKED_MAGIC + struct.pack('<I', len(wrapped)) + wrapped
    stream.write(header)
    number = 0
    def quote(value):
        # SQLite's quote(TEXT) truncates at NUL. Preserve exact text/blob values
        # and infinities rather than trusting the SQL-dump convenience default.
        if value is None:return 'NULL'
        if isinstance(value,str):
            return "CAST(X'"+value.encode('utf-8').hex()+"' AS TEXT)" if '\x00' in value else "'"+value.replace("'","''")+"'"
        if isinstance(value,bytes):return "X'"+value.hex()+"'"
        if isinstance(value,float) and math.isinf(value):return '-9.0e999' if value<0 else '9.0e999'
        return repr(value)
    database.create_function('quote',1,quote)

    def record(plain):
        nonlocal number
        compressed = zlib.compress(plain, level=1)
        fields = struct.pack('<QII', number, len(plain), len(compressed))
        nonce = os.urandom(12)
        cipher, tag = aes_gcm(compressed, key, nonce, aad=header + fields)
        stream.write(fields + nonce + tag)
        stream.write(cipher)
        number += 1

    virtual = tuple('INSERT INTO "' + name.replace('"', '""') + '" VALUES' for (name,) in
                    database.execute("SELECT name FROM sqlite_master WHERE sql LIKE 'CREATE VIRTUAL TABLE%'") )
    batch = bytearray()
    for statement in database.iterdump():
        if statement.startswith(virtual):
            continue  # Shadow tables already preserve virtual-table contents and indexes.
        encoded = statement.encode('utf-8')
        # ponytail: 64 MiB per SQL row; a paged encrypted VFS is needed for larger individual rows.
        if len(encoded) + 4 > MAX_CHUNK_BYTES:
            raise ValueError('A single encrypted-index row exceeds 64 MiB')
        if batch and len(batch) + len(encoded) + 4 > CHUNK_BYTES:
            record(batch)
            batch.clear()
        batch.extend(struct.pack('<I', len(encoded)))
        batch.extend(encoded)
    if batch:
        record(batch)
    record(b'')  # Authenticated terminal record rejects truncation at a chunk boundary.


def restore_database(stream, database):
    """Restore v3 batches into a private RAM database before exposing any results."""
    def exact(length):
        value = stream.read(length)
        if len(value) != length:
            raise ValueError('Truncated protected index')
        return value

    if exact(len(CHUNKED_MAGIC)) != CHUNKED_MAGIC:
        raise ValueError('Unknown protected index format')
    length_bytes = exact(4)
    wrapped_size = int.from_bytes(length_bytes, 'little')
    if not 32 <= wrapped_size <= 65536:
        raise ValueError('Invalid protected index key length')
    wrapped = exact(wrapped_size)
    header = CHUNKED_MAGIC + length_bytes + wrapped
    key = protect(wrapped, decrypt=True)
    if len(key) != 32:
        raise ValueError('Invalid protected index key')
    number = 0
    while True:
        fields = exact(16)
        sequence, plain_size, cipher_size = struct.unpack('<QII', fields)
        if sequence != number or plain_size > MAX_CHUNK_BYTES or not 1 <= cipher_size <= MAX_CHUNK_BYTES + 65536:
            raise ValueError('Invalid protected index chunk')
        nonce, tag = exact(12), exact(16)
        compressed, _ = aes_gcm(exact(cipher_size), key, nonce, tag, aad=header + fields)
        inflater = zlib.decompressobj()
        plain = inflater.decompress(compressed, plain_size + 1)
        if len(plain) != plain_size or not inflater.eof or inflater.unused_data or inflater.unconsumed_tail:
            raise ValueError('Invalid protected index compressed data')
        if not plain:
            if stream.read(1):
                raise ValueError('Unexpected protected index trailing data')
            if database.in_transaction:
                raise ValueError('Incomplete protected index transaction')
            return
        offset = 0
        while offset < len(plain):
            if len(plain) - offset < 4:
                raise ValueError('Truncated protected SQL row')
            size = int.from_bytes(plain[offset:offset + 4], 'little')
            offset += 4
            if not 1 <= size <= len(plain) - offset:
                raise ValueError('Invalid protected SQL row')
            database.execute(plain[offset:offset + size].decode('utf-8'))
            offset += size
        number += 1


def protect(data, decrypt=False):
    """Authenticate/encrypt bytes for the current Windows account, never machine-wide."""
    if os.name != "nt":
        raise RuntimeError("Windows account encryption requires Windows")
    class Blob(ctypes.Structure):
        _fields_ = [("size", wintypes.DWORD), ("data", ctypes.POINTER(ctypes.c_ubyte))]
    crypt = ctypes.WinDLL("crypt32", use_last_error=True)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    kernel.LocalFree.restype = ctypes.c_void_p
    buffer = ctypes.create_string_buffer(data)
    source = Blob(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
    target = Blob()
    function = crypt.CryptUnprotectData if decrypt else crypt.CryptProtectData
    function.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
    function.restype = wintypes.BOOL
    # UI_FORBIDDEN, current-user scope, and no optional entropy masquerading as a key.
    if not function(ctypes.byref(source), None, None, None, None, 1, ctypes.byref(target)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        return ctypes.string_at(target.data, target.size)
    finally:
        kernel.LocalFree(target.data)


class ProtectedDatabase:
    """SQLite in RAM; periodic streamed, authenticated, atomic checkpoints on disk.

    A process killed during inference can lose at most the uncheckpointed changes.
    The next metadata reconciliation recovers them. Source documents are untouched.
    """
    def __init__(self, path):
        import msvcrt
        self.path = Path(str(path) + ".dpapi")
        self.lock = threading.RLock()
        self.flush_lock = threading.Lock()
        self.stopping = threading.Event()
        self.file_lock = self.path.with_suffix(".lock").open("a+b")
        self.file_lock.seek(0)
        if not self.file_lock.read(1):
            self.file_lock.write(b"0"); self.file_lock.flush()
        self.file_lock.seek(0)
        try:
            msvcrt.locking(self.file_lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError:
            self.file_lock.close()
            raise RuntimeError("This index is already open. Close AI Center before CLI indexing or benchmarking.")
        self.db = sqlite3.connect(":memory:", check_same_thread=False)
        self.db.row_factory = sqlite3.Row
        self.generation = -1
        self.legacy = Path(path) if Path(path).exists() else None
        try:
            if self.path.exists():
                restored = sqlite3.connect(':memory:')
                try:
                    with self.path.open('rb') as stream:
                        current = stream.read(len(CHUNKED_MAGIC)) == CHUNKED_MAGIC
                        stream.seek(0)
                        if current:
                            restore_database(stream, restored)
                        else:
                            plain = bytearray(unseal(stream.read()))
                            plain[18:20] = b"\x01\x01"
                            restored.deserialize(plain)
                            del plain
                    # Backup also removes the deserialize pager's growth ceiling and
                    # reloads FTS virtual-table schemas after their shadow data restores.
                    restored.backup(self.db)
                finally:
                    restored.close()
                self.generation = self.db.total_changes if current else -1
            elif self.legacy:
                source = sqlite3.connect(f"file:{self.legacy.resolve().as_posix()}?mode=ro", uri=True)
                try:
                    source.backup(self.db)
                finally:
                    # A SQLite transaction context does not close its Windows handle.
                    source.close()
            self.db.execute("PRAGMA journal_mode=MEMORY")
            self.db.execute("PRAGMA temp_store=MEMORY")
            self.db.execute("PRAGMA foreign_keys=ON")
            # Snapshot saves stream bounded rows instead of allocating the entire
            # index through SQLite's approximately 2 GiB single-allocation API.
            self.db.execute("PRAGMA max_page_count=4294967294")
            self.thread = threading.Thread(target=self._checkpoint, daemon=True, name="protected-index-checkpoint")
            self.thread.start()
        except Exception:
            self.db.close(); self.file_lock.close(); raise

    def flush(self, force=False):
        with self.flush_lock:
            snapshot = sqlite3.connect(':memory:')
            try:
                with self.lock:
                    generation = self.db.total_changes
                    if not force and generation == self.generation:
                        return
                    # Copy stable pages in RAM. Compression/encryption then runs
                    # outside the live lock, so queries and indexing can continue.
                    self.db.backup(snapshot)
                temporary = self.path.with_suffix(".pending")
                with temporary.open("wb") as stream:
                    write_database(stream, snapshot)
                    stream.flush(); os.fsync(stream.fileno())
                temporary.replace(self.path)
                self.generation = generation
            finally:
                snapshot.close()

    def finish_migration(self):
        if not self.legacy:
            return
        self.flush(force=True)
        # Verify authenticated round-trip before moving any old index files.
        import hashlib
        def fingerprint(database):
            digest = hashlib.sha256()
            for statement in database.iterdump():
                digest.update(statement.encode('utf-8'))
                digest.update(b'\n')
            return digest.digest()
        restored = sqlite3.connect(':memory:')
        verified = sqlite3.connect(':memory:')
        try:
            with self.path.open('rb') as stream:
                restore_database(stream, restored)
            restored.backup(verified)
            if fingerprint(verified) != fingerprint(self.db):
                raise ValueError("Protected index verification failed")
        finally:
            restored.close(); verified.close()
        archive = self.legacy.parent.parent / "legacy-index-backups" / time.strftime("%Y%m%dT%H%M%S")
        archive.mkdir(parents=True, exist_ok=False)
        for suffix in ("", "-wal", "-shm"):
            original = Path(str(self.legacy) + suffix)
            if original.exists():
                raw = original.read_bytes()
                encrypted = seal(raw)
                if unseal(encrypted) != raw:
                    raise ValueError('Legacy backup encryption verification failed')
                temporary = Path(str(original) + '.encrypted-pending')
                with temporary.open('wb') as stream:
                    stream.write(encrypted); stream.flush(); os.fsync(stream.fileno())
                temporary.replace(original)
                original.rename(archive / (original.name + '.dpapi'))
        self.legacy = None

    def _checkpoint(self):
        while not self.stopping.wait(15):
            try:
                self.flush()
            except Exception:
                # Never silently claim an encrypted checkpoint succeeded.
                import logging
                logging.getLogger(__name__).exception("Protected index checkpoint failed")

    def close(self):
        self.stopping.set(); self.thread.join()
        try:
            self.flush()
        finally:
            self.db.close(); self.file_lock.close()
