"""Sample CPU, memory, disk writes, GPU and NPU use of the running suite apps and FileSync.

Dependencies: Windows, an existing Python with psutil, typeperf.exe. Run it with an interpreter outside the
app folders where possible; this process and its children are left out of the figures either way.
Outputs: results/NNN_<utc>_resources/ with config.json, samples.csv, gpu.csv and summary.json.
Nothing is started, stopped or changed; only running processes are read.
Command: python -B tools/measure_apps.py [--seconds 300] [--label idle]
"""
import argparse
import csv
import json
import os
import re
import statistics
import subprocess
import time
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

import psutil

ROOT = Path(__file__).resolve().parents[1]
SUITE = "c:\\program files\\xiaomi revamp\\"
APPS = {SUITE + "pc manager\\": "PC Manager", SUITE + "ai center\\": "AI Center",
        SUITE + "screen translator\\": "Screen Translator", SUITE + "syncfile\\": "FileSync"}


def owner(process, cache):
    """The app a process belongs to: its own image path, or the nearest ancestor inside an app folder."""
    chain = []
    try:
        if process.pid == os.getpid() or process.ppid() == os.getpid():
            return None  # this tool and its counter reader
    except psutil.Error:
        return None
    while process is not None:
        key = (process.pid, process.create_time())
        if key in cache:
            found = cache[key]
            break
        chain.append(key)
        try:
            path = process.exe().lower()
        except (psutil.Error, OSError):
            path = ""
        found = next((name for prefix, name in APPS.items() if path.startswith(prefix)), None)
        if found:
            break
        try:
            process = process.parent()
        except psutil.Error:
            process = None
    else:
        found = None
    for key in chain:
        cache[key] = found
    return found


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--seconds", type=int, default=300, help="How long to sample, one sample per second")
    parser.add_argument("--label", default="idle", help="Short name of the situation measured")
    args = parser.parse_args()
    results = ROOT / "results"
    results.mkdir(exist_ok=True)
    number = len([path for path in results.iterdir() if path.is_dir()]) + 1
    output = results / f"{number:03d}_{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}_resources_{args.label}"
    output.mkdir(exist_ok=False)
    cores = psutil.cpu_count()
    (output / "config.json").write_text(json.dumps({
        "label": args.label, "seconds": args.seconds, "interval_seconds": 1, "logical_cpus": cores,
        "cpu": "CPU time delta / elapsed / logical CPUs: percent of the whole machine",
        "working_set": "Sum of working sets; pages shared between processes are counted more than once",
        "private": "Sum of private committed bytes",
        "gpu": "typeperf GPU Engine utilisation per process and engine type; the Neural engine type is the NPU. "
               "A single engine reading above 100 is a counter artefact and is counted as 100; 'active' is the share of samples above 1 percent",
        "scope": "Processes whose image is inside an app folder, and their descendants (WebView2, Python, Unison)",
        "limitations": "Processes that live less than a second can be missed; GPU counters cover processes present at the start",
    }, indent=2), encoding="utf-8")
    # typeperf fixes its instance list at start, which is fine for a steady state.
    gpu = subprocess.Popen(["typeperf", r"\GPU Engine(*)\Utilization Percentage", r"\GPU Process Memory(*)\Dedicated Usage",
                            r"\GPU Process Memory(*)\Shared Usage", "-si", "2", "-sc", str(max(2, args.seconds // 2)),
                            "-f", "CSV", "-o", str(output / "gpu.csv"), "-y"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    cache, previous, names = {}, {}, {}
    rows = []
    started = last = time.monotonic()
    fields = ["seconds", "app", "process", "count", "cpu_percent", "working_set_mb", "private_mb", "threads", "handles", "write_kb"]
    with (output / "samples.csv").open("w", newline="", encoding="utf-8") as file:
        writer = csv.DictWriter(file, fieldnames=fields)
        writer.writeheader()
        while time.monotonic() - started < args.seconds:
            time.sleep(1)
            now = time.monotonic()
            elapsed, last = now - last, now
            totals = defaultdict(lambda: defaultdict(float))
            current = {}
            for process in psutil.process_iter():
                try:
                    app = owner(process, cache)
                    if not app:
                        continue
                    key = (process.pid, process.create_time())
                    with process.oneshot():
                        times, memory, io = process.cpu_times(), process.memory_info(), process.io_counters()
                        name = process.name().lower()
                        threads, handles = process.num_threads(), process.num_handles()
                    names[process.pid] = (app, name)
                    spent, written = times.user + times.system, io.write_bytes
                    before = previous.get(key, (spent, written))
                    current[key] = (spent, written)
                    entry = totals[(app, name)]
                    entry["count"] += 1
                    entry["cpu_percent"] += max(0, spent - before[0]) / elapsed / cores * 100
                    entry["working_set_mb"] += memory.rss / 1048576
                    entry["private_mb"] += getattr(memory, "private", memory.vms) / 1048576
                    entry["threads"] += threads
                    entry["handles"] += handles
                    entry["write_kb"] += max(0, written - before[1]) / 1024
                except (psutil.Error, OSError):
                    continue
            previous = current
            for (app, name), entry in sorted(totals.items()):
                row = {"seconds": round(now - started, 1), "app": app, "process": name, **{key: round(value, 3) for key, value in entry.items()}}
                writer.writerow(row)
                rows.append(row)
            file.flush()
    gpu.wait(timeout=30)
    summary = {"apps": {}, "gpu": {}}
    steady = [row for row in rows if row["seconds"] > 5]
    seconds = sorted({row["seconds"] for row in steady})
    for app in sorted({row["app"] for row in steady}):
        per_second = defaultdict(lambda: defaultdict(float))
        processes = defaultdict(lambda: defaultdict(list))
        for row in steady:
            if row["app"] != app:
                continue
            for key in fields[3:]:
                per_second[row["seconds"]][key] += row[key]
                processes[row["process"]][key].append(row[key])
        series = {key: [per_second[second][key] for second in seconds] for key in fields[3:]}
        summary["apps"][app] = {
            "mean_cpu_percent": round(statistics.mean(series["cpu_percent"]), 3),
            "peak_cpu_percent": round(max(series["cpu_percent"]), 2),
            "seconds_above_1_percent": sum(value > 1 for value in series["cpu_percent"]),
            "mean_working_set_mb": round(statistics.mean(series["working_set_mb"])),
            "mean_private_mb": round(statistics.mean(series["private_mb"])),
            "peak_private_mb": round(max(series["private_mb"])),
            "mean_processes": round(statistics.mean(series["count"]), 1),
            "mean_threads": round(statistics.mean(series["threads"])),
            "written_mb": round(sum(series["write_kb"]) / 1024, 1),
            "processes": {name: {"count": round(statistics.mean(values["count"]), 1),
                                 "mean_cpu_percent": round(sum(values["cpu_percent"]) / len(seconds), 3),
                                 "mean_private_mb": round(sum(values["private_mb"]) / len(seconds)),
                                 "mean_working_set_mb": round(sum(values["working_set_mb"]) / len(seconds))}
                          for name, values in sorted(processes.items())},
        }
    # GPU counters: columns are named ...(pid_N_luid_..._engtype_T)\Utilization Percentage.
    try:
        with (output / "gpu.csv").open(encoding="utf-8", errors="replace") as file:
            table = list(csv.reader(file))
        header, data = table[0], [line for line in table[1:] if len(line) == len(table[0])]
        usage = defaultdict(list)
        for index, column in enumerate(header[1:], 1):
            match = re.search(r"pid_(\d+)_", column)
            if not match or int(match.group(1)) not in names:
                continue
            app = names[int(match.group(1))][0]
            engine = re.search(r"engtype_([^)]+)\)", column)
            kind = "engine " + engine.group(1) + " percent" if engine else ("dedicated memory MB" if "Dedicated" in column else "shared memory MB")
            values = [float(line[index]) if line[index].strip() not in ("", " ") else 0.0 for line in data]
            values = [value / 1048576 for value in values] if not engine else [min(value, 100.0) for value in values]
            usage[(app, kind)].append(values)
        for (app, kind), columns in sorted(usage.items()):
            combined = [sum(values) for values in zip(*columns)]
            if combined and max(combined) > 0:
                summary["gpu"].setdefault(app, {})[kind] = {"mean": round(statistics.mean(combined), 2), "median": round(statistics.median(combined), 2), "peak": round(max(combined), 2),
                                                             "active_share": round(sum(value > 1 for value in combined) / len(combined), 3)}
    except (OSError, IndexError, ValueError) as error:
        summary["gpu_error"] = str(error)
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=2))
    print("PASS: results in " + str(output))


if __name__ == "__main__":
    main()
