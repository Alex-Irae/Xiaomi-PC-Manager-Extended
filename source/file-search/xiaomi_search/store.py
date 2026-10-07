"""SQLite metadata, FTS5, persistent embeddings, and hybrid retrieval."""
import contextlib
import html
import json
import re
import sqlite3
import threading
import time
import unicodedata
from datetime import datetime
from pathlib import Path
from .exclusions import is_within

# Leading dimensions of each passage vector kept in RAM for the first-pass scan.
# Qwen3 embeddings are Matryoshka-trained, so a renormalized prefix preserves ranking
# closely; the exact full vector still decides the final order. Calibrate with
# the prefix-quality measurement described in BENCHMARKS.md (run 004).
COARSE_DIMENSION = 256
COARSE_CANDIDATES = 2000
FTS_CANDIDATES = 3000  # best full-text matches kept before scope filters
COMBINING = re.compile('[\u0300-\u036f\u1ab0-\u1aff\u1dc0-\u1dff\u20d0-\u20ff\ufe20-\ufe2f]')  # combining accent blocks

SCHEMA = """
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS files(
 id INTEGER PRIMARY KEY, path TEXT UNIQUE NOT NULL, name TEXT NOT NULL,
 extension TEXT NOT NULL, file_type INTEGER NOT NULL, size INTEGER NOT NULL,
 mtime_ns INTEGER NOT NULL, digest TEXT NOT NULL DEFAULT '',
 status TEXT NOT NULL DEFAULT 'pending', error TEXT, active INTEGER NOT NULL DEFAULT 1,
 extraction_key TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS chunks(
 id INTEGER PRIMARY KEY, file_id INTEGER NOT NULL REFERENCES files(id),
 location TEXT NOT NULL, text TEXT NOT NULL, vector BLOB, model_id TEXT, dimension INTEGER);
CREATE INDEX IF NOT EXISTS chunks_file ON chunks(file_id);
CREATE VIRTUAL TABLE IF NOT EXISTS file_fts USING fts5(name,path,tokenize='unicode61 remove_diacritics 2');
CREATE VIRTUAL TABLE IF NOT EXISTS chunk_fts USING fts5(text,tokenize='unicode61 remove_diacritics 2');
CREATE TRIGGER IF NOT EXISTS chunks_delete AFTER DELETE ON chunks BEGIN
 DELETE FROM chunk_fts WHERE rowid=old.id;
END;
CREATE TABLE IF NOT EXISTS vectors(
 slot INTEGER PRIMARY KEY AUTOINCREMENT, chunk_id INTEGER NOT NULL,
 model_id TEXT NOT NULL, coarse BLOB NOT NULL);
CREATE INDEX IF NOT EXISTS vectors_chunk ON vectors(chunk_id);
CREATE TRIGGER IF NOT EXISTS chunks_delete_vectors AFTER DELETE ON chunks BEGIN
 DELETE FROM vectors WHERE chunk_id=old.id;
END;
"""


def file_type(path):
    if Path(path).is_dir():
        return 2
    suffix = Path(path).suffix.lower()
    if suffix == ".docx":
        return 4
    if suffix == ".xlsx":
        return 8
    if suffix == ".pptx":
        return 16
    if suffix == ".pdf":
        return 32
    if suffix in (".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tiff", ".heic"):
        return 256
    if suffix in (".mp3", ".wav", ".m4a", ".flac", ".ogg"):
        return 128
    if suffix in (".mp4", ".mkv", ".mov", ".webm", ".avi"):
        return 512
    if suffix in (".zip", ".7z", ".rar", ".gz", ".tar"):
        return 1024
    if suffix in (".exe", ".msi", ".lnk"):
        return 2048  # Programs category, shared with Start apps
    from .extract import SUPPORTED
    return 64 if suffix in SUPPORTED else 1


def highlighted(text, tokens):
    """Escape file text before adding only our own literal highlight tags."""
    # A lone Latin letter ("a" in a sentence query) would light up every such letter.
    tokens = [t for t in tokens if len(t) > 1 or not t.isascii()]
    if not tokens:
        return html.escape(text)
    pattern = re.compile("(" + "|".join(re.escape(t) for t in sorted(set(tokens), key=len, reverse=True)) + ")", re.IGNORECASE)
    return "".join("<b>" + html.escape(part) + "</b>" if i % 2 else html.escape(part) for i, part in enumerate(pattern.split(text)))


