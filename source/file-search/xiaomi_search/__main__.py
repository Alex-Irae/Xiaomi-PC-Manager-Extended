"""Local Xiaomi search CLI and desktop launcher.

Dependencies: requirements.txt; Windows 11 + WebView2 for desktop.
Outputs: protected data/index.sqlite3.dpapi, data/model_cache/, data/search.log, terminal progress.
Commands (from project root):
  python -m xiaomi_search desktop
  python -m xiaomi_search index --lexical-only
  python -m xiaomi_search search "shared interpolation weights"
  python -m xiaomi_search --config <config.json> --data <data folder> compact   (with the app closed)
"""
import argparse
import json
import logging
import os
import sys
import subprocess
from pathlib import Path


def main():
    # Windows terminals may default to cp1252; real file snippets include Unicode.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="backslashreplace")
    # Tokenizers and OpenVINO load only local paths; prevent transitive HF lookups.
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    parser = argparse.ArgumentParser(description="Local filename, full-text, and semantic search using the Xiaomi frontend")
    parser.add_argument("--config", default="config.json", help="Local configuration JSON; defaults to example settings when absent")
    parser.add_argument("--data", default="data", help="Directory for this application's disposable index, log, and model cache")
    commands = parser.add_subparsers(dest="command", required=True)
    desktop = commands.add_parser("desktop", help="Open the compact search bar and portable settings")
    desktop.add_argument("--no-shortcut", action="store_true", help="Launch directly without a global shortcut listener")
    index = commands.add_parser("index", help="Explicitly scan selected roots and build/backfill the local index")
    index.add_argument("--lexical-only", action="store_true", help="Extract content and filenames without loading an embedding model")
    search = commands.add_parser("search", help="Search the existing index from the terminal")
    search.add_argument("query", help="Natural language or filename; supports ext:, type:, folder:, before:, after:")
    search.add_argument("--lexical-only", action="store_true", help="Skip semantic inference and return filename/content matches")
    commands.add_parser("status", help="Inspect index counts and configured local model presence without inference")
    commands.add_parser("compact", help="With the app closed: drop excluded or missing files and the passages of name-only types, then shrink the index file")
    args = parser.parse_args()
    if args.command == "desktop":
        from .config import PROJECT
        executable = PROJECT / "native/bin/Release/net8.0-windows/AI Center.exe"
        if not executable.is_file():
            parser.error("Native UI is not built. Run pwsh -NoProfile -File native/build.ps1")
        command = [str(executable), "--search", "--config", str(Path(args.config).resolve()), "--data", str(Path(args.data).resolve())]
        if args.no_shortcut:
            command.append("--no-shortcut")
        subprocess.Popen(command, creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0))
        return 0
    data = Path(args.data).resolve()
    data.mkdir(parents=True, exist_ok=True)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", handlers=[logging.StreamHandler(), logging.FileHandler(data / "search.log", encoding="utf-8")])
    from .config import load
    from .service import Service
    config = load(args.config)
    if getattr(args, "no_shortcut", False):
        config["shortcut"] = "none"
    if getattr(args, "lexical_only", False):
        config["semantic_enabled"] = False
    service = Service(config, data, args.config)
    try:
        if args.command == "index":
            if not config["roots"]:
                raise ValueError("No index roots configured. Add selected folders to config.json")
            # A manual index command is an explicit request, so battery scheduling
            # does not silently leave it waiting forever for a charger.
            service.indexer.set_mode("normal")
            service.indexer.start(watch=False)
            service.indexer.request_scan()
            service.indexer.wait_complete()
            print(json.dumps(service.status(), indent=2))
            if service.indexer.semantic_error or service.indexer.scan_error or service.store.counts().get("error", 0):
                return 1
        elif args.command == "compact":
            print(json.dumps(service.store.compact(config["name_only_extensions"])))
        elif args.command == "search":
            print(json.dumps(service.search({"text": args.query}), indent=2, ensure_ascii=False))
        else:
            print(json.dumps(service.status(), indent=2, ensure_ascii=False))
    except KeyboardInterrupt:
        logging.info("Stopped by user")
        return 130
    except Exception as exc:
        logging.error("%s", exc)
        return 1
    finally:
        service.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
