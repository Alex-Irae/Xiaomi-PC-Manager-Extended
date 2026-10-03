"""Windows WebView2 host and independent global shortcut; no broker dependency."""
import ctypes
import json
import logging
import os
import subprocess
import threading
import time
from ctypes import wintypes

from .config import PROJECT

LOG = logging.getLogger(__name__)


def copy_path(path):
    """Put a UTF-16 path on the Windows clipboard without requiring WebView focus."""
    user = ctypes.WinDLL("user32", use_last_error=True)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    user.OpenClipboard.argtypes = (wintypes.HWND,)
    user.OpenClipboard.restype = wintypes.BOOL
    user.SetClipboardData.argtypes = (wintypes.UINT, wintypes.HANDLE)
    user.SetClipboardData.restype = wintypes.HANDLE
    kernel.GlobalAlloc.argtypes = (wintypes.UINT, ctypes.c_size_t)
    kernel.GlobalAlloc.restype = wintypes.HGLOBAL
    kernel.GlobalLock.argtypes = (wintypes.HGLOBAL,)
    kernel.GlobalLock.restype = ctypes.c_void_p
    kernel.GlobalUnlock.argtypes = (wintypes.HGLOBAL,)
    kernel.GlobalFree.argtypes = (wintypes.HGLOBAL,)
    kernel.GlobalFree.restype = wintypes.HGLOBAL
    encoded = (path + "\0").encode("utf-16-le")
    handle = kernel.GlobalAlloc(0x0002, len(encoded))
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    pointer = kernel.GlobalLock(handle)
    if not pointer:
        kernel.GlobalFree(handle)
        raise ctypes.WinError(ctypes.get_last_error())
    ctypes.memmove(pointer, encoded, len(encoded))
    kernel.GlobalUnlock(handle)
    transferred = False
    if not user.OpenClipboard(None):
        kernel.GlobalFree(handle)
        raise RuntimeError("Clipboard is busy. Try copying the path again")
    try:
        if not user.EmptyClipboard() or not user.SetClipboardData(13, handle):
            raise ctypes.WinError(ctypes.get_last_error())
        transferred = True
    finally:
        user.CloseClipboard()
        if not transferred:
            kernel.GlobalFree(handle)


