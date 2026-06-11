using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MacroHid.Converter;
using MacroHid.Core;
using MacroHid.Runtime;
using Microsoft.Win32;

namespace MacroStudio;

public partial class MainWindow : Window
{
    private const string ActionTemplateDragFormat = "MacroHID.ActionTemplate";
    private const string MacroLibraryDragFormat = "MacroHID.MacroLibraryItem";

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
    private readonly MacroLibraryStore libraryStore = new(MacroLibraryStore.GetDefaultRoot());
    private MacroLibrarySnapshot librarySnapshot = new([], null);
    private string? selectedMacroId;
    private bool suppressMacroSelection;
    private bool updatingMacroName;
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
        InitializeMacroLibrary();
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
        LibraryTitleText.Text = L("MacroLibrary");
        NewMacroButton.Content = L("NewMacro");
        NewFolderButton.Content = L("NewFolder");
        DuplicateMacroButton.Content = L("DuplicateMacro");
        DeleteMacroButton.Content = L("Delete");
        SaveLibraryButton.Content = L("SaveLibrary");
        RunNowHeaderButton.Content = L("RunNow");
        StopHeaderButton.Content = L("Stop");
        SequenceTitleText.Text = L("Sequence");
        AddTitleText.Text = L("Add");
        AddDelayText.Text = L("AddDelay");
        AddKeyboardText.Text = L("AddKeyboard");
        AddMouseText.Text = L("AddMouseButton");
        AddMouseMoveText.Text = L("AddMouseMove");
        AddWheelText.Text = L("AddWheel");
        AddTextActionText.Text = L("AddText");
        AddLoopText.Text = L("AddLoop");
        AddPixelText.Text = L("AddPixel");
        AddMacroHintText.Text = L("AddMacroHint");
        DurationLabelText.Text = L("Duration");
        StepUpButton.Content = L("MoveUp");
        StepDownButton.Content = L("MoveDown");
        StepDeleteButton.Content = L("Delete");
        NameLabelText.Text = L("Name");
        ScheduledStepsLabelText.Text = L("ScheduledSteps");
        PlaybackTitleText.Text = L("Playback");
        TriggerLabelText.Text = L("Trigger");
        CaptureTriggerButton.Content = L("Capture");
        ModeLabelText.Text = L("Mode");
        PlaybackCountLabelText.Text = L("Count");
        StartListeningButton.Content = L("StartListening");
        StopListeningButton.Content = L("StopListening");
        RunNowButton.Content = L("RunNow");
        StopPlaybackButton.Content = L("Stop");
        DiagnosticsTitleText.Text = L("Diagnostics");
        ConversionTitleText.Text = L("ImportExport");
        ImportMacroButton.Content = L("ImportMacro");
        ImportRazerModulesButton.Content = L("ImportRazerModules");
        ApplyImportButton.Content = L("ApplyImport");
        ExportFormatLabelText.Text = L("ExportFormat");
        ExportMacroButton.Content = L("ExportMacro");
        MacroSearchBox.ToolTip = L("SearchMacros");
        AdvancedJsonExpander.Header = L("AdvancedJson");

