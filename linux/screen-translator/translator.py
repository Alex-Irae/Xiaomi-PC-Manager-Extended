#!/usr/bin/env python3
"""Screen Translator for Linux: capture the screen, translate the text on it, show the translation in place.

Purpose: plays the part of the Windows host around the unchanged engine (python -m screen_translator.backend:
    text detection, recognition and translation with OpenVINO).
    translator.py --tray      start in the background and load the models (Hyprland exec-once)
    translator.py --screen    translate the focused monitor, or close the overlay if it is showing
    translator.py --region    translate an area you drag out (needs slurp)
    translator.py --quit
Dependencies (Arch): python-gobject, gtk4, webkitgtk-6.0, gtk4-layer-shell, grim, slurp, libnotify, Hyprland;
    the engine's Python packages in the virtual environment linux/install.sh creates, and the model folder.
Outputs: settings in ~/.config/xiaomi-revamp/screen-translator/settings.json. Captures stay in memory.
Not yet on Linux: the settings window, live re-translation while the screen changes, the "show original" and
    filter shortcuts, unloading the models when idle.
Status: written on Windows; the engine's own tests pass there, this host is untested until it runs on Arch.
"""
import base64
import json
import os
import struct
import subprocess
import sys
import threading
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from revamp import desktop  # noqa: E402
from revamp.webhost import GLib, Pages, WebWindow, run  # noqa: E402

SOURCE = HERE.parents[1] / "source" / "screen-translator"
DEFAULTS = {"mode": "Maximum performance", "devices": ["GPU", "GPU", "GPU"], "batch_size": 8, "incremental": False, "cache": True}


def png_size(data):
    """Width and height from a PNG header, so the picture does not have to be decoded here."""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise RuntimeError("The capture is not a PNG image.")
    return struct.unpack(">II", data[16:24])


