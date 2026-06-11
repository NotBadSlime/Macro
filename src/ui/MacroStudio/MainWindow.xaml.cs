using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
using CoreMouseButton = MacroHid.Core.MouseButton;

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
    private bool capturingStepKey;
    private bool updatingLanguageComboBox;
    private bool updatingStepEditor;
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
        InitializeStepEditorControls();
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

    private void InitializeStepEditorControls()
    {
        FillEnumBox(ActionKindBox, ButtonActionKind.Click);
        FillEnumBox(MouseButtonBox, CoreMouseButton.Left);
        FillEnumBox(MoveModeBox, MouseMoveMode.Relative);
        RefreshMacroTargetBox();
        StepEditorFieldsPanel.Visibility = Visibility.Collapsed;
        ApplyStepEditButton.IsEnabled = false;
    }

    private static void FillEnumBox<TEnum>(ComboBox comboBox, TEnum selected)
        where TEnum : struct, Enum
    {
        comboBox.Items.Clear();
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (value.ToString() == "None")
            {
                continue;
            }

            var item = new ComboBoxItem
            {
                Tag = value,
                Content = value.ToString()
            };
            comboBox.Items.Add(item);
            if (EqualityComparer<TEnum>.Default.Equals(value, selected))
            {
                comboBox.SelectedItem = item;
            }
        }
    }

    private void RefreshMacroTargetBox(string? selected = null)
    {
        if (MacroTargetBox is null)
        {
            return;
        }

        var previous = selected ?? (MacroTargetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        MacroTargetBox.Items.Clear();
        foreach (var item in libraryStore.Load().Items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var comboItem = new ComboBoxItem
            {
                Tag = item.Name,
                Content = item.Name
            };
            MacroTargetBox.Items.Add(comboItem);
            if (string.Equals(item.Name, previous, StringComparison.CurrentCultureIgnoreCase)
                || string.Equals(item.Id, previous, StringComparison.OrdinalIgnoreCase))
            {
                MacroTargetBox.SelectedItem = comboItem;
            }
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
        AddMacroText.Text = L("AddMacro");
        AddLoopText.Text = L("AddLoop");
        AddPixelText.Text = L("AddPixel");
        AddMacroHintText.Text = L("AddMacroHint");
        StepEditorTitleText.Text = L("StepProperties");
        StepEditorHintText.Text = L("StepPropertiesHint");
        ApplyStepEditButton.Content = L("Apply");
        MacroTargetLabelText.Text = L("Macro");
        KeyLabelText.Text = L("Key");
        CaptureStepKeyButton.Content = L("Capture");
        ActionKindLabelText.Text = L("Action");
        MouseButtonLabelText.Text = L("AddMouseButton");
        MoveModeLabelText.Text = L("MoveMode");
        WheelLabelText.Text = L("AddWheel");
        TimingLabelText.Text = L("TimingMs");
        TextActionLabelText.Text = L("AddText");
        LoopCountLabelText.Text = L("LoopCount");
        PixelLabelText.Text = L("PixelIf");
        PickPixelColorButton.Content = L("PickColor");
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
        RefreshMacroTargetBox();
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
        var importedCount = 0;
        foreach (var file in razerModuleFiles)
        {
            try
            {
                var imported = MacroConversionService.ImportToMcrx(new MacroImportRequest(
                    file.Content,
                    file.FileName,
                    MacroConversionFormat.RazerSynapseXml,
                    []));
                libraryStore.CreateMacro(imported.Document);
                importedCount++;
            }
            catch
            {
                // Some Synapse module files only serve as references for a parent macro.
            }
        }

        if (importedCount > 0)
        {
            RefreshMacroLibraryList();
        }

        RefreshConversionText();
        SetPlaybackResultResource("LastResultMessage", LF("ConversionModulesImported", razerModuleFiles.Count, importedCount));
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
        RefreshStepEditor();
    }

    private void RefreshStepEditor()
    {
        if (StepList.SelectedItem is not StepDisplayItem { Index: >= 0 } selected)
        {
            StepEditorFieldsPanel.Visibility = Visibility.Collapsed;
            ApplyStepEditButton.IsEnabled = false;
            StepEditorHintText.Text = L("StepPropertiesHint");
            return;
        }

        try
        {
            var document = McrxParser.Parse(MacroEditor.Text);
            if (selected.Index >= document.Steps.Count)
            {
                return;
            }

            updatingStepEditor = true;
            PopulateStepEditor(document.Steps[selected.Index]);
            updatingStepEditor = false;
            StepEditorFieldsPanel.Visibility = Visibility.Visible;
            ApplyStepEditButton.IsEnabled = true;
            StepEditorHintText.Text = $"{selected.Title}: {selected.Subtitle}";
        }
        catch (Exception ex)
        {
            StepEditorFieldsPanel.Visibility = Visibility.Collapsed;
            ApplyStepEditButton.IsEnabled = false;
            StepEditorHintText.Text = ex.Message;
        }
    }

    private void PopulateStepEditor(MacroStep step)
    {
        SetEditorPanels(
            keyboard: step is KeyStep,
            action: step is KeyStep or MouseButtonStep or ConsumerStep,
            mouseButton: step is MouseButtonStep,
            mouseMove: step is MouseMoveStep,
            wheel: step is MouseWheelStep,
            timing: step is KeyStep or MouseButtonStep or ConsumerStep or MouseMoveStep or WaitStep,
            text: step is TextStep,
            loop: step is RepeatStep,
            macro: step is MacroCallStep,
            pixel: step is PixelWhenStep);

        switch (step)
        {
            case KeyStep key:
                StepKeyBox.Text = key.Key.ToString();
                SetModifierBoxes(key.Modifiers);
                SetComboBox(ActionKindBox, key.Kind.ToString());
                TimingMsBox.Text = FormatNumber(key.Hold.TotalMilliseconds);
                break;
            case MouseButtonStep button:
                SetComboBox(MouseButtonBox, button.Button.ToString());
                SetComboBox(ActionKindBox, button.Kind.ToString());
                TimingMsBox.Text = FormatNumber(button.Hold.TotalMilliseconds);
                break;
            case MouseMoveStep move:
                SetComboBox(MoveModeBox, move.Mode.ToString());
                MoveXBox.Text = move.X.ToString();
                MoveYBox.Text = move.Y.ToString();
                TimingMsBox.Text = FormatNumber(move.Duration.TotalMilliseconds);
                break;
            case MouseWheelStep wheel:
                WheelVerticalBox.Text = wheel.Vertical.ToString();
                WheelHorizontalBox.Text = wheel.Horizontal.ToString();
                break;
            case WaitStep wait:
                TimingMsBox.Text = FormatNumber(wait.Duration.TotalMilliseconds);
                break;
            case TextStep text:
                StepTextBox.Text = text.Text;
                break;
            case RepeatStep repeat:
                LoopCountBox.Text = repeat.Count.ToString();
                break;
            case MacroCallStep macro:
                RefreshMacroTargetBox(macro.Macro);
                break;
            case PixelWhenStep pixel:
                PixelXBox.Text = pixel.Condition.Coordinate.X.ToString();
                PixelYBox.Text = pixel.Condition.Coordinate.Y.ToString();
                PixelToleranceBox.Text = pixel.Condition.Tolerance.ToString();
                PixelRBox.Text = pixel.Condition.Expected.R.ToString();
                PixelGBox.Text = pixel.Condition.Expected.G.ToString();
                PixelBBox.Text = pixel.Condition.Expected.B.ToString();
                PixelWindowStartBox.Text = pixel.WindowStart is { } start ? FormatNumber(start.TotalMilliseconds) : string.Empty;
                PixelWindowEndBox.Text = pixel.WindowEnd is { } end ? FormatNumber(end.TotalMilliseconds) : string.Empty;
                PixelPollBox.Text = pixel.PollInterval is { } poll ? FormatNumber(poll.TotalMilliseconds) : string.Empty;
                UpdatePixelPreview(pixel.Condition.Expected);
                break;
        }
    }

    private void SetEditorPanels(
        bool keyboard,
        bool action,
        bool mouseButton,
        bool mouseMove,
        bool wheel,
        bool timing,
        bool text,
        bool loop,
        bool macro,
        bool pixel)
    {
        KeyboardEditPanel.Visibility = ToVisibility(keyboard);
        ButtonEditPanel.Visibility = ToVisibility(action);
        MouseButtonEditPanel.Visibility = ToVisibility(mouseButton);
        MouseMoveEditPanel.Visibility = ToVisibility(mouseMove);
        WheelEditPanel.Visibility = ToVisibility(wheel);
        TimingEditPanel.Visibility = ToVisibility(timing);
        TextEditPanel.Visibility = ToVisibility(text);
        LoopEditPanel.Visibility = ToVisibility(loop);
        MacroEditPanel.Visibility = ToVisibility(macro);
        PixelEditPanel.Visibility = ToVisibility(pixel);
    }

    private static Visibility ToVisibility(bool visible)
    {
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyStepEdit_Click(object sender, RoutedEventArgs e)
    {
        if (updatingStepEditor || StepList.SelectedItem is not StepDisplayItem { Index: >= 0 } selected)
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

            steps[selected.Index] = BuildEditedStep(steps[selected.Index]);
            SetEditorDocument(document with { Steps = steps });
            StepList.SelectedIndex = selected.Index;
            SetPlaybackResultResource("LastResultMessage", L("StepPropertiesApplied"));
        }
        catch (Exception ex)
        {
            SetPlaybackResultResource("LastResultMessage", ex.Message);
        }
    }

    private MacroStep BuildEditedStep(MacroStep current)
    {
        return current switch
        {
            KeyStep key => key with
            {
                Key = ParseHidKeyFromText(StepKeyBox.Text),
                Kind = GetComboBoxEnum<KeyActionKind>(ActionKindBox),
                Modifiers = ReadStepModifiers(),
                Hold = TimeSpan.FromMilliseconds(ReadDouble(TimingMsBox.Text, 0))
            },
            MouseButtonStep button => button with
            {
                Button = GetComboBoxEnum<CoreMouseButton>(MouseButtonBox),
                Kind = GetComboBoxEnum<ButtonActionKind>(ActionKindBox),
                Hold = TimeSpan.FromMilliseconds(ReadDouble(TimingMsBox.Text, 0))
            },
            MouseMoveStep move => move with
            {
                Mode = GetComboBoxEnum<MouseMoveMode>(MoveModeBox),
                X = ReadInt(MoveXBox.Text, 0),
                Y = ReadInt(MoveYBox.Text, 0),
                Duration = TimeSpan.FromMilliseconds(ReadDouble(TimingMsBox.Text, 0))
            },
            MouseWheelStep wheel => wheel with
            {
                Vertical = ReadInt(WheelVerticalBox.Text, 0),
                Horizontal = ReadInt(WheelHorizontalBox.Text, 0)
            },
            WaitStep => new WaitStep(TimeSpan.FromMilliseconds(ReadDouble(TimingMsBox.Text, 0))),
            TextStep => new TextStep(StepTextBox.Text),
            RepeatStep repeat => repeat with { Count = Math.Max(1, ReadInt(LoopCountBox.Text, repeat.Count)) },
            MacroCallStep => new MacroCallStep(ReadSelectedMacroName()),
            PixelWhenStep pixel => BuildEditedPixelStep(pixel),
            _ => current
        };
    }

    private PixelWhenStep BuildEditedPixelStep(PixelWhenStep current)
    {
        var color = new RgbColor(
            checked((byte)Math.Clamp(ReadInt(PixelRBox.Text, current.Condition.Expected.R), 0, 255)),
            checked((byte)Math.Clamp(ReadInt(PixelGBox.Text, current.Condition.Expected.G), 0, 255)),
            checked((byte)Math.Clamp(ReadInt(PixelBBox.Text, current.Condition.Expected.B), 0, 255)));
        var condition = new PixelCondition(
            new PixelCoordinate(
                current.Condition.Coordinate.Scope,
                ReadInt(PixelXBox.Text, current.Condition.Coordinate.X),
                ReadInt(PixelYBox.Text, current.Condition.Coordinate.Y),
                current.Condition.Coordinate.WindowTitle),
            color,
            checked((byte)Math.Clamp(ReadInt(PixelToleranceBox.Text, current.Condition.Tolerance), 0, 255)));
        UpdatePixelPreview(color);
        return current with
        {
            Condition = condition,
            WindowStart = ReadOptionalTimeSpan(PixelWindowStartBox.Text),
            WindowEnd = ReadOptionalTimeSpan(PixelWindowEndBox.Text),
            PollInterval = ReadOptionalTimeSpan(PixelPollBox.Text)
        };
    }

    private void CaptureStepKey_Click(object sender, RoutedEventArgs e)
    {
        if (capturingStepKey)
        {
            StopStepKeyCapture();
            return;
        }

        capturingStepKey = true;
        SetPlaybackResultResource("LastResultPressTrigger");
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(CaptureStepKey_KeyDown), true);
    }

    private void CaptureStepKey_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
        {
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (GlobalKeyboardHook.TryMapVirtualKeyToHidKey(virtualKey, out var hidKey))
        {
            StepKeyBox.Text = hidKey.ToString();
        }

        StopStepKeyCapture();
        e.Handled = true;
    }

    private void StopStepKeyCapture()
    {
        if (!capturingStepKey)
        {
            return;
        }

        capturingStepKey = false;
        RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(CaptureStepKey_KeyDown));
    }

    private void PickPixelColor_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPickScreenPixel(out var x, out var y, out var color))
        {
            SetPlaybackResultResource("LastResultMessage", L("PickColorFailed"));
            return;
        }

        PixelXBox.Text = x.ToString();
        PixelYBox.Text = y.ToString();
        PixelRBox.Text = color.R.ToString();
        PixelGBox.Text = color.G.ToString();
        PixelBBox.Text = color.B.ToString();
        UpdatePixelPreview(color);
    }

    private static bool TryPickScreenPixel(out int x, out int y, out RgbColor color)
    {
        x = 0;
        y = 0;
        color = new RgbColor(0, 0, 0);
        if (!GetCursorPos(out var point))
        {
            return false;
        }

        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var pixel = GetPixel(dc, point.X, point.Y);
            if (pixel == 0xFFFF_FFFF)
            {
                return false;
            }

            x = point.X;
            y = point.Y;
            color = new RgbColor(
                (byte)(pixel & 0xFF),
                (byte)((pixel >> 8) & 0xFF),
                (byte)((pixel >> 16) & 0xFF));
            return true;
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private void UpdatePixelPreview(RgbColor color)
    {
        PixelColorPreview.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
    }

    private string ReadSelectedMacroName()
    {
        return (MacroTargetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? MacroTargetBox.Text.Trim();
    }

    private HidModifier ReadStepModifiers()
    {
        var modifiers = HidModifier.None;
        if (StepCtrlBox.IsChecked == true)
        {
            modifiers |= HidModifier.LeftCtrl;
        }

        if (StepShiftBox.IsChecked == true)
        {
            modifiers |= HidModifier.LeftShift;
        }

        if (StepAltBox.IsChecked == true)
        {
            modifiers |= HidModifier.LeftAlt;
        }

        if (StepWinBox.IsChecked == true)
        {
            modifiers |= HidModifier.LeftGui;
        }

        return modifiers;
    }

    private void SetModifierBoxes(HidModifier modifiers)
    {
        StepCtrlBox.IsChecked = (modifiers & (HidModifier.LeftCtrl | HidModifier.RightCtrl)) != 0;
        StepShiftBox.IsChecked = (modifiers & (HidModifier.LeftShift | HidModifier.RightShift)) != 0;
        StepAltBox.IsChecked = (modifiers & (HidModifier.LeftAlt | HidModifier.RightAlt)) != 0;
        StepWinBox.IsChecked = (modifiers & (HidModifier.LeftGui | HidModifier.RightGui)) != 0;
    }

    private static void SetComboBox(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private static TEnum GetComboBoxEnum<TEnum>(ComboBox comboBox)
        where TEnum : struct, Enum
    {
        if (comboBox.SelectedItem is ComboBoxItem { Tag: TEnum value })
        {
            return value;
        }

        return Enum.TryParse<TEnum>(comboBox.Text, ignoreCase: true, out var parsed)
            ? parsed
            : Enum.GetValues<TEnum>()[0];
    }

    private static HidKey ParseHidKeyFromText(string value)
    {
        var text = value.Trim();
        if (text.Length == 1 && char.IsDigit(text[0]))
        {
            text = $"D{text}";
        }

        if (!Enum.TryParse<HidKey>(text, ignoreCase: true, out var key) || key == HidKey.None)
        {
            throw new InvalidOperationException($"Unsupported key '{value}'.");
        }

        return key;
    }

    private static int ReadInt(string value, int defaultValue)
    {
        return int.TryParse(value.Trim(), out var parsed) ? parsed : defaultValue;
    }

    private static double ReadDouble(string value, double defaultValue)
    {
        return double.TryParse(value.Trim(), out var parsed) ? parsed : defaultValue;
    }

    private static TimeSpan? ReadOptionalTimeSpan(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : TimeSpan.FromMilliseconds(ReadDouble(value, 0));
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.000_1
            ? ((int)Math.Round(value)).ToString()
            : value.ToString("0.####", LocalizationService.CurrentCulture);
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
        TriggerTextBox.Text = string.Empty;
        SetPlaybackResultResource("LastResultPressTrigger");
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(CaptureTrigger_KeyDown), true);
        AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(CaptureTrigger_KeyUp), true);
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(CaptureTrigger_MouseDown), true);
    }

    private void CaptureTrigger_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
        {
            TriggerTextBox.Text = new HotkeyGesture(ReadCurrentModifiers(), HidKey.None).ToString();
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

    private void CaptureTrigger_KeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsModifierKey(key))
        {
            return;
        }

        var modifier = ModifierFromKey(key) | ReadCurrentModifiers();
        if (modifier == HidModifier.None)
        {
            return;
        }

        var gesture = new HotkeyGesture(modifier, HidKey.None);
        TriggerTextBox.Text = gesture.ToString();
        SetPlaybackResultResource("LastResultCapturedTrigger", gesture);
        StopCapture();
        e.Handled = true;
    }

    private void CaptureTrigger_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var button = e.ChangedButton switch
        {
            System.Windows.Input.MouseButton.XButton1 => CoreMouseButton.X1,
            System.Windows.Input.MouseButton.XButton2 => CoreMouseButton.X2,
            _ => CoreMouseButton.None
        };

        if (button == CoreMouseButton.None)
        {
            return;
        }

        var gesture = new HotkeyGesture(ReadCurrentModifiers(), HidKey.None, button);
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

            playbackController = new MacroPlaybackController(document, new MacroPlaybackExecutor(inputSink, macroResolver: ResolveMacroForPlayback));
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
            playbackController = new MacroPlaybackController(document, new MacroPlaybackExecutor(inputSink, macroResolver: ResolveMacroForPlayback));
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

    private MacroDocument? ResolveMacroForPlayback(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            var current = McrxParser.Parse(MacroEditor.Text);
            if (string.Equals(current.Name, name, StringComparison.CurrentCultureIgnoreCase))
            {
                return current;
            }
        }
        catch
        {
            // The active editor document may be mid-edit; saved macros still remain resolvable.
        }

        var item = libraryStore.Load().Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.CurrentCultureIgnoreCase)
            || string.Equals(candidate.Id, name, StringComparison.OrdinalIgnoreCase));
        return item is null ? null : libraryStore.ReadMacro(item.Id);
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
        RemoveHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(CaptureTrigger_KeyUp));
        RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(CaptureTrigger_MouseDown));
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
    }

    private static HidModifier ModifierFromKey(Key key)
    {
        return key switch
        {
            Key.LeftCtrl => HidModifier.LeftCtrl,
            Key.RightCtrl => HidModifier.RightCtrl,
            Key.LeftShift => HidModifier.LeftShift,
            Key.RightShift => HidModifier.RightShift,
            Key.LeftAlt => HidModifier.LeftAlt,
            Key.RightAlt => HidModifier.RightAlt,
            Key.LWin => HidModifier.LeftGui,
            Key.RWin => HidModifier.RightGui,
            _ => HidModifier.None
        };
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

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
            MacroCallStep macro => $"macro.call {macro.Macro}",
            RepeatStep repeat => $"repeat count={repeat.Count} steps={repeat.Steps.Count}",
            PixelWhenStep pixel => DescribePixelCondition(pixel),
            _ => step.GetType().Name
        };
    }

    private static string DescribePixelCondition(PixelWhenStep pixel)
    {
        var condition = pixel.Condition;
        var timeWindow = pixel.WindowStart is null && pixel.WindowEnd is null
            ? string.Empty
            : $" after {FormatOptionalMs(pixel.WindowStart, "0")}..{FormatOptionalMs(pixel.WindowEnd, "end")}ms";
        return $"IF pixel {condition.Coordinate.Scope} x={condition.Coordinate.X} y={condition.Coordinate.Y} rgb({condition.Expected.R},{condition.Expected.G},{condition.Expected.B}) +/-{condition.Tolerance}{timeWindow} then {pixel.ThenSteps.Count} step(s)";
    }

    private static string FormatOptionalMs(TimeSpan? value, string fallback)
    {
        return value is { } time ? $"{time.TotalMilliseconds:0.###}" : fallback;
    }

    private static string TrimForDisplay(string value)
    {
        return value.Length <= 32 ? value : $"{value[..29]}...";
    }
}

