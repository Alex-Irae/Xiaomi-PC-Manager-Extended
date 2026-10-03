"""Windows 11 desktop controller. Dependencies: root requirements.txt.

Outputs: local settings and rotating timing logs under the app data directory.
No screenshot/text persistence during ordinary operation.
Command from project root: python -m screen_translator
"""
import ctypes
from ctypes import wintypes
import json
import logging
from logging.handlers import RotatingFileHandler
import multiprocessing
from pathlib import Path
from queue import Queue, Empty
import sys
from threading import Event
from time import perf_counter

from PyQt5 import QtCore, QtGui, QtWidgets

from .engine import serve
from .hardware import MODES
from .render import ReplacementOverlay, qimage_to_pil

ROOT = Path(__file__).resolve().parents[1]
STYLE = """
QWidget { color:#222630; font-family:'Segoe UI'; font-size:14px; }
QWidget#main { background:#f5f6f8; }
QFrame#card { background:#ffffff; border:1px solid #e9ecf1; border-radius:16px; }
QLabel#title { font-size:27px; font-weight:600; }
QLabel#section { font-size:17px; font-weight:600; }
QLabel#muted { color:#717986; }
QLabel#badge { color:#cf5300; background:#fff0e5; padding:6px 10px; border-radius:9px; }
QPushButton { background:#ffffff; border:1px solid #dfe3e9; border-radius:10px; padding:11px 15px; }
QPushButton:hover { background:#edf2f8; }
QPushButton:pressed { background:#e3eaf4; }
QPushButton#primary { background:#ff6900; color:white; border:0; font-weight:600; padding:16px; }
QPushButton#primary:hover { background:#ec6100; }
QPushButton:disabled { color:#9ca3ad; background:#e8ebef; }
QComboBox,QLineEdit,QSpinBox,QDoubleSpinBox { background:white; border:1px solid #dfe3e9; border-radius:7px; padding:7px; }
QTabWidget::pane { border:0; }
QTabBar::tab { padding:12px; background:#edf0f4; }
QTabBar::tab:selected { color:#d65400; background:white; }
QPlainTextEdit { background:#fafbfc; border:1px solid #e3e7ed; border-radius:8px; padding:8px; }
QCheckBox { spacing:9px; }
"""


def label(text, name=None):
    widget = QtWidgets.QLabel(text)
    if name:
        widget.setObjectName(name)
    widget.setWordWrap(True)
    return widget


def button(text, callback, primary=False):
    widget = QtWidgets.QPushButton(text)
    widget.setCursor(QtCore.Qt.PointingHandCursor)
    if primary:
        widget.setObjectName("primary")
    widget.clicked.connect(callback)
    return widget


