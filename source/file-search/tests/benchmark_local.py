"""Benchmark the opted-in corpus and explicit CPU/GPU/NPU embedding devices.

Dependencies: project requirements, existing local model. Outputs: a new numbered
results directory with config.json, summary.json, timings.csv and an SVG figure.
Command: .venv/Scripts/python.exe tests/benchmark_local.py --root ".." --documents 8
Each native device runs in its own process, with no fallback. No packages/models
are installed. Source files are read only. Semantic coverage is a bounded sample,
not a full-corpus quality evaluation. Progress is printed throughout.
"""
import argparse
import csv
import json
import logging
import os
import statistics
import subprocess
import shutil
import sqlite3
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import PROJECT, load
from xiaomi_search.service import Service
from xiaomi_search.store import Store


def save(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")


def worker(args):
    """Measure direct-device inference, normalize vectors, then persist samples."""
    import numpy as np
    import openvino as ov
    import openvino_genai as genai
    directory = Path(args.output)
    protocol = json.loads((directory / "config.json").read_text(encoding="utf-8"))
    config = protocol["settings"]
    samples = json.loads((directory / "samples.json").read_text(encoding="utf-8"))
    report = {"device": args.worker, "pid": os.getpid(), "fallback": False}
    try:
        core = ov.Core()
        report["device_name"] = core.get_property(args.worker, "FULL_DEVICE_NAME")
        options = genai.TextEmbeddingPipeline.Config()
        options.max_length = config["max_tokens"]
        options.batch_size = 1
        options.pad_to_max_length = True
        options.padding_side = "left"
        options.pooling_type = genai.TextEmbeddingPipeline.PoolingType.LAST_TOKEN
        options.normalize = True
        options.query_instruction = config["query_instruction"]
        print(f"{args.worker}: compiling local model (no fallback)", flush=True)
        cache = directory / "model_cache" / args.worker
        cache.mkdir(parents=True, exist_ok=True)
        begin = time.perf_counter()
        pipeline = genai.TextEmbeddingPipeline(config["model_path"], args.worker, options, CACHE_DIR=str(cache))
        report["load_seconds"] = time.perf_counter() - begin
        cpu_begin = time.process_time()
        document_times, vectors = [], []
        for index, sample in enumerate(samples):
            begin = time.perf_counter()
            vector = np.asarray(pipeline.embed_documents([sample["text"]])[0], dtype=np.float32)  # shape: [D]
            document_times.append((time.perf_counter() - begin) * 1000)
            vectors.append(vector)
            print(f"{args.worker}: document {index+1}/{len(samples)} {document_times[-1]:.1f} ms", flush=True)
        matrix = np.stack(vectors)  # shape: [N,D]
        assert np.isfinite(matrix).all() and np.all(np.linalg.norm(matrix, axis=1) > 0), "Malformed embeddings"
        # Normalize every passage so dot products represent cosine similarity.
        matrix = matrix / np.linalg.norm(matrix, axis=1, keepdims=True)  # shape: [N,D], broadcast [N,1]
        store = Store(directory / "index" / "index.sqlite3")
        for sample, vector in zip(samples, matrix):
            store.put_vector(sample["id"], vector, "benchmark-device-" + args.worker)
        queries, query_vectors = [], []
        for query in protocol["queries"]:
            begin = time.perf_counter()
            vector = np.asarray(pipeline.embed_query(query), dtype=np.float32)  # shape: [D]
            assert np.isfinite(vector).all() and np.linalg.norm(vector) > 0
            vector = vector / np.linalg.norm(vector)  # shape: [D]
            # Compare the query against the same representative corpus on each device.
            similarities = matrix @ vector  # shape: [N], reduce D
            embedding_ms = (time.perf_counter()-begin)*1000
            threshold = config["semantic_threshold"]
            class QueryVector:
                config = {"semantic_threshold": threshold}

                def identity(self):
                    return "benchmark-device-" + args.worker

                def encode(self, texts, query=False):
                    return vector[None, :]  # shape: [1,D], reuses the timed real query

            begin = time.perf_counter()
            retrieval = store.search(query, QueryVector())
            queries.append({"query": query, "milliseconds": embedding_ms, "hybrid_ms": embedding_ms+(time.perf_counter()-begin)*1000,
                            "top_file": samples[int(np.argmax(similarities))]["path"], "hybrid_top_files": [r["file_path"] for r in retrieval["results"][:5]],
                            "semantic_matches": [r["file_path"] for r in retrieval["results"] if "Semantic" in r["matches"]][:10], "warnings": retrieval["warnings"]})
            query_vectors.append(vector)
            print(f"{args.worker}: query {len(queries)}/{len(protocol['queries'])} {queries[-1]['milliseconds']:.1f} ms", flush=True)
        report.update(success=True, dimension=matrix.shape[1], documents_ms=document_times, queries=queries, active_process_cpu_seconds=time.process_time()-cpu_begin,
                      document_median_ms=statistics.median(document_times[1:] or document_times), query_median_ms=statistics.median(q["milliseconds"] for q in queries),
                      utilization_note="Explicit single-device OpenVINO pipeline, no automatic fallback. Process CPU time measured; accelerator utilization/power percentages not measured.")
        if args.worker == "GPU":
            # Keep an explicitly uncached GPU workload active during Windows
            # counter samples. This extra telemetry loop is not a latency sample.
            counter_command = "$ErrorActionPreference='Stop'; Get-Counter '\\GPU Engine(*)\\Utilization Percentage' -SampleInterval 1 -MaxSamples 4 | ForEach-Object { $_.CounterSamples | Where-Object InstanceName -like 'pid_" + str(os.getpid()) + "_*' | Select-Object InstanceName,CookedValue } | ConvertTo-Json -Compress"
            monitor = subprocess.Popen([shutil.which("pwsh") or "powershell.exe", "-NoProfile", "-Command", counter_command], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            telemetry_begin = time.monotonic()
            while time.monotonic()-telemetry_begin < 6:
                pipeline.embed_query("exclude folders from indexing")
            output, errors = monitor.communicate(timeout=15)
            try:
                counters = json.loads(output)
                counters = counters if isinstance(counters,list) else [counters]
                report["gpu_engine_samples"] = counters
                report["gpu_peak_engine_percent"] = max(float(row["CookedValue"]) for row in counters)
            except (ValueError, TypeError, KeyError):
                report["gpu_telemetry_error"] = errors or "No per-process GPU counter samples returned"
        # Small validation vectors are necessary to compare accelerator outputs.
        np.savez(directory / f"{args.worker}_vectors.npz", documents=matrix, queries=np.stack(query_vectors))
    except Exception as exc:
        report.update(success=False, error=str(exc))
        print(f"{args.worker}: FAILED {exc}", flush=True)
    save(directory / f"{args.worker}.json", report)
    return 0 if report["success"] else 1


def run(args):
    import importlib.metadata
    import openvino as ov
    prefix = max((int(p.name.split('_')[0]) for p in (PROJECT / "results").glob('[0-9][0-9][0-9]_*')), default=0)+1
    directory = PROJECT / "results" / f"{prefix:03d}_{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}_seed42"
    directory.mkdir(parents=True, exist_ok=False)
    config = load(PROJECT / "config.json")
    config.update(roots=[str(Path(args.root).resolve())], semantic_enabled=False, indexing_mode="normal")
    config["excluded_folders"] = list(dict.fromkeys(config["excluded_folders"] + [str(PROJECT / "results"), str(PROJECT / "data"), str(PROJECT.parent / "screen-translator" / "results")]))
    config["excluded_names"] = list(dict.fromkeys(config["excluded_names"] + ["model_cache"]))
    protocol = {"seed": 42, "root": args.root, "settings": config, "documents": args.documents, "device_timeout_seconds": args.timeout,
                "devices": args.devices, "queries": ["embedding.py", "semantic search", "recover from a native model crash", "exclude folders from indexing", "translate the screen", "coefficient"],
                "sampling": "One representative passage per file; prioritize this application's architecture and core modules, then translator modules and format diversity. Semantic retrieval covers this bounded sample.", "reuse_index": args.reuse_index,
                "packages": {n: importlib.metadata.version(n) for n in ("openvino", "openvino-genai", "numpy")},
                "hardware": {d: ov.Core().get_property(d, "FULL_DEVICE_NAME") for d in ov.Core().available_devices}}
    save(directory / "config.json", protocol)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s", handlers=[logging.FileHandler(directory / "progress.log", encoding="utf-8"), logging.StreamHandler()])
    print(f"RESULTS {directory}", flush=True)
    if args.reuse_index:
        (directory / "index").mkdir()
        with sqlite3.connect(f"file:{Path(args.reuse_index).resolve().as_posix()}?mode=ro", uri=True) as source, sqlite3.connect(directory / "index" / "index.sqlite3") as destination:
            source.backup(destination)
    service = Service(config, directory / "index", directory / "config.json")
    begin = time.perf_counter()
    if not args.reuse_index:
        service.indexer.start(watch=False)
        service.indexer.request_scan()
        service.indexer.wait_complete()
    summary = {"index_seconds": time.perf_counter()-begin, "counts": service.store.counts(), "extraction_errors": service.store.errors(), "lexical": [], "devices": {}}
    for query in protocol["queries"] + ["type:folder"]:
        begin = time.perf_counter()
        result = service.search({"text": query, "semantic": False})
        elapsed = (time.perf_counter()-begin)*1000
        begin = time.perf_counter()
        repeated = service.search({"text": query, "semantic": False})
        summary["lexical"].append({"query": query, "uncached_ms": elapsed, "cached_ms": (time.perf_counter()-begin)*1000, "cache_hit": repeated["timing"]["cache_hit"], "matches": len(result["results"]), "top_files": [r["file_path"] for r in result["results"][:5]]})
    with service.store.connect() as db:
        candidates = [dict(r) for r in db.execute("SELECT c.id,c.file_id,c.text,f.path,f.extension FROM chunks c JOIN files f ON f.id=c.file_id WHERE f.active=1 AND f.status='indexed' AND c.id IN (SELECT min(id) FROM chunks GROUP BY file_id) ORDER BY f.size,f.path")]
    pools = {}
    for candidate in candidates:
        if any(part in candidate["path"].lower() for part in ("vendor", "frontend\\xiaomi", "assets\\index")):
            continue
        pools.setdefault(candidate["extension"], []).append(candidate)
    priority = [PROJECT / "research" / "architecture.md", PROJECT / "README.md"] + [PROJECT / "xiaomi_search" / f"{name}.py" for name in ("embedding", "indexer", "config", "extract", "service", "store")] + [PROJECT.parent / "screen-translator" / "screen_translator" / f"{name}.py" for name in ("engine", "bootstrap")]
    samples = []
    for path in priority:
        candidate = next((row for row in candidates if row["path"] == str(path)), None)
        if candidate and len(samples)<args.documents:
            samples.append(candidate)
    for extension in pools:
        pools[extension] = [row for row in pools[extension] if row["path"] not in {s["path"] for s in samples}]
    while any(pools.values()) and len(samples) < args.documents:
        for extension in sorted(pools):
            if pools[extension] and len(samples) < args.documents:
                samples.append(pools[extension].pop(0))
    save(directory / "samples.json", samples)
    service.close()
    save(directory / "summary.json", summary)
    print(f"Lexical index done: {summary['counts']}; {summary['index_seconds']:.1f}s; sample {len(samples)} passages", flush=True)
    for device in args.devices:
        command = [sys.executable, str(Path(__file__).resolve()), "--worker", device, "--output", str(directory)]
        with (directory / f"{device}.log").open("w", encoding="utf-8") as log:
            process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
            begin = time.monotonic()
            previous = 0
            while process.poll() is None:
                time.sleep(1)
                content = (directory / f"{device}.log").read_text(encoding="utf-8", errors="replace")
                if len(content) > previous:
                    print(content[previous:], end="", flush=True)
                    previous = len(content)
                if time.monotonic()-begin > args.timeout:
                    process.kill()  # Only the native worker created by this benchmark.
                    process.wait()
                    print(f"{device}: own worker exceeded timeout", flush=True)
                    break
            filename = directory / f"{device}.json"
            report = json.loads(filename.read_text(encoding="utf-8")) if filename.exists() else {"success": False, "error": "Native worker exit/timeout before report", "exit_code": process.returncode}
            summary["devices"][device] = report
            save(directory / "summary.json", summary)
    successful = [(d,r) for d,r in summary["devices"].items() if r.get("success")]
    if successful:
        import numpy as np
        reference_device = "CPU" if any(d=="CPU" for d,_ in successful) else successful[0][0]
        reference = np.load(directory / f"{reference_device}_vectors.npz")["queries"]  # shape: [Q,D]
        for device, report in successful:
            queries = np.load(directory / f"{device}_vectors.npz")["queries"]  # shape: [Q,D]
            # Row-wise dot products check cross-device embedding consistency.
            report["mean_query_cosine_to_reference"] = float(np.sum(reference*queries, axis=1).mean())
        summary["reference_device"] = reference_device
        summary["fastest_warm_query_device"] = min(successful, key=lambda item:item[1]["query_median_ms"])[0]
    with (directory / "timings.csv").open("w", newline="", encoding="utf-8") as stream:
        writer=csv.writer(stream);writer.writerow(["device_or_mode", "measure", "milliseconds"])
        for row in summary["lexical"]:
            writer.writerow(["lexical", row["query"], row["uncached_ms"]]);writer.writerow(["cached lexical", row["query"], row["cached_ms"]])
        for device, report in successful:
            writer.writerow([device,"warm query median",report["query_median_ms"]])
    bars=[("Lexical", statistics.median(r["uncached_ms"] for r in summary["lexical"])),("Cached lexical",statistics.median(r["cached_ms"] for r in summary["lexical"]))]+[(d,r["query_median_ms"]) for d,r in successful]
    maximum=max(v for _,v in bars) or 1
    svg=['<svg xmlns="http://www.w3.org/2000/svg" width="760" height="440"><rect width="100%" height="100%" fill="#f7f8fa"/><g font-family="Segoe UI,Arial" fill="#59616e"><text x="30" y="35" font-size="20">Warm retrieval latency · lower is faster</text><text x="30" y="60" font-size="12">Lexical: full eligible index. Device: query embedding + dot product on sample.</text>']
    for i,(label,value) in enumerate(bars):
        y=90+i*48;svg.append(f'<text x="30" y="{y+18}" font-size="14">{label}</text><rect x="170" y="{y}" width="{450*value/maximum:.2f}" height="26" rx="5" fill="#6687cf"/><text x="{182+450*value/maximum:.2f}" y="{y+18}" font-size="12">{value:.2f} ms</text>')
    svg.append('<text x="30" y="395" font-size="12">Horizontal scale: milliseconds; compile time excluded. No utilization/power telemetry.</text></g></svg>')
    (directory / "latency.svg").write_text(''.join(svg), encoding="utf-8")
    summary["semantic_sample_passages"] = len(samples)
    save(directory / "summary.json", summary)
    print(f"COMPLETE {directory}", flush=True)


def main():
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1")
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root",default=str(PROJECT.parent),help="Only this directory is scanned")
    parser.add_argument("--documents",type=int,default=8,help="Representative passages to embed on each device")
    parser.add_argument("--timeout",type=int,default=240,help="Maximum seconds per native device worker")
    parser.add_argument("--devices",nargs="+",choices=["CPU","GPU","NPU"],default=["CPU","GPU","NPU"],help="Explicit devices to test without fallback")
    parser.add_argument("--worker",choices=["CPU","GPU","NPU"],help="Internal isolated device worker")
    parser.add_argument("--output",help="Existing benchmark directory for internal worker")
    parser.add_argument("--reuse-index",help="Read-only SQLite snapshot from a previous corpus scan; copy to a new results directory")
    args=parser.parse_args()
    if args.documents<1 or args.timeout<1:parser.error("documents and timeout must be positive")
    return worker(args) if args.worker else run(args)


if __name__ == "__main__":
    sys.exit(main())
