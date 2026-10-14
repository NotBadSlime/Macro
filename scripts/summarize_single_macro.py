"""Summarize paired single-macro probes without third-party dependencies."""
import argparse
import csv
import hashlib
import json
import math
import pathlib
import re


def quantiles(values):
    ordered = sorted(values)
    if not ordered:
        raise ValueError("Empty sample series")
    return {
        **{f"p{p * 100:g}_us": ordered[math.ceil(p * len(ordered)) - 1]
           for p in (0.5, 0.95, 0.99, 0.999)},
        "min_us": ordered[0], "max_us": ordered[-1],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=pathlib.Path)
    args = parser.parse_args()
    directory = args.directory.resolve()
    log_metrics = {}
    for name, dense in (("paired.log", False), ("paired-dense.log", True)):
        key = None
        for line in (directory / name).read_text(encoding="utf-8-sig").splitlines():
            label = re.search(r"variant=(\w+) mode=(\d+) round=(\d+)", line)
            if label:
                variant, mode, repeat = label.groups()
                key = f"{'dense' if dense else 'paired'}-{variant}-mode{mode}-round{repeat}.csv"
            elif line.startswith("fake input;") and key:
                log_metrics[key] = {k: float(v) for k, v in re.findall(r"(\w+)=(-?[\d.]+)", line)}
    records = []
    for pattern, kind in (("paired-*-mode*-round*.csv", "periodic"),
                          ("dense-*-mode*-round*.csv", "dense")):
        for path in sorted(directory.glob(pattern)):
            match = re.fullmatch(r"(?:paired|dense)-(\w+)-mode(\d+)-round(\d+)\.csv", path.name)
            if not match:
                continue
            variant, mode, repeat = match.groups()
            with path.open(newline="", encoding="utf-8-sig") as stream:
                samples = list(csv.DictReader(stream))
            metrics = log_metrics[path.name]
            if len(samples) != int(metrics["events"]) or [int(s["batch"]) for s in samples] != list(range(len(samples))):
                raise ValueError(f"Missing or reordered samples: {path.name}")
            signed = [(int(s["submit_qpc"]) - int(s["due_qpc"])) * 1e6 / int(s["frequency"])
                      for s in samples]
            records.append({
                "file": path.name, "kind": kind, "variant": variant,
                "mode": int(mode), "round": int(repeat), "n": len(samples),
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                "signed_error": quantiles(signed),
                "early": sum(x < 0 for x in signed),
                "over_100_us": sum(x > 100 for x in signed),
                "over_250_us": sum(x > 250 for x in signed),
                "over_1000_us": sum(x > 1000 for x in signed),
                "process_cpu_ms": metrics["cpu_ms"], "wall_ms": metrics["wall_ms"],
                "startup_us": metrics["startup_us"],
            })
    groups = []
    for kind in ("periodic", "dense"):
        for mode in (1, 2):
            for variant in ("baseline", "final"):
                rows = [r for r in records if (r["kind"], r["mode"], r["variant"]) == (kind, mode, variant)]
                if not rows:
                    continue
                groups.append({
                    "kind": kind, "mode": mode, "variant": variant,
                    "runs": len(rows), "n": sum(r["n"] for r in rows),
                    "p999_range_us": [min(r["signed_error"]["p99.9_us"] for r in rows), max(r["signed_error"]["p99.9_us"] for r in rows)],
                    "maximum_us": max(r["signed_error"]["max_us"] for r in rows),
                    "early": sum(r["early"] for r in rows),
                    "over_1000_us": sum(r["over_1000_us"] for r in rows),
                    "mean_process_cpu_ms": sum(r["process_cpu_ms"] for r in rows) / len(rows),
                    "mean_wall_ms": sum(r["wall_ms"] for r in rows) / len(rows),
                })
    binaries = {name: hashlib.sha256((directory / name).read_bytes()).hexdigest()
                for name in ("baseline.exe", "final.exe")}
    result = {"percentile": "nearest-rank from raw QPC; signed error retained",
              "note": "Fake input. Dense elapsed time measures throughput, not periodic jitter.",
              "binary_sha256": binaries, "groups": groups, "runs": records}
    (directory / "summary.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(groups, indent=2))


if __name__ == "__main__":
    main()
