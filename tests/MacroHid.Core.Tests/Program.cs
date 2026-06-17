using MacroHid.Core;
using MacroHid.Converter;
using MacroHid.Runtime;
using MacroStudio;
using MacroStudio.Controls;
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
    ("MCRX parser covers mouse button coordinates", McrxParserCoversMouseButtonCoordinates),
    ("MCRX parser preserves fractional millisecond timing", McrxParserPreservesFractionalMillisecondTiming),
    ("MCRX parser expands tap and click steps into press release steps", McrxParserExpandsTapAndClickStepsIntoPressReleaseSteps),
    ("MCRX serializer works when JSON reflection defaults are disabled", McrxSerializerWorksWhenJsonReflectionDefaultsAreDisabled),
    ("MCRX parser covers macro call and pixel time windows", McrxParserCoversMacroCallAndPixelTimeWindows),
    ("MCRX parser covers conditional directive time windows", McrxParserCoversConditionalDirectiveTimeWindows),
    ("MCRX parser covers random waits and conditional directive paths", McrxParserCoversRandomWaitsAndConditionalDirectivePaths),
    ("MCRX parser covers playback hotkey settings", McrxParserCoversPlaybackHotkeySettings),
    ("MCRX parser covers playback precision mode", McrxParserCoversPlaybackPrecisionMode),
    ("Playback process filter matches foreground process names", PlaybackProcessFilterMatchesForegroundProcessNames),
    ("MCRX parser covers modifier-only and mouse side button triggers", McrxParserCoversModifierOnlyAndMouseSideButtonTriggers),
    ("MCRX parser defaults missing playback settings", McrxParserDefaultsMissingPlaybackSettings),
    ("MCRX parser rejects invalid playback settings", McrxParserRejectsInvalidPlaybackSettings),
    ("Sample baseline macro remains parseable", SampleBaselineMacroRemainsParseable),
    ("Scheduler expands repeats and applies waits with QPC ticks", SchedulerExpandsRepeatsAndAppliesWaits),
    ("Playback delay strategy uses high precision short wait path", PlaybackDelayStrategyUsesHighPrecisionShortWaitPath),
    ("Playback runtime exposes precision context compiled plans and jitter stats", PlaybackRuntimeExposesPrecisionContextCompiledPlansAndJitterStats),
    ("Input action compiler expands hold actions into timed actions", InputActionCompilerExpandsHoldActions),
    ("Input action compiler emits text actions", InputActionCompilerEmitsTextActions),
    ("Input action compiler expands macro calls and pixel windows", InputActionCompilerExpandsMacroCallsAndPixelWindows),
    ("Input action compiler evaluates pixel branches before emitting actions", InputActionCompilerEvaluatesPixelBranches),
    ("Input action compiler samples random waits for each compilation", InputActionCompilerSamplesRandomWaitsForEachCompilation),
    ("SendInput encoder covers keyboard, mouse, wheel, and consumer input", SendInputEncoderCoversInputActions),
    ("SendInput encoder covers Unicode text actions", SendInputEncoderCoversUnicodeTextActions),
    ("Prepared SendInput batches preserve encoder output", PreparedSendInputBatchesPreserveEncoderOutput),
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
    ("MacroStudio uses launcher style soft workbench shell", MacroStudioUsesLauncherStyleSoftWorkbenchShell),
    ("MacroStudio lays out base and conditional sequences side by side", MacroStudioLaysOutBaseAndConditionalSequencesSideBySide),
    ("MacroStudio opens condition then-action editor", MacroStudioOpensConditionThenActionEditor),
    ("MacroStudio exposes inline step editing and condition range controls", MacroStudioExposesInlineStepEditingAndConditionRangeControls),
    ("MacroStudio validates condition time windows and persists path ranges", MacroStudioValidatesConditionTimeWindowsAndPersistsPathRanges),
    ("MacroStudio suppresses condition range events while refreshing step choices", MacroStudioSuppressesConditionRangeEventsWhileRefreshingStepChoices),
    ("MacroStudio lets condition sequence pick pixel colors", MacroStudioLetsConditionSequencePickPixelColors),
    ("MacroStudio exposes mouse button coordinate editor", MacroStudioExposesMouseButtonCoordinateEditor),
    ("MacroStudio keeps step editor combo box values selectable", MacroStudioKeepsStepEditorComboBoxValuesSelectable),
    ("MacroStudio exposes undo for the last sequence edit", MacroStudioExposesUndoForLastSequenceEdit),
    ("MacroStudio gives every sequence row inline actions and styled menus", MacroStudioGivesEverySequenceRowInlineActionsAndStyledMenus),
    ("MacroStudio supports undo shortcut and clearing the whole sequence", MacroStudioSupportsUndoShortcutAndClearingWholeSequence),
    ("MacroStudio supports macro call selection and playback autosave", MacroStudioSupportsMacroCallSelectionAndPlaybackAutosave),
    ("MacroStudio supports multiple hotkey listeners and library trigger summaries", MacroStudioSupportsMultipleHotkeyListenersAndLibraryTriggerSummaries),
    ("MacroStudio drop insertion uses target row and suppresses drag click duplication", MacroStudioDropInsertionUsesTargetRowAndSuppressesDragClickDuplication),
    ("MacroStudio supports multi-select drag feedback and keeps pixel conditions in condition panel", MacroStudioSupportsMultiSelectDragFeedbackAndConditionOnlyPixel),
    ("MacroStudio hosts step properties inside sequence cards", MacroStudioHostsStepPropertiesInsideSequenceCards),
    ("MacroStudio closes inline editor after apply and returns focus to sequence", MacroStudioClosesInlineEditorAfterApplyAndReturnsFocusToSequence),
    ("MacroStudio closes inline editor on outside click but keeps coordinate picker open", MacroStudioClosesInlineEditorOnOutsideClickButKeepsCoordinatePickerOpen),
    ("MacroStudio closes inline editor when focus moves to sibling panels", MacroStudioClosesInlineEditorWhenFocusMovesToSiblingPanels),
    ("MacroStudio uses shared step sequence panel for base and condition actions", MacroStudioUsesSharedStepSequencePanelForBaseAndConditionActions),
    ("MacroStudio condition actions support explorer sequence interactions", MacroStudioConditionActionsSupportExplorerSequenceInteractions),
    ("MacroStudio condition list supports explorer operations", MacroStudioConditionListSupportsExplorerOperations),
    ("MacroStudio provides workspace window menu", MacroStudioProvidesWorkspaceWindowMenu),
    ("MacroStudio provides IDEA style tool window stripe", MacroStudioProvidesIdeaStyleToolWindowStripe),
    ("MacroStudio detaches MCRX JSON into a tool panel", MacroStudioDetachesMcrxJsonIntoToolPanel),
    ("MacroStudio moves conversion into macro library and removes diagnostics controls", MacroStudioMovesConversionIntoMacroLibraryAndRemovesDiagnosticsControls),
    ("MacroStudio preserves Razer module calls when importing main macros", MacroStudioPreservesRazerModuleCallsWhenImportingMainMacros),
    ("MacroStudio supports multi-select toolbar move operations", MacroStudioSupportsMultiSelectToolbarMoveOperations),
    ("MacroStudio supports explorer style box selection in sequence panels", MacroStudioSupportsExplorerStyleBoxSelectionInSequencePanels),
    ("MacroStudio supports explorer shortcuts autoscroll and stable multi-drag", MacroStudioSupportsExplorerShortcutsAutoscrollAndStableMultiDrag),
    ("MacroStudio action template gate rejects duplicate gesture inserts", MacroStudioActionTemplateGateRejectsDuplicateGestureInserts),
    ("Macro step tree editor inserts and deletes inside loops by path", MacroStepTreeEditorInsertsAndDeletesInsideLoopsByPath),
    ("Macro step tree editor moves multiple selected steps into loops by path", MacroStepTreeEditorMovesMultipleSelectedStepsIntoLoopsByPath),
    ("Macro step tree editor edits linked press release pairs", MacroStepTreeEditorEditsLinkedPressReleasePairs),
    ("Step display labels use press release wording", StepDisplayLabelsUsePressReleaseWording),
    ("Step display labels resolve macro call ids to names", StepDisplayLabelsResolveMacroCallIdsToNames),
    ("MacroStudio resolves macro call aliases for display and playback", MacroStudioResolvesMacroCallAliasesForDisplayAndPlayback),
    ("Runtime diagnostics report SendInput availability from stats", RuntimeDiagnosticsReportSendInputAvailabilityFromStats),
    ("Condition monitor uses precision thread and prepared then actions", ConditionMonitorUsesPrecisionThreadAndPreparedThenActions),
    ("LatencyProbe reports precision histograms", LatencyProbeReportsPrecisionHistograms),
    ("Embedded converter imports MacroConverter formats", EmbeddedConverterImportsMacroConverterFormats),
    ("Embedded converter preserves Razer sub-millisecond timing", EmbeddedConverterPreservesRazerSubMillisecondTiming),
    ("Embedded converter imports Razer module references as macro calls", EmbeddedConverterImportsRazerModuleReferencesAsMacroCalls),
    ("Embedded converter exports MacroConverter formats", EmbeddedConverterExportsMacroConverterFormats),
    ("Embedded converter reports warnings for unsupported external features", EmbeddedConverterReportsWarningsForUnsupportedExternalFeatures),
    ("Macro library store persists and duplicates macros", MacroLibraryStorePersistsAndDuplicatesMacros),
    ("Macro library store resolves external aliases", MacroLibraryStoreResolvesExternalAliases),
    ("Macro library store persists empty folders and moves macros like files", MacroLibraryStorePersistsEmptyFoldersAndMovesMacrosLikeFiles),
    ("Macro library store renames macros and folders", MacroLibraryStoreRenamesMacrosAndFolders),
    ("MacroStudio macro library uses a folder tree", MacroStudioMacroLibraryUsesFolderTree),
    ("MacroStudio macro library supports Explorer rename copy and paste", MacroStudioMacroLibrarySupportsExplorerRenameCopyAndPaste),
    ("Macro action templates create playable press release steps", MacroActionTemplatesCreatePlayablePressReleaseSteps),
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
    Assert.Equal(7, document.Steps.Count);
    Assert.Equal(KeyActionKind.Down, Assert.IsType<KeyStep>(document.Steps[0]).Kind);
    Assert.Equal(TimeSpan.FromMilliseconds(5), Assert.IsType<WaitStep>(document.Steps[1]).Duration);
    Assert.Equal(KeyActionKind.Up, Assert.IsType<KeyStep>(document.Steps[2]).Kind);
    Assert.IsType<MouseMoveStep>(document.Steps[3]);
    Assert.IsType<WaitStep>(document.Steps[4]);
    var repeat = Assert.IsType<RepeatStep>(document.Steps[5]);
    Assert.Equal(ButtonActionKind.Down, Assert.IsType<MouseButtonStep>(repeat.Steps[0]).Kind);
    Assert.Equal(ButtonActionKind.Up, Assert.IsType<MouseButtonStep>(repeat.Steps[1]).Kind);
    Assert.IsType<PixelWhenStep>(document.Steps[6]);

    var key = (KeyStep)document.Steps[0];
    Assert.Equal(HidKey.A, key.Key);
    Assert.Equal(HidModifier.LeftCtrl, key.Modifiers);
    Assert.Equal(TimeSpan.Zero, key.Hold);

    var pixel = (PixelWhenStep)document.Steps[6];
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

static void PlaybackDelayStrategyUsesHighPrecisionShortWaitPath()
{
    var playbackCode = File.ReadAllText(Path.Combine("src", "shared", "MacroHid.Runtime", "MacroPlaybackExecutor.cs"));

    Assert.Contains("private const long SleepOneThresholdUs = 8_000", playbackCode);
    Assert.Contains("private const long YieldThresholdUs = 1_500", playbackCode);
    Assert.Contains("Thread.Sleep(0)", playbackCode);
    Assert.Contains("Thread.SpinWait(calibratedSpinIterations * 4)", playbackCode);
    Assert.Contains("ThreadPriority.Highest", playbackCode);
}

