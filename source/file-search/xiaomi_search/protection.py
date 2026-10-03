"""Windows-account encrypted SQLite snapshots with no plaintext working file.

Dependencies: Python stdlib, Windows DPAPI. Outputs: .dpapi snapshots and an
exclusive lock file. Command: used by python -m xiaomi_search (no standalone CLI).
RAM is plaintext while unlocked; this cannot protect against code running as you.
"""
import ctypes
import os
import sqlite3
import threading
import time
from ctypes import wintypes
from pathlib import Path

MAGIC = b"XIAISEARCH-DPAPI-1\n"
ENVELOPE_MAGIC = b"XIAISEARCH-AESGCM-2\n"

# SQLite serialize allocates one signed-32-bit-sized buffer. Leave headroom so
# a write cannot grow the RAM database beyond a saveable encrypted checkpoint.
# ponytail: whole-snapshot ceiling; segmented encrypted checkpoints are needed above 2 GiB.
SNAPSHOT_MAX_BYTES = 2**31 - 256 * 1024


class IndexCapacityError(RuntimeError):
    """A protected index reached the current snapshot format's size limit."""


def capacity_error():
    return IndexCapacityError(
        "Indexing paused: the encrypted index reached its approximately 2 GiB "
        "snapshot capacity. Existing search results remain available. Reduce the "
        "indexed scope before rebuilding into a separate data directory."
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
    if payload.startswith(MAGIC):return protect(payload[len(MAGIC):],decrypt=True)
    if not payload.startswith(ENVELOPE_MAGIC):raise ValueError('Unknown protected index format')
    begin=len(ENVELOPE_MAGIC);length=int.from_bytes(payload[begin:begin+4],'little');begin+=4
    if not 32<=length<=65536 or len(payload)<begin+length+28:raise ValueError('Truncated protected index')
    key=protect(payload[begin:begin+length],decrypt=True);begin+=length
    plain,_=aes_gcm(payload[begin+28:],key,payload[begin:begin+12],payload[begin+12:begin+28],ENVELOPE_MAGIC)
    return plain


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
    """Serialized SQLite in RAM; periodic authenticated, atomic checkpoints on disk.

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
                payload = self.path.read_bytes()
                plain = bytearray(unseal(payload))
                # A WAL file header cannot be reopened without its sidecar in RAM.
                plain[18:20] = b"\x01\x01"
                restored = sqlite3.connect(":memory:")
                try:
                    restored.deserialize(plain)
                    # Deserialize uses SQLite's capped memdb pager. Backup into an ordinary
                    # RAM database so the encrypted index can grow after a reload.
                    restored.backup(self.db)
                finally:
                    restored.close()
                self.generation = self.db.total_changes if payload.startswith(ENVELOPE_MAGIC) else -1
                del plain, payload
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
            page_size = self.db.execute("PRAGMA page_size").fetchone()[0]
            self.db.execute(f"PRAGMA max_page_count={SNAPSHOT_MAX_BYTES // page_size}")
            self.thread = threading.Thread(target=self._checkpoint, daemon=True, name="protected-index-checkpoint")
            self.thread.start()
        except Exception:
            self.db.close(); self.file_lock.close(); raise

    def flush(self, force=False):
        with self.flush_lock:
            with self.lock:
                generation = self.db.total_changes
                if not force and generation == self.generation:
                    return
                plain = self.db.serialize()
            encrypted = seal(plain)
            temporary = self.path.with_suffix(".pending")
            with temporary.open("wb") as stream:
                stream.write(encrypted); stream.flush(); os.fsync(stream.fileno())
            temporary.replace(self.path)
            self.generation = generation

    def finish_migration(self):
        if not self.legacy:
            return
        self.flush(force=True)
        # Verify authenticated round-trip before moving any old index files.
        if unseal(self.path.read_bytes()) != self.db.serialize():
            raise ValueError("Protected index verification failed")
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
