using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio.Controls;
using MacroStudio.Services;
using Microsoft.Win32;

namespace MacroStudio;

public partial class MainWindow : Window
{
    private readonly MacroLibraryStore libraryStore = new(MacroLibraryStore.GetDefaultRoot());
    private readonly MacroEditorState editorState;
    private readonly SendInputMacroSink inputSink = new();
    private readonly ActionTemplateInsertGate actionTemplateInsertGate = new();

    private GlobalKeyboardHook? keyboardHook;
    private MacroPlaybackController? playbackController;
    private readonly Dictionary<string, MacroPlaybackController> listeningControllers = [];
    private readonly Dictionary<string, string> listeningMacroNames = [];
    private readonly Dictionary<string, string> listeningProcessFilters = [];
    private readonly Dictionary<string, WorkspacePanelRegistration> workspacePanels = [];
    private WorkspaceLayout workspaceLayout = new([]);
    private bool listening;
    private bool updatingLanguageComboBox;
    private bool updatingWorkspaceMenu;
    private bool syncingJsonPanel;

    public MainWindow()
    {
        editorState = new MacroEditorState(libraryStore);

        InitializeComponent();
        LocalizationService.Initialize();

        InitializeLanguageComboBox();
        InitializePanels();
        InitializeWorkspacePanels();
        ApplyLocalization();
        InitializeMacroLibrary();
    }

    private void InitializePanels()
    {
        LibraryPanel.Initialize(editorState);
        SequencePanelControl.Initialize(editorState);
        ConditionPanel.Initialize(editorState);

        LibraryPanel.MacroSelected += OnMacroSelected;
        LibraryPanel.MacroCreated += OnMacroCreated;
        LibraryPanel.MacroDuplicated += OnMacroDuplicated;
        LibraryPanel.MacroDeleted += OnMacroDeleted;
        LibraryPanel.ImportApplied += OnImportApplied;
        LibraryPanel.DocumentRequested += () => GetDocumentWithPlayback();
        LibraryPanel.ResultMessage += msg => SetStatus(msg);

        SequencePanelControl.SaveLibraryRequested += OnSaveLibrary;
        SequencePanelControl.RunNowRequested += OnRunNow;
        SequencePanelControl.StopRequested += OnStopPlayback;
        SequencePanelControl.StepSelectionChanged += OnStepSelectionChanged;
        SequencePanelControl.ActionTemplateDropped += OnActionTemplateDropped;
        SequencePanelControl.MacroLibraryDropped += OnMacroLibraryDropped;
        SequencePanelControl.UndoApplied += OnSequenceUndoApplied;
        SequencePanelControl.DocumentEdited += OnSequenceDocumentEdited;
        SequencePanelControl.SequenceActivated += ConditionPanel.DeactivateThenActionSequence;
        SequencePanelControl.EditorTextChanged += OnSequenceEditorTextChanged;

        JsonPanel.EditorTextChanged += OnJsonPanelEditorTextChanged;
        JsonPanel.ApplyJsonRequested += OnApplyJsonRequested;

        ActionPalette.ActionClicked += OnActionPaletteClicked;

        PlaybackPanelControl.StartListeningRequested += OnStartListening;
        PlaybackPanelControl.StopListeningRequested += OnStopListening;
        PlaybackPanelControl.RunNowRequested += OnRunNow;
        PlaybackPanelControl.StopPlaybackRequested += OnStopPlayback;
        PlaybackPanelControl.PlaybackSettingsEdited += OnPlaybackSettingsEdited;

        ConditionPanel.ConditionSelectionChanged += OnConditionSelectionChanged;
        ConditionPanel.ConditionsModified += OnConditionsModified;
        ConditionPanel.PickRegionRequested += OnPickRegionRequested;
    }

    private void InitializeWorkspacePanels()
    {
        RegisterWorkspacePanel("library", WorkspaceTitle("WorkspacePanelLibrary"), LibraryPanelHost);
        RegisterWorkspacePanel("sequence", WorkspaceTitle("WorkspacePanelSequence"), SequencePanelHost);
        RegisterWorkspacePanel("conditions", WorkspaceTitle("WorkspacePanelConditions"), ConditionPanelHost);
        RegisterWorkspacePanel("json", WorkspaceTitle("AdvancedJson"), JsonPanelHost);
        RegisterWorkspacePanel("actions", WorkspaceTitle("WorkspacePanelActions"), ActionPaletteHost);
        RegisterWorkspacePanel("playback", WorkspaceTitle("WorkspacePanelPlayback"), PlaybackPanelHost);

        workspaceLayout = WorkspaceLayoutStore.Load();
        ApplyWorkspaceLayout();
        UpdateWorkspaceMenuChecks();
    }

    private void RegisterWorkspacePanel(string id, string title, ContentControl host)
    {
        if (host.Content is not FrameworkElement element)
        {
            throw new InvalidOperationException($"Workspace panel '{id}' has no content.");
        }

        workspacePanels[id] = new WorkspacePanelRegistration(id, title, host, element)
        {
            IsVisible = id != "json"
        };
    }

