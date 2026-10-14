"""Summarize InputApiProbe CSVs and judge each candidate against its baseline (stdlib only)."""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path

THRESHOLD_US = 100.0
MIN_ROUNDS = 5
MIN_IMPROVED_ROUNDS = 4
F24 = 0x87


def stats(values):
    """Nearest-rank percentiles; None when there are no samples."""
    values = sorted(values)
    if not values:
        return None

    def rank(p):
        return values[math.ceil(p * len(values)) - 1]

    return {"n": len(values), "p50": rank(.5), "p95": rank(.95), "p99": rank(.99),
            "p999": rank(.999), "max": values[-1]}


def build_group(records):
    """records: (round, latency_us, count). count 0 = lost, >1 = duplicate; latency counts when count >= 1."""
    lost = duplicates = 0
    pooled, by_round = [], {}
    for round_number, latency, count in records:
        if count == 0:
            lost += 1
            continue
        if count > 1:
            duplicates += 1
        pooled.append(latency)
        by_round.setdefault(round_number, []).append(latency)
    return {"pooled": stats(pooled),
            "rounds": [{"round": r, **stats(v)} for r, v in sorted(by_round.items())],
            "lost": lost, "duplicates": duplicates}


def verdict(base, cand, threshold_us=THRESHOLD_US):
    """candidate | no-net-benefit | inconclusive | invalid, per the design's decision gate."""
    if cand["lost"] or cand["duplicates"] or cand["pooled"] is None:
        return "invalid"
    if base["pooled"] is None or len(base["rounds"]) < MIN_ROUNDS or len(cand["rounds"]) < MIN_ROUNDS:
        return "inconclusive"
    base_p50 = [r["p50"] for r in base["rounds"]]
    if max(base_p50) - min(base_p50) >= threshold_us:
        return "inconclusive"
    cand_by_round = {r["round"]: r for r in cand["rounds"]}
    improved = sum(1 for r in base["rounds"]
                   if r["round"] in cand_by_round and cand_by_round[r["round"]]["p50"] < r["p50"])
    better = (base["pooled"]["p50"] - cand["pooled"]["p50"] >= threshold_us
              and cand["pooled"]["p99"] <= base["pooled"]["p99"]
              and cand["pooled"]["max"] <= base["pooled"]["max"]
              and improved >= MIN_IMPROVED_ROUNDS)
    return "candidate" if better else "no-net-benefit"


def read_rows(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    if not rows:
        raise ValueError(f"Empty CSV: {path}")
    return rows


def fingerprint(path):
    return {"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def summarize_capture(path):
    records = {}
    for row in read_rows(path):
        count = int(row["captures"])
        capture = int(row["capture_qpc"])
        if count and capture <= 0:
            count = 0
        latency = (capture - int(row["send_qpc"])) * 1e6 / int(row["frequency"]) if count else 0.0
        records.setdefault(row["strategy"], []).append((int(row["round"]), latency, count))
    return {**fingerprint(path), "groups": {k: build_group(v) for k, v in records.items()}}


def summarize_inject(path):
    down, up = {}, {}
    for row in read_rows(path):
        key = f"{row['variant']}|{row['hook']}"
        freq = int(row["frequency"])
        round_number = int(row["round"])
        count = int(row["down_count"]) if int(row["vk"]) == F24 else 0
        down.setdefault(key, []).append(
            (round_number, (int(row["recv_down_qpc"]) - int(row["send_down_qpc"])) * 1e6 / freq if count else 0.0, count))
        up_count = int(row["up_count"]) if count else 0
        up.setdefault(key, []).append(
            (round_number, (int(row["recv_up_qpc"]) - int(row["send_up_qpc"])) * 1e6 / freq if up_count else 0.0, up_count))
    return {**fingerprint(path),
            "groups": {k: build_group(v) for k, v in down.items()},
            "up_groups": {k: build_group(v) for k, v in up.items()}}


def summarize_paired(path, baseline):
    """Per-strategy delta (strategy - baseline, microseconds) for the nth capture of each strategy."""
    captures = {}
    freq = 1
    for row in read_rows(path):
        freq = int(row["frequency"])
        captures.setdefault(row["strategy"], {})[int(row["sample"])] = int(row["capture_qpc"])
    base = captures[baseline]
    result = {}
    for strategy, values in captures.items():
        if strategy == baseline:
            continue
        deltas = [(values[i] - base[i]) * 1e6 / freq for i in sorted(values) if i in base]
        result[strategy] = {**(stats(deltas) or {"n": 0}), "count_mismatch": len(values) != len(base)}
    return result


def judge(groups, baseline):
    return {key: verdict(groups[baseline], value) for key, value in groups.items() if key != baseline}


def print_table(title, groups, verdicts, baseline):
    print(title)
    print(f"{'candidate':28} {'n':>6} {'lost':>5} {'dup':>4} {'p50':>9} {'p95':>9} {'p99':>9} {'p99.9':>9} {'max':>9}  verdict")
    for key, value in groups.items():
        s = value["pooled"] or {}
        label = "baseline" if key == baseline else verdicts.get(key, "")
        print(f"{key:28} {s.get('n', 0):>6} {value['lost']:>5} {value['duplicates']:>4} "
              f"{s.get('p50', float('nan')):>9.1f} {s.get('p95', float('nan')):>9.1f} {s.get('p99', float('nan')):>9.1f} "
              f"{s.get('p999', float('nan')):>9.1f} {s.get('max', float('nan')):>9.1f}  {label}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("capture", "inject", "paired"):
        p = sub.add_parser(name)
        p.add_argument("csv", type=Path)
        p.add_argument("--baseline", default={"capture": "ll-hook", "inject": "vk|none", "paired": "ll-hook"}[name])
        p.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.command == "paired":
        result = {**fingerprint(args.csv), "baseline": args.baseline, "deltas_us": summarize_paired(args.csv, args.baseline)}
        for key, value in result["deltas_us"].items():
            print(key, value)
    else:
        result = summarize_capture(args.csv) if args.command == "capture" else summarize_inject(args.csv)
        result["baseline"] = args.baseline
        result["verdicts"] = judge(result["groups"], args.baseline)
        print_table(f"{args.command}: time from send to {'capture' if args.command == 'capture' else 'receiver WM_KEYDOWN'} (us)",
                    result["groups"], result["verdicts"], args.baseline)
        if args.command == "inject":
            result["up_verdicts"] = judge(result["up_groups"], args.baseline)
            print_table("inject: key-up send to receiver WM_KEYUP (us)", result["up_groups"], result["up_verdicts"], args.baseline)
    if args.output:
        args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
