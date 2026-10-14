using System.IO;
using System.Diagnostics;
using System.Text;

namespace InputApiProbe;

internal static class CaptureExperiment
{
    private const int Warmup = 20;

    private static readonly (string Id, Func<ICaptureStrategy> Create)[] Candidates =
    {
        ("ll-hook", () => new LowLevelHookCapture(Native.VkF24)),
        ("raw-input", () => new RawInputCapture(Native.VkF24)),
        ("register-hotkey", () => new RegisterHotKeyCapture(Native.VkF24)),
        ("poll-spin", () => new PollingCapture(Native.VkF24, 0)),
        ("poll-1ms", () => new PollingCapture(Native.VkF24, 1)),
    };

    /// <summary>Injects F24 down, waits for the strategy to report it, then F24 up.</summary>
    private sealed class TriggerProbe
    {
        private readonly ManualResetEventSlim signal = new(false);
        private long capturedTick;
        private int count;

        public void OnCaptured(long tick)
        {
            if (Interlocked.Increment(ref count) == 1) { Volatile.Write(ref capturedTick, tick); signal.Set(); }
        }

        public (long Send, long Capture, int Count) Fire()
        {
            signal.Reset(); Volatile.Write(ref capturedTick, 0); Interlocked.Exchange(ref count, 0);
            long send = Stopwatch.GetTimestamp();
            Native.KeyDown(InjectVariant.Vk);
            bool seen = signal.Wait(100);
            long tick = Volatile.Read(ref capturedTick);
            Native.KeyUp(InjectVariant.Vk);
            Thread.Sleep(10); // let late duplicates arrive before reading the count
            return (send, seen ? tick : 0, Volatile.Read(ref count));
        }
    }

    public static int Run(int rounds, int perRound, string output, string[] args)
    {
        Native.Warmup(requireScan: false); // keep lazy static init out of timed regions
        Directory.CreateDirectory(output);
        using var samples = new StreamWriter(Path.Combine(output, "capture-samples.csv"), false, new UTF8Encoding(false));
        using var blocks = new StreamWriter(Path.Combine(output, "capture-blocks.csv"), false, new UTF8Encoding(false));
        samples.WriteLine("round,strategy,sample,send_qpc,capture_qpc,captures,frequency");
        blocks.WriteLine("round,strategy,samples,lost,wall_ms,cpu_ms,error");
        Provenance.Write(Path.Combine(output, "capture-provenance.json"), args);
        for (int round = 1; round <= rounds; round++)
            for (int k = 0; k < Candidates.Length; k++)
            {
                var (id, create) = Candidates[(k + round - 1) % Candidates.Length]; // rotate the order every round
                RunBlock(round, id, create, perRound, samples, blocks);
            }
        return 0;
    }

    private static void RunBlock(int round, string id, Func<ICaptureStrategy> create, int perRound,
        StreamWriter samples, StreamWriter blocks)
    {
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        int lost = 0, done = 0;
        string error = "";
        try
        {
            using var strategy = create();
            var probe = new TriggerProbe();
            strategy.Captured += probe.OnCaptured;
            strategy.Start();
            Thread.Sleep(200); // let the strategy's thread and registrations settle
            int seen = 0;
            for (int i = 0; i < Warmup; i++) if (probe.Fire().Count > 0) seen++;
            if (seen == 0) throw new InvalidOperationException("strategy observed none of the warm-up triggers");
            for (int i = 0; i < perRound; i++)
            {
                var (send, capture, count) = probe.Fire();
                if (count == 0 || capture == 0) lost++;
                samples.WriteLine($"{round},{id},{i},{send},{capture},{count},{Stopwatch.Frequency}");
                done++;
            }
        }
        catch (Exception ex) { error = ex.Message.Replace(',', ';').Replace('\r', ' ').Replace('\n', ' '); }
        double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        blocks.WriteLine($"{round},{id},{done},{lost},{wall.ElapsedMilliseconds},{cpuMs:F0},{error}");
        samples.Flush(); blocks.Flush();
        Console.WriteLine($"round {round} {id}: n={done} lost={lost} {error}");
    }
}