    private void ApplyWorkspaceLayout()
    {
        foreach (var panel in workspacePanels.Values)
        {
            var layout = workspaceLayout.Panels.TryGetValue(panel.Id, out var saved)
                ? saved
                : GetDefaultWorkspacePanelLayout(panel.Id);

            panel.IsVisible = layout.IsVisible;
            panel.SavedLayout = layout;
            if (layout.IsFloating)
            {
                FloatWorkspacePanel(panel.Id, saveLayout: false);
            }
            else
            {
                DockWorkspacePanel(panel.Id, saveLayout: false);
            }

            ToggleWorkspacePanelVisibility(panel.Id, layout.IsVisible, saveLayout: false);
        }
    }

    private static WorkspacePanelLayout GetDefaultWorkspacePanelLayout(string id)
    {
        return id == "json"
            ? new WorkspacePanelLayout(IsVisible: false, Width: 720, Height: 520)
            : new WorkspacePanelLayout(IsVisible: true);
    }

    private void WindowMenu_Click(object sender, RoutedEventArgs e)
    {
        UpdateWorkspaceMenuChecks();
        WindowMenuContext.PlacementTarget = WindowMenuButton;
        WindowMenuContext.IsOpen = true;
    }

    private void WindowPanelVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (updatingWorkspaceMenu) return;
        if (sender is not MenuItem { Tag: string id } item) return;
        ToggleWorkspacePanelVisibility(id, item.IsChecked);
        e.Handled = true;
    }

    private void WorkspaceToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (updatingWorkspaceMenu) return;
        if (sender is not ToggleButton { Tag: string id } item) return;
        ToggleWorkspacePanelVisibility(id, item.IsChecked == true);
        e.Handled = true;
    }

    private void FloatPanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string id }) return;
        FloatWorkspacePanel(id);
        e.Handled = true;
    }

    private void DockPanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string id }) return;
        DockWorkspacePanel(id);
        e.Handled = true;
    }

    private void ResetWorkspaceLayout_Click(object sender, RoutedEventArgs e)
    {
        ResetWorkspaceLayout();
        e.Handled = true;
    }

    private void ToggleWorkspacePanelVisibility(string id, bool visible, bool saveLayout = true)
    {
        if (!workspacePanels.TryGetValue(id, out var panel)) return;

        panel.IsVisible = visible;
        if (panel.IsFloating)
        {
            EnsureWorkspacePanelWindow(panel);
            if (visible)
            {
                panel.Window!.Show();
                panel.Window.Activate();
            }
            else
            {
                panel.Window!.Hide();
            }
        }
        else
        {
            panel.DockHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateWorkspaceMenuChecks();
        if (saveLayout)
        {
            SaveWorkspaceLayout();
        }
    }

    private void FloatWorkspacePanel(string id, bool saveLayout = true)
    {
        if (!workspacePanels.TryGetValue(id, out var panel)) return;
        EnsureWorkspacePanelWindow(panel);

        if (!ReferenceEquals(panel.Window!.ContentHost.Content, panel.Element))
        {
            panel.DockHost.Content = null;
            panel.Window.ContentHost.Content = panel.Element;
        }

        panel.IsFloating = true;
        panel.IsVisible = true;
        panel.DockHost.Visibility = Visibility.Collapsed;
        RestoreFloatingBounds(panel);
        panel.Window.Show();
        panel.Window.Activate();
        UpdateWorkspaceMenuChecks();
        if (saveLayout)
        {
            SaveWorkspaceLayout();
        }
    }

    private void DockWorkspacePanel(string id, bool saveLayout = true)
    {
        if (!workspacePanels.TryGetValue(id, out var panel)) return;

        if (panel.Window is not null && ReferenceEquals(panel.Window.ContentHost.Content, panel.Element))
        {
            panel.Window.ContentHost.Content = null;
            panel.Window.Hide();
        }

        if (!ReferenceEquals(panel.DockHost.Content, panel.Element))
        {
            panel.DockHost.Content = panel.Element;
        }

        panel.IsFloating = false;
        panel.DockHost.Visibility = panel.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateWorkspaceMenuChecks();
        if (saveLayout)
        {
            SaveWorkspaceLayout();
        }
    }

    private void ResetWorkspaceLayout()
    {
        WorkspaceLayoutStore.Reset();
        workspaceLayout = new([]);
        foreach (var panel in workspacePanels.Values)
        {
            panel.SavedLayout = GetDefaultWorkspacePanelLayout(panel.Id);
            panel.IsVisible = panel.SavedLayout.IsVisible;
            DockWorkspacePanel(panel.Id, saveLayout: false);
            ToggleWorkspacePanelVisibility(panel.Id, panel.IsVisible, saveLayout: false);
        }

        SaveWorkspaceLayout();
        SetStatus(L("WorkspaceLayoutReset"));
    }

    private void EnsureWorkspacePanelWindow(WorkspacePanelRegistration panel)
    {
        if (panel.Window is not null)
        {
            panel.Window.SetTitle(panel.Title, L("WorkspaceDock"));
            return;
        }

        panel.Window = new WorkspacePanelWindow(panel.Id, panel.Title)
        {
            Owner = this
        };
        panel.Window.SetTitle(panel.Title, L("WorkspaceDock"));
        panel.Window.DockRequested += (_, _) => DockWorkspacePanel(panel.Id);
        panel.Window.HideRequested += (_, _) =>
        {
            panel.IsVisible = false;
            UpdateWorkspaceMenuChecks();
            SaveWorkspaceLayout();
        };
    }

    private void RestoreFloatingBounds(WorkspacePanelRegistration panel)
    {
        if (panel.Window is null) return;
        var layout = panel.SavedLayout ?? GetDefaultWorkspacePanelLayout(panel.Id);
        panel.Window.Width = Math.Max(panel.Window.MinWidth, layout.Width);
        panel.Window.Height = Math.Max(panel.Window.MinHeight, layout.Height);
        panel.Window.Left = layout.Left;
        panel.Window.Top = layout.Top;
    }

    private void SaveWorkspaceLayout()
    {
        var panels = new Dictionary<string, WorkspacePanelLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var panel in workspacePanels.Values)
        {
            var current = panel.SavedLayout ?? GetDefaultWorkspacePanelLayout(panel.Id);
            var left = current.Left;
            var top = current.Top;
            var width = current.Width;
            var height = current.Height;

            if (panel.Window is not null)
            {
                left = panel.Window.Left;
                top = panel.Window.Top;
                width = panel.Window.Width;
                height = panel.Window.Height;
            }

            panel.SavedLayout = new WorkspacePanelLayout(panel.IsVisible, panel.IsFloating, left, top, width, height);
            panels[panel.Id] = panel.SavedLayout;
        }

        workspaceLayout = new WorkspaceLayout(panels);
        WorkspaceLayoutStore.Save(workspaceLayout);
    }

    private void UpdateWorkspaceMenuChecks()
    {
        updatingWorkspaceMenu = true;
        SetShowMenuCheck(ShowLibraryMenuItem, "library");
        SetShowMenuCheck(ShowSequenceMenuItem, "sequence");
        SetShowMenuCheck(ShowConditionMenuItem, "conditions");
        SetShowMenuCheck(ShowJsonMenuItem, "json");
        SetShowMenuCheck(ShowActionMenuItem, "actions");
        SetShowMenuCheck(ShowPlaybackMenuItem, "playback");
        UpdateWorkspaceToolButtonStates();
        updatingWorkspaceMenu = false;
    }

    private void SetShowMenuCheck(MenuItem item, string id)
    {
        item.IsChecked = workspacePanels.TryGetValue(id, out var panel) && panel.IsVisible;
    }

    private void UpdateWorkspaceToolButtonStates()
    {
        SetToolButtonCheck(LibraryToolButton, "library");
        SetToolButtonCheck(SequenceToolButton, "sequence");
        SetToolButtonCheck(ConditionToolButton, "conditions");
        SetToolButtonCheck(JsonToolButton, "json");
        SetToolButtonCheck(ActionToolButton, "actions");
        SetToolButtonCheck(PlaybackToolButton, "playback");
    }

    private void SetToolButtonCheck(ToggleButton button, string id)
    {
        button.IsChecked = workspacePanels.TryGetValue(id, out var panel) && panel.IsVisible;
    }

    private static void SetWorkspaceToolButtonText(ToggleButton button, string title)
    {
        button.ToolTip = title;
    }

    private void SetWorkspacePanelTitle(string id, string title)
    {
        if (!workspacePanels.TryGetValue(id, out var panel)) return;
        panel.Title = title;
        if (panel.Window is not null)
        {
            panel.Window.SetTitle(title, L("WorkspaceDock"));
        }
    }

    private void OnSequenceEditorTextChanged(string text)
    {
        if (syncingJsonPanel) return;
        syncingJsonPanel = true;
        JsonPanel.EditorText = text;
        syncingJsonPanel = false;
    }

    private void OnJsonPanelEditorTextChanged(string text)
    {
        if (syncingJsonPanel) return;
        TryApplyJsonTextToEditor(text, showStatus: false);
    }

    private void OnApplyJsonRequested()
    {
        TryApplyJsonTextToEditor(JsonPanel.EditorText, showStatus: true);
    }

    private bool TryApplyJsonTextToEditor(string text, bool showStatus)
    {
        try
        {
            McrxParser.Parse(text);
            syncingJsonPanel = true;
            SequencePanelControl.EditorText = text;
            syncingJsonPanel = false;
            SequencePanelControl.ValidateCurrentMacro();
            RefreshEditorAfterSequenceDocumentChange(showStatus ? L("JsonApplied") : null);
            return true;
        }
        catch (Exception ex)
        {
            syncingJsonPanel = false;
            if (showStatus)
            {
                SetStatus(ex.Message);
            }

            return false;
        }
    }

    private void InitializeMacroLibrary()
    {
        editorState.ReloadLibrary();
        if (editorState.LibrarySnapshot.Items.Count == 0)
        {
            libraryStore.CreateMacro(McrxParser.Parse(SampleMacro));
            editorState.ReloadLibrary();
        }

        editorState.SelectedMacroId = editorState.LibrarySnapshot.SelectedMacroId
            ?? editorState.LibrarySnapshot.Items.FirstOrDefault()?.Id;

        LibraryPanel.RefreshList();

        if (editorState.SelectedMacroId is not null)
        {
            LoadMacroFromLibrary(editorState.SelectedMacroId);
        }
        else
        {
            SequencePanelControl.SetEditorDocument(new MacroDocument(1, "Macro 1", PlaybackSettings.Default, []));
            SequencePanelControl.ClearUndoHistory();
            RefreshConditionStepChoices();
        }
    }

    private void LoadMacroFromLibrary(string id)
    {
        try
        {
            editorState.SelectedMacroId = id;
            var document = libraryStore.ReadMacro(id);
            SequencePanelControl.SetEditorDocument(document);
            SequencePanelControl.ClearUndoHistory();
            PlaybackPanelControl.SetPlaybackControls(document.Playback);
            RefreshConditionStepChoices();
            ConditionPanel.LoadConditions(document.EffectiveConditions);
            SequencePanelControl.SetConditionHighlights(document.EffectiveConditions, i => ConditionPanel.GetConditionColor(i));
            CheckAndWarnConflicts(document);
            SetStatus(document.Name);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void OnMacroSelected(string id) => LoadMacroFromLibrary(id);

    private void OnMacroCreated(MacroLibraryItem item)
    {
        editorState.SelectedMacroId = item.Id;
        LoadMacroFromLibrary(item.Id);
    }

    private void OnMacroDuplicated(string id)
    {
        try
        {
            var document = SequencePanelControl.GetCurrentDocument();
            libraryStore.SaveMacro(id, document);
            var item = libraryStore.DuplicateMacro(id, $"{document.Name} Copy");
            editorState.SelectedMacroId = item.Id;
            LibraryPanel.RefreshList();
            LoadMacroFromLibrary(item.Id);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void OnMacroDeleted(string id)
    {
        var name = SequencePanelControl.MacroName;
        var result = MessageBox.Show(
            this,
            LocalizationService.Format("DeleteMacroConfirm", string.IsNullOrWhiteSpace(name) ? L("Macro") : name),
            L("Delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        libraryStore.DeleteMacro(id);
        editorState.ReloadLibrary();
        editorState.SelectedMacroId = editorState.LibrarySnapshot.SelectedMacroId;
        LibraryPanel.RefreshList();

        if (editorState.SelectedMacroId is not null)
            LoadMacroFromLibrary(editorState.SelectedMacroId);
        else
        {
            SequencePanelControl.SetEditorDocument(new MacroDocument(1, "Macro 1", PlaybackSettings.Default, []));
            SequencePanelControl.ClearUndoHistory();
            RefreshConditionStepChoices();
        }
    }

    private void OnSaveLibrary()
    {
        try
        {
            AutoSaveCurrentMacro(updateStatus: false);
            SetStatus(L("LibrarySaved"));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void OnPlaybackSettingsEdited()
    {
        try
        {
            AutoSaveCurrentMacro(updateStatus: false);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void AutoSaveCurrentMacro(bool updateStatus)
    {
        var document = GetDocumentWithPlayback();
        MacroLibraryItem item;
        if (editorState.SelectedMacroId is null)
            item = libraryStore.CreateMacro(document);
        else
            item = libraryStore.SaveMacro(editorState.SelectedMacroId, document);

        editorState.SelectedMacroId = item.Id;
        LibraryPanel.RefreshList();
        if (updateStatus)
        {
            SetStatus(L("LibrarySaved"));
        }
    }

    private void OnStepSelectionChanged(int stepIndex)
    {
        editorState.SelectedStepIndex = stepIndex;
    }

    private void OnActionPaletteClicked(MacroActionTemplateKind kind)
    {
        if (!actionTemplateInsertGate.TryAccept(kind)) return;
        if (ConditionPanel.TryInsertActionTemplateIntoActiveCondition(kind))
        {
            SetStatus(LocalizationService.Format("ActionInserted", L($"Template{kind}")));
            return;
        }

        SequencePanelControl.InsertSteps(MacroActionTemplateFactory.CreateSteps(kind));
        RefreshConditionStepChoices();
        SetStatus(LocalizationService.Format("ActionInserted", L($"Template{kind}")));
    }

    private void OnActionTemplateDropped(MacroActionTemplateKind kind, string parentPathText, int insertIndex)
    {
        if (!actionTemplateInsertGate.TryAccept(kind)) return;
        SequencePanelControl.InsertStepsAtPath(MacroActionTemplateFactory.CreateSteps(kind), parentPathText, insertIndex);
        RefreshConditionStepChoices();
    }

    private void OnMacroLibraryDropped(string macroId, string parentPathText, int insertIndex)
    {
        try
        {
            var document = libraryStore.ReadMacro(macroId);
            SequencePanelControl.InsertStepsAtPath(document.Steps, parentPathText, insertIndex);
            RefreshConditionStepChoices();
            SetStatus(LocalizationService.Format("InsertedMacroSteps", document.Name, document.Steps.Count));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async void OnStartListening()
    {
        try
        {
            AutoSaveCurrentMacro(updateStatus: false);

            var bindings = new List<HotkeyBinding>();
            var controllers = new Dictionary<string, MacroPlaybackController>(StringComparer.OrdinalIgnoreCase);
            var macroNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var processFilters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in libraryStore.Load().Items)
            {
                MacroDocument document;
                try
                {
                    document = libraryStore.ReadMacro(item.Id);
                }
                catch
                {
                    continue;
                }

                if (document.Playback.Trigger is not { } trigger)
                {
                    continue;
                }

                bindings.Add(new HotkeyBinding(item.Id, trigger));
                controllers[item.Id] = new MacroPlaybackController(document, new MacroPlaybackExecutor(inputSink, macroResolver: ResolveMacroForPlayback));
                macroNames[item.Id] = document.Name;
                processFilters[item.Id] = document.Playback.ProcessFilter;
            }

            if (bindings.Count == 0)
                throw new InvalidOperationException(L("ChooseTriggerBeforeListening"));

            keyboardHook?.Dispose();
            StopListeningControllers();

            keyboardHook = new GlobalKeyboardHook();
            keyboardHook.TriggerPressed += KeyboardHook_TriggerPressed;
            keyboardHook.TriggerReleased += KeyboardHook_TriggerReleased;
            keyboardHook.Start(bindings);

            foreach (var (id, controller) in controllers)
            {
                listeningControllers[id] = controller;
            }

            foreach (var (id, name) in macroNames)
            {
                listeningMacroNames[id] = name;
            }

            foreach (var (id, processFilter) in processFilters)
            {
                listeningProcessFilters[id] = processFilter;
            }

            playbackController = null;
            listening = true;
            PlaybackPanelControl.SetPlaybackStatus($"{L("PlaybackStatusListening")} ({bindings.Count})");
            SetStatus($"{L("Listening")} ({bindings.Count})");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusError"));
            PlaybackPanelControl.SetPlaybackResult(ex.Message);
            SetStatus(L("PlaybackError"));
        }
    }

    private void OnStopListening()
    {
        listening = false;
        keyboardHook?.Dispose();
        keyboardHook = null;
        StopListeningControllers();
        playbackController?.Stop();
        PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusIdle"));
        PlaybackPanelControl.SetPlaybackResult(L("HotkeyListenerStopped"));
        SetStatus(L("Idle"));
    }

    private void StopListeningControllers()
    {
        foreach (var controller in listeningControllers.Values)
        {
            controller.Stop();
        }

        listeningControllers.Clear();
        listeningMacroNames.Clear();
        listeningProcessFilters.Clear();
    }

    private async void OnRunNow()
    {
        try
        {
            var document = GetDocumentWithPlayback();
            playbackController = new MacroPlaybackController(document, new MacroPlaybackExecutor(inputSink, macroResolver: ResolveMacroForPlayback));
            await playbackController.RunNowAsync();
            UpdatePlaybackStatus();
            _ = WatchPlaybackAsync(playbackController);
        }
        catch (Exception ex)
        {
            PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusError"));
            PlaybackPanelControl.SetPlaybackResult(ex.Message);
        }
    }

    private void OnStopPlayback()
    {
        playbackController?.Stop();
        foreach (var controller in listeningControllers.Values)
        {
            controller.Stop();
        }

        UpdatePlaybackStatus();
        if (playbackController is not null)
            _ = WatchPlaybackAsync(playbackController);
    }

    private void OnImportApplied(MacroDocument document)
    {
        var item = libraryStore.CreateMacro(document);
        editorState.SelectedMacroId = item.Id;
        LibraryPanel.RefreshList();
        SequencePanelControl.SetEditorDocument(document);
        SequencePanelControl.ClearUndoHistory();
        PlaybackPanelControl.SetPlaybackControls(document.Playback);
        RefreshConditionStepChoices();
        SetStatus(L("ConversionApplied"));
    }

    private void OnSequenceUndoApplied()
    {
        RefreshEditorAfterSequenceDocumentChange(L("Undo"));
    }

    private void OnSequenceDocumentEdited()
    {
        RefreshEditorAfterSequenceDocumentChange(null);
    }

    private void RefreshEditorAfterSequenceDocumentChange(string? status)
    {
        try
        {
            var document = SequencePanelControl.GetCurrentDocument();
            RefreshConditionStepChoices();
            ConditionPanel.LoadConditions(document.EffectiveConditions);
            SequencePanelControl.SetConditionHighlights(document.EffectiveConditions, i => ConditionPanel.GetConditionColor(i));
            OnStepSelectionChanged(SequencePanelControl.SelectedStepIndex);
            CheckAndWarnConflicts(document);
            if (!string.IsNullOrWhiteSpace(status))
            {
                SetStatus(status);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private MacroDocument GetDocumentWithPlayback()
    {
        ApplyPlaybackSettingsToEditor();
        return SequencePanelControl.GetCurrentDocument();
    }

    private void ApplyPlaybackSettingsToEditor()
    {
        var triggerText = PlaybackPanelControl.TriggerText;
        var trigger = string.IsNullOrWhiteSpace(triggerText) ? null : McrxParser.ParseHotkeyGesture(triggerText);
        var mode = PlaybackPanelControl.GetSelectedPlaybackMode();
        if (!int.TryParse(PlaybackPanelControl.CountText, out var count) || count < 1) count = 1;
        var processFilter = PlaybackPanelControl.ProcessFilterText;

        var root = JsonNode.Parse(SequencePanelControl.EditorText)?.AsObject()
            ?? throw new JsonException("Macro JSON root must be an object.");

        var macroName = SequencePanelControl.MacroName;
        if (!string.IsNullOrWhiteSpace(macroName))
            root["name"] = macroName;

        var playback = new JsonObject
        {
            ["mode"] = ToPlaybackModeText(mode),
            ["count"] = count
        };
        if (trigger is not null) playback["trigger"] = trigger.ToString();
        if (!string.IsNullOrWhiteSpace(processFilter)) playback["processFilter"] = processFilter;
        root["playback"] = playback;

        SequencePanelControl.EditorText = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        });
        SequencePanelControl.ValidateCurrentMacro();
    }

    private void RefreshConditionStepChoices()
    {
        try
        {
            ConditionPanel.SetStepChoices(SequencePanelControl.GetStepChoices());
        }
        catch
        {
            ConditionPanel.SetStepChoices([]);
        }
    }

    private MacroDocument? ResolveMacroForPlayback(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var current = SequencePanelControl.GetCurrentDocument();
            if (string.Equals(current.Name, name, StringComparison.CurrentCultureIgnoreCase))
                return current;
        }
        catch { }

        var item = libraryStore.Load().Items.FirstOrDefault(candidate => candidate.MatchesReference(name));
        return item is null ? null : libraryStore.ReadMacro(item.Id);
    }

    private void KeyboardHook_TriggerPressed(object? sender, HotkeyTriggeredEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (!listeningControllers.TryGetValue(e.Id, out var controller)) return;
            if (listeningProcessFilters.TryGetValue(e.Id, out var processFilter)
                && !PlaybackProcessFilter.Matches(processFilter, ForegroundProcessService.GetForegroundProcessName()))
            {
                return;
            }

            playbackController = controller;
            await controller.TriggerPressedAsync();
            UpdatePlaybackStatus();
            if (listeningMacroNames.TryGetValue(e.Id, out var macroName))
            {
                SetStatus(macroName);
            }

            _ = WatchPlaybackAsync(controller);
        });
    }

    private void KeyboardHook_TriggerReleased(object? sender, HotkeyTriggeredEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!listeningControllers.TryGetValue(e.Id, out var controller)) return;
            controller.TriggerReleased();
            playbackController = controller;
            UpdatePlaybackStatus();
            _ = WatchPlaybackAsync(controller);
        });
    }

    private async Task WatchPlaybackAsync(MacroPlaybackController controller)
    {
        try
        {
            var result = await controller.WhenIdleAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                PlaybackPanelControl.SetPlaybackStatus(listening ? L("PlaybackStatusListening") : L("PlaybackStatusIdle"));
                PlaybackPanelControl.SetPlaybackResult(LocalizationService.Format("LastResultRunSummary", result.IterationsCompleted, result.ActionsSubmitted, result.Cancelled));
                SetStatus(listening ? L("Listening") : L("Idle"));
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusError"));
                PlaybackPanelControl.SetPlaybackResult(ex.Message);
                SetStatus(L("PlaybackError"));
            });
        }
    }

    private void UpdatePlaybackStatus()
    {
        if (playbackController is null)
        {
            PlaybackPanelControl.SetPlaybackStatus(listening ? L("PlaybackStatusListening") : L("PlaybackStatusIdle"));
            return;
        }

        PlaybackPanelControl.SetPlaybackStatus(L(PlaybackStatusResourceKey(playbackController.Status)));
        SetStatus(L(StatusResourceKey(playbackController.Status)));
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // --- Window chrome ---

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        SequencePanelControl.CloseInlineStepEditorOnExternalPointerDown(e.OriginalSource as DependencyObject);
        ConditionPanel.CloseInlineStepEditorOnExternalPointerDown(e.OriginalSource as DependencyObject);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if ((modifiers & ModifierKeys.Control) != 0
            && key == Key.Z
            && !IsTextEditingSource(e.OriginalSource as DependencyObject))
        {
            SequencePanelControl.UndoLastChange();
            e.Handled = true;
            return;
        }

        if (!IsTextEditingSource(e.OriginalSource as DependencyObject)
            && ConditionPanel.HandleExplorerShortcut(key, modifiers))
        {
            e.Handled = true;
            return;
        }

        if (!IsTextEditingSource(e.OriginalSource as DependencyObject)
            && SequencePanelControl.HandleExplorerShortcut(key, modifiers))
        {
            e.Handled = true;
            return;
        }

        if ((modifiers & ModifierKeys.Control) != 0
            && key == Key.Delete
            && !IsTextEditingSource(e.OriginalSource as DependencyObject))
        {
            SequencePanelControl.ClearAllSteps();
            e.Handled = true;
        }
    }

    private void TopChromeBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsChromeInteractiveSource(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.LeftButton == MouseButtonState.Pressed)
            try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Toggle();
        ThemeToggleButton.Content = ThemeService.CurrentTheme == AppTheme.Dark ? "☾" : "☀";
    }

    // --- File operations ---

    private void OpenMacro_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = L("MacroFileFilter"), Title = L("OpenMacroTitle") };
        if (dialog.ShowDialog(this) == true)
        {
            SequencePanelControl.EditorText = File.ReadAllText(dialog.FileName);
            SequencePanelControl.ValidateCurrentMacro();
            SequencePanelControl.ClearUndoHistory();
            SetStatus(Path.GetFileName(dialog.FileName));
        }
    }

    private void SaveMacro_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = L("MacroFileFilter"), Title = L("SaveMacroTitle"), DefaultExt = ".mcrx" };
        if (dialog.ShowDialog(this) == true)
        {
            ApplyPlaybackSettingsToEditor();
            File.WriteAllText(dialog.FileName, SequencePanelControl.EditorText);
            SetStatus(Path.GetFileName(dialog.FileName));
        }
    }

    // --- Language ---

    private void InitializeLanguageComboBox()
    {
        updatingLanguageComboBox = true;
        LanguageComboBox.Items.Clear();
        foreach (var language in LocalizationService.SupportedLanguages)
        {
            var item = new ComboBoxItem { Tag = language.CultureName, Content = LocalizationService.Get(language.DisplayNameResourceKey) };
            LanguageComboBox.Items.Add(item);
            if (string.Equals(language.CultureName, LocalizationService.CurrentCulture.Name, StringComparison.OrdinalIgnoreCase))
                LanguageComboBox.SelectedItem = item;
        }
        updatingLanguageComboBox = false;
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingLanguageComboBox) return;
        if ((LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag is not string cultureName) return;
        LocalizationService.SetLanguage(cultureName);
        ApplyLocalization();
        LibraryPanel.RefreshList();
    }

    private void ApplyLocalization()
    {
        Title = L("AppTitle");
        LanguageLabelText.Text = L("Language");
        OpenButton.Content = L("Open");
        SaveButton.Content = L("Save");
        WindowMenuButton.Content = L("WorkspaceWindowMenu");
        ShowPanelsMenuItem.Header = L("WorkspaceShow");
        FloatPanelsMenuItem.Header = L("WorkspaceFloat");
        DockPanelsMenuItem.Header = L("WorkspaceDock");
        ResetWorkspaceLayoutMenuItem.Header = L("WorkspaceResetLayout");

        SetWorkspacePanelTitle("library", WorkspaceTitle("WorkspacePanelLibrary"));
        SetWorkspacePanelTitle("sequence", WorkspaceTitle("WorkspacePanelSequence"));
        SetWorkspacePanelTitle("conditions", WorkspaceTitle("WorkspacePanelConditions"));
        SetWorkspacePanelTitle("json", WorkspaceTitle("AdvancedJson"));
        SetWorkspacePanelTitle("actions", WorkspaceTitle("WorkspacePanelActions"));
        SetWorkspacePanelTitle("playback", WorkspaceTitle("WorkspacePanelPlayback"));
        ApplyWorkspaceMenuItemText();

        updatingLanguageComboBox = true;
        foreach (var item in LanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            var cultureName = item.Tag?.ToString();
            var language = LocalizationService.SupportedLanguages
                .FirstOrDefault(c => string.Equals(c.CultureName, cultureName, StringComparison.OrdinalIgnoreCase));
            if (language is not null) item.Content = LocalizationService.Get(language.DisplayNameResourceKey);
        }
        updatingLanguageComboBox = false;

        LibraryPanel.ApplyLocalization();
        SequencePanelControl.ApplyLocalization();
        JsonPanel.ApplyLocalization();
        ActionPalette.ApplyLocalization();
        PlaybackPanelControl.ApplyLocalization();
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveWorkspaceLayout();
        keyboardHook?.Dispose();
        StopListeningControllers();
        playbackController?.Stop();
        foreach (var panel in workspacePanels.Values)
        {
            panel.Window?.ForceClose();
        }

        base.OnClosed(e);
    }

    private void ApplyWorkspaceMenuItemText()
    {
        ShowLibraryMenuItem.Header = WorkspaceTitle("WorkspacePanelLibrary");
        ShowSequenceMenuItem.Header = WorkspaceTitle("WorkspacePanelSequence");
        ShowConditionMenuItem.Header = WorkspaceTitle("WorkspacePanelConditions");
        ShowJsonMenuItem.Header = WorkspaceTitle("AdvancedJson");
        ShowActionMenuItem.Header = WorkspaceTitle("WorkspacePanelActions");
        ShowPlaybackMenuItem.Header = WorkspaceTitle("WorkspacePanelPlayback");
        SetSubMenuHeaders(FloatPanelsMenuItem);
        SetSubMenuHeaders(DockPanelsMenuItem);

        ConditionSection.Header = WorkspaceTitle("WorkspacePanelConditions");
        ActionPaletteSection.Header = WorkspaceTitle("WorkspacePanelActions");
        PlaybackSection.Header = WorkspaceTitle("WorkspacePanelPlayback");

        SetWorkspaceToolButtonText(LibraryToolButton, WorkspaceTitle("WorkspacePanelLibrary"));
        SetWorkspaceToolButtonText(SequenceToolButton, WorkspaceTitle("WorkspacePanelSequence"));
        SetWorkspaceToolButtonText(ConditionToolButton, WorkspaceTitle("WorkspacePanelConditions"));
        SetWorkspaceToolButtonText(JsonToolButton, WorkspaceTitle("AdvancedJson"));
        SetWorkspaceToolButtonText(ActionToolButton, WorkspaceTitle("WorkspacePanelActions"));
        SetWorkspaceToolButtonText(PlaybackToolButton, WorkspaceTitle("WorkspacePanelPlayback"));
    }

    private void SetSubMenuHeaders(MenuItem parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            item.Header = item.Tag?.ToString() switch
            {
                "library" => WorkspaceTitle("WorkspacePanelLibrary"),
                "sequence" => WorkspaceTitle("WorkspacePanelSequence"),
                "conditions" => WorkspaceTitle("WorkspacePanelConditions"),
                "json" => WorkspaceTitle("AdvancedJson"),
                "actions" => WorkspaceTitle("WorkspacePanelActions"),
                "playback" => WorkspaceTitle("WorkspacePanelPlayback"),
                _ => item.Header
            };
        }
    }

    // --- Condition directive integration ---

    private void OnConditionSelectionChanged(object? sender, ConditionSelectionChangedEventArgs e)
    {
        if (e.Directive != null)
        {
            SequencePanelControl.HighlightSingleCondition(e.Index, e.Directive, ConditionPanel.GetConditionColor(e.Index));
        }
        else
        {
            try
            {
                var doc = SequencePanelControl.GetCurrentDocument();
                SequencePanelControl.SetConditionHighlights(doc.EffectiveConditions, i => ConditionPanel.GetConditionColor(i));
            }
            catch { SequencePanelControl.SetConditionHighlights(null); }
        }
    }

    private void OnConditionsModified(object? sender, EventArgs e)
    {
        try
        {
            var doc = SequencePanelControl.GetCurrentDocument();
            var updated = doc with { Conditions = ConditionPanel.Conditions.ToList() };
            SequencePanelControl.CaptureUndoSnapshot();
            SequencePanelControl.SetEditorDocument(updated);
            RefreshConditionStepChoices();
            SequencePanelControl.SetConditionHighlights(updated.EffectiveConditions, i => ConditionPanel.GetConditionColor(i));
            CheckAndWarnConflicts(updated);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void OnPickRegionRequested(object? sender, EventArgs e)
    {
        var region = ScreenRegionPicker.PickRegion(this);
        if (region != null)
        {
            ConditionPanel.SetRegion(region);
            SetStatus($"区域已选取: ({region.TopLeft.X},{region.TopLeft.Y}) ~ ({region.BottomRight.X},{region.BottomRight.Y})");
        }
    }

    private void CheckAndWarnConflicts(MacroDocument document)
    {
        var warnings = ConflictDetector.DetectConflicts(document);
        if (warnings.Count > 0)
        {
            SetStatus($"⚠ {warnings.Count} 个按键冲突");
        }
    }

    // --- Helpers ---

    private static bool IsChromeInteractiveSource(DependencyObject? source)
    {
        return FindVisualParent<ButtonBase>(source) is not null
            || FindVisualParent<ComboBox>(source) is not null
            || FindVisualParent<TextBoxBase>(source) is not null;
    }

    private static bool IsTextEditingSource(DependencyObject? source)
    {
        return FindVisualParent<TextBoxBase>(source) is not null
            || FindVisualParent<PasswordBox>(source) is not null;
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static string PlaybackStatusResourceKey(PlaybackStatus status) => status switch
    {
        PlaybackStatus.Idle => "PlaybackStatusIdle",
        PlaybackStatus.Listening => "PlaybackStatusListening",
        PlaybackStatus.Running => "PlaybackStatusRunning",
        PlaybackStatus.Stopping => "PlaybackStatusStopping",
        PlaybackStatus.InputUnavailable => "PlaybackStatusInputUnavailable",
        PlaybackStatus.Error => "PlaybackStatusError",
        _ => "PlaybackStatusFormat"
    };

    private static string StatusResourceKey(PlaybackStatus status) => status switch
    {
        PlaybackStatus.Idle => "Idle",
        PlaybackStatus.Listening => "Listening",
        PlaybackStatus.Error => "PlaybackError",
        _ => PlaybackStatusResourceKey(status)
    };

    private static string ToPlaybackModeText(PlaybackMode mode) => mode switch
    {
        PlaybackMode.ToggleLoop => "toggleLoop",
        PlaybackMode.HoldLoop => "holdLoop",
        _ => "fixedCount"
    };

    private static string WorkspaceTitle(string key) => LocalizationService.Get(key);
    private static string L(string key) => LocalizationService.Get(key);

    private sealed class WorkspacePanelRegistration
    {
        public WorkspacePanelRegistration(string id, string title, ContentControl dockHost, FrameworkElement element)
        {
            Id = id;
            Title = title;
            DockHost = dockHost;
            Element = element;
        }

        public string Id { get; }
        public string Title { get; set; }
        public ContentControl DockHost { get; }
        public FrameworkElement Element { get; }
        public WorkspacePanelWindow? Window { get; set; }
        public bool IsVisible { get; set; }
        public bool IsFloating { get; set; }
        public WorkspacePanelLayout? SavedLayout { get; set; }
    }

    private const string SampleMacro = """
    {
      "version": 1,
      "name": "baseline",
      "steps": [
        { "type": "key.tap", "key": "A", "modifiers": ["LeftCtrl"], "holdMs": 5 },
        { "type": "mouse.move", "mode": "relative", "x": 25, "y": -10, "durationMs": 0 },
        { "type": "mouse.wheel", "vertical": -1, "horizontal": 0 },
        { "type": "consumer.tap", "control": "VolumeUp" },
        { "type": "wait", "ms": 2 }
      ]
    }
    """;
}
