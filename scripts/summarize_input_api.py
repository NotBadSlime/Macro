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
PAIR_WINDOW_MS = 50.0
KNOWN_STRATEGIES = ("ll-hook", "raw-input", "register-hotkey", "poll-spin", "poll-1ms")


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


def read_blocks(path):
    """capture-blocks.csv -> per-strategy CPU/wall totals and error strings; {} when the file is absent."""
    if path is None or not path.exists():
        return {}
    blocks = {}
    for row in read_rows(path):
        entry = blocks.setdefault(row["strategy"], {"samples": 0, "cpu_ms": 0.0, "wall_ms": 0.0, "errors": []})
        entry["samples"] += int(row["samples"])
        entry["cpu_ms"] += float(row["cpu_ms"])
        entry["wall_ms"] += float(row["wall_ms"])
        if row.get("error", "").strip():
            entry["errors"].append(row["error"].strip())
    for entry in blocks.values():
        entry["cpu_ms_per_1000"] = entry["cpu_ms"] * 1000 / entry["samples"] if entry["samples"] else None
    return blocks


def summarize_capture(path, blocks_path=None):
    records = {}
    for row in read_rows(path):
        count = int(row["captures"])
        capture = int(row["capture_qpc"])
        if count and capture <= 0:
            count = 0
        latency = (capture - int(row["send_qpc"])) * 1e6 / int(row["frequency"]) if count else 0.0
        records.setdefault(row["strategy"], []).append((int(row["round"]), latency, count))
    if blocks_path is None:
        blocks_path = path.with_name("capture-blocks.csv")
    return {**fingerprint(path), "groups": {k: build_group(v) for k, v in records.items()},
            "blocks": read_blocks(blocks_path)}


def judge_capture(result, baseline):
    verdicts = judge(result["groups"], baseline)
    for strategy, entry in result["blocks"].items():
        if entry["errors"] and strategy != baseline:
            verdicts[strategy] = "invalid"
    return verdicts


def summarize_inject(path):
    down, up, gap = {}, {}, {}
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
        gap.setdefault(key, []).append(
            (round_number, (int(row["recv_up_qpc"]) - int(row["recv_down_qpc"])) * 1e6 / freq if up_count else 0.0, up_count))
    groups = {k: build_group(v) for k, v in down.items()}
    return {**fingerprint(path), "groups": groups,
            "up_groups": {k: build_group(v) for k, v in up.items()},
            "gap_groups": {k: build_group(v) for k, v in gap.items()},
            "hook_tax": hook_tax(groups)}


def hook_tax(groups):
    """Per variant: pooled p50/p99 of `X|ll-empty` minus `X|none` (microseconds)."""
    tax = {}
    for key, value in groups.items():
        variant, _, hook = key.partition("|")
        other = groups.get(f"{variant}|ll-empty")
        if hook != "none" or other is None or not value["pooled"] or not other["pooled"]:
            continue
        tax[variant] = {"p50": other["pooled"]["p50"] - value["pooled"]["p50"],
                        "p99": other["pooled"]["p99"] - value["pooled"]["p99"]}
    return tax


def judge_per_hook(groups, reference="vk", exclude=()):
    """Judge each variant against `vk` under the same hook condition."""
    verdicts = {}
    for key in groups:
        variant, _, hook = key.partition("|")
        if variant == reference or variant in exclude:
            continue
        base = groups.get(f"{reference}|{hook}")
        verdicts[key] = verdict(base, groups[key]) if base else "inconclusive"
    return verdicts


def judge_inject(result):
    """(down verdicts, up verdicts); merged-pair up latency is measured from send_down so it is not judged."""
    return (judge_per_hook(result["groups"]),
            judge_per_hook(result["up_groups"], exclude=("merged-pair",)))


def match_ticks(base, ticks, window):
    """Greedy nearest-tick matching: each strategy tick is used at most once. Returns (deltas, unmatched_base, unmatched_strategy)."""
    free = sorted(ticks)
    deltas, unmatched_base = [], 0
    for b in sorted(base):
        best = min(free, key=lambda t: abs(t - b), default=None)
        if best is not None and abs(best - b) <= window:
            deltas.append(best - b)
            free.remove(best)
        else:
            unmatched_base += 1
    return deltas, unmatched_base, len(free)


