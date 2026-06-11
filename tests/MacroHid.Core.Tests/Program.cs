using MacroHid.Core;
using MacroHid.Converter;
using MacroHid.Runtime;
using MacroStudio;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

if (Environment.GetEnvironmentVariable("MACROHID_JSON_REFLECTION_DISABLED_PROBE") == "1")
{
    AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
    var probe = new MacroDocument(
        1,
        "json-probe",
        PlaybackSettings.Default,
        [new WaitStep(TimeSpan.FromMilliseconds(0.5))]);
    Console.WriteLine(McrxSerializer.Serialize(probe));
    return 0;
}

var tests = new (string Name, Action Body)[]
{
    ("MCRX parser covers keyboard, mouse, wait, repeat, and pixel steps", McrxParserCoversBaselineSteps),
    ("MCRX parser covers wheel and consumer control steps", McrxParserCoversWheelAndConsumerSteps),
    ("MCRX parser covers key text steps", McrxParserCoversKeyTextSteps),
    ("MCRX parser preserves fractional millisecond timing", McrxParserPreservesFractionalMillisecondTiming),
    ("MCRX serializer works when JSON reflection defaults are disabled", McrxSerializerWorksWhenJsonReflectionDefaultsAreDisabled),
    ("MCRX parser covers macro call and pixel time windows", McrxParserCoversMacroCallAndPixelTimeWindows),
    ("MCRX parser covers playback hotkey settings", McrxParserCoversPlaybackHotkeySettings),
    ("MCRX parser covers modifier-only and mouse side button triggers", McrxParserCoversModifierOnlyAndMouseSideButtonTriggers),
    ("MCRX parser defaults missing playback settings", McrxParserDefaultsMissingPlaybackSettings),
    ("MCRX parser rejects invalid playback settings", McrxParserRejectsInvalidPlaybackSettings),
    ("Sample baseline macro remains parseable", SampleBaselineMacroRemainsParseable),
    ("Scheduler expands repeats and applies waits with QPC ticks", SchedulerExpandsRepeatsAndAppliesWaits),
    ("Input action compiler expands hold actions into timed actions", InputActionCompilerExpandsHoldActions),
    ("Input action compiler emits text actions", InputActionCompilerEmitsTextActions),
    ("Input action compiler expands macro calls and pixel windows", InputActionCompilerExpandsMacroCallsAndPixelWindows),
    ("Input action compiler evaluates pixel branches before emitting actions", InputActionCompilerEvaluatesPixelBranches),
    ("SendInput encoder covers keyboard, mouse, wheel, and consumer input", SendInputEncoderCoversInputActions),
    ("SendInput encoder covers Unicode text actions", SendInputEncoderCoversUnicodeTextActions),
    ("Pixel conditions match expected colors within tolerance", PixelConditionsMatchWithinTolerance),
    ("Latency histogram computes p50 p95 p99 from microsecond samples", LatencyHistogramComputesPercentiles),
    ("Playback controller starts and stops toggle loop on trigger press", PlaybackControllerStopsToggleLoopOnTriggerPress),
    ("Playback controller runs fixed count once by default", PlaybackControllerRunsFixedCountOnceByDefault),
    ("Playback controller cancels hold loop when trigger is released", PlaybackControllerCancelsHoldLoopWhenTriggerIsReleased),
    ("Playback executor checks cancellation before submitting delayed actions", PlaybackExecutorChecksCancellationBeforeDelayedActions),
    ("Playback executor resolves macro call steps", PlaybackExecutorResolvesMacroCallSteps),
    ("Localization normalizes supported cultures", LocalizationNormalizesSupportedCultures),
    ("Localization resources cover playback label in three languages", LocalizationResourcesCoverPlaybackLabelInThreeLanguages),
    ("Localization resources cover macro workbench labels in three languages", LocalizationResourcesCoverMacroWorkbenchLabelsInThreeLanguages),
    ("MacroStudio manifest requests administrator by default", MacroStudioManifestRequestsAdministratorByDefault),
    ("MacroStudio uses borderless custom window chrome", MacroStudioUsesBorderlessCustomWindowChrome),
    ("Runtime diagnostics report SendInput availability from stats", RuntimeDiagnosticsReportSendInputAvailabilityFromStats),
    ("Embedded converter imports MacroConverter formats", EmbeddedConverterImportsMacroConverterFormats),
    ("Embedded converter preserves Razer sub-millisecond timing", EmbeddedConverterPreservesRazerSubMillisecondTiming),
    ("Embedded converter exports MacroConverter formats", EmbeddedConverterExportsMacroConverterFormats),
    ("Embedded converter reports warnings for unsupported external features", EmbeddedConverterReportsWarningsForUnsupportedExternalFeatures),
    ("Macro library store persists and duplicates macros", MacroLibraryStorePersistsAndDuplicatesMacros),
    ("Macro action templates create playable steps", MacroActionTemplatesCreatePlayableSteps),
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

static void InputActionCompilerEmitsTextActions()
{
    var document = new MacroDocument(
        Version: 1,
        Name: "text",
        Steps:
        [
            new TextStep("Hi")
        ]);

    var actions = InputActionCompiler.Compile(document, startTick: 500, qpcFrequency: 1_000_000);

    Assert.Equal(1, actions.Count);
    Assert.Equal(500, actions[0].DueTick);
    Assert.Equal(new TextInputAction("Hi"), actions[0].Action);
}

static void InputActionCompilerExpandsMacroCallsAndPixelWindows()
{
    var target = new MacroDocument(
        1,
        "burst",
        [
            new KeyStep(KeyActionKind.Tap, HidKey.B, HidModifier.None, TimeSpan.FromMilliseconds(1))
        ]);
    var condition = new PixelCondition(
        new PixelCoordinate(CoordinateScope.Screen, 10, 20),
        new RgbColor(255, 0, 0),
        4);
    var document = new MacroDocument(
        1,
        "flow",
        [
            new MacroCallStep("burst"),
            new PixelWhenStep(
                condition,
                [new MouseButtonStep(MouseButton.Left, ButtonActionKind.Click, TimeSpan.FromMilliseconds(1))],
                WindowStart: TimeSpan.FromMilliseconds(3),
                WindowEnd: TimeSpan.FromMilliseconds(5),
                PollInterval: TimeSpan.FromMilliseconds(1))
        ]);

    var actions = InputActionCompiler.Compile(
        document,
        startTick: 100,
        qpcFrequency: 1_000,
        pixelEvaluator: _ => true,
        macroResolver: name => name == "burst" ? target : null);

    Assert.Equal(4, actions.Count);
    Assert.Equal(100, actions[0].DueTick);
    Assert.Equal(new KeyInputAction(KeyActionKind.Down, HidKey.B, HidModifier.None), actions[0].Action);
    Assert.Equal(101, actions[1].DueTick);
    Assert.Equal(new KeyInputAction(KeyActionKind.Up, HidKey.B, HidModifier.None), actions[1].Action);
    Assert.Equal(103, actions[2].DueTick);
    Assert.Equal(new MouseButtonInputAction(MouseButton.Left, ButtonActionKind.Down), actions[2].Action);
    Assert.Equal(104, actions[3].DueTick);
    Assert.Equal(new MouseButtonInputAction(MouseButton.Left, ButtonActionKind.Up), actions[3].Action);
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

static void McrxParserCoversKeyTextSteps()
{
    const string json = """
    {
      "version": 1,
      "name": "text-input",
      "steps": [
        { "type": "key.text", "text": "Hello 世界" }
      ]
    }
    """;

    var document = McrxParser.Parse(json);

    var text = Assert.IsType<TextStep>(document.Steps[0]);
    Assert.Equal("Hello 世界", text.Text);
}

static void McrxParserPreservesFractionalMillisecondTiming()
{
    const string json = """
    {
      "version": 1,
      "name": "fractional-timing",
      "steps": [
        { "type": "key.tap", "key": "A", "holdMs": 0.75 },
        { "type": "wait", "ms": 0.5 },
        { "type": "mouse.move", "mode": "relative", "x": 1, "y": 2, "durationMs": 1.25 }
      ]
    }
    """;

    var document = McrxParser.Parse(json);
    var key = Assert.IsType<KeyStep>(document.Steps[0]);
    var wait = Assert.IsType<WaitStep>(document.Steps[1]);
    var move = Assert.IsType<MouseMoveStep>(document.Steps[2]);

    Assert.Equal(TimeSpan.FromTicks(7_500), key.Hold);
    Assert.Equal(TimeSpan.FromTicks(5_000), wait.Duration);
    Assert.Equal(TimeSpan.FromTicks(12_500), move.Duration);

    var serialized = McrxSerializer.Serialize(document);
    Assert.Contains("\"holdMs\": 0.75", serialized);
    Assert.Contains("\"ms\": 0.5", serialized);
    Assert.Contains("\"durationMs\": 1.25", serialized);
}

static void McrxSerializerWorksWhenJsonReflectionDefaultsAreDisabled()
{
    Assert.True(GetPrivateJsonOptions(typeof(McrxSerializer), "Options").TypeInfoResolver is not null);
    Assert.True(GetPrivateJsonOptions(typeof(MacroLibraryStore), "Options").TypeInfoResolver is not null);

    var processPath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Process path is not available.");
    var startInfo = new ProcessStartInfo(processPath)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    startInfo.Environment["MACROHID_JSON_REFLECTION_DISABLED_PROBE"] = "1";

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start JSON reflection probe process.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit(15_000);

    Assert.Equal(0, process.ExitCode);
    Assert.Contains("\"name\": \"json-probe\"", output);
    Assert.Equal(string.Empty, error);
}

static JsonSerializerOptions GetPrivateJsonOptions(Type type, string fieldName)
{
    var field = type.GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException($"Field {type.Name}.{fieldName} was not found.");
    return (JsonSerializerOptions)(field.GetValue(null)
        ?? throw new InvalidOperationException($"Field {type.Name}.{fieldName} is null."));
}

static void McrxParserCoversMacroCallAndPixelTimeWindows()
{
    const string json = """
    {
      "version": 1,
      "name": "flow",
      "steps": [
        { "type": "macro.call", "macro": "burst" },
        {
          "type": "pixel.when",
          "scope": "screen",
          "x": 10,
          "y": 20,
          "r": 255,
          "g": 0,
          "b": 0,
          "tolerance": 6,
          "windowStartMs": 3000,
          "windowEndMs": 5000,
          "pollIntervalMs": 25,
          "then": [
            { "type": "key.tap", "key": "F" }
          ]
        }
      ]
    }
    """;

    var document = McrxParser.Parse(json);

    var call = Assert.IsType<MacroCallStep>(document.Steps[0]);
    Assert.Equal("burst", call.Macro);

    var pixel = Assert.IsType<PixelWhenStep>(document.Steps[1]);
    Assert.Equal(TimeSpan.FromSeconds(3), pixel.WindowStart);
    Assert.Equal(TimeSpan.FromSeconds(5), pixel.WindowEnd);
    Assert.Equal(TimeSpan.FromMilliseconds(25), pixel.PollInterval);
    Assert.Equal(new RgbColor(255, 0, 0), pixel.Condition.Expected);

    var serialized = McrxSerializer.Serialize(document);
    Assert.Contains("\"type\": \"macro.call\"", serialized);
    Assert.Contains("\"windowStartMs\": 3000", serialized);
    Assert.Contains("\"windowEndMs\": 5000", serialized);
    Assert.Contains("\"pollIntervalMs\": 25", serialized);
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

static void McrxParserCoversModifierOnlyAndMouseSideButtonTriggers()
{
    var ctrl = McrxParser.ParseHotkeyGesture("Ctrl");
    Assert.Equal(HidModifier.LeftCtrl, ctrl.Modifiers);
    Assert.Equal(HidKey.None, ctrl.Key);
    Assert.Equal(MouseButton.None, ctrl.MouseButton);
    Assert.Equal("Ctrl", ctrl.ToString());

    var mouse = McrxParser.ParseHotkeyGesture("Ctrl+X1");
    Assert.Equal(HidModifier.LeftCtrl, mouse.Modifiers);
    Assert.Equal(HidKey.None, mouse.Key);
    Assert.Equal(MouseButton.X1, mouse.MouseButton);
    Assert.Equal("Ctrl+X1", mouse.ToString());
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
      "playback": { "trigger": "F8+F9", "mode": "fixedCount" },
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

static void SendInputEncoderCoversUnicodeTextActions()
{
    var packets = SendInputEncoder.Encode(new TextInputAction("A中"));

    Assert.Equal(4, packets.Count);
    Assert.Equal(SendInputPacketKind.Keyboard, packets[0].Kind);
    Assert.Equal(0, packets[0].VirtualKey);
    Assert.Equal((ushort)'A', packets[0].ScanCode);
    Assert.Equal(0x0004u, packets[0].Flags);
    Assert.Equal((ushort)'A', packets[1].ScanCode);
    Assert.Equal(0x0006u, packets[1].Flags);
    Assert.Equal((ushort)'中', packets[2].ScanCode);
    Assert.Equal(0x0004u, packets[2].Flags);
    Assert.Equal(0x0006u, packets[3].Flags);
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

static void PlaybackExecutorResolvesMacroCallSteps()
{
    var sink = new RecordingInputSink();
    var target = new MacroDocument(
        1,
        "burst",
        [new KeyStep(KeyActionKind.Tap, HidKey.B, HidModifier.None, TimeSpan.FromMilliseconds(1))]);
    var document = new MacroDocument(
        1,
        "caller",
        [new MacroCallStep("burst")]);
    var executor = new MacroPlaybackExecutor(
        sink,
        macroResolver: name => name == "burst" ? target : null);

    var result = executor.RunAsync(
        document,
        new PlaybackExecutionOptions(PlaybackMode.FixedCount, Count: 1, PixelEvaluationMode.MatchAll, NoWait: true),
        CancellationToken.None).GetAwaiter().GetResult();

    Assert.Equal(PlaybackRunStatus.Completed, result.Status);
    Assert.Equal(2, sink.Actions.Count);
    Assert.Equal(new KeyInputAction(KeyActionKind.Down, HidKey.B, HidModifier.None), sink.Actions[0]);
    Assert.Equal(new KeyInputAction(KeyActionKind.Up, HidKey.B, HidModifier.None), sink.Actions[1]);
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

static void LocalizationResourcesCoverMacroWorkbenchLabelsInThreeLanguages()
{
    Assert.Equal("Macro Library", LocalizationService.Get("MacroLibrary", new CultureInfo("en-US")));
    Assert.Equal("宏数据库", LocalizationService.Get("MacroLibrary", new CultureInfo("zh-CN")));
    Assert.Equal("巨集資料庫", LocalizationService.Get("MacroLibrary", new CultureInfo("zh-TW")));
    Assert.Equal("Keyboard Function", LocalizationService.Get("AddKeyboard", new CultureInfo("en-US")));
    Assert.Equal("键盘功能", LocalizationService.Get("AddKeyboard", new CultureInfo("zh-CN")));
    Assert.Equal("鍵盤功能", LocalizationService.Get("AddKeyboard", new CultureInfo("zh-TW")));
    Assert.Equal("Macro", LocalizationService.Get("AddMacro", new CultureInfo("en-US")));
    Assert.Equal("宏", LocalizationService.Get("AddMacro", new CultureInfo("zh-CN")));
    Assert.Equal("巨集", LocalizationService.Get("AddMacro", new CultureInfo("zh-TW")));
    Assert.Equal("Pixel IF", LocalizationService.Get("PixelIf", new CultureInfo("en-US")));
    Assert.Equal("像素 IF 条件", LocalizationService.Get("PixelIf", new CultureInfo("zh-CN")));
    Assert.Equal("像素 IF 條件", LocalizationService.Get("PixelIf", new CultureInfo("zh-TW")));
}

static void MacroStudioManifestRequestsAdministratorByDefault()
{
    var manifestPath = Path.Combine("src", "ui", "MacroStudio", "app.manifest");
    var projectPath = Path.Combine("src", "ui", "MacroStudio", "MacroStudio.csproj");

    Assert.True(File.Exists(manifestPath));
    Assert.Contains("level=\"requireAdministrator\"", File.ReadAllText(manifestPath));
    Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", File.ReadAllText(projectPath));
}

static void MacroStudioUsesBorderlessCustomWindowChrome()
{
    var xaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml"));
    var codeBehind = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));

    Assert.Contains("WindowStyle=\"None\"", xaml);
    Assert.Contains("WindowChrome.WindowChrome", xaml);
    Assert.Contains("MinimizeWindowButton", xaml);
    Assert.Contains("MaximizeWindowButton", xaml);
    Assert.Contains("CloseWindowButton", xaml);
    Assert.Contains("StepEditorPanel", xaml);
    Assert.Contains("MacroTargetBox", xaml);
    Assert.Contains("PickPixelColorButton", xaml);
    Assert.Contains("Color=\"#F5F5F7\"", xaml);
    Assert.Contains("TopChromeBar_MouseLeftButtonDown", codeBehind);
    Assert.Contains("ToggleWindowMaximize", codeBehind);
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

static void EmbeddedConverterImportsMacroConverterFormats()
{
    const string qmacro = """
    MoveTo 640, 360
    LeftClick 1
    Delay 250
    KeyPress "F1", 1
    SayString "MacroConverter"
    """;
    const string lua = """
    macro.begin("Lua")
    macro.move(10, 20)
    macro.click("right", 1)
    macro.wait(15)
    macro.key("Escape", 1)
    macro.text("hello")
    macro.finish()
    """;
    const string xmouse = """
    <XMouseProfile name="XMouse Demo">
      <Button name="Button4">
        <Action type="simulated-keystrokes" keys="{CTRL}C{WAITMS:200}{LMB}" />
      </Button>
    </XMouseProfile>
    """;
    const string macroXml = """
    <MacroConverterMacro version="1" name="XML Demo">
      <Nodes>
        <Node id="start" type="start" label="Start" x="0" y="120" />
        <Node id="move" type="mouse.move" label="Move" x="160" y="120"><Data x="320" y="240" durationMs="120" /></Node>
        <Node id="click" type="mouse.click" label="Click" x="320" y="120"><Data button="left" count="1" /></Node>
        <Node id="end" type="end" label="End" x="480" y="120" />
      </Nodes>
      <Edges>
        <Edge id="e-start-move" source="start" target="move" kind="default" />
        <Edge id="e-move-click" source="move" target="click" kind="default" />
        <Edge id="e-click-end" source="click" target="end" kind="default" />
      </Edges>
    </MacroConverterMacro>
    """;
    const string razer = """
    <Macro>
      <Name>Razer Demo</Name>
      <MacroEvents>
        <MacroEvent><Type>6</Type><Number>2</Number><LoopEvent><State>0</State></LoopEvent></MacroEvent>
        <MacroEvent><Type>2</Type><MouseEvent><MouseButton>0</MouseButton><State>0</State></MouseEvent></MacroEvent>
        <MacroEvent><Type>0</Type><Number>0.010</Number></MacroEvent>
        <MacroEvent><Type>2</Type><MouseEvent><MouseButton>0</MouseButton><State>1</State></MouseEvent></MacroEvent>
        <MacroEvent><Type>6</Type><Number>2</Number><LoopEvent><State>1</State></LoopEvent></MacroEvent>
      </MacroEvents>
      <Version>4</Version>
    </Macro>
    """;

    var qmacroResult = MacroConversionService.ImportToMcrx(new MacroImportRequest(qmacro, "sample.mq"));
    Assert.Equal(MacroConversionFormat.QMacro, qmacroResult.SourceFormat);
    Assert.IsType<TextStep>(qmacroResult.Document.Steps.Last());

    var luaResult = MacroConversionService.ImportToMcrx(new MacroImportRequest(lua, "sample.lua"));
    Assert.Equal(MacroConversionFormat.Lua, luaResult.SourceFormat);
    Assert.True(luaResult.Document.Steps.OfType<TextStep>().Any());

    var xmouseResult = MacroConversionService.ImportToMcrx(new MacroImportRequest(xmouse, "sample.xmbcs"));
    Assert.Equal(MacroConversionFormat.XMouse, xmouseResult.SourceFormat);
    Assert.True(xmouseResult.Document.Steps.OfType<WaitStep>().Any());

    var macroXmlResult = MacroConversionService.ImportToMcrx(new MacroImportRequest(macroXml, "sample.xml"));
    Assert.Equal(MacroConversionFormat.MacroConverterXml, macroXmlResult.SourceFormat);
    Assert.IsType<MouseMoveStep>(macroXmlResult.Document.Steps[0]);

    var razerResult = MacroConversionService.ImportToMcrx(new MacroImportRequest(razer, "sample.xml"));
    Assert.Equal(MacroConversionFormat.RazerSynapseXml, razerResult.SourceFormat);
    var repeat = Assert.IsType<RepeatStep>(razerResult.Document.Steps[0]);
    Assert.Equal(2, repeat.Count);
    Assert.IsType<MouseButtonStep>(repeat.Steps[0]);
}

static void EmbeddedConverterPreservesRazerSubMillisecondTiming()
{
    const string razer = """
    <Macro>
      <Name>Razer Precision</Name>
      <MacroEvents>
        <MacroEvent><Type>2</Type><MouseEvent><MouseButton>0</MouseButton><State>0</State></MouseEvent></MacroEvent>
        <MacroEvent><Type>0</Type><Number>0.00075</Number></MacroEvent>
        <MacroEvent><Type>2</Type><MouseEvent><MouseButton>0</MouseButton><State>1</State></MouseEvent></MacroEvent>
        <MacroEvent><Type>0</Type><Number>0.00125</Number></MacroEvent>
        <MacroEvent><Type>1</Type><KeyEvent><Makecode>65</Makecode><State>0</State></KeyEvent></MacroEvent>
        <MacroEvent><Type>0</Type><Number>0.0005</Number></MacroEvent>
        <MacroEvent><Type>1</Type><KeyEvent><Makecode>65</Makecode><State>1</State></KeyEvent></MacroEvent>
      </MacroEvents>
      <Version>4</Version>
    </Macro>
    """;

    var import = MacroConversionService.ImportToMcrx(new MacroImportRequest(razer, "precision.xml"));

    var click = Assert.IsType<MouseButtonStep>(import.Document.Steps[0]);
    var wait = Assert.IsType<WaitStep>(import.Document.Steps[1]);
    var key = Assert.IsType<KeyStep>(import.Document.Steps[2]);

    Assert.Equal(TimeSpan.FromTicks(7_500), click.Hold);
    Assert.Equal(TimeSpan.FromTicks(12_500), wait.Duration);
    Assert.Equal(TimeSpan.FromTicks(5_000), key.Hold);
}

static void EmbeddedConverterExportsMacroConverterFormats()
{
    var document = new MacroDocument(
        1,
        "Export Demo",
        [
            new MouseMoveStep(MouseMoveMode.Absolute, 100, 200, TimeSpan.Zero),
            new MouseButtonStep(MouseButton.Left, ButtonActionKind.Click, TimeSpan.FromMilliseconds(10)),
            new WaitStep(TimeSpan.FromMilliseconds(25)),
            new KeyStep(KeyActionKind.Tap, HidKey.F1, HidModifier.None, TimeSpan.Zero),
            new TextStep("MacroConverter")
        ]);

    var mcrx = MacroConversionService.ExportFromMcrx(document, MacroConversionFormat.MacroHidMcrx);
    Assert.Contains("\"key.text\"", mcrx.Output);

    var xml = MacroConversionService.ExportFromMcrx(document, MacroConversionFormat.MacroConverterXml);
    Assert.Contains("<MacroConverterMacro", xml.Output);
    Assert.Contains("keyboard.text", xml.Output);

    var lua = MacroConversionService.ExportFromMcrx(document, MacroConversionFormat.Lua);
    Assert.Contains("macro.text(\"MacroConverter\")", lua.Output);

    var qmacro = MacroConversionService.ExportFromMcrx(document, MacroConversionFormat.QMacro);
    Assert.Contains("SayString \"MacroConverter\"", qmacro.Output);

    var xmouse = MacroConversionService.ExportFromMcrx(document, MacroConversionFormat.XMouse);
    Assert.Contains("{LMB}", xmouse.Output);

    var razer = MacroConversionService.ExportFromMcrx(document, MacroConversionFormat.RazerSynapseXml);
    Assert.Contains("<Macro>", razer.Output);
    Assert.True(razer.Diagnostics.Any(item => item.Severity == MacroDiagnosticSeverity.Warning));
}

static void EmbeddedConverterReportsWarningsForUnsupportedExternalFeatures()
{
    var document = new MacroDocument(
        1,
        "Unsupported",
        [
            new ConsumerStep(ConsumerControl.VolumeUp, ButtonActionKind.Click, TimeSpan.Zero),
            new PixelWhenStep(
                new PixelCondition(new PixelCoordinate(CoordinateScope.Screen, 10, 20), new RgbColor(1, 2, 3), 0),
                [new KeyStep(KeyActionKind.Tap, HidKey.A, HidModifier.None, TimeSpan.Zero)])
        ]);

    var export = MacroConversionService.ExportFromMcrx(document, MacroConversionFormat.Lua);

    Assert.True(export.Diagnostics.Any(item => item.Severity == MacroDiagnosticSeverity.Warning));
    Assert.True(export.Output.Contains("macro.begin", StringComparison.Ordinal));
}

static void MacroLibraryStorePersistsAndDuplicatesMacros()
{
    var root = Path.Combine(Path.GetTempPath(), "MacroHID-tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new MacroLibraryStore(root);
        var created = store.CreateMacro("Burst A", "Combat",
        [
            new KeyStep(KeyActionKind.Tap, HidKey.A, HidModifier.None, TimeSpan.FromMilliseconds(0.5)),
            new WaitStep(TimeSpan.FromMilliseconds(12))
        ]);

        var snapshot = store.Load();
        Assert.Equal(1, snapshot.Items.Count);
        Assert.Equal("Burst A", snapshot.Items[0].Name);
        Assert.Equal("Combat", snapshot.Items[0].Folder);
        Assert.Equal(created.Id, snapshot.SelectedMacroId);

        var saved = store.ReadMacro(created.Id);
        Assert.Equal("Burst A", saved.Name);
        Assert.Equal(2, saved.Steps.Count);

        var duplicate = store.DuplicateMacro(created.Id, "Burst A Copy");
        var reloaded = new MacroLibraryStore(root).Load();
        Assert.Equal(2, reloaded.Items.Count);
        Assert.True(reloaded.Items.Any(item => item.Id == duplicate.Id && item.Name == "Burst A Copy"));
        Assert.Equal("Burst A Copy", new MacroLibraryStore(root).ReadMacro(duplicate.Id).Name);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void MacroActionTemplatesCreatePlayableSteps()
{
    var delay = Assert.IsType<WaitStep>(MacroActionTemplateFactory.CreateStep(MacroActionTemplateKind.Delay));
    Assert.Equal(TimeSpan.FromMilliseconds(100), delay.Duration);

    var key = Assert.IsType<KeyStep>(MacroActionTemplateFactory.CreateStep(MacroActionTemplateKind.Keyboard));
    Assert.Equal(KeyActionKind.Tap, key.Kind);
    Assert.Equal(HidKey.A, key.Key);

    var mouse = Assert.IsType<MouseButtonStep>(MacroActionTemplateFactory.CreateStep(MacroActionTemplateKind.MouseButton));
    Assert.Equal(MouseButton.Left, mouse.Button);
    Assert.Equal(ButtonActionKind.Click, mouse.Kind);

    var move = Assert.IsType<MouseMoveStep>(MacroActionTemplateFactory.CreateStep(MacroActionTemplateKind.MouseMove));
    Assert.Equal(MouseMoveMode.Relative, move.Mode);

    var text = Assert.IsType<TextStep>(MacroActionTemplateFactory.CreateStep(MacroActionTemplateKind.Text));
    Assert.Equal("text", text.Text);

    var repeat = Assert.IsType<RepeatStep>(MacroActionTemplateFactory.CreateStep(MacroActionTemplateKind.Loop));
    Assert.Equal(2, repeat.Count);
    Assert.True(repeat.Steps.Count > 0);

    var document = new MacroDocument(
        Version: 1,
        Name: "templates",
        Steps:
        [
            delay,
            key,
            mouse,
            move,
            text,
            repeat
        ]);
    var roundTrip = McrxParser.Parse(McrxSerializer.Serialize(document));
    Assert.Equal(6, roundTrip.Steps.Count);
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
