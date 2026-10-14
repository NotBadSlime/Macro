import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("compare", Path(__file__).parents[1] / "scripts/compare_frame_times.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class FrameComparisonTests(unittest.TestCase):
    def sample(self, fps=100, p99=10):
        return dict(duration_seconds=600, invalid_rows=0, average_fps=fps, p99_frame_ms=p99)

    def test_boundaries_and_each_round(self):
        baseline = [self.sample() for _ in range(5)]
        candidate = [self.sample(98, 10.3) for _ in range(5)]
        self.assertEqual(module.compare(baseline, candidate, "balanced")["status"], "pass")
        candidate[2] = self.sample(97.9, 10.3)
        self.assertEqual(module.compare(baseline, candidate, "balanced")["status"], "fail")

    def test_unstable_or_short_captures_are_not_passes(self):
        baseline = [self.sample() for _ in range(5)]
        candidate = baseline.copy()
        baseline[0] = self.sample(110)
        self.assertEqual(module.compare(baseline, candidate, "low")["status"], "inconclusive")
        candidate[0] = dict(self.sample(), duration_seconds=30)
        self.assertEqual(module.compare(candidate, candidate, "extreme")["status"], "inconclusive")

    def test_fps_is_count_over_elapsed_and_swap_chains_are_not_mixed(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "frames.csv"
            path.write_text("Application,ProcessID,SwapChainAddress,MsBetweenPresents\ngame,1,a,10\ngame,1,a,30\n")
            result = module.capture(path, "MsBetweenPresents")
            self.assertEqual(result["average_fps"], 50)
            self.assertEqual(result["p99_frame_ms"], 30)
            with path.open("a") as stream:
                stream.write("game,1,b,10\n")
            with self.assertRaises(ValueError):
                module.capture(path, "MsBetweenPresents")


if __name__ == "__main__":
    unittest.main()