static void PlaybackRuntimeExposesPrecisionContextCompiledPlansAndJitterStats()
{
    var runtimeDir = Path.Combine("src", "shared", "MacroHid.Runtime");
    var playbackCode = File.ReadAllText(Path.Combine(runtimeDir, "MacroPlaybackExecutor.cs"));
    var optionsCode = File.ReadAllText(Path.Combine(runtimeDir, "MacroPlaybackController.cs"));
    var planPath = Path.Combine(runtimeDir, "CompiledPlaybackPlan.cs");
    var precisionPath = Path.Combine(runtimeDir, "PrecisionPlaybackContext.cs");
    var clockPath = Path.Combine(runtimeDir, "HighResolutionClock.cs");

    Assert.True(File.Exists(planPath));
    Assert.True(File.Exists(precisionPath));
    Assert.True(File.Exists(clockPath));

    var planCode = File.ReadAllText(planPath);
    var precisionCode = File.ReadAllText(precisionPath);
    var clockCode = File.ReadAllText(clockPath);

    Assert.Contains("PrecisionMode Precision", optionsCode);
    Assert.Contains("PrecisionMode.ExtremeDuringPlayback", optionsCode);
    Assert.Contains("PrecisionPlaybackContext.Enter(options.Precision)", playbackCode);
    Assert.Contains("CompiledPlaybackPlan.Create", playbackCode);
    Assert.Contains("plan.RequiresResampling", playbackCode);
    Assert.Contains("PlaybackTimingStats", playbackCode);
    Assert.Contains("RecordJitter", playbackCode);

    Assert.Contains("sealed class CompiledPlaybackPlan", planCode);
    Assert.Contains("PreparedInputBatch", planCode);
    Assert.Contains("RequiresResampling", planCode);
    Assert.Contains("Resample", planCode);

    Assert.Contains("ThreadPriorityTimeCritical", precisionCode);
    Assert.Contains("SetThreadPriority", precisionCode);
    Assert.Contains("ProcessPriorityClass.High", precisionCode);
    Assert.Contains("GC.TryStartNoGCRegion", precisionCode);
    Assert.Contains("SetThreadIdealProcessor", precisionCode);

    Assert.Contains("interface IHighResolutionClock", clockCode);
    Assert.Contains("QueryPerformanceCounter", clockCode);
    Assert.Contains("QueryPerformanceFrequency", clockCode);
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
    Assert.Equal(7, scheduled.Count);
}

static void InputActionCompilerSamplesRandomWaitsForEachCompilation()
{
    var document = new MacroDocument(
        1,
        "random-delay",
        PlaybackSettings.Default,
        [
            new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero),
            new WaitStep(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(5)),
            new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)
        ]);

    var first = InputActionCompiler.Compile(
        document,
        startTick: 0,
        qpcFrequency: 1_000,
        waitDurationSampler: wait => wait.MinDuration + TimeSpan.FromMilliseconds(1));
    var second = InputActionCompiler.Compile(
        document,
        startTick: 0,
        qpcFrequency: 1_000,
        waitDurationSampler: wait => wait.MaxDuration ?? wait.Duration);

    Assert.Equal(2, first.Count);
    Assert.Equal(2, second.Count);
    Assert.Equal(3, first[1].DueTick);
    Assert.Equal(5, second[1].DueTick);
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

    Assert.Equal(3, document.Steps.Count);
    var wheel = Assert.IsType<MouseWheelStep>(document.Steps[0]);
    Assert.Equal(-1, wheel.Vertical);
    Assert.Equal(2, wheel.Horizontal);

    var consumer = Assert.IsType<ConsumerStep>(document.Steps[1]);
    Assert.Equal(ConsumerControl.VolumeUp, consumer.Control);
    Assert.Equal(ButtonActionKind.Down, consumer.Kind);
    Assert.Equal(ButtonActionKind.Up, Assert.IsType<ConsumerStep>(document.Steps[2]).Kind);

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

static void McrxParserCoversMouseButtonCoordinates()
{
    const string json = """
    {
      "version": 1,
      "name": "mouse-coordinate",
      "steps": [
        { "type": "mouse.down", "button": "Left", "mode": "absolute", "x": 320, "y": 240 },
        { "type": "mouse.up", "button": "Left" }
      ]
    }
    """;

    var document = McrxParser.Parse(json);
    var down = Assert.IsType<MouseButtonStep>(document.Steps[0]);

    Assert.Equal(MouseMoveMode.Absolute, down.CoordinateMode);
    Assert.Equal(320, down.X);
    Assert.Equal(240, down.Y);

    var serialized = McrxSerializer.Serialize(document);
    Assert.Contains("\"mode\": \"absolute\"", serialized);
    Assert.Contains("\"x\": 320", serialized);
    Assert.Contains("\"y\": 240", serialized);

    var actions = InputActionCompiler.Compile(document, startTick: 1_000, qpcFrequency: 1_000);

    Assert.Equal(3, actions.Count);
    Assert.Equal(1_000, actions[0].DueTick);
    Assert.Equal(new MouseMoveInputAction(MouseMoveMode.Absolute, 320, 240), actions[0].Action);
    Assert.Equal(new MouseButtonInputAction(MouseButton.Left, ButtonActionKind.Down), actions[1].Action);
    Assert.Equal(new MouseButtonInputAction(MouseButton.Left, ButtonActionKind.Up), actions[2].Action);
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
    var keyUp = Assert.IsType<KeyStep>(document.Steps[2]);
    var waitAfterKey = Assert.IsType<WaitStep>(document.Steps[3]);
    var move = Assert.IsType<MouseMoveStep>(document.Steps[4]);

    Assert.Equal(KeyActionKind.Down, key.Kind);
    Assert.Equal(TimeSpan.FromTicks(7_500), wait.Duration);
    Assert.Equal(KeyActionKind.Up, keyUp.Kind);
    Assert.Equal(TimeSpan.FromTicks(5_000), waitAfterKey.Duration);
    Assert.Equal(TimeSpan.FromTicks(12_500), move.Duration);

    var serialized = McrxSerializer.Serialize(document);
    Assert.False(serialized.Contains("\"holdMs\": 0.75", StringComparison.Ordinal));
    Assert.Contains("\"ms\": 0.75", serialized);
    Assert.Contains("\"ms\": 0.5", serialized);
    Assert.Contains("\"durationMs\": 1.25", serialized);
}

