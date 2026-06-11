using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio;
using System.Globalization;
using System.Text.Json;

var tests = new (string Name, Action Body)[]
{
    ("MCRX parser covers keyboard, mouse, wait, repeat, and pixel steps", McrxParserCoversBaselineSteps),
    ("MCRX parser covers wheel and consumer control steps", McrxParserCoversWheelAndConsumerSteps),
    ("MCRX parser covers playback hotkey settings", McrxParserCoversPlaybackHotkeySettings),
    ("MCRX parser defaults missing playback settings", McrxParserDefaultsMissingPlaybackSettings),
    ("MCRX parser rejects invalid playback settings", McrxParserRejectsInvalidPlaybackSettings),
    ("Sample baseline macro remains parseable", SampleBaselineMacroRemainsParseable),
    ("Scheduler expands repeats and applies waits with QPC ticks", SchedulerExpandsRepeatsAndAppliesWaits),
    ("Report compiler expands hold actions into timed HID reports", ReportCompilerExpandsHoldActions),
    ("Report compiler evaluates pixel branches before emitting reports", ReportCompilerEvaluatesPixelBranches),
    ("Protocol constants match driver IOCTL contract", ProtocolConstantsMatchDriverIoctlContract),
    ("HID report encoder covers keyboard, mouse, and consumer reports", HidReportEncoderCoversReports),
    ("Pixel conditions match expected colors within tolerance", PixelConditionsMatchWithinTolerance),
    ("Latency histogram computes p50 p95 p99 from microsecond samples", LatencyHistogramComputesPercentiles),
    ("Playback controller starts and stops toggle loop on trigger press", PlaybackControllerStopsToggleLoopOnTriggerPress),
    ("Playback controller runs fixed count once by default", PlaybackControllerRunsFixedCountOnceByDefault),
    ("Playback controller cancels hold loop when trigger is released", PlaybackControllerCancelsHoldLoopWhenTriggerIsReleased),
    ("Playback executor checks cancellation before submitting delayed reports", PlaybackExecutorChecksCancellationBeforeDelayedReports),
    ("Localization normalizes supported cultures", LocalizationNormalizesSupportedCultures),
    ("Localization resources cover playback label in three languages", LocalizationResourcesCoverPlaybackLabelInThreeLanguages),
    ("Runtime diagnostics report driver availability from stats", RuntimeDiagnosticsReportDriverAvailabilityFromStats),
    ("MacroConverter integration finds sibling packaged executable", MacroConverterIntegrationFindsSiblingPackagedExecutable),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}");
        Console.Error.WriteLine(ex);
    }
}

if (failed > 0)
{
    Console.Error.WriteLine($"{failed} test(s) failed.");
    return 1;
}

Console.WriteLine($"{tests.Length} test(s) passed.");
return 0;

static void McrxParserCoversBaselineSteps()
{
    const string json = """
    {
      "version": 1,
      "name": "baseline",
      "steps": [
        { "type": "key.tap", "key": "A", "modifiers": ["LeftCtrl"], "holdMs": 5 },
        { "type": "mouse.move", "mode": "relative", "x": 25, "y": -10, "durationMs": 0 },
        { "type": "wait", "ms": 12 },
        {
          "type": "repeat",
          "count": 2,
          "steps": [
            { "type": "mouse.click", "button": "X1" }
          ]
        },
        {
          "type": "pixel.when",
          "scope": "screen",
          "x": 100,
          "y": 200,
          "r": 10,
          "g": 20,
          "b": 30,
          "tolerance": 4,
          "then": [
            { "type": "key.down", "key": "Enter" }
          ]
        }
      ]
    }
    """;

    var document = McrxParser.Parse(json);

    Assert.Equal("baseline", document.Name);
    Assert.Equal(1, document.Version);
    Assert.Equal(5, document.Steps.Count);
    Assert.IsType<KeyStep>(document.Steps[0]);
    Assert.IsType<MouseMoveStep>(document.Steps[1]);
    Assert.IsType<WaitStep>(document.Steps[2]);
    Assert.IsType<RepeatStep>(document.Steps[3]);
    Assert.IsType<PixelWhenStep>(document.Steps[4]);

    var key = (KeyStep)document.Steps[0];
    Assert.Equal(HidKey.A, key.Key);
    Assert.Equal(HidModifier.LeftCtrl, key.Modifiers);
    Assert.Equal(TimeSpan.FromMilliseconds(5), key.Hold);

    var pixel = (PixelWhenStep)document.Steps[4];
    Assert.Equal(CoordinateScope.Screen, pixel.Condition.Coordinate.Scope);
    Assert.Equal(new RgbColor(10, 20, 30), pixel.Condition.Expected);
    Assert.Equal(4, pixel.Condition.Tolerance);
    Assert.IsType<KeyStep>(pixel.ThenSteps[0]);
}

