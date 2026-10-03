"""Qt-free resident backend for the native WebView2 development host.

Dependencies: root requirements.txt; stdlib-only supervisor, Pillow for PNG input.
Outputs: JSON lines on stdout, progress on stderr; screen data stays in RAM.
Command from project root: python -u -m screen_translator.backend
"""
import base64
from dataclasses import asdict
import io
import json
import multiprocessing
from multiprocessing.connection import Client, Listener
import os
from pathlib import Path
from queue import Empty, Queue
import sys
import subprocess
from threading import Event, Lock, Thread
from time import monotonic, sleep
from uuid import uuid4

from .engine import serve

MAX_MESSAGE = 100 * 1024 * 1024


def engine_process(address):
    """Private authenticated local pipe; inherited stdin is explicitly disconnected."""
    authkey = bytes.fromhex(os.environ.pop("SCREEN_TRANSLATOR_PIPE_KEY"))
    with Listener(address, family="AF_PIPE", authkey=authkey) as listener:
        with listener.accept() as connection:
            serve(connection)


def validate_config(value):
    """Validate the native settings boundary without importing inference libraries."""
    from .hardware import MODES
    if not isinstance(value, dict):
        raise ValueError("Settings must be an object")
    config = dict(value)
    if config.get("mode") not in ["Managed", "Automatic", *MODES]:
        raise ValueError("Unknown hardware mode")
    devices = config.get("devices")
    if not isinstance(devices, list) or len(devices) != 3 or any(
            not isinstance(device, str) or not device.strip() for device in devices):
        raise ValueError("Supply detector, recognizer and translator device IDs")
    batch = config.get("batch_size")
    if type(batch) is not int or not 1 <= batch <= 64:
        raise ValueError("Batch size must be an integer from 1 to 64")
    if not isinstance(config.get("models"), str) or not config["models"].strip():
        raise ValueError("Supply a local model directory")
    config["models"] = str(Path(config["models"]).resolve())
    if type(config.get("incremental")) is not bool:
        raise ValueError("Incremental setting must be Boolean")
    config.setdefault("benchmark", "")
    return config


def decode_frame(value):
    """Decode one bounded in-memory PNG. No screenshot files are created."""
    from PIL import Image
    raw = base64.b64decode(value["png"], validate=True)
    with Image.open(io.BytesIO(raw)) as opened:
        if opened.format != "PNG" or opened.width * opened.height > 64_000_000:
            raise ValueError("Expected a PNG capture of at most 64 million pixels")
        image = opened.convert("RGB")
    key = value["key"]
    if not isinstance(key, list) or len(key) != 5 or not isinstance(key[0], str):
        raise ValueError("Invalid capture identity")
    if any(type(number) is not int for number in key[1:]):
        raise ValueError("Capture geometry must use integer physical pixels")
    if key[3:] != [image.width, image.height] or type(value.get("cache")) is not bool:
        raise ValueError("Capture dimensions or cache setting do not match")
    return image, {"key": tuple(key), "cache": value["cache"]}


