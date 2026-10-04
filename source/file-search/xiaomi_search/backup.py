"""Consistent index backups without loading the encrypted file into memory.

Dependencies: Python stdlib and the existing Store. Outputs: a new dated folder
with the index, SHA256 manifest and recovery instructions. Called by Service's
local_backup_index RPC; launch with Launch AI Center.cmd, then Search settings.
"""
import hashlib
import contextlib
import json
import os
import sqlite3
from datetime import datetime, timezone
from pathlib import Path


def create_backup(store, parent, config, progress=lambda phase: None):
    """Save a complete checkpoint to a new folder; publish manifest last.

    The existing encrypted checkpoint lock prevents replacement during streaming
    copy. Indexing continues in RAM. Plain SQLite uses its online backup API.
    Windows-protected backups require the original Windows profile keys.
    A folder without manifest.json is incomplete and must not be restored.
    """
    parent = Path(parent).resolve(strict=True)
    if not parent.is_dir():
        raise ValueError('Choose an existing backup folder')
    data = store.path.parent.resolve()
    if parent == data or data in parent.parents:
        raise ValueError('Choose a backup folder outside AI Center application data')
    created = datetime.now(timezone.utc)
    folder = parent / ('AI-Center-Index-' + created.strftime('%Y%m%dT%H%M%S%fZ'))
    folder.mkdir(exist_ok=False)
    protected = store.protected
    filename = 'index.sqlite3.dpapi' if protected else 'index.sqlite3'
    target = folder / filename
    progress('Saving checkpoint')
    if protected:
        protected.flush()
        progress('Copying encrypted index')
        with protected.flush_lock:
            with protected.path.open('rb') as source, target.open('xb') as output:
                while block := source.read(1024 * 1024):
                    output.write(block)
                output.flush()
                os.fsync(output.fileno())
    else:
        progress('Copying index')
        with store.connect() as source, contextlib.closing(sqlite3.connect(target)) as destination:
            source.backup(destination)
    progress('Verifying backup')
    with target.open('rb') as stream:
        checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
    manifest = {
        'schema': 1, 'created_utc': created.isoformat(), 'index': filename,
        'bytes': target.stat().st_size, 'sha256': checksum,
        'source_index': str(protected.path if protected else store.path),
        'protection': 'windows-profile' if protected else 'none',
        'configuration': config,
        'portable': False if protected else True,
    }
    (folder / 'RECOVERY.txt').write_text(
        'This backup contains indexed metadata, extracted text and embeddings.\n'
        'It does not include your original documents or embedding model weights.\n'
        'manifest.json is written only after the index copy succeeds. A folder\n'
        'without this manifest is incomplete. Verify the index SHA256 first.\n\n'
        'Encrypted backups need the original Windows profile protection keys.\n'
        'Another device or a reinstalled Windows profile usually cannot decrypt\n'
        'them. File paths and the compatible embedding model must also match.\n\n'
        'Recovery on this Windows profile:\n'
        '1. Quit PC Manager and AI Center from their tray menus.\n'
        '2. Preserve a copy of the current index and configuration.\n'
        '3. Copy the backup index to source_index from manifest.json, replacing\n'
        '   that file only while AI Center is fully stopped.\n'
        '4. Start AI Center. Retain matching include/exclude settings and model.\n'
        '   Original documents remain untouched.\n', encoding='utf-8')
    # Exclusive creation and last publication distinguish complete from partial
    # backups if the process exits, storage fills up, or copying fails.
    pending = folder / 'manifest.pending'
    with pending.open('x', encoding='utf-8') as stream:
        json.dump(manifest, stream, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    pending.rename(folder / 'manifest.json')
    return {'path': str(folder), 'bytes': manifest['bytes'], 'sha256': checksum}
