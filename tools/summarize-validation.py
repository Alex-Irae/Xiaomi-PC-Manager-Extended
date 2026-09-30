"""Purpose: summarize saved runtime requests, retaining failures and producing a latency figure.
Dependencies: Python standard library. Outputs: fresh metrics.json, requests.csv and latency.svg beside each run.
Command: python tools/summarize-validation.py results/015_20260927T144952427_controls
"""
import argparse
import csv
import html
import json
import statistics
from pathlib import Path


def summarize(folder: Path) -> None:
    report = json.loads((folder / "summary.json").read_text(encoding="utf-8-sig"))
    rows = report["report"]["rows"]
    outputs = [folder / name for name in ("metrics.json", "requests.csv", "latency.svg")]
    if any(path.exists() for path in outputs):
        raise FileExistsError(f"Preserving existing outputs in {folder}")
    groups = {}
    for row in rows:
        name = row["name"]
        group = next((label for prefix, label in [
            ("brightness-changed", "Brightness"), ("mode-DOM", "UI mode selection"), ("mode-", "Performance"),
            ("charge-limit-", "Charge presets"), ("refresh-", "Refresh rate"),
            ("haptics-", "Haptic strength"), ("pressure-", "Click force"),
            ("input-", "Input devices"), ("awake-", "Stay awake"),
            ("telemetry-", "Telemetry"), ("OEM-", "OEM launch")]
            if name.startswith(prefix)), None)
        if group:
            groups.setdefault(group, []).append(row)
    metrics = {}
    for group, items in groups.items():
        values = [row.get("callMs", row.get("elapsedMs", row.get("inputToReadbackMs"))) for row in items]
        values = [value for value in values if isinstance(value, (int, float))]
        # Summarize request response time, including errors; exclude deliberate settle/hold intervals.
        metrics[group] = {"requests": len(items), "failed": sum(row["status"] == "fail" for row in items),
                          "median_ms": statistics.median(values) if values else None,
                          "maximum_ms": max(values) if values else None}
    counts = {status: sum(row["status"] == status for row in rows) for status in sorted({row["status"] for row in rows})}
    outputs[0].write_text(json.dumps({"counts": counts, "groups": metrics, "preferences_restored": report["preferencesRestored"],
        "restoration_errors": report["errors"], "limitations": "Small uncontrolled sample. Software response/readback, not optical latency. Charging observations, when present, require separate direct battery-status interpretation. UI mode selection is separate from firmware requests. Error times included; settle/hold excluded."}, indent=2), encoding="utf-8")
    with outputs[1].open("w", newline="", encoding="utf-8") as target:
        fields = ["name", "status", "response_ms", "input_to_readback_ms", "error"]
        writer = csv.DictWriter(target, fieldnames=fields)
        writer.writeheader()
        for row in rows:
            writer.writerow(dict(name=row["name"], status=row["status"], response_ms=row.get("callMs", row.get("elapsedMs")),
                                 input_to_readback_ms=row.get("inputToReadbackMs"), error=row.get("error", "")))
    height = 135 + len(metrics) * 54
    svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="1050" height="{height}" viewBox="0 0 1050 {height}">',
           '<rect width="100%" height="100%" fill="white"/>', '<g font-family="Segoe UI,Arial,sans-serif" fill="#303238">',
           f'<text x="25" y="30" font-size="20">{html.escape(folder.name)}: request latency and failures</text>',
           '<text x="25" y="55" font-size="13">Blue: median response. Thin line: maximum. Deliberate holds excluded. Physical latency unmeasured.</text>']
    maximum = max([m["maximum_ms"] or 0 for m in metrics.values()] + [1])
    for index, (group, metric) in enumerate(metrics.items()):
        y = 85 + index * 54
        median = metric["median_ms"] or 0
        high = metric["maximum_ms"] or 0
        svg += [f'<text x="25" y="{y+17}" font-size="15">{html.escape(group)}</text>',
                f'<rect x="190" y="{y}" width="{median/maximum*480:.2f}" height="25" rx="3" fill="#3482ff"/>',
                f'<path d="M190 {y+13}H{190+high/maximum*480:.2f}" stroke="#60656c"/>',
                f'<text x="690" y="{y+17}" font-size="14">{median:.1f} / {high:.1f} ms; n={metric["requests"]}, failures={metric["failed"]}</text>']
    svg += [f'<text x="25" y="{height-20}" font-size="13">Scale: 0 to {maximum:.1f} ms. Success requires matching device readback, not a fast error.</text>', '</g></svg>']
    outputs[2].write_text("\n".join(svg), encoding="utf-8")
    print("PASS", folder.name, json.dumps(counts), "restored:", report["preferencesRestored"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Summarize saved resident validation without replacing existing evidence.")
    parser.add_argument("folders", type=Path, nargs="+", help="Run directories containing a completed summary.json")
    for folder in parser.parse_args().folders:
        summarize(folder)