def run(service):
    import webview
    from pynput import keyboard

    if os.name != "nt":
        raise RuntimeError("The desktop host supports Windows 11 only")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateMutexW.argtypes = (ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR)
    kernel.CreateMutexW.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
    kernel.CreateEventW.argtypes = (ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR)
    kernel.CreateEventW.restype = wintypes.HANDLE
    kernel.SetEvent.argtypes = (wintypes.HANDLE,)
    kernel.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
    show_event = kernel.CreateEventW(None, False, False, "Local\\XiaomiSemanticSearchShow")
    if not show_event:
        raise ctypes.WinError(ctypes.get_last_error())
    mutex = kernel.CreateMutexW(None, False, "Local\\XiaomiSemanticSearchDesktop")
    if not mutex:
        error = ctypes.get_last_error()
        kernel.CloseHandle(show_event)
        raise ctypes.WinError(error)
    if ctypes.get_last_error() == 183:
        kernel.SetEvent(show_event)
        kernel.CloseHandle(show_event)
        kernel.CloseHandle(mutex)
        return

    windows = {}
    quitting = threading.Event()

    class Api:
        def __init__(self, owner):
            self._owner = owner

        def rpc(self, message):
            return service.rpc(message, self._owner)

    def focus_search():
        window = windows["search"]
        window.show()
        window.restore()
        try:
            user = ctypes.WinDLL("user32", use_last_error=True)
            user.SetForegroundWindow.argtypes = (wintypes.HWND,)
            user.SetForegroundWindow.restype = wintypes.BOOL
            user.SetForegroundWindow(wintypes.HWND(window.native.Handle.ToInt64()))
            window.evaluate_js("window.dispatchEvent(new Event('local-focus'));document.querySelector('input[type=text]')?.focus()")
        except Exception:
            LOG.debug("Native foreground activation unavailable")

    def action(name, params):
        if name == "open":
            os.startfile(params["path"])
        elif name == "reveal":
            subprocess.Popen(["explorer.exe", "/select,", params["path"]], creationflags=subprocess.CREATE_NO_WINDOW)
        elif name == "copy":
            copy_path(params["path"])
        elif name == "choose_folder":
            selection = windows["manager"].create_file_dialog(webview.FileDialog.FOLDER)
            return selection[0] if selection else None
        elif name == "resize":
            windows["search"].resize(params["width"], params["height"])
        elif name == "appearance":
            windows["search"].evaluate_js("window.dispatchEvent(new CustomEvent('settings-changed',{detail:" + json.dumps(params) + "}))")
        elif name in ("manage", "show_search"):
            if name == "show_search":
                focus_search()
            else:
                windows["search"].hide()
                windows["manager"].show()
                windows["manager"].restore()
        elif name == "hide":
            windows["search"].hide()
        elif name == "quit":
            quitting.set()
            for window in windows.values():
                window.destroy()

    service.window_action = action

    def emit(message):
        owner = message.pop("owner", "search")
        window = windows.get(owner)
        if window:
            window.evaluate_js("window.localBridge?.receive(" + json.dumps(message) + ")")

    service.emit = emit
    windows["search"] = webview.create_window("Local Search", str(PROJECT / "frontend/search.html"), js_api=Api("search"), width=700, height=96, min_size=(500, 70), frameless=True, easy_drag=False, on_top=True, background_color="#F7F8FA")
    windows["manager"] = webview.create_window("Search settings", str(PROJECT / "frontend/settings.html"), js_api=Api("manager"), width=460, height=650, min_size=(400, 420), hidden=True, background_color="#F7F8FA")

    def keep_resident(window):
        if not quitting.is_set():
            window.hide()
            if window == windows["manager"]:
                focus_search()
            return False

    for window in windows.values():
        window.events.closing += lambda w=window: keep_resident(w)

    pressed = set()
    control_keys = {keyboard.Key.ctrl, keyboard.Key.ctrl_l, keyboard.Key.ctrl_r}
    alt_keys = {keyboard.Key.alt, keyboard.Key.alt_l, keyboard.Key.alt_r}
    state = {"last_ctrl": 0.0, "ctrl_down": 0.0, "chord": False}

    def press(key):
        if key in pressed:
            return
        pressed.add(key)
        if key in control_keys:
            if len(pressed & control_keys) == 1:
                state["ctrl_down"] = time.monotonic()
                state["chord"] = bool(pressed - control_keys)
        elif pressed & control_keys:
            state["chord"] = True
            state["last_ctrl"] = 0.0
        if service.config["shortcut"] == "alt_space" and key == keyboard.Key.space and pressed & alt_keys:
            threading.Thread(target=focus_search, daemon=True).start()

    def release(key):
        pressed.discard(key)
        if key not in control_keys or pressed & control_keys:
            return
        now = time.monotonic()
        if service.config["shortcut"] == "double_ctrl" and not state["chord"] and now - state["ctrl_down"] < 0.6:
            if 0 < now - state["last_ctrl"] <= service.config["double_ctrl_ms"] / 1000:
                state["last_ctrl"] = 0.0
                threading.Thread(target=focus_search, daemon=True).start()
            else:
                state["last_ctrl"] = now
        else:
            state["last_ctrl"] = 0.0

    listener = keyboard.Listener(on_press=press, on_release=release)

    def startup():
        if service.config["shortcut"] != "none":
            listener.start()
        def show_requests():
            while not quitting.is_set():
                if kernel.WaitForSingleObject(show_event, 250) == 0:
                    focus_search()
        threading.Thread(target=show_requests, daemon=True).start()
        service.indexer.start()
        service.indexer.request_scan()
        LOG.info("Resident search ready | shortcut=%s | roots=%d", service.config["shortcut"], len(service.config["roots"]))
        if not service.config["roots"]:
            action("manage", {})

    try:
        # pywebview uses a local static HTTP server for module assets. There is no
        # remotely accessible search HTTP API; operations go through the WebView.
        webview.start(startup, gui="edgechromium", http_server=True, private_mode=True, storage_path=str(service.data / "webview"))
    finally:
        listener.stop()
        service.close()
        kernel.CloseHandle(mutex)
        quitting.set()
        kernel.CloseHandle(show_event)