        foreach (var item in LibrarySortBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = item.Tag?.ToString() switch
            {
                "updated" => L("SortRecentlyUpdated"),
                _ => L("SortName")
            };
        }

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
        RefreshMacroLibraryList();
    }

    private void TopChromeBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsChromeInteractiveSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleWindowMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove can throw if Windows releases capture between the click and drag.
            }
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowMaximize();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static bool IsChromeInteractiveSource(DependencyObject? source)
    {
        return FindVisualParent<ButtonBase>(source) is not null
            || FindVisualParent<ComboBox>(source) is not null
            || FindVisualParent<TextBoxBase>(source) is not null;
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private void InitializeMacroLibrary()
    {
        librarySnapshot = libraryStore.Load();
        if (librarySnapshot.Items.Count == 0)
        {
            libraryStore.CreateMacro(McrxParser.Parse(SampleMacro));
            librarySnapshot = libraryStore.Load();
        }

        selectedMacroId = librarySnapshot.SelectedMacroId ?? librarySnapshot.Items.FirstOrDefault()?.Id;
        RefreshMacroLibraryList();
        if (selectedMacroId is not null)
        {
            LoadMacroFromLibrary(selectedMacroId);
        }
        else
        {
            SetEditorDocument(new MacroDocument(1, "Macro 1", PlaybackSettings.Default, []));
        }
    }

    private void RefreshMacroLibraryList()
    {
        librarySnapshot = libraryStore.Load();
        var selectedId = selectedMacroId ?? librarySnapshot.SelectedMacroId;
        var search = MacroSearchBox.Text.Trim();
        var sortMode = (LibrarySortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "name";
        var items = librarySnapshot.Items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            items = items.Where(item =>
                item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Folder.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        items = sortMode == "updated"
            ? items.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            : items.OrderBy(item => string.IsNullOrWhiteSpace(item.Folder) ? L("Ungrouped") : item.Folder, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);

        suppressMacroSelection = true;
        MacroListBox.Items.Clear();
        var grouped = items
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Folder) ? L("Ungrouped") : item.Folder)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);

        MacroLibraryListEntry? selectedEntry = null;
        foreach (var group in grouped)
        {
            MacroListBox.Items.Add(MacroLibraryListEntry.Header(group.Key));
            foreach (var item in group)
            {
                var entry = MacroLibraryListEntry.Macro(item);
                MacroListBox.Items.Add(entry);
                if (item.Id == selectedId)
                {
                    selectedEntry = entry;
                }
            }
        }

        MacroListBox.SelectedItem = selectedEntry;
        suppressMacroSelection = false;
    }

    private void MacroSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshMacroLibraryList();
    }

    private void LibrarySortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MacroListBox is not null)
        {
            RefreshMacroLibraryList();
        }
    }

    private void MacroListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressMacroSelection)
        {
            return;
        }

        if (MacroListBox.SelectedItem is not MacroLibraryListEntry { Item: { } item })
        {
            return;
        }

        selectedMacroId = item.Id;
        libraryStore.SetSelected(item.Id);
        LoadMacroFromLibrary(item.Id);
    }

    private void MacroListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (MacroListBox.SelectedItem is not MacroLibraryListEntry { Item: { } item })
        {
            return;
        }

        DragDrop.DoDragDrop(MacroListBox, new DataObject(MacroLibraryDragFormat, item.Id), DragDropEffects.Copy);
    }

    private void LoadMacroFromLibrary(string id)
    {
        try
        {
            selectedMacroId = id;
            var document = libraryStore.ReadMacro(id);
            SetEditorDocument(document);
            SetStatusPlainText(document.Name);
        }
        catch (Exception ex)
        {
            SetStatusPlainText(ex.Message);
        }
    }

    private void SetEditorDocument(MacroDocument document)
    {
        MacroEditor.Text = McrxSerializer.Serialize(document);
        ValidateCurrentMacro();
    }

    private void NewMacro_Click(object sender, RoutedEventArgs e)
    {
        var name = NextMacroName("Macro");
        var item = libraryStore.CreateMacro(name);
        selectedMacroId = item.Id;
        RefreshMacroLibraryList();
        LoadMacroFromLibrary(item.Id);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = NextFolderName();
        var item = libraryStore.CreateMacro(NextMacroName("Macro"), folder);
        selectedMacroId = item.Id;
        RefreshMacroLibraryList();
        LoadMacroFromLibrary(item.Id);
    }

    private void DuplicateMacro_Click(object sender, RoutedEventArgs e)
    {
        if (selectedMacroId is null)
        {
            return;
        }

        try
        {
            var document = McrxParser.Parse(MacroEditor.Text);
            libraryStore.SaveMacro(selectedMacroId, document);
            var item = libraryStore.DuplicateMacro(selectedMacroId, $"{document.Name} Copy");
            selectedMacroId = item.Id;
            RefreshMacroLibraryList();
            LoadMacroFromLibrary(item.Id);
        }
        catch (Exception ex)
        {
            SetPlaybackResultResource("LastResultMessage", ex.Message);
        }
    }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if (selectedMacroId is null)
        {
            return;
        }

        var name = MacroNameBox.Text.Trim();
        var result = MessageBox.Show(
            this,
            LF("DeleteMacroConfirm", string.IsNullOrWhiteSpace(name) ? L("Macro") : name),
            L("Delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        libraryStore.DeleteMacro(selectedMacroId);
        librarySnapshot = libraryStore.Load();
        selectedMacroId = librarySnapshot.SelectedMacroId;
        RefreshMacroLibraryList();
        if (selectedMacroId is not null)
        {
            LoadMacroFromLibrary(selectedMacroId);
        }
        else
        {
            SetEditorDocument(new MacroDocument(1, NextMacroName("Macro"), PlaybackSettings.Default, []));
        }
    }

    private void SaveLibrary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var document = ParseDocumentWithPlaybackFromControls(applyToEditor: true);
            MacroLibraryItem item;
            if (selectedMacroId is null)
            {
                item = libraryStore.CreateMacro(document);
            }
            else
            {
                item = libraryStore.SaveMacro(selectedMacroId, document);
            }

            selectedMacroId = item.Id;
            RefreshMacroLibraryList();
            SetStatusResource("LibrarySaved");
        }
        catch (Exception ex)
        {
            SetPlaybackResultResource("LastResultMessage", ex.Message);
            SetStatusResource("MacroInvalid");
        }
    }

    private string NextMacroName(string prefix)
    {
        var used = libraryStore.Load().Items.Select(item => item.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{prefix} {i}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{prefix} {DateTime.Now:HHmmss}";
    }

    private string NextFolderName()
    {
        var used = libraryStore.Load().Items.Select(item => item.Folder).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{L("Folder")} {i}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{L("Folder")} {DateTime.Now:HHmmss}";
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
        if (pendingImportJson is null || pendingImport is null)
        {
            return;
        }

        var item = libraryStore.CreateMacro(pendingImport.Document);
        selectedMacroId = item.Id;
        RefreshMacroLibraryList();
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
            var selectedStepIndex = StepList.SelectedItem is StepDisplayItem selectedStep ? selectedStep.Index : -1;

            MacroNameText.Text = document.Name;
            updatingMacroName = true;
            MacroNameBox.Text = document.Name;
            updatingMacroName = false;
            StepCountText.Text = scheduled.Count.ToString();
            DurationText.Text = FormatDuration(EstimateDuration(document.Steps));
            StepList.Items.Clear();

            for (var i = 0; i < document.Steps.Count; i++)
            {
                StepList.Items.Add(StepDisplayItem.FromStep(i, document.Steps[i], Describe(document.Steps[i]), FormatStepBadge(document.Steps[i])));
            }

            if (selectedStepIndex >= 0 && selectedStepIndex < StepList.Items.Count)
            {
                StepList.SelectedIndex = selectedStepIndex;
            }

            RefreshSelectedStepText();
            SetStatusResource("MacroValid");
            SetPlaybackControls(document.Playback);
        }
        catch (Exception ex)
        {
            MacroNameText.Text = "-";
            updatingMacroName = true;
            MacroNameBox.Text = string.Empty;
            updatingMacroName = false;
            StepCountText.Text = "0";
            DurationText.Text = "0 ms";
            StepList.Items.Clear();
            StepList.Items.Add(StepDisplayItem.Error(ex.Message));
            SelectedStepText.Text = ex.Message;
            SetStatusResource("MacroInvalid");
        }
    }

    private void ActionPalette_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            AddTemplateFromTag(button.Tag?.ToString());
        }
    }

    private void ActionPalette_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not Button button)
        {
            return;
        }

        if (button.Tag?.ToString() is not { Length: > 0 } tag)
        {
            return;
        }

        DragDrop.DoDragDrop(button, new DataObject(ActionTemplateDragFormat, tag), DragDropEffects.Copy);
    }

    private void StepList_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(ActionTemplateDragFormat)
                && e.Data.GetData(ActionTemplateDragFormat) is string template)
            {
                AddTemplateFromTag(template);
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(MacroLibraryDragFormat)
                && e.Data.GetData(MacroLibraryDragFormat) is string macroId)
            {
                var document = libraryStore.ReadMacro(macroId);
                InsertSteps(document.Steps);
                SetPlaybackResultResource("LastResultMessage", LF("InsertedMacroSteps", document.Name, document.Steps.Count));
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            SetPlaybackResultResource("LastResultMessage", ex.Message);
        }
    }

    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSelectedStepText();
    }

    private void StepUp_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedStep(-1);
    }

    private void StepDown_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedStep(1);
    }

    private void StepDelete_Click(object sender, RoutedEventArgs e)
    {
        if (StepList.SelectedItem is not StepDisplayItem { Index: >= 0 } selected)
        {
            return;
        }

        try
        {
            var document = McrxParser.Parse(MacroEditor.Text);
            var steps = document.Steps.ToList();
            if (selected.Index >= steps.Count)
            {
                return;
            }

            steps.RemoveAt(selected.Index);
            SetEditorDocument(document with { Steps = steps });
            if (steps.Count > 0)
            {
                StepList.SelectedIndex = Math.Min(selected.Index, steps.Count - 1);
            }
        }
        catch (Exception ex)
        {
            SetPlaybackResultResource("LastResultMessage", ex.Message);
        }
    }

    private void MacroNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyMacroNameFromBox();
    }

    private void MacroNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ApplyMacroNameFromBox();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void AddTemplateFromTag(string? tag)
    {
        if (!Enum.TryParse<MacroActionTemplateKind>(tag, ignoreCase: true, out var kind))
        {
            return;
        }

        InsertSteps([MacroActionTemplateFactory.CreateStep(kind)]);
        SetPlaybackResultResource("LastResultMessage", LF("ActionInserted", L($"Template{kind}")));
    }

    private void InsertSteps(IReadOnlyList<MacroStep> stepsToInsert)
    {
        if (stepsToInsert.Count == 0)
        {
            return;
        }

        var document = McrxParser.Parse(MacroEditor.Text);
        var steps = document.Steps.ToList();
        var insertAt = StepList.SelectedItem is StepDisplayItem { Index: >= 0 } selected
            ? selected.Index + 1
            : steps.Count;
        steps.InsertRange(insertAt, stepsToInsert);
        SetEditorDocument(document with { Steps = steps });
        StepList.SelectedIndex = insertAt;
    }

    private void MoveSelectedStep(int offset)
    {
        if (StepList.SelectedItem is not StepDisplayItem { Index: >= 0 } selected)
        {
            return;
        }

        try
        {
            var document = McrxParser.Parse(MacroEditor.Text);
            var steps = document.Steps.ToList();
            var target = selected.Index + offset;
            if (selected.Index < 0 || selected.Index >= steps.Count || target < 0 || target >= steps.Count)
            {
                return;
            }

            (steps[selected.Index], steps[target]) = (steps[target], steps[selected.Index]);
            SetEditorDocument(document with { Steps = steps });
            StepList.SelectedIndex = target;
        }
        catch (Exception ex)
        {
            SetPlaybackResultResource("LastResultMessage", ex.Message);
        }
    }

    private void ApplyMacroNameFromBox()
    {
        if (updatingMacroName)
        {
            return;
        }

        try
        {
            var document = McrxParser.Parse(MacroEditor.Text);
            var name = string.IsNullOrWhiteSpace(MacroNameBox.Text) ? document.Name : MacroNameBox.Text.Trim();
            if (string.Equals(document.Name, name, StringComparison.Ordinal))
            {
                return;
            }

            SetEditorDocument(document with { Name = name });
        }
        catch (Exception ex)
        {
            SetPlaybackResultResource("LastResultMessage", ex.Message);
        }
    }

    private void RefreshSelectedStepText()
    {
        SelectedStepText.Text = StepList.SelectedItem is StepDisplayItem { Index: >= 0 } selected
            ? $"{selected.Title}: {selected.Subtitle}"
            : L("DropActionsHint");
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
        var macroName = MacroNameBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(macroName))
        {
            root["name"] = macroName;
        }

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
        MacroEditor.Text = root.ToJsonString(CreateIndentedJsonOptions());
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

    private static JsonSerializerOptions CreateIndentedJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
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

    private static TimeSpan EstimateDuration(IReadOnlyList<MacroStep> steps)
    {
        var ticks = 0L;
        foreach (var step in steps)
        {
            ticks += EstimateStepDuration(step).Ticks;
        }

        return TimeSpan.FromTicks(ticks);
    }

    private static TimeSpan EstimateStepDuration(MacroStep step)
    {
        return step switch
        {
            KeyStep key => key.Hold,
            MouseMoveStep move => move.Duration,
            MouseButtonStep button => button.Hold,
            ConsumerStep consumer => consumer.Hold,
            WaitStep wait => wait.Duration,
            RepeatStep repeat => TimeSpan.FromTicks(EstimateDuration(repeat.Steps).Ticks * Math.Max(0, repeat.Count)),
            PixelWhenStep pixel => EstimateDuration(pixel.ThenSteps),
            _ => TimeSpan.Zero
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds >= 1)
        {
            return $"{duration.TotalSeconds:0.###} s";
        }

        return $"{duration.TotalMilliseconds:0.###} ms";
    }

    private static string FormatStepBadge(MacroStep step)
    {
        var duration = EstimateStepDuration(step);
        return duration > TimeSpan.Zero ? FormatDuration(duration) : string.Empty;
    }

    private static string Describe(MacroStep step)
    {
        return step switch
        {
            KeyStep key => key.Modifiers == HidModifier.None
                ? $"key.{key.Kind.ToString().ToLowerInvariant()} {key.Key}"
                : $"key.{key.Kind.ToString().ToLowerInvariant()} {key.Modifiers}+{key.Key}",
            TextStep text => $"key.text \"{TrimForDisplay(text.Text)}\"",
            MouseMoveStep move => $"mouse.move {move.Mode} x={move.X} y={move.Y}",
            MouseButtonStep button => $"mouse.{button.Kind.ToString().ToLowerInvariant()} {button.Button}",
            MouseWheelStep wheel => $"mouse.wheel vertical={wheel.Vertical} horizontal={wheel.Horizontal}",
            ConsumerStep consumer => $"consumer.{consumer.Kind.ToString().ToLowerInvariant()} {consumer.Control}",
            RepeatStep repeat => $"repeat count={repeat.Count} steps={repeat.Steps.Count}",
            PixelWhenStep pixel => $"pixel.when {pixel.Condition.Coordinate.Scope} x={pixel.Condition.Coordinate.X} y={pixel.Condition.Coordinate.Y}",
            _ => step.GetType().Name
        };
    }

    private static string TrimForDisplay(string value)
    {
        return value.Length <= 32 ? value : $"{value[..29]}...";
    }
}

