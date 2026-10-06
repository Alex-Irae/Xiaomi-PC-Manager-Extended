"""Time the one search step that depends on the device: turning a query into a vector, on GPU, NPU and CPU.

Dependencies: the app's own Python runtime and local embedding model; no index is read or written.
Outputs: one JSON per device and summary.json under --output (new folder). Nothing is installed or downloaded.
Each device runs in its own process: load, one untimed call, then queries back to back and after idle pauses,
because a search usually comes after the hardware has been idle for a while.
Command: APP/runtime/python/python.exe tests/benchmark_query_devices.py --config CONFIG.json --data DATA_DIR --output results/NEW_RUN
"""
import argparse
import json
import os
import statistics
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
QUERIES = ['invoice 2024', 'notes about transformers and attention', 'where is the lease contract for the apartment',
           'python script that renames photos by date', 'budget spreadsheet', 'research plan']
PAUSES = [0, 0.5, 5, 30]


def worker(args):
    import psutil
    from xiaomi_search.config import load
    from xiaomi_search.embedding import Embedder
    settings = load(args.config)
    settings.update(preferred_device='auto', devices=[args.device], model_standby='keep_loaded')
    embedder = Embedder(settings, args.data)
    process = psutil.Process()
    start = time.perf_counter()
    embedder._load()
    report = {'device_requested': args.device, 'device_actual': embedder.device, 'load_seconds': round(time.perf_counter() - start, 2), 'pauses': {}}
    if embedder.device != args.device:
        report['error'] = 'the model fell back to another device'
    else:
        embedder.encode([QUERIES[0]], query=True)  # untimed first call
        for pause in PAUSES:
            times, cpu = [], []
            for repeat in range(args.repeats):
                for query in QUERIES if pause == 0 else QUERIES[:2]:
                    time.sleep(pause)
                    before = process.cpu_times()
                    start = time.perf_counter()
                    embedder.encode([query], query=True)
                    times.append((time.perf_counter() - start) * 1000)
                    after = process.cpu_times()
                    cpu.append((after.user + after.system - before.user - before.system) * 1000)
            report['pauses'][str(pause)] = {'samples': len(times), 'median_ms': round(statistics.median(times), 1), 'worst_ms': round(max(times), 1),
                                            'median_cpu_ms': round(statistics.median(cpu), 1)}
        report['private_mb'] = round(process.memory_info().private / 1048576)
    (args.output / (args.device + '.json')).write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report))
    os._exit(0)  # skip the model's idle-release timers


def main():
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True, help='New output directory, refused if it exists')
    parser.add_argument('--config', required=True, help='Settings file of the app (read only)')
    parser.add_argument('--data', required=True, help='Data directory with the model cache (compiled models may be added to it)')
    parser.add_argument('--repeats', type=int, default=3, help='Rounds per idle pause')
    parser.add_argument('--device', choices=['CPU', 'GPU', 'NPU'], help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.device:
        return worker(args)
    args.output.mkdir(parents=True, exist_ok=False)
    (args.output / 'config.json').write_text(json.dumps({'queries': QUERIES, 'pauses_seconds': PAUSES, 'repeats': args.repeats,
                                                         'measure': 'wall time of one query embedding; CPU time of this process during it'}, indent=2), encoding='utf-8')
    reports = []
    for device in ['GPU', 'NPU', 'CPU']:
        command = [sys.executable, '-B', str(Path(__file__).resolve()), '--device', device, '--output', str(args.output), '--config', args.config, '--data', args.data, '--repeats', str(args.repeats)]
        with (args.output / (device + '.log')).open('w', encoding='utf-8') as log:
            try:
                subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, timeout=900)
            except subprocess.TimeoutExpired:
                pass
        file = args.output / (device + '.json')
        reports.append(json.loads(file.read_text(encoding='utf-8')) if file.exists() else {'device_requested': device, 'error': 'no result; see ' + device + '.log'})
        print(json.dumps(reports[-1]), flush=True)
    (args.output / 'summary.json').write_text(json.dumps(reports, indent=2), encoding='utf-8')
    print('PASS: results in ' + str(args.output))


if __name__ == '__main__':
    main()
