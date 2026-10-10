#!/usr/bin/env python3
"""AI Center file search for Linux: the search bar and the settings window over the unchanged search engine.

Purpose: plays the part of the Windows host (native/Program.cs). It starts the engine
    (python -m xiaomi_search.backend, the same worker as on Windows), shows the existing pages, passes their
    requests on and performs what the engine asks of the desktop (open a file, copy a path...).
    ai_center.py --tray       start in the background (Hyprland exec-once)
    ai_center.py --search     show or hide the search bar (bind this to a key)
    ai_center.py --center     open AI Center (index, folders, settings)
    ai_center.py --quit
Dependencies (Arch): python-gobject, gtk4, webkitgtk-6.0, gtk4-layer-shell, xdg-utils, wl-clipboard; the engine's
    Python packages in the virtual environment linux/install.sh creates, and the embedding model folder.
Outputs: settings in ~/.config/xiaomi-revamp/file-search, index and logs in ~/.local/share/xiaomi-revamp/file-search.
Not yet on Linux: program results, the Windows Search channel, idle memory release (the worker stays loaded
    according to the "Search model in memory" setting, as with "keep loaded"), ranking by remembered choices.
Status: written on Windows; the engine's own checks pass there, this host is untested until it runs on Arch.
"""
import json
import os
import subprocess
import sys
import threading
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from revamp import desktop  # noqa: E402
from revamp.webhost import GLib, Gtk, Pages, WebWindow, run  # noqa: E402

SOURCE = HERE.parents[1] / "source" / "file-search"
HOST = "ai-center.local"
# What differs from the Windows defaults in config.example.json. Everything else is shared.
LINUX_DEFAULTS = {
    "index_protection": "none", "windows_semantic_enabled": False, "programs_enabled": False, "shortcut": "none", "run_at_startup": False,
    "excluded_names": [".git", ".venv", "venv", ".conda", "node_modules", "__pycache__", ".cache", ".local", ".mozilla", ".cargo", ".rustup", ".npm", ".var", "Trash", "lost+found"],
}