def summarize_paired(path, baseline):
    """Per-strategy delta (strategy - baseline, microseconds) matched by nearest tick within +-50 ms."""
    captures = {}
    freq = 1
    for row in read_rows(path):
        freq = int(row["frequency"])
        captures.setdefault(row["strategy"], []).append(int(row["capture_qpc"]))
    if not captures.get(baseline):
        raise ValueError(f"baseline {baseline} has no captures")
    window = PAIR_WINDOW_MS * freq / 1000
    result = {}
    for strategy in sorted(set(captures) | set(KNOWN_STRATEGIES)):
        if strategy == baseline:
            continue
        deltas, unmatched_base, unmatched_strategy = match_ticks(captures[baseline], captures.get(strategy, []), window)
        result[strategy] = {**(stats([d * 1e6 / freq for d in deltas]) or {"n": 0}),
                            "unmatched_baseline": unmatched_base, "unmatched_strategy": unmatched_strategy}
    return result


def judge(groups, baseline):
    return {key: verdict(groups[baseline], value) for key, value in groups.items() if key != baseline}


def print_table(title, groups, verdicts, baseline):
    baselines = {baseline} if isinstance(baseline, str) or baseline is None else set(baseline)
    print(title)
    print(f"{'candidate':28} {'n':>6} {'lost':>5} {'dup':>4} {'p50':>9} {'p95':>9} {'p99':>9} {'p99.9':>9} {'max':>9}  verdict")
    for key, value in groups.items():
        s = value["pooled"] or {}
        label = "baseline" if key in baselines else verdicts.get(key, "")
        print(f"{key:28} {s.get('n', 0):>6} {value['lost']:>5} {value['duplicates']:>4} "
              f"{s.get('p50', float('nan')):>9.1f} {s.get('p95', float('nan')):>9.1f} {s.get('p99', float('nan')):>9.1f} "
              f"{s.get('p999', float('nan')):>9.1f} {s.get('max', float('nan')):>9.1f}  {label}")


def print_extras(result):
    print("\ninject: hook tax (pooled, ll-empty minus none, us)")
    print(f"{'variant':16} {'p50':>9} {'p99':>9}")
    for variant, t in result["hook_tax"].items():
        print(f"{variant:16} {t['p50']:>9.1f} {t['p99']:>9.1f}")
    print_table("\ninject: down-to-up gap (recv_up - recv_down, us)", result["gap_groups"], {}, None)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("capture", "inject", "paired"):
        p = sub.add_parser(name)
        p.add_argument("csv", type=Path)
        if name != "inject":
            p.add_argument("--baseline", default="ll-hook")
        if name == "capture":
            p.add_argument("--blocks", type=Path)
        p.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.command == "paired":
        result = {**fingerprint(args.csv), "baseline": args.baseline, "deltas_us": summarize_paired(args.csv, args.baseline)}
        for key, value in result["deltas_us"].items():
            print(key, value)
    elif args.command == "capture":
        result = summarize_capture(args.csv, args.blocks)
        result["baseline"] = args.baseline
        result["verdicts"] = judge_capture(result, args.baseline)
        print_table("capture: time from send to capture (us)", result["groups"], result["verdicts"], args.baseline)
        if not result["blocks"]:
            print("note: no capture-blocks.csv found; CPU and errors not reported")
        for key, b in result["blocks"].items():
            per = b["cpu_ms_per_1000"]
            print(f"{key:16} cpu_ms={b['cpu_ms']:.0f} wall_ms={b['wall_ms']:.0f} "
                  f"cpu_ms/1000={'n/a' if per is None else format(per, '.1f')}")
            for error in b["errors"]:
                print(f"  ERROR {key}: {error}")
    else:
        result = summarize_inject(args.csv)
        result["verdicts"], result["up_verdicts"] = judge_inject(result)
        print_table("inject: send to receiver WM_KEYDOWN (us), judged vs vk under the same hook", result["groups"], result["verdicts"], ("vk|none", "vk|ll-empty"))
        up_groups = {k: v for k, v in result["up_groups"].items() if not k.startswith("merged-pair|")}
        print_table("inject: key-up send to receiver WM_KEYUP (us), merged-pair excluded", up_groups, result["up_verdicts"], ("vk|none", "vk|ll-empty"))
        print_extras(result)
    if args.output:
        args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
