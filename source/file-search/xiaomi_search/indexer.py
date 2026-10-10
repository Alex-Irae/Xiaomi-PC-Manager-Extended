"""Read-only crawl and debounced Windows filesystem notifications."""
import ctypes
import hashlib
import json
import logging
import os
import stat
import threading
import time
from pathlib import Path

from .extract import CHUNK_VERSION, EXTRACTOR_VERSION, SUPPORTED, chunks
from .exclusions import canonical_path, is_within
from .protection import IndexCapacityError

LOG = logging.getLogger(__name__)
# Executables are listed by name only (never read), so they can be found and launched.
CATCH_UP_PASSAGES = 5000  # fewer waiting passages do not justify loading the model during a walk
SKIP_EXTENSIONS = {".sys", ".safetensors", ".gguf", ".bin", ".onnx", ".pyc", ".pyo", ".pth", ".pt"}


def on_battery():
    if os.name != "nt":
        return False

    class Power(ctypes.Structure):
        _fields_ = [("ac", ctypes.c_ubyte), ("flags", ctypes.c_ubyte), ("percent", ctypes.c_ubyte), ("reserved", ctypes.c_ubyte), ("seconds", ctypes.c_ulong), ("full_seconds", ctypes.c_ulong)]

    power = Power()
    return bool(ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(power)) and power.ac == 0)


