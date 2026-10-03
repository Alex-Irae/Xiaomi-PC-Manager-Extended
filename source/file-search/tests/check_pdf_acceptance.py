"""Check the user's hidden-PDF task against the real local retrieval service.

Dependencies: existing Python environment, local model, completed application index.
Outputs: new JSON/CSV/SVG evidence in --output; never changes the source PDF.
Command: .venv/Scripts/python.exe tests/check_pdf_acceptance.py --expected PATH.pdf
         --output results/NEW_RUN --label attempt01 --config config.json --data data
The expected path is used only to evaluate returned results, never to retrieve them.
"""
import argparse
import csv
import json
import logging
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from xiaomi_search.config import load
from xiaomi_search.service import Service


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expected", required=True, type=Path, help="User-supplied PDF path, used only as the acceptance target")
    parser.add_argument("--output", required=True, type=Path, help="Existing numbered results directory")
    parser.add_argument("--label", default="attempt01", help="New evidence filename prefix; existing output is refused")
    parser.add_argument("--config", default="config.json", help="Search settings and local model")
    parser.add_argument("--data", default="data", help="Existing application index/cache")
    args = parser.parse_args()
    if not args.expected.is_file() or args.expected.suffix.lower() != ".pdf":
        parser.error("--expected must be an existing PDF")
    if not args.output.is_dir() or Path(args.label).name != args.label:
        parser.error("Use an existing results directory and a plain filename label")
    target = args.expected.resolve()
    evidence = args.output / (args.label + ".json")
    if evidence.exists():
        parser.error("Evidence already exists; choose a new label")
    settings = load(args.config)
    service = Service(settings, args.data, args.config)
    report = {"seed": 42, "expected_pdf": str(target), "configuration": settings, "cases": []}
    try:
        model_id = service.embedder.identity()
        with service.store.connect() as db:
            total, ready = db.execute("SELECT COUNT(*),SUM(c.vector IS NOT NULL AND c.model_id=?) FROM chunks c JOIN files f ON f.id=c.file_id WHERE f.active=1 AND f.status='indexed'", (model_id,)).fetchone()
        report["coverage"] = {"passages": total, "matching_model_vectors": ready or 0}
        if not total or ready != total:
            raise AssertionError(f"Full semantic backfill is incomplete: {ready}/{total}")
        for query in ["EFS", "estimation-free", "generate samples without estimating a score function",
                      "learningless synthesis utilizing collective repulsions",
                      "synthèse déterministe échantillonnage sans apprentissage"]:
            for mode in ["lexical", "hybrid", "hybrid_cached"]:
                print(f"SEARCH {mode}: {query}", flush=True)
                response = service.search({"text": query, "semantic": mode != "lexical"})
                rows = response["results"]
                rank = next((index + 1 for index, row in enumerate(rows) if Path(row["file_path"]).resolve() == target), None)
                hit = rows[rank - 1] if rank else None
                record = {"query": query, "mode": mode, "pdf_rank": rank, "timing": response["timing"], "warnings": response["warnings"],
                          "pdf_matches": hit["matches"] if hit else [], "pdf_location": hit.get("location") if hit else None,
                          "top5": [{key: row.get(key) for key in ["name", "file_path", "matches", "location", "score"]} for row in rows[:5]]}
                report["cases"].append(record)
                print(f"RESULT rank={rank}, ms={response['timing']['total_ms']}, matches={record['pdf_matches']}", flush=True)
        report["device"] = service.embedder.device
        report["counts"] = service.store.counts()
        required = [case for case in report["cases"] if case["query"] in ("EFS", "estimation-free")]
        report["passed"] = all(case["pdf_rank"] == 1 and not case["warnings"] for case in required)
        report["paraphrase_top5"] = all(case["pdf_rank"] is not None and case["pdf_rank"] <= 5 for case in report["cases"] if case["query"].startswith("generate") and case["mode"] != "lexical")
        meaning_cases = [case for case in report["cases"] if case["query"].startswith(("learningless", "synthèse"))]
        report["meaning_only_top5"] = all(case["pdf_rank"] is None if case["mode"] == "lexical" else case["pdf_rank"] is not None and case["pdf_rank"] <= 5 and case["pdf_matches"] == ["Semantic"] for case in meaning_cases)
        report["passed"] = report["passed"] and report["paraphrase_top5"] and report["meaning_only_top5"]
    except Exception as error:
        report.update(passed=False, error=str(error))
        print("FAIL: " + str(error), flush=True)
    finally:
        service.close()
    with evidence.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, ensure_ascii=False)
    with (args.output / (args.label + ".csv")).open("x", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["query", "mode", "pdf_rank", "total_ms", "cache_hit"])
        for case in report["cases"]:
            writer.writerow([case["query"], case["mode"], case["pdf_rank"], case["timing"]["total_ms"], case["timing"].get("cache_hit", False)])
    # Ranking is the acceptance criterion; a latency plot shows cold versus cached cost separately.
    bars = [(case["query"], case["mode"], case["timing"]["total_ms"]) for case in report["cases"]]
    maximum = max((value for _, _, value in bars), default=1) or 1
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="1050" height="{100+46*len(bars)}"><rect width="100%" height="100%" fill="white"/><g font-family="Segoe UI" fill="#535861"><text x="22" y="32" font-size="20">Full-index PDF retrieval latency</text><text x="22" y="56" font-size="12">Milliseconds, lower is faster. First hybrid includes model setup. See CSV for PDF ranks.</text>']
    for index, (query, mode, value) in enumerate(bars):
        y = 82 + index * 46
        parts.append(f'<text x="22" y="{y+17}" font-size="11">{query[:48]} · {mode}</text><rect x="455" y="{y}" width="{465*value/maximum:.1f}" height="24" rx="5" fill="#3482ff"/><text x="{465+465*value/maximum:.1f}" y="{y+17}" font-size="11">{value:.1f} ms</text>')
    parts.append('</g></svg>')
    with (args.output / (args.label + ".svg")).open("x", encoding="utf-8") as stream:
        stream.write("".join(parts))
    print("PASS" if report.get("passed") else "FAIL", flush=True)
    return 0 if report.get("passed") else 1


if __name__ == "__main__":
    sys.exit(main())
