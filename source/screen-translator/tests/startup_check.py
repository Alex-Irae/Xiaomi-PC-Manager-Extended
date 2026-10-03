"""Verify Windows startup and GUI responsiveness during actual model warm-up.

Dependencies: root requirements.txt and provisioned models; no screen capture.
Outputs: terminal progress and a new results/startup/NNN_timestamp_seed0/ report.
Command from project root: python tests/startup_check.py --timeout 240
User initiated only. Does not run a hardware benchmark or save screen/text data.
"""
import argparse
import json
from pathlib import Path
import sys
import traceback
from time import monotonic

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


def main():
    if sys.stdout is not None and hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="backslashreplace")
    parser = argparse.ArgumentParser(description="Check live startup and Settings while warming resident models, without screen capture")
    parser.add_argument("--timeout", type=float, default=240, help="Maximum seconds for model initialization before stopping the owned engine")
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error("Timeout must be positive")
    from multiprocessing import freeze_support
    freeze_support()
    from screen_translator.bootstrap import prefer_system_runtime
    prefer_system_runtime()
    # Imports stay inside main so Windows' spawned engine does not import Qt.
    from PyQt5 import QtCore, QtWidgets
    from screen_translator.desktop import MainWindow, SettingsDialog, STYLE
    from screen_translator.cli import result_directory, save_json, environment
    from screen_translator.models import validate
    application = QtWidgets.QApplication([])
    application.setApplicationName("Screen Translator")
    application.setQuitOnLastWindowClosed(False)
    application.setStyle("Fusion")
    application.setStyleSheet(STYLE)
    window = MainWindow()
    window.show()
    directory = result_directory(Path(__file__).resolve().parents[1]/"results/startup")
    save_json(directory/"config.json", {"seed": 0, "protocol": "Real model initialization with a GUI heartbeat and Settings click; no capture/inference benchmark",
        "timeout_seconds": args.timeout, "app_config": window.config, "environment": environment(),
        "model_manifest": validate(window.config["models"])})
    started = monotonic()
    state = {"heartbeat_ticks": 0, "settings_opened": False, "settings_during_load": False,
             "cancel_responded": False, "models_ready": False, "native_failure_contained": False, "ok": False}
    finished = False
    last_log = started

    def close_settings():
        for widget in application.topLevelWidgets():
            if isinstance(widget, SettingsDialog):
                state["settings_opened"] = widget.isVisible() and widget.mode.isEnabled()
                state["settings_during_load"] = window.busy
                widget.reject()

    def click_settings():
        QtCore.QTimer.singleShot(100, close_settings)
        for widget in window.findChildren(QtWidgets.QPushButton):
            if widget.text() == "Settings":
                widget.click()
                return

    def cancel_and_reload():
        window.cancel_button.click()
        state["cancel_responded"] = not window.busy and window.reload_button.isEnabled()
        window.reload_button.click()

    def finish(ok, reason):
        nonlocal finished
        finished = True
        state.update(ok=ok, reason=reason, elapsed_seconds=monotonic()-started,
                     final_status=window.status.text())
        save_json(directory/"summary.json", state)
        print(json.dumps(state, indent=2), flush=True)
        print(f"Startup check report: {directory.resolve()}", flush=True)
        window.shutdown()

    def heartbeat():
        nonlocal last_log
        if finished:
            return
        now = monotonic()
        state["heartbeat_ticks"] += 1
        if now-last_log >= 1:
            print(f"GUI alive, {now-started:.1f}s: {window.status.text()}", flush=True)
            last_log = now
        if window.ready and not state["models_ready"]:
            state.update(models_ready=True, model_diagnostics=window.last_diagnostics.get("models", {}),
                         screen_button_enabled=window.screen_button.isEnabled(), region_button_enabled=window.region_button.isEnabled())
            # Fault injection stops only this check's owned engine, not another user's process.
            window.worker.process.terminate()
        elif not window.busy and window.status.text().startswith("Action failed:"):
            if state["models_ready"]:
                state["native_failure_contained"] = not window.ready and window.reload_button.isEnabled()
                ok = all(state[key] for key in ("settings_opened", "settings_during_load", "cancel_responded", "models_ready",
                                                "screen_button_enabled", "region_button_enabled", "native_failure_contained"))
                finish(ok, "Startup, Settings, cancel/reload and engine-exit containment passed" if ok else "Recovery/control check failed")
            else:
                finish(False, "Model initialization failed; GUI remained responsive")
        elif now-started >= args.timeout:
            finish(False, "Startup exceeded timeout; GUI remained responsive")

    def callback_failed(kind, error, frames):
        traceback.print_exception(kind, error, frames)
        save_json(directory/"failure.json", {"type": kind.__name__, "error": str(error), "state": state})
        window.shutdown()

    sys.excepthook = callback_failed

    timer = QtCore.QTimer()
    timer.timeout.connect(heartbeat)
    timer.start(50)
    QtCore.QTimer.singleShot(500, click_settings)
    QtCore.QTimer.singleShot(800, cancel_and_reload)
    application.exec_()
    return 0 if state["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