class ResidentWorker(QtCore.QThread):
    ready = QtCore.pyqtSignal(object)
    result = QtCore.pyqtSignal(object, object, object, object)
    failed = QtCore.pyqtSignal(str, object)
    progress = QtCore.pyqtSignal(str, object)
    downloaded = QtCore.pyqtSignal(object)
    benchmarked = QtCore.pyqtSignal(object)

    def __init__(self):
        super().__init__()
        self.queue = Queue()
        self.stop_requested, self.cancel_requested = Event(), Event()
        self.process, self.connection = None, None
        self.cancelled_load_through, self.cancelled_frame_through = -1, -1
        self.stage = "Starting isolated inference process"

    def cancel(self, load_generation, frame_generation):
        self.cancelled_load_through = max(self.cancelled_load_through, load_generation)
        self.cancelled_frame_through = max(self.cancelled_frame_through, frame_generation)
        self.cancel_requested.set()

    def stop(self):
        self.stop_requested.set()

    def dispose_engine(self):
        if self.process is not None:
            if self.process.is_alive():
                self.process.terminate()
            if self.process.pid is not None:
                self.process.join(timeout=1)
            if self.process.is_alive():
                self.process.kill()
                self.process.join(timeout=1)
            if not self.process.is_alive():
                self.process.close()
            self.process = None
        if self.connection is not None:
            self.connection.close()
            self.connection = None

    def start_engine(self):
        context = multiprocessing.get_context("spawn")
        self.connection, child = context.Pipe()
        self.process = context.Process(target=serve, args=(child,), daemon=True,
                                       name="ScreenTranslatorInference")
        try:
            self.process.start()
        finally:
            child.close()

    def crash_message(self, error):
        code = self.process.exitcode if self.process is not None else None
        details = f"exit code 0x{code & 0xffffffff:08X}" if code is not None else f"{type(error).__name__}: {error}"
        return f"Inference process stopped ({details}). Last stage: {self.stage}. Reload models or choose another hardware profile in Settings."

    def run(self):
        try:
            while not self.stop_requested.is_set():
                if self.cancel_requested.is_set():
                    self.dispose_engine()
                    self.cancel_requested.clear()
                try:
                    command, payload = self.queue.get(timeout=.1)
                except Empty:
                    if self.process is not None and not self.process.is_alive():
                        error = self.crash_message(RuntimeError("Engine exited while idle"))
                        self.dispose_engine()
                        self.failed.emit(error, {"kind": "engine", "generation": None, "engine_stopped": True})
                    continue
                if command == "exit":
                    return
                generation = payload["generation"] if command in ("load", "download", "benchmark") else payload[1]["generation"] if command == "frame" else None
                if self.cancel_requested.is_set():
                    self.dispose_engine()
                    self.cancel_requested.clear()
                if command in ("load", "download", "benchmark") and generation <= self.cancelled_load_through:
                    continue
                if command == "frame" and generation <= self.cancelled_frame_through:
                    continue
                context = {"kind": command, "generation": generation}
                try:
                    if self.process is None:
                        self.stage = "Starting isolated inference process"
                        self.progress.emit(self.stage, context)
                        self.start_engine()
                    wire_payload = payload
                    if command == "frame":
                        # QRect stays in the GUI process; image/keys/caches travel in RAM only.
                        image, frame_context = payload
                        wire_payload = (image, {key: frame_context[key] for key in ("key", "cache")})
                    self.connection.send((command, wire_payload))
                    while not self.stop_requested.is_set() and not self.cancel_requested.is_set():
                        if not self.connection.poll(.1):
                            if not self.process.is_alive():
                                raise RuntimeError("Engine exited during operation")
                            continue
                        event, value = self.connection.recv()
                        if event == "progress":
                            self.stage = value
                            self.progress.emit(value, context)
                            continue
                        if event == "failed":
                            self.failed.emit(value, context)
                        elif event == "result":
                            regions, timing = value
                            self.result.emit(payload[0], regions, timing, payload[1])
                        elif event != "cleared":
                            getattr(self, event).emit(value)
                        break
                except Exception as exc:
                    # Native DLL crashes become recoverable UI failures, with no CPU retry.
                    error = self.crash_message(exc)
                    self.dispose_engine()
                    self.failed.emit(error, {**context, "engine_stopped": True})
        finally:
            self.dispose_engine()


class Hotkeys(QtCore.QAbstractNativeEventFilter):
    def __init__(self, window):
        super().__init__()
        self.window = window
        self.api = ctypes.WinDLL("user32", use_last_error=True)
        self.api.RegisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int, wintypes.UINT, wintypes.UINT]
        self.api.RegisterHotKey.restype = wintypes.BOOL
        self.api.UnregisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int]
        self.api.UnregisterHotKey.restype = wintypes.BOOL
        self.hwnd, self.registered = int(window.winId()), []
        self.callbacks = {1: window.translate_screen, 2: window.select_region,
                          3: window.toggle_original, 4: window.toggle_filter}
        failures = []
        for identifier, key in ((1, "F"), (2, "T"), (3, "O"), (4, "D")):
            if self.api.RegisterHotKey(self.hwnd, identifier, 0x4000 | 0x0001 | 0x0002, ord(key)):
                self.registered.append(identifier)
            else:
                failures.append(f"Ctrl+Alt+{key}")
        self.failures = failures

    def nativeEventFilter(self, event_type, message):
        msg = wintypes.MSG.from_address(int(message))
        if msg.message == 0x0312 and int(msg.wParam) in self.callbacks:
            self.callbacks[int(msg.wParam)]()
            return True, 0
        return False, 0

    def close(self):
        for identifier in self.registered:
            self.api.UnregisterHotKey(self.hwnd, identifier)
        self.registered.clear()


class RegionSelector(QtWidgets.QWidget):
    selected = QtCore.pyqtSignal(object)
    canceled = QtCore.pyqtSignal()

    def __init__(self, screen):
        super().__init__(None, QtCore.Qt.Tool | QtCore.Qt.FramelessWindowHint | QtCore.Qt.WindowStaysOnTopHint)
        self.setAttribute(QtCore.Qt.WA_TranslucentBackground)
        self.setGeometry(screen.geometry())
        self.setCursor(QtCore.Qt.CrossCursor)
        self.anchor, self.selection = None, QtCore.QRect()
        self.show()
        self.activateWindow()

    def paintEvent(self, event):
        painter = QtGui.QPainter(self)
        painter.fillRect(self.rect(), QtGui.QColor(0, 0, 0, 75))
        painter.setPen(QtGui.QPen(QtGui.QColor("#ff6900"), 2))
        painter.drawRect(self.selection)
        painter.setPen(QtCore.Qt.white)
        painter.drawText(24, 38, "Drag to select a region. Esc to cancel.")

    def mousePressEvent(self, event):
        if event.button() == QtCore.Qt.LeftButton:
            self.anchor = event.pos()

    def mouseMoveEvent(self, event):
        if self.anchor is not None:
            self.selection = QtCore.QRect(self.anchor, event.pos()).normalized().intersected(self.rect())
            self.update()

    def mouseReleaseEvent(self, event):
        if self.selection.width() >= 5 and self.selection.height() >= 5:
            selection = QtCore.QRect(self.selection)
            self.close()
            self.selected.emit(selection)
        else:
            self.close()
            self.canceled.emit()

    def keyPressEvent(self, event):
        if event.key() == QtCore.Qt.Key_Escape:
            self.close()
            self.canceled.emit()


