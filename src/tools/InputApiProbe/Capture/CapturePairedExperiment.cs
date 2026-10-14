using System.Diagnostics;
using System.IO;
using System.Text;

namespace InputApiProbe;

/// <summary>All capture strategies armed at once; one physical key press reaches each of them. Records raw capture ticks only.</summary>
internal static class CapturePairedExperiment
{
    public static ushort ParseKey(string name)
    {
        if (name.Length >= 2 && name[0] is 'F' or 'f' && int.TryParse(name.AsSpan(1), out int n) && n is >= 1 and <= 24)
            return (ushort)(0x70 + n - 1);
        throw new ArgumentException($"Unsupported key '{name}', use F1..F24.");
    }

    public static int Run(int samples, ushort vk, string output, string[] args)
    {
        Directory.CreateDirectory(output);
        var candidates = new ICaptureStrategy[]
        {
            new LowLevelHookCapture(vk), new RawInputCapture(vk), new RegisterHotKeyCapture(vk),
            new PollingCapture(vk, 0), new PollingCapture(vk, 1)
        };
        var ticks = new Dictionary<string, List<long>>();
        var armed = new List<ICaptureStrategy>();
        foreach (var strategy in candidates)
        {
            try
            {
                var list = new List<long>();
                strategy.Captured += tick => { lock (list) list.Add(tick); };
                strategy.Start();
                ticks[strategy.Id] = list;
                armed.Add(strategy);
            }
            catch (Exception ex) { Console.Error.WriteLine($"{strategy.Id}: not armed ({ex.Message})"); strategy.Dispose(); }
        }
        try
        {
            Thread.Sleep(300);
            Console.WriteLine($"PHYSICAL_READY: press and release the key {samples} times, about 0.5s apart. Do not touch anything else.");
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < 300)
            {
                int first = 0;
                if (ticks.TryGetValue("ll-hook", out var llList)) lock (llList) first = llList.Count;
                if (first >= samples) break;
                Thread.Sleep(50);
            }
            Thread.Sleep(500);
        }
        finally { foreach (var strategy in armed) strategy.Dispose(); }
        using var file = new StreamWriter(Path.Combine(output, "capture-paired.csv"), false, new UTF8Encoding(false));
        file.WriteLine("sample,strategy,capture_qpc,frequency");
        foreach (var (id, list) in ticks)
            lock (list) for (int i = 0; i < list.Count; i++) file.WriteLine($"{i},{id},{list[i]},{Stopwatch.Frequency}");
        Provenance.Write(Path.Combine(output, "capture-paired-provenance.json"), args);
        foreach (var (id, list) in ticks) Console.WriteLine($"{id}: {list.Count} captures");
        return 0;
    }
}