public sealed class MacroLibraryListEntry
{
    private static readonly Brush HeaderBrush = new SolidColorBrush(Color.FromRgb(158, 164, 170));
    private static readonly Brush MacroBrush = new SolidColorBrush(Color.FromRgb(101, 216, 78));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(244, 246, 248));

    private MacroLibraryListEntry(MacroLibraryItem? item, string title, string subtitle, string icon, Brush accent, FontWeight weight, Brush titleBrush)
    {
        Item = item;
        Title = title;
        Subtitle = subtitle;
        Icon = icon;
        Accent = accent;
        Weight = weight;
        TitleBrush = titleBrush;
    }

    public MacroLibraryItem? Item { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Icon { get; }

    public Brush Accent { get; }

    public FontWeight Weight { get; }

    public Brush TitleBrush { get; }

    public static MacroLibraryListEntry Header(string title)
    {
        return new MacroLibraryListEntry(null, title, string.Empty, "F", HeaderBrush, FontWeights.SemiBold, HeaderBrush);
    }

    public static MacroLibraryListEntry Macro(MacroLibraryItem item)
    {
        var subtitle = item.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return new MacroLibraryListEntry(item, item.Name, subtitle, "M", MacroBrush, FontWeights.Normal, TextBrush);
    }
}

public sealed class StepDisplayItem
{
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(101, 216, 78));
    private static readonly Brush BlueBrush = new SolidColorBrush(Color.FromRgb(74, 163, 255));
    private static readonly Brush OrangeBrush = new SolidColorBrush(Color.FromRgb(255, 159, 67));
    private static readonly Brush PinkBrush = new SolidColorBrush(Color.FromRgb(231, 90, 165));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(229, 83, 92));
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(158, 164, 170));

    private StepDisplayItem(int index, string icon, string title, string subtitle, string badge, Brush accentBrush)
    {
        Index = index;
        Icon = icon;
        Title = title;
        Subtitle = subtitle;
        Badge = badge;
        AccentBrush = accentBrush;
    }

    public int Index { get; }

    public string Icon { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Badge { get; }

    public Brush AccentBrush { get; }

    public static StepDisplayItem FromStep(int index, MacroStep step, string subtitle, string badge)
    {
        return step switch
        {
            KeyStep => new StepDisplayItem(index, "K", $"#{index + 1} Key", subtitle, badge, GreenBrush),
            TextStep => new StepDisplayItem(index, "T", $"#{index + 1} Text", subtitle, badge, BlueBrush),
            MouseMoveStep => new StepDisplayItem(index, "XY", $"#{index + 1} Move", subtitle, badge, OrangeBrush),
            MouseButtonStep => new StepDisplayItem(index, "M", $"#{index + 1} Mouse", subtitle, badge, OrangeBrush),
            MouseWheelStep => new StepDisplayItem(index, "W", $"#{index + 1} Wheel", subtitle, badge, BlueBrush),
            ConsumerStep => new StepDisplayItem(index, "C", $"#{index + 1} Media", subtitle, badge, PinkBrush),
            WaitStep => new StepDisplayItem(index, "D", $"#{index + 1} Delay", subtitle, badge, GrayBrush),
            RepeatStep => new StepDisplayItem(index, "R", $"#{index + 1} Loop", subtitle, badge, RedBrush),
            PixelWhenStep => new StepDisplayItem(index, "P", $"#{index + 1} Pixel", subtitle, badge, PinkBrush),
            _ => new StepDisplayItem(index, "?", $"#{index + 1} Step", subtitle, badge, GrayBrush)
        };
    }

    public static StepDisplayItem Error(string message)
    {
        return new StepDisplayItem(-1, "!", "Invalid macro", message, string.Empty, RedBrush);
    }
}
