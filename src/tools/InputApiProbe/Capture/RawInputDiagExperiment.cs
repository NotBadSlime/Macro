using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace InputApiProbe;

/// <summary>
/// Raw Input diagnostics for physical keys: registers the same keyboard device as <see cref="RawInputCapture"/>
/// but records every WM_INPUT without filtering, so "never delivered" and "delivered but filtered out" can be told apart.
/// Variables are single-switch: --window message|toplevel, --flags sink|none, --also &lt;strategies armed alongside&gt;.
/// </summary>
internal static class RawInputDiagExperiment
{
    private const uint WmInput = 0x00FF, RidInput = 0x10000003, RidevInputSink = 0x100;
    private const int HeaderSize = 24; // RAWINPUTHEADER on x64: dwType, dwSize, hDevice, wParam

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    private sealed record Row(int Seq, long Qpc, bool ReadOk, long Device, uint Type, ushort MakeCode, ushort VKey, ushort Flags, uint Message, uint Extra);

    public static int Run(int seconds, string windowKind, string flagsKind, string also, ushort vk, string output, string[] args)
    {
        Directory.CreateDirectory(output);
        if (windowKind is not ("message" or "toplevel")) throw new ArgumentException("--window must be message or toplevel");
        if (flagsKind is not ("sink" or "none")) throw new ArgumentException("--flags must be sink or none");

        var companions = new List<ICaptureStrategy>();
        var companionCounts = new Dictionary<string, int[]>();
        try
        {
            foreach (var id in also.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                ICaptureStrategy strategy = id switch
                {
                    "ll-hook" => new LowLevelHookCapture(vk),
                    "register-hotkey" => new RegisterHotKeyCapture(vk),
                    "poll-spin" => new PollingCapture(vk, 0),
                    "poll-1ms" => new PollingCapture(vk, 1),
                    _ => throw new ArgumentException($"--also: unknown strategy '{id}'")
                };
                var count = new int[1];
                companionCounts[strategy.Id] = count;
                strategy.Captured += _ => Interlocked.Increment(ref count[0]);
                companions.Add(strategy);
                strategy.Start();
            }
            return RunCore(seconds, windowKind, flagsKind, also, companionCounts, output, args);
        }
        finally { foreach (var strategy in companions) strategy.Dispose(); }
    }

    private static int RunCore(int seconds, string windowKind, string flagsKind, string also, Dictionary<string, int[]> companionCounts, string output, string[] args)
    {
        var rows = new List<Row>();
        var buffer = new byte[256];
        int seq = 0;

        bool OnMessage(uint message, IntPtr w, IntPtr l)
        {
            if (message != WmInput) return false;
            long tick = Stopwatch.GetTimestamp();
            uint size = (uint)buffer.Length;
            uint read = GetRawInputData(l, RidInput, buffer, ref size, (uint)HeaderSize);
            if (read == uint.MaxValue || read < HeaderSize)
            {
                lock (rows) rows.Add(new Row(seq++, tick, false, 0, 0, 0, 0, 0, 0, 0));
                return false;
            }
            uint type = BitConverter.ToUInt32(buffer, 0);
            long device = BitConverter.ToInt64(buffer, 8);
            ushort make = 0, vkey = 0, flags = 0; uint msg = 0, extra = 0;
            if (type == 1 && read >= HeaderSize + 16) // RAWKEYBOARD: MakeCode, Flags, Reserved, VKey, Message, ExtraInformation
            {
                make = BitConverter.ToUInt16(buffer, HeaderSize);
                flags = BitConverter.ToUInt16(buffer, HeaderSize + 2);
                vkey = BitConverter.ToUInt16(buffer, HeaderSize + 6);
                msg = BitConverter.ToUInt32(buffer, HeaderSize + 8);
                extra = BitConverter.ToUInt32(buffer, HeaderSize + 12);
            }
            lock (rows) rows.Add(new Row(seq++, tick, true, device, type, make, vkey, flags, msg, extra));
            return false;
        }

        using (var window = new MessageWindow(OnMessage, visible: false, "raw-diag", messageOnly: windowKind != "toplevel", onCreated: hwnd =>
        {
            var device = new RawInputDevice { UsagePage = 1, Usage = 6, Flags = flagsKind == "sink" ? RidevInputSink : 0, Target = hwnd };
            if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
                throw new InvalidOperationException($"RegisterRawInputDevices failed, error={Marshal.GetLastWin32Error()}");
        }))
        {
            Console.WriteLine($"RAW_DIAG_READY window={windowKind} flags={flagsKind} also=[{also}]: press keys for {seconds}s. Do not touch anything else.");
            Thread.Sleep(seconds * 1000);
            if (window.HandlerFailure is not null) throw new InvalidOperationException("handler failed", window.HandlerFailure);
        }

        using var file = new StreamWriter(Path.Combine(output, "raw-diag.csv"), false, new UTF8Encoding(false));
        file.WriteLine("seq,qpc,frequency,read_ok,device,type,make_code,vkey,flags,message,extra");
        lock (rows)
            foreach (var r in rows)
                file.WriteLine($"{r.Seq},{r.Qpc},{Stopwatch.Frequency},{(r.ReadOk ? 1 : 0)},{r.Device},{r.Type},{r.MakeCode},{r.VKey},{r.Flags},{r.Message},{r.Extra}");
        Provenance.Write(Path.Combine(output, "raw-diag-provenance.json"), args);
        Console.WriteLine($"raw-diag: {rows.Count} WM_INPUT records ({windowKind}, {flagsKind}, also=[{also}])");
        foreach (var (id, count) in companionCounts) Console.WriteLine($"companion {id}: {count[0]} captures");
        return 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, byte[] data, ref uint size, uint headerSize);
}