class Indexer:
    def __init__(self, config, store, embedder, data, notify=lambda: None):
        self.config, self.store, self.embedder = config, store, embedder
        self.data = Path(data).resolve()
        self.roots = [Path(p).resolve() for p in config["roots"]]
        self.notify = notify
        self.condition = threading.Condition()
        self.jobs = {}
        self.vector_jobs = set()
        self.catching_up = False  # a walk of the folders is embedding what it found, whatever the standby choice
        self.catch_up_requested = False
        self.running = False
        self.busy = False
        self.current = ""
        self.phase = 'Idle'
        self.scanning = None
        self.internal_files = set()
        self.processed = 0
        self.scan_error = None
        self.semantic_error = None
        self.observer = None
        self.thread = None
        self.mode = config["indexing_mode"]
        self.refresh_exclusions = lambda: None
        self.exclusions_checked = 0.0
        self._ancestors = {}  # folder -> (expiry, no reparse/super-hidden ancestor)
        self.extraction_key = json.dumps({"extractor": EXTRACTOR_VERSION, "chunker": CHUNK_VERSION, "characters": config["chunk_characters"], "overlap": config["overlap_characters"], "maximum": config["max_extracted_characters"], "max_chunks": config["max_chunks_per_file"]}, sort_keys=True)

    def allowed(self, path):
        """Reject paths outside opted-in roots, excluded ancestors, and reparse points."""
        try:
            if time.monotonic() - self.exclusions_checked >= 1:
                self.refresh_exclusions()
                self.exclusions_checked = time.monotonic()
            original = Path(path).absolute()
            path = original.resolve()
            if str(path) in self.internal_files:
                return False
            if any(is_within(path, folder) for folder in self.config["excluded_folders"]):
                return False
            if not path.is_dir() and path.suffix.lower() in self.config["excluded_extensions"]:
                return False
            if path.is_relative_to(self.data) or path.suffix.lower() in SKIP_EXTENSIONS:
                return False
            exclusions = {p.casefold() for p in self.config["excluded_names"]}
            if not any(path.is_relative_to(root) and not any(p.casefold() in exclusions for p in path.relative_to(root).parts) for root in self.roots):
                return False
            # Never follow junctions/symlinks or hydrate cloud placeholders.
            # Siblings share every ancestor, so the folder verdict is reused briefly
            # instead of re-reading each parent's attributes for every file.
            now = time.monotonic()
            cached = self._ancestors.get(original.parent)
            if cached is None or cached[0] < now:
                if len(self._ancestors) > 50000:
                    self._ancestors.clear()
                cached = (now + 30, self._plain(original.parent, *original.parent.parents))
                self._ancestors[original.parent] = cached
            return cached[1] and self._plain(original)
        except (OSError, ValueError):
            return False

    def _plain(self, *parts):
        """True when no given path is a reparse point or a hidden system entry."""
        for part in parts:
            try:
                information = part.lstat()
            except FileNotFoundError:
                continue  # Deleted paths still need missing-file reconciliation.
            attributes = getattr(information, 'st_file_attributes', 0)
            if stat.S_ISLNK(information.st_mode) or attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                return False
            # Windows marks the drive root hidden/system. It must not
            # exclude every descendant when the user explicitly selects C:\.
            if part != Path(part.anchor) and any(part.is_relative_to(root) for root in self.roots) and attributes & stat.FILE_ATTRIBUTE_HIDDEN and attributes & stat.FILE_ATTRIBUTE_SYSTEM:
                return False
        return True

    def enqueue(self, path, delay=0.8):
        if not self.allowed(path):
            return
        with self.condition:
            # An explicit scan already covers directory notifications beneath it.
            # Individual file notifications remain queued to cover concurrent edits.
            if self.scanning and Path(path).is_dir() and Path(path).is_relative_to(self.scanning):
                return
            self.jobs[str(Path(path).absolute())] = time.monotonic() + delay
            self.condition.notify_all()

    def request_scan(self, explicit=True):
        """Queue a walk of every root. `explicit` (Index now, a settings change) embeds whatever it finds;
        the walk at start and the daily one decide by how much is waiting (see _scan)."""
        self.catch_up_requested = self.catch_up_requested or explicit
        self.refresh_exclusions()
        self.semantic_error = None
        self.embedder.failed_devices.clear()
        for root in self.roots:
            self.enqueue(root, delay=0)
        self.notify()

    def start(self, watch=True):
        if self.running:
            return
        self.running = True
        # Startup must not delay the bridge by validating hundreds of thousands of
        # old paths. Search filters apply immediately; scans reconcile metadata.
        self.configure_watch(watch)
        # Visits a previous worker had put off (see reread_wait) are queued again; each waits out its own time.
        for path in self.store.stale_files():
            self.enqueue(path)
        self.thread = threading.Thread(target=self._work, name="local-indexer", daemon=True)
        self.thread.start()

    def configure_watch(self, watch):
        """Update notifications without starting a scan or changing its queue."""
        if not watch or not self.roots:
            if self.observer:
                self.observer.stop()
                self.observer.join(timeout=5)
                self.observer = None
            return
        if watch and self.roots:
            from watchdog.events import FileSystemEventHandler
            from watchdog.observers import Observer
            owner = self

            class Changes(FileSystemEventHandler):
                def on_any_event(self, event):
                    if event.event_type not in ("created", "modified", "deleted", "moved"):
                        return
                    # Directory modified notifications must not trigger recurring tree crawls.
                    if event.is_directory and event.event_type == "modified":
                        return
                    owner.enqueue(event.src_path)
                    destination = getattr(event, "dest_path", None)
                    if destination:
                        owner.enqueue(destination)

            if self.observer:
                self.observer.unschedule_all()
            else:
                self.observer = Observer()
            for root in self.roots:
                if self.allowed(root):
                    self.observer.schedule(Changes(), str(root), recursive=True)
            if not self.observer.is_alive():
                self.observer.start()

    def set_mode(self, mode):
        if mode not in ("normal", "battery_saver", "paused"):
            raise ValueError("Unknown indexing mode")
        with self.condition:
            self.mode = mode
            self.condition.notify_all()
        self.notify()

    def stop(self):
        with self.condition:
            self.running = False
            self.condition.notify_all()
        if self.observer:
            self.observer.stop()
            self.observer.join(timeout=5)
            self.observer = None
        if self.thread:
            self.thread.join(timeout=5)

    def _settled(self):
        """Files a walk need not look at again while their date, size and creation time are unchanged:
        {path: (mtime_ns, size, ctime_ns)} and, for folders, {path: mtime_ns}. Empty when the stored
        passages may belong to another embedding model, so that each file is examined as before."""
        if self.config["semantic_enabled"]:
            try:
                if self.store.embedding_model() not in (None, self.embedder.identity()):
                    return {}, {}
            except (OSError, ValueError):
                return {}, {}
        name_only = set(self.config["name_only_extensions"])
        limit = self.config["max_file_mb"] * 1024 * 1024
        files, folders = {}, {}
        for path, mtime_ns, size, ctime_ns, extension, status, kind in self.store.settled_rows(self.extraction_key):
            if kind == 2:
                folders[path] = mtime_ns
            elif extension in name_only:
                if status == "metadata_only":
                    files[path] = (mtime_ns, size, ctime_ns)
            elif not (status == "metadata_only" and extension in SUPPORTED and size <= limit):
                files[path] = (mtime_ns, size, ctime_ns)
        return files, folders

    def _scan(self, root):
        """Discover, extract and embed progressively without writing source files.

        The folders are listed once each. Windows hands back every entry's dates, size and attributes with
        the listing, so a file that has not changed costs one dictionary lookup: no call about the file
        itself and nothing opened. Before, each file was resolved three times (which opens it) and asked
        about a dozen times, 293 files a second; a whole drive took from ten minutes to most of an hour.
        """
        def failure(exc):
            self.scan_error = str(exc)
            LOG.warning("Folder access error: %s", exc)

        root = Path(root)
        if not self.allowed(root):  # also re-reads the exclusion lists
            return
        files, folders = self._settled()
        names = {name.casefold() for name in self.config["excluded_names"]}
        types = set(self.config["excluded_extensions"]) | SKIP_EXTENSIONS
        barred = {canonical_path(folder) for folder in self.config["excluded_folders"]} | {canonical_path(self.data)}
        link, hidden = stat.FILE_ATTRIBUTE_REPARSE_POINT, stat.FILE_ATTRIBUTE_HIDDEN | stat.FILE_ATTRIBUTE_SYSTEM
        seen = set()
        discovered = 0
        pending = [root]
        while pending:
            folder = pending.pop()
            if not self.running:
                return
            with self.condition:
                while self.running and self.mode == "paused":
                    self.condition.wait()
            seen.add(str(folder))
            try:
                information = folder.stat()
                if folders.get(str(folder)) != information.st_mtime_ns:
                    row = self.store.discover(folder, information)
                    if row['status'] != 'metadata_only' or row['mtime_ns'] != information.st_mtime_ns:
                        self.store.save(row["id"], information, "", [], self.extraction_key, status="metadata_only")
                with os.scandir(folder) as listing:
                    entries = list(listing)
            except OSError as exc:
                failure(exc)
                continue
            for entry in entries:
                if not self.running:
                    return
                try:
                    information = entry.stat(follow_symlinks=False)
                    attributes = information.st_file_attributes
                    # Never follow junctions or symbolic links, hydrate cloud placeholders or enter protected system entries.
                    if entry.is_symlink() or attributes & link or attributes & hidden == hidden or entry.name.casefold() in names:
                        continue
                    if entry.is_dir(follow_symlinks=False):
                        if canonical_path(entry.path) not in barred:
                            pending.append(Path(entry.path))
                        continue
                    if os.path.splitext(entry.name)[1].lower() in types or entry.path in self.internal_files:
                        continue
                    if files.get(entry.path) == (information.st_mtime_ns, information.st_size, information.st_ctime_ns):
                        seen.add(entry.path)
                    else:
                        with self.condition:
                            while self.running and self.mode == 'paused':
                                self.condition.wait()
                        path = Path(entry.path)
                        if not self.allowed(path):
                            continue
                        self.phase = 'Extracting'
                        self.current = entry.path
                        self._file(path)
                        seen.add(entry.path)
                        # Interleave meanings during discovery rather than waiting for
                        # every file on the drive to be crawled and extracted first.
                        self._embedding_step()
                    discovered += 1
                    self.processed += 1
                    if discovered % 10000 == 0:
                        LOG.info('Discovered %d files; filenames already searchable', discovered)
                        self.notify()
                except OSError as exc:
                    failure(exc)
        # Reconcile missing entries after explicit/initial scans, including changes
        # made while this application was stopped. Permission errors never tombstone.
        # Only entries beneath the scanned folder that this walk did not meet are checked.
        prefix = str(root).rstrip("\\/")
        with self.store.connect() as db:
            paths = [r[0] for r in db.execute("SELECT path FROM files WHERE active=1 AND (lower(path)=lower(?) OR lower(substr(path,1,?))=lower(?))", (prefix, len(prefix) + 1, prefix + "\\"))]
        for name in paths:
            if not self.running:
                return
            if name in seen:
                continue
            try:
                Path(name).stat()
            except FileNotFoundError:
                self.store.mark_missing(Path(name))
            except OSError:
                pass
        if root in self.roots:
            # Scope changes are reconciled on whole-root scans; queries already filter them.
            # What this walk met is allowed by construction; only the rest is tested one by one.
            self.store.retain_roots(self.roots, lambda candidate: str(candidate) in seen or self.allowed(candidate))
        # Passages still without a vector are queued once here: unchanged files are no longer visited.
        if self.config["semantic_enabled"]:
            self.vector_jobs.update(self.store.pending_vector_files())
            # With "free when idle" the model is loaded for a walk only when it was asked for ("Index now",
            # a settings change) or when a real amount of new text was found. The few hundred passages that
            # application state files leave behind wait for the next search.
            if not self.catching_up and self.store.pending_vector_passages() >= CATCH_UP_PASSAGES:
                self.catching_up = True
        LOG.info("Discovery complete: %d files", discovered)

    def _file(self, path):
        if not self.allowed(path):
            return
        try:
            information = path.stat()
        except FileNotFoundError:
            self.store.mark_missing(path)
            return
        row = self.store.discover(path, information)
        # Types found by name only (source code by default) are never read: their passages made up five
        # sixths of a whole-drive index and of the embedding work. A file indexed before the rule loses them here.
        name_only = path.suffix.lower() in self.config["name_only_extensions"]
        changed = row["mtime_ns"] != information.st_mtime_ns or row["size"] != information.st_size or row['ctime_ns'] != information.st_ctime_ns or row["status"] == "pending" or (row["status"] == "error" and (row["error"] or "").startswith(("[Errno", "[WinError"))) or (row["status"] != "error" and row["extraction_key"] != self.extraction_key) or (name_only and row["status"] != "metadata_only")
        # A readable file that was left as a name only, and whose type is no longer on that list, is read now.
        changed = changed or (not name_only and row["status"] == "metadata_only" and path.suffix.lower() in SUPPORTED and information.st_size <= self.config["max_file_mb"] * 1024 * 1024)
        if changed and not name_only and row["status"] in ("indexed", "error"):
            # A file that keeps changing (a log that grows all day) would have every passage embedded again at
            # each change. It is read again only after a wait that grows with its size; until then its stored
            # passages stay searchable and the visit is queued for the end of the wait.
            # A file that could not be read (too long, locked) is tried again after an hour, not at each change.
            wait = self.store.reread_wait(row["id"], self.config["reread_seconds_per_passage"], 3600 if row["status"] == "error" and self.config["reread_seconds_per_passage"] else 0)
            if wait > 0:
                self.store.mark_stale(row["id"])  # a later worker picks the visit up again
                self.enqueue(path, delay=wait)
                changed = False
        if changed:
            try:
                if name_only or information.st_size > self.config["max_file_mb"] * 1024 * 1024 or path.suffix.lower() not in SUPPORTED:
                    self.store.save(row["id"], information, "", [], self.extraction_key, status="metadata_only")
                    return
                with path.open("rb") as stream:
                    digest = hashlib.file_digest(stream, "sha256").hexdigest()
                if digest != row["digest"] or row["extraction_key"] != self.extraction_key or row["status"] == "error":
                    parts = chunks(path, self.config)
                    after = path.stat()
                    if (after.st_mtime_ns, after.st_size) != (information.st_mtime_ns, information.st_size):
                        self.enqueue(path)
                        return
                    status = "indexed" if parts else "empty_text"
                    self.store.save(row["id"], information, digest, parts, self.extraction_key, status=status)
                else:
                    with self.store.connect() as db:
                        db.execute("UPDATE files SET mtime_ns=?,size=?,ctime_ns=? WHERE id=?", (information.st_mtime_ns, information.st_size, information.st_ctime_ns, row["id"]))
            except IndexCapacityError:
                raise
            except Exception as exc:
                self.store.record_error(row["id"], exc, information)
                LOG.warning("Extraction failed: %s: %s", path, exc)
                return
        if not changed and row.get("stale"):
            self.store.clear_stale(row["id"])
        if self.config["semantic_enabled"] and path.suffix.lower() not in self.config["content_only_extensions"] and self.store.get_file(row['id'])['status'] == 'indexed':
            try:
                # Unchanged, fully embedded files must not compile/load the model
                # again on every scheduled snapshot check.
                if self.store.pending_vectors(row['id'], self.embedder.identity(), limit=1):
                    self.vector_jobs.add(row['id'])
            except Exception as exc:
                self.semantic_error = str(exc)

    def _vectors(self, fid):
        row = self.store.get_file(fid)
        if row is None or row["status"] != "indexed" or row["extension"] in self.config["content_only_extensions"] or not self.allowed(Path(row["path"])):
            return
        try:
            model_id = self.embedder.identity()
            pending = self.store.pending_vectors(fid, model_id, limit=9)
            ready = []
            try:
                for chunk in pending[:8]:
                    if not self.running or self.mode == "paused" or not self.config["semantic_enabled"] or (self.mode == "battery_saver" and on_battery()):
                        self.vector_jobs.add(fid)
                        return
                    began = time.monotonic()
                    vector = self.embedder.encode([chunk["text"]])[0]  # shape: [D]
                    # Indexing load limit: rest after each passage so the embedding device is
                    # busy for only the chosen share of the time (50% rests as long as it worked).
                    # Searches are never slowed; this loop only fills the index.
                    share = int(self.config["indexing_load"])
                    if share < 100:
                        time.sleep((time.monotonic() - began) * (100 - share) / share)
                    if self.embedder.identity() != model_id:
                        raise ValueError("Model files changed during indexing. Restart semantic backfill")
                    ready.append((chunk["id"], vector))
            finally:
                # One transaction per step, including vectors finished before a pause.
                self.store.put_vectors(ready, model_id)
            if len(pending) > 8:
                self.vector_jobs.add(fid)
        except IndexCapacityError:
            raise
        except Exception as exc:
            # Lexical search stays usable; one broken model cannot fail every file.
            self.semantic_error = str(exc)
            LOG.warning("Semantic backfill paused: %s", exc)

    def _may_embed(self):
        """With "free when idle" the model is never loaded for background work alone: files that keep
        changing (application state, logs) would bring it back every few minutes. Their passages wait until
        a search has loaded the model, or until a walk of the folders (start, "Index now") is catching up."""
        return self.config["model_standby"] != "free_idle" or getattr(self.embedder, "pipeline", None) is not None or self.catching_up

    def resume_embedding(self):
        """The model has just been loaded for a search: passages that waited for it are embedded now."""
        self.vector_jobs.update(self.store.pending_vector_files())
        with self.condition:
            self.condition.notify_all()

    def _embedding_step(self):
        if self.vector_jobs and self._may_embed() and self.config['semantic_enabled'] and not self.semantic_error and self.mode != 'paused' and not (self.mode == 'battery_saver' and on_battery()):
            fid = self.vector_jobs.pop()
            self.phase = 'Embedding'
            self._vectors(fid)
            self.notify()

    def _work(self):
        while True:
            with self.condition:
                while self.running:
                    now = time.monotonic()
                    ready = next((p for p, deadline in self.jobs.items() if deadline <= now), None)
                    if not self.vector_jobs and not self.scanning:
                        self.catching_up = False
                    can_embed = bool(self.vector_jobs) and self._may_embed() and self.config["semantic_enabled"] and not self.semantic_error and not (self.mode == "battery_saver" and on_battery())
                    if self.mode != "paused" and (ready or can_embed):
                        break
                    wait = None
                    if self.mode != "paused" and self.jobs:
                        wait = max(0.05, min(self.jobs.values()) - now)
                    if self.mode == "battery_saver" and self.vector_jobs and self.config["semantic_enabled"] and not self.semantic_error:
                        wait = min(wait, 60) if wait is not None else 60
                    self.condition.wait(timeout=wait)
                if not self.running:
                    return
                if ready:
                    del self.jobs[ready]
                else:
                    fid = self.vector_jobs.pop()
                self.busy = True
            try:
                if ready:
                    path = Path(ready)
                    self.current = str(path)
                    if path.is_dir():
                        self.scanning = path
                        self.catching_up, self.catch_up_requested = self.catch_up_requested, False
                        try:
                            self._scan(path)
                        finally:
                            self.scanning = None
                    else:
                        self.phase = 'Extracting'
                        self._file(path)
                        if self.processed % 8 == 0:
                            self._embedding_step()
                else:
                    self.phase = 'Embedding'
                    self.current = f"Embedding file {fid}"
                    self._vectors(fid)
                self.processed += 1
                # One line per file put hundreds of thousands of lines a day into two logs; the status call carries live progress.
                if self.processed % 250 == 0 or not (self.jobs or self.vector_jobs):
                    LOG.info("Processed %d | files queued %d | embedding queued %d", self.processed, len(self.jobs), len(self.vector_jobs))
            except IndexCapacityError as exc:
                self.scan_error = str(exc)
                self.set_mode('paused')
                LOG.error("%s", exc)
            except Exception as exc:
                self.scan_error = str(exc)
                LOG.exception("Indexing operation failed; continuing")
            finally:
                with self.condition:
                    self.busy = False
                    self.condition.notify_all()
                if self.embedder is not None:self.embedder.schedule_release()
                self.notify()

    def wait_complete(self):
        """Used only by an explicit CLI index command; semantic failures terminate cleanly."""
        with self.condition:
            # Visits put off for hours and passages waiting for the model are not work in progress: counting
            # them left "Index now" and the daily request unanswered for as long as any file kept changing.
            while self.running and (any(deadline <= time.monotonic() + 60 for deadline in self.jobs.values()) or self.busy or (self.vector_jobs and self._may_embed() and self.config['semantic_enabled'] and not self.semantic_error)):
                if self.mode == 'paused':
                    return
                self.condition.wait(timeout=1)

    def status(self):
        with self.condition:
            return {"mode": self.mode, "busy": self.busy, "phase": self.phase if self.busy else 'Idle', "current": self.current, "processed": self.processed, "scanning": bool(self.scanning), "catching_up": self.catching_up, "queued_files": sum(1 for deadline in self.jobs.values() if deadline <= time.monotonic() + 60), "queued_embedding_files": len(self.vector_jobs), "scan_error": self.scan_error, "semantic_error": self.semantic_error}
