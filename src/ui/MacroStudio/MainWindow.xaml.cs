using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MacroHid.Converter;
using MacroHid.Core;
using MacroHid.Runtime;
using Microsoft.Win32;

namespace MacroStudio;

public partial class MainWindow : Window
{
    private const string SampleMacro = """
    {
      "version": 1,
      "name": "baseline",
      "steps": [
        { "type": "key.tap", "key": "A", "modifiers": ["LeftCtrl"], "holdMs": 5 },
        { "type": "mouse.move", "mode": "relative", "x": 25, "y": -10, "durationMs": 0 },
        { "type": "mouse.wheel", "vertical": -1, "horizontal": 0 },
        { "type": "consumer.tap", "control": "VolumeUp" },
        { "type": "wait", "ms": 2 },
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

    private GlobalKeyboardHook? keyboardHook;
    private MacroPlaybackController? playbackController;
    private readonly SendInputMacroSink inputSink = new();
    private bool listening;
    private bool capturingTrigger;
    private bool updatingLanguageComboBox;
    private MacroImportResult? pendingImport;
    private string? pendingImportJson;
    private List<AuxiliaryMacroFile> razerModuleFiles = [];
    private string? statusResourceKey = "InputBackendReady";
    private string? statusPlainText;
    private object[] statusArgs = [];
    private string playbackStatusResourceKey = "PlaybackStatusIdle";
    private object[] playbackStatusArgs = [];
    private string playbackResultResourceKey = "LastResultNone";
    private object[] playbackResultArgs = [];

    public MainWindow()
    {
        InitializeComponent();
        LocalizationService.Initialize();
        InitializeLanguageComboBox();
        InitializeExportFormatBox();
        ApplyLocalization();
        MacroEditor.Text = SampleMacro;
        ValidateCurrentMacro();
        RefreshRuntimeDiagnostics();
    }

    private static string L(string key)
    {
        return LocalizationService.Get(key);
    }

    private static string LF(string key, params object[] args)
    {
        return LocalizationService.Format(key, args);
    }

    private void InitializeLanguageComboBox()
    {
        updatingLanguageComboBox = true;
        LanguageComboBox.Items.Clear();

        foreach (var language in LocalizationService.SupportedLanguages)
        {
            var item = new ComboBoxItem
            {
                Tag = language.CultureName,
                Content = LocalizationService.Get(language.DisplayNameResourceKey)
            };
            LanguageComboBox.Items.Add(item);

            if (string.Equals(language.CultureName, LocalizationService.CurrentCulture.Name, StringComparison.OrdinalIgnoreCase))
            {
                LanguageComboBox.SelectedItem = item;
            }
        }

        updatingLanguageComboBox = false;
    }

    private void InitializeExportFormatBox()
    {
        ExportFormatBox.Items.Clear();
        foreach (var format in MacroConversionService.GetFormats().Where(item => item.CanExport))
        {
            var item = new ComboBoxItem
            {
                Tag = format.Format,
                Content = format.Label
            };
            ExportFormatBox.Items.Add(item);
            if (format.Format == MacroConversionFormat.MacroConverterXml)
            {
                ExportFormatBox.SelectedItem = item;
            }
        }

        if (ExportFormatBox.SelectedItem is null && ExportFormatBox.Items.Count > 0)
        {
            ExportFormatBox.SelectedIndex = 0;
        }
    }

    private void ApplyLocalization()
    {
        Title = L("AppTitle");
        LanguageLabelText.Text = L("Language");
        OpenButton.Content = L("Open");
        SaveButton.Content = L("Save");
        ValidateButton.Content = L("Validate");
        ProbeButton.Content = L("Probe");
        MacroGroupBox.Header = L("Macro");
        NameLabelText.Text = L("Name");
        ScheduledStepsLabelText.Text = L("ScheduledSteps");
        PlaybackGroupBox.Header = L("Playback");
        TriggerLabelText.Text = L("Trigger");
        CaptureTriggerButton.Content = L("Capture");
        ModeLabelText.Text = L("Mode");
        PlaybackCountLabelText.Text = L("Count");
        StartListeningButton.Content = L("StartListening");
        StopListeningButton.Content = L("StopListening");
        RunNowButton.Content = L("RunNow");
        StopPlaybackButton.Content = L("Stop");
        SequenceGroupBox.Header = L("Sequence");
        DiagnosticsGroupBox.Header = L("Diagnostics");
        ConversionGroupBox.Header = L("ImportExport");
        ImportMacroButton.Content = L("ImportMacro");
        ImportRazerModulesButton.Content = L("ImportRazerModules");
        ApplyImportButton.Content = L("ApplyImport");
        ExportFormatLabelText.Text = L("ExportFormat");
        ExportMacroButton.Content = L("ExportMacro");

        foreach (var item in LanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            var cultureName = item.Tag?.ToString();
            var language = LocalizationService.SupportedLanguages
                .FirstOrDefault(candidate => string.Equals(candidate.CultureName, cultureName, StringComparison.OrdinalIgnoreCase));
            if (language is not null)
            {
                item.Content = LocalizationService.Get(language.DisplayNameResourceKey);
            }
        }

        foreach (var item in PlaybackModeBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = item.Tag?.ToString() switch
            {
                "toggleLoop" => L("ModeToggleLoop"),
                "holdLoop" => L("ModeHoldLoop"),
                "fixedCount" => L("ModeFixedCount"),
                _ => item.Content
            };
        }

        RefreshDynamicText();
        RefreshRuntimeDiagnostics();
        RefreshConversionText();
    }

    private void RefreshDynamicText()
    {
        StatusText.Text = statusPlainText ?? FormatResource(statusResourceKey, statusArgs);
        PlaybackStatusText.Text = FormatResource(playbackStatusResourceKey, playbackStatusArgs);
        PlaybackResultText.Text = FormatResource(playbackResultResourceKey, playbackResultArgs);
    }

    private void SetStatusResource(string key, params object[] args)
    {
        statusResourceKey = key;
        statusPlainText = null;
        statusArgs = args;
        StatusText.Text = FormatResource(key, args);
    }

    private void SetStatusPlainText(string text)
    {
        statusResourceKey = null;
        statusPlainText = text;
        statusArgs = [];
        StatusText.Text = text;
    }

    private void SetPlaybackStatusResource(string key, params object[] args)
    {
        playbackStatusResourceKey = key;
        playbackStatusArgs = args;
        PlaybackStatusText.Text = FormatResource(key, args);
    }

    private void SetPlaybackResultResource(string key, params object[] args)
    {
        playbackResultResourceKey = key;
        playbackResultArgs = args;
        PlaybackResultText.Text = FormatResource(key, args);
    }

    private static string FormatResource(string? key, object[] args)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return args.Length == 0 ? L(key) : LF(key, args);
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingLanguageComboBox)
        {
            return;
        }

        if ((LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag is not string cultureName)
        {
            return;
        }

        LocalizationService.SetLanguage(cultureName);
        ApplyLocalization();
    }

    private void OpenMacro_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = L("MacroFileFilter"),
            Title = L("OpenMacroTitle")
        };

        if (dialog.ShowDialog(this) == true)
        {
            MacroEditor.Text = File.ReadAllText(dialog.FileName);
            ValidateCurrentMacro();
            SetStatusPlainText(Path.GetFileName(dialog.FileName));
        }
    }

    private void SaveMacro_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = L("MacroFileFilter"),
            Title = L("SaveMacroTitle"),
            DefaultExt = ".mcrx"
        };

        if (dialog.ShowDialog(this) == true)
        {
            ApplyPlaybackSettingsToEditor();
            File.WriteAllText(dialog.FileName, MacroEditor.Text);
            SetStatusPlainText(Path.GetFileName(dialog.FileName));
        }
    }

    private void ValidateMacro_Click(object sender, RoutedEventArgs e)
    {
        ValidateCurrentMacro();
    }

    private void Probe_Click(object sender, RoutedEventArgs e)
    {
        var histogram = new LatencyHistogram();
        var stopwatch = Stopwatch.StartNew();
        var intervalTicks = Stopwatch.Frequency / 1_000;
        var nextTick = stopwatch.ElapsedTicks + intervalTicks;

        for (var i = 0; i < 250; i++)
        {
            while (stopwatch.ElapsedTicks < nextTick)
            {
                Thread.SpinWait(32);
            }

            var actual = stopwatch.ElapsedTicks;
            histogram.RecordMicroseconds(Math.Abs(actual - nextTick) * 1_000_000 / Stopwatch.Frequency);
            _ = SendInputEncoder.Encode(new KeyInputAction(KeyActionKind.Down, HidKey.A, HidModifier.None));
            nextTick += intervalTicks;
        }

        LatencyText.Text = histogram.Summary();
        RefreshRuntimeDiagnostics();
    }

    private void ImportMacro_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = L("ConverterImportFileFilter"),
            Title = L("ImportMacroTitle")
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var content = File.ReadAllText(dialog.FileName);
            pendingImport = MacroConversionService.ImportToMcrx(new MacroImportRequest(
                content,
                dialog.FileName,
                MacroConversionFormat.Auto,
                razerModuleFiles));
            pendingImportJson = McrxSerializer.Serialize(pendingImport.Document);
            ApplyImportButton.IsEnabled = true;
            RefreshConversionText();
            SetPlaybackResultResource("LastResultMessage", LF("ConversionImported", pendingImport.SourceFormat, pendingImport.Document.Steps.Count));
        }
        catch (Exception ex)
        {
            pendingImport = null;
            pendingImportJson = null;
            ApplyImportButton.IsEnabled = false;
            ConversionText.Text = LF("ConversionImportFailed", ex.Message);
            SetPlaybackResultResource("LastResultMessage", ex.Message);
        }
    }

    private void ImportRazerModules_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = L("RazerModuleFileFilter"),
            Title = L("ImportRazerModulesTitle"),
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        razerModuleFiles = dialog.FileNames
            .Select(fileName => new AuxiliaryMacroFile(Path.GetFileName(fileName), File.ReadAllText(fileName)))
            .ToList();
        RefreshConversionText();
        SetPlaybackResultResource("LastResultMessage", LF("ConversionModulesLoaded", razerModuleFiles.Count));
    }

    private void ApplyImport_Click(object sender, RoutedEventArgs e)
    {
        if (pendingImportJson is null)
        {
            return;
        }

        MacroEditor.Text = pendingImportJson;
        ValidateCurrentMacro();
        SetStatusResource("MacroValid");
        SetPlaybackResultResource("LastResultMessage", L("ConversionApplied"));
    }

    private void ExportMacro_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var document = ParseDocumentWithPlaybackFromControls(applyToEditor: true);
            var format = GetSelectedExportFormat();
            var export = MacroConversionService.ExportFromMcrx(document, format);
            var dialog = new SaveFileDialog
            {
                Filter = FormatFilter(format),
                Title = L("ExportMacroTitle"),
                DefaultExt = MacroConversionService.GetDefaultExtension(format),
                FileName = export.FileName
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            File.WriteAllText(dialog.FileName, export.Output);
            ConversionText.Text = FormatDiagnostics(export.Diagnostics);
            SetPlaybackResultResource("LastResultMessage", LF("ConversionExported", Path.GetFileName(dialog.FileName)));
        }
        catch (Exception ex)
        {
            ConversionText.Text = LF("ConversionExportFailed", ex.Message);
            SetPlaybackResultResource("LastResultMessage", ex.Message);
        }
    }

    private void ValidateCurrentMacro()
    {
        try
        {
            var document = McrxParser.Parse(MacroEditor.Text);
            var scheduled = MacroScheduler.Compile(document, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

            MacroNameText.Text = document.Name;
            StepCountText.Text = scheduled.Count.ToString();
            StepList.Items.Clear();

            foreach (var step in scheduled)
            {
                StepList.Items.Add($"{step.DueTick}: {Describe(step.Step)}");
            }

            SetStatusResource("MacroValid");
            SetPlaybackControls(document.Playback);
        }
        catch (Exception ex)
        {
            MacroNameText.Text = "-";
            StepCountText.Text = "0";
            StepList.Items.Clear();
            StepList.Items.Add(ex.Message);
            SetStatusResource("MacroInvalid");
        }
    }

    private void RefreshRuntimeDiagnostics()
    {
        var diagnostics = RuntimeDiagnosticsSnapshot.Collect();
        PixelText.Text = LF("PixelSamplerStatus", diagnostics.PixelSampler.Detail);
        InputBackendText.Text = LF("InputBackendStatus", diagnostics.InputBackend.Detail);
    }

    private void RefreshConversionText()
    {
        if (pendingImport is null)
        {
            ConversionText.Text = razerModuleFiles.Count > 0
                ? LF("ConversionModulesLoaded", razerModuleFiles.Count)
                : L("ConversionReady");
            return;
        }

        ConversionText.Text = LF("ConversionImported", pendingImport.SourceFormat, pendingImport.Document.Steps.Count)
            + Environment.NewLine
            + FormatDiagnostics(pendingImport.Diagnostics);
    }

    private MacroConversionFormat GetSelectedExportFormat()
    {
        return ExportFormatBox.SelectedItem is ComboBoxItem { Tag: MacroConversionFormat format }
            ? format
            : MacroConversionFormat.MacroConverterXml;
    }

    private static string FormatFilter(MacroConversionFormat format)
    {
        return MacroConversionService.GetFormats().FirstOrDefault(item => item.Format == format)?.FileDialogFilter
            ?? "All files (*.*)|*.*";
    }

    private static string FormatDiagnostics(IReadOnlyList<MacroConversionDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return L("ConversionDiagnosticsNone");
        }

        return string.Join(
            Environment.NewLine,
            diagnostics.Select(item => $"{item.Severity}: {item.Message}"));
    }

    private void CaptureTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (capturingTrigger)
        {
            StopCapture();
            return;
        }

        capturingTrigger = true;
        SetPlaybackResultResource("LastResultPressTrigger");
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(CaptureTrigger_KeyDown), true);
    }

    private void CaptureTrigger_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
        {
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (!GlobalKeyboardHook.TryMapVirtualKeyToHidKey(virtualKey, out var hidKey))
        {
            SetPlaybackResultResource("LastResultUnsupportedTriggerKey", key);
            StopCapture();
            e.Handled = true;
            return;
        }

        var gesture = new HotkeyGesture(ReadCurrentModifiers(), hidKey);
        TriggerTextBox.Text = gesture.ToString();
        SetPlaybackResultResource("LastResultCapturedTrigger", gesture);
        StopCapture();
        e.Handled = true;
    }

    private async void StartListening_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var document = ParseDocumentWithPlaybackFromControls(applyToEditor: true);
            if (document.Playback.Trigger is null)
            {
                throw new InvalidOperationException(L("ChooseTriggerBeforeListening"));
            }

            keyboardHook?.Dispose();
            keyboardHook = new GlobalKeyboardHook();
            keyboardHook.TriggerPressed += KeyboardHook_TriggerPressed;
            keyboardHook.TriggerReleased += KeyboardHook_TriggerReleased;
            keyboardHook.Start(document.Playback.Trigger);

            playbackController = new MacroPlaybackController(document, new MacroPlaybackExecutor(inputSink));
            listening = true;
            SetPlaybackStatusResource("PlaybackStatusListeningWithTrigger", document.Playback.Trigger);
            SetPlaybackResultResource("HotkeyListenerStarted");
            SetStatusResource("Listening");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            SetPlaybackStatusResource("PlaybackStatusError");
            SetPlaybackResultResource("LastResultMessage", ex.Message);
            SetStatusResource("PlaybackError");
        }
    }

    private void StopListening_Click(object sender, RoutedEventArgs e)
    {
        listening = false;
        keyboardHook?.Dispose();
        keyboardHook = null;
        playbackController?.Stop();
        SetPlaybackStatusResource("PlaybackStatusIdle");
        SetPlaybackResultResource("HotkeyListenerStopped");
        SetStatusResource("Idle");
    }

    private async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var document = ParseDocumentWithPlaybackFromControls(applyToEditor: true);
            playbackController = new MacroPlaybackController(document, new MacroPlaybackExecutor(inputSink));
            await playbackController.RunNowAsync();
            UpdatePlaybackStatus();
            _ = WatchPlaybackAsync(playbackController);
        }
        catch (Exception ex)
        {
            SetPlaybackStatusResource("PlaybackStatusError");
            SetPlaybackResultResource("LastResultMessage", ex.Message);
        }
    }

    private void StopPlayback_Click(object sender, RoutedEventArgs e)
    {
        playbackController?.Stop();
        UpdatePlaybackStatus();
        if (playbackController is not null)
        {
            _ = WatchPlaybackAsync(playbackController);
        }
    }

    private void PlaybackModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaybackCountTextBox is not null)
        {
            PlaybackCountTextBox.IsEnabled = GetSelectedPlaybackMode() == PlaybackMode.FixedCount;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        keyboardHook?.Dispose();
        base.OnClosed(e);
    }

    private void KeyboardHook_TriggerPressed(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (playbackController is null)
            {
                return;
            }

            await playbackController.TriggerPressedAsync();
            UpdatePlaybackStatus();
            _ = WatchPlaybackAsync(playbackController);
        });
    }

    private void KeyboardHook_TriggerReleased(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            playbackController?.TriggerReleased();
            UpdatePlaybackStatus();
            if (playbackController is not null)
            {
                _ = WatchPlaybackAsync(playbackController);
            }
        });
    }

    private async Task WatchPlaybackAsync(MacroPlaybackController controller)
    {
        try
        {
            var result = await controller.WhenIdleAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                SetPlaybackStatusResource(listening ? "PlaybackStatusListening" : PlaybackStatusResourceKey(controller.Status));
                SetPlaybackResultResource("LastResultRunSummary", result.IterationsCompleted, result.ActionsSubmitted, result.Cancelled);
                SetStatusResource(listening ? "Listening" : "Idle");
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SetPlaybackStatusResource("PlaybackStatusError");
                SetPlaybackResultResource("LastResultMessage", ex.Message);
                SetStatusResource("PlaybackError");
            });
        }
    }

    private void UpdatePlaybackStatus()
    {
        if (playbackController is null)
        {
            SetPlaybackStatusResource(listening ? "PlaybackStatusListening" : "PlaybackStatusIdle");
            return;
        }

        SetPlaybackStatusResource(PlaybackStatusResourceKey(playbackController.Status));
        SetStatusResource(StatusResourceKey(playbackController.Status));
    }

    private MacroDocument ParseDocumentWithPlaybackFromControls(bool applyToEditor)
    {
        if (applyToEditor)
        {
            ApplyPlaybackSettingsToEditor();
        }

        return McrxParser.Parse(MacroEditor.Text);
    }

    private void ApplyPlaybackSettingsToEditor()
    {
        var settings = GetPlaybackSettingsFromControls();
        var root = JsonNode.Parse(MacroEditor.Text)?.AsObject()
            ?? throw new JsonException("Macro JSON root must be an object.");
        var playback = new JsonObject
        {
            ["mode"] = ToPlaybackModeText(settings.Mode),
            ["count"] = settings.Count
        };

        if (settings.Trigger is not null)
        {
            playback["trigger"] = settings.Trigger.ToString();
        }

        root["playback"] = playback;
        MacroEditor.Text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        ValidateCurrentMacro();
    }

    private PlaybackSettings GetPlaybackSettingsFromControls()
    {
        var triggerText = TriggerTextBox.Text.Trim();
        var trigger = string.IsNullOrWhiteSpace(triggerText)
            ? null
            : McrxParser.ParseHotkeyGesture(triggerText);
        var mode = GetSelectedPlaybackMode();
        var count = 1;
        if (!string.IsNullOrWhiteSpace(PlaybackCountTextBox.Text)
            && !int.TryParse(PlaybackCountTextBox.Text, out count))
        {
            throw new InvalidOperationException(L("PlaybackCountWholeNumber"));
        }

        if (count < 1)
        {
            throw new InvalidOperationException(L("PlaybackCountAtLeastOne"));
        }

        return new PlaybackSettings(trigger, mode, count);
    }

    private PlaybackMode GetSelectedPlaybackMode()
    {
        var selected = PlaybackModeBox.SelectedItem as ComboBoxItem;
        var value = selected?.Tag?.ToString() ?? "fixedCount";
        return value switch
        {
            "toggleLoop" => PlaybackMode.ToggleLoop,
            "holdLoop" => PlaybackMode.HoldLoop,
            "fixedCount" => PlaybackMode.FixedCount,
            _ => PlaybackMode.FixedCount
        };
    }

    private void SetPlaybackControls(PlaybackSettings settings)
    {
        TriggerTextBox.Text = settings.Trigger?.ToString() ?? string.Empty;
        PlaybackCountTextBox.Text = settings.Count.ToString();
        foreach (var item in PlaybackModeBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), ToPlaybackModeText(settings.Mode), StringComparison.Ordinal))
            {
                PlaybackModeBox.SelectedItem = item;
                break;
            }
        }

        PlaybackCountTextBox.IsEnabled = settings.Mode == PlaybackMode.FixedCount;
    }

    private static string PlaybackStatusResourceKey(PlaybackStatus status)
    {
        return status switch
        {
            PlaybackStatus.Idle => "PlaybackStatusIdle",
            PlaybackStatus.Listening => "PlaybackStatusListening",
            PlaybackStatus.Running => "PlaybackStatusRunning",
            PlaybackStatus.Stopping => "PlaybackStatusStopping",
            PlaybackStatus.InputUnavailable => "PlaybackStatusInputUnavailable",
            PlaybackStatus.Error => "PlaybackStatusError",
            _ => "PlaybackStatusFormat"
        };
    }

    private static string StatusResourceKey(PlaybackStatus status)
    {
        return status switch
        {
            PlaybackStatus.Idle => "Idle",
            PlaybackStatus.Listening => "Listening",
            PlaybackStatus.Error => "PlaybackError",
            _ => PlaybackStatusResourceKey(status)
        };
    }

    private static string ToPlaybackModeText(PlaybackMode mode)
    {
        return mode switch
        {
            PlaybackMode.ToggleLoop => "toggleLoop",
            PlaybackMode.HoldLoop => "holdLoop",
            PlaybackMode.FixedCount => "fixedCount",
            _ => "fixedCount"
        };
    }

    private void StopCapture()
    {
        if (!capturingTrigger)
        {
            return;
        }

        capturingTrigger = false;
        RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(CaptureTrigger_KeyDown));
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
    }

    private static HidModifier ReadCurrentModifiers()
    {
        var modifiers = HidModifier.None;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            modifiers |= HidModifier.LeftCtrl;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            modifiers |= HidModifier.LeftShift;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            modifiers |= HidModifier.LeftAlt;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0)
        {
            modifiers |= HidModifier.LeftGui;
        }

        return modifiers;
    }

    private static string Describe(MacroStep step)
    {
        return step switch
        {
            KeyStep key => $"key.{key.Kind.ToString().ToLowerInvariant()} {key.Modifiers}+{key.Key}",
            TextStep text => $"key.text length={text.Text.Length}",
            MouseMoveStep move => $"mouse.move {move.Mode} x={move.X} y={move.Y} buttons={move.Buttons}",
            MouseButtonStep button => $"mouse.{button.Kind.ToString().ToLowerInvariant()} {button.Button}",
            MouseWheelStep wheel => $"mouse.wheel vertical={wheel.Vertical} horizontal={wheel.Horizontal}",
            ConsumerStep consumer => $"consumer.{consumer.Kind.ToString().ToLowerInvariant()} {consumer.Control}",
            RepeatStep repeat => $"repeat count={repeat.Count} steps={repeat.Steps.Count}",
            PixelWhenStep pixel => $"pixel.when {pixel.Condition.Coordinate.Scope} x={pixel.Condition.Coordinate.X} y={pixel.Condition.Coordinate.Y}",
            _ => step.GetType().Name
        };
    }
}
