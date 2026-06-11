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
    ("Input action compiler expands hold actions into timed actions", InputActionCompilerExpandsHoldActions),
    ("Input action compiler evaluates pixel branches before emitting actions", InputActionCompilerEvaluatesPixelBranches),
    ("SendInput encoder covers keyboard, mouse, wheel, and consumer input", SendInputEncoderCoversInputActions),
    ("Pixel conditions match expected colors within tolerance", PixelConditionsMatchWithinTolerance),
    ("Latency histogram computes p50 p95 p99 from microsecond samples", LatencyHistogramComputesPercentiles),
    ("Playback controller starts and stops toggle loop on trigger press", PlaybackControllerStopsToggleLoopOnTriggerPress),
    ("Playback controller runs fixed count once by default", PlaybackControllerRunsFixedCountOnceByDefault),
    ("Playback controller cancels hold loop when trigger is released", PlaybackControllerCancelsHoldLoopWhenTriggerIsReleased),
    ("Playback executor checks cancellation before submitting delayed actions", PlaybackExecutorChecksCancellationBeforeDelayedActions),
    ("Localization normalizes supported cultures", LocalizationNormalizesSupportedCultures),
    ("Localization resources cover playback label in three languages", LocalizationResourcesCoverPlaybackLabelInThreeLanguages),
    ("Runtime diagnostics report SendInput availability from stats", RuntimeDiagnosticsReportSendInputAvailabilityFromStats),
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

static void InputActionCompilerExpandsHoldActions()
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

    var actions = InputActionCompiler.Compile(document, startTick: 10_000, qpcFrequency: 1_000_000);

    Assert.Equal(6, actions.Count);
    Assert.Equal(10_000, actions[0].DueTick);
    Assert.Equal(new KeyInputAction(KeyActionKind.Down, HidKey.A, HidModifier.LeftCtrl), actions[0].Action);
    Assert.Equal(12_000, actions[1].DueTick);
    Assert.Equal(new KeyInputAction(KeyActionKind.Up, HidKey.A, HidModifier.LeftCtrl), actions[1].Action);
    Assert.Equal(12_000, actions[2].DueTick);
    Assert.Equal(new MouseButtonInputAction(MouseButton.Left, ButtonActionKind.Down), actions[2].Action);
    Assert.Equal(15_000, actions[3].DueTick);
    Assert.Equal(new MouseButtonInputAction(MouseButton.Left, ButtonActionKind.Up), actions[3].Action);
    Assert.Equal(15_000, actions[4].DueTick);
    Assert.Equal(new ConsumerInputAction(ConsumerControl.VolumeUp, ButtonActionKind.Down), actions[4].Action);
    Assert.Equal(19_000, actions[5].DueTick);
    Assert.Equal(new ConsumerInputAction(ConsumerControl.VolumeUp, ButtonActionKind.Up), actions[5].Action);
}

static void InputActionCompilerEvaluatesPixelBranches()
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

    var falseActions = InputActionCompiler.Compile(document, 0, 1_000_000, _ => false);
    var trueActions = InputActionCompiler.Compile(document, 0, 1_000_000, _ => true);

    Assert.Equal(0, falseActions.Count);
    Assert.Equal(1, trueActions.Count);
    Assert.Equal(new KeyInputAction(KeyActionKind.Down, HidKey.Enter, HidModifier.None), trueActions[0].Action);
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

    var actions = InputActionCompiler.Compile(document, 0, 1_000_000);
    Assert.Equal(new MouseWheelInputAction(-1, 2, MouseButton.None), actions[0].Action);
    Assert.Equal(new ConsumerInputAction(ConsumerControl.VolumeUp, ButtonActionKind.Down), actions[1].Action);
    Assert.Equal(new ConsumerInputAction(ConsumerControl.VolumeUp, ButtonActionKind.Up), actions[2].Action);
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