static void McrxParserExpandsTapAndClickStepsIntoPressReleaseSteps()
{
    const string json = """
    {
      "version": 1,
      "name": "press-release",
      "steps": [
        { "type": "key.tap", "key": "A", "holdMs": 0.75 },
        { "type": "mouse.click", "button": "Left", "holdMs": 1.25 },
        { "type": "consumer.tap", "control": "VolumeUp", "holdMs": 0.5 },
        {
          "type": "repeat",
          "count": 2,
          "steps": [
            { "type": "mouse.click", "button": "Right" }
          ]
        }
      ]
    }
    """;

    var document = McrxParser.Parse(json);

    Assert.Equal(10, document.Steps.Count);
    Assert.Equal(KeyActionKind.Down, Assert.IsType<KeyStep>(document.Steps[0]).Kind);
    Assert.Equal(TimeSpan.FromTicks(7_500), Assert.IsType<WaitStep>(document.Steps[1]).Duration);
    Assert.Equal(KeyActionKind.Up, Assert.IsType<KeyStep>(document.Steps[2]).Kind);
    Assert.Equal(ButtonActionKind.Down, Assert.IsType<MouseButtonStep>(document.Steps[3]).Kind);
    Assert.Equal(TimeSpan.FromTicks(12_500), Assert.IsType<WaitStep>(document.Steps[4]).Duration);
    Assert.Equal(ButtonActionKind.Up, Assert.IsType<MouseButtonStep>(document.Steps[5]).Kind);
    Assert.Equal(ButtonActionKind.Down, Assert.IsType<ConsumerStep>(document.Steps[6]).Kind);
    Assert.Equal(TimeSpan.FromTicks(5_000), Assert.IsType<WaitStep>(document.Steps[7]).Duration);
    Assert.Equal(ButtonActionKind.Up, Assert.IsType<ConsumerStep>(document.Steps[8]).Kind);

    var repeat = Assert.IsType<RepeatStep>(document.Steps[9]);
    Assert.Equal(2, repeat.Count);
    Assert.Equal(ButtonActionKind.Down, Assert.IsType<MouseButtonStep>(repeat.Steps[0]).Kind);
    Assert.Equal(ButtonActionKind.Up, Assert.IsType<MouseButtonStep>(repeat.Steps[1]).Kind);

    var serialized = McrxSerializer.Serialize(new MacroDocument(
        1,
        "serialize",
        [
            new MouseButtonStep(MouseButton.Left, ButtonActionKind.Click, TimeSpan.FromMilliseconds(1)),
            new KeyStep(KeyActionKind.Tap, HidKey.B, HidModifier.None, TimeSpan.FromMilliseconds(1))
        ]));
    Assert.False(serialized.Contains("mouse.click", StringComparison.Ordinal));
    Assert.False(serialized.Contains("key.tap", StringComparison.Ordinal));
    Assert.Contains("\"type\": \"mouse.down\"", serialized);
    Assert.Contains("\"type\": \"mouse.up\"", serialized);
    Assert.Contains("\"type\": \"key.down\"", serialized);
    Assert.Contains("\"type\": \"key.up\"", serialized);
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

static void McrxParserCoversConditionalDirectiveTimeWindows()
{
    const string json = """
    {
      "version": 1,
      "name": "conditional",
      "steps": [
        { "type": "mouse.down", "button": "Left" },
        { "type": "mouse.up", "button": "Left" }
      ],
      "conditions": [
        {
          "id": "c1",
          "name": "red window",
          "startStep": 0,
          "endStep": 1,
          "windowStartMs": 3000,
          "windowEndMs": 5000,
          "pollMs": 10,
          "type": "pixel",
          "x": 8,
          "y": 9,
          "r": 255,
          "g": 0,
          "b": 0
        }
      ]
    }
    """;

    var document = McrxParser.Parse(json);

    Assert.Equal(1, document.EffectiveConditions.Count);
    var condition = document.EffectiveConditions[0];
    Assert.Equal(TimeSpan.FromSeconds(3), condition.WindowStart);
    Assert.Equal(TimeSpan.FromSeconds(5), condition.WindowEnd);

    var serialized = McrxSerializer.Serialize(document);
    Assert.Contains("\"windowStartMs\": 3000", serialized);
    Assert.Contains("\"windowEndMs\": 5000", serialized);
}

static void McrxParserCoversRandomWaitsAndConditionalDirectivePaths()
{
    const string json = """
    {
      "version": 1,
      "name": "path-condition",
      "steps": [
        { "type": "repeat", "count": 2, "steps": [
          { "type": "wait", "minMs": 1.25, "maxMs": 3.5 },
          { "type": "key.down", "key": "A" }
        ] }
      ],
      "conditions": [
        {
          "id": "c1",
          "name": "nested",
          "startStep": 0,
          "endStep": 0,
          "startPath": "0.0",
          "endPath": "0.1",
          "type": "pixel",
          "x": 1,
          "y": 2,
          "r": 3,
          "g": 4,
          "b": 5
        }
      ]
    }
    """;

    var document = McrxParser.Parse(json);
    var repeat = Assert.IsType<RepeatStep>(document.Steps[0]);
    var wait = Assert.IsType<WaitStep>(repeat.Steps[0]);
    Assert.Equal(TimeSpan.FromTicks(12_500), wait.MinDuration);
    Assert.Equal(TimeSpan.FromTicks(35_000), wait.MaxDuration);

    var condition = document.EffectiveConditions[0];
    Assert.Equal("0.0", condition.StartStepPathText);
    Assert.Equal("0.1", condition.EndStepPathText);

    var serialized = McrxSerializer.Serialize(document);
    Assert.Contains("\"minMs\": 1.25", serialized);
    Assert.Contains("\"maxMs\": 3.5", serialized);
    Assert.Contains("\"startPath\": \"0.0\"", serialized);
    Assert.Contains("\"endPath\": \"0.1\"", serialized);
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
        "count": 3,
        "processFilter": "notepad.exe; chrome"
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
    Assert.Equal("notepad.exe; chrome", document.Playback.ProcessFilter);

    var serialized = McrxSerializer.Serialize(document);
    Assert.Contains("\"processFilter\": \"notepad.exe; chrome\"", serialized);
}

static void McrxParserCoversPlaybackPrecisionMode()
{
    const string json = """
    {
      "version": 1,
      "name": "precision-playback",
      "playback": {
        "mode": "fixedCount",
        "precision": "balanced"
      },
      "steps": [
        { "type": "wait", "ms": 0.5 }
      ]
    }
    """;

    var document = McrxParser.Parse(json);
    Assert.Equal(PrecisionMode.Balanced, document.Playback.Precision);

    var oldDocument = McrxParser.Parse("""{ "version": 1, "name": "old", "steps": [] }""");
    Assert.Equal(PrecisionMode.ExtremeDuringPlayback, oldDocument.Playback.Precision);
    Assert.Equal(PrecisionMode.ExtremeDuringPlayback, PlaybackSettings.Default.Precision);

    var serialized = McrxSerializer.Serialize(document);
    Assert.Contains("\"precision\": \"balanced\"", serialized);
}

static void PlaybackProcessFilterMatchesForegroundProcessNames()
{
    Assert.True(PlaybackProcessFilter.Matches(null, "notepad"));
    Assert.True(PlaybackProcessFilter.Matches("", "notepad"));
    Assert.True(PlaybackProcessFilter.Matches("notepad", "notepad.exe"));
    Assert.True(PlaybackProcessFilter.Matches("notepad.exe; chrome", "CHROME"));
    Assert.True(PlaybackProcessFilter.Matches("game, editor.exe", "editor.exe"));
    Assert.False(PlaybackProcessFilter.Matches("notepad", "chrome"));
    Assert.False(PlaybackProcessFilter.Matches("notepad", null));
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

static void PreparedSendInputBatchesPreserveEncoderOutput()
{
    InputAction[] actions =
    [
        new KeyInputAction(KeyActionKind.Down, HidKey.A, HidModifier.LeftCtrl),
        new MouseWheelInputAction(-1, 0, MouseButton.None),
        new TextInputAction("中")
    ];

    var prepared = PreparedInputBatch.FromActions(actions);
    var expectedPackets = actions.SelectMany(action => SendInputEncoder.Encode(action)).ToArray();

    Assert.Equal(actions.Length, prepared.ActionCount);
    Assert.Equal(expectedPackets.Length, prepared.NativeInputCount);
    Assert.Equal(expectedPackets.Length, prepared.Packets.Count);
    for (var i = 0; i < expectedPackets.Length; i++)
    {
        Assert.Equal(expectedPackets[i], prepared.Packets[i]);
    }
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
    Assert.Equal("Relative", LocalizationService.Get("MoveModeRelative", new CultureInfo("en-US")));
    Assert.Equal("相对", LocalizationService.Get("MoveModeRelative", new CultureInfo("zh-CN")));
    Assert.Equal("相對", LocalizationService.Get("MoveModeRelative", new CultureInfo("zh-TW")));
    Assert.Equal("Absolute", LocalizationService.Get("MoveModeAbsolute", new CultureInfo("en-US")));
    Assert.Equal("绝对", LocalizationService.Get("MoveModeAbsolute", new CultureInfo("zh-CN")));
    Assert.Equal("絕對", LocalizationService.Get("MoveModeAbsolute", new CultureInfo("zh-TW")));
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
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var stepEditorXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml"));
    var lightThemeXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Themes", "LightTheme.xaml"));

    Assert.Contains("WindowStyle=\"None\"", xaml);
    Assert.Contains("WindowChrome.WindowChrome", xaml);
    Assert.Contains("MinimizeWindowButton", xaml);
    Assert.Contains("MaximizeWindowButton", xaml);
    Assert.Contains("CloseWindowButton", xaml);
    Assert.DoesNotContain("StepEditorPanel", xaml);
    Assert.Contains("InlineStepEditorPopup", stepSequenceXaml);
    Assert.Contains("MacroTargetBox", stepEditorXaml);
    Assert.Contains("PickPixelColorButton", stepEditorXaml);
    Assert.Contains("Color=\"#EFF3F8\"", lightThemeXaml);
    Assert.Contains("TopChromeBar_MouseLeftButtonDown", codeBehind);
    Assert.Contains("ToggleMaximize", codeBehind);
}

static void MacroStudioUsesLauncherStyleSoftWorkbenchShell()
{
    var mainWindowXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml"));
    var lightThemeXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Themes", "LightTheme.xaml"));
    var darkThemeXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Themes", "DarkTheme.xaml"));
    var sharedStyles = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Themes", "SharedStyles.xaml"));
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var libraryXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "MacroLibraryPanel.xaml"));

    Assert.Contains("x:Name=\"AppShellRoot\"", mainWindowXaml);
    Assert.Contains("x:Name=\"CommandBar\"", mainWindowXaml);
    Assert.Contains("x:Name=\"WorkbenchGrid\"", mainWindowXaml);
    Assert.Contains("x:Name=\"InspectorPanel\"", mainWindowXaml);
    Assert.Contains("Height=\"600\"", mainWindowXaml);
    Assert.Contains("MinHeight=\"560\"", mainWindowXaml);
    Assert.Contains("Width=\"1120\"", mainWindowXaml);
    Assert.Contains("MinWidth=\"1040\"", mainWindowXaml);
    Assert.Contains("x:Name=\"WorkbenchGrid\" Margin=\"14,10,14,14\" ClipToBounds=\"True\"", mainWindowXaml);
    Assert.Contains("ColumnDefinition Width=\"240\" MinWidth=\"200\"", mainWindowXaml);
    Assert.Contains("ColumnDefinition Width=\"*\" MinWidth=\"500\"", mainWindowXaml);
    Assert.Contains("ColumnDefinition Width=\"420\" MinWidth=\"340\"", mainWindowXaml);
    Assert.Contains("<controls:SmoothGridSplitter Grid.Column=\"1\"", mainWindowXaml);
    Assert.Contains("<controls:SmoothGridSplitter Grid.Column=\"3\"", mainWindowXaml);
    Assert.DoesNotContain("Width=\"250\"", mainWindowXaml);
    Assert.Contains("AutomationProperties.AutomationId=\"ConditionSequenceColumn\"", mainWindowXaml);
    Assert.Contains("AutomationProperties.AutomationId=\"SequencePanelControl\"", mainWindowXaml);
    Assert.Contains("Grid.Column=\"4\"", mainWindowXaml);
    Assert.Contains("Background=\"{DynamicResource InspectorBackground}\"", mainWindowXaml);
    Assert.Contains("Panel.ZIndex=\"10\"", mainWindowXaml);
    Assert.Contains("<ContentControl x:Name=\"SequencePanelHost\" Grid.Column=\"2\">", mainWindowXaml);
    Assert.Contains("<Border ClipToBounds=\"True\">", mainWindowXaml);
    Assert.Contains("ClipToBounds=\"True\"", mainWindowXaml);
    Assert.Contains("<controls:ConditionDirectivePanel x:Name=\"ConditionPanel\"", mainWindowXaml);
    Assert.DoesNotContain("Grid.Column=\"6\"", mainWindowXaml);

    Assert.Contains("Color=\"#EFF3F8\"", lightThemeXaml);
    Assert.Contains("Color=\"#F7F8FB\"", lightThemeXaml);
    Assert.Contains("Color=\"#EEF2F6\"", lightThemeXaml);
    Assert.Contains("Color=\"#1F232A\"", darkThemeXaml);
    Assert.Contains("Color=\"#2B3038\"", darkThemeXaml);

    Assert.Contains("Style x:Key=\"CommandBarButton\"", sharedStyles);
    Assert.Contains("Style TargetType=\"{x:Type TreeView}\"", sharedStyles);
    Assert.Contains("Style TargetType=\"{x:Type TreeViewItem}\"", sharedStyles);
    Assert.Contains("Style x:Key=\"SoftListItemBorder\"", sharedStyles);
    Assert.Contains("CornerRadius=\"14\"", sharedStyles);
    Assert.Contains("ShadowDepth=\"0\"", sharedStyles);

    Assert.Contains("Style=\"{StaticResource SoftListItemBorder}\"", stepSequenceXaml);
    Assert.Contains("Style=\"{StaticResource SidebarPanelBorderStyle}\"", libraryXaml);
}

static void MacroStudioLaysOutBaseAndConditionalSequencesSideBySide()
{
    var mainWindowXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml"));
    var conditionXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml"));
    var conditionCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml.cs"));

    Assert.Contains("AutomationProperties.AutomationId=\"ConditionSequenceColumn\"", mainWindowXaml);
    Assert.Contains("<controls:SequencePanel x:Name=\"SequencePanelControl\"", mainWindowXaml);
    Assert.Contains("<controls:ConditionDirectivePanel x:Name=\"ConditionPanel\"", mainWindowXaml);
    Assert.Contains("Header=\"条件序列\"", mainWindowXaml);
    Assert.DoesNotContain("Header=\"条件\" IsExpanded=\"False\"", mainWindowXaml);
    Assert.True(mainWindowXaml.IndexOf("SequencePanelControl", StringComparison.Ordinal) < mainWindowXaml.IndexOf("ConditionPanel", StringComparison.Ordinal));

    Assert.Contains("x:Name=\"ConditionSequenceTitleText\"", conditionXaml);
    Assert.Contains("Text=\"条件列表\"", conditionXaml);
    Assert.Contains("x:Name=\"EmptyConditionHintText\"", conditionXaml);
    Assert.Contains("暂无条件，点击 + 添加", conditionXaml);
    Assert.Contains("x:Name=\"ThenActionSequence\"", conditionXaml);
    Assert.Contains("x:Name=\"EditorBorder\"", conditionXaml);
    Assert.Contains("StepSequencePanel", conditionXaml);
    Assert.Contains("Text=\"触发后执行\"", conditionXaml);
    Assert.Contains("OnThenActionSequenceStepsChanged", conditionCode);
    Assert.Contains("EditorBorder.Visibility", conditionCode);
    Assert.Contains("ThenActionSequence.SetSteps", conditionCode);
    Assert.Contains("ConditionList.SelectedIndex = 0", conditionCode);
}

static void MacroStudioOpensConditionThenActionEditor()
{
    var conditionXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml"));
    var conditionCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml.cs"));
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));

    Assert.Contains("<local:StepSequencePanel x:Name=\"ThenActionSequence\"", conditionXaml);
    Assert.Contains("ThenActionSequence.StepsChanged += OnThenActionSequenceStepsChanged", conditionCode);
    Assert.Contains("ThenSteps = ThenActionSequence.Steps.ToList()", conditionCode);
    Assert.Contains("OnThenActionTemplateDropped", conditionCode);
    Assert.Contains("OnThenMacroLibraryDropped", conditionCode);
    Assert.Contains("StepEditorPanel", stepSequenceXaml);
    Assert.Contains("MacroActionTemplateFactory.CreateSteps", conditionCode);
    Assert.Contains("MacroStepTreeEditor.ReplaceAtPath", stepSequenceCode);
    Assert.Contains("BuildEditedStep", stepSequenceCode);
    Assert.DoesNotContain("new ThenActionsEditorWindow", conditionCode);
}

static void MacroStudioExposesInlineStepEditingAndConditionRangeControls()
{
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));
    var conditionXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml"));

    Assert.Contains("StepTitle_MouseLeftButtonUp", stepSequenceXaml);
    Assert.Contains("StepList_PreviewMouseMove", stepSequenceXaml);
    Assert.Contains("StepDragFormat", stepSequenceCode);
    Assert.Contains("OpenInlineStepEditor", stepSequenceCode);
    Assert.Contains("InlineStepEditor.BuildEditedStep", stepSequenceCode);
    Assert.Contains("StartStepCombo", conditionXaml);
    Assert.Contains("EndStepCombo", conditionXaml);
    Assert.Contains("WindowStartMsBox", conditionXaml);
    Assert.Contains("WindowEndMsBox", conditionXaml);
}