class SettingsDialog(QtWidgets.QDialog):
    def __init__(self, owner):
        super().__init__(owner)
        self.setWindowTitle("Screen Translator settings")
        self.resize(680, 530)
        layout = QtWidgets.QVBoxLayout(self)
        tabs = QtWidgets.QTabWidget()
        layout.addWidget(tabs)
        pages = {}
        for name in ("Translation", "Performance", "Overlay", "Advanced"):
            page = QtWidgets.QWidget()
            pages[name] = QtWidgets.QFormLayout(page)
            pages[name].setSpacing(16)
            tabs.addTab(page, name)
        pages["Translation"].addRow("Source", label("Chinese (Simplified)"))
        pages["Translation"].addRow("Target", label("English"))
        self.models = QtWidgets.QLineEdit(owner.config["models"])
        self.models.setAccessibleName("Local model bundle directory")
        row = QtWidgets.QHBoxLayout()
        row.addWidget(self.models)
        row.addWidget(button("Browse", self.browse_models))
        pages["Translation"].addRow("Local models", row)
        pages["Translation"].addRow(label("Translation is offline. Model downloads happen only when you explicitly start provisioning.", "muted"))
        self.mode = QtWidgets.QComboBox()
        self.mode.addItems(["Automatic"]+list(MODES))
        self.mode.setCurrentText(owner.config["mode"])
        pages["Performance"].addRow("Hardware mode", self.mode)
        self.devices = []
        for index, name in enumerate(("Detector device", "Recognizer device", "Translator device")):
            field = QtWidgets.QLineEdit(owner.config["devices"][index])
            field.setPlaceholderText("CPU, GPU.0, NPU")
            pages["Performance"].addRow(name, field)
            self.devices.append(field)
        self.mode.currentTextChanged.connect(self.assign_devices)
        self.report = QtWidgets.QLineEdit(owner.config["benchmark"])
        row = QtWidgets.QHBoxLayout()
        row.addWidget(self.report)
        row.addWidget(button("Browse", self.browse_report))
        pages["Performance"].addRow("Benchmark summary", row)
        pages["Performance"].addRow(label("Balanced uses NPU detection and GPU recognition/translation. Use Benchmark hardware on the main window to measure CPU, GPU and NPU alternatives and select Automatic mode.", "muted"))
        self.batch = QtWidgets.QSpinBox()
        self.batch.setRange(1, 64)
        self.batch.setValue(owner.config["batch_size"])
        pages["Performance"].addRow("Translation batch", self.batch)
        self.cache = QtWidgets.QCheckBox("Reuse local translation and frame caches")
        self.cache.setChecked(owner.config["cache"])
        pages["Performance"].addRow(self.cache)
        self.incremental = QtWidgets.QCheckBox("Only OCR changed screen areas")
        self.incremental.setChecked(owner.config["incremental"])
        pages["Performance"].addRow(self.incremental)
        self.interval = QtWidgets.QSpinBox()
        self.interval.setRange(200, 10000)
        self.interval.setValue(owner.config["interval_ms"])
        self.interval.setSuffix(" ms")
        pages["Performance"].addRow("Filter polling", self.interval)
        pages["Overlay"].addRow("Replacement", label("Fast: sampled opaque background, no borders"))
        pages["Overlay"].addRow("Interaction", label("Click-through always enabled"))
        pages["Overlay"].addRow("Original view", label("Ctrl + Alt + O"))
        self.font = QtWidgets.QDoubleSpinBox()
        self.font.setRange(.7, 1.5)
        self.font.setSingleStep(.05)
        self.font.setValue(owner.config["font_scale"])
        pages["Overlay"].addRow("Font scale", self.font)
        pages["Overlay"].addRow(label("Long translations shrink, wrap, then show an ellipsis within the original region. Text never expands across neighboring controls.", "muted"))
        strict = QtWidgets.QCheckBox("Strict offline mode")
        strict.setChecked(True)
        strict.setEnabled(False)
        pages["Advanced"].addRow(strict)
        self.debug = QtWidgets.QCheckBox("Show replacement bounds")
        self.debug.setChecked(owner.config["debug"])
        pages["Advanced"].addRow(self.debug)
        pages["Advanced"].addRow(button("Clear caches", owner.clear_cache))
        pages["Advanced"].addRow(button("Export local diagnostics", owner.export_diagnostics))
        pages["Advanced"].addRow(label("Logs contain timings and counts. Screenshots and recognized text are never stored during normal translation.", "muted"))
        buttons = QtWidgets.QDialogButtonBox(QtWidgets.QDialogButtonBox.Save | QtWidgets.QDialogButtonBox.Cancel)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    def assign_devices(self, mode):
        if mode in MODES:
            for field, device in zip(self.devices, MODES[mode]):
                field.setText(device)

    def browse_models(self):
        path = QtWidgets.QFileDialog.getExistingDirectory(self, "Local model bundle", self.models.text())
        if path:
            self.models.setText(path)

    def browse_report(self):
        path, _ = QtWidgets.QFileDialog.getOpenFileName(self, "Benchmark summary", "", "JSON (*.json)")
        if path:
            self.report.setText(path)

    def values(self):
        return {"models": self.models.text().strip(), "mode": self.mode.currentText(),
                "devices": [field.text().strip() for field in self.devices],
                "benchmark": self.report.text().strip(), "batch_size": self.batch.value(),
                "cache": self.cache.isChecked(), "incremental": self.incremental.isChecked(),
                "interval_ms": self.interval.value(), "font_scale": self.font.value(), "debug": self.debug.isChecked()}


