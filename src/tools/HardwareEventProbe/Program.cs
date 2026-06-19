using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace HardwareEventProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!HardwareEventProbeOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 1;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var form = new ProbeForm(options);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            form.RequestStop("Canceled from console.");
        };

        Console.WriteLine("HardwareEventProbe is listening.");
        Console.WriteLine("Press the bound mouse side button, then let Razer/MacroHID emit the filtered macro keys.");
        Console.WriteLine($"Expected macro events: {options.ExpectedEvents}, interval: {options.IntervalUs:0.###}us, loop steps: {options.LoopSteps}");
        Console.WriteLine($"Analysis skip events: {options.AnalysisSkipEvents}");
        Console.WriteLine($"Filtered virtual keys: {(options.FilterVirtualKeys.Count == 0 ? "all keyboard events" : string.Join(",", options.FilterVirtualKeys.Select(item => item.ToString(CultureInfo.InvariantCulture))))}");
        Console.WriteLine("Ctrl+C stops the probe.");

        Application.Run(form);
        return form.ExitCode;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        HardwareEventProbe - Raw Input timing probe for hardware/driver macro black-box tests.

        Usage:
          HardwareEventProbe --expected-events 50000 --loop-steps 10 --interval-us 1000 --filter-vk F13,F14 --csv artifacts\razer-events.csv
          HardwareEventProbe --expected-events 50000 --loop-steps 10 --interval-us 1000 --filter-vk E,Q --analysis-skip-events 10 --csv artifacts\razer-eq.csv

        Default double-click preset:
          If launched without arguments, the probe uses the Razer F13/F14 1ms preset:
          --expected-events 50000 --loop-steps 10 --interval-us 1000 --filter-vk F13,F14 --analysis-skip-events 10

        Options:
          --expected-events <n>   Number of macro output key events to capture before stopping. Default: 50000
          --loop-steps <n>        Number of macro events in one logical loop. Default: 10
          --interval-us <value>   Expected interval between macro events in microseconds. Default: 1000
          --analysis-skip-events <n>
                                Keep raw capture/CSV intact, but exclude the first n macro events from cadence metrics. Default: 0
          --filter-vk <list>      Comma-separated virtual keys, such as F13,F14,E,Q,124,125. Default: all keyboard events
          --timeout-sec <value>   Stop after this many seconds if expected events are not reached. Default: 60
          --csv <path>            Optional CSV path for raw event trace
          --exit-after-report      Print/write the report and close immediately, useful for scripted runs
          --help                  Show this help

        Startup latency:
          The probe listens for RI_MOUSE_BUTTON_4_DOWN and RI_MOUSE_BUTTON_5_DOWN as trigger events.
          If Razer swallows the physical side button, StartupLatency is reported as unavailable and the first macro key is used as t0.

        Razer E/Q sample:
          The 1ms E/Q precision sample emits down and up transitions, so use --expected-events 50000 --loop-steps 10 --interval-us 1000 --filter-vk E,Q.
          Use --analysis-skip-events 10 if the first loop includes application focus/startup warmup noise.
        """);
    }
}

internal sealed class ProbeForm : Form
{
    private const int WM_INPUT = 0x00FF;
    private const int RIM_TYPEMOUSE = 0;
    private const int RIM_TYPEKEYBOARD = 1;
    private const int RID_INPUT = 0x10000003;
    private const int RIDEV_INPUTSINK = 0x00000100;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
    private const ushort RI_KEY_BREAK = 0x0001;
    private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
    private const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
    private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
    private const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;
    private const ushort RI_MOUSE_BUTTON_4_DOWN = 0x0040;
    private const ushort RI_MOUSE_BUTTON_4_UP = 0x0080;
    private const ushort RI_MOUSE_BUTTON_5_DOWN = 0x0100;
    private const ushort RI_MOUSE_BUTTON_5_UP = 0x0200;

    private readonly HardwareEventProbeOptions options;
    private readonly System.Windows.Forms.Timer timeoutTimer;
    private readonly List<ProbeEvent> allEvents = [];
    private readonly List<ProbeEvent> macroEvents = [];
    private readonly Label statusLabel;
    private readonly TextBox ReportTextBox;
    private readonly Button closeButton;
    private long firstTriggerTick;
    private long firstMacroTick;
    private long frequency;
    private bool completed;
    private bool captureTimeoutStarted;

    public ProbeForm(HardwareEventProbeOptions options)
    {
        this.options = options;
        Text = "HardwareEventProbe";
        Width = 820;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        MaximizeBox = true;
        MinimizeBox = true;
        TopMost = true;

        statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 72,
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            Text = "Listening for Raw Input..."
        };
        Controls.Add(statusLabel);

        ReportTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new System.Drawing.Font("Consolas", 10),
            Visible = false
        };
        Controls.Add(ReportTextBox);
        ReportTextBox.BringToFront();

        closeButton = new Button
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Text = "Close",
            Visible = false
        };
        closeButton.Click += (_, _) => Close();
        Controls.Add(closeButton);

        timeoutTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(1, (int)Math.Min(int.MaxValue, options.Timeout.TotalMilliseconds))
        };
        timeoutTimer.Tick += (_, _) => Finish("Timeout reached.", options.ExpectedEvents == 0 || macroEvents.Count >= options.ExpectedEvents ? 0 : 2);
    }

    public int ExitCode { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        QueryPerformanceFrequency(out frequency);
        RegisterRawInput();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WM_INPUT)
        {
            ProcessRawInput(message.LParam);
        }

        base.WndProc(ref message);
    }

    public void RequestStop(string reason)
    {
        if (IsHandleCreated)
        {
            BeginInvoke(() => Finish(reason, 0));
        }
    }

    private void RegisterRawInput()
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_MOUSE,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = Handle
            },
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = Handle
            }
        };

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            throw new InvalidOperationException($"RegisterRawInputDevices failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private void ProcessRawInput(IntPtr rawInputHandle)
    {
        if (completed)
        {
            return;
        }

        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(rawInputHandle, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawInputHandle, RID_INPUT, buffer, ref size, headerSize) != size)
            {
                return;
            }

            var input = Marshal.PtrToStructure<RAWINPUT>(buffer);
            QueryPerformanceCounter(out var tick);

            if (input.header.dwType == RIM_TYPEKEYBOARD)
            {
                RecordKeyboard(input.data.keyboard, tick);
            }
            else if (input.header.dwType == RIM_TYPEMOUSE)
            {
                RecordMouse(input.data.mouse, tick);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void RecordKeyboard(RAWKEYBOARD keyboard, long tick)
    {
        if (keyboard.VKey == 0)
        {
            return;
        }

        if (options.FilterVirtualKeys.Count > 0 && !options.FilterVirtualKeys.Contains(keyboard.VKey))
        {
            return;
        }

        var state = (keyboard.Flags & RI_KEY_BREAK) == 0 ? "down" : "up";
        var item = new ProbeEvent(allEvents.Count, "key", keyboard.VKey, state, tick, false);
        allEvents.Add(item);
        macroEvents.Add(item);

        firstMacroTick = firstMacroTick == 0 ? tick : firstMacroTick;
        StartCaptureTimeout();
        statusLabel.Text = $"Macro events: {macroEvents.Count}/{options.ExpectedEvents}";

        if (options.ExpectedEvents > 0 && macroEvents.Count >= options.ExpectedEvents)
        {
            Finish("Expected macro event count reached.", 0);
        }
    }

    private void RecordMouse(RAWMOUSE mouse, long tick)
    {
        var flags = (ushort)(mouse.ulButtons & 0xFFFF);
        RecordMouseButton(flags, RI_MOUSE_LEFT_BUTTON_DOWN, RI_MOUSE_LEFT_BUTTON_UP, 1, tick);
        RecordMouseButton(flags, RI_MOUSE_RIGHT_BUTTON_DOWN, RI_MOUSE_RIGHT_BUTTON_UP, 2, tick);
        RecordMouseButton(flags, RI_MOUSE_MIDDLE_BUTTON_DOWN, RI_MOUSE_MIDDLE_BUTTON_UP, 3, tick);
        RecordMouseButton(flags, RI_MOUSE_BUTTON_4_DOWN, RI_MOUSE_BUTTON_4_UP, 4, tick);
        RecordMouseButton(flags, RI_MOUSE_BUTTON_5_DOWN, RI_MOUSE_BUTTON_5_UP, 5, tick);
    }

    private void RecordMouseButton(ushort flags, ushort downFlag, ushort upFlag, ushort code, long tick)
    {
        if ((flags & downFlag) != 0)
        {
            var isTrigger = downFlag is RI_MOUSE_BUTTON_4_DOWN or RI_MOUSE_BUTTON_5_DOWN;
            if (isTrigger && firstTriggerTick == 0)
            {
                firstTriggerTick = tick;
                StartCaptureTimeout();
            }

            allEvents.Add(new ProbeEvent(allEvents.Count, "mouse", code, "down", tick, isTrigger));
        }

        if ((flags & upFlag) != 0)
        {
            allEvents.Add(new ProbeEvent(allEvents.Count, "mouse", code, "up", tick, upFlag is RI_MOUSE_BUTTON_4_UP or RI_MOUSE_BUTTON_5_UP));
        }
    }

    private void Finish(string reason, int exitCode)
    {
        if (completed)
        {
            return;
        }

        completed = true;
        ExitCode = exitCode;
        timeoutTimer.Stop();

        var report = ProbeReport.Create(options, allEvents, macroEvents, firstTriggerTick, firstMacroTick, frequency, reason);
        var reportText = report.ToText();
        Console.WriteLine();
        Console.WriteLine(reportText);

        if (!string.IsNullOrWhiteSpace(options.CsvPath))
        {
            report.WriteCsv(options.CsvPath);
            Console.WriteLine($"CSV trace: {options.CsvPath}");
            reportText += Environment.NewLine + $"CSV trace: {options.CsvPath}";
        }

        if (options.ExitAfterReport)
        {
            Close();
            return;
        }

        ShowReport(reportText);
    }

    private void StartCaptureTimeout()
    {
        if (captureTimeoutStarted)
        {
            return;
        }

        captureTimeoutStarted = true;
        timeoutTimer.Start();
    }

    private void ShowReport(string reportText)
    {
        Text = "HardwareEventProbe - Report";
        statusLabel.Text = "Probe finished. Report stays open here so it is not lost when launched by double-click.";
        ReportTextBox.Text = reportText;
        ReportTextBox.Visible = true;
        closeButton.Visible = true;
        ReportTextBox.Focus();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceFrequency(out long lpFrequency);
}

internal sealed record HardwareEventProbeOptions(
    bool ShowHelp,
    int ExpectedEvents,
    int LoopSteps,
    double IntervalUs,
    TimeSpan Timeout,
    int AnalysisSkipEvents,
    HashSet<ushort> FilterVirtualKeys,
    string? CsvPath,
    bool ExitAfterReport)
{
    private const bool UseRazerF13F14DefaultWhenNoArguments = true;

    public static bool TryParse(string[] args, out HardwareEventProbeOptions options, out string error)
    {
        if (args.Length == 0 && UseRazerF13F14DefaultWhenNoArguments)
        {
            options = RazerF13F14Default();
            error = string.Empty;
            return true;
        }

        var expectedEvents = 50_000;
        var loopSteps = 10;
        var intervalUs = 1_000.0;
        var timeout = TimeSpan.FromSeconds(60);
        var analysisSkipEvents = 0;
        var filterVirtualKeys = new HashSet<ushort>();
        string? csvPath = null;
        var showHelp = false;
        var exitAfterReport = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "--help" or "-h" or "/?")
            {
                showHelp = true;
                continue;
            }

            string NextValue()
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {arg}.");
                }

                return args[++index];
            }

            try
            {
                switch (arg)
                {
                    case "--expected-events":
                        expectedEvents = int.Parse(NextValue(), CultureInfo.InvariantCulture);
                        break;
                    case "--loop-steps":
                        loopSteps = int.Parse(NextValue(), CultureInfo.InvariantCulture);
                        break;
                    case "--interval-us":
                        intervalUs = double.Parse(NextValue(), CultureInfo.InvariantCulture);
                        break;
                    case "--analysis-skip-events":
                        analysisSkipEvents = int.Parse(NextValue(), CultureInfo.InvariantCulture);
                        break;
                    case "--timeout-sec":
                        timeout = TimeSpan.FromSeconds(double.Parse(NextValue(), CultureInfo.InvariantCulture));
                        break;
                    case "--filter-vk":
                        filterVirtualKeys = ParseVirtualKeys(NextValue());
                        break;
                    case "--csv":
                        csvPath = NextValue();
                        break;
                    case "--exit-after-report":
                        exitAfterReport = true;
                        break;
                    default:
                        error = $"Unknown argument: {arg}";
                        options = Default;
                        return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                error = ex.Message;
                options = Default;
                return false;
            }
        }

        if (expectedEvents < 0 || analysisSkipEvents < 0)
        {
            error = "--expected-events and --analysis-skip-events must be >= 0.";
            options = Default;
            return false;
        }

        if (loopSteps <= 0 || intervalUs <= 0 || timeout <= TimeSpan.Zero)
        {
            error = "--loop-steps, --interval-us, and --timeout-sec must be positive.";
            options = Default;
            return false;
        }

        options = new HardwareEventProbeOptions(showHelp, expectedEvents, loopSteps, intervalUs, timeout, analysisSkipEvents, filterVirtualKeys, csvPath, exitAfterReport);
        error = string.Empty;
        return true;
    }

    private static HardwareEventProbeOptions Default => new(false, 50_000, 10, 1_000, TimeSpan.FromSeconds(60), 0, [], null, false);

    private static HardwareEventProbeOptions RazerF13F14Default()
    {
        const int analysisSkipEvents = 10;
        var csvPath = Path.Combine(Environment.CurrentDirectory, "razer-f13f14-corrected.csv");
        return new HardwareEventProbeOptions(
            ShowHelp: false,
            ExpectedEvents: 50_000,
            LoopSteps: 10,
            IntervalUs: 1_000,
            Timeout: TimeSpan.FromSeconds(300),
            AnalysisSkipEvents: analysisSkipEvents,
            FilterVirtualKeys: [0x7C, 0x7D],
            CsvPath: csvPath,
            ExitAfterReport: false);
    }

    private static HashSet<ushort> ParseVirtualKeys(string value)
    {
        var result = new HashSet<ushort>();
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(ParseVirtualKey(token));
        }

        return result;
    }

    private static ushort ParseVirtualKey(string value)
    {
        if (ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric;
        }

        var upper = value.Trim().ToUpperInvariant();
        if (upper.Length == 1 && char.IsLetterOrDigit(upper[0]))
        {
            return upper[0];
        }

        if (upper.StartsWith("F", StringComparison.Ordinal) && int.TryParse(upper[1..], CultureInfo.InvariantCulture, out var functionKey) && functionKey is >= 1 and <= 24)
        {
            return (ushort)(0x70 + functionKey - 1);
        }

        return upper switch
        {
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            _ => throw new FormatException($"Unsupported virtual key token: {value}")
        };
    }
}

internal sealed record ProbeEvent(int Index, string Kind, int Code, string State, long Tick, bool IsTrigger);

internal sealed class ProbeReport
{
    private readonly HardwareEventProbeOptions options;
    private readonly IReadOnlyList<ProbeEvent> allEvents;
    private readonly IReadOnlyList<ProbeEvent> macroEvents;
    private readonly long firstTriggerTick;
    private readonly long firstMacroTick;
    private readonly long frequency;
    private readonly string reason;

    private ProbeReport(
        HardwareEventProbeOptions options,
        IReadOnlyList<ProbeEvent> allEvents,
        IReadOnlyList<ProbeEvent> macroEvents,
        long firstTriggerTick,
        long firstMacroTick,
        long frequency,
        string reason)
    {
        this.options = options;
        this.allEvents = allEvents;
        this.macroEvents = macroEvents;
        this.firstTriggerTick = firstTriggerTick;
        this.firstMacroTick = firstMacroTick;
        this.frequency = frequency;
        this.reason = reason;
    }

    public static ProbeReport Create(
        HardwareEventProbeOptions options,
        IReadOnlyList<ProbeEvent> allEvents,
        IReadOnlyList<ProbeEvent> macroEvents,
        long firstTriggerTick,
        long firstMacroTick,
        long frequency,
        string reason)
    {
        return new ProbeReport(options, allEvents, macroEvents, firstTriggerTick, firstMacroTick, frequency, reason);
    }

    public string ToText()
    {
        var builder = new StringBuilder();
        var startupLatency = firstTriggerTick > 0 && firstMacroTick > 0
            ? ToMicroseconds(firstMacroTick - firstTriggerTick).ToString("0.###", CultureInfo.InvariantCulture) + "us"
            : "unavailable";
        var droppedOrExtraEvents = macroEvents.Count - options.ExpectedEvents;
        var hasExpectedCount = options.ExpectedEvents > 0;
        var captureComplete = !hasExpectedCount || macroEvents.Count >= options.ExpectedEvents;
        var captureCompleteness = hasExpectedCount
            ? macroEvents.Count * 100.0 / options.ExpectedEvents
            : 100.0;
        var analysisSkipEvents = Math.Min(options.AnalysisSkipEvents, macroEvents.Count);
        var metricEvents = macroEvents.Skip(analysisSkipEvents).ToArray();

        builder.AppendLine($"Reason: {reason}");
        builder.AppendLine($"capturedEvents={allEvents.Count} macroEvents={macroEvents.Count} expectedEvents={options.ExpectedEvents} droppedOrExtraEvents={droppedOrExtraEvents}");
        builder.AppendLine($"captureComplete={captureComplete.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()} captureCompleteness={captureCompleteness:0.###}% completeLoops={macroEvents.Count / options.LoopSteps} partialLoopEvents={macroEvents.Count % options.LoopSteps}");
        builder.AppendLine($"analysisSkipEvents={analysisSkipEvents} metricEvents={metricEvents.Length} metricCompleteLoops={metricEvents.Length / options.LoopSteps} metricPartialLoopEvents={metricEvents.Length % options.LoopSteps}");
        builder.AppendLine($"StartupLatency={startupLatency}");

        if (metricEvents.Length < 2)
        {
            builder.AppendLine("Not enough macro events for cadence metrics.");
            return builder.ToString();
        }

        var actualIntervals = new List<double>(metricEvents.Length - 1);
        var intervalErrors = new List<double>(metricEvents.Length - 1);
        var cumulativeDrifts = captureComplete && hasExpectedCount ? new List<double>(metricEvents.Length) : [];
        var loopLocalDrifts = new List<double>((metricEvents.Length / options.LoopSteps) * options.LoopSteps);
        var loopEndLocalDrifts = new List<double>(Math.Max(0, metricEvents.Length / options.LoopSteps));
        var intervalTicks = options.IntervalUs * frequency / 1_000_000.0;
        var firstTick = metricEvents[0].Tick;

        for (var index = 0; index < metricEvents.Length; index++)
        {
            var expectedTick = firstTick + (long)Math.Round(intervalTicks * index);
            if (captureComplete && hasExpectedCount)
            {
                cumulativeDrifts.Add(Math.Abs(ToMicroseconds(metricEvents[index].Tick - expectedTick)));
            }

            if (index > 0)
            {
                var actualInterval = metricEvents[index].Tick - metricEvents[index - 1].Tick;
                var actualIntervalUs = ToMicroseconds(actualInterval);
                actualIntervals.Add(actualIntervalUs);
                intervalErrors.Add(Math.Abs(actualIntervalUs - options.IntervalUs));
            }
        }

        var completeLoops = metricEvents.Length / options.LoopSteps;
        for (var loopIndex = 0; loopIndex < completeLoops; loopIndex++)
        {
            var loopStartIndex = loopIndex * options.LoopSteps;
            var loopStartTick = metricEvents[loopStartIndex].Tick;
            for (var offset = 0; offset < options.LoopSteps; offset++)
            {
                var eventIndex = loopStartIndex + offset;
                var expectedTick = loopStartTick + (long)Math.Round(intervalTicks * offset);
                var driftUs = Math.Abs(ToMicroseconds(metricEvents[eventIndex].Tick - expectedTick));
                loopLocalDrifts.Add(driftUs);
                if (offset == options.LoopSteps - 1)
                {
                    loopEndLocalDrifts.Add(driftUs);
                }
            }
        }

        var over2msCount = actualIntervals.Count(item => item > 2_000);
        var over5msCount = actualIntervals.Count(item => item > 5_000);
        builder.AppendLine($"actualIntervalUs {FormatRangeStats(actualIntervals, "actualIntervalMin", "actualIntervalMax", "actualIntervalRange")}");
        builder.AppendLine($"intervalErrorUs {FormatRangeStats(intervalErrors, "intervalErrorMin", "intervalErrorMax", "intervalErrorRange")}");
        builder.AppendLine($"intervalThresholds over2msCount={over2msCount} over5msCount={over5msCount}");

        if (captureComplete && hasExpectedCount)
        {
            builder.AppendLine($"cumulativeDriftAbs {FormatStats(cumulativeDrifts)}");
        }
        else
        {
            builder.AppendLine("cumulativeDriftAbs skipped(incomplete capture; cumulative drift would include missing events or timeout)");
        }

        builder.AppendLine($"intervalErrorAbs {FormatStats(intervalErrors)}");
        builder.AppendLine($"loopLocalDriftAbs {FormatStats(loopLocalDrifts)}");
        builder.AppendLine($"loopEndLocalDriftAbs {FormatStats(loopEndLocalDrifts)}");
        return builder.ToString();
    }

    public void WriteCsv(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("index,kind,code,state,isTrigger,tick,relativeUs,loopIndex,loopOffset,localDueUs,localDriftUs,cumulativeDueUs,cumulativeDriftUs");
        var firstTick = firstMacroTick == 0 ? allEvents.FirstOrDefault()?.Tick ?? 0 : firstMacroTick;
        var macroIndex = 0;
        var intervalTicks = options.IntervalUs * frequency / 1_000_000.0;

        foreach (var item in allEvents)
        {
            var relativeUs = ToMicroseconds(item.Tick - firstTick);
            var loopIndexText = string.Empty;
            var loopOffsetText = string.Empty;
            var localDueUs = string.Empty;
            var localDriftUs = string.Empty;
            var cumulativeDueUs = string.Empty;
            var cumulativeDriftUs = string.Empty;

            if (macroIndex < macroEvents.Count && ReferenceEquals(item, macroEvents[macroIndex]))
            {
                var loopIndex = macroIndex / options.LoopSteps;
                var loopOffset = macroIndex % options.LoopSteps;
                var loopStartTick = macroEvents[loopIndex * options.LoopSteps].Tick;
                var localDueTick = loopStartTick + (long)Math.Round(intervalTicks * loopOffset);
                var cumulativeDueTick = firstTick + (long)Math.Round(intervalTicks * macroIndex);

                loopIndexText = loopIndex.ToString(CultureInfo.InvariantCulture);
                loopOffsetText = loopOffset.ToString(CultureInfo.InvariantCulture);
                localDueUs = (options.IntervalUs * loopOffset).ToString("0.###", CultureInfo.InvariantCulture);
                localDriftUs = ToMicroseconds(item.Tick - localDueTick).ToString("0.###", CultureInfo.InvariantCulture);
                cumulativeDueUs = (options.IntervalUs * macroIndex).ToString("0.###", CultureInfo.InvariantCulture);
                cumulativeDriftUs = ToMicroseconds(item.Tick - cumulativeDueTick).ToString("0.###", CultureInfo.InvariantCulture);
                macroIndex++;
            }

            writer.WriteLine(string.Join(',',
                item.Index.ToString(CultureInfo.InvariantCulture),
                item.Kind,
                item.Code.ToString(CultureInfo.InvariantCulture),
                item.State,
                item.IsTrigger ? "true" : "false",
                item.Tick.ToString(CultureInfo.InvariantCulture),
                relativeUs.ToString("0.###", CultureInfo.InvariantCulture),
                loopIndexText,
                loopOffsetText,
                localDueUs,
                localDriftUs,
                cumulativeDueUs,
                cumulativeDriftUs));
        }
    }

    private static string FormatStats(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return "count=0";
        }

        var sorted = values.OrderBy(item => item).ToArray();
        return string.Create(CultureInfo.InvariantCulture,
            $"count={sorted.Length} p50={Percentile(sorted, 0.50):0.###}us p95={Percentile(sorted, 0.95):0.###}us p99={Percentile(sorted, 0.99):0.###}us p99.9={Percentile(sorted, 0.999):0.###}us max={sorted[^1]:0.###}us");
    }

    private static string FormatRangeStats(IReadOnlyList<double> values, string minName, string maxName, string rangeName)
    {
        if (values.Count == 0)
        {
            return $"count=0 {minName}=unavailable {maxName}=unavailable {rangeName}=unavailable";
        }

        var sorted = values.OrderBy(item => item).ToArray();
        var min = sorted[0];
        var max = sorted[^1];
        return string.Create(CultureInfo.InvariantCulture,
            $"count={sorted.Length} {minName}={min:0.###}us {maxName}={max:0.###}us {rangeName}={max - min:0.###}us p50={Percentile(sorted, 0.50):0.###}us p95={Percentile(sorted, 0.95):0.###}us p99={Percentile(sorted, 0.99):0.###}us p99.9={Percentile(sorted, 0.999):0.###}us");
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private double ToMicroseconds(double ticks)
    {
        return ticks * 1_000_000.0 / frequency;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWINPUTDEVICE
{
    public ushort usUsagePage;
    public ushort usUsage;
    public int dwFlags;
    public IntPtr hwndTarget;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWINPUTHEADER
{
    public int dwType;
    public int dwSize;
    public IntPtr hDevice;
    public IntPtr wParam;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWINPUT
{
    public RAWINPUTHEADER header;
    public RAWINPUTDATA data;
}

[StructLayout(LayoutKind.Explicit)]
internal struct RAWINPUTDATA
{
    [FieldOffset(0)]
    public RAWMOUSE mouse;

    [FieldOffset(0)]
    public RAWKEYBOARD keyboard;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWMOUSE
{
    public ushort usFlags;
    public uint ulButtons;
    public uint ulRawButtons;
    public int lLastX;
    public int lLastY;
    public uint ulExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWKEYBOARD
{
    public ushort MakeCode;
    public ushort Flags;
    public ushort Reserved;
    public ushort VKey;
    public uint Message;
    public uint ExtraInformation;
}