static void MacroStudioValidatesConditionTimeWindowsAndPersistsPathRanges()
{
    var conditionXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml"));
    var conditionCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml.cs"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));

    Assert.Contains("SelectedValuePath=\"PathText\"", conditionXaml);
    Assert.Contains("StepChoiceLabelTemplate", conditionXaml);
    Assert.Contains("ItemTemplate=\"{StaticResource StepChoiceLabelTemplate}\"", conditionXaml);
    Assert.Contains("TryReadTimeWindow", conditionCode);
    Assert.Contains("SetTimeWindowValidity(false)", conditionCode);
    Assert.Contains("StartStepPath", conditionCode);
    Assert.Contains("EndStepPath", conditionCode);
    Assert.Contains("PathText", conditionCode);
    Assert.Contains("StepDisplayItem.FlattenSteps(steps, macroNameResolver: ResolveMacroDisplayName)", stepSequenceCode);
    Assert.Contains("public override string ToString() => Label;", stepSequenceCode);
    Assert.Contains("ConditionRangeHighlight", stepSequenceCode);
}

static void MacroStudioSuppressesConditionRangeEventsWhileRefreshingStepChoices()
{
    var conditionCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml.cs"));
    var setStepChoicesIndex = conditionCode.IndexOf("public void SetStepChoices", StringComparison.Ordinal);
    var nextMethodIndex = conditionCode.IndexOf("public void LoadConditions", StringComparison.Ordinal);
    Assert.True(setStepChoicesIndex >= 0);
    Assert.True(nextMethodIndex > setStepChoicesIndex);

    var setStepChoicesBody = conditionCode[setStepChoicesIndex..nextMethodIndex];
    Assert.Contains("var previousLoadingEditor = loadingEditor", setStepChoicesBody);
    Assert.Contains("loadingEditor = true", setStepChoicesBody);
    Assert.Contains("finally", setStepChoicesBody);
    Assert.Contains("loadingEditor = previousLoadingEditor", setStepChoicesBody);
}

static void MacroStudioLetsConditionSequencePickPixelColors()
{
    var conditionXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml"));
    var conditionCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml.cs"));
    var samplerCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ScreenPixelSampler.cs"));

    Assert.Contains("PickConditionColorButton", conditionXaml);
    Assert.Contains("Content=\"取色\"", conditionXaml);
    Assert.Contains("Click=\"PickConditionColor_Click\"", conditionXaml);
    Assert.Contains("PickConditionColor_Click", conditionCode);
    Assert.Contains("new ScreenCoordinatePickerWindow", conditionCode);
    Assert.Contains("ScreenPixelSampler.TryReadPixel", conditionCode);
    Assert.Contains("ColorRBox.Text", conditionCode);
    Assert.Contains("ColorGBox.Text", conditionCode);
    Assert.Contains("ColorBBox.Text", conditionCode);
    Assert.Contains("ColorPreview.Background", conditionCode);
    Assert.Contains("ScreenRegion.FromSinglePixel", conditionCode);
    Assert.Contains("ConditionsModified?.Invoke", conditionCode);
    Assert.Contains("GetPixel", samplerCode);
}

static void MacroStudioExposesMouseButtonCoordinateEditor()
{
    var stepEditorXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml"));
    var stepEditorCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml.cs"));
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));
    var pickerXamlPath = Path.Combine("src", "ui", "MacroStudio", "Controls", "ScreenCoordinatePickerWindow.xaml");
    var pickerCodePath = Path.Combine("src", "ui", "MacroStudio", "Controls", "ScreenCoordinatePickerWindow.xaml.cs");

    Assert.Contains("MouseButtonCoordinateEnabledBox", stepEditorXaml);
    Assert.Contains("MouseButtonCoordinateModeBox", stepEditorXaml);
    Assert.Contains("MouseButtonXBox", stepEditorXaml);
    Assert.Contains("MouseButtonYBox", stepEditorXaml);
    Assert.Contains("PickMouseButtonCoordinateButton", stepEditorXaml);
    Assert.Contains("RefreshEnumBoxLocalization(MouseButtonCoordinateModeBox)", stepEditorCode);
    Assert.Contains("RefreshEnumBoxLocalization(MoveModeBox)", stepEditorCode);
    Assert.Contains("GetEnumLabel", stepEditorCode);
    Assert.DoesNotContain("Content = value.ToString()", stepEditorCode);
    Assert.DoesNotContain("取当前鼠标位置", stepEditorXaml);
    Assert.Contains("选择屏幕坐标", stepEditorXaml);
    Assert.Contains("button.CoordinateMode", stepEditorCode);
    Assert.Contains("MouseButtonXBox.Text", stepEditorCode);
    Assert.Contains("PickMouseButtonCoordinate_Click", stepEditorCode);
    Assert.Contains("new ScreenCoordinatePickerWindow", stepEditorCode);
    Assert.Contains("CoordinatePickerStarted", stepEditorCode);
    Assert.Contains("CoordinatePickerFinished", stepEditorCode);
    Assert.Contains("ShowDialog() == true", stepEditorCode);
    Assert.Contains("picker.SelectedX", stepEditorCode);
    Assert.Contains("picker.SelectedY", stepEditorCode);
    Assert.Contains("InlineStepEditor.CoordinatePickerStarted", stepSequenceCode);
    Assert.Contains("InlineStepEditor.CoordinatePickerFinished", stepSequenceCode);
    Assert.Contains("StaysOpen=\"True\"", stepSequenceXaml);
    Assert.Contains("inlineCoordinatePickerActive", stepSequenceCode);
    Assert.Contains("InlineStepEditorPopup.IsOpen = true", stepSequenceCode);

    Assert.True(File.Exists(pickerXamlPath));
    Assert.True(File.Exists(pickerCodePath));
    var pickerXaml = File.ReadAllText(pickerXamlPath);
    var pickerCode = File.ReadAllText(pickerCodePath);
    Assert.Contains("WindowStyle=\"None\"", pickerXaml);
    Assert.Contains("Topmost=\"True\"", pickerXaml);
    Assert.Contains("PreviewMouseLeftButtonDown", pickerXaml);
    Assert.Contains("KeyDown", pickerXaml);
    Assert.Contains("SelectedX", pickerCode);
    Assert.Contains("SelectedY", pickerCode);
    Assert.Contains("Key.Escape", pickerCode);
    Assert.Contains("GetCursorPos", pickerCode);
}

static void MacroStudioKeepsStepEditorComboBoxValuesSelectable()
{
    var sharedStylesXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Themes", "SharedStyles.xaml"));
    var stepEditorXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml"));
    var stepEditorCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml.cs"));

    Assert.Contains("x:Name=\"PART_EditableTextBox\"", sharedStylesXaml);
    Assert.Contains("Property=\"IsEditable\" Value=\"True\"", sharedStylesXaml);
    Assert.Contains("TargetName=\"ContentSite\" Property=\"Visibility\" Value=\"Collapsed\"", sharedStylesXaml);
    Assert.Contains("TargetName=\"PART_EditableTextBox\" Property=\"Visibility\" Value=\"Visible\"", sharedStylesXaml);
    Assert.Contains("ActionKindBox", stepEditorXaml);
    Assert.Contains("MouseButtonBox", stepEditorXaml);
    Assert.Contains("MouseButtonCoordinateModeBox", stepEditorXaml);
    Assert.Contains("MoveModeBox", stepEditorXaml);
    Assert.Contains("comboBox.Text = item.Content?.ToString()", stepEditorCode);
    Assert.Contains("ResolveComboBoxEnumFromText<TEnum>", stepEditorCode);
}

static void MacroStudioExposesUndoForLastSequenceEdit()
{
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var sequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "SequencePanel.xaml.cs"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));
    var english = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.resx"));
    var simplified = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.zh-CN.resx"));
    var traditional = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.zh-TW.resx"));

    Assert.Contains("StepUndoButton", stepSequenceXaml);
    Assert.Contains("UndoLastChange", sequenceCode);
    Assert.Contains("CaptureUndoSnapshot", sequenceCode);
    Assert.Contains("UndoApplied", sequenceCode);
    Assert.Contains("OnSequenceUndoApplied", mainWindowCode);
    Assert.Contains("name=\"Undo\"", english);
    Assert.Contains("<value>Undo</value>", english);
    Assert.Contains("name=\"Undo\"", simplified);
    Assert.Contains("<value>撤回</value>", simplified);
    Assert.Contains("name=\"Undo\"", traditional);
    Assert.Contains("<value>撤回</value>", traditional);
}

static void MacroStudioGivesEverySequenceRowInlineActionsAndStyledMenus()
{
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));
    var sequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "SequencePanel.xaml.cs"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));
    var sharedStyles = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Themes", "SharedStyles.xaml"));
    var english = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.resx"));
    var simplified = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.zh-CN.resx"));
    var traditional = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.zh-TW.resx"));

    Assert.Contains("StepRow_MouseLeftButtonUp", stepSequenceXaml);
    Assert.Contains("StepCopyButton", stepSequenceXaml);
    Assert.Contains("StepRowDeleteButton", stepSequenceXaml);
    Assert.Contains("StepRowActionButton", stepSequenceXaml);
    Assert.Contains("StepCopy_Click", stepSequenceCode);
    Assert.Contains("StepRowDelete_Click", stepSequenceCode);
    Assert.Contains("DuplicateStepAtIndex", stepSequenceCode);
    Assert.Contains("DocumentEdited", sequenceCode);
    Assert.Contains("OnSequenceDocumentEdited", mainWindowCode);
    Assert.Contains("Style TargetType=\"{x:Type ContextMenu}\"", sharedStyles);
    Assert.Contains("Style TargetType=\"{x:Type MenuItem}\"", sharedStyles);
    Assert.Contains("CornerRadius=\"12\"", sharedStyles);
    Assert.Contains("name=\"Copy\"", english);
    Assert.Contains("<value>Copy</value>", english);
    Assert.Contains("name=\"Copy\"", simplified);
    Assert.Contains("<value>复制</value>", simplified);
    Assert.Contains("name=\"Copy\"", traditional);
    Assert.Contains("<value>複製</value>", traditional);
}

static void MacroStudioSupportsUndoShortcutAndClearingWholeSequence()
{
    var mainWindowXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var sequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "SequencePanel.xaml.cs"));
    var english = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.resx"));
    var simplified = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.zh-CN.resx"));
    var traditional = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.zh-TW.resx"));

    Assert.Contains("PreviewKeyDown=\"MainWindow_PreviewKeyDown\"", mainWindowXaml);
    Assert.Contains("MainWindow_PreviewKeyDown", mainWindowCode);
    Assert.Contains("UndoLastChange", mainWindowCode);
    Assert.Contains("ClearAllSteps", mainWindowCode);
    Assert.Contains("StepClearButton", stepSequenceXaml);
    Assert.Contains("ClearAllSteps", sequenceCode);
    Assert.Contains("name=\"ClearAll\"", english);
    Assert.Contains("<value>Clear All</value>", english);
    Assert.Contains("name=\"ClearAll\"", simplified);
    Assert.Contains("<value>清空</value>", simplified);
    Assert.Contains("name=\"ClearAll\"", traditional);
    Assert.Contains("<value>清空</value>", traditional);
}

static void MacroStudioSupportsMacroCallSelectionAndPlaybackAutosave()
{
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));
    var stepEditorXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml"));
    var stepEditorCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml.cs"));
    var playbackXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "PlaybackPanel.xaml"));
    var playbackCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "PlaybackPanel.xaml.cs"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));

    Assert.Contains("OpenInlineStepEditor", stepSequenceCode);
    Assert.Contains("case MacroCallStep", stepEditorCode);
    Assert.Contains("MacroTargetBox", stepEditorXaml);
    Assert.Contains("RefreshMacroTargetBox", stepEditorCode);
    Assert.Contains("PlaybackSettings_TextChanged", playbackXaml);
    Assert.Contains("ProcessFilterBox", playbackXaml);
    Assert.Contains("ProcessFilterText", playbackCode);
    Assert.Contains("settings.ProcessFilter", playbackCode);
    Assert.Contains("PlaybackSettingsEdited", playbackCode);
    Assert.Contains("OnPlaybackSettingsEdited", mainWindowCode);
    Assert.Contains("AutoSaveCurrentMacro", mainWindowCode);
    Assert.Contains("libraryStore.SaveMacro", mainWindowCode);
}