class MainWindow(QtWidgets.QWidget):
    def __init__(self):
        super().__init__()
        self.setObjectName("main")
        self.setWindowTitle("Screen Translator")
        self.resize(500, 700)
        self.setMinimumWidth(440)
        self.settings_store = QtCore.QSettings("ScreenTranslator", "Overlay")
        defaults = {"models": str(ROOT/"models/zh-en"), "mode": "Balanced", "devices": list(MODES["Balanced"]),
                    "benchmark": "", "batch_size": 8, "cache": True, "incremental": True,
                    "interval_ms": 600, "font_scale": 1.0, "debug": False}
        try:
            saved = json.loads(self.settings_store.value("config", "{}"))
            self.config = {key: saved.get(key, value) for key, value in defaults.items()}
        except (ValueError, TypeError, AttributeError):
            self.config = defaults
        self.ready, self.busy, self.original, self.exiting = False, False, False, False
        self.generation, self.target, self.last_diagnostics = 0, None, {}
        self.load_generation = 0
        self.request_started = None
        self.bound_screens = set()
        self.overlay = ReplacementOverlay()
        self.overlay.painted.connect(self.rendered)
        self.selector = None
        self.worker = ResidentWorker()
        self.worker.ready.connect(self.models_ready)
        self.worker.result.connect(self.apply_result)
        self.worker.failed.connect(self.failed)
        self.worker.progress.connect(self.status_update)
        self.worker.downloaded.connect(self.models_downloaded)
        self.worker.benchmarked.connect(self.benchmark_finished)
        self.timer = QtCore.QTimer(self)
        self.timer.timeout.connect(self.capture_target)
        self.create_ui()
        self.create_tray()
        self.hotkeys = Hotkeys(self)
        QtWidgets.QApplication.instance().installNativeEventFilter(self.hotkeys)
        if self.hotkeys.failures:
            self.hotkey_status.setText("Unavailable: " + ", ".join(self.hotkeys.failures))
        QtWidgets.QApplication.instance().screenRemoved.connect(self.screens_changed)
        QtWidgets.QApplication.instance().screenAdded.connect(self.screens_changed)
        self.worker.start()
        QtCore.QTimer.singleShot(0, self.load_models)

    def create_ui(self):
        outer = QtWidgets.QVBoxLayout(self)
        outer.setContentsMargins(28, 25, 28, 24)
        outer.setSpacing(18)
        header = QtWidgets.QHBoxLayout()
        header.addWidget(label("Screen Translator", "title"))
        header.addStretch()
        header.addWidget(label("LOCAL", "badge"))
        outer.addLayout(header)
        outer.addWidget(label("A translation layer for your screen", "muted"))
        card = QtWidgets.QFrame()
        card.setObjectName("card")
        content = QtWidgets.QVBoxLayout(card)
        content.setContentsMargins(22, 22, 22, 22)
        content.setSpacing(17)
        languages = QtWidgets.QHBoxLayout()
        languages.addWidget(label("中文\nChinese", "section"))
        languages.addStretch()
        languages.addWidget(label("→", "muted"))
        languages.addStretch()
        languages.addWidget(label("EN\nEnglish", "section"))
        content.addLayout(languages)
        self.monitor = QtWidgets.QComboBox()
        self.fill_screens()
        self.monitor.setAccessibleName("Monitor to translate")
        content.addWidget(self.monitor)
        self.screen_button = button("Translate screen", self.translate_screen, True)
        content.addWidget(self.screen_button)
        self.region_button = button("Select a region", self.select_region)
        content.addWidget(self.region_button)
        content.addWidget(label("Ctrl + Alt + F: screen    ·    Ctrl + Alt + T: region", "muted"))
        self.filter = QtWidgets.QCheckBox("Translation filter")
        self.filter.setAccessibleName("Continuously translate changes on the selected screen")
        self.filter.toggled.connect(self.filter_changed)
        content.addWidget(self.filter)
        content.addWidget(label("Translate new content as the screen changes", "muted"))
        outer.addWidget(card)
        self.status = label("Checking local models…")
        outer.addWidget(self.status)
        self.device_status = label("Devices not compiled", "muted")
        outer.addWidget(self.device_status)
        self.download_button = button("Download offline models · 687 MB", self.download_models)
        outer.addWidget(self.download_button)
        self.benchmark_button = button("Benchmark hardware", self.benchmark_hardware)
        outer.addWidget(self.benchmark_button)
        recovery = QtWidgets.QHBoxLayout()
        self.reload_button = button("Reload models", self.load_models)
        self.cancel_button = button("Stop loading / inference", self.cancel_operation)
        recovery.addWidget(self.reload_button)
        recovery.addWidget(self.cancel_button)
        outer.addLayout(recovery)
        self.hotkey_status = label("Ctrl + Alt + O: original    ·    Ctrl + Alt + D: filter", "muted")
        outer.addWidget(self.hotkey_status)
        self.diagnostics = QtWidgets.QPlainTextEdit()
        self.diagnostics.setReadOnly(True)
        self.diagnostics.setMaximumHeight(165)
        self.diagnostics.setPlainText("Stage timings appear after your first translation.")
        outer.addWidget(self.diagnostics)
        footer = QtWidgets.QHBoxLayout()
        footer.addWidget(button("Show original", self.toggle_original))
        footer.addWidget(button("Clear overlay", self.clear_overlay))
        footer.addStretch()
        footer.addWidget(button("Settings", self.open_settings))
        outer.addLayout(footer)
        self.set_controls(False)

    def fill_screens(self):
        self.monitor.clear()
        for index, screen in enumerate(QtWidgets.QApplication.screens()):
            self.monitor.addItem(f"Display {index+1} · {screen.name()}", screen)
            if screen not in self.bound_screens:
                screen.geometryChanged.connect(lambda rect: self.clear_overlay())
                screen.logicalDotsPerInchChanged.connect(lambda dpi: self.clear_overlay())
                self.bound_screens.add(screen)

    def screens_changed(self, screen):
        self.clear_overlay()
        self.fill_screens()

    def create_tray(self):
        pixmap = QtGui.QPixmap(64, 64)
        pixmap.fill(QtCore.Qt.transparent)
        painter = QtGui.QPainter(pixmap)
        painter.setRenderHint(QtGui.QPainter.Antialiasing)
        painter.setPen(QtCore.Qt.NoPen)
        painter.setBrush(QtGui.QColor("#ff6900"))
        painter.drawRoundedRect(2, 2, 60, 60, 17, 17)
        painter.setPen(QtCore.Qt.white)
        font = QtGui.QFont("Segoe UI", 22)
        font.setBold(True)
        painter.setFont(font)
        painter.drawText(pixmap.rect(), QtCore.Qt.AlignCenter, "译")
        painter.end()
        icon = QtGui.QIcon(pixmap)
        self.setWindowIcon(icon)
        self.tray = QtWidgets.QSystemTrayIcon(icon, self)
        self.tray.setToolTip("Screen Translator · offline")
        menu = QtWidgets.QMenu()
        for text, callback in (("Translate screen", self.translate_screen), ("Translate region", self.select_region),
                               ("Translation filter", self.toggle_filter), ("Pause / original", self.toggle_original),
                               ("Settings", self.open_settings), ("Show controls", self.show), ("Exit", self.shutdown)):
            menu.addAction(text, callback)
        self.tray.setContextMenu(menu)
        self.tray.activated.connect(lambda reason: self.show() if reason == QtWidgets.QSystemTrayIcon.Trigger else None)
        self.tray.show()

    def set_controls(self, enabled):
        for widget in (self.screen_button, self.region_button, self.filter):
            widget.setEnabled(enabled)
        self.download_button.setEnabled(not self.busy)
        self.reload_button.setEnabled(not self.busy)
        self.cancel_button.setEnabled(self.busy)
        # A failed default NPU compile must still allow measuring the GPU alternative.
        self.benchmark_button.setEnabled(not self.busy and (Path(self.config["models"])/"manifest.json").is_file())

    def load_models(self):
        self.clear_overlay()
        self.ready, self.busy = False, True
        self.set_controls(False)
        config = dict(self.config)
        self.load_generation += 1
        config["generation"] = self.load_generation
        try:
            if len(config["devices"]) != 3 or any(not isinstance(device, str) or not device for device in config["devices"]):
                raise ValueError("Specify three explicit device IDs")
            self.status.setText("Loading and warming local models…")
            self.worker.queue.put(("load", config))
        except Exception as exc:
            self.failed(str(exc))

    def models_ready(self, info):
        if info["generation"] != self.load_generation:
            return
        self.ready, self.busy = True, False
        self.set_controls(True)
        self.last_diagnostics = {"models": info}
        ocr = info["ocr"]
        self.device_status.setText("Detector: " + ", ".join(ocr["detector"]["execution_devices"]) +
                                   "  ·  Recognizer: " + ", ".join(ocr["recognizer"]["execution_devices"]) +
                                   "\nTranslator: " + info["translator"]["device_name"] +
                                   " (" + info["translator"]["compiled_device"] + ")")
        self.diagnostics.setPlainText(json.dumps(info, indent=2, ensure_ascii=False))
        self.status_update("● Models ready · fully local")
        logging.getLogger("screen_translator").info("Execution devices: %s", json.dumps({
            "detector": ocr["detector"]["execution_devices"], "recognizer": ocr["recognizer"]["execution_devices"],
            "translator": info["translator"]["execution_devices"]}))
        self.download_button.setEnabled(True)

    def status_update(self, text, context=None):
        if context is not None:
            expected = self.load_generation if context["kind"] in ("load", "download", "benchmark") else self.generation
            if context["generation"] is not None and context["generation"] != expected:
                return
        self.status.setText(text)
        logging.getLogger("screen_translator").info("engine: %s", text)
        if sys.stdout is not None:
            try:
                print(text, flush=True)
            except (OSError, UnicodeError):
                # A closed/legacy-encoded terminal must not kill a Qt callback.
                pass

    def cancel_operation(self):
        self.clear_overlay()
        self.load_generation += 1
        self.worker.cancel(self.load_generation, self.generation)
        self.ready, self.busy = False, False
        self.set_controls(False)
        self.status.setText("Inference stopped. Reload models or choose a hardware profile in Settings.")

    def download_models(self):
        if self.busy:
            return
        self.clear_overlay()
        self.ready, self.busy = False, True
        self.load_generation += 1
        self.set_controls(False)
        self.status.setText("Downloading model files only; inference remains local…")
        self.worker.queue.put(("download", {"models": self.config["models"], "generation": self.load_generation}))

    def models_downloaded(self, result):
        if result["generation"] != self.load_generation:
            return
        self.config["models"] = result["models"]
        self.settings_store.setValue("config", json.dumps(self.config))
        self.load_models()

    def benchmark_hardware(self):
        if self.busy:
            return
        self.clear_overlay()
        self.hide()
        self.busy = True
        self.set_controls(False)
        QtCore.QTimer.singleShot(60, self.begin_benchmark)

    def begin_benchmark(self):
        try:
            image = qimage_to_pil(self.selected_screen().grabWindow(0).toImage())
            self.ready = False
            self.load_generation += 1
            self.status.setText("Benchmarking actual local CPU, iGPU and NPU profiles…")
            self.show()
            self.worker.queue.put(("benchmark", {"models": self.config["models"], "image": image,
                "batch_size": self.config["batch_size"], "generation": self.load_generation,
                "results": str(ROOT/"results/calibration")}))
        except Exception as exc:
            self.failed(str(exc))

    def benchmark_finished(self, result):
        if result["generation"] != self.load_generation:
            return
        summary = json.loads(Path(result["report"]).read_text(encoding="utf-8"))
        self.config.update(mode="Automatic", benchmark=result["report"], devices=summary["fastest_measured_devices"])
        self.settings_store.setValue("config", json.dumps(self.config))
        self.load_models()

    def selected_screen(self):
        return self.monitor.currentData() or QtWidgets.QApplication.primaryScreen()

    def translate_screen(self):
        if not self.ready or self.busy:
            return
        screen = self.selected_screen()
        self.target = (screen, QtCore.QRect(0, 0, screen.geometry().width(), screen.geometry().height()))
        self.original = False
        self.request_started = perf_counter()
        self.hide()
        QtCore.QTimer.singleShot(60, self.capture_target)

    def select_region(self):
        if not self.ready or self.busy or self.selector is not None:
            return
        self.filter.setChecked(False)
        self.clear_overlay()
        self.hide()
        screen = self.selected_screen()
        self.selector = RegionSelector(screen)
        self.selector.selected.connect(lambda rect: self.region_selected(screen, rect))
        self.selector.canceled.connect(self.region_canceled)

    def region_canceled(self):
        self.selector = None
        self.show()

    def region_selected(self, screen, rect):
        self.selector = None
        self.target = (screen, rect)
        self.original = False
        self.request_started = perf_counter()
        QtCore.QTimer.singleShot(60, self.capture_target)

    def capture_target(self):
        if self.busy or not self.ready or self.target is None or self.original or self.selector is not None:
            return
        if self.overlay.isVisible() and not self.overlay.capture_excluded:
            # Fail closed: never feed the English overlay back into Chinese OCR.
            self.filter.setChecked(False)
            self.overlay.hide()
            self.status.setText("Capture exclusion unavailable. Filter paused; use a fresh one-shot capture.")
            return
        screen, local = self.target
        start = perf_counter()
        try:
            shot = screen.grabWindow(0).toImage()
            if shot.isNull():
                raise RuntimeError("Screen capture returned no pixels")
            sx, sy = shot.width()/screen.geometry().width(), shot.height()/screen.geometry().height()
            crop = QtCore.QRect(round(local.x()*sx), round(local.y()*sy),
                                round(local.width()*sx), round(local.height()*sy)).intersected(shot.rect())
            image = qimage_to_pil(shot.copy(crop))
            geometry = QtCore.QRect(local)
            geometry.translate(screen.geometry().topLeft())
            context = {"key": (screen.name(), geometry.x(), geometry.y(), geometry.width(), geometry.height()),
                       "geometry": geometry, "generation": self.generation, "cache": self.config["cache"],
                       "capture_ms": (perf_counter()-start)*1000, "started": self.request_started or start,
                       "capture_size": image.size, "dpi_scale": (sx, sy)}
            self.busy = True
            self.set_controls(False)
            self.request_started = None
            self.status.setText("Translating locally…")
            self.worker.queue.put(("frame", (image, context)))
        except Exception as exc:
            self.failed(str(exc))

    def apply_result(self, image, regions, timing, context):
        if context["generation"] != self.generation:
            if self.ready:
                self.busy = False
                self.set_controls(True)
            return
        self.busy = False
        self.set_controls(self.ready)
        timing.update(capture_ms=context["capture_ms"], capture_size=context["capture_size"], dpi_scale=context["dpi_scale"])
        self.last_diagnostics["last_translation"] = timing
        self.last_started = context["started"]
        cached = (timing["frame_cache_hit"] and self.overlay.isVisible() and
                  self.overlay.geometry() == context["geometry"])
        if cached:
            timing.update(layout_ms=0, clipped_regions=sum(b.clipped for b in self.overlay.blocks))
            self.rendered(0)
        elif not self.original:
            timing.update(self.overlay.replace(image, regions, context["geometry"], self.config["font_scale"], self.config["debug"]))
            if not self.overlay.blocks:
                self.rendered(0)
        else:
            timing.update(layout_ms=0, clipped_regions=0)
            self.rendered(0)
        self.status.setText("Original view · translation paused" if self.original else
                            f"● Translated · {len(regions)} regions" if regions else "No readable text found")

    def rendered(self, duration):
        timing = self.last_diagnostics.get("last_translation", {})
        timing.update(render_ms=duration, total_hotkey_ms=(perf_counter()-self.last_started)*1000)
        names = [("Capture", "capture_ms"), ("Change detection", "change_detection_ms"),
                 ("Text detection", "detection_ms"), ("Recognition", "recognition_ms"),
                 ("Grouping", "grouping_ms"), ("Cache lookup", "cache_lookup_ms"),
                 ("Translation", "translation_ms"), ("Layout", "layout_ms"), ("Rendering", "render_ms"),
                 ("TOTAL", "total_hotkey_ms")]
        lines = [f"{name:<19} {timing.get(key, 0):8.1f} ms" for name, key in names]
        lines.append(f"Cache hits: {timing.get('translation_cache_hits', 0)} text, {timing.get('spatial_cache_hits', 0)} spatial")
        lines.append(f"Clipped regions: {timing.get('clipped_regions', 0)}")
        self.diagnostics.setPlainText("\n".join(lines))
        logging.getLogger("screen_translator").info(json.dumps(timing))

    def failed(self, error, context=None):
        if context is not None:
            expected = self.load_generation if context["kind"] in ("load", "download", "benchmark") else self.generation
            if context["generation"] is not None and context["generation"] != expected:
                if context["kind"] == "frame" and self.ready:
                    self.busy = False
                    self.set_controls(True)
                return
        self.busy = False
        if context is not None and (context.get("engine_stopped") or context["kind"] in ("load", "download", "benchmark")):
            self.ready = False
        self.filter.setChecked(False)
        self.overlay.hide()
        self.status.setText("Action failed: " + error)
        self.last_diagnostics["last_error"] = {"message": error, "kind": context["kind"] if context else "controller"}
        logging.getLogger("screen_translator").error("%s", error)
        # Loader failure leaves ready false; inference failure allows explicit retry.
        self.set_controls(self.ready)
        self.show()

    def filter_changed(self, checked):
        if checked:
            if self.original:
                self.original = False
            if self.target is None:
                self.translate_screen()
            self.timer.start(self.config["interval_ms"])
        else:
            self.timer.stop()

    def toggle_filter(self):
        if self.ready:
            self.filter.setChecked(not self.filter.isChecked())

    def toggle_original(self):
        self.original = not self.original
        if self.original:
            self.overlay.hide()
            self.status.setText("Original view · translation paused")
        else:
            # Recapture to avoid showing stale translations after a scroll/click.
            self.capture_target()

    def clear_overlay(self):
        self.generation += 1
        self.filter.setChecked(False)
        self.overlay.hide()
        self.target = None
        self.original = False
        self.request_started = None

    def clear_cache(self):
        self.worker.queue.put(("clear", None))
        self.status.setText("Local caches scheduled for clearing")

    def open_settings(self):
        dialog = SettingsDialog(self)
        if dialog.exec_() == QtWidgets.QDialog.Accepted:
            if self.busy:
                self.worker.cancel(self.load_generation, self.generation)
            self.config = dialog.values()
            self.settings_store.setValue("config", json.dumps(self.config))
            self.load_models()

    def export_diagnostics(self):
        path, _ = QtWidgets.QFileDialog.getSaveFileName(self, "Export local diagnostics", "screen-translator-diagnostics.json", "JSON (*.json)")
        if path:
            try:
                # Export is user initiated; existing files use the native confirmation dialog.
                Path(path).write_text(json.dumps(self.last_diagnostics, indent=2), encoding="utf-8")
            except OSError as exc:
                self.status.setText("Cannot export diagnostics: " + str(exc))

    def closeEvent(self, event):
        if self.exiting or not QtWidgets.QSystemTrayIcon.isSystemTrayAvailable():
            self.shutdown()
            event.accept()
        else:
            self.hide()
            event.ignore()

    def shutdown(self):
        if self.exiting:
            return
        self.exiting = True
        self.timer.stop()
        self.overlay.close()
        if self.selector:
            self.selector.close()
        self.hotkeys.close()
        self.status.setText("Stopping isolated inference process…")
        # Stop only our child process; the Qt thread is never forcibly terminated.
        self.worker.finished.connect(QtWidgets.QApplication.instance().quit)
        self.worker.stop()
        if not self.worker.isRunning():
            QtWidgets.QApplication.instance().quit()


