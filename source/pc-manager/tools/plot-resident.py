"""Purpose: plot recorded CPU/memory/power counters without attributing whole-machine watts to this app.
Dependencies: Python standard library only. Outputs: new resident-figure.svg files beside input CSVs.
Command: python tools/plot-resident.py results/001_20260927T044703132_resident results/002_20260927T050133513_resident results/003_20260927T050306091_resident
"""
import argparse
import csv
import html
import json
import math
from pathlib import Path


def plot_run(folder: Path) -> None:
    """Draw four time-series panels; blank counters create gaps rather than fabricated zero readings."""
    output = folder / "resident-figure.svg"
    if output.exists():
        raise FileExistsError(f"Preserved existing figure: {output}")
    with (folder / "samples.csv").open(encoding="utf-8-sig", newline="") as source:
        rows = list(csv.DictReader(source))
    summary = json.loads((folder / "summary.json").read_text(encoding="utf-8-sig"))
    seconds = [float(row["seconds"]) for row in rows]
    end = max(seconds) or 1
    svg = ['<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="900" viewBox="0 0 1000 900">',
           '<rect width="1000" height="900" fill="white"/>',
           '<g font-family="Segoe UI,Arial,sans-serif" fill="#35373a">']
    def label(x, y, text, size=14, anchor="start"):
        svg.append(f'<text x="{x}" y="{y}" font-size="{size}" text-anchor="{anchor}">{html.escape(text)}</text>')
    label(500, 30, summary["label"] + " | observational process-tree measurements", 21, "middle")
    label(500, 55, "Whole-machine power cannot isolate app watts. Shared working-set pages can be counted twice.", 13, "middle")
    for index, (field, title) in enumerate([
        ("cpu_percent", "Process-tree CPU (% of total machine capacity)"),
        ("working_set_mib", "Summed working sets (MiB)"),
        ("cpu_package_w", "Whole CPU package (W), includes other workloads and observer"),
        ("battery_flow_w", "Battery flow (W), positive charging / negative discharging"),
    ]):
        top = 95 + index * 190
        values = [float(row[field]) if row[field] else math.nan for row in rows]
        if field == "cpu_percent":
            values[0] = math.nan  # The initial snapshot has no preceding CPU-time delta.
        finite = [value for value in values if math.isfinite(value)]
        low = min(0, min(finite, default=0))
        high = max(finite, default=1)
        high = max(low + 0.01, high + max(abs(high) * 0.1, 0.01))
        span = high - low
        label(110, top - 10, title, 15)
        for tick in range(5):
            y = top + tick * 130 / 4
            value = high - tick * span / 4
            svg.append(f'<path d="M110 {y}H960" stroke="#e5e7eb" fill="none"/>')
            label(98, y + 5, f"{value:.3f}" if high < 1 else f"{value:.1f}", 12, "end")
        for tick in range(5):
            x = 110 + tick * 850 / 4
            label(x, top + 151, f"{tick * end / 4:.0f}", 12, "middle")
        segment = []
        def flush():
            if segment:
                svg.append('<polyline fill="none" stroke="#3482ff" stroke-width="2" points="' + ' '.join(segment) + '"/>')
                segment.clear()
        for second, value in zip(seconds, values):
            if not math.isfinite(value):
                flush()
                continue
            x = 110 + second / end * 850
            y = top + (high - value) / span * 130
            segment.append(f"{x:.2f},{y:.2f}")
        flush()
    label(535, 866, "Elapsed observation time (seconds)", 15, "middle")
    svg.append('</g></svg>')
    output.write_text('\n'.join(svg), encoding="utf-8")
    print(f"PASS saved {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Plot existing resident CSVs without replacing previous figures.")
    parser.add_argument("folders", type=Path, nargs="+", help="Recorded folders containing samples.csv and summary.json")
    for run_folder in parser.parse_args().folders:
        plot_run(run_folder)