static void SchedulerExpandsRepeatsAndAppliesWaits()
{
    var document = new MacroDocument(
        Version: 1,
        Name: "schedule",
        Steps:
        [
            new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero),
            new WaitStep(TimeSpan.FromMilliseconds(2)),
            new RepeatStep(2,
            [
                new KeyStep(KeyActionKind.Tap, HidKey.B, HidModifier.None, TimeSpan.FromMilliseconds(1)),
                new WaitStep(TimeSpan.FromMilliseconds(3))
            ])
        ]);

    var scheduled = MacroScheduler.Compile(document, startTick: 1_000, qpcFrequency: 1_000_000);

    Assert.Equal(3, scheduled.Count);
    Assert.Equal(1_000, scheduled[0].DueTick);
    Assert.Equal(3_000, scheduled[1].DueTick);
    Assert.Equal(6_000, scheduled[2].DueTick);
    Assert.Equal(HidKey.B, ((KeyStep)scheduled[2].Step).Key);
}

static void ReportCompilerExpandsHoldActions()
{
    var document = new MacroDocument(
        Version: 1,
        Name: "reports",
        Steps:
        [
            new KeyStep(KeyActionKind.Tap, HidKey.A, HidModifier.LeftCtrl, TimeSpan.FromMilliseconds(2)),
            new MouseButtonStep(MouseButton.Left, ButtonActionKind.Click, TimeSpan.FromMilliseconds(3)),
            new ConsumerStep(ConsumerControl.VolumeUp, ButtonActionKind.Click, TimeSpan.FromMilliseconds(4))
        ]);

    var reports = MacroReportCompiler.Compile(document, startTick: 10_000, qpcFrequency: 1_000_000);

    Assert.Equal(6, reports.Count);
    Assert.Equal(10_000, reports[0].DueTick);
    Assert.SequenceEqual([0x01, 0x01, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00], reports[0].Report);
    Assert.Equal(12_000, reports[1].DueTick);
    Assert.SequenceEqual([0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], reports[1].Report);
    Assert.Equal(12_000, reports[2].DueTick);
    Assert.SequenceEqual([0x02, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], reports[2].Report);
    Assert.Equal(15_000, reports[3].DueTick);
    Assert.SequenceEqual([0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], reports[3].Report);
    Assert.Equal(15_000, reports[4].DueTick);
    Assert.SequenceEqual([0x03, 0xE9, 0x00], reports[4].Report);
    Assert.Equal(19_000, reports[5].DueTick);
    Assert.SequenceEqual([0x03, 0x00, 0x00], reports[5].Report);
}

static void ReportCompilerEvaluatesPixelBranches()
{
    var condition = new PixelCondition(
        new PixelCoordinate(CoordinateScope.Screen, x: 5, y: 6),
        new RgbColor(1, 2, 3),
        tolerance: 0);
    var document = new MacroDocument(
        Version: 1,
        Name: "pixel",
        Steps:
        [
            new PixelWhenStep(condition,
            [
                new KeyStep(KeyActionKind.Down, HidKey.Enter, HidModifier.None, TimeSpan.Zero)
            ])
        ]);

    var falseReports = MacroReportCompiler.Compile(document, 0, 1_000_000, _ => false);
    var trueReports = MacroReportCompiler.Compile(document, 0, 1_000_000, _ => true);

    Assert.Equal(0, falseReports.Count);
    Assert.Equal(1, trueReports.Count);
    Assert.SequenceEqual([0x01, 0x00, 0x00, 0x28, 0x00, 0x00, 0x00, 0x00, 0x00], trueReports[0].Report);
}

static void ProtocolConstantsMatchDriverIoctlContract()
{
    Assert.Equal(0x80002004u, MacroHidProtocol.IoctlPing);
    Assert.Equal(0x8000A008u, MacroHidProtocol.IoctlSubmitReport);
    Assert.Equal(0x8000600Cu, MacroHidProtocol.IoctlGetStats);
}