static void MacroStudioSupportsMultipleHotkeyListenersAndLibraryTriggerSummaries()
{
    var hookCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "GlobalKeyboardHook.cs"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));
    var displayModels = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "DisplayModels.cs"));
    var libraryPanelCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "MacroLibraryPanel.xaml.cs"));

    Assert.Contains("HotkeyBinding", hookCode);
    Assert.Contains("HotkeyTriggeredEventArgs", hookCode);
    Assert.Contains("Start(IEnumerable<HotkeyBinding>", hookCode);
    Assert.Contains("triggersDown", hookCode);
    Assert.Contains("listeningControllers", mainWindowCode);
    Assert.Contains("keyboardHook.Start(bindings", mainWindowCode);
    Assert.Contains("TriggerPressed += KeyboardHook_TriggerPressed", mainWindowCode);
    Assert.Contains("listeningProcessFilters", mainWindowCode);
    Assert.Contains("ForegroundProcessService.GetForegroundProcessName", mainWindowCode);
    Assert.Contains("PlaybackProcessFilter.Matches", mainWindowCode);
    Assert.Contains("FormatPlaybackMode", displayModels);
    Assert.Contains("ProcessFilter", displayModels);
    Assert.Contains("public static MacroLibraryTreeNode Macro", displayModels);
    Assert.Contains("CreateMacroNode", libraryPanelCode);
}

static void MacroStudioDropInsertionUsesTargetRowAndSuppressesDragClickDuplication()
{
    var actionPaletteXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ActionPalettePanel.xaml"));
    var actionPaletteCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ActionPalettePanel.xaml.cs"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));

    Assert.Contains("PreviewMouseLeftButtonDown=\"ActionPalette_PreviewMouseLeftButtonDown\"", actionPaletteXaml);
    Assert.Contains("paletteDragStarted", actionPaletteCode);
    Assert.Contains("e.Handled = true", actionPaletteCode);
    Assert.Contains("ActionTemplateDropped?.Invoke(kind, target.ParentPathText, target.InsertIndex)", stepSequenceCode);
    Assert.Contains("MacroLibraryDropped?.Invoke(macroId, target.ParentPathText, target.InsertIndex)", stepSequenceCode);
    Assert.Contains("GetStepDropTargetFromPoint", stepSequenceCode);
    Assert.Contains("MoveStepsToDropTarget", stepSequenceCode);
    Assert.Contains("InsertStepsAt", stepSequenceCode);
    Assert.Contains("StepPath", stepSequenceCode);
    Assert.Contains("StepDropTarget", stepSequenceCode);
    Assert.Contains("MacroStepTreeEditor.DeleteAtPath", stepSequenceCode);
    Assert.Contains("MacroStepTreeEditor.InsertAtPath", stepSequenceCode);
    Assert.Contains("MacroStepTreeEditor.MoveManyAtPath", stepSequenceCode);
    Assert.Contains("OnActionTemplateDropped(MacroActionTemplateKind kind, string parentPathText, int insertIndex)", mainWindowCode);
    Assert.Contains("OnMacroLibraryDropped(string macroId, string parentPathText, int insertIndex)", mainWindowCode);
}

static void MacroStudioSupportsMultiSelectDragFeedbackAndConditionOnlyPixel()
{
    var actionPaletteXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ActionPalettePanel.xaml"));
    var actionPaletteCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ActionPalettePanel.xaml.cs"));
    var conditionXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml"));
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));

    Assert.Contains("SelectionMode=\"Extended\"", stepSequenceXaml);
    Assert.Contains("DropIndicator", stepSequenceXaml);
    Assert.Contains("DragLeave=\"StepList_DragLeave\"", stepSequenceXaml);
    Assert.Contains("GetSelectedStepPathTextsForDrag", stepSequenceCode);
    Assert.Contains("MoveStepsToDropTarget", stepSequenceCode);
    Assert.Contains("ShowStepDragGhost", stepSequenceCode);
    Assert.Contains("UpdateDropIndicator", stepSequenceCode);
    Assert.Contains("DragGhostAdorner", stepSequenceCode);
    Assert.Contains("StepDragPathSeparator", stepSequenceCode);
    Assert.DoesNotContain("AddPixelButton", actionPaletteXaml);
    Assert.DoesNotContain("Tag=\"Pixel\"", actionPaletteXaml);
    Assert.DoesNotContain("AddPixelText", actionPaletteCode);
    Assert.Contains("AddConditionButton", conditionXaml);
    Assert.Contains("CondTypeCombo", conditionXaml);
    Assert.Contains("PixelOptionsPanel", conditionXaml);
}

static void MacroStudioHostsStepPropertiesInsideSequenceCards()
{
    var mainWindowXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));
    var stepEditorXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml"));

    Assert.DoesNotContain("<controls:StepEditorPanel x:Name=\"StepEditor\"", mainWindowXaml);
    Assert.DoesNotContain("Header=\"步骤属性\"", mainWindowXaml);
    Assert.DoesNotContain("StepEditor.Initialize", mainWindowCode);
    Assert.DoesNotContain("StepEditor.StepEdited", mainWindowCode);
    Assert.DoesNotContain("StepEditor.ApplyLocalization", mainWindowCode);
    Assert.Contains("InlineStepEditorPopup", stepSequenceXaml);
    Assert.Contains("InlineStepEditor", stepSequenceXaml);
    Assert.Contains("Placement=\"Right\"", stepSequenceXaml);
    Assert.Contains("InlineStepEditor.Initialize(editorState)", stepSequenceCode);
    Assert.Contains("InlineStepEditor.StepEdited += OnInlineStepEdited", stepSequenceCode);
    Assert.Contains("OpenInlineStepEditor", stepSequenceCode);
    Assert.Contains("OnInlineStepEdited", stepSequenceCode);
    Assert.Contains("InlineStepEditor.BuildEditedStep", stepSequenceCode);
    Assert.Contains("ApplyEditedStepAtPath", stepSequenceCode);
    Assert.Contains("MacroTargetBox", stepEditorXaml);
}

static void MacroStudioClosesInlineEditorAfterApplyAndReturnsFocusToSequence()
{
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));
    var stepEditorXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml"));

    Assert.Contains("Closed=\"InlineStepEditorPopup_Closed\"", stepSequenceXaml);
    Assert.Contains("CloseInlineStepEditorAfterApply", stepSequenceCode);
    Assert.Contains("ResetStepDragState", stepSequenceCode);
    Assert.Contains("StepList.Focus()", stepSequenceCode);
    Assert.Contains("IsEditable=\"True\"", stepEditorXaml);
    Assert.Contains("DelayModeBox", stepEditorXaml);
    Assert.Contains("DelayMinMsBox", stepEditorXaml);
    Assert.Contains("DelayMaxMsBox", stepEditorXaml);
}

static void MacroStudioClosesInlineEditorOnOutsideClickButKeepsCoordinatePickerOpen()
{
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));

    Assert.Contains("x:Name=\"StepSequenceRoot\"", stepSequenceXaml);
    Assert.Contains("PreviewMouseDown=\"SequenceRoot_PreviewMouseDown\"", stepSequenceXaml);
    Assert.Contains("StaysOpen=\"True\"", stepSequenceXaml);
    Assert.Contains("inlineCoordinatePickerActive", stepSequenceCode);
    Assert.Contains("SequenceRoot_PreviewMouseDown", stepSequenceCode);
    Assert.Contains("IsClickInsideInlineEditor", stepSequenceCode);
    Assert.Contains("CloseInlineStepEditorForOutsideClick", stepSequenceCode);
    Assert.DoesNotContain("InlineStepEditorPopup.StaysOpen = inlineEditorPopupOriginalStaysOpen", stepSequenceCode);
}

static void MacroStudioClosesInlineEditorWhenFocusMovesToSiblingPanels()
{
    var mainWindowXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));

    Assert.Contains("PreviewMouseDown=\"MainWindow_PreviewMouseDown\"", mainWindowXaml);
    Assert.Contains("MainWindow_PreviewMouseDown", mainWindowCode);
    Assert.Contains("SequencePanelControl.CloseInlineStepEditorOnExternalPointerDown", mainWindowCode);
    Assert.Contains("ConditionPanel.CloseInlineStepEditorOnExternalPointerDown", mainWindowCode);
    Assert.Contains("CloseInlineStepEditorOnExternalPointerDown", stepSequenceCode);
    Assert.Contains("IsClickInsideSequencePanel", stepSequenceCode);
    Assert.Contains("inlineCoordinatePickerActive", stepSequenceCode);
}

static void MacroStudioUsesSharedStepSequencePanelForBaseAndConditionActions()
{
    var stepSequenceXamlPath = Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml");
    var stepSequenceCodePath = Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs");
    var sequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "SequencePanel.xaml"));
    var conditionXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml"));
    var conditionCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml.cs"));

    Assert.True(File.Exists(stepSequenceXamlPath));
    Assert.True(File.Exists(stepSequenceCodePath));
    Assert.Contains("<local:StepSequencePanel x:Name=\"StepSequenceControl\"", sequenceXaml);
    Assert.Contains("<local:StepSequencePanel x:Name=\"ThenActionSequence\"", conditionXaml);
    Assert.Contains("ThenActionSequence.StepsChanged", conditionCode);
    Assert.DoesNotContain("new ThenActionsEditorWindow", conditionCode);
}

static void MacroStudioConditionActionsSupportExplorerSequenceInteractions()
{
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));
    var conditionCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml.cs"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));

    Assert.Contains("SelectionMode=\"Extended\"", stepSequenceXaml);
    Assert.Contains("x:Name=\"SelectionRectangle\"", stepSequenceXaml);
    Assert.Contains("x:Name=\"DropIndicator\"", stepSequenceXaml);
    Assert.Contains("x:Name=\"InlineStepEditorPopup\"", stepSequenceXaml);
    Assert.Contains("StepCopyButton", stepSequenceXaml);
    Assert.Contains("StepRowDeleteButton", stepSequenceXaml);
    Assert.Contains("PreviewMouseMove=\"StepList_PreviewMouseMove\"", stepSequenceXaml);
    Assert.Contains("Drop=\"StepList_Drop\"", stepSequenceXaml);

    Assert.Contains("HandleExplorerShortcut", stepSequenceCode);
    Assert.Contains("MoveSelectedSteps", stepSequenceCode);
    Assert.Contains("CopySelectedStepsToClipboard", stepSequenceCode);
    Assert.Contains("CutSelectedStepsToClipboard", stepSequenceCode);
    Assert.Contains("PasteStepsFromClipboard", stepSequenceCode);
    Assert.Contains("AutoScrollStepList", stepSequenceCode);
    Assert.Contains("MacroStepTreeEditor.MoveManyAtPath", stepSequenceCode);
    Assert.Contains("CloseInlineStepEditorOnExternalPointerDown", stepSequenceCode);

    Assert.Contains("ThenActionSequence.HandleExplorerShortcut", conditionCode);
    Assert.Contains("ConditionPanel.HandleExplorerShortcut", mainWindowCode);
}

static void MacroStudioConditionListSupportsExplorerOperations()
{
    var conditionXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml"));
    var conditionCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml.cs"));

    Assert.Contains("SelectionMode=\"Extended\"", conditionXaml);
    Assert.Contains("AllowDrop=\"True\"", conditionXaml);
    Assert.Contains("Drop=\"ConditionList_Drop\"", conditionXaml);
    Assert.Contains("DragOver=\"ConditionList_DragOver\"", conditionXaml);
    Assert.Contains("ConditionList_PreviewMouseMove", conditionXaml);

    Assert.Contains("HandleExplorerShortcut", conditionCode);
    Assert.Contains("SelectAllConditions", conditionCode);
    Assert.Contains("CopySelectedConditionsToClipboard", conditionCode);
    Assert.Contains("CutSelectedConditionsToClipboard", conditionCode);
    Assert.Contains("PasteConditionsFromClipboard", conditionCode);
    Assert.Contains("MoveSelectedConditionsToIndex", conditionCode);
    Assert.Contains("ConditionClipboardPrefix", conditionCode);
    Assert.Contains("CloneConditionForPaste", conditionCode);
    Assert.Contains("ConditionalDirective.NewId()", conditionCode);
}