static void SendInputEncoderCoversInputActions()
{
    var keyboard = SendInputEncoder.Encode(new KeyInputAction(
        KeyActionKind.Down,
        HidKey.A,
        HidModifier.LeftCtrl | HidModifier.LeftShift));
    Assert.Equal(3, keyboard.Count);
    Assert.Equal(SendInputPacketKind.Keyboard, keyboard[0].Kind);
    Assert.Equal(0xA2, keyboard[0].VirtualKey);
    Assert.Equal(0xA0, keyboard[1].VirtualKey);
    Assert.Equal(0x41, keyboard[2].VirtualKey);

    var mouseMove = SendInputEncoder.Encode(new MouseMoveInputAction(MouseMoveMode.Relative, 25, -10, MouseButton.Left | MouseButton.X1));
    Assert.Equal(1, mouseMove.Count);
    Assert.Equal(SendInputPacketKind.Mouse, mouseMove[0].Kind);
    Assert.Equal(25, mouseMove[0].MouseX);
    Assert.Equal(-10, mouseMove[0].MouseY);
    Assert.Equal(0x0001u, mouseMove[0].Flags);

    var button = SendInputEncoder.Encode(new MouseButtonInputAction(MouseButton.X1, ButtonActionKind.Down));
    Assert.Equal(0x0080u, button[0].Flags);
    Assert.Equal(0x0001u, button[0].MouseData);

    var wheel = SendInputEncoder.Encode(new MouseWheelInputAction(-1, 2, MouseButton.None));
    Assert.Equal(2, wheel.Count);
    Assert.Equal(0x0800u, wheel[0].Flags);
    Assert.Equal(unchecked((uint)-120), wheel[0].MouseData);
    Assert.Equal(0x1000u, wheel[1].Flags);
    Assert.Equal(240u, wheel[1].MouseData);

    var consumer = SendInputEncoder.Encode(new ConsumerInputAction(ConsumerControl.VolumeUp, ButtonActionKind.Down));
    Assert.Equal(0xAF, consumer[0].VirtualKey);
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

static void PlaybackExecutorChecksCancellationBeforeDelayedActions()
{
    var document = new MacroDocument(
        1,
        "cancel",
        [new WaitStep(TimeSpan.FromMilliseconds(10)), new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero)]);
    var sink = new RecordingInputSink();
    using var cancellation = new CancellationTokenSource();
    var delay = new CancellingDelayStrategy(cancellation);
    var executor = new MacroPlaybackExecutor(sink, delay);

    var result = executor.RunAsync(
        document,
        new PlaybackExecutionOptions(PlaybackMode.FixedCount, Count: 1, PixelEvaluationMode.MatchAll, NoWait: false),
        cancellation.Token).GetAwaiter().GetResult();

    Assert.True(result.Cancelled);
    Assert.False(sink.Actions.Contains(new KeyInputAction(KeyActionKind.Down, HidKey.A, HidModifier.None)));
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

static void RuntimeDiagnosticsReportSendInputAvailabilityFromStats()
{
    var present = RuntimeDiagnosticsSnapshot.FromInputStats(new InputSubmissionStats(
        ActionsSubmitted: 7,
        NativeInputsSubmitted: 12,
        FailedSubmissions: 0,
        LastWin32Error: 0,
        LastSubmitQpc: 1234));
    Assert.True(present.InputBackend.Available);
    Assert.Contains("SendInput", present.InputBackend.Detail);
    Assert.Contains("actions=7", present.InputBackend.Detail);
    Assert.Contains("nativeInputs=12", present.InputBackend.Detail);
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
        completion.TrySetResult(new PlaybackRunResult(PlaybackRunStatus.Completed, IterationsCompleted: 1, ActionsSubmitted: 0, Cancelled: false, InputStats: null));
    }
}

sealed class RecordingInputSink : IMacroInputSink
{
    public bool IsAvailable => true;

    public List<InputAction> Actions { get; } = [];

    public void Submit(uint sequence, InputAction action)
    {
        Actions.Add(action);
    }

    public InputSubmissionStats? GetStats()
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