def main():
    if sys.platform != "win32":
        raise SystemExit("Screen Translator requires Windows 11")
    # Set per-monitor DPI awareness before Qt creates any window or screen object.
    ctypes.windll.user32.SetProcessDpiAwarenessContext.argtypes = [ctypes.c_void_p]
    ctypes.windll.user32.SetProcessDpiAwarenessContext.restype = wintypes.BOOL
    ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    QtWidgets.QApplication.setAttribute(QtCore.Qt.AA_EnableHighDpiScaling)
    QtWidgets.QApplication.setAttribute(QtCore.Qt.AA_UseHighDpiPixmaps)
    application = QtWidgets.QApplication(sys.argv)
    application.setApplicationName("Screen Translator")
    application.setQuitOnLastWindowClosed(False)
    application.setStyle("Fusion")
    application.setStyleSheet(STYLE)
    directory = Path(QtCore.QStandardPaths.writableLocation(QtCore.QStandardPaths.AppLocalDataLocation))
    directory.mkdir(parents=True, exist_ok=True)
    handler = RotatingFileHandler(directory/"timings.log", maxBytes=1024*1024, backupCount=3, encoding="utf-8")
    logging.getLogger("screen_translator").addHandler(handler)
    logging.getLogger("screen_translator").setLevel(logging.INFO)
    window = MainWindow()
    def callback_failed(kind, error, frames):
        logging.getLogger("screen_translator").error("GUI callback failed", exc_info=(kind, error, frames))
        window.failed(f"{kind.__name__}: {error}")
    sys.excepthook = callback_failed
    window.show()
    return application.exec_()