class Backend:
    """Serial resident engine with prompt cancellation and native-crash containment."""
    def __init__(self, output=sys.stdout):
        self.output, self.lock = output, Lock()
        self.queue, self.closed = Queue(), Event()
        self.epoch = 0
        self.process = self.connection = None
        self.stage = "Starting isolated inference process"
        self.thread = Thread(target=self.run, name="inference-supervisor", daemon=True)

    def emit(self, identifier, event, value, epoch=None):
        with self.lock:
            if epoch is not None and epoch != self.epoch:
                return
            try:
                self.output.write(json.dumps({"id": identifier, "event": event, "value": value},
                                             ensure_ascii=True, allow_nan=False) + "\n")
                self.output.flush()
            except (BrokenPipeError, OSError):
                self.closed.set()

    def submit(self, message):
        identifier, command = message.get("id"), message.get("command")
        if not isinstance(identifier, str) or len(identifier) > 80:
            raise ValueError("Command requires a short string ID")
        if command in ("stop", "exit"):
            with self.lock:
                self.epoch += 1
            self.emit(identifier, "stopped", "Inference stopped. Reload models to resume.")
            if command == "exit":
                self.closed.set()
            return
        if command not in ("load", "frame", "clear", "download", "benchmark"):
            raise ValueError("Unknown backend command")
        self.queue.put((self.epoch, identifier, command, message.get("payload", {})))

    def dispose_engine(self):
        if self.process is not None:
            if self.process.poll() is None:
                self.process.terminate()
            try:
                self.process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=1)
            self.process = None
        if self.connection is not None:
            self.connection.close()
            self.connection = None

    def crash_message(self):
        code = self.process.poll() if self.process is not None else None
        detail = f"0x{code & 0xffffffff:08X}" if code is not None else "unavailable"
        return f"Inference process exited ({detail}). Last stage: {self.stage}. Reload models or change hardware settings."

    def run(self):
        epoch = self.epoch
        try:
            while not self.closed.is_set():
                if epoch != self.epoch:
                    self.dispose_engine()
                    epoch = self.epoch
                try:
                    job_epoch, identifier, command, payload = self.queue.get(timeout=.1)
                except Empty:
                    if self.process is not None and self.process.poll() is not None:
                        self.emit(None, "failed", {"message": self.crash_message(), "engine_stopped": True}, epoch)
                        self.dispose_engine()
                    continue
                if job_epoch != self.epoch:
                    continue
                if epoch != self.epoch:
                    self.dispose_engine()
                    epoch = self.epoch
                try:
                    if command == "load":
                        payload = validate_config(payload)
                        payload["generation"] = job_epoch
                    elif command == "frame":
                        payload = decode_frame(payload)
                    elif command == "benchmark":
                        image, _ = decode_frame(payload)
                        payload = {"image": image, "models": payload["models"],
                                   "batch_size": payload["batch_size"], "results": payload["results"],
                                   "generation": job_epoch}
                    elif command == "download":
                        payload = {"models": payload["models"], "generation": job_epoch}
                    if self.process is None:
                        self.stage = "Starting isolated inference process"
                        self.emit(identifier, "progress", self.stage, job_epoch)
                        # Explicit stdio prevents a spawned worker inheriting the host's command reader.
                        # AF_PIPE is local Windows IPC, authenticated with an ephemeral per-worker key.
                        address = rf"\\.\pipe\ScreenTranslator-{uuid4().hex}"
                        authkey = os.urandom(32)
                        environment = {**os.environ, "SCREEN_TRANSLATOR_PIPE_KEY": authkey.hex()}
                        self.process = subprocess.Popen([sys.executable, "-u", "-m", "screen_translator.backend",
                            "--engine-pipe", address], stdin=subprocess.DEVNULL, stdout=sys.stderr,
                            stderr=sys.stderr, env=environment, creationflags=subprocess.CREATE_NO_WINDOW)
                        deadline = monotonic() + 15
                        while job_epoch == self.epoch and not self.closed.is_set():
                            if self.process.poll() is not None:
                                raise EOFError("Engine exited before connecting")
                            try:
                                self.connection = Client(address, family="AF_PIPE", authkey=authkey)
                                break
                            except OSError:
                                if monotonic() >= deadline:
                                    self.dispose_engine()
                                    raise TimeoutError("Local inference pipe did not connect within 15 seconds")
                                sleep(.05)
                        if job_epoch != self.epoch or self.closed.is_set():
                            continue
                    self.connection.send((command, payload))
                    while not self.closed.is_set() and job_epoch == self.epoch:
                        if not self.connection.poll(.1):
                            if self.process.poll() is not None:
                                raise EOFError("Inference process exited")
                            continue
                        event, value = self.connection.recv()
                        if event == "progress":
                            self.stage = value
                        elif event == "result":
                            regions, timing = value
                            value = {"regions": [asdict(region) for region in regions], "timing": timing}
                        elif event == "failed":
                            value = {"message": value, "engine_stopped": command != "frame"}
                        self.emit(identifier, event, value, job_epoch)
                        if event != "progress":
                            break
                except Exception as error:
                    stopped = isinstance(error, (EOFError, BrokenPipeError)) or (
                        self.process is not None and self.process.poll() is not None)
                    message = self.crash_message() if stopped else f"{type(error).__name__}: {error}"
                    if stopped:
                        self.dispose_engine()
                    self.emit(identifier, "failed", {"message": message,
                              "engine_stopped": stopped or command != "frame"}, job_epoch)
        finally:
            self.dispose_engine()


def main():
    multiprocessing.freeze_support()
    backend = Backend()
    backend.thread.start()
    try:
        while not backend.closed.is_set():
            line = sys.stdin.readline(MAX_MESSAGE + 1)
            if not line:
                break
            try:
                if len(line) > MAX_MESSAGE:
                    raise ValueError("Backend message exceeds capture size limit")
                message = json.loads(line)
                if not isinstance(message, dict):
                    raise ValueError("Command must be an object")
                backend.submit(message)
            except Exception as error:
                backend.emit(None, "failed", {"message": f"{type(error).__name__}: {error}",
                                             "engine_stopped": False})
    finally:
        backend.closed.set()
        backend.thread.join(timeout=4)


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--engine-pipe":
        engine_process(sys.argv[2])
    else:
        main()