class Center:
    def __init__(self, application):
        self.application = application
        self.config_dir, self.data = desktop.config_dir("file-search"), desktop.data_dir("file-search")
        self.config_dir.mkdir(parents=True, exist_ok=True)
        self.data.mkdir(parents=True, exist_ok=True)
        self.config = self.config_dir / "config.json"
        if not self.config.exists():
            self.config.write_text(json.dumps({**LINUX_DEFAULTS, "model_path": str(self.data / "models" / "qwen3-embedding")}, indent=2), encoding="utf-8")
        self.settings, self.settings_history, self.ready = {}, None, False
        self.write_lock = threading.Lock()
        self.pages = Pages({HOST: SOURCE / "frontend"}, self.data)
        self.search = WebWindow(application, self.pages, self.pages.url(HOST, "search.html"), lambda message: self.from_page(self.search, "search", message),
                                title="Local Search · AI Center", size=(700, 78), transparent=True,
                                layer={"anchor": ("top",), "margin": {"top": 220}, "keyboard": "exclusive", "namespace": "revamp-search"})
        self.center = WebWindow(application, self.pages, self.pages.url(HOST, "center.html"), lambda message: self.from_page(self.center, "manager", message),
                                title="AI Center", size=(1180, 800))
        self.start_worker()

    # --- the engine ---------------------------------------------------------------------------------------
    def start_worker(self):
        python = desktop.data_dir("") / "venv" / "bin" / "python"
        command = [str(python if python.exists() else sys.executable), "-B", "-m", "xiaomi_search.backend", "--config", str(self.config), "--data", str(self.data), "--no-shortcut"]
        self.worker = subprocess.Popen(command, cwd=SOURCE, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8",
                                       env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"})

        def read():
            for line in self.worker.stdout:
                try:
                    message = json.loads(line)
                except ValueError:
                    continue
                GLib.idle_add(lambda value=message: self.from_worker(value) and False)
            GLib.idle_add(lambda: self.stopped() and False)
        threading.Thread(target=read, daemon=True, name="search-worker").start()

    def stopped(self):
        self.ready = False
        for window in (self.search, self.center):
            window.send({"kind": "backend_stopped"})

    def write(self, message):
        with self.write_lock:
            if self.worker.poll() is None:
                self.worker.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
                self.worker.stdin.flush()

    def from_worker(self, message):
        kind = message.get("kind")
        if kind == "ready":
            self.ready, self.settings = True, message.get("settings", {})
            for window in (self.search, self.center):
                window.send(message)
        elif kind == "action":
            self.action(message.get("token"), message.get("action"), message.get("params") or {})
        elif kind is None and message.get("owner") in ("manager", "search"):
            (self.center if message["owner"] == "manager" else self.search).send(message)

    def action(self, token, name, params):
        """What the engine asks the desktop to do. Every request is answered, or the engine waits three minutes."""
        def done(value=None, error=None):
            if token:
                self.write({"kind": "action_result", "token": token, "value": value, "error": error})
        try:
            if name in ("open", "open_with"):
                desktop.open_path(params["path"])
                done({"opened": True})
            elif name == "folder":
                desktop.open_path(params["path"])
                done()
            elif name == "reveal":
                desktop.reveal(params["path"])
                done()
            elif name == "copy":
                desktop.copy_text(params["path"])
                done()
            elif name == "choose_folder":
                dialog = Gtk.FileDialog(title="Choose a backup or folder location")
                dialog.select_folder(self.center.window, None, lambda source, result: self.chosen(source, result, done))
            elif name == "resize":
                self.search.resize(int(params["width"]), int(params["height"]))
                done()
            elif name == "appearance":
                self.settings = params
                self.search.script("window.dispatchEvent(new CustomEvent('settings-changed',{detail:" + json.dumps(params) + "}))")
                done()
            elif name == "hide":
                self.search.hide()
                done()
            elif name == "show_search":
                self.show_search()
                done()
            elif name == "manage":
                self.show_center()
                done()
            elif name == "quit":
                done()
                self.quit()
            else:
                done(error="Unknown native action " + str(name))
        except (RuntimeError, OSError, KeyError, ValueError) as error:
            done(error=str(error))

    @staticmethod
    def chosen(dialog, result, done):
        try:
            done(dialog.select_folder_finish(result).get_path())
        except GLib.Error:
            done(None)  # the dialog was closed without a choice

    # --- the pages ----------------------------------------------------------------------------------------
    def reply(self, window, data, result=None, code=0, text=""):
        if "id" in data:
            window.send({"id": data["id"], "response": {"code": code, "message": text, "data": result}})

    def history(self, method, params):
        """Recent queries. A plain JSON file here; on Windows this file is sealed with the account's keys."""
        path = self.data / "history.json"
        try:
            queries = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            queries = []
        if method == "local_history_save":
            queries = [str(value) for value in params.get("queries", [])][:50]
        elif method == "local_history_remember":
            queries = ([params["query"]] + [value for value in queries if value != params["query"]])[:50]
        elif method == "local_history_remove":
            queries = [value for value in queries if value != params["query"]]
        elif method == "local_history_clear":
            queries = []
        if method != "local_history_get":
            path.write_text(json.dumps(queries), encoding="utf-8")
        return {"queries": queries, "opened": {}, "saved": True}

    def from_page(self, window, owner, message):
        if "host" in message:
            return self.host_action(window, message["host"])
        data = message.get("data")
        if not isinstance(data, dict):
            return
        request = data.get("request") or {}
        method, params = request.get("method", ""), request.get("params") or {}
        try:
            if method == "local_config":
                return self.reply(window, data, {"settings": self.settings, "path": str(self.config), "version": "0.3.13-linux"})
            if method == "local_xiaoai":
                return self.reply(window, data, {"installed": False, "icon": None})
            if method == "local_suite_manager":
                return self.reply(window, data, None, 1, "PC Manager is opened from its own shortcut on Linux.")
            if method in ("local_index_info", "local_index_folder"):
                index = self.data / "index.sqlite3"
                if method == "local_index_folder":
                    desktop.open_path(self.data)
                saved = __import__("datetime").datetime.fromtimestamp(index.stat().st_mtime).astimezone().isoformat() if index.exists() else None
                return self.reply(window, data, {"path": str(index), "bytes": index.stat().st_size if index.exists() else 0, "saved": saved, "model_path": self.settings.get("model_path")})
            if method in ("local_exclusions_get", "local_exclusions_edit"):
                path = self.config_dir / "excluded-folders.txt"
                if not path.exists():
                    path.write_text("# Excluded folders: one absolute path per line. Save to apply.\n" + "\n".join(self.settings.get("excluded_folders", [])) + "\n", encoding="utf-8")
                if method == "local_exclusions_edit":
                    desktop.open_path(path)
                lines = [line for line in path.read_text(encoding="utf-8").splitlines() if line.strip() and not line.lstrip().startswith("#")]
                return self.reply(window, data, {"path": str(path), "count": len(lines)})
            if method == "local_drives":
                return self.reply(window, data, {"roots": [str(Path.home())]})
            if method in ("local_profile_get", "local_profile_choose", "local_profile_reset"):
                return self.reply(window, data, {"image": None})
            if method == "local_settings_history_get":
                return self.reply(window, data, self.settings_history)
            if method == "local_settings_history_save":
                self.settings_history = params
                return self.reply(window, data, {"saved": True})
            if method.startswith("local_history_"):
                state = self.history(method, params)
                self.reply(window, data, state)
                if method == "local_history_clear":
                    for target in (self.search, self.center):
                        target.send({"kind": "history_changed", "queries": state["queries"]})
                return
            if method == "window_resize":
                self.search.resize(max(320, min(1400, int(params["width"]))), max(78, min(1000, int(params["height"]))))
                return self.reply(window, data)
            if method == "local_hide":
                self.search.hide()
                return self.reply(window, data)
            if method == "local_quit":
                return self.quit()
        except (OSError, KeyError, ValueError, RuntimeError) as error:
            return self.reply(window, data, None, 1, str(error))
        if not self.ready:
            return self.reply(window, data, None, 1, "The search engine is still starting.")
        self.write({**message, "owner": owner})

    def host_action(self, window, action):
        if action == "search":
            self.show_search()
        elif action in ("hide", "close"):
            window.hide()
        elif action == "minimize":
            window.window.minimize()
        elif action == "maximize":
            window.window.unmaximize() if window.window.is_maximized() else window.window.maximize()
        elif action == "fullscreen":
            window.window.unfullscreen() if window.window.is_fullscreen() else window.window.fullscreen()
        elif action == "quit":
            self.quit()
        # theme, drag, drag_start, drag_move: the compositor draws and moves windows here

    # --- windows ------------------------------------------------------------------------------------------
    def show_search(self):
        self.center.hide()
        self.search.show()
        self.search.focus()
        self.search.script("window.dispatchEvent(new Event('local-search-open'))")
        if self.ready:  # load the model while the first characters are typed, as on Windows
            self.write({"owner": "suite", "mode": 0, "data": {"id": 0, "persistent": False, "request": {"method": "local_warm", "params": {}}}})

    def hide_search(self):
        self.search.script("window.dispatchEvent(new Event('local-search-dismiss'))")
        self.search.hide()

    def show_center(self):
        self.search.hide()
        self.center.show()
        self.center.script("window.dispatchEvent(new Event('local-center-focus'))")

    def quit(self):
        self.write({"kind": "shutdown"})
        try:
            self.worker.stdin.close()
            self.worker.wait(timeout=10)
        except (OSError, subprocess.TimeoutExpired):
            self.worker.terminate()
        self.application.quit()

    def command(self, arguments):
        if "--quit" in arguments:
            self.quit()
        elif "--search" in arguments:
            self.hide_search() if self.search.visible else self.show_search()
        elif "--center" in arguments or not arguments:
            self.show_center()
        # --tray: stay in the background


def main():
    state = {}
    return run("io.github.alexirae.revamp.AICenter", lambda application: state.update(center=Center(application)), lambda _application, arguments: state["center"].command(arguments))


if __name__ == "__main__":
    sys.exit(main())