static void SampleBaselineMacroRemainsParseable()
{
    var json = File.ReadAllText(Path.Combine("samples", "baseline.mcrx"));
    var document = McrxParser.Parse(json);
    var scheduled = MacroScheduler.Compile(document, startTick: 0, qpcFrequency: 1_000_000);

    Assert.Equal("baseline", document.Name);
    Assert.Equal(5, scheduled.Count);
}

static void McrxParserCoversWheelAndConsumerSteps()
{
    const string json = """
    {
      "version": 1,
      "name": "extended-input",
      "steps": [
        { "type": "mouse.wheel", "vertical": -1, "horizontal": 2 },
        { "type": "consumer.tap", "control": "VolumeUp" }
      ]
    }
    """;

    var document = McrxParser.Parse(json);

    Assert.Equal(2, document.Steps.Count);
    var wheel = Assert.IsType<MouseWheelStep>(document.Steps[0]);
    Assert.Equal(-1, wheel.Vertical);
    Assert.Equal(2, wheel.Horizontal);

    var consumer = Assert.IsType<ConsumerStep>(document.Steps[1]);
    Assert.Equal(ConsumerControl.VolumeUp, consumer.Control);

    Assert.SequenceEqual([0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x02], HidReportEncoder.EncodeMouseWheel(wheel));
    Assert.SequenceEqual([0x03, 0xE9, 0x00], HidReportEncoder.EncodeConsumer(consumer.Control));
}

static void McrxParserCoversPlaybackHotkeySettings()
{
    const string json = """
    {
      "version": 1,
      "name": "hotkey-playback",
      "playback": {
        "trigger": "Ctrl+Alt+F8",
        "mode": "toggleLoop",
        "count": 3
      },
      "steps": [
        { "type": "key.tap", "key": "A" }
      ]
    }
    """;

    var document = McrxParser.Parse(json);

    Assert.Equal(PlaybackMode.ToggleLoop, document.Playback.Mode);
    Assert.Equal(3, document.Playback.Count);
    Assert.Equal(HidKey.F8, document.Playback.Trigger!.Key);
    Assert.Equal(HidModifier.LeftCtrl | HidModifier.LeftAlt, document.Playback.Trigger.Modifiers);
    Assert.Equal("Ctrl+Alt+F8", document.Playback.Trigger.ToString());
}

static void McrxParserDefaultsMissingPlaybackSettings()
{
    const string json = """
    {
      "version": 1,
      "name": "manual",
      "steps": [
        { "type": "key.tap", "key": "A" }
      ]
    }
    """;

    var document = McrxParser.Parse(json);

    Assert.Equal(PlaybackMode.FixedCount, document.Playback.Mode);
    Assert.Equal(1, document.Playback.Count);
    Assert.Equal(null, document.Playback.Trigger);
}

static void McrxParserRejectsInvalidPlaybackSettings()
{
    const string invalidCountJson = """
    {
      "version": 1,
      "name": "invalid-count",
      "playback": { "mode": "fixedCount", "count": 0 },
      "steps": [
        { "type": "key.tap", "key": "A" }
      ]
    }
    """;

    const string invalidTriggerJson = """
    {
      "version": 1,
      "name": "invalid-trigger",
      "playback": { "trigger": "Ctrl+Alt", "mode": "fixedCount" },
      "steps": [
        { "type": "key.tap", "key": "A" }
      ]
    }
    """;

    Assert.Throws<JsonException>(() => McrxParser.Parse(invalidCountJson));
    Assert.Throws<JsonException>(() => McrxParser.Parse(invalidTriggerJson));
}

static void HidReportEncoderCoversReports()
{
    var keyboard = HidReportEncoder.EncodeKeyboard(
        new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.LeftCtrl | HidModifier.LeftShift, TimeSpan.Zero));
    Assert.SequenceEqual([0x01, 0x03, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00], keyboard);

    var mouse = HidReportEncoder.EncodeMouseMove(
        new MouseMoveStep(MouseMoveMode.Relative, 25, -10, TimeSpan.Zero, MouseButton.Left | MouseButton.X1));
    Assert.SequenceEqual([0x02, 0x09, 0x19, 0x00, 0xF6, 0xFF, 0x00, 0x00], mouse);

    var consumer = HidReportEncoder.EncodeConsumer(ConsumerControl.VolumeUp);
    Assert.SequenceEqual([0x03, 0xE9, 0x00], consumer);
}

