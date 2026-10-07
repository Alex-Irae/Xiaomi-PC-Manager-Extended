"""Frontend-independent search service and Xiaomi WebView message adapter."""
import json
import copy
import logging
import threading
import time
from collections import defaultdict, OrderedDict
from pathlib import Path

from .embedding import Embedder
from .indexer import Indexer
from .store import Store, file_type
from .windows_search import WindowsSearch, APP_TYPE, APP_PREFIX
from .exclusions import canonical_path

LOG = logging.getLogger(__name__)


class Service:
    def __init__(self, config, data, config_path="config.json"):
        self.windows = WindowsSearch(config["idle_unload_seconds"])
        self.config = config
        self.config_path = Path(config_path).resolve()
        self.data = Path(data).resolve()
        self.store = Store(self.data / "index.sqlite3", config['index_protection'])
        if config['index_protection'] == 'efs' and self.store.unencrypted:
            LOG.warning('Index is NOT encrypted on disk: %s', self.store.unencrypted)
        self.maintenance_lock = threading.Lock()
        self.maintenance_thread = None
        self.maintenance_active = False
        self.backup_lock = threading.Lock()
        self.backup_thread = None
        self.backup_state = {'active': False, 'phase': ''}
        self.store.roots = config['roots']
        self.store.excluded_folders = config["excluded_folders"]
        self.store.excluded_extensions = config["excluded_extensions"]
        self.store.type_weights = config["type_weights"]
        self._cache = OrderedDict()
        self._cache_lock = threading.Lock()
        self.embedder = Embedder(config, self.data)
        self.subscriptions = {}
        self.emit = lambda message: None
        self.window_action = lambda action, params: None
        self.indexer = Indexer(config, self.store, self.embedder, self.data, notify=self.notify)
        self.indexer.internal_files = {str(self.config_path), str(self.config_path)+'.tmp', *(str(self.config_path.parent / name) for name in ('included-folders.txt', 'excluded-folders.txt', 'included-folders.txt.tmp', 'excluded-folders.txt.tmp'))}
        self.latest_search = None
        self._notification_lock = threading.Lock()
        self._notification_timer = None
        self._last_notification = 0.0
        self._closed = False
        self.indexer.refresh_exclusions = self.refresh_exclusions
        self.embedder.on_change = self.notify
        if config['programs_enabled']:
            self.windows.programs('preload')  # starts the background Start-apps enumeration
        if config['semantic_enabled']:
            threading.Thread(target=self._backfill, daemon=True, name='coarse-backfill').start()
        self.embedder.active = lambda: self.maintenance_active or self.indexer.busy or (
            self.indexer.mode != 'paused' and bool(self.indexer.jobs or self.indexer.vector_jobs))

    def _backfill(self):
        try:
            added = self.store.backfill_coarse(self.embedder.identity(), lambda: not self._closed)
            if added:
                LOG.info('Converted %d stored vectors for the fast scan', added)
        except Exception:
            LOG.exception('Vector conversion failed; semantic search covers converted passages only')

    def refresh_exclusions(self):
        from .exclusions import refresh
        refresh(self.config, self.config_path)
        self.store.excluded_folders = self.config['excluded_folders']
        self.store.roots = self.config['roots']
        self.indexer.roots = [Path(path) for path in self.config['roots']]

    def status(self):
        return {"counts": self.store.counts(), "indexer": self.activity(), "model": {"path": self.config["model_path"], "available": (Path(self.config["model_path"]) / "openvino_model.xml").is_file(), "loaded": self.embedder.pipeline is not None, "device": self.embedder.device, "error": self.embedder.error},
            "roots": self.config["roots"], "shortcut": self.config["shortcut"], "semantic_enabled": self.config["semantic_enabled"], "offline": True, "index_unencrypted": self.store.unencrypted,
            "backup": dict(self.backup_state)}

    def activity(self):
        state = self.indexer.status()
        if self.maintenance_active and not state['busy']:
            state.update(busy=True, phase='Saving snapshot' if not state['queued_files'] and not state['queued_embedding_files'] else 'Indexing')
        if self.backup_state['active'] and not state['busy']:
            state.update(busy=True, phase=self.backup_state['phase'])
        return state

    def backup_index(self):
        """Choose a destination and acknowledge before a large checkpoint finishes."""
        if not self.backup_lock.acquire(blocking=False):
            raise RuntimeError('An index backup is already running')
        try:
            parent = self.window_action('choose_folder', {})
            if not parent:
                self.backup_lock.release()
                return {'cancelled': True}
            parent = Path(parent).resolve(strict=True)
            app = Path(__file__).resolve().parent.parent
            if parent == app or app in parent.parents:
                raise ValueError('Choose a backup folder outside the AI Center installation')
            profile = self.config_path.parent
            if parent == profile or profile in parent.parents:
                raise ValueError('Choose a backup folder outside AI Center application data')
            configuration = copy.deepcopy(self.config)
            self.backup_state = {'active': True, 'phase': 'Saving checkpoint'}
            # Pin the native worker before acknowledging the RPC, even if the
            # settings window closes while the disk copy is in progress.
            self.emit({'kind': 'index_activity', 'indexer': self.activity()})
            def progress(phase):
                self.backup_state = {'active': True, 'phase': phase}
                self.notify()
            def background():
                from .backup import create_backup
                try:
                    result = create_backup(self.store, parent, configuration, progress)
                    self.backup_state = {'active': False, 'phase': 'Complete', **result}
                except Exception as exc:
                    self.backup_state = {'active': False, 'phase': 'Failed', 'error': str(exc)}
                    LOG.exception('Index backup failed')
                finally:
                    self.backup_lock.release()
                    self.notify()
            self.backup_thread = threading.Thread(target=background, name='index-backup', daemon=True)
            self.backup_thread.start()
            return {'accepted': True}
        except Exception:
            self.backup_state = {'active': False, 'phase': ''}
            self.backup_lock.release()
            raise

    def subscription_data(self, method):
        if method == "register_aiservice_state_change":
            return {"status": 1}
        if method == "register_indexing_files_count":
            return {"count": len(self.indexer.jobs) + len(self.indexer.vector_jobs)}
        if method == "register_aisearch_switch_status":
            return {"ai_search_switch": 1}
        if method == "register_devices_list":
            return {"devices": []}
        if method == "register_session_notify":
            return {"lock_flag": False}
        if method == "register_aisearch_model_status":
            # Xiaomi's gate represents service availability here. Actual model
            # availability is reported separately, so filename/content work offline.
            return {"status": 2}
        if method == "register_get_offline_large_model_status":
            return {"install_status": True}
        if method == "register_intent":
            return {"intent": -1}
        if method == "register_local_status":
            return self.status()
        raise ValueError(f"Unsupported subscription: {method}")

    def notify(self):
        # Coalesce work-driven updates. Counting the whole index after every
        # chunk would turn status reporting into a large background workload.
        with self._notification_lock:
            if self._closed or self._notification_timer:
                return
            delay = max(0.0, 1.0 - (time.monotonic() - self._last_notification))
            self._notification_timer = threading.Timer(delay, self._flush_notifications)
            self._notification_timer.daemon = True
            self._notification_timer.start()

    def _flush_notifications(self):
        with self._notification_lock:
            if self._closed:
                return
            self._notification_timer = None
            self._last_notification = time.monotonic()
        # Native worker lifetime must follow indexing, even with every WebView closed.
        self.emit({'kind': 'index_activity', 'indexer': self.activity()})
        for key, (method, owner) in list(self.subscriptions.items()):
            if method not in ("register_indexing_files_count", "register_local_status"):
                continue
            try:
                self.emit({"owner": owner, "id": key, "response": {"code": 0, "data": self.subscription_data(method)}})
            except Exception:
                LOG.debug("Window not ready for status update")

    def search(self, params):
        begin = time.perf_counter()
        query = params.get("text", "")
        if not isinstance(query, str) or len(query) > 2048:
            raise ValueError("Search must be a string of at most 2048 characters")
        semantic = params.get("semantic", True) and self.config["semantic_enabled"]
        requested_type = params.get("file_type")
        if requested_type is not None and (type(requested_type) is not int or not 1 <= requested_type <= 4095):
            raise ValueError("Invalid file type")
        preferred_paths = params.get('preferred_paths', [])
        # Remembered programs are app identifiers, not filesystem paths.
        preferred_programs = [p for p in preferred_paths if isinstance(p, str) and p.startswith(APP_PREFIX)] if isinstance(preferred_paths, list) else []
        if preferred_programs:
            preferred_paths = [p for p in preferred_paths if p not in preferred_programs]
        if not isinstance(preferred_paths, list) or len(preferred_paths)>20 or any(not isinstance(path,str) or not Path(path).is_absolute() for path in preferred_paths):
            raise ValueError('Invalid remembered file choices')
        revision = self.store.revision()
        model_id = None
        if semantic:
            try:
                model_id = self.embedder.identity()
            except (OSError, ValueError):
                pass
        key = (query, bool(semantic), requested_type, revision, model_id, tuple(preferred_paths), tuple(preferred_programs), json.dumps(self.config, sort_keys=True))
        with self._cache_lock:
            cached = self._cache.get(key)
            if cached is not None:
                self._cache.move_to_end(key)
                result = copy.deepcopy(cached)
                result["timing"] = {"total_ms": round((time.perf_counter() - begin) * 1000, 2), "cache_hit": True, "original_total_ms": cached["timing"]["total_ms"]}
                self.latest_search = result
                return self._with_programs(result, query, requested_type, preferred_programs)
        result = self.store.search(query, self.embedder if semantic else None, requested_type, limit=500, names=self.config['name_enabled'], contents=self.config['content_enabled'], preferred_paths=preferred_paths)
        choice_order = {canonical_path(p): i for i, p in enumerate(preferred_paths)}
        for row in result["results"]:
            row["preference_rank"] = choice_order.get(canonical_path(row["file_path"]), 1000000)
        result["timing"]["cache_hit"] = False
        # Cache successful responses only; SQLite triggers invalidate after any
        # metadata/content/vector change. A compiling model never poisons cache.
        if not result["warnings"] and revision == self.store.revision():
            with self._cache_lock:
                self._cache[key] = copy.deepcopy(result)
                while len(self._cache) > 64:
                    self._cache.popitem(last=False)
        self.latest_search = result
        return self._with_programs(result, query, requested_type, preferred_programs)

    def _with_programs(self, result, query, requested_type, preferred_programs):
        """Prepend matching Start apps; never cached, because the app list loads in the background."""
        if self.config['programs_enabled'] and (requested_type is None or requested_type & APP_TYPE):
            from .store import parse_query
            programs = self.windows.programs(parse_query(query)[0])
            for row in programs:
                remembered = row['file_path'] in preferred_programs
                row['preference_rank'] = preferred_programs.index(row['file_path']) if remembered else 1000000
                if remembered:
                    row['matches'] = row['matches'] + ['Previously opened']
            # Programs lead the local list so rank fusion keeps them above file matches.
            result["results"] = sorted(programs, key=lambda r: r['preference_rank']) + result["results"]
        return result

    def _indexed_file(self, params):
        fid = params.get("file_id")
        path = params.get("file_path")
        if params.get('program') is True:
            if fid is None and self.config['programs_enabled'] and self.windows.program_path(path):
                return {'id': None, 'path': path, 'file_type': APP_TYPE}
            raise ValueError('This program is no longer listed. Search again.')
        if fid is None and not isinstance(path, str):
            raise ValueError("An indexed file ID or path is required")
        row = self.store.get_file(file_id=fid, path=path)
        if path is not None and (not isinstance(path,str) or row is not None and Path(row['path']).resolve()!=Path(path).resolve()):
            raise ValueError('This result changed. Search again before opening it.')
        if row is None and fid is None and params.get('windows_result') is True and isinstance(path, str):
            candidate = Path(path)
            if self.config.get('windows_semantic_enabled') and self.windows.issued_path(candidate) and candidate.is_file() and self.indexer.allowed(candidate):
                row = {'id': None, 'path': str(candidate.resolve()), 'file_type': file_type(candidate)}
        if row is None or not self.indexer.allowed(Path(row["path"])):
            raise ValueError("File is outside the active index")
        return row

    def dispatch(self, method, params):
        self.refresh_exclusions()
        if method in ("track_event", "set_js_log", "set_window_active"):
            return {}  # No telemetry or logs containing query/file contents.
        if method == "request_get_device_info":
            return {"ai_pc_flag": True}
        if method == "local_windows_search":
            return self.windows.search(self, params)
        if method in ("search", "search_by_file_type", "local_search"):
            result = self.search(params)
            if method == "local_search":
                return result
            rows = result["results"]
            if method == "search_by_file_type":
                page = params.get("current_page", 1)
                size = params.get("page_size", 50)
                if type(page) is not int or type(size) is not int or page < 1 or not 1 <= size <= 100:
                    raise ValueError("Invalid pagination")
                offset = (page - 1) * size
                return {"file_list": rows[offset:offset + size], "current_page": page, "page_size": size, "search_end": offset + size >= len(rows), "search_error": 0}
            groups = defaultdict(list)
            for row in rows[8:]:
                groups[row["file_type"]].append(row)
            return {"best_match": rows[:8], "file_list": [{"file_type": kind, "files": files[:50]} for kind, files in groups.items()], "extra_data": "", "search_error": 0, "local_timing": result["timing"], "local_warnings": result["warnings"]}
        if method == "get_filecontent_matchpoints":
            row = self._indexed_file(params)
            return {"txt_match_points": self.store.preview(row["id"], params.get("text", "")), "img_match_points": [], "match_points": []}
        if method in ("open_file", "open_file_with", "open_doc_highlight_text", "open_doc_jump_image", "open_file_folder", "check_file_exist", "copy_file_path_to_clipboard", "copy_to_clipboard"):
            row = self._indexed_file(params)
            if row['file_type'] == APP_TYPE:
                if method != 'open_file':
                    raise ValueError('Programs can only be opened')
                query = params.get('query', '')
                self.window_action('open', {'path': row['path'], 'query': query if isinstance(query, str) and len(query) <= 256 else ''})
                return {"status": 0, 'opened': True}
            if not Path(row["path"]).exists():
                return {"status": 1}
            if method == "check_file_exist":
                return {"status": 0}
            if method=='open_file_with' and row['file_type']==2:
                raise ValueError('Open with applies to files')
            query=params.get('query','')
            if not isinstance(query,str) or len(query)>256:
                raise ValueError('Invalid history query')
            action = "reveal" if method == "open_file_folder" else "copy" if method.startswith("copy") else "open_with" if method=='open_file_with' else "open"
            result=self.window_action(action, {"path": row["path"], 'query': query})
            return {"status": 0, 'opened': result.get('opened',True) if isinstance(result,dict) else True}
        if method == "window_resize":
            width, height = params.get("width"), params.get("height")
            if not isinstance(width, (float, int)) or not isinstance(height, (float, int)) or not 500 <= width <= 1600 or not 70 <= height <= 1200:
                raise ValueError("Invalid window dimensions")
            self.window_action("resize", {"width": int(width), "height": int(height)})
            return {}
        if method == "window_jump":
            self.window_action("hide" if params.get("target") == 1 else "manage", {})
            return {"status": 0}
        if method == "local_status":
            return self.status()
        if method == "local_errors":
            return self.store.errors()
        if method == "local_config":
            # The installer writes the released version beside the package; an editable copy has none.
            manifest = Path(__file__).resolve().parents[1] / "package-manifest.json"
            version = json.loads(manifest.read_text(encoding="utf-8")).get("version", "") if manifest.is_file() else "development"
            return {"settings": self.config, "path": str(self.config_path), "version": version}
        if method == "local_choose_folder":
            return self.window_action("choose_folder", {})
        if method == 'local_backup_index':
            return self.backup_index()
        if method == "local_save_config":
            from .config import validate, write_atomic
            allowed = {"indexing_load", "roots", "model_path", "devices", "indexing_mode", "indexing_frequency", "shortcut", "run_at_startup", "center_at_startup", "semantic_enabled", "name_enabled", "content_enabled", "preferred_device", "excluded_folders", "excluded_extensions", "name_only_extensions", "accent_color", "theme", "index_protection", "font_family", "bar_size", "arrow_style", "model_standby", "follow_suite_appearance", "windows_semantic_enabled", "programs_enabled"}
            if set(params) - allowed:
                raise ValueError("Unsupported setting")
            settings = validate({**self.config, **params}, self.config_path)
            restart = any(settings[key] != self.config[key] for key in ('model_path', 'index_protection'))
            roots_changed = settings['roots'] != self.config['roots']
            frequency_changed = settings['indexing_frequency'] != self.config['indexing_frequency']
            device_changed = settings["preferred_device"] != self.config["preferred_device"] or settings["devices"] != self.config["devices"]
            enabling_semantics = settings["semantic_enabled"] and not self.config["semantic_enabled"]
            name_only_changed = settings["name_only_extensions"] != self.config["name_only_extensions"]
            disabling_semantics = self.config['semantic_enabled'] and not settings['semantic_enabled']
            for key, filename in [('roots', 'included-folders.txt'), ('excluded_folders', 'excluded-folders.txt')]:
                if key in params:
                    target = self.config_path.parent / filename
                    write_atomic(target, '# One absolute folder path per line.\n'+'\n'.join(settings[key])+'\n')
            write_atomic(self.config_path, json.dumps(settings, indent=2))
            self.config.update(settings)
            self.indexer.roots = [Path(p).resolve() for p in settings["roots"]]
            self.store.excluded_folders = settings["excluded_folders"]
            self.store.excluded_extensions = settings["excluded_extensions"]
            self.store.roots = settings['roots']
            if self.indexer.running and (roots_changed or frequency_changed):
                self.indexer.configure_watch(settings['indexing_frequency'] == 'realtime')
            # Queries already filter excluded paths. Reconcile on the indexer thread,
            # never by crawling the entire database during an appearance change.
            if device_changed or disabling_semantics:
                self.embedder.unload()
                self.embedder.failed_devices.clear()
            if 'model_standby' in params:
                self.embedder.schedule_release()
            if 'indexing_mode' in params:
                self.indexer.set_mode(settings["indexing_mode"])
            if enabling_semantics and settings['indexing_frequency'] == 'realtime':
                self.indexer.request_scan()
            # A type added to the list loses its passages, and one taken off is read again, when the
            # indexer next visits each file. With manual indexing that visit is "Index now".
            if name_only_changed and settings['indexing_frequency'] != 'manual':
                self.indexer.request_scan()
            with self._cache_lock:
                self._cache.clear()
            self.window_action("appearance", settings)
            self.notify()
            return {"saved": True, "restart_required": restart, "settings": dict(self.config)}
        if method == "local_scan":
            self.indexer.request_scan()
            return self.status()
        if method in ('local_index_now', 'local_reset_index'):
            if not self.maintenance_lock.acquire(blocking=False):
                raise RuntimeError('An indexing request is already running. Progress continues in the background.')
            self.maintenance_active = True
            def index():
                try:
                    archive = None
                    if method == 'local_reset_index':
                        self.indexer.stop()
                        if self.indexer.thread and self.indexer.thread.is_alive():
                            raise RuntimeError('Indexing is still finishing. Try resetting after it stops.')
                        archive = self.store.reset()
                    self.indexer.start(watch=self.config['indexing_frequency'] == 'realtime')
                    previous = self.indexer.mode
                    if params.get('force', True):
                        self.indexer.set_mode('normal')
                    self.indexer.request_scan()
                    try:
                        self.indexer.wait_complete()
                        self.store.flush()
                    finally:
                        if self.indexer.mode == 'normal' and self.config['indexing_mode'] == previous:
                            self.indexer.set_mode(previous)
                    result = {**self.status(), 'indexer': self.indexer.status(), 'archive': archive}
                finally:
                    self.maintenance_active = False
                    self.maintenance_lock.release()
                return result
            if params.get('wait', True):
                return index()
            def background():
                try:
                    index()
                except Exception as exc:
                    self.indexer.scan_error = str(exc)
                    LOG.exception('Background indexing failed')
                finally:
                    self.notify()
            # Pin native lifetime before acknowledging the short-lived RPC.
            self.maintenance_thread = threading.Thread(target=background, name='index-command', daemon=True)
            self.maintenance_thread.start()
            return {**self.status(), 'accepted': True}
        if method == "local_mode":
            self.indexer.set_mode(params.get("mode"))
            return self.status()
        if method == "local_model_release":
            self.embedder.unload()
            return self.status()
        if method in ("local_manage", "local_hide", "local_show_search", "local_quit"):
            self.window_action(method.removeprefix("local_"), {})
            return {}
        if method == "local_semantic":
            enabled = params.get("enabled")
            if type(enabled) is not bool:
                raise ValueError("enabled must be a boolean")
            self.config["semantic_enabled"] = enabled
            with self.indexer.condition:
                self.indexer.condition.notify_all()
            if not enabled:
                self.embedder.unload()
            self.notify()
            return self.status()
        # Fail explicitly rather than pretend proprietary operations succeeded.
        raise ValueError(f"This local host does not implement {method}")

    def rpc(self, message, owner="search"):
        """Accept the original Xiaomi envelope and return its exact response shape."""
        identifier = None
        try:
            if not isinstance(message, dict) or message.get("mode") not in (0, 1):
                raise ValueError("Invalid bridge message")
            data = message.get("data")
            if not isinstance(data, dict):
                raise ValueError("Bridge data must be an object")
            identifier = data.get("id")
            if type(identifier) is not int or not 0 <= identifier <= 2**53 - 1:
                raise ValueError("Invalid bridge request ID")
            if message["mode"] == 1:
                self.subscriptions.pop(identifier, None)
                return {"id": identifier, "response": {"code": 0, "data": {}}}
            request = data.get("request", {})
            method, params = request.get("method"), request.get("params") or {}
            if not isinstance(method, str) or not isinstance(params, dict):
                raise ValueError("Invalid bridge request")
            if data.get("persistent"):
                result = self.subscription_data(method)
                self.subscriptions[identifier] = (method, owner)
            else:
                result = self.dispatch(method, params)
            return {"id": identifier, "response": {"code": 0, "data": result}}
        except Exception as exc:
            LOG.warning("Bridge request failed: %s", exc)
            return {"id": identifier, "response": {"code": 1, "message": str(exc), "data": {}}}

    def close(self):
        self.windows.close()
        with self._notification_lock:
            self._closed = True
            if self._notification_timer:
                self._notification_timer.cancel()
        self.indexer.stop()
        if self.maintenance_thread:
            self.maintenance_thread.join(timeout=10)
        if self.backup_thread:
            self.backup_thread.join()
        self.embedder.unload()
        self.store.close()