public sealed class MacroLibraryListEntry
{
    private static readonly Brush HeaderBrush = new SolidColorBrush(Color.FromRgb(110, 110, 115));
    private static readonly Brush MacroBrush = new SolidColorBrush(Color.FromRgb(52, 199, 89));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(29, 29, 31));

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
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(52, 199, 89));
    private static readonly Brush BlueBrush = new SolidColorBrush(Color.FromRgb(0, 122, 255));
    private static readonly Brush OrangeBrush = new SolidColorBrush(Color.FromRgb(255, 149, 0));
    private static readonly Brush PinkBrush = new SolidColorBrush(Color.FromRgb(255, 45, 85));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(255, 59, 48));
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(142, 142, 147));

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
            MacroCallStep => new StepDisplayItem(index, "M", $"#{index + 1} Macro", subtitle, badge, GreenBrush),
            RepeatStep => new StepDisplayItem(index, "R", $"#{index + 1} Loop", subtitle, badge, RedBrush),
            PixelWhenStep => new StepDisplayItem(index, "IF", $"#{index + 1} Pixel IF", subtitle, badge, PinkBrush),
            _ => new StepDisplayItem(index, "?", $"#{index + 1} Step", subtitle, badge, GrayBrush)
        };
    }

    public static StepDisplayItem Error(string message)
    {
        return new StepDisplayItem(-1, "!", "Invalid macro", message, string.Empty, RedBrush);
    }
}