static void MacroStudioProvidesWorkspaceWindowMenu()
{
    var mainWindowXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));

    Assert.Contains("x:Name=\"WindowMenuButton\"", mainWindowXaml);
    Assert.Contains("x:Name=\"WindowMenuContext\"", mainWindowXaml);
    Assert.Contains("ShowLibraryMenuItem", mainWindowXaml);
    Assert.Contains("FloatJsonMenuItem", mainWindowXaml);
    Assert.Contains("DockJsonMenuItem", mainWindowXaml);
    Assert.Contains("ResetWorkspaceLayoutMenuItem", mainWindowXaml);
    Assert.Contains("x:Name=\"LibraryPanelHost\"", mainWindowXaml);
    Assert.Contains("x:Name=\"SequencePanelHost\"", mainWindowXaml);
    Assert.Contains("x:Name=\"ConditionPanelHost\"", mainWindowXaml);
    Assert.Contains("x:Name=\"ActionPaletteHost\"", mainWindowXaml);
    Assert.Contains("x:Name=\"PlaybackPanelHost\"", mainWindowXaml);
    Assert.Contains("x:Name=\"JsonPanelHost\"", mainWindowXaml);

    Assert.Contains("RegisterWorkspacePanel", mainWindowCode);
    Assert.Contains("ToggleWorkspacePanelVisibility", mainWindowCode);
    Assert.Contains("FloatWorkspacePanel", mainWindowCode);
    Assert.Contains("DockWorkspacePanel", mainWindowCode);
    Assert.Contains("ResetWorkspaceLayout", mainWindowCode);
    Assert.Contains("WorkspaceLayoutStore", mainWindowCode);
    Assert.Contains("\"library\"", mainWindowCode);
    Assert.Contains("\"sequence\"", mainWindowCode);
    Assert.Contains("\"conditions\"", mainWindowCode);
    Assert.Contains("\"json\"", mainWindowCode);
    Assert.Contains("\"actions\"", mainWindowCode);
    Assert.Contains("\"playback\"", mainWindowCode);
}

static void MacroStudioProvidesIdeaStyleToolWindowStripe()
{
    var mainWindowXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));
    var sharedStyles = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Themes", "SharedStyles.xaml"));

    Assert.Contains("x:Name=\"WorkspaceRoot\"", mainWindowXaml);
    Assert.Contains("x:Name=\"ToolWindowRail\"", mainWindowXaml);
    Assert.Contains("x:Name=\"ToolWindowBottomButtons\"", mainWindowXaml);
    Assert.DoesNotContain("x:Name=\"ToolWindowTopButtons\"", mainWindowXaml);
    Assert.Contains("DockPanel.Dock=\"Bottom\"", mainWindowXaml);
    Assert.Contains("x:Name=\"LibraryToolButton\"", mainWindowXaml);
    Assert.Contains("x:Name=\"SequenceToolButton\"", mainWindowXaml);
    Assert.Contains("x:Name=\"ConditionToolButton\"", mainWindowXaml);
    Assert.Contains("x:Name=\"JsonToolButton\"", mainWindowXaml);
    Assert.Contains("x:Name=\"ActionToolButton\"", mainWindowXaml);
    Assert.Contains("x:Name=\"PlaybackToolButton\"", mainWindowXaml);
    Assert.DoesNotContain("Content=\"M\"", mainWindowXaml);
    Assert.DoesNotContain("Content=\"S\"", mainWindowXaml);
    Assert.DoesNotContain("Content=\"C\"", mainWindowXaml);
    Assert.DoesNotContain("Content=\"A\"", mainWindowXaml);
    Assert.DoesNotContain("Content=\"J\"", mainWindowXaml);
    Assert.DoesNotContain("Content=\"P\"", mainWindowXaml);
    Assert.Contains("Click=\"WorkspaceToolButton_Click\"", mainWindowXaml);
    Assert.Contains("Style=\"{StaticResource ToolWindowRailButton}\"", mainWindowXaml);
    Assert.Contains("Grid.Column=\"1\"", mainWindowXaml);

    Assert.Contains("WorkspaceToolButton_Click", mainWindowCode);
    Assert.Contains("UpdateWorkspaceToolButtonStates", mainWindowCode);
    Assert.Contains("SetToolButtonCheck", mainWindowCode);
    Assert.Contains("SetWorkspaceToolButtonText", mainWindowCode);

    Assert.Contains("Style x:Key=\"ToolWindowRailButton\"", sharedStyles);
    Assert.Contains("TargetType=\"ToggleButton\"", sharedStyles);
    Assert.Contains("Segoe Fluent Icons", sharedStyles);
}

static void MacroStudioDetachesMcrxJsonIntoToolPanel()
{
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));
    var sequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "SequencePanel.xaml"));
    var sequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "SequencePanel.xaml.cs"));
    var jsonPanelXamlPath = Path.Combine("src", "ui", "MacroStudio", "Controls", "McrxJsonPanel.xaml");
    var jsonPanelCodePath = Path.Combine("src", "ui", "MacroStudio", "Controls", "McrxJsonPanel.xaml.cs");
    var panelWindowPath = Path.Combine("src", "ui", "MacroStudio", "Controls", "WorkspacePanelWindow.cs");
    var layoutStorePath = Path.Combine("src", "ui", "MacroStudio", "Services", "WorkspaceLayoutStore.cs");

    Assert.DoesNotContain("MCRX JSON", sequenceXaml);
    Assert.DoesNotContain("MacroEditor", sequenceXaml);
    Assert.Contains("private string editorText", sequenceCode);
    Assert.Contains("EditorTextChanged", sequenceCode);

    Assert.True(File.Exists(jsonPanelXamlPath));
    Assert.True(File.Exists(jsonPanelCodePath));
    var jsonPanelXaml = File.ReadAllText(jsonPanelXamlPath);
    var jsonPanelCode = File.ReadAllText(jsonPanelCodePath);
    Assert.Contains("x:Name=\"MacroEditor\"", jsonPanelXaml);
    Assert.Contains("ApplyJsonButton", jsonPanelXaml);
    Assert.Contains("EditorTextChanged", jsonPanelCode);
    Assert.Contains("ApplyJsonRequested", jsonPanelCode);
    Assert.Contains("TryApplyJsonTextToEditor", mainWindowCode);
    Assert.Contains("TryApplyJsonTextToEditor(text, showStatus: false)", mainWindowCode);
    Assert.Contains("TryApplyJsonTextToEditor(JsonPanel.EditorText, showStatus: true)", mainWindowCode);

    Assert.True(File.Exists(panelWindowPath));
    Assert.True(File.Exists(layoutStorePath));
    var panelWindowCode = File.ReadAllText(panelWindowPath);
    var layoutStoreCode = File.ReadAllText(layoutStorePath);
    Assert.Contains("DockRequested", panelWindowCode);
    Assert.Contains("PanelId", panelWindowCode);
    Assert.Contains("WorkspacePanelLayout", layoutStoreCode);
    Assert.Contains("MacroStudioWorkspaceLayout.json", layoutStoreCode);
}

static void MacroStudioMovesConversionIntoMacroLibraryAndRemovesDiagnosticsControls()
{
    var mainWindowXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));
    var libraryXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "MacroLibraryPanel.xaml"));
    var libraryCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "MacroLibraryPanel.xaml.cs"));

    Assert.DoesNotContain("ValidateButton", mainWindowXaml);
    Assert.DoesNotContain("ProbeButton", mainWindowXaml);
    Assert.DoesNotContain("ValidateMacro_Click", mainWindowCode);
    Assert.DoesNotContain("Probe_Click", mainWindowCode);
    Assert.DoesNotContain("ConversionPanelControl", mainWindowXaml);
    Assert.DoesNotContain("DiagnosticsPanelControl", mainWindowXaml);
    Assert.DoesNotContain("ConversionPanelControl", mainWindowCode);
    Assert.DoesNotContain("DiagnosticsPanelControl", mainWindowCode);

    Assert.Contains("ImportMacroButton", libraryXaml);
    Assert.Contains("ImportRazerModulesButton", libraryXaml);
    Assert.Contains("ExportFormatBox", libraryXaml);
    Assert.Contains("ExportMacroButton", libraryXaml);
    Assert.Contains("MacroConversionService.GetFormats", libraryCode);
    Assert.Contains("TryGetRazerMacroGuid", libraryCode);
    Assert.Contains("AddAliasesToMacro", libraryCode);
    Assert.Contains("CreateMacro(imported.Document, aliases:", libraryCode);
    Assert.Contains("ImportApplied", libraryCode);
    Assert.Contains("DocumentRequested", libraryCode);
    Assert.Contains("ResultMessage", libraryCode);
    Assert.Contains("LibraryPanel.ImportApplied += OnImportApplied", mainWindowCode);
    Assert.Contains("LibraryPanel.DocumentRequested += () => GetDocumentWithPlayback()", mainWindowCode);
    Assert.Contains("LibraryPanel.ResultMessage += msg => SetStatus(msg)", mainWindowCode);
}

static void MacroStudioPreservesRazerModuleCallsWhenImportingMainMacros()
{
    var libraryCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "MacroLibraryPanel.xaml.cs"));

    Assert.Contains("MacroConversionFormat.Auto, [])", libraryCode);
    Assert.DoesNotContain("MacroConversionFormat.Auto, razerModuleFiles", libraryCode);
}

static void MacroStudioSupportsMultiSelectToolbarMoveOperations()
{
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));

    Assert.Contains("MoveSelectedSteps", stepSequenceCode);
    Assert.Contains("GetSelectedStepPaths", stepSequenceCode);
    Assert.Contains("AreSameParent", stepSequenceCode);
    Assert.Contains("MacroStepTreeEditor.MoveManyAtPath", stepSequenceCode);
    Assert.Contains("StepUp_Click", stepSequenceCode);
    Assert.Contains("StepDown_Click", stepSequenceCode);
}

static void MacroStudioSupportsExplorerStyleBoxSelectionInSequencePanels()
{
    var stepSequenceXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml"));
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));
    var conditionXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml"));
    var conditionCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml.cs"));

    Assert.Contains("x:Name=\"SelectionRectangle\"", stepSequenceXaml);
    Assert.Contains("PreviewMouseLeftButtonUp=\"StepList_PreviewMouseLeftButtonUp\"", stepSequenceXaml);
    Assert.Contains("BeginBoxSelection", stepSequenceCode);
    Assert.Contains("UpdateBoxSelection", stepSequenceCode);
    Assert.Contains("FinishBoxSelection", stepSequenceCode);
    Assert.Contains("SelectStepItemsInsideBox", stepSequenceCode);
    Assert.Contains("StepList.CaptureMouse()", stepSequenceCode);
    Assert.Contains("boxSelectionBasePaths", stepSequenceCode);

    Assert.Contains("SelectionMode=\"Extended\"", conditionXaml);
    Assert.Contains("x:Name=\"ConditionSelectionRectangle\"", conditionXaml);
    Assert.Contains("PreviewMouseLeftButtonDown=\"ConditionList_PreviewMouseLeftButtonDown\"", conditionXaml);
    Assert.Contains("PreviewMouseMove=\"ConditionList_PreviewMouseMove\"", conditionXaml);
    Assert.Contains("PreviewMouseLeftButtonUp=\"ConditionList_PreviewMouseLeftButtonUp\"", conditionXaml);
    Assert.Contains("BeginConditionBoxSelection", conditionCode);
    Assert.Contains("UpdateConditionBoxSelection", conditionCode);
    Assert.Contains("FinishConditionBoxSelection", conditionCode);
    Assert.Contains("SelectConditionItemsInsideBox", conditionCode);
    Assert.Contains("ConditionList.CaptureMouse()", conditionCode);
    Assert.Contains("GetSelectedConditionIndexes", conditionCode);
}