static void PixelConditionsMatchWithinTolerance()
{
    var condition = new PixelCondition(
        new PixelCoordinate(CoordinateScope.Window, x: 42, y: 24, WindowTitle: "Editor"),
        new RgbColor(100, 110, 120),
        tolerance: 5);

    Assert.True(condition.Matches(new PixelSample(42, 24, new RgbColor(104, 106, 121))));
    Assert.False(condition.Matches(new PixelSample(42, 24, new RgbColor(106, 110, 120))));
}

static void LatencyHistogramComputesPercentiles()
{
    var histogram = new LatencyHistogram();
    foreach (var value in new[] { 100, 200, 300, 400, 500, 1_000 })
    {
        histogram.RecordMicroseconds(value);
    }

    Assert.Equal(6, histogram.Count);
    Assert.Equal(300, histogram.PercentileMicroseconds(0.50));
    Assert.Equal(500, histogram.PercentileMicroseconds(0.95));
    Assert.Equal(500, histogram.PercentileMicroseconds(0.99));
    Assert.Contains("p95=500us", histogram.Summary());
}

static void PlaybackControllerStopsToggleLoopOnTriggerPress()
{
    var document = new MacroDocument(
        1,
        "toggle",
        new PlaybackSettings(new HotkeyGesture(HidModifier.LeftCtrl, HidKey.F8), PlaybackMode.ToggleLoop, 1),
        [new KeyStep(KeyActionKind.Tap, HidKey.A, HidModifier.None, TimeSpan.Zero)]);
    var executor = new ControlledPlaybackExecutor();
    var controller = new MacroPlaybackController(document, executor);

    controller.TriggerPressedAsync().GetAwaiter().GetResult();

    Assert.Equal(PlaybackStatus.Running, controller.Status);
    Assert.Equal(PlaybackMode.ToggleLoop, executor.LastOptions!.Mode);

    controller.TriggerPressedAsync().GetAwaiter().GetResult();

    Assert.True(executor.LastCancellationToken.IsCancellationRequested);
    executor.Complete();
    controller.WhenIdleAsync().GetAwaiter().GetResult();
    Assert.Equal(PlaybackStatus.Idle, controller.Status);
}

static void PlaybackControllerRunsFixedCountOnceByDefault()
{
    var document = new MacroDocument(
        1,
        "fixed",
        [new KeyStep(KeyActionKind.Tap, HidKey.A, HidModifier.None, TimeSpan.Zero)]);
    var executor = new ControlledPlaybackExecutor();
    var controller = new MacroPlaybackController(document, executor);

    controller.TriggerPressedAsync().GetAwaiter().GetResult();

    Assert.Equal(PlaybackMode.FixedCount, executor.LastOptions!.Mode);
    Assert.Equal(1, executor.LastOptions.Count);
    executor.Complete();
    controller.WhenIdleAsync().GetAwaiter().GetResult();
    Assert.Equal(PlaybackStatus.Idle, controller.Status);
}

static void PlaybackControllerCancelsHoldLoopWhenTriggerIsReleased()
{
    var document = new MacroDocument(
        1,
        "hold",
        new PlaybackSettings(new HotkeyGesture(HidModifier.None, HidKey.F9), PlaybackMode.HoldLoop, 1),
        [new KeyStep(KeyActionKind.Tap, HidKey.A, HidModifier.None, TimeSpan.Zero)]);
    var executor = new ControlledPlaybackExecutor();
    var controller = new MacroPlaybackController(document, executor);

    controller.TriggerPressedAsync().GetAwaiter().GetResult();
    controller.TriggerReleased();

    Assert.True(executor.LastCancellationToken.IsCancellationRequested);
    executor.Complete();
    controller.WhenIdleAsync().GetAwaiter().GetResult();
    Assert.Equal(PlaybackStatus.Idle, controller.Status);
}

static void PlaybackExecutorChecksCancellationBeforeDelayedReports()
{
    var document = new MacroDocument(
        1,
        "cancel",
        [new WaitStep(TimeSpan.FromMilliseconds(10)), new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero)]);
    var sink = new RecordingReportSink();
    using var cancellation = new CancellationTokenSource();
    var delay = new CancellingDelayStrategy(cancellation);
    var executor = new MacroPlaybackExecutor(sink, delay);

    var result = executor.RunAsync(
        document,
        new PlaybackExecutionOptions(PlaybackMode.FixedCount, Count: 1, PixelEvaluationMode.MatchAll, NoWait: false),
        cancellation.Token).GetAwaiter().GetResult();

    Assert.True(result.Cancelled);
    byte[] keyDownAReport = [0x01, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00];
    Assert.False(sink.Reports.Any(report => report.SequenceEqual(keyDownAReport)));
}

