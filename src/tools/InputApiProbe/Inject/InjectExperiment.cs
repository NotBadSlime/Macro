using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace InputApiProbe;

internal static class InjectExperiment
{
    private const int Warmup = 20;

    private static readonly (string Id, InjectVariant Variant)[] Variants =
    {
        ("vk", InjectVariant.Vk), ("scan", InjectVariant.Scan), ("vk-scan", InjectVariant.VkScan),
        ("keybd-event", InjectVariant.KeybdEvent), ("merged-pair", InjectVariant.MergedPair),
    };

    public static int Run(int rounds, int perBlock, string output, string[] args)
    {
        Native.Warmup(requireScan: false);
        Directory.CreateDirectory(output);
        // Native.ScanF24 == 0 means this layout has no scan code for F24; the scan variants cannot be tested.
        var variants = Variants.Where(v => Native.ScanF24 != 0 || v.Variant is InjectVariant.Vk or InjectVariant.MergedPair).ToArray();
        var conditions = variants.SelectMany(v => new[] { (v.Id, v.Variant, Hook: false), (v.Id, v.Variant, Hook: true) }).ToArray();
        long totalRows = (long)rounds * conditions.Length * perBlock;
        string name = "Local\\MacroHID-ApiProbe-" + Guid.NewGuid().ToString("N");
        using var memory = MemoryMappedFile.CreateNew(name, ReceiverProcess.Header + totalRows * ReceiverProcess.Stride);
        using var view = memory.CreateViewAccessor();
        view.Write(ReceiverProcess.CurrentRow, -1);

        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        info.ArgumentList.Add("--receiver"); info.ArgumentList.Add(name);
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        using var receiver = Process.Start(info) ?? throw new InvalidOperationException("Cannot start receiver");
        using var csv = new StreamWriter(Path.Combine(output, "inject-samples.csv"), false, new UTF8Encoding(false));
        csv.WriteLine("round,variant,hook,sample,send_down_qpc,send_up_qpc,recv_down_qpc,recv_up_qpc,down_count,up_count,vk,frequency");
        Provenance.Write(Path.Combine(output, "inject-provenance.json"), args);
        try
        {
            var start = Stopwatch.StartNew();
            while (view.ReadInt32(ReceiverProcess.Ready) == 0 && start.Elapsed.TotalSeconds < 30) Thread.Sleep(20);
            IntPtr target = new(view.ReadInt64(ReceiverProcess.WindowHandle));
            if (target == IntPtr.Zero) throw new InvalidOperationException("Receiver did not start");
            Console.WriteLine("WINDOW_READY: click the receiver window to bring it to the foreground, then do not touch anything.");
            while (Native.GetForegroundWindow() != target && start.Elapsed.TotalSeconds < 300)
            { if (receiver.HasExited) throw new InvalidOperationException("Receiver closed"); Thread.Sleep(50); }
            if (Native.GetForegroundWindow() != target) throw new TimeoutException("Receiver never became the foreground window");

            int row = 0;
            for (int round = 1; round <= rounds; round++)
                for (int k = 0; k < conditions.Length; k++)
                {
                    var (id, variant, hook) = conditions[(k + round - 1) % conditions.Length];
                    using var hookScope = hook ? new EmptyHook() : null;
                    Thread.Sleep(100);
                    for (int i = 0; i < Warmup; i++) Sample(view, target, variant, hook ? "ll-empty" : "none", -1);
                    for (int i = 0; i < perBlock; i++)
                    {
                        var s = Sample(view, target, variant, hook ? "ll-empty" : "none", row);
                        long at = ReceiverProcess.RowOffset(row);
                        csv.WriteLine($"{round},{id},{(hook ? "ll-empty" : "none")},{i},{s.SendDown},{s.SendUp}," +
                            $"{view.ReadInt64(at + ReceiverProcess.DownQpc)},{view.ReadInt64(at + ReceiverProcess.UpQpc)}," +
                            $"{view.ReadInt32(at + ReceiverProcess.DownCount)},{view.ReadInt32(at + ReceiverProcess.UpCount)}," +
                            $"{view.ReadInt32(at + ReceiverProcess.DownVk)},{Stopwatch.Frequency}");
                        row++;
                    }
                    csv.Flush();
                    Console.WriteLine($"round {round} {id} hook={(hook ? "ll-empty" : "none")}: done");
                }
            return 0;
        }
        finally
        {
            view.Write(ReceiverProcess.Done, 1);
            if (!receiver.WaitForExit(5000)) receiver.Kill();
        }
    }

    /// <summary>Sends F24 down, waits for the receiver, sends F24 up, waits for the receiver. row -1 = warm-up (receiver ignores it).</summary>
    private static (long SendDown, long SendUp) Sample(MemoryMappedViewAccessor view, IntPtr target, InjectVariant variant, string hookLabel, int row)
    {
        if (Native.GetForegroundWindow() != target) throw new InvalidOperationException("Receiver lost foreground focus; stopped before further injection.");
        long at = row >= 0 ? ReceiverProcess.RowOffset(row) : 0;
        view.Write(ReceiverProcess.CurrentRow, row);
        long sendDown = Stopwatch.GetTimestamp();
        Native.KeyDown(variant);
        bool downSeen = true, upSeen = true;
        if (row >= 0) downSeen = WaitFor(view, at + ReceiverProcess.DownCount); else Thread.Sleep(5);
        long sendUp = variant == InjectVariant.MergedPair ? sendDown : Stopwatch.GetTimestamp();
        Native.KeyUp(variant);
        if (row >= 0) upSeen = WaitFor(view, at + ReceiverProcess.UpCount); else Thread.Sleep(5);
        Thread.Sleep(10);
        view.Write(ReceiverProcess.CurrentRow, -1);
        if (!downSeen || !upSeen)
            throw new InvalidOperationException($"missing receipt: variant={variant} hook={hookLabel} row={row} ({(downSeen ? "up" : "down")} not received within 100 ms)");
        return (sendDown, sendUp);
    }

    private static bool WaitFor(MemoryMappedViewAccessor view, long countAt)
    {
        var wait = Stopwatch.StartNew();
        while (view.ReadInt32(countAt) == 0 && wait.ElapsedMilliseconds < 100) Thread.Yield();
        return view.ReadInt32(countAt) != 0;
    }
}
