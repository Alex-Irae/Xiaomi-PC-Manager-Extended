"""Native desktop's private stdio backend, with no Python GUI imports.

Dependencies: project requirements, existing local model. Outputs: JSON RPC on
stdout, data/index.sqlite3, model_cache and backend.log. No TCP search server.
Command: .venv/Scripts/python.exe -m xiaomi_search.backend --config config.json
Normally launched and owned by native/AI Center.exe; stdin EOF requests shutdown.
"""
import argparse
import faulthandler
import json
import logging
import os
import sys
import threading
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from .config import load
from .service import Service


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    sys.stdin.reconfigure(encoding="utf-8")
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", default="config.json", help="Local settings file")
    parser.add_argument("--data", default="data", help="Private application index/cache directory")
    parser.add_argument("--paused", action="store_true", help="Pause background indexing for an explicit validation session")
    parser.add_argument("--no-shortcut", action="store_true", help="Suppress the native host shortcut for this session")
    args = parser.parse_args()
    data = Path(args.data).resolve()
    data.mkdir(parents=True, exist_ok=True)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", handlers=[logging.StreamHandler(sys.stderr), logging.FileHandler(data / "backend.log", encoding="utf-8")])
    # A slow native/watch startup must leave actionable stacks instead of silence.
    trace = os.environ.get('AI_CENTER_TRACE_SECONDS', '')
    trace_seconds = max(5, min(3600, int(trace))) if trace.isdecimal() else 0
    faulthandler.dump_traceback_later(trace_seconds or 30, repeat=bool(trace_seconds), file=sys.stderr)
    logging.info('Opening local configuration and protected index')
    settings = load(args.config)
    if args.paused:
        settings["indexing_mode"] = "paused"
    if args.no_shortcut:
        settings["shortcut"] = "none"
    # Initialize NumPy's native DLLs on the main thread before watchdog and
    # checkpoint threads exist. Lazy background import stalled the live indexer.
    import numpy
    service = Service(settings, data, args.config)
    output_lock, action_lock = threading.Lock(), threading.Lock()
    actions = {}
    counter = 0

    def send(message):
        with output_lock:
            print(json.dumps(message, ensure_ascii=False), flush=True)

    def action(name, params):
        nonlocal counter
        if name == 'appearance':
            # Declarative state notification: persistence must not await a
            # WebView/window acknowledgment, especially during dismissal.
            send({'kind':'action', 'token':0, 'action':name, 'params':params})
            return None
        event = threading.Event()
        with action_lock:
            counter += 1
            token = counter
            result = {}
            actions[token] = (event, result)
        send({"kind": "action", "token": token, "action": name, "params": params})
        try:
            if not event.wait(180):
                raise TimeoutError("Native window action timed out")
            if result.get("error"):
                raise RuntimeError(result["error"])
            return result.get("value")
        finally:
            with action_lock:
                actions.pop(token, None)

    service.emit = send
    service.window_action = action

    def request(message):
        owner = message.pop("owner", "search")
        response = service.rpc(message, owner)
        send({"owner": owner, **response})

    try:
        logging.info('Starting index notifications')
        service.indexer.start(watch=settings['indexing_frequency'] == 'realtime')
        if settings['indexing_frequency'] == 'realtime':
            service.indexer.request_scan()
        send({"kind": "ready", "settings": settings, "pid": os.getpid()})
        if not trace_seconds:
            faulthandler.cancel_dump_traceback_later()
        logging.info('Local search backend ready')
        # ponytail: four RPC workers keep lexical requests responsive while one
        # query waits for native inference; GPU/NPU calls remain serialized.
        with ThreadPoolExecutor(max_workers=4, thread_name_prefix="desktop-rpc") as executor, ThreadPoolExecutor(max_workers=1, thread_name_prefix='settings-rpc') as settings_executor:
            for line in sys.stdin:
                try:
                    message = json.loads(line)
                    if message.get("kind") == "action_result":
                        with action_lock:
                            pending = actions.get(message.get("token"))
                            if pending:
                                pending[1].update(message)
                                pending[0].set()
                    elif message.get("kind") == "shutdown":
                        break
                    else:
                        method = message.get('data', {}).get('request', {}).get('method')
                        # Persist rapid edits in receipt order, independently of
                        # searches and WebView lifetime. No shared temp-file races.
                        (settings_executor if method == 'local_save_config' else executor).submit(request, message)
                except (ValueError, TypeError, AttributeError) as exc:
                    logging.warning("Rejected malformed native RPC: %s", exc)
            with action_lock:
                for event, result in actions.values():
                    result["error"] = "Desktop is closing"
                    event.set()
    finally:
        service.close()


if __name__ == "__main__":
    main()
