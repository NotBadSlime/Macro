import csv
import importlib.util
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("summary", Path(__file__).parents[1] / "scripts/summarize_input_api.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def group(round_p50, p50, p99=500.0, mx=900.0, lost=0, duplicates=0):
    """Hand-built group: per-round P50s plus pooled stats (round P99/max follow the pooled values)."""
    return {
        "pooled": {"n": 1000, "p50": p50, "p99": p99, "max": mx},
        "rounds": [{"round": i + 1, "p50": v, "p99": p99, "max": mx} for i, v in enumerate(round_p50)],
        "lost": lost, "duplicates": duplicates,
    }


def write_csv(path, header, rows):
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(header)
        writer.writerows(rows)


class PercentileTests(unittest.TestCase):
    def test_nearest_rank(self):
        self.assertEqual(module.stats([4, 1, 3, 2])["p50"], 2)
        self.assertEqual(module.stats(list(range(1, 1001)))["p99"], 990)
        self.assertEqual(module.stats(list(range(1, 1001)))["max"], 1000)

    def test_empty_is_none(self):
        self.assertIsNone(module.stats([]))


class VerdictTests(unittest.TestCase):
    base = group([1000] * 5, 1000)

    def test_clear_win_is_candidate(self):
        self.assertEqual(module.verdict(self.base, group([800] * 5, 800)), "candidate")

    def test_small_gain_is_no_net_benefit(self):
        self.assertEqual(module.verdict(self.base, group([950] * 5, 950)), "no-net-benefit")

    def test_tail_regression_blocks_candidate(self):
        self.assertEqual(module.verdict(self.base, group([800] * 5, 800, p99=600.0)), "no-net-benefit")
        self.assertEqual(module.verdict(self.base, group([800] * 5, 800, mx=1200.0)), "no-net-benefit")

    def test_needs_four_improved_rounds(self):
        self.assertEqual(module.verdict(self.base, group([800, 800, 800, 1100, 1100], 800)), "no-net-benefit")
        self.assertEqual(module.verdict(self.base, group([800, 800, 800, 800, 1100], 800)), "candidate")

    def test_lost_or_duplicate_is_invalid(self):
        self.assertEqual(module.verdict(self.base, group([800] * 5, 800, lost=1)), "invalid")
        self.assertEqual(module.verdict(self.base, group([800] * 5, 800, duplicates=1)), "invalid")

    def test_too_few_rounds_is_inconclusive(self):
        self.assertEqual(module.verdict(group([1000] * 4, 1000), group([800] * 4, 800)), "inconclusive")

    def test_noisy_baseline_is_inconclusive(self):
        noisy = group([900, 1000, 1050, 1000, 1000], 1000)
        self.assertEqual(module.verdict(noisy, group([700] * 5, 700)), "inconclusive")


class CaptureSummaryTests(unittest.TestCase):
    def test_lost_duplicate_and_latency(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "capture-samples.csv"
            header = ["round", "strategy", "sample", "send_qpc", "capture_qpc", "captures", "frequency"]
            # frequency 1_000_000 -> 1 tick = 1 microsecond
            write_csv(path, header, [
                [1, "ll-hook", 0, 1000, 1100, 1, 1000000],
                [1, "ll-hook", 1, 2000, 2300, 1, 1000000],
                [1, "ll-hook", 2, 3000, 0, 0, 1000000],
                [2, "ll-hook", 0, 4000, 4200, 2, 1000000],
                [1, "raw-input", 0, 1000, 1050, 1, 1000000],
            ])
            result = module.summarize_capture(path)
            ll = result["groups"]["ll-hook"]
            self.assertEqual(ll["lost"], 1)
            self.assertEqual(ll["duplicates"], 1)
            self.assertEqual(ll["pooled"]["n"], 3)
            self.assertEqual(ll["pooled"]["p50"], 200)
            self.assertEqual([r["round"] for r in ll["rounds"]], [1, 2])
            self.assertEqual(result["groups"]["raw-input"]["pooled"]["p50"], 50)


class InjectSummaryTests(unittest.TestCase):
    def test_wrong_key_counts_as_lost_and_up_latency_is_reported(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "inject-samples.csv"
            header = ["round", "variant", "hook", "sample", "send_down_qpc", "send_up_qpc",
                      "recv_down_qpc", "recv_up_qpc", "down_count", "up_count", "vk", "frequency"]
            write_csv(path, header, [
                [1, "vk", "none", 0, 1000, 5000, 1400, 5300, 1, 1, 135, 1000000],
                [1, "vk", "none", 1, 6000, 9000, 6500, 9200, 1, 1, 135, 1000000],
                [1, "scan", "none", 0, 1000, 5000, 1200, 5100, 1, 1, 17, 1000000],  # wrong key
            ])
            result = module.summarize_inject(path)
            vk = result["groups"]["vk|none"]
            self.assertEqual(vk["pooled"]["p50"], 400)
            self.assertEqual(result["up_groups"]["vk|none"]["pooled"]["p50"], 200)
            self.assertEqual(result["groups"]["scan|none"]["lost"], 1)


class PairedTests(unittest.TestCase):
    def test_delta_against_baseline_by_nth_capture(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "paired.csv"
            write_csv(path, ["sample", "strategy", "capture_qpc", "frequency"], [
                [0, "ll-hook", 1000, 1000000], [1, "ll-hook", 2000, 1000000],
                [0, "raw-input", 1100, 1000000], [1, "raw-input", 1900, 1000000],
                [0, "poll-1ms", 1500, 1000000],
            ])
            result = module.summarize_paired(path, "ll-hook")
            self.assertEqual(result["raw-input"]["n"], 2)
            self.assertEqual(result["raw-input"]["p50"], -100)
            self.assertEqual(result["raw-input"]["max"], 100)
            self.assertEqual(result["poll-1ms"]["count_mismatch"], True)


if __name__ == "__main__":
    unittest.main()