static void MacroStudioSupportsExplorerShortcutsAutoscrollAndStableMultiDrag()
{
    var stepSequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepSequencePanel.xaml.cs"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));

    Assert.Contains("HandleExplorerShortcut", stepSequenceCode);
    Assert.Contains("SelectAllSteps", stepSequenceCode);
    Assert.Contains("CopySelectedStepsToClipboard", stepSequenceCode);
    Assert.Contains("CutSelectedStepsToClipboard", stepSequenceCode);
    Assert.Contains("PasteStepsFromClipboard", stepSequenceCode);
    Assert.Contains("StepClipboardPrefix", stepSequenceCode);
    Assert.Contains("SelectStepPaths", stepSequenceCode);

    Assert.Contains("dragSelectionSnapshot", stepSequenceCode);
    Assert.Contains("IsSelectedStepPath(stepDragStartPath)", stepSequenceCode);
    Assert.Contains("GetSelectedStepPathTextsForDrag(stepDragStartPath)", stepSequenceCode);

    Assert.Contains("boxSelectionAutoScrollTimer", stepSequenceCode);
    Assert.Contains("AutoScrollStepList", stepSequenceCode);
    Assert.Contains("FindVisualChild<ScrollViewer>", stepSequenceCode);

    Assert.Contains("SequencePanelControl.HandleExplorerShortcut", mainWindowCode);
    Assert.Contains("ConditionPanel.HandleExplorerShortcut", mainWindowCode);
}

static void MacroStudioActionTemplateGateRejectsDuplicateGestureInserts()
{
    var gate = new ActionTemplateInsertGate(TimeSpan.FromMilliseconds(250));
    const long frequency = 1_000;

    Assert.True(gate.TryAccept(MacroActionTemplateKind.Delay, nowTicks: 1_000, frequency));
    Assert.False(gate.TryAccept(MacroActionTemplateKind.Delay, nowTicks: 1_050, frequency));
    Assert.True(gate.TryAccept(MacroActionTemplateKind.Keyboard, nowTicks: 1_060, frequency));
    Assert.True(gate.TryAccept(MacroActionTemplateKind.Delay, nowTicks: 1_400, frequency));
}

static void MacroStepTreeEditorInsertsAndDeletesInsideLoopsByPath()
{
    var steps = new MacroStep[]
    {
        new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero),
        new RepeatStep(3, []),
        new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)
    };

    var withNestedWait = MacroStepTreeEditor.InsertAtPath(
        steps,
        parentPath: [1],
        insertIndex: 0,
        [new WaitStep(TimeSpan.FromMilliseconds(100))]);

    Assert.Equal(3, withNestedWait.Count);
    var loop = Assert.IsType<RepeatStep>(withNestedWait[1]);
    Assert.Single(loop.Steps);
    Assert.IsType<WaitStep>(loop.Steps[0]);

    var afterNestedDelete = MacroStepTreeEditor.DeleteAtPath(withNestedWait, [1, 0]);
    Assert.Equal(3, afterNestedDelete.Count);
    Assert.Equal(HidKey.A, Assert.IsType<KeyStep>(afterNestedDelete[0]).Key);
    Assert.Equal(HidKey.A, Assert.IsType<KeyStep>(afterNestedDelete[2]).Key);
    Assert.Empty(Assert.IsType<RepeatStep>(afterNestedDelete[1]).Steps);

    var movedIntoLoop = MacroStepTreeEditor.MoveAtPath(afterNestedDelete, [0], [1], 0);
    Assert.Equal(2, movedIntoLoop.Count);
    var movedLoop = Assert.IsType<RepeatStep>(movedIntoLoop[0]);
    Assert.Single(movedLoop.Steps);
    Assert.Equal(KeyActionKind.Down, Assert.IsType<KeyStep>(movedLoop.Steps[0]).Kind);
}

static void MacroStepTreeEditorMovesMultipleSelectedStepsIntoLoopsByPath()
{
    var steps = new MacroStep[]
    {
        new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero),
        new WaitStep(TimeSpan.FromMilliseconds(10)),
        new MouseButtonStep(MouseButton.Left, ButtonActionKind.Down, TimeSpan.Zero),
        new RepeatStep(2, []),
        new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)
    };

    var moveMany = typeof(MacroStepTreeEditor).GetMethod(
        "MoveManyAtPath",
        [typeof(IReadOnlyList<MacroStep>), typeof(IReadOnlyList<IReadOnlyList<int>>), typeof(IReadOnlyList<int>), typeof(int)]);
    Assert.True(moveMany is not null);

    var sourcePaths = new IReadOnlyList<int>[] { [0], [2] };
    var moved = (IReadOnlyList<MacroStep>)moveMany!.Invoke(null, [steps, sourcePaths, new[] { 3 }, 0])!;

    Assert.Equal(3, moved.Count);
    Assert.IsType<WaitStep>(moved[0]);
    var repeat = Assert.IsType<RepeatStep>(moved[1]);
    Assert.Equal(2, repeat.Steps.Count);
    Assert.Equal(KeyActionKind.Down, Assert.IsType<KeyStep>(repeat.Steps[0]).Kind);
    Assert.Equal(ButtonActionKind.Down, Assert.IsType<MouseButtonStep>(repeat.Steps[1]).Kind);
    Assert.Equal(KeyActionKind.Up, Assert.IsType<KeyStep>(moved[2]).Kind);
}

static void MacroStepTreeEditorEditsLinkedPressReleasePairs()
{
    var keyboardSteps = new MacroStep[]
    {
        new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero),
        new WaitStep(TimeSpan.FromMilliseconds(10)),
        new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)
    };

    var editedKeyboard = MacroStepTreeEditor.ReplaceAtPathWithLinkedPressRelease(
        keyboardSteps,
        [2],
        new KeyStep(KeyActionKind.Up, HidKey.B, HidModifier.LeftShift, TimeSpan.Zero));

    var keyDown = Assert.IsType<KeyStep>(editedKeyboard[0]);
    var keyUp = Assert.IsType<KeyStep>(editedKeyboard[2]);
    Assert.Equal(HidKey.B, keyDown.Key);
    Assert.Equal(HidModifier.LeftShift, keyDown.Modifiers);
    Assert.Equal(HidKey.B, keyUp.Key);
    Assert.Equal(HidModifier.LeftShift, keyUp.Modifiers);

    var mouseSteps = new MacroStep[]
    {
        new MouseButtonStep(MouseButton.Left, ButtonActionKind.Down, TimeSpan.Zero, MouseMoveMode.Absolute, 100, 200),
        new WaitStep(TimeSpan.FromMilliseconds(5)),
        new MouseButtonStep(MouseButton.Left, ButtonActionKind.Up, TimeSpan.Zero)
    };

    var editedMouse = MacroStepTreeEditor.ReplaceAtPathWithLinkedPressRelease(
        mouseSteps,
        [0],
        new MouseButtonStep(MouseButton.X1, ButtonActionKind.Down, TimeSpan.Zero, MouseMoveMode.Absolute, 300, 400));

    var mouseDown = Assert.IsType<MouseButtonStep>(editedMouse[0]);
    var mouseUp = Assert.IsType<MouseButtonStep>(editedMouse[2]);
    Assert.Equal(MouseButton.X1, mouseDown.Button);
    Assert.Equal(MouseButton.X1, mouseUp.Button);
    Assert.Equal(MouseMoveMode.Absolute, mouseDown.CoordinateMode);
    Assert.Equal(300, mouseDown.X);
    Assert.Equal(400, mouseDown.Y);
    Assert.False(mouseUp.HasCoordinate);
}

static void StepDisplayLabelsUsePressReleaseWording()
{
    Assert.Equal(
        "A按下",
        StepDisplayItem.FromStep(0, new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero)).Title);
    Assert.Equal(
        "A抬起",
        StepDisplayItem.FromStep(1, new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)).Title);
    Assert.Equal(
        "Ctrl+Alt+A按下",
        StepDisplayItem.FromStep(2, new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.LeftCtrl | HidModifier.LeftAlt, TimeSpan.Zero)).Title);
    Assert.Equal(
        "左键按下",
        StepDisplayItem.FromStep(3, new MouseButtonStep(MouseButton.Left, ButtonActionKind.Down, TimeSpan.Zero)).Title);
    Assert.Equal(
        "右键抬起",
        StepDisplayItem.FromStep(4, new MouseButtonStep(MouseButton.Right, ButtonActionKind.Up, TimeSpan.Zero)).Title);
    Assert.Equal(
        "鼠标按键 4按下",
        StepDisplayItem.FromStep(5, new MouseButtonStep(MouseButton.X1, ButtonActionKind.Down, TimeSpan.Zero)).Title);
}

static void StepDisplayLabelsResolveMacroCallIdsToNames()
{
    const string macroId = "b0faee5c-9bde-47ef-bcaa-ac25544e21e8";
    Func<string, string> resolveMacroName = value => value == macroId ? "2as（无前摇）" : value;

    var fromStep = typeof(StepDisplayItem).GetMethod(
        "FromStep",
        [typeof(int), typeof(MacroStep), typeof(int), typeof(IReadOnlyList<int>), typeof(Func<string, string>)]);
    var flattenSteps = typeof(StepDisplayItem).GetMethod(
        "FlattenSteps",
        [typeof(IReadOnlyList<MacroStep>), typeof(int), typeof(IReadOnlyList<int>), typeof(Func<string, string>)]);

    Assert.True(fromStep is not null);
    Assert.True(flattenSteps is not null);

    var item = (StepDisplayItem)fromStep!.Invoke(null, [0, new MacroCallStep(macroId), 0, null, resolveMacroName])!;
    Assert.Equal("调用宏: 2as（无前摇）", item.Title);

    var nested = (List<StepDisplayItem>)flattenSteps!.Invoke(null, [
        new MacroStep[] { new RepeatStep(1, [new MacroCallStep(macroId)]) },
        0,
        null,
        resolveMacroName
    ])!;
    Assert.True(nested.Any(step => step.Title == "调用宏: 2as（无前摇）"));
}

static void MacroStudioResolvesMacroCallAliasesForDisplayAndPlayback()
{
    var sequenceCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "SequencePanel.xaml.cs"));
    var mainWindowCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));
    var stepEditorCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml.cs"));

    Assert.Contains("item.MatchesReference(value)", sequenceCode);
    Assert.Contains("candidate.MatchesReference(name)", mainWindowCode);
    Assert.Contains("item.MatchesReference(previous)", stepEditorCode);
}

static void RuntimeDiagnosticsReportSendInputAvailabilityFromStats()
{
    var present = RuntimeDiagnosticsSnapshot.FromInputStats(new InputSubmissionStats(
        ActionsSubmitted: 7,
        NativeInputsSubmitted: 12,
        FailedSubmissions: 0,
        LastWin32Error: 0,
        LastSubmitQpc: 1234,
        LastSubmitDurationMicroseconds: 42,
        Timing: new PlaybackTimingStats(3, -10, 250, "count=3 p50=12us p95=18us p99=20us")));
    Assert.True(present.InputBackend.Available);
    Assert.Contains("SendInput", present.InputBackend.Detail);
    Assert.Contains("actions=7", present.InputBackend.Detail);
    Assert.Contains("nativeInputs=12", present.InputBackend.Detail);
    Assert.Contains("submit=42us", present.InputBackend.Detail);
    Assert.Contains("jitter=count=3", present.InputBackend.Detail);
}

static void ConditionMonitorUsesPrecisionThreadAndPreparedThenActions()
{
    var conditionCode = File.ReadAllText(Path.Combine("src", "shared", "MacroHid.Runtime", "ConditionMonitor.cs"));

    Assert.Contains("new Thread(() => PollLoop", conditionCode);
    Assert.Contains("Priority = ThreadPriority.Highest", conditionCode);
    Assert.Contains("PrecisionPlaybackContext.Enter(PrecisionMode.ExtremeDuringPlayback)", conditionCode);
    Assert.Contains("CompiledPlaybackPlan.Create", conditionCode);
    Assert.Contains("SubmitPrepared", conditionCode);
    Assert.DoesNotContain("Task.Run(() => PollLoop", conditionCode);
    Assert.DoesNotContain("Thread.Sleep(Math.Max(1, pollMs))", conditionCode);
}

