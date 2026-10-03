"""Resident model process, deliberately independent of PyQt.

Dependencies: root requirements.txt for inference; standard library for transport.
Outputs: progress/results over a local multiprocessing pipe, never screen files.
Started by the desktop's spawn context. No standalone command or model downloads.
"""
from pathlib import Path

from .bootstrap import prefer_system_runtime


def serve(connection):
    """Keep one pipeline resident; exchange only plain Python/PIL data, never Qt objects."""
    prefer_system_runtime()
    from .cli import make_pipeline
    pipeline, capture_key = None, None

    def progress(message):
        connection.send(("progress", message))

    try:
        while True:
            command, payload = connection.recv()
            if command == "exit":
                return
            try:
                if command == "load":
                    pipeline, capture_key = None, None
                    from .models import digest
                    from .hardware import identity
                    import json
                    config = dict(payload)
                    if config["mode"] == "Managed":
                        from .hardware import automatic_pipeline
                        pipeline, info = automatic_pipeline(config["models"], config["batch_size"],
                                                            config["incremental"], progress)
                        connection.send(("ready", {**info, "generation": config["generation"]}))
                        continue
                    if config["mode"] == "Automatic":
                        progress("Checking benchmark hardware and model identity")
                        summary = json.loads(Path(config["benchmark"]).read_text(encoding="utf-8"))
                        if summary["model_manifest_sha256"] != digest(Path(config["models"])/"manifest.json"):
                            raise ValueError("Benchmark belongs to a different model bundle")
                        if summary["batch_size"] != config["batch_size"] or not summary.get("fastest_measured_devices"):
                            raise ValueError("Benchmark has no successful matching batch configuration")
                        if summary.get("hardware", {}).get("fingerprint") != identity()["fingerprint"]:
                            raise ValueError("Hardware or runtime changed. Run Benchmark hardware again.")
                        config["devices"] = summary["fastest_measured_devices"]
                    pipeline, info = make_pipeline(config["models"], config["devices"],
                        config["batch_size"], config["incremental"], progress=progress)
                    connection.send(("ready", {**info, "generation": config["generation"]}))
                elif command == "download":
                    from .provision import download
                    pipeline, capture_key = None, None
                    root = download(payload["models"], progress)
                    connection.send(("downloaded", {"models": root, "generation": payload["generation"]}))
                elif command == "benchmark":
                    from .hardware import calibrate
                    pipeline, capture_key = None, None
                    report = calibrate(payload["models"], payload["image"], payload["batch_size"],
                                       results=payload["results"], progress=progress)
                    connection.send(("benchmarked", {"report": report, "generation": payload["generation"]}))
                elif command == "clear":
                    if pipeline:
                        pipeline.clear()
                    connection.send(("cleared", None))
                elif command == "frame":
                    if pipeline is None:
                        raise RuntimeError("Models are not ready")
                    image, context = payload
                    if capture_key != context["key"]:
                        pipeline.reset_frame()
                        capture_key = context["key"]
                    regions, timing = pipeline.run(image, use_cache=context["cache"])
                    connection.send(("result", (regions, timing)))
                else:
                    raise ValueError(f"Unknown engine command: {command}")
            except Exception as exc:
                connection.send(("failed", f"{type(exc).__name__}: {exc}"))
    except (EOFError, BrokenPipeError):
        return
    finally:
        connection.close()