class Translator:
    def __init__(self, application):
        self.application = application
        self.config_dir, self.data = desktop.config_dir("screen-translator"), desktop.data_dir("screen-translator")
        self.settings = {**DEFAULTS, "models": str(self.data / "models" / "zh-en")}
        try:
            self.settings.update(json.loads((self.config_dir / "settings.json").read_text(encoding="utf-8")))
        except (OSError, ValueError):
            pass
        self.loaded, self.loading, self.pending, self.capture, self.sequence = False, False, None, None, 0
        self.pages = Pages({"translator.local": HERE / "overlay"}, self.data)
        self.overlay = WebWindow(application, self.pages, self.pages.url("translator.local", "overlay.html"), self.from_page, title="Screen Translator overlay", transparent=True,
                                 layer={"anchor": ("top", "bottom", "left", "right"), "keyboard": "exclusive", "namespace": "revamp-translator"})
        python = desktop.data_dir("") / "venv" / "bin" / "python"
        self.backend = subprocess.Popen([str(python if python.exists() else sys.executable), "-B", "-u", "-m", "screen_translator.backend"], cwd=SOURCE,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8", env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"})
        threading.Thread(target=self.read, daemon=True, name="translator-backend").start()

    # --- the engine ---------------------------------------------------------------------------------------
    def read(self):
        for line in self.backend.stdout:
            try:
                message = json.loads(line)
            except ValueError:
                continue
            GLib.idle_add(lambda value=message: self.from_backend(value) and False)

    def send(self, command, payload=None):
        self.sequence += 1
        self.backend.stdin.write(json.dumps({"id": str(self.sequence), "command": command, "payload": payload or {}}) + "\n")
        self.backend.stdin.flush()

    def load(self):
        if self.loaded or self.loading:
            return
        if not Path(self.settings["models"]).is_dir():
            return self.status("The model folder is missing: " + self.settings["models"] + ". See linux/README.md.", notify=True)
        self.loading = True
        self.send("load", {key: self.settings[key] for key in ("mode", "devices", "batch_size", "models", "incremental")})

    def from_backend(self, message):
        event, value = message.get("event"), message.get("value")
        if event == "progress":
            self.status(str(value))
        elif event == "ready":
            self.loaded, self.loading = True, False
            self.status("")
            if self.pending:
                frame, self.pending = self.pending, None
                self.send("frame", frame)
        elif event == "result" and self.capture:
            self.overlay.send({"regions": value.get("regions", []), "scale": self.capture["scale"], "png": self.capture["png"]})
        elif event == "failed":
            text = value.get("message", "Translation failed.") if isinstance(value, dict) else str(value)
            if self.loading and self.settings["devices"] != ["CPU", "CPU", "CPU"]:
                # The graphics or neural device could not run the models here: fall back to the processor once.
                self.loading, self.settings["devices"], self.settings["mode"] = False, ["CPU", "CPU", "CPU"], "Compatibility"
                self.status("Falling back to the processor: " + text)
                return self.load()
            self.loading = False
            self.status(text, notify=True)

    def status(self, text, notify=False):
        if self.overlay.visible:
            self.overlay.send({"status": text})
        elif notify and text:
            desktop.notify("Screen Translator", text)

    # --- capture ------------------------------------------------------------------------------------------
    def translate(self, region=False):
        if self.overlay.visible:
            return self.close()
        try:
            monitors = desktop.hyprctl("monitors", as_json=True)
            monitor = next((item for item in monitors if item.get("focused")), monitors[0])
            if region:
                area = desktop.run(["slurp"], timeout=120).strip()  # "x,y wxh" in layout coordinates
                command = ["grim", "-t", "png", "-g", area, "-"]
                origin = [int(value) for value in area.split()[0].split(",")]
            else:
                command = ["grim", "-t", "png", "-o", monitor["name"], "-"]
                origin = [monitor.get("x", 0), monitor.get("y", 0)]
            png = subprocess.run(command, capture_output=True, timeout=10, check=True).stdout
        except (RuntimeError, OSError, subprocess.SubprocessError, StopIteration, IndexError, ValueError) as error:
            return desktop.notify("Screen Translator", f"The screen could not be captured: {error}")
        width, height = png_size(png)
        encoded = base64.b64encode(png).decode("ascii")
        # A region is drawn on the whole monitor too: the overlay is offset to where the area starts.
        self.capture = {"png": encoded, "scale": float(monitor.get("scale", 1)), "offset": [origin[0] - monitor.get("x", 0), origin[1] - monitor.get("y", 0)] if region else [0, 0]}
        frame = {"png": encoded, "key": [monitor["name"], int(origin[0]), int(origin[1]), int(width), int(height)], "cache": bool(self.settings["cache"])}
        self.overlay.show()
        self.overlay.focus()
        if self.capture["offset"] != [0, 0]:
            x, y = self.capture["offset"]
            self.overlay.script(f"document.getElementById('regions').style.transform='translate({x}px,{y}px)'")
        else:
            self.overlay.script("document.getElementById('regions').style.transform=''")
        if self.loaded:
            self.overlay.send({"status": "Translating…"})
            self.send("frame", frame)
        else:
            self.pending = frame
            self.overlay.send({"status": "Loading the models…"})
            self.load()

    def close(self):
        self.capture = self.pending = None
        self.overlay.hide()

    def from_page(self, message):
        if message.get("action") == "close":
            self.close()

    def command(self, arguments):
        if "--quit" in arguments:
            try:
                self.send("exit")
                self.backend.wait(timeout=5)
            except (OSError, subprocess.TimeoutExpired):
                self.backend.terminate()
            self.application.quit()
        elif "--region" in arguments:
            self.translate(region=True)
        elif "--screen" in arguments:
            self.translate()
        else:  # --tray or no argument: stay in the background with the models ready
            self.load()


def main():
    state = {}
    return run("io.github.alexirae.revamp.ScreenTranslator", lambda application: state.update(app=Translator(application)), lambda _application, arguments: state["app"].command(arguments))


if __name__ == "__main__":
    sys.exit(main())
