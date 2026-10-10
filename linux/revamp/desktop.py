"""Small desktop helpers for the Linux apps: folders, opening files, clipboard, notifications, Hyprland.

Purpose: one place for what the Windows hosts do with Win32 calls.
Dependencies: Python 3.11+. At run time: xdg-utils (xdg-open), wl-clipboard (wl-copy), libnotify (notify-send),
    Hyprland (hyprctl). Each helper reports a missing program instead of failing silently.
Outputs: none. Usage: imported by the app hosts; `run` can be replaced in checks.
"""
import json
import os
import shutil
import subprocess
from pathlib import Path

APP = "xiaomi-revamp"


def config_dir(component):
    return Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config") / APP / component


def data_dir(component):
    return Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share") / APP / component


def run(arguments, *, text=None, timeout=10):
    """Run a program and return its output. Raises RuntimeError naming the program when it is missing or fails."""
    if shutil.which(arguments[0]) is None:
        raise RuntimeError(f"{arguments[0]} is not installed.")
    done = subprocess.run(arguments, input=text, capture_output=True, text=True, timeout=timeout)
    if done.returncode != 0:
        raise RuntimeError(f"{arguments[0]} failed: {(done.stderr or done.stdout).strip()[:300]}")
    return done.stdout


def open_path(path):
    subprocess.Popen(["xdg-open", str(path)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def reveal(path):
    """Open the folder that holds `path` (file managers differ too much to select the file itself)."""
    target = Path(path)
    open_path(target if target.is_dir() else target.parent)


def copy_text(text):
    run(["wl-copy"], text=text)


def notify(title, body=""):
    try:
        run(["notify-send", "--app-name", "Xiaomi Revamp", title, body])
    except RuntimeError:
        pass  # a missing notification service must not break the action that wanted to report


def hyprctl(*arguments, as_json=False, runner=run):
    output = runner(["hyprctl", *(["-j"] if as_json else []), *arguments])
    return json.loads(output) if as_json else output