static void LocalizationNormalizesSupportedCultures()
{
    Assert.Equal("zh-CN", LocalizationService.NormalizeCultureName(new CultureInfo("zh-Hans-CN")));
    Assert.Equal("zh-TW", LocalizationService.NormalizeCultureName(new CultureInfo("zh-Hant-HK")));
    Assert.Equal("en-US", LocalizationService.NormalizeCultureName(new CultureInfo("fr-FR")));
}

static void LocalizationResourcesCoverPlaybackLabelInThreeLanguages()
{
    Assert.Equal("Playback", LocalizationService.Get("Playback", new CultureInfo("en-US")));
    Assert.Equal("播放", LocalizationService.Get("Playback", new CultureInfo("zh-CN")));
    Assert.Equal("播放", LocalizationService.Get("Playback", new CultureInfo("zh-TW")));
}

static void RuntimeDiagnosticsReportDriverAvailabilityFromStats()
{
    var missing = RuntimeDiagnosticsSnapshot.FromDriverStats(null);
    Assert.False(missing.Driver.Available);
    Assert.Contains("not found", missing.Driver.Detail);

    var present = RuntimeDiagnosticsSnapshot.FromDriverStats(new MacroDriverStats(
        ProtocolVersion: 1,
        ReportsSubmitted: 7,
        ReportsRejected: 2,
        LastNtStatus: 0,
        LastSubmitQpc: 1234));
    Assert.True(present.Driver.Available);
    Assert.Contains("submitted=7", present.Driver.Detail);
    Assert.Contains("rejected=2", present.Driver.Detail);
}

static void MacroConverterIntegrationFindsSiblingPackagedExecutable()
{
    var root = Path.Combine(Path.GetTempPath(), "MacroHID-tests", Guid.NewGuid().ToString("N"));
    var macroRoot = Path.Combine(root, "Macro");
    var startDirectory = Path.Combine(macroRoot, "src", "ui", "MacroStudio", "bin", "Release", "net8.0-windows");
    var converterExe = Path.Combine(root, "MacroConverter", "dist", "MacroConverter-win32-x64", "MacroConverter.exe");

    Directory.CreateDirectory(startDirectory);
    Directory.CreateDirectory(Path.GetDirectoryName(converterExe)!);
    File.WriteAllText(converterExe, string.Empty);

    try
    {
        var status = MacroConverterIntegration.Probe(startDirectory);
        Assert.True(status.Available);
        Assert.Equal(Path.GetFullPath(converterExe), status.ExecutablePath);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    public static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true, got false.");
        }
    }

    public static void False(bool value)
    {
        if (value)
        {
            throw new InvalidOperationException("Expected false, got true.");
        }
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'.");
        }
    }

    public static void SequenceEqual(byte[] expected, byte[] actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }

    public static T IsType<T>(object value)
    {
        if (value is not T)
        {
            throw new InvalidOperationException($"Expected {typeof(T).Name}, got {value.GetType().Name}.");
        }

        return (T)value;
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }
}

sealed class ControlledPlaybackExecutor : IMacroPlaybackExecutor
{
    private readonly TaskCompletionSource<PlaybackRunResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PlaybackExecutionOptions? LastOptions { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public Task<PlaybackRunResult> RunAsync(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CancellationToken cancellationToken)
    {
        LastOptions = options;
        LastCancellationToken = cancellationToken;
        return completion.Task;
    }

    public void Complete()
    {
        completion.TrySetResult(new PlaybackRunResult(PlaybackRunStatus.Completed, IterationsCompleted: 1, ReportsSubmitted: 0, Cancelled: false, DriverStats: null));
    }
}

sealed class RecordingReportSink : IMacroReportSink
{
    public bool IsAvailable => true;

    public List<byte[]> Reports { get; } = [];

    public void Submit(uint sequence, byte[] report)
    {
        Reports.Add(report.ToArray());
    }

    public MacroDriverStats? GetStats()
    {
        return null;
    }
}

sealed class CancellingDelayStrategy : IPlaybackDelayStrategy
{
    private readonly CancellationTokenSource cancellation;

    public CancellingDelayStrategy(CancellationTokenSource cancellation)
    {
        this.cancellation = cancellation;
    }

    public void WaitUntil(long dueTick, long qpcFrequency, CancellationToken cancellationToken, bool noWait)
    {
        cancellation.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
