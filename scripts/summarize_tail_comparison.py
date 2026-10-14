"""Summarize repeated native deadline probes, retaining maximums and early events."""
import argparse
import csv
import hashlib
import json
import re
from pathlib import Path
from summarize_end_to_end import percentiles


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--log", required=True)
    parser.add_argument("--pattern", required=True, help="CSV filename with {variant} and {round}")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    reports, groups = [], {}
    key = None
    for line in (args.directory / args.log).read_text(encoding="utf-8-sig").splitlines():
        label = re.fullmatch(r"variant=(\w+) round=(\d+) mask=(\S+)", line)
        if label:
            key = label.groups()
        elif line.startswith("fake input;") and key:
            variant, repeat, mask = key
            metrics = {k: float(v) for k, v in re.findall(r"(\w+)=(-?[\d.]+)", line)}
            path = args.directory / args.pattern.format(variant=variant, round=repeat)
            with path.open(encoding="utf-8-sig", newline="") as stream:
                samples = list(csv.DictReader(stream))
            if len(samples) != int(metrics["events"]) or [int(s["batch"]) for s in samples] != list(range(len(samples))):
                raise ValueError(f"Missing/reordered events: {path}")
            errors = [(int(s["submit_qpc"]) - int(s["due_qpc"])) * 1e6 / int(s["frequency"])
                      for s in samples]
            report = {"file": path.name, "variant": variant, "round": int(repeat), "mask": mask,
                      "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "n": len(errors),
                      "metrics": percentiles(errors), "early": sum(e < 0 for e in errors),
                      "over_100_us": sum(e > 100 for e in errors),
                      "over_1000_us": sum(e > 1000 for e in errors), "process": metrics}
            reports.append(report)
            groups.setdefault(variant, []).extend(errors)
            key = None
    if not reports or key:
        raise ValueError("No complete benchmark results or incomplete final round")
    pooled = []
    for variant, errors in groups.items():
        runs = [r for r in reports if r["variant"] == variant]
        pooled.append({"variant": variant, "n": len(errors), "rounds": len(runs),
                       "metrics": percentiles(errors), "early": sum(e < 0 for e in errors),
                       "over_100_us": sum(e > 100 for e in errors),
                       "over_1000_us": sum(e > 1000 for e in errors),
                       "round_max_us": [r["metrics"]["max_us"] for r in runs],
                       "startup_us": [r["process"]["startup_us"] for r in runs],
                       "logical_cores_cpu": sum(r["process"]["cpu_ms"] for r in runs) /
                       sum(r["process"]["wall_ms"] for r in runs)})
    output = {"measurement": "QPC immediately before fake input; not target receive latency",
              "percentiles": "nearest-rank; maxima are observations, not bounds", "rounds": reports, "pooled": pooled}
    (args.directory / args.output).write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    for group in pooled:
        print(group["variant"], "n=" + str(group["n"]), group["metrics"], "round_max", group["round_max_us"])


if __name__ == "__main__":
    main()