static void LatencyProbeReportsPrecisionHistograms()
{
    var probeCode = File.ReadAllText(Path.Combine("src", "tools", "LatencyProbe", "Program.cs"));

    Assert.Contains("scheduleJitter", probeCode);
    Assert.Contains("submitDuration", probeCode);
    Assert.Contains("encodeCost", probeCode);
    Assert.Contains("conditionTrigger", probeCode);
    Assert.Contains("PreparedInputBatch.FromActions", probeCode);
    Assert.Contains("QpcPlaybackDelayStrategy", probeCode);
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
    Assert.False(ContainsTapOrClick(qmacroResult.Document.Steps));

    var luaResult = MacroConversionService.ImportToMcrx(new MacroImportRequest(lua, "sample.lua"));
    Assert.Equal(MacroConversionFormat.Lua, luaResult.SourceFormat);
    Assert.True(luaResult.Document.Steps.OfType<TextStep>().Any());
    Assert.False(ContainsTapOrClick(luaResult.Document.Steps));

    var xmouseResult = MacroConversionService.ImportToMcrx(new MacroImportRequest(xmouse, "sample.xmbcs"));
    Assert.Equal(MacroConversionFormat.XMouse, xmouseResult.SourceFormat);
    Assert.True(xmouseResult.Document.Steps.OfType<WaitStep>().Any());
    Assert.False(ContainsTapOrClick(xmouseResult.Document.Steps));

    var macroXmlResult = MacroConversionService.ImportToMcrx(new MacroImportRequest(macroXml, "sample.xml"));
    Assert.Equal(MacroConversionFormat.MacroConverterXml, macroXmlResult.SourceFormat);
    Assert.IsType<MouseMoveStep>(macroXmlResult.Document.Steps[0]);
    Assert.False(ContainsTapOrClick(macroXmlResult.Document.Steps));

    var razerResult = MacroConversionService.ImportToMcrx(new MacroImportRequest(razer, "sample.xml"));
    Assert.Equal(MacroConversionFormat.RazerSynapseXml, razerResult.SourceFormat);
    var repeat = Assert.IsType<RepeatStep>(razerResult.Document.Steps[0]);
    Assert.Equal(2, repeat.Count);
    Assert.IsType<MouseButtonStep>(repeat.Steps[0]);
    Assert.False(ContainsTapOrClick(razerResult.Document.Steps));
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

    var mouseDown = Assert.IsType<MouseButtonStep>(import.Document.Steps[0]);
    var mouseHold = Assert.IsType<WaitStep>(import.Document.Steps[1]);
    var wait = Assert.IsType<WaitStep>(import.Document.Steps[3]);
    var keyDown = Assert.IsType<KeyStep>(import.Document.Steps[4]);
    var keyHold = Assert.IsType<WaitStep>(import.Document.Steps[5]);

    Assert.Equal(ButtonActionKind.Down, mouseDown.Kind);
    Assert.Equal(TimeSpan.FromTicks(7_500), mouseHold.Duration);
    Assert.Equal(TimeSpan.FromTicks(12_500), wait.Duration);
    Assert.Equal(KeyActionKind.Down, keyDown.Kind);
    Assert.Equal(TimeSpan.FromTicks(5_000), keyHold.Duration);
    Assert.False(ContainsTapOrClick(import.Document.Steps));
}

static void EmbeddedConverterImportsRazerModuleReferencesAsMacroCalls()
{
    const string razer = """
    <Macro>
      <Name>Main Macro</Name>
      <MacroEvents>
        <MacroEvent>
          <Type>7</Type>
          <guid>{missing-module-guid}</guid>
          <Name>Nested Burst</Name>
        </MacroEvent>
      </MacroEvents>
      <Version>4</Version>
    </Macro>
    """;

    var import = MacroConversionService.ImportToMcrx(new MacroImportRequest(razer, "nested.xml"));

    var call = Assert.IsType<MacroCallStep>(import.Document.Steps.Single());
    Assert.Equal("Nested Burst", call.Macro);
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
        Assert.Equal(4, saved.Steps.Count);
        Assert.False(ContainsTapOrClick(saved.Steps));

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

static void MacroLibraryStoreResolvesExternalAliases()
{
    const string razerGuid = "b0faee5c-9bde-47ef-bcaa-ac25544e21e8";
    var root = Path.Combine(Path.GetTempPath(), "MacroHID-tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new MacroLibraryStore(root);
        var created = store.CreateMacro(
            new MacroDocument(1, "2as", [new WaitStep(TimeSpan.FromMilliseconds(1))]),
            aliases: [razerGuid]);

        var reloaded = new MacroLibraryStore(root).Load().Items.Single(item => item.Id == created.Id);

        Assert.True(reloaded.Aliases?.Contains(razerGuid, StringComparer.OrdinalIgnoreCase) == true);
        Assert.True(reloaded.MatchesReference(razerGuid));
        Assert.True(reloaded.MatchesReference(created.Id));
        Assert.True(reloaded.MatchesReference("2as"));

        var updated = store.AddAliasesToMacro(created.Id, ["module-guid-2"]);
        Assert.True(updated.MatchesReference("module-guid-2"));
        Assert.Equal(2, updated.Aliases?.Count ?? 0);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void MacroLibraryStorePersistsEmptyFoldersAndMovesMacrosLikeFiles()
{
    var root = Path.Combine(Path.GetTempPath(), "MacroHID-tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new MacroLibraryStore(root);
        store.CreateFolder("Combat");
        var macro = store.CreateMacro("Burst", steps: [new WaitStep(TimeSpan.FromMilliseconds(1))]);

        var moved = store.MoveMacro(macro.Id, "Combat");
        var reloaded = new MacroLibraryStore(root).Load();

        Assert.True(reloaded.Folders.Contains("Combat", StringComparer.Ordinal));
        Assert.Equal("Combat", moved.Folder);
        Assert.Equal("Combat", reloaded.Items.Single(item => item.Id == macro.Id).Folder);

        store.DeleteFolder("Combat", deleteMacros: false);
        var afterFolderDelete = store.Load();

        Assert.False(afterFolderDelete.Folders.Contains("Combat", StringComparer.Ordinal));
        Assert.Equal("", afterFolderDelete.Items.Single(item => item.Id == macro.Id).Folder);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void MacroLibraryStoreRenamesMacrosAndFolders()
{
    var root = Path.Combine(Path.GetTempPath(), "MacroHID-tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new MacroLibraryStore(root);
        var macro = store.CreateMacro("Burst", "Combat", [new WaitStep(TimeSpan.FromMilliseconds(1))]);

        var renamedMacro = store.RenameMacro(macro.Id, "Burst Prime");
        store.RenameFolder("Combat", "Raid");

        var reloaded = new MacroLibraryStore(root).Load();
        var item = reloaded.Items.Single(item => item.Id == macro.Id);

        Assert.Equal("Burst Prime", renamedMacro.Name);
        Assert.Equal("Burst Prime", store.ReadMacro(macro.Id).Name);
        Assert.Equal("Raid", item.Folder);
        Assert.False(reloaded.Folders.Contains("Combat", StringComparer.Ordinal));
        Assert.True(reloaded.Folders.Contains("Raid", StringComparer.Ordinal));
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void MacroStudioMacroLibraryUsesFolderTree()
{
    var libraryXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "MacroLibraryPanel.xaml"));
    var libraryCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "MacroLibraryPanel.xaml.cs"));
    var displayModels = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "DisplayModels.cs"));

    Assert.Contains("TreeView x:Name=\"MacroTreeView\"", libraryXaml);
    Assert.Contains("HierarchicalDataTemplate", libraryXaml);
    Assert.Contains("MacroLibraryTreeNode", displayModels);
    Assert.Contains("RefreshTree", libraryCode);
    Assert.Contains("CreateFolder", libraryCode);
    Assert.Contains("MoveMacro", libraryCode);
    Assert.Contains("MacroTreeView_Drop", libraryCode);
}

static void MacroStudioMacroLibrarySupportsExplorerRenameCopyAndPaste()
{
    var libraryXaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "MacroLibraryPanel.xaml"));
    var libraryCode = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "MacroLibraryPanel.xaml.cs"));
    var storeCode = File.ReadAllText(Path.Combine("src", "shared", "MacroHid.Core", "MacroLibraryStore.cs"));
    var displayModels = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "DisplayModels.cs"));

    Assert.Contains("MacroTreeNode_MouseLeftButtonDown", libraryXaml);
    Assert.Contains("RenameTextBox", libraryXaml);
    Assert.Contains("CopyMacroButton", libraryXaml);
    Assert.Contains("PasteMacroButton", libraryXaml);
    Assert.Contains("MacroTreeView_KeyDown", libraryXaml);
    Assert.Contains("BeginRename", libraryCode);
    Assert.Contains("CommitRename", libraryCode);
    Assert.Contains("CopySelectionToClipboard", libraryCode);
    Assert.Contains("PasteClipboard", libraryCode);
    Assert.Contains("RenameMacro", storeCode);
    Assert.Contains("RenameFolder", storeCode);
    Assert.Contains("IsRenaming", displayModels);
}

static void MacroActionTemplatesCreatePlayablePressReleaseSteps()
{
    var delay = Assert.IsType<WaitStep>(MacroActionTemplateFactory.CreateSteps(MacroActionTemplateKind.Delay)[0]);
    Assert.Equal(TimeSpan.FromMilliseconds(100), delay.Duration);

    var keySteps = MacroActionTemplateFactory.CreateSteps(MacroActionTemplateKind.Keyboard);
    Assert.Equal(2, keySteps.Count);
    Assert.Equal(KeyActionKind.Down, Assert.IsType<KeyStep>(keySteps[0]).Kind);
    Assert.Equal(KeyActionKind.Up, Assert.IsType<KeyStep>(keySteps[1]).Kind);

    var mouseSteps = MacroActionTemplateFactory.CreateSteps(MacroActionTemplateKind.MouseButton);
    Assert.Equal(2, mouseSteps.Count);
    Assert.Equal(MouseButton.Left, Assert.IsType<MouseButtonStep>(mouseSteps[0]).Button);
    Assert.Equal(ButtonActionKind.Down, Assert.IsType<MouseButtonStep>(mouseSteps[0]).Kind);
    Assert.Equal(ButtonActionKind.Up, Assert.IsType<MouseButtonStep>(mouseSteps[1]).Kind);

    var move = Assert.IsType<MouseMoveStep>(MacroActionTemplateFactory.CreateSteps(MacroActionTemplateKind.MouseMove)[0]);
    Assert.Equal(MouseMoveMode.Relative, move.Mode);

    var text = Assert.IsType<TextStep>(MacroActionTemplateFactory.CreateSteps(MacroActionTemplateKind.Text)[0]);
    Assert.Equal("text", text.Text);

    var repeat = Assert.IsType<RepeatStep>(MacroActionTemplateFactory.CreateSteps(MacroActionTemplateKind.Loop)[0]);
    Assert.Equal(2, repeat.Count);
    Assert.Equal(0, repeat.Steps.Count);

    var document = new MacroDocument(
        Version: 1,
        Name: "templates",
        Steps:
        [
            delay,
            .. keySteps,
            .. mouseSteps,
            move,
            text,
            repeat
        ]);
    var roundTrip = McrxParser.Parse(McrxSerializer.Serialize(document));
    Assert.Equal(8, roundTrip.Steps.Count);
    Assert.False(roundTrip.Steps.OfType<KeyStep>().Any(step => step.Kind == KeyActionKind.Tap));
    Assert.False(roundTrip.Steps.OfType<MouseButtonStep>().Any(step => step.Kind == ButtonActionKind.Click));
}

static bool ContainsTapOrClick(IEnumerable<MacroStep> steps)
{
    foreach (var step in steps)
    {
        switch (step)
        {
            case KeyStep { Kind: KeyActionKind.Tap }:
            case MouseButtonStep { Kind: ButtonActionKind.Click }:
            case ConsumerStep { Kind: ButtonActionKind.Click }:
                return true;
            case RepeatStep repeat when ContainsTapOrClick(repeat.Steps):
                return true;
            case PixelWhenStep pixel when ContainsTapOrClick(pixel.ThenSteps):
                return true;
        }
    }

    return false;
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

    public static void Empty<T>(IReadOnlyCollection<T> values)
    {
        if (values.Count != 0)
        {
            throw new InvalidOperationException($"Expected empty collection, got {values.Count} item(s).");
        }
    }

    public static void Single<T>(IReadOnlyCollection<T> values)
    {
        if (values.Count != 1)
        {
            throw new InvalidOperationException($"Expected single item, got {values.Count} item(s).");
        }
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'.");
        }
    }

    public static void DoesNotContain(string unexpected, string actual)
    {
        if (actual.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text to not contain '{unexpected}'.");
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
