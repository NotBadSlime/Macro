"""Compare paired PresentMon captures. Never combines playback and recognition scenes.

Capture one process/swap chain using a fixed game replay and --v1_metrics.
Pass five baseline/candidate files in matching round order; alternate capture order.
The output includes invalid rows and incomplete captures; those cannot pass.
"""
import argparse
import csv
import json
import math
import statistics
from pathlib import Path

BUDGETS = {"low": (1, 2), "balanced": (2, 3), "extreme": (3, 5)}


def percentile(values, p):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * p) - 1)]


def capture(path, column, process=None, swap_chain=None):
    intervals, identities, invalid = [], set(), 0
    with Path(path).open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if column not in (reader.fieldnames or []):
            raise ValueError(f"{path}: missing explicit frame-time column {column}")
        for row in reader:
            if process and row.get("Application") != process:
                continue
            if swap_chain and row.get("SwapChainAddress") != swap_chain:
                continue
            identities.add((row.get("Application"), row.get("ProcessID"), row.get("SwapChainAddress")))
            try:
                value = float(row[column])
            except (ValueError, TypeError):
                invalid += 1
                continue
            if not math.isfinite(value) or value <= 0:
                invalid += 1
                continue
            intervals.append(value)
    if len(identities) != 1 or not intervals:
        raise ValueError(f"{path}: require exactly one nonempty process/swap chain, got {identities}")
    duration = sum(intervals) / 1000
    return {"file": str(path), "frames": len(intervals), "invalid_rows": invalid,
            "duration_seconds": duration, "average_fps": len(intervals) / duration,
            "p99_frame_ms": percentile(intervals, .99)}


def compare(baseline, candidate, profile):
    if len(baseline) != len(candidate) or not baseline:
        raise ValueError("Provide matched baseline/candidate rounds")
    fps_budget, frame_budget = BUDGETS[profile]
    reasons = []
    if len(baseline) < 5:
        reasons.append("At least five paired rounds are required")
    if any(x["duration_seconds"] < 599.9 for x in baseline + candidate):
        reasons.append("At least ten minutes per capture are required")
    # At most the first undefined interval is tolerated; retain the count in the report.
    if any(x["invalid_rows"] > 1 for x in baseline + candidate):
        reasons.append("Invalid frame intervals require investigation")
    for key, budget in [("average_fps", fps_budget), ("p99_frame_ms", frame_budget)]:
        values = [x[key] for x in baseline]
        spread = 100 * (max(values) - min(values)) / statistics.median(values)
        if spread > budget:
            reasons.append(f"Baseline {key} spread {spread:.3f}% exceeds budget {budget}%")
    pairs = []
    for a, b in zip(baseline, candidate):
        fps_loss = 100 * (1 - b["average_fps"] / a["average_fps"])
        frame_growth = 100 * (b["p99_frame_ms"] / a["p99_frame_ms"] - 1)
        pairs.append({"baseline": a, "candidate": b, "fps_loss_percent": fps_loss,
                      "p99_growth_percent": frame_growth,
                      "passes": fps_loss <= fps_budget + 1e-9 and frame_growth <= frame_budget + 1e-9})
    return {"profile": profile, "status": "inconclusive" if reasons else "pass" if all(p["passes"] for p in pairs) else "fail",
            "reasons": reasons, "fps_budget_percent": fps_budget,
            "p99_budget_percent": frame_budget, "pairs": pairs}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", nargs="+", required=True)
    parser.add_argument("--candidate", nargs="+", required=True)
    parser.add_argument("--profile", choices=BUDGETS, required=True)
    parser.add_argument("--scenario", choices=["playback", "mixed-recognition"], required=True)
    parser.add_argument("--frame-column", default="MsBetweenPresents")
    parser.add_argument("--process")
    parser.add_argument("--swap-chain")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    read = lambda paths: [capture(p, args.frame_column, args.process, args.swap_chain) for p in paths]
    result = compare(read(args.baseline), read(args.candidate), args.profile)
    result.update(scenario=args.scenario, frame_column=args.frame_column)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(result["status"])
    return 0 if result["status"] == "pass" else 2


if __name__ == "__main__":
    raise SystemExit(main())
