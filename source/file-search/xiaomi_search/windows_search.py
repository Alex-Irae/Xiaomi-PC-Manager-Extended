"""Optional Windows index candidate source and Start apps list; never writes Windows settings.
Dependencies: stdlib and WindowsSearch.ps1 beside this file, on Windows PowerShell 5.1.
Outputs: filtered result rows, app rows and short-lived in-memory caches.
Used by: python -m xiaomi_search.backend --config config.json --data data.
"""
import copy
import json
import logging
import os
import subprocess
import threading
import time
from collections import OrderedDict
from pathlib import Path
from .store import file_type, highlighted, parse_query
from .exclusions import canonical_path

APP_PREFIX = 'shell:AppsFolder\\'
APP_TYPE = 2048  # File-type bit used by the search bar's Programs category.


class WindowsSearch:
    def __init__(self, idle_seconds=120):
        self.lock = threading.Lock()      # guards caches and the issued-path list
        self.pipe = threading.Lock()      # one request/response on the worker at a time
        self.process = None
        self.timer = None
        self.idle_seconds = idle_seconds
        self.cache = OrderedDict()
        self.issued = OrderedDict()
        self.apps = (0.0, [])
        self.loading = False

    def close(self):
        with self.pipe:
            self._stop()

    def _stop(self):
        if self.timer:
            self.timer.cancel()
            self.timer = None
        if self.process and self.process.poll() is None:
            self.process.kill()
        self.process = None

    def _idle(self):
        # A busy pipe means the worker is in use; its request reschedules this timer.
        if self.pipe.acquire(blocking=False):
            try:
                self._stop()
            finally:
                self.pipe.release()

    def request(self, message, timeout=6):
        """Send one JSON line to the persistent worker and return its JSON answer.

        The worker starts on first use (about 0.4 s) and is killed after idle_seconds
        or on any timeout/protocol error, so a stuck Windows query cannot pin it.
        """
        with self.pipe:
            if self.timer:
                self.timer.cancel()
            if self.process is None or self.process.poll() is not None:
                script = Path(__file__).resolve().with_name('WindowsSearch.ps1')
                powershell = Path(os.environ.get('SystemRoot', 'C:/Windows')) / 'System32/WindowsPowerShell/v1.0/powershell.exe'
                self.process = subprocess.Popen([str(powershell), '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(script)],
                    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                    encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
            process, answer = self.process, []
            try:
                process.stdin.write(json.dumps(message) + '\n')
                process.stdin.flush()
                reader = threading.Thread(target=lambda: answer.append(process.stdout.readline()), daemon=True)
                reader.start()
                reader.join(timeout)
                if not answer or not answer[0]:
                    raise RuntimeError('Windows Search did not answer in time.')
                value = json.loads(answer[0].lstrip('\ufeff'))
                if not isinstance(value, dict):
                    raise ValueError('Invalid Windows result envelope')
            except (OSError, RuntimeError, ValueError):
                self._stop()
                raise
            self.timer = threading.Timer(self.idle_seconds, self._idle)
            self.timer.daemon = True
            self.timer.start()
            return value

    def programs(self, text, limit=5):
        """Start-menu apps whose name contains every query word, shortest name first."""
        words = text.casefold().split()
        if not words or os.name != 'nt':
            return []
        with self.lock:
            loaded, apps = self.apps
            refresh = (time.monotonic() - loaded > 600 or not apps) and not self.loading
            if refresh:
                self.loading = True
        if refresh:
            # Enumerating Start apps takes about a second; typing must never wait for it.
            threading.Thread(target=self._load_programs, daemon=True, name='start-apps').start()
        found = [(name, key) for name, key in apps if all(word in name.casefold() for word in words)]
        found.sort(key=lambda app: (not app[0].casefold().startswith(words[0]), len(app[0])))
        return [{'file_id': None, 'file_path': APP_PREFIX + key, 'name': name, 'file_type': APP_TYPE,
                 'file_name_with_highlight': highlighted(name, words), 'snippet': '', 'location': '',
                 'matches': ['Program'], 'program': True, 'score': 1.0, 'index_status': 'program'} for name, key in found[:limit]]

    def _load_programs(self):
        try:
            raw = self.request({'kind': 'apps'}, timeout=15).get('apps') or []
            apps = [(a['name'], a['id']) for a in raw if isinstance(a, dict) and isinstance(a.get('name'), str) and isinstance(a.get('id'), str)]
            with self.lock:
                self.apps = (time.monotonic(), apps)
        except (OSError, RuntimeError, ValueError) as error:
            logging.getLogger(__name__).warning('Start apps unavailable: %s', error)
        finally:
            with self.lock:
                self.loading = False

    def program_path(self, path):
        """True only for an app identifier this session actually enumerated."""
        with self.lock:
            return isinstance(path, str) and path.startswith(APP_PREFIX) and any(path == APP_PREFIX + key for _, key in self.apps[1])

    def search(self, service, params):
        started = time.perf_counter()
        query = params.get('text', '')
        if not isinstance(query, str) or len(query) > 2048:
            raise ValueError('Invalid Windows search query')
        text, filters, tokens = parse_query(query)
        preferred = [p for p in params.get('preferred_paths', []) if not (isinstance(p, str) and p.startswith(APP_PREFIX))]
        if not isinstance(params.get('preferred_paths', []), list) or len(preferred) > 20 or any(not isinstance(p, str) or not Path(p).is_absolute() for p in preferred):
            raise ValueError('Invalid remembered paths')
        answer = {'results': [], 'warnings': [], 'timing': {'total_ms': 0, 'cache_hit': False}}
        if not service.config.get('windows_semantic_enabled') or not text:
            return answer
        if os.name != 'nt':
            answer['warnings'] = ['Windows Search requires Windows.']
            return answer
        requested_type = params.get('file_type')
        if requested_type is not None and (type(requested_type) is not int or not 1 <= requested_type <= 4095):
            raise ValueError('Invalid file type')
        # Reuse the exact SQL filter predicates used by local retrieval.
        where, where_params = service.store._where(filters, requested_type)
        key = (text, filters.get('ext'), tuple(service.config['roots']))
        with self.lock:
            cached = self.cache.get(key)
            raw = copy.deepcopy(cached[1]) if cached and time.monotonic()-cached[0] < 15 else None
        if raw is None:
            request = {'kind': 'search', 'text': text, 'extension': '.'+filters['ext'].lstrip('.') if filters.get('ext') else '', 'roots': service.config['roots']}
            try:
                raw = self.request(request)
                if not isinstance(raw.get('results'), list):
                    raise ValueError('Invalid Windows result envelope')
            except (OSError, RuntimeError, ValueError) as error:
                answer['warnings'] = [str(error)]
                return answer
            with self.lock:
                if not raw.get('warnings'):
                    self.cache[key] = (time.monotonic(), copy.deepcopy(raw))
                    while len(self.cache) > 32:
                        self.cache.popitem(last=False)
        else:
            answer['timing']['cache_hit'] = True
        candidates, seen = [], set()
        for item in raw['results'][:256]:
            try:
                path = Path(item['path'])
                if not path.is_absolute() or not service.indexer.allowed(path):
                    continue
                path = path.resolve()
                identity = canonical_path(path)
                if identity in seen:
                    continue
                seen.add(identity)
                metadata = path.stat()  # also proves the indexed file still exists
                candidates.append((path, file_type(path), metadata, item.get('rank', 1), str(item.get('summary') or '')))
            except (KeyError, TypeError, OSError, ValueError):
                continue
        eligible = set()
        if candidates:
            # The temporary CTE applies every existing root/type/folder/date filter
            # to metadata; it neither inserts files nor extracts their contents.
            values = ','.join('(?,?,?,?,?,1)' for _ in candidates)
            args = []
            for index, (path, kind, metadata, _, _) in enumerate(candidates):
                args.extend((index, str(path), path.suffix.lower(), kind, metadata.st_mtime_ns))
            with service.store.connect() as db:
                eligible = {r[0] for r in db.execute(
                    'WITH f(id,path,extension,file_type,mtime_ns,active) AS (VALUES '+values+') SELECT f.id FROM f WHERE '+where,
                    args + where_params)}
        order = {canonical_path(p): i for i, p in enumerate(preferred)}
        results = []
        for index, (path, kind, metadata, rank, summary) in enumerate(candidates):
            if index not in eligible or kind == 2:
                continue
            identity = canonical_path(path)
            results.append({'file_id': None, 'file_path': str(path), 'name': path.name,
                'file_type': kind, 'file_size': metadata.st_size, 'time_stamp': metadata.st_mtime_ns // 1000000,
                'file_name_with_highlight': highlighted(path.name, tokens),
                'snippet': summary[:650], 'matches': ['Windows Search']+(['Previously opened'] if identity in order else []),
                'windows_result': True, 'windows_rank': rank,
                'preference_rank': order.get(identity, 1000000)})
            with self.lock:
                self.issued[identity] = time.monotonic()
                self.issued.move_to_end(identity)
                while len(self.issued) > 512:
                    self.issued.popitem(last=False)
        results.sort(key=lambda r: (r['preference_rank'], r['windows_rank']))
        answer.update(results=results, warnings=raw.get('warnings', []),
            semantic_requested=bool(raw.get('semantic')), cloud_providers=False)
        answer['timing']['total_ms'] = round((time.perf_counter()-started)*1000, 2)
        return answer

    def issued_path(self, path):
        with self.lock:
            timestamp = self.issued.get(canonical_path(path), 0)
            return time.monotonic()-timestamp < 1800 and bool(timestamp)
