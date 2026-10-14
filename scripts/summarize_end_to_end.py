"""Validate complete samples and summarize QPC-based input probes (stdlib only)."""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path


def percentiles(values):
    values = sorted(values)
    if not values:
        raise ValueError("No samples")
    return {**{f"p{p * 100:g}_us": round(values[math.ceil(p * len(values)) - 1], 3)
               for p in (.5, .95, .99, .999)}, "max_us": round(values[-1], 3)}


def summarize(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    if not rows:
        raise ValueError(f"Empty CSV: {path}")
    if [int(row["sample"]) for row in rows] != list(range(len(rows))):
        raise ValueError(f"Missing/reordered rows: {path}")
    for row in rows:
        if any(int(row[field]) != 1 for field in ("deliveries", "run_ok", "native")):
            raise ValueError(f"Invalid delivery/run/backend: {path}, row={row['sample']}")
        ticks = [int(row[f"{field}_qpc"]) for field in
                 ("hook", "dispatch", "worker", "queued", "submit")]
        if ticks[0] <= 0 or ticks != sorted(ticks) or int(row["receiver_qpc"]) < ticks[-1]:
            raise ValueError(f"Invalid timestamps: {path}, row={row['sample']}")
        if int(row["complete_qpc"]) < ticks[-1] or int(row["frequency"]) <= 0:
            raise ValueError(f"Incomplete sample: {path}, row={row['sample']}")
    result = {"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "groups": []}
    for source in sorted({row["source"] for row in rows}):
        group = [row for row in rows if row["source"] == source]
        stages = [("hook", "dispatch"), ("hook", "worker"), ("hook", "submit"),
                  ("submit", "receiver"), ("hook", "receiver")]
        if source == "synthetic_tagged":
            stages.append(("source", "hook"))
        if "output_hook_qpc" in group[0]:
            if any(int(row["output_hook_qpc"]) <= 0 for row in group):
                raise ValueError(f"Missing output hook: {path}")
            stages += [("submit", "output_hook"), ("output_hook", "receiver")]
        if "output_forwarded_qpc" in group[0]:
            if any(int(row["output_forwarded_qpc"]) <= 0 for row in group):
                raise ValueError(f"Missing hook forwarding result: {path}")
            stages += [("output_hook", "output_forwarded"), ("output_forwarded", "receiver")]
        metrics = {f"{a}_to_{b}": percentiles([
            (int(row[f"{b}_qpc"]) - int(row[f"{a}_qpc"])) * 1e6 / int(row["frequency"])
            for row in group]) for a, b in stages}
        if "native_api_total_us" in group[0]:
            metrics["native_api_total"] = percentiles([int(row["native_api_total_us"]) for row in group])
        result["groups"].append({"source": source, "n": len(group), "metrics": metrics,
                                 "note": "N < 1000: p99.9 is an order statistic, not a tail estimate"
                                 if len(group) < 1000 else "nearest-rank percentiles"})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("files", type=Path, nargs="+")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = [summarize(path) for path in args.files]
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    for file in report:
        for group in file["groups"]:
            print(file["file"], group["source"], "n=" + str(group["n"]), group["metrics"]["hook_to_receiver"])


if __name__ == "__main__":
    main()