def parse_query(query):
    """Extract explicit ext/type/folder/date filters; quote FTS terms separately."""
    filters = {}
    pattern = re.compile(r'\b(ext|type|folder|before|after):("[^"]*"|\S+)', re.I)
    for match in pattern.finditer(query):
        filters[match[1].lower()] = match[2].strip('"')
    text = pattern.sub("", query).strip()
    for key in ("before", "after"):
        if key in filters:
            datetime.strptime(filters[key], "%Y-%m-%d")
    tokens = re.findall(r"[^\W_]+", text, flags=re.UNICODE)[:40]
    return text, filters, tokens


class Store:
    def __init__(self, path, protection='none'):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.protected = None
        self.unencrypted = '' if protection != 'none' else 'Plain on disk was chosen in settings.'
        self._local = threading.local()   # one long-lived on-disk connection per thread
        self._connections = []
        self._connections_lock = threading.Lock()
        self._coarse_lock = threading.Lock()
        self._coarse = None  # (model_id, last_slot, count, chunk ids [capacity], matrix [capacity,C])
        if protection == 'windows':
            from .protection import ProtectedDatabase
            self.protected = ProtectedDatabase(self.path)
        else:
            from .protection import encrypt_folder, export_snapshot
            if protection == 'efs':
                # New files in an EFS folder are encrypted transparently by NTFS for
                # this Windows account: on-disk SQLite, no whole-index copy in RAM.
                try:
                    encrypt_folder(self.path.parent)
                except OSError as error:
                    # Windows Home and non-NTFS volumes have no EFS. Search must still
                    # start; the status and the log say plainly that the index is not encrypted.
                    self.unencrypted = str(error)
            export_snapshot(Path(str(self.path)+'.dpapi'), self.path)
        with self.connect() as db:
            db.execute("PRAGMA journal_mode=WAL")
            db.executescript(SCHEMA)
            columns = {row[1] for row in db.execute('PRAGMA table_info(files)')}
            for name, kind in [('file_key', "TEXT NOT NULL DEFAULT ''"), ('ctime_ns', 'INTEGER NOT NULL DEFAULT 0')]:
                if name not in columns:
                    db.execute(f'ALTER TABLE files ADD COLUMN {name} {kind}')
            # Drive-wide discovery must not scan every stored file for each
            # Windows file-identity lookup used to preserve renamed entries.
            db.execute('CREATE INDEX IF NOT EXISTS files_identity ON files(file_key,file_type)')
            version = db.execute("SELECT value FROM meta WHERE key='schema_version'").fetchone()
            if version and version[0] != "1":
                raise ValueError("Unsupported index schema. Use a new data directory")
            db.execute("INSERT OR IGNORE INTO meta VALUES('schema_version','1')")
            db.execute("INSERT OR IGNORE INTO meta VALUES('revision','0')")
            for table in ("files", "chunks"):
                for operation in ("INSERT", "UPDATE", "DELETE"):
                    db.execute(f"CREATE TRIGGER IF NOT EXISTS revision_{table}_{operation} AFTER {operation} ON {table} BEGIN UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='revision'; END")
        self.excluded_folders = []
        self.excluded_extensions = []
        self.type_weights = {}  # extension -> ranking multiplier, 'default' for the rest; empty means no preference
        self.roots = None  # Standalone Store callers have no configured scope.
        if self.protected:
            self.protected.finish_migration()

    def revision(self):
        with self.connect() as db:
            return int(db.execute("SELECT value FROM meta WHERE key='revision'").fetchone()[0])

    @contextlib.contextmanager
    def connect(self):
        if self.protected:
            try:
                with self.protected.lock:
                    with self.protected.db:
                        self.protected.db.create_function('path_within', 2, is_within, deterministic=True)
                        yield self.protected.db
            except sqlite3.OperationalError as exc:
                if getattr(exc, 'sqlite_errorcode', None) == sqlite3.SQLITE_FULL:
                    from .protection import capacity_error
                    raise capacity_error() from exc
                raise
            return
        db = getattr(self._local, 'db', None)
        if db is None:
            # Each thread keeps its connection instead of reopening the file (and, on an
            # EFS folder, unwrapping its key) for every statement group.
            db = sqlite3.connect(self.path, timeout=15, check_same_thread=False)
            db.create_function('path_within', 2, is_within, deterministic=True)
            db.row_factory = sqlite3.Row
            db.execute("PRAGMA foreign_keys=ON")
            # WAL stays consistent after a crash; only the last commits before a power
            # loss can roll back, and the next scan re-derives them from the files.
            db.execute("PRAGMA synchronous=NORMAL")
            self._local.db = db
            with self._connections_lock:
                self._connections.append(db)
        with db:
            yield db

    def discover(self, path, stat):
        path = Path(path)
        key = f'{stat.st_dev}:{stat.st_ino}' if stat.st_ino else ''
        with self.connect() as db:
            old = db.execute("SELECT * FROM files WHERE path=?", (str(path),)).fetchone()
            if old is None and key:
                renamed = db.execute('SELECT * FROM files WHERE file_key=? AND file_type=?', (key, file_type(path))).fetchone()
                if renamed and not Path(renamed['path']).exists():
                    db.execute('UPDATE files SET path=?,name=?,extension=?,active=1 WHERE id=?', (str(path), path.name, path.suffix.lower(), renamed['id']))
                    db.execute('DELETE FROM file_fts WHERE rowid=?', (renamed['id'],))
                    db.execute('INSERT INTO file_fts(rowid,name,path) VALUES(?,?,?)', (renamed['id'], re.sub(r'[_./\\-]', ' ', path.name), re.sub(r'[_./\\-]', ' ', str(path))))
                    old = db.execute('SELECT * FROM files WHERE id=?', (renamed['id'],)).fetchone()
            db.execute("""INSERT INTO files(path,name,extension,file_type,size,mtime_ns)
                VALUES(?,?,?,?,?,?) ON CONFLICT(path) DO UPDATE SET active=1 WHERE active<>1""",
                (str(path), path.name, path.suffix.lower(), file_type(path), stat.st_size, stat.st_mtime_ns))
            row = db.execute("SELECT * FROM files WHERE path=?", (str(path),)).fetchone()
            if old is None:
                # Split identifier/path punctuation so FTS can find words in source filenames.
                db.execute("INSERT INTO file_fts(rowid,name,path) VALUES(?,?,?)", (row["id"], re.sub(r"[_./\\-]", " ", path.name), re.sub(r"[_./\\-]", " ", str(path))))
            if row['file_key'] != key:
                db.execute('UPDATE files SET file_key=? WHERE id=?', (key, row['id']))
            return dict(row)

    def save(self, file_id, stat, digest, parts, extraction_key, status="indexed", error=None):
        """Atomically replace chunks only after extraction has completed."""
        with self.connect() as db:
            db.execute("DELETE FROM chunks WHERE file_id=?", (file_id,))
            for location, text in parts:
                cursor = db.execute("INSERT INTO chunks(file_id,location,text) VALUES(?,?,?)", (file_id, location, text))
                db.execute("INSERT INTO chunk_fts(rowid,text) VALUES(?,?)", (cursor.lastrowid, text))
            db.execute("UPDATE files SET size=?,mtime_ns=?,ctime_ns=?,digest=?,status=?,error=?,extraction_key=?,active=1 WHERE id=?", (stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns, digest, status, error, extraction_key, file_id))

    def flush(self):
        if self.protected:
            self.protected.flush()

    def close(self):
        if self.protected:
            self.protected.close()
        with self._connections_lock:
            for db in self._connections:
                db.close()
            self._connections.clear()
        self._local = threading.local()

    def reset(self):
        """Archive the current index, then clear only derived data, never originals."""
        archive = self.path.parent / 'index-archives'
        archive.mkdir(exist_ok=True)
        label = datetime.now().strftime('%Y%m%dT%H%M%S%f')
        if self.protected:
            self.flush()
            target = archive / (label + '.dpapi')
            target.write_bytes(self.protected.path.read_bytes())
        else:
            target = archive / (label + '.sqlite3')
            with self.connect() as source, contextlib.closing(sqlite3.connect(target)) as destination:
                source.backup(destination)
        with self.connect() as db:
            db.execute('DELETE FROM chunks'); db.execute('DELETE FROM file_fts'); db.execute('DELETE FROM files')
        with self.connect() as db:
            db.execute('VACUUM')
        self.flush()
        return str(target)

    def compact(self, name_only=()):
        """Remove what the current settings no longer search, then give the disk space back.

        Rows of files that are excluded or gone are deleted (such a file is indexed afresh if it
        returns), and files of the name-only types lose their passages. Run with the app closed:
        the final VACUUM rewrites the whole file. Returns the number of files of each kind.
        """
        marks = ','.join('?' * len(name_only))
        with self.connect() as db:
            gone = [row[0] for row in db.execute('SELECT id FROM files WHERE active=0')]
            named = [row[0] for row in db.execute(f"SELECT id FROM files WHERE active=1 AND status<>'metadata_only' AND extension IN ({marks})", list(name_only))] if name_only else []
        # Small transactions: one over two million passages would grow the journal by gigabytes.
        for ids, removed in ((gone, True), (named, False)):
            for start in range(0, len(ids), 500):
                batch = ids[start:start + 500]
                some = ','.join('?' * len(batch))
                with self.connect() as db:
                    db.execute(f'DELETE FROM chunks WHERE file_id IN ({some})', batch)
                    if removed:
                        db.execute(f'DELETE FROM file_fts WHERE rowid IN ({some})', batch)
                        db.execute(f'DELETE FROM files WHERE id IN ({some})', batch)
                    else:
                        db.execute(f"UPDATE files SET status='metadata_only',digest='',error=NULL WHERE id IN ({some})", batch)
        with self.connect() as db:
            db.execute('VACUUM')
        self.flush()
        return {'removed_files': len(gone), 'name_only_files': len(named)}

    def record_error(self, file_id, error):
        # Stale content is withheld when a previously indexed file fails to update.
        with self.connect() as db:
            db.execute("UPDATE files SET status='error',error=? WHERE id=?", (str(error), file_id))

    def mark_missing(self, path):
        with self.connect() as db:
            prefix = str(path).rstrip("\\/")
            db.execute("UPDATE files SET active=0 WHERE path=? OR substr(path,1,?)=?", (prefix, len(prefix + "\\"), prefix + "\\"))

    def retain_roots(self, roots, allowed):
        with self.connect() as db:
            rows = db.execute("SELECT id,path FROM files WHERE active=1").fetchall()
        # Filesystem checks must never hold the protected database's global lock.
        rejected = [(row['id'],) for row in rows if not allowed(Path(row['path']))]
        for start in range(0, len(rejected), 250):
            with self.connect() as db:
                db.executemany('UPDATE files SET active=0 WHERE id=?', rejected[start:start+250])

    def pending_vectors(self, file_id, model_id, limit=None):
        with self.connect() as db:
            sql = 'SELECT id,text FROM chunks WHERE file_id=? AND (vector IS NULL OR model_id IS NULL OR model_id<>?)'
            arguments = [file_id, model_id]
            if limit is not None:
                sql += ' LIMIT ?'
                arguments.append(limit)
            return [dict(r) for r in db.execute(sql, arguments)]

    @staticmethod
    def _coarse_blob(vector):
        """Renormalized leading dimensions as little-endian float16 bytes."""
        import numpy as np
        prefix = np.asarray(vector[:COARSE_DIMENSION], dtype=np.float32)  # shape: [C]
        return (prefix / max(float(np.linalg.norm(prefix)), 1e-12)).astype('<f2').tobytes()

    def put_vectors(self, rows, model_id):
        """Store unit vectors [D] for chunk ids in one transaction.

        rows: iterable of (chunk_id, float32 vector [D]). The exact vector stays in
        chunks.vector; its coarse prefix goes to the compact vectors table.
        """
        rows = list(rows)
        if not rows:
            return
        with self.connect() as db:
            for chunk_id, vector in rows:
                db.execute("UPDATE chunks SET vector=?,dimension=?,model_id=? WHERE id=?", (vector.astype("<f4").tobytes(), len(vector), model_id, chunk_id))
                db.execute("DELETE FROM vectors WHERE chunk_id=?", (chunk_id,))
                db.execute("INSERT INTO vectors(chunk_id,model_id,coarse) VALUES(?,?,?)", (chunk_id, model_id, self._coarse_blob(vector)))
            db.execute("INSERT OR REPLACE INTO meta VALUES('embedding_model_hash',?)", (model_id,))
            db.execute("INSERT OR REPLACE INTO meta VALUES('embedding_dimension',?)", (str(len(rows[0][1])),))

    def put_vector(self, chunk_id, vector, model_id):
        self.put_vectors([(chunk_id, vector)], model_id)

    def backfill_coarse(self, model_id, running=lambda: True):
        """Give vectors written by an older build their coarse prefix; returns rows added.

        Runs once per model on a background thread. Each batch commits on its own,
        so searches keep working and simply see more passages as it progresses.
        """
        import numpy as np
        with self.connect() as db:
            done = db.execute("SELECT value FROM meta WHERE key='coarse_model'").fetchone()
        if done is not None and done[0] == model_id:
            return 0
        added, last = 0, 0
        while running():
            with self.connect() as db:
                # Walk chunk ids in order so each batch is one bounded range read.
                batch = db.execute("SELECT c.id,c.vector FROM chunks c WHERE c.id>? AND c.model_id=? AND c.vector IS NOT NULL AND NOT EXISTS(SELECT 1 FROM vectors v WHERE v.chunk_id=c.id AND v.model_id=c.model_id) ORDER BY c.id LIMIT 2000", (last, model_id)).fetchall()
                if not batch:
                    db.execute("INSERT OR REPLACE INTO meta VALUES('coarse_model',?)", (model_id,))
                    return added
                db.executemany("INSERT INTO vectors(chunk_id,model_id,coarse) VALUES(?,?,?)",
                    [(r[0], model_id, self._coarse_blob(np.frombuffer(r[1], dtype='<f4'))) for r in batch])
            added += len(batch)
            last = batch[-1][0]
        return added

    def coarse(self, model_id):
        """Return (chunk ids [N], float32 matrix [N,C]) for one model, kept in RAM.

        Only slots added since the previous call are read. Vectors written by an
        older build are converted by backfill_coarse. Deleted chunks keep a stale row
        until restart; the SQL join after the scan drops them.
        """
        import numpy as np
        with self._coarse_lock, self.connect() as db:
            if self._coarse is None or self._coarse[0] != model_id:
                self._coarse = (model_id, 0, 0, np.empty(0, dtype=np.int64), np.empty((0, COARSE_DIMENSION), dtype=np.float32))
            _, last, count, ids, matrix = self._coarse
            cursor = db.execute("SELECT slot,chunk_id,coarse FROM vectors WHERE slot>? AND model_id=? ORDER BY slot", (last, model_id))
            while batch := cursor.fetchmany(8192):
                # ponytail: a model with fewer than COARSE_DIMENSION dimensions has shorter rows, which are
                # skipped here, so it gets no semantic results. Zero-pad the prefix (here and for the query) if
                # such a model is ever used; the two shipped models have 1024 and 384.
                batch = [r for r in batch if len(r[2]) == COARSE_DIMENSION * 2]
                if not batch:
                    continue
                if count + len(batch) > len(ids):
                    # Double capacity so appends stay amortized O(1).
                    capacity = max(2 * len(ids), count + len(batch), 8192)
                    ids = np.concatenate([ids, np.empty(capacity - len(ids), dtype=np.int64)])  # shape: [capacity]
                    matrix = np.concatenate([matrix, np.empty((capacity - len(matrix), COARSE_DIMENSION), dtype=np.float32)])  # shape: [capacity,C]
                ids[count:count + len(batch)] = [r[1] for r in batch]
                # Decode float16 rows into the float32 scan matrix.
                matrix[count:count + len(batch)] = np.frombuffer(b''.join(r[2] for r in batch), dtype='<f2').reshape(len(batch), COARSE_DIMENSION)  # shape: [B,C]
                count += len(batch)
                last = batch[-1][0]
            self._coarse = (model_id, last, count, ids, matrix)
            return ids[:count], matrix[:count]

    def get_file(self, file_id=None, path=None):
        with self.connect() as db:
            if file_id is not None:
                row = db.execute("SELECT * FROM files WHERE id=? AND active=1", (int(file_id),)).fetchone()
            else:
                row = db.execute("SELECT * FROM files WHERE path=? AND active=1", (str(Path(path).resolve()),)).fetchone()
            return dict(row) if row else None

    def counts(self):
        # Status pushes arrive every second while indexing; counting 700k passages
        # each time took 0.8 s, so a recent answer is reused.
        cached = getattr(self, '_counts', None)
        if cached and time.monotonic() - cached[0] < 5:
            return dict(cached[1])
        with self.connect() as db:
            counts = dict(db.execute("SELECT status,count(*) FROM files WHERE active=1 AND file_type<>2 GROUP BY status"))
            counts["files"] = sum(counts.values())
            counts["folders"] = db.execute("SELECT count(*) FROM files WHERE active=1 AND file_type=2").fetchone()[0]
            counts["chunks"] = db.execute("SELECT count(*) FROM chunks c JOIN files f ON f.id=c.file_id WHERE f.active=1 AND f.status='indexed'").fetchone()[0]
            counts["vectors"] = db.execute("SELECT count(*) FROM chunks c JOIN files f ON f.id=c.file_id WHERE f.active=1 AND f.status='indexed' AND c.vector IS NOT NULL").fetchone()[0]
            self._counts = (time.monotonic(), dict(counts))
            return counts

    def errors(self):
        with self.connect() as db:
            return [dict(r) for r in db.execute("SELECT name,path,error FROM files WHERE active=1 AND status='error' LIMIT 100")]

    def preview(self, file_id, query):
        text, _, tokens = parse_query(query)
        with self.connect() as db:
            rows = [dict(r) for r in db.execute("SELECT c.id,c.location,c.text FROM chunks c JOIN files f ON f.id=c.file_id WHERE c.file_id=? AND f.active=1 AND f.status='indexed' LIMIT 3000", (int(file_id),))]
        # Prefer literal matches; semantic-only matches still expose real stored passages.
        rows.sort(key=lambda r: sum(token.casefold() in r["text"].casefold() for token in tokens), reverse=True)
        return [{"position": r["location"], "file_content_hightlight": highlighted(r["text"], tokens)} for r in rows[:20]]

    def type_weight(self, extension, kind):
        """Ranking multiplier for one file. A folder has no type to judge and keeps 1."""
        return 1.0 if kind == 2 else self.type_weights.get(extension, self.type_weights.get('default', 1.0))

    def _where(self, filters, requested_type):
        clauses, params = ["f.active=1"], []
        if self.roots is not None:
            roots = []
            for root in self.roots:
                roots.append('path_within(f.path, ?)')
                params.append(str(root))
            clauses.append('('+' OR '.join(roots)+')' if roots else '0')
        for folder in self.excluded_folders:
            # ponytail: one normalized boundary predicate per exclusion; store normalized paths if profiling warrants it.
            clauses.append('NOT path_within(f.path, ?)')
            params.append(str(folder))
        if self.excluded_extensions:
            clauses.append("(f.file_type=2 OR f.extension NOT IN (" + ",".join("?" for _ in self.excluded_extensions) + "))")
            params.extend(self.excluded_extensions)
        types = {"folder": 2, "pdf": 32, "document": 124, "docs": 124, "image": 256, "audio": 128, "video": 512, "archive": 1024, "code": 64, "program": 2048}
        if requested_type is not None and requested_type != 4095:
            clauses.append("(f.file_type & ?)<>0")
            params.append(requested_type)
        if "type" in filters:
            kind = filters["type"].lower()
            if kind not in types:
                raise ValueError("Unknown type filter; use pdf, docs, code, image, audio, video, archive, program")
            clauses.append("(f.file_type & ?)<>0")
            params.append(types[kind])
        if filters.get("type", "").lower() == "code":
            code_extensions = (".py", ".c", ".cpp", ".h", ".hpp", ".java", ".js", ".jsx", ".ts", ".tsx", ".sql", ".ps1", ".sh")
            clauses.append("f.extension IN (" + ",".join("?" for _ in code_extensions) + ")")
            params.extend(code_extensions)
        if "ext" in filters:
            clauses.append("f.extension=?")
            params.append("." + filters["ext"].lower().lstrip("."))
        if "folder" in filters:
            clauses.append("instr(lower(f.path),lower(?))>0")
            params.append(filters["folder"])
        for key, operator in (("before", "<"), ("after", ">=")):
            if key in filters:
                clauses.append(f"f.mtime_ns {operator} ?")
                params.append(int(datetime.strptime(filters[key], "%Y-%m-%d").timestamp() * 1e9))
        return " AND ".join(clauses), params

    def search(self, query, embedder=None, requested_type=None, limit=100, names=True, contents=True, preferred_paths=()):
        """Fuse filename/path BM25, content BM25, and cosine passage rankings.

        Returns real files with one best passage, match labels and elapsed times.
        Exact filenames dominate rank fusion. Vector scanning is exact and bounded
        over a 256-dimension prefix kept in RAM, then exact on the best candidates.
        """
        begin = time.perf_counter()
        text, filters, tokens = parse_query(query)
        where, params = self._where(filters, requested_type)
        scores, reasons, passages, exact = {}, {}, {}, set()
        warnings = []
        def words(value):
            # Match FTS unicode61's case/accent normalization for lexical coverage.
            normalized = value.casefold()
            if not normalized.isascii():
                # Strip combining accents after decomposition. A compiled range over the
                # combining-mark blocks runs in C; the earlier per-character category test
                # dominated lexical queries on PDF text full of symbols.
                normalized = COMBINING.sub('', unicodedata.normalize('NFD', normalized))
            return set(re.findall(r'[^\W_]+', normalized))

        query_words = words(text)
        def coverage(row):
            return len(query_words & words(row.get('text', row.get('name', '') + ' ' + row.get('path', '')))) / max(1, len(query_words))

        def add(rows, channel, weight):
            lexical = channel in ('Content', 'Path')
            if lexical:
                # Partial OR matches should not receive the same vote as a full description.
                # Sort coverage first, BM25 order second; retain the best passage per file.
                for row in rows:
                    row['coverage'] = coverage(row)
                rows = sorted(rows, key=lambda row: row['coverage'], reverse=True)
            seen = set()
            for row in rows:
                fid = row["file_id"]
                if fid in seen:
                    continue
                seen.add(fid)
                # Reciprocal rank fusion compares channels without mixing BM25 scales.
                # Cosine confidence preserves separation between adjacent semantic ranks.
                strength = row['coverage'] ** 2 if lexical else row.get('similarity', 1)
                if channel == 'Filename' and not query_words.issubset(words(row.get('name',''))):
                    # An incidental substring such as EFS inside "refs" must
                    # not outrank a document matching both contents and meaning.
                    strength = .25
                scores[fid] = scores.get(fid, 0) + strength * weight / (60 + len(seen))
                reasons.setdefault(fid, set()).add(channel)
                if "text" in row and (channel == "Semantic" or fid not in passages):
                    passages[fid] = (row.get("location", ""), row["text"])

        with self.connect() as db:
            if not text:
                rows = db.execute(f"SELECT f.id AS file_id FROM files f WHERE {where} ORDER BY f.mtime_ns DESC LIMIT ?", params + [limit]).fetchall()
                add([dict(r) for r in rows], "Metadata", 1)
            else:
                if names:
                    # Evaluate the selective substring first, before checking many
                    # scope/exclusion prefixes against every entry on a full drive.
                    name_rows = db.execute(f"SELECT f.id AS file_id,f.name,f.path FROM files f WHERE instr(lower(f.name),lower(?))>0 AND {where} ORDER BY (lower(f.name)=lower(?)) DESC, length(f.name) LIMIT 500", [text] + params + [text]).fetchall()
                    add([dict(r) for r in name_rows], "Filename", 2)
                    exact = {r["file_id"] for r in name_rows if r["name"].casefold() == text.casefold() or Path(r["name"]).stem.casefold() == text.casefold()}
                if tokens:
                    expression = " OR ".join('"' + token.replace('"', '""') + '"' for token in tokens)
                    if names:
                        # Rank inside FTS first, then apply scope filters to that short list. Joining
                        # and filtering every match took seconds on a 700k-passage index because
                        # the scope predicate is a Python callback per row.
                        # ponytail: filters apply after the top-N cut; raise FTS_CANDIDATES if a narrow scope over a huge index loses matches.
                        paths = db.execute(f"SELECT f.id AS file_id,f.name,f.path FROM (SELECT rowid AS id,bm25(file_fts,5,1) AS score FROM file_fts WHERE file_fts MATCH ? ORDER BY score LIMIT {FTS_CANDIDATES}) m JOIN files f ON f.id=m.id WHERE {where} ORDER BY m.score LIMIT 500", [expression] + params).fetchall()
                        add([dict(r) for r in paths], "Path", 1)
                    if contents:
                        content = db.execute(f"SELECT c.file_id,c.location,c.text FROM (SELECT rowid AS id,rank AS score FROM chunk_fts WHERE chunk_fts MATCH ? ORDER BY rank LIMIT {FTS_CANDIDATES}) m JOIN chunks c ON c.id=m.id JOIN files f ON f.id=c.file_id WHERE {where} AND f.status='indexed' ORDER BY m.score LIMIT 1000", [expression] + params).fetchall()
                        add([dict(r) for r in content], "Content", 1)
        lexical_ms = (time.perf_counter() - begin) * 1000
        semantic_ms = 0.0
        if text and embedder is not None:
            semantic_begin = time.perf_counter()
            try:
                import numpy as np
                model_id = embedder.identity()
                ids, matrix = self.coarse(model_id)  # shapes: [N], [N,C]
                if len(ids):
                    query_vector = embedder.encode([text], query=True)[0]  # shape: [D]
                    if embedder.identity() != model_id:
                        raise ValueError("Model files changed during search. Restart semantic backfill")
                    threshold = embedder.config["semantic_threshold"]
                    # First pass: cosine of every stored passage against the query on the
                    # renormalized leading dimensions (one BLAS matrix-vector product).
                    prefix = query_vector[:COARSE_DIMENSION]
                    coarse_scores = matrix @ (prefix / np.linalg.norm(prefix))  # shape: [N], reduces C
                    # ponytail: filters apply after this top-K cut, so a very selective filter over a huge index can lose matches; raise COARSE_CANDIDATES or filter first if that shows up.
                    keep = min(COARSE_CANDIDATES, len(ids))
                    top = np.argpartition(-coarse_scores, keep - 1)[:keep]  # shape: [K], unordered best K
                    # The prefix score only approximates the exact cosine (largest gap measured on
                    # real Qwen vectors: 0.144, BENCHMARKS.md run 004), so keep a margin below the threshold.
                    candidates = [int(i) for i in ids[top[coarse_scores[top] >= threshold - .2]]]
                    best = {}
                    if candidates:
                        with self.connect() as db:
                            rows = db.execute(f"SELECT c.file_id,c.location,c.text,c.vector FROM chunks c JOIN files f ON f.id=c.file_id WHERE c.id IN ({','.join('?' * len(candidates))}) AND {where} AND f.status='indexed' AND c.model_id=? AND c.dimension=?", candidates + params + [model_id, len(query_vector)]).fetchall()
                        rows = [r for r in rows if len(r["vector"]) == len(query_vector) * 4]
                        if rows:
                            exact_matrix = np.frombuffer(b''.join(r["vector"] for r in rows), dtype="<f4").reshape(len(rows), len(query_vector))  # shape: [K,D]
                            # Second pass: exact cosine on the full normalized vectors.
                            similarities = exact_matrix @ query_vector  # shape: [K], reduces D
                            for row, similarity in zip(rows, similarities):
                                fid = row["file_id"]
                                if similarity >= threshold and (fid not in best or similarity > best[fid][0]):
                                    best[fid] = (float(similarity), {"file_id": fid, "location": row["location"], "text": row["text"], "similarity": float(similarity)})
                    ordered = sorted(best.values(), key=lambda r: r[0], reverse=True)[:500]
                    add([r[1] for r in ordered], "Semantic", 1.2)
                else:
                    warnings.append("Semantic index is not ready for the configured model. Run indexing or semantic backfill.")
            except Exception as exc:
                warnings.append(f"Semantic search unavailable: {exc}")
            semantic_ms = (time.perf_counter() - semantic_begin) * 1000
        preferences = {}
        if text and preferred_paths:
            with self.connect() as db:
                for priority,path in enumerate(preferred_paths):
                    row=db.execute(f'SELECT f.id,f.path FROM files f WHERE f.path=? COLLATE NOCASE AND {where}',[str(Path(path))]+params).fetchone()
                    if row and Path(row['path']).exists():
                        fid=row['id'];preferences[fid]=len(preferred_paths)-priority
                        scores.setdefault(fid,0);reasons.setdefault(fid,set()).add('Previously opened')
        if text and scores and self.type_weights:
            # The file types the user works with come first among comparable matches. Files opened
            # before and exact names keep their place: they are sorted ahead of the score below.
            with self.connect() as db:
                for row in db.execute(f"SELECT id,extension,file_type FROM files WHERE id IN ({','.join('?' * len(scores))})", list(scores)):
                    scores[row['id']] *= self.type_weight(row['extension'], row['file_type'])
        ordered_ids = sorted(scores, key=lambda fid: (preferences.get(fid,0),fid in exact, scores[fid]), reverse=True)[:limit]
        results = []
        with self.connect() as db:
            for fid in ordered_ids:
                row = db.execute("SELECT * FROM files WHERE id=? AND active=1", (fid,)).fetchone()
                if row is None:
                    continue
                row = dict(row)
                location, passage = passages.get(fid, ("", ""))
                labels = sorted(reasons[fid])
                snippet = highlighted(passage[:650], tokens)
                caption = html.escape(" · ".join(labels + ([location] if location else [])))
                results.append({"file_id": fid, "file_path": row["path"], "file_type": row["file_type"], "file_size": row["size"], "time_stamp": row["mtime_ns"] // 1000000,
                    "file_name_with_highlight": highlighted(row["name"], tokens), "file_content_with_highlight": caption + ("<br>" + snippet if snippet else ""),
                    "name": row["name"], "snippet": passage[:650], "location": location, "matches": labels, "score": scores[fid], "index_status": row["status"]})
        return {"results": results, "warnings": warnings, "timing": {"lexical_ms": round(lexical_ms, 1), "semantic_ms": round(semantic_ms, 1), "total_ms": round((time.perf_counter() - begin) * 1000, 1)}}
