"""SQLite metadata, FTS5, persistent embeddings, and hybrid retrieval."""
import contextlib
import html
import json
import re
import sqlite3
import time
import unicodedata
from datetime import datetime
from pathlib import Path
from .exclusions import is_within

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
    from .extract import SUPPORTED
    return 64 if suffix in SUPPORTED else 1


def highlighted(text, tokens):
    """Escape file text before adding only our own literal highlight tags."""
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
        if protection == 'windows':
            from .protection import ProtectedDatabase
            self.protected = ProtectedDatabase(self.path)
        elif Path(str(self.path)+'.dpapi').exists():
            raise ValueError('A protected index exists. Use Windows account protection for this data directory.')
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
        db = sqlite3.connect(self.path, timeout=15)
        db.create_function('path_within', 2, is_within, deterministic=True)
        db.row_factory = sqlite3.Row
        db.execute("PRAGMA foreign_keys=ON")
        try:
            with db:
                yield db
        finally:
            db.close()

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

    def put_vector(self, chunk_id, vector, model_id):
        with self.connect() as db:
            db.execute("UPDATE chunks SET vector=?,dimension=?,model_id=? WHERE id=?", (vector.astype("<f4").tobytes(), len(vector), model_id, chunk_id))
            db.execute("INSERT OR REPLACE INTO meta VALUES('embedding_model_hash',?)", (model_id,))
            db.execute("INSERT OR REPLACE INTO meta VALUES('embedding_dimension',?)", (str(len(vector)),))

    def get_file(self, file_id=None, path=None):
        with self.connect() as db:
            if file_id is not None:
                row = db.execute("SELECT * FROM files WHERE id=? AND active=1", (int(file_id),)).fetchone()
            else:
                row = db.execute("SELECT * FROM files WHERE path=? AND active=1", (str(Path(path).resolve()),)).fetchone()
            return dict(row) if row else None

    def counts(self):
        with self.connect() as db:
            counts = dict(db.execute("SELECT status,count(*) FROM files WHERE active=1 AND file_type<>2 GROUP BY status"))
            counts["files"] = sum(counts.values())
            counts["folders"] = db.execute("SELECT count(*) FROM files WHERE active=1 AND file_type=2").fetchone()[0]
            counts["chunks"] = db.execute("SELECT count(*) FROM chunks c JOIN files f ON f.id=c.file_id WHERE f.active=1 AND f.status='indexed'").fetchone()[0]
            counts["vectors"] = db.execute("SELECT count(*) FROM chunks c JOIN files f ON f.id=c.file_id WHERE f.active=1 AND f.status='indexed' AND c.vector IS NOT NULL").fetchone()[0]
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
        types = {"folder": 2, "pdf": 32, "document": 124, "docs": 124, "image": 256, "audio": 128, "video": 512, "archive": 1024, "code": 64}
        if requested_type is not None and requested_type != 4095:
            clauses.append("(f.file_type & ?)<>0")
            params.append(requested_type)
        if "type" in filters:
            kind = filters["type"].lower()
            if kind not in types:
                raise ValueError("Unknown type filter; use pdf, docs, code, image, audio, video, archive")
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
        in memory, but O(chunks * dimension), pending a measured ANN requirement.
        """
        begin = time.perf_counter()
        text, filters, tokens = parse_query(query)
        where, params = self._where(filters, requested_type)
        scores, reasons, passages, exact = {}, {}, {}, set()
        warnings = []
        def words(value):
            # Match FTS unicode61's case/accent normalization for lexical coverage.
            normalized = ''.join(c for c in unicodedata.normalize('NFD', value.casefold()) if unicodedata.category(c) != 'Mn')
            return set(re.findall(r'[^\W_]+', normalized))

        query_words = words(text)
        def coverage(row):
            return len(query_words & words(row.get('text', row.get('name', '') + ' ' + row.get('path', '')))) / max(1, len(query_words))

        def add(rows, channel, weight):
            lexical = channel in ('Content', 'Path')
            if lexical:
                # Partial OR matches should not receive the same vote as a full description.
                # Sort coverage first, BM25 order second; retain the best passage per file.
                rows = sorted(rows, key=coverage, reverse=True)
            seen = set()
            for row in rows:
                fid = row["file_id"]
                if fid in seen:
                    continue
                seen.add(fid)
                # Reciprocal rank fusion compares channels without mixing BM25 scales.
                # Cosine confidence preserves separation between adjacent semantic ranks.
                strength = coverage(row) ** 2 if lexical else row.get('similarity', 1)
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
                        paths = db.execute(f"SELECT f.id AS file_id,f.name,f.path FROM file_fts JOIN files f ON f.id=file_fts.rowid WHERE file_fts MATCH ? AND {where} ORDER BY bm25(file_fts,5,1) LIMIT 500", [expression] + params).fetchall()
                        add([dict(r) for r in paths], "Path", 1)
                    if contents:
                        content = db.execute(f"SELECT c.file_id,c.location,c.text FROM chunk_fts JOIN chunks c ON c.id=chunk_fts.rowid JOIN files f ON f.id=c.file_id WHERE chunk_fts MATCH ? AND {where} AND f.status='indexed' ORDER BY bm25(chunk_fts) LIMIT 1000", [expression] + params).fetchall()
                        add([dict(r) for r in content], "Content", 1)
        lexical_ms = (time.perf_counter() - begin) * 1000
        semantic_ms = 0.0
        if text and embedder is not None:
            semantic_begin = time.perf_counter()
            try:
                import numpy as np
                model_id = embedder.identity()
                with self.connect() as db:
                    available = db.execute(f"SELECT 1 FROM chunks c JOIN files f ON f.id=c.file_id WHERE {where} AND f.status='indexed' AND c.model_id=? AND c.vector IS NOT NULL LIMIT 1", params + [model_id]).fetchone()
                if available:
                    query_vector = embedder.encode([text], query=True)[0]  # shape: [D]
                    if embedder.identity() != model_id:
                        raise ValueError("Model files changed during search. Restart semantic backfill")
                    best = {}
                    with self.connect() as db:
                        cursor = db.execute(f"SELECT c.file_id,c.location,c.text,c.vector,c.dimension FROM chunks c JOIN files f ON f.id=c.file_id WHERE {where} AND f.status='indexed' AND c.model_id=? AND c.vector IS NOT NULL", params + [model_id])
                        while batch := cursor.fetchmany(1024):
                            valid = [r for r in batch if r["dimension"] == len(query_vector) and len(r["vector"]) == len(query_vector) * 4]
                            if not valid:
                                continue
                            matrix = np.stack([np.frombuffer(r["vector"], dtype="<f4") for r in valid])  # shape: [B,D]
                            # Compare each normalized stored passage with the query.
                            similarities = matrix @ query_vector  # shape: [B], reduces D
                            for row, similarity in zip(valid, similarities):
                                fid = row["file_id"]
                                if similarity >= embedder.config["semantic_threshold"] and (fid not in best or similarity > best[fid][0]):
                                    best[fid] = (float(similarity), {"file_id": fid, "location": row["location"], "text": row["text"], "similarity": float(similarity)})
                    # ponytail: exact O(ND) scan; use ANN once measured corpus latency requires it.
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
