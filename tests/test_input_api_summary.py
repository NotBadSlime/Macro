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


class CaptureBlocksTests(unittest.TestCase):
    def test_blocks_cpu_and_errors_invalidate(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "capture-samples.csv"
            header = ["round", "strategy", "sample", "send_qpc", "capture_qpc", "captures", "frequency"]
            write_csv(path, header, [
                [1, "ll-hook", 0, 1000, 1100, 1, 1000000],
                [1, "ll-hook", 1, 2000, 2100, 1, 1000000],
                [1, "poll-1ms", 0, 1000, 1500, 1, 1000000],
            ])
            write_csv(Path(folder) / "capture-blocks.csv",
                      ["round", "strategy", "samples", "lost", "wall_ms", "cpu_ms", "error"], [
                [1, "ll-hook", 2, 0, 100, 10, ""],
                [2, "ll-hook", 2, 0, 100, 30, ""],
                [1, "poll-1ms", 1, 0, 50, 20, "boom"],
            ])
            result = module.summarize_capture(path)
            ll = result["blocks"]["ll-hook"]
            self.assertEqual((ll["cpu_ms"], ll["wall_ms"], ll["samples"]), (40, 200, 4))
            self.assertEqual(ll["cpu_ms_per_1000"], 10000.0)
            self.assertEqual(ll["errors"], [])
            self.assertEqual(result["blocks"]["poll-1ms"]["errors"], ["boom"])
            verdicts = module.judge_capture(result, "ll-hook")
            self.assertEqual(verdicts["poll-1ms"], "invalid")

    def test_missing_blocks_file_is_skipped(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "capture-samples.csv"
            write_csv(path, ["round", "strategy", "sample", "send_qpc", "capture_qpc", "captures", "frequency"],
                      [[1, "ll-hook", 0, 1000, 1100, 1, 1000000]])
            self.assertEqual(module.summarize_capture(path)["blocks"], {})


class InjectJudgementTests(unittest.TestCase):
    def make(self, folder):
        path = Path(folder) / "inject-samples.csv"
        header = ["round", "variant", "hook", "sample", "send_down_qpc", "send_up_qpc",
                  "recv_down_qpc", "recv_up_qpc", "down_count", "up_count", "vk", "frequency"]
        rows = []
        # (variant, hook) -> down latency, up latency
        spec = {("vk", "none"): (400, 200), ("vk", "ll-empty"): (500, 250),
                ("scan", "none"): (300, 100), ("scan", "ll-empty"): (450, 150),
                ("merged-pair", "none"): (300, 100)}
        for (variant, hook), (d, u) in spec.items():
            for r in range(1, 6):
                rows.append([r, variant, hook, 0, 1000, 5000, 1000 + d, 5000 + u, 1, 1, 135, 1000000])
        write_csv(path, header, rows)
        return path

    def test_hook_tax_pairing_and_merged_pair(self):
        with tempfile.TemporaryDirectory() as folder:
            result = module.summarize_inject(self.make(folder))
            self.assertEqual(result["hook_tax"]["vk"]["p50"], 100)
            self.assertEqual(result["hook_tax"]["scan"]["p50"], 150)
            self.assertNotIn("merged-pair", result["hook_tax"])
            down, up = module.judge_inject(result)
            self.assertEqual(down["scan|none"], "candidate")      # 400 -> 300 vs vk|none
            self.assertEqual(down["scan|ll-empty"], "no-net-benefit")  # 500 -> 450 vs vk|ll-empty
            self.assertNotIn("vk|none", down)
            self.assertFalse([k for k in up if k.startswith("merged-pair")])
            self.assertIn("merged-pair|none", down)
            self.assertEqual(result["gap_groups"]["vk|none"]["pooled"]["p50"], 4000 + 200 - 400)


class PairedTests(unittest.TestCase):
    def write(self, folder, rows):
        path = Path(folder) / "paired.csv"
        write_csv(path, ["sample", "strategy", "capture_qpc", "frequency"], rows)
        return path

    def test_delta_against_baseline_by_nearest_tick(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self.write(folder, [
                [0, "ll-hook", 1000, 1000000], [1, "ll-hook", 2000, 1000000],
                [0, "raw-input", 1100, 1000000], [1, "raw-input", 1900, 1000000],
                [0, "poll-1ms", 1500, 1000000],
            ])
            result = module.summarize_paired(path, "ll-hook")
            self.assertEqual(result["raw-input"]["n"], 2)
            self.assertEqual(result["raw-input"]["p50"], -100)
            self.assertEqual(result["raw-input"]["max"], 100)
            poll = result["poll-1ms"]
            self.assertEqual((poll["n"], poll["p50"]), (1, 500))
            self.assertEqual((poll["unmatched_baseline"], poll["unmatched_strategy"]), (1, 0))

    def test_far_ticks_unmatched_and_empty_strategy_present(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self.write(folder, [
                [0, "ll-hook", 1000, 1000000], [0, "raw-input", 1000 + 60000, 1000000],
                [0, "register-hotkey", 1000, 1000000], [1, "register-hotkey", 1020, 1000000],
            ])
            result = module.summarize_paired(path, "ll-hook")
            self.assertEqual(result["raw-input"]["n"], 0)
            self.assertEqual(result["raw-input"]["unmatched_baseline"], 1)
            self.assertEqual(result["raw-input"]["unmatched_strategy"], 1)
            self.assertEqual(result["register-hotkey"]["n"], 1)  # each strategy tick used once
            self.assertEqual(result["register-hotkey"]["unmatched_strategy"], 1)

    def test_missing_baseline_raises(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self.write(folder, [[0, "raw-input", 1000, 1000000]])
            with self.assertRaisesRegex(ValueError, "baseline ll-hook has no captures"):
                module.summarize_paired(path, "ll-hook")


class RawDiagTests(unittest.TestCase):
    HEADER = ["seq", "qpc", "frequency", "read_ok", "device", "type", "make_code", "vkey", "flags", "message", "extra"]
    F8 = 0x77

    def write(self, folder, rows):
        path = Path(folder) / "raw.csv"
        write_csv(path, self.HEADER, rows)
        return path

    @staticmethod
    def row(seq, vkey, flags=0, device=100, read_ok=1, rtype=1, make=66):
        return [seq, 1000 + seq, 1000000, read_ok, device, rtype, make, vkey, flags, 0x100, 0]

    def test_target_received(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self.write(folder, [
                self.row(0, self.F8), self.row(1, self.F8, flags=1),
                self.row(2, 0x41), self.row(3, self.F8, device=200),
            ])
            result = module.summarize_raw_diag(path, self.F8)
            self.assertEqual(result["total"], 4)
            self.assertEqual((result["target_down"], result["target_up"]), (2, 1))
            self.assertEqual(result["devices"], 2)
            self.assertEqual(result["by_vkey"][self.F8], 3)
            self.assertEqual(result["diagnosis"], "received-target")

    def test_other_keys_only_means_filtered_not_missing(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self.write(folder, [self.row(0, 0x41), self.row(1, 0x42)])
            result = module.summarize_raw_diag(path, self.F8)
            self.assertEqual(result["target_down"], 0)
            self.assertEqual(result["diagnosis"], "received-other-keys-only")

    def test_no_keyboard_wm_input_at_all(self):
        with tempfile.TemporaryDirectory() as folder:
            result = module.summarize_raw_diag(self.write(folder, [self.row(0, 0, read_ok=0)]), self.F8)
            self.assertEqual(result["read_failures"], 1)
            self.assertEqual(result["diagnosis"], "no-readable-wm-input")

    def test_non_keyboard_type_is_not_counted_as_key(self):
        with tempfile.TemporaryDirectory() as folder:
            result = module.summarize_raw_diag(self.write(folder, [self.row(0, self.F8, rtype=0)]), self.F8)
            self.assertEqual(result["target_down"], 0)
            self.assertEqual(result["non_keyboard"], 1)


if __name__ == "__main__":
    unittest.main()
