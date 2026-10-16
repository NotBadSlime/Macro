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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacroHid.Converter;
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
    private readonly DispatcherTimer autoSaveTimer;
    private readonly DispatcherTimer coreSelectionTimer;
    private readonly CoreSelectionSchedule coreSelectionSchedule = new();
    private int coreSelectionPollInFlight;
    private readonly ColorSampleOverlayWindow colorSampleOverlay = new();
    private RuntimePrecisionSettings runtimePrecisionSettings = RuntimePrecisionSettingsStore.Load();

    private GlobalKeyboardHook? keyboardHook;
    private bool runNowEmergencyStopArmed;
    private MacroInputRecorder? macroRecorder;
    private MacroPlaybackController? playbackController;
    private readonly Dictionary<string, MacroPlaybackController> listeningControllers = [];
    private readonly Dictionary<string, string> listeningMacroNames = [];
    private readonly Dictionary<string, string> listeningGroupProcessFilters = [];
    private readonly HashSet<string> activeTriggeredControllerIds = [];
    private readonly Dictionary<string, WorkspacePanelRegistration> workspacePanels = [];
    private readonly HashSet<string> pendingFloatingPanelIds = [];
    private WorkspaceLayout workspaceLayout = new([], WorkspaceLayoutStore.CurrentVersion);
    private HwndSource? windowSource;
    private bool listening;
    private bool listeningPaused;
    private bool pauseOwnedByCapture;
    private bool coreHotkeysSuspended;
    private readonly List<string> pausedListeningIds = [];
    private bool updatingLanguageComboBox;
    private bool updatingWorkspaceMenu;
    private bool syncingJsonPanel;
    private bool workspaceContentRendered;
    private bool macroRecordingTargetsCondition;
    private string activeRightToolPanelId = "actions";
    private string? activeEditorMacroId;
    private WindowState windowStateBeforeRecording = WindowState.Normal;
    private readonly Stack<string> macroNavigationStack = new();
    private bool suppressMacroNavigationStackClear;

    private sealed record ListeningCandidate(
        MacroLibraryItem Item,
        MacroDocument Document,
        HotkeyGesture Trigger,
        string GroupProcessFilter)
    {
        public string EffectiveProcessFilter => GroupProcessFilter;
    }

    public MainWindow()
    {
        editorState = new MacroEditorState(libraryStore);

        InitializeComponent();
        // Keep the larger sequence viewport inside the usable desktop on small screens.
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24));
        autoSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        autoSaveTimer.Tick += (_, _) => PerformDebouncedAutoSave();
        coreSelectionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        coreSelectionTimer.Tick += (_, _) => {
            PollAutomaticCoreSelection();
            var preparation = PlaybackRuntimeStatus.Preparation;
            var state = preparation.Preparing ? "准备中" : preparation.Ready ? "已就绪" : "待准备";
            var reason = string.Join("；", new[] { preparation.Reason, PlaybackRuntimeStatus.FallbackReason }.Where(s => !string.IsNullOrEmpty(s)));
            PerformanceStatusText.Text = $"{state} · 实际后端：{PlaybackRuntimeStatus.ActualBackend}"
                + (PlaybackRuntimeStatus.ActiveRuns > 2 ? $" · 并发 {PlaybackRuntimeStatus.ActiveRuns}，超出已验证规模" : "");
            PerformanceStatusText.ToolTip = $"准备后端：{preparation.Backend}\n回退 / 准备原因：{(reason.Length == 0 ? "无" : reason)}";
        };
        ContentRendered += MainWindow_ContentRendered;
        LocalizationService.Initialize();
        ConfigureWorkspaceDockHost();
        RefreshThemeToggleButton();

        InitializeLanguageComboBox();
        InitializePanels();
        LibraryPanel.SetRuntimePrecisionControls(runtimePrecisionSettings);
        WarmUpRuntimePrecision();
        InitializeWorkspacePanels();
        ApplyLocalization();
        InitializeMacroLibrary();
        coreSelectionTimer.Start();
        PollAutomaticCoreSelection();
        RefreshKeyboardHook();
        StateChanged += (_, _) => RefreshWindowChromeButtons();
        RefreshWindowChromeButtons();
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        workspaceContentRendered = true;
        RestorePendingFloatingPanels();
    }

    private void InitializePanels()
    {
        LibraryPanel.Initialize(editorState);
        SequencePanelControl.Initialize(editorState);
        ConditionPanel.Initialize(editorState);

        LibraryPanel.MacroSelected += OnMacroSelected;
        LibraryPanel.InsertConditionMacroRequested += OnConditionListMacroLibraryDropped;
        LibraryPanel.MacroCreated += OnMacroCreated;
        LibraryPanel.MacroDuplicated += OnMacroDuplicated;
        LibraryPanel.MacroDeleted += OnMacroDeleted;
        LibraryPanel.LibraryItemsDeleteRequested += OnLibraryItemsDeleteRequested;
        LibraryPanel.MacroLockChanged += OnLibraryMacroLockChanged;
        LibraryPanel.ImportApplied += OnImportApplied;
        LibraryPanel.DocumentRequested += () => GetDocumentWithPlayback();
        LibraryPanel.ResultMessage += msg => SetStatus(msg);
        LibraryPanel.StartListeningGroupsRequested += OnStartListeningGroups;
        LibraryPanel.StopListeningGroupsRequested += OnStopListeningGroups;
        LibraryPanel.StopListeningAllRequested += OnStopListeningAll;
        LibraryPanel.PrecisionSettingsEdited += OnPrecisionSettingsEdited;
        LibraryPanel.BasicControlRequested += OnBasicControlRequested;
        LibraryPanel.PrecisionControlRequested += OnPrecisionControlRequested;
        LibraryPanel.LibraryStructureEdited += OnLibraryStructureEdited;

        SequencePanelControl.SaveLibraryRequested += OnSaveLibrary;
        SequencePanelControl.RunNowRequested += OnRunNow;
        SequencePanelControl.StopRequested += OnStopPlayback;
        SequencePanelControl.RecordingStartRequested += OnStartRecording;
        SequencePanelControl.RecordingStopRequested += OnStopRecording;
        SequencePanelControl.StepSelectionChanged += OnStepSelectionChanged;
        SequencePanelControl.ActionTemplateDropped += OnActionTemplateDropped;
        SequencePanelControl.MacroLibraryDropped += OnMacroLibraryDropped;
        SequencePanelControl.UndoApplied += OnSequenceUndoApplied;
        SequencePanelControl.RedoApplied += OnSequenceRedoApplied;
        SequencePanelControl.DocumentEdited += OnSequenceDocumentEdited;
        SequencePanelControl.SequenceActivated += ConditionPanel.DeactivateThenActionSequence;
        SequencePanelControl.EditorTextChanged += OnSequenceEditorTextChanged;
        SequencePanelControl.EditLockChanged += OnEditLockChanged;
        SequencePanelControl.OpenReferencedMacroRequested += OnOpenReferencedMacroRequested;
        SequencePanelControl.ReturnPreviousMacroRequested += OnReturnPreviousMacroRequested;
        SequencePanelControl.LibraryPathNavigateRequested += OnLibraryPathNavigate;

        JsonPanel.EditorTextChanged += OnJsonPanelEditorTextChanged;
        JsonPanel.ApplyJsonRequested += OnApplyJsonRequested;

        ActionPalette.ActionClicked += OnActionPaletteClicked;

        PlaybackPanelControl.StartListeningRequested += OnStartCurrentListening;
        PlaybackPanelControl.StopListeningRequested += OnStopCurrentListening;
        PlaybackPanelControl.RunNowRequested += OnRunNow;
        PlaybackPanelControl.StopPlaybackRequested += OnStopPlayback;
        PlaybackPanelControl.PlaybackSettingsEdited += OnPlaybackSettingsEdited;
        PlaybackPanelControl.TriggerCaptureStarted += PauseListeningForCapture;
        PlaybackPanelControl.TriggerCaptureFinished += ResumeListeningAfterCapture;
        StepEditorPanel.AnyKeyCaptureStarted += PauseListeningForCapture;
        StepEditorPanel.AnyKeyCaptureFinished += ResumeListeningAfterCapture;

        ConditionPanel.ConditionSelectionChanged += OnConditionSelectionChanged;
        ConditionPanel.ConditionsModified += OnConditionsModified;
        ConditionPanel.PickRegionRequested += OnPickRegionRequested;
        ConditionPanel.RecordingStartRequested += OnStartConditionRecording;
        ConditionPanel.RecordingStopRequested += OnStopRecording;
        ConditionPanel.StatusMessageRequested += msg => SetStatus(msg);
        ConditionPanel.ExtractConditionPackRequested += OnExtractConditionPackRequested;
        ConditionPanel.MacroLibraryDroppedOnConditionList += OnConditionListMacroLibraryDropped;
        ConditionPanel.InsertSelectedLibraryConditionMacroRequested += OnInsertSelectedLibraryConditionMacro;
    }

    private void ConfigureWorkspaceDockHost()
    {
        DockHost.AttachRegions(
            LeftDockColumn,
            RightDockColumn,
            BottomDockRow,
            LeftDockSplitter,
            RightDockSplitter,
            BottomDockSplitter,
            LeftDockContent,
            RightDockContent,
            BottomDockContent);
        DockHost.DockSizeChanged += (_, _) =>
        {
            SaveDockSizes();
            SaveWorkspaceLayout();
        };
        DockHost.BottomResizeRequested += (_, _) => OpenBottomRegionForResize();
    }

    private void InitializeWorkspacePanels()
    {
        RegisterWorkspacePanel("library", WorkspaceTitle("WorkspacePanelLibrary"), LibraryToolChrome, LibraryPanelHost, WorkspaceDockRegion.Left, 260, 0);
        RegisterWorkspacePanel("sequence", WorkspaceTitle("WorkspacePanelSequence"), SequenceToolChrome, SequencePanelHost, WorkspaceDockRegion.Center, 0, 0);
        RegisterWorkspacePanel("conditions", WorkspaceTitle("WorkspacePanelConditions"), ConditionToolChrome, ConditionPanelHost, WorkspaceDockRegion.Right, 390, 0, defaultIsVisible: false);
        RegisterWorkspacePanel("actions", WorkspaceTitle("WorkspacePanelActions"), ActionPaletteToolChrome, ActionPaletteHost, WorkspaceDockRegion.Right, 390, 1);
        RegisterWorkspacePanel("playback", WorkspaceTitle("WorkspacePanelPlayback"), PlaybackToolChrome, PlaybackPanelHost, WorkspaceDockRegion.Right, 390, 2);
        RegisterWorkspacePanel("json", WorkspaceTitle("AdvancedJson"), JsonToolChrome, JsonPanelHost, WorkspaceDockRegion.Bottom, 230, 0, defaultIsVisible: false);

        workspaceLayout = WorkspaceLayoutStore.Load();
        ApplyDockSizes();
        ApplyWorkspaceLayout();
        UpdateWorkspaceMenuChecks();
    }

    private void RegisterWorkspacePanel(
        string id,
        string title,
        FrameworkElement dockContainer,
        ContentControl host,
        WorkspaceDockRegion defaultDockRegion,
        double defaultDockedSize,
        int defaultOrder,
        bool defaultIsVisible = true)
    {
        if (host.Content is not FrameworkElement element)
        {
            throw new InvalidOperationException($"Workspace panel '{id}' has no content.");
        }

        workspacePanels[id] = new WorkspacePanelRegistration(id, title, dockContainer, host, element, defaultDockRegion, defaultDockedSize, defaultOrder, defaultIsVisible)
        {
            IsVisible = defaultIsVisible,
            DockRegion = defaultDockRegion
        };
    }

    private void ApplyWorkspaceLayout()
    {
        var layoutNeedsSave = workspaceLayout.Version < WorkspaceLayoutStore.CurrentVersion;
        foreach (var panel in workspacePanels.Values)
        {
            var rawLayout = workspaceLayout.Panels.TryGetValue(panel.Id, out var saved)
                ? saved
                : GetDefaultWorkspacePanelLayout(panel.Id);
            var layout = NormalizeWorkspacePanelLayout(panel, rawLayout, workspaceLayout.Version, out var layoutChanged);
            layoutNeedsSave |= layoutChanged;

            panel.IsVisible = layout.IsVisible;
            panel.SavedLayout = layout;
            panel.DockRegion = layout.DockRegion;
            if (layout.DockRegion == WorkspaceDockRegion.Floating)
            {
                QueueFloatingWorkspacePanel(panel.Id);
            }
            else
            {
                DockWorkspacePanel(panel.Id, saveLayout: false);
                ToggleWorkspacePanelVisibility(panel.Id, layout.IsVisible, saveLayout: false);
            }
        }

        ApplyDockRegionVisibility();
        if (layoutNeedsSave)
        {
            SaveWorkspaceLayout();
        }
    }

    private void ApplyDockSizes()
    {
        DockHost.ApplyDockSizes(workspaceLayout.DockSizes ?? new WorkspaceDockSizes());
    }

    private void SaveDockSizes()
    {
        workspaceLayout = workspaceLayout with { DockSizes = DockHost.CaptureDockSizes() };
    }

    private WorkspacePanelLayout NormalizeWorkspacePanelLayout(
        WorkspacePanelRegistration panel,
        WorkspacePanelLayout layout,
        int layoutVersion,
        out bool changed)
    {
        changed = false;
        var dockRegion = layout.DockRegion;
        var isVisible = layout.IsVisible;
        var dockedSize = layout.DockedSize;
        var order = layout.Order;

        if (layoutVersion < WorkspaceLayoutStore.CurrentVersion)
        {
            dockRegion = panel.DefaultDockRegion;
            isVisible = panel.DefaultIsVisible;
            dockedSize = panel.DefaultDockedSize;
            order = panel.DefaultOrder;
            changed = true;
        }

        if (dockRegion != WorkspaceDockRegion.Floating && dockRegion != panel.DefaultDockRegion)
        {
            dockRegion = panel.DefaultDockRegion;
            changed = true;
        }

        if (dockedSize <= 0 && panel.DefaultDockedSize > 0)
        {
            dockedSize = panel.DefaultDockedSize;
            changed = true;
        }

        if (order != panel.DefaultOrder && layoutVersion < WorkspaceLayoutStore.CurrentVersion)
        {
            order = panel.DefaultOrder;
            changed = true;
        }

        return layout with
        {
            IsVisible = isVisible,
            DockRegion = dockRegion,
            DockedSize = dockedSize,
            Order = order
        };
    }

    private WorkspacePanelLayout GetDefaultWorkspacePanelLayout(string id)
    {
        if (!workspacePanels.TryGetValue(id, out var panel))
        {
            return new WorkspacePanelLayout();
        }

        return new WorkspacePanelLayout(
            IsVisible: panel.DefaultIsVisible,
            DockRegion: panel.DefaultDockRegion,
            DockedSize: panel.DefaultDockedSize,
            Order: panel.DefaultOrder,
            IsPinned: true,
            FloatingBounds: new WorkspaceFloatingBounds(120, 120, 720,
                id == "sequence" ? Math.Min(760, Math.Max(220, SystemParameters.WorkArea.Height - 24)) : 520));
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

    private void FloatPanelFromHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            FloatWorkspacePanel(id);
            e.Handled = true;
        }
    }

    private void CloseWorkspacePanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ToggleWorkspacePanelVisibility(id, false);
            e.Handled = true;
        }
    }

    private void ToggleWorkspacePanelVisibility(string id, bool visible, bool saveLayout = true)
    {
        if (!workspacePanels.TryGetValue(id, out var panel)) return;

        if (visible && string.Equals(id, "json", StringComparison.OrdinalIgnoreCase))
        {
            RefreshJsonPanelText();
        }

        panel.IsVisible = visible;
        if (visible && panel.DockRegion == WorkspaceDockRegion.Right && !panel.IsFloating)
        {
            activeRightToolPanelId = id;
        }

        if (panel.IsFloating)
        {
            EnsureWorkspacePanelWindow(panel);
            if (visible)
            {
                DialogOwnerService.AssignOwnerIfShown(panel.Window!, this);
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
            panel.DockContainer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        ApplyDockRegionVisibility();
        UpdateWorkspaceMenuChecks();
        if (saveLayout)
        {
            SaveWorkspaceLayout();
        }
    }

    private void FloatWorkspacePanel(string id, bool saveLayout = true)
    {
        if (!workspacePanels.TryGetValue(id, out var panel)) return;
        if (string.Equals(id, "json", StringComparison.OrdinalIgnoreCase))
        {
            RefreshJsonPanelText();
        }

        if (!workspaceContentRendered)
        {
            QueueFloatingWorkspacePanel(id);
            if (saveLayout)
            {
                SaveWorkspaceLayout();
            }

            return;
        }

        EnsureWorkspacePanelWindow(panel);

        if (!ReferenceEquals(panel.Window!.ContentHost.Content, panel.Element))
        {
            panel.DockHost.Content = null;
            panel.Window.ContentHost.Content = panel.Element;
        }

        panel.IsFloating = true;
        panel.DockRegion = WorkspaceDockRegion.Floating;
        panel.IsVisible = true;
        panel.DockContainer.Visibility = Visibility.Collapsed;
        RestoreFloatingBounds(panel);
        DialogOwnerService.AssignOwnerIfShown(panel.Window!, this);
        panel.Window.Show();
        panel.Window.Activate();
        ApplyDockRegionVisibility();
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
        pendingFloatingPanelIds.Remove(id);
        panel.DockRegion = ResolveDockRegion(panel);
        ApplyWorkspaceDockRegion(panel, panel.DockRegion);
        if (panel.DockRegion == WorkspaceDockRegion.Right)
        {
            activeRightToolPanelId = panel.Id;
        }

        panel.DockContainer.Visibility = panel.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        ApplyDockRegionVisibility();
        UpdateWorkspaceMenuChecks();
        if (saveLayout)
        {
            SaveWorkspaceLayout();
        }
    }

    private void ResetWorkspaceLayout()
    {
        WorkspaceLayoutStore.Reset();
        activeRightToolPanelId = "actions";
        workspaceLayout = new([], WorkspaceLayoutStore.CurrentVersion, new WorkspaceDockSizes());
        ApplyDockSizes();
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
            DialogOwnerService.AssignOwnerIfShown(panel.Window!, this);
            return;
        }

        panel.Window = new WorkspacePanelWindow(panel.Id, panel.Title);
        DialogOwnerService.AssignOwnerIfShown(panel.Window!, this);
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
        var bounds = layout.FloatingBounds ?? new WorkspaceFloatingBounds();
        var width = ClampFinite(bounds.Width, panel.Window.MinWidth, SystemParameters.VirtualScreenWidth);
        var height = ClampFinite(bounds.Height, panel.Window.MinHeight, SystemParameters.VirtualScreenHeight);
        var minLeft = SystemParameters.VirtualScreenLeft;
        var minTop = SystemParameters.VirtualScreenTop;
        var maxLeft = minLeft + Math.Max(0, SystemParameters.VirtualScreenWidth - width);
        var maxTop = minTop + Math.Max(0, SystemParameters.VirtualScreenHeight - height);
        panel.Window.Width = width;
        panel.Window.Height = height;
        panel.Window.Left = ClampFinite(bounds.Left, minLeft, maxLeft);
        panel.Window.Top = ClampFinite(bounds.Top, minTop, maxTop);
    }

    private void QueueFloatingWorkspacePanel(string id)
    {
        if (!workspacePanels.TryGetValue(id, out var panel)) return;

        panel.IsFloating = true;
        panel.DockRegion = WorkspaceDockRegion.Floating;
        panel.DockContainer.Visibility = Visibility.Collapsed;
        pendingFloatingPanelIds.Add(id);

        if (workspaceContentRendered)
        {
            RestorePendingFloatingPanels();
        }
    }

    private void RestorePendingFloatingPanels()
    {
        foreach (var id in pendingFloatingPanelIds.ToList())
        {
            if (!workspacePanels.TryGetValue(id, out var panel))
            {
                pendingFloatingPanelIds.Remove(id);
                continue;
            }

            if (panel.IsVisible)
            {
                FloatWorkspacePanel(id, saveLayout: false);
            }
            else
            {
                panel.IsFloating = true;
                panel.DockRegion = WorkspaceDockRegion.Floating;
                panel.DockContainer.Visibility = Visibility.Collapsed;
            }

            pendingFloatingPanelIds.Remove(id);
        }

        ApplyDockRegionVisibility();
        UpdateWorkspaceMenuChecks();
    }

    private static double ClampFinite(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return min;

        return Math.Clamp(value, min, Math.Max(min, max));
    }

    private WorkspaceDockRegion ResolveDockRegion(WorkspacePanelRegistration panel)
    {
        var saved = panel.SavedLayout?.DockRegion ?? panel.DefaultDockRegion;
        return saved == WorkspaceDockRegion.Floating ? panel.DefaultDockRegion : saved;
    }

    private void ApplyWorkspaceDockRegion(WorkspacePanelRegistration panel, WorkspaceDockRegion region)
    {
        if (region == WorkspaceDockRegion.Floating)
        {
            return;
        }

        var target = GetDockTarget(region);
        if (!ReferenceEquals(panel.DockContainer.Parent, target))
        {
            DetachFromParent(panel.DockContainer);
            target.Children.Add(panel.DockContainer);
        }

        panel.DockRegion = region;
        panel.IsFloating = false;
        SortDockRegion(target);
        ApplyDockRegionVisibility();
    }

    private Panel GetDockTarget(WorkspaceDockRegion region) => region switch
    {
        WorkspaceDockRegion.Left => LeftDockContent,
        WorkspaceDockRegion.Center => CenterDockContent,
        WorkspaceDockRegion.Bottom => BottomDockContent,
        _ => RightToolDeck
    };

    private static void DetachFromParent(FrameworkElement element)
    {
        if (element.Parent is Panel panel)
        {
            panel.Children.Remove(element);
        }
        else if (element.Parent is ContentControl contentControl)
        {
            contentControl.Content = null;
        }
    }

    private void SortDockRegion(Panel target)
    {
        var ordered = target.Children
            .OfType<FrameworkElement>()
            .OrderBy(GetWorkspacePanelOrder)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var currentIndex = target.Children.IndexOf(ordered[i]);
            if (currentIndex != i)
            {
                target.Children.RemoveAt(currentIndex);
                target.Children.Insert(i, ordered[i]);
            }
        }
    }

    private int GetWorkspacePanelOrder(FrameworkElement element)
    {
        return workspacePanels.Values.FirstOrDefault(p => ReferenceEquals(p.DockContainer, element))?.SavedLayout?.Order
            ?? workspacePanels.Values.FirstOrDefault(p => ReferenceEquals(p.DockContainer, element))?.DefaultOrder
            ?? 0;
    }

    private void RightToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string id })
        {
            SelectRightToolPanel(id);
            e.Handled = true;
        }
    }

    private void SelectRightToolPanel(string id)
    {
        if (!workspacePanels.TryGetValue(id, out var panel) || panel.DockRegion != WorkspaceDockRegion.Right)
            return;

        if (!panel.IsVisible)
        {
            panel.IsVisible = true;
        }

        activeRightToolPanelId = id;
        ApplyDockRegionVisibility();
        UpdateWorkspaceMenuChecks();
        SaveWorkspaceLayout();
    }

    private void ApplyRightToolPanelVisibility()
    {
        var visibleRightPanels = workspacePanels.Values
            .Where(panel => panel.IsVisible && !panel.IsFloating && panel.DockRegion == WorkspaceDockRegion.Right)
            .OrderBy(panel => panel.SavedLayout?.Order ?? panel.DefaultOrder)
            .ToList();

        if (visibleRightPanels.Count > 0 && visibleRightPanels.All(panel => panel.Id != activeRightToolPanelId))
        {
            activeRightToolPanelId = visibleRightPanels[0].Id;
        }

        foreach (var panel in workspacePanels.Values.Where(panel => panel.DockRegion == WorkspaceDockRegion.Right && !panel.IsFloating))
        {
            panel.DockContainer.Visibility = panel.IsVisible && panel.Id == activeRightToolPanelId
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        SetRightToolButtonState(ConditionRightToolButton, "conditions");
        SetRightToolButtonState(ActionRightToolButton, "actions");
        SetRightToolButtonState(PlaybackRightToolButton, "playback");
    }

    private void SetRightToolButtonState(ToggleButton button, string id)
    {
        if (!workspacePanels.TryGetValue(id, out var panel))
            return;

        var visible = panel.IsVisible && !panel.IsFloating && panel.DockRegion == WorkspaceDockRegion.Right;
        button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        button.IsChecked = visible && string.Equals(activeRightToolPanelId, id, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyDockRegionVisibility()
    {
        var leftVisible = HasVisibleDockedPanel(WorkspaceDockRegion.Left);
        var rightVisible = HasVisibleDockedPanel(WorkspaceDockRegion.Right);
        var bottomVisible = HasVisibleDockedPanel(WorkspaceDockRegion.Bottom);

        ApplyRightToolPanelVisibility();
        DockHost.SetRegionVisible(WorkspaceDockRegion.Left, leftVisible);
        DockHost.SetRegionVisible(WorkspaceDockRegion.Right, rightVisible);
        DockHost.SetRegionVisible(WorkspaceDockRegion.Bottom, bottomVisible);
        BottomDockSplitterRow.Height = new GridLength(12);
    }

    private void OpenBottomRegionForResize()
    {
        if (!workspacePanels.TryGetValue("json", out var panel))
            return;

        if (panel.IsFloating)
        {
            DockWorkspacePanel(panel.Id, saveLayout: false);
        }

        ToggleWorkspacePanelVisibility(panel.Id, true, saveLayout: false);
        SaveWorkspaceLayout();
    }

    private bool HasVisibleDockedPanel(WorkspaceDockRegion region)
    {
        return workspacePanels.Values.Any(panel =>
            panel.IsVisible
            && !panel.IsFloating
            && panel.DockRegion == region);
    }

    private double GetDockedSize(WorkspaceDockRegion region, double fallback)
    {
        return workspacePanels.Values
            .Where(panel => panel.DockRegion == region)
            .Select(panel => panel.SavedLayout?.DockedSize ?? panel.DefaultDockedSize)
            .FirstOrDefault(size => size > 0) is var size && size > 0
                ? size
                : fallback;
    }

    private void SaveWorkspaceLayout()
    {
        var panels = new Dictionary<string, WorkspacePanelLayout>(StringComparer.OrdinalIgnoreCase);
        var dockSizes = DockHost.CaptureDockSizes();
        foreach (var panel in workspacePanels.Values)
        {
            var current = panel.SavedLayout ?? GetDefaultWorkspacePanelLayout(panel.Id);
            var bounds = current.FloatingBounds ?? new WorkspaceFloatingBounds();
            var dockedSize = GetCurrentDockedSize(panel);

            if (panel.Window is not null)
            {
                bounds = new WorkspaceFloatingBounds(panel.Window.Left, panel.Window.Top, panel.Window.Width, panel.Window.Height);
            }

            panel.SavedLayout = new WorkspacePanelLayout(
                panel.IsVisible,
                panel.DockRegion,
                dockedSize,
                current.Order,
                current.IsPinned,
                bounds);
            panels[panel.Id] = panel.SavedLayout;
        }

        workspaceLayout = new WorkspaceLayout(panels, WorkspaceLayoutStore.CurrentVersion, dockSizes);
        WorkspaceLayoutStore.Save(workspaceLayout);
    }

    private double GetCurrentDockedSize(WorkspacePanelRegistration panel)
    {
        return panel.DockRegion switch
        {
            WorkspaceDockRegion.Left => Math.Max(0, LeftDockColumn.ActualWidth),
            WorkspaceDockRegion.Right => Math.Max(0, RightDockColumn.ActualWidth),
            WorkspaceDockRegion.Bottom => Math.Max(0, BottomDockRow.ActualHeight),
            _ => panel.SavedLayout?.DockedSize ?? panel.DefaultDockedSize
        };
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
        jsonRevision++;
        jsonValid = true;
        JsonPanel.SetValidationMessage("");
        syncingJsonPanel = true;
        JsonPanel.EditorText = text;
        syncingJsonPanel = false;
    }

    private void RefreshJsonPanelText()
    {
        jsonRevision++;
        jsonValid = true;
        if (syncingJsonPanel) return;

        try
        {
            syncingJsonPanel = true;
            JsonPanel.EditorText = SequencePanelControl.EditorText;
        }
        finally
        {
            syncingJsonPanel = false;
        }
    }

    private void OnJsonPanelEditorTextChanged(string text)
    {
        if (syncingJsonPanel || editorState.IsEditLocked) return;
        QueueJsonValidation(text);
    }

    private void OnApplyJsonRequested()
    {
        jsonRevision++;
        if (RejectLockedEdit()) return;
        TryApplyJsonTextToEditor(JsonPanel.EditorText, showStatus: true);
    }

    private bool TryApplyJsonTextToEditor(string text, bool showStatus, MacroDocument? parsed = null)
    {
        if (editorState.IsEditLocked)
        {
            if (showStatus) SetStatus(L("MacroLockedReadOnly"));
            return false;
        }

        try
        {
            parsed ??= McrxParser.Parse(text);
            syncingJsonPanel = true;
            SequencePanelControl.SetParsedDocument(text, parsed);
            syncingJsonPanel = false;
            jsonValid = true;
            JsonPanel.SetValidationMessage("");
            RefreshEditorAfterSequenceDocumentChange(showStatus ? L("JsonApplied") : null);
            return true;
        }
        catch (Exception ex)
        {
            jsonValid = false;
            JsonPanel.SetValidationMessage(ex.Message);
            syncingJsonPanel = false;
            if (showStatus)
            {
                SetErrorStatus(ex);
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
            SequencePanelControl.SetLibraryPath(string.Empty);
            SequencePanelControl.ClearUndoHistory();
            RefreshConditionStepChoices();
            ApplyEditLock(false);
        }
    }

    private void LoadMacroFromLibrary(string id)
    {
        try
        {
            editorState.SelectedMacroId = id;
            activeEditorMacroId = id;
            libraryStore.SetSelected(id);
            var document = libraryStore.ReadMacro(id);
            SequencePanelControl.SetEditorDocument(document);
            SequencePanelControl.ClearUndoHistory();
            PlaybackPanelControl.SetPlaybackControls(document.Playback);
            RefreshConditionStepChoices();
            ConditionPanel.LoadConditions(document.EffectiveConditions);
            SequencePanelControl.SetConditionHighlights(document.EffectiveConditions, i => ConditionPanel.GetConditionColor(i));
            CheckAndWarnConflicts(document);
            var item = libraryStore.Load().Items.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            ApplyEditLock(item?.IsLocked == true);
            SetLibraryPathForItem(item, document.Name);
            SetStatus(document.Name);
            SequencePanelControl.FocusMacroNameEditor();
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
        }
    }

    private void OnMacroSelected(string id)
    {
        ClearMacroNavigationStackUnlessSuppressed();
        SaveActiveEditorBeforeSwitch(id);
        LoadMacroFromLibrary(id);
        RefreshMacroNavigationUi();
    }

    private void OnMacroCreated(MacroLibraryItem item)
    {
        ClearMacroNavigationStack();
        SaveActiveEditorBeforeSwitch(item.Id);
        editorState.SelectedMacroId = item.Id;
        LoadMacroFromLibrary(item.Id);
        RefreshMacroNavigationUi();
    }

    private void OnMacroDuplicated(string id)
    {
        try
        {
            ClearMacroNavigationStack();
            var source = libraryStore.Load().Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            var document = source?.IsLocked == true
                ? libraryStore.ReadMacro(id)
                : SequencePanelControl.GetCurrentDocument();
            if (source?.IsLocked != true)
            {
                SaveCurrentRevision(id, document);
            }
            var item = libraryStore.DuplicateMacro(id, $"{document.Name} Copy");
            editorState.SelectedMacroId = item.Id;
            LibraryPanel.RefreshList();
            LoadMacroFromLibrary(item.Id);
            RefreshMacroNavigationUi();
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
        }
    }

    private void OnMacroDeleted(string id)
    {
        var item = libraryStore.Load().Items.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        var name = item?.Name ?? SequencePanelControl.MacroName;
        var result = DialogOwnerService.MessageBoxSafe(
            this,
            LocalizationService.Format("DeleteMacroConfirm", string.IsNullOrWhiteSpace(name) ? L("Macro") : name),
            L("Delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            ClearMacroNavigationStack();
            libraryStore.DeleteMacro(id);
            editorState.ReloadLibrary();
            editorState.SelectedMacroId = editorState.LibrarySnapshot.SelectedMacroId;
            activeEditorMacroId = editorState.SelectedMacroId;
            LibraryPanel.RefreshList();

            if (editorState.SelectedMacroId is not null)
                LoadMacroFromLibrary(editorState.SelectedMacroId);
            else
            {
                SequencePanelControl.SetEditorDocument(new MacroDocument(1, "Macro 1", PlaybackSettings.Default, []));
                SequencePanelControl.ClearUndoHistory();
                RefreshConditionStepChoices();
                ConditionPanel.LoadConditions([]);
                ApplyEditLock(false);
            }

            OnLibraryStructureEdited();
            SetStatus(LocalizationService.Format("MacroDeleted", name));
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            var message = LocalizationService.Format("DeleteFailed", ex.Message);
            SetStatus(message);
            DialogOwnerService.MessageBoxSafe(
                this,
                message,
                L("Delete"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnLibraryItemsDeleteRequested(IReadOnlyList<MacroLibraryDeleteItem> requests)
    {
        var deletable = requests.Where(request => !request.IsLocked).ToList();
        if (deletable.Count == 0)
        {
            SetStatus(L("MacroLockedReadOnly"));
            return;
        }

        var result = DialogOwnerService.MessageBoxSafe(
            this,
            LocalizationService.Format("DeleteSelectedItemsConfirm", deletable.Count),
            L("Delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            foreach (var request in deletable)
            {
                if (request.IsFolder)
                {
                    libraryStore.DeleteFolder(request.FolderName, deleteMacros: false, request.GroupId);
                }
                else if (request.MacroId is { } macroId)
                {
                    libraryStore.DeleteMacro(macroId);
                }
            }

            editorState.ReloadLibrary();
            editorState.SelectedMacroId = editorState.LibrarySnapshot.SelectedMacroId;
            activeEditorMacroId = editorState.SelectedMacroId;
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
                ConditionPanel.LoadConditions([]);
                ApplyEditLock(false);
            }

            OnLibraryStructureEdited();
            SetStatus(LocalizationService.Format("SelectedItemsDeleted", deletable.Count));
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            var message = LocalizationService.Format("DeleteFailed", ex.Message);
            SetStatus(message);
            DialogOwnerService.MessageBoxSafe(this, message, L("Delete"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnLibraryMacroLockChanged(string macroId, bool isLocked)
    {
        editorState.ReloadLibrary();
        if (string.Equals(editorState.SelectedMacroId ?? activeEditorMacroId, macroId, StringComparison.OrdinalIgnoreCase))
        {
            ApplyEditLock(isLocked);
        }
    }

    private void OnSaveLibrary()
    {
        if (RejectLockedEdit()) return;
        try
        {
            AutoSaveCurrentMacro(updateStatus: false);
            SetStatus(L("LibrarySaved"));
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
        }
    }

    private void OnPlaybackSettingsEdited()
    {
        if (editorState.IsEditLocked) return;
        try
        {
            ScheduleAutoSave();
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
        }
    }

    private void OnPrecisionSettingsEdited()
    {
        try
        {
            runtimePrecisionSettings = ReadRuntimePrecisionSettingsFromPanel();
            RuntimePrecisionSettingsStore.Save(runtimePrecisionSettings);
            SystemLatencyCheck.ApplyQuietCores(runtimePrecisionSettings.AvoidNoisyCores ? SystemLatencyCheck.StableQuietMask : 0);
            WarmUpRuntimePrecision();
            if (listening)
            {
                RestartListeningWithCurrentSettings();
            }
            else
            {
                RefreshKeyboardHook();
            }

            WarnColorSampleConflicts();
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
        }
    }

    private RuntimePrecisionSettings ReadRuntimePrecisionSettingsFromPanel()
    {
        // 以面板持有的完整设置为底，避免丢失默认间隔、组合键间隔、强制停止键、关闭按钮等字段。
        var current = LibraryPanel.RuntimeSettings;
        var affinityMask = PlaybackAffinityMask.NormalizeOrThrow(current.AffinityMask);
        return current with
        {
            Precision = LibraryPanel.GetSelectedPrecisionMode(),
            AffinityMask = affinityMask
        };
    }

    private void WarmUpRuntimePrecision()
    {
        NativePlaybackWarmup.QueueWarmUpForPrecision(
            runtimePrecisionSettings.Precision,
            runtimePrecisionSettings.AffinityMask,
            runtimePrecisionSettings.Generation);
    }

    private void RestoreMaskAfterCancelledTest(string savedMask, CorePickerDialog dialog)
    {
        if (!dialog.LastMeasureSucceeded
            || string.Equals(dialog.LastMeasuredMask, savedMask, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(savedMask))
        {
            NativePlaybackWarmup.QueueWarmUpForPrecision(runtimePrecisionSettings.Precision, "", runtimePrecisionSettings.Generation);
            return;
        }

        NativePlaybackWarmup.QueueWarmUpForAffinityMask(savedMask, force: true);
    }

    private void PollAutomaticCoreSelection()
    {
        if (runtimePrecisionSettings.Precision != PrecisionMode.UltraLowJitter)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref coreSelectionPollInFlight, 1, 0) != 0)
        {
            return;
        }

        var filters = editorState.LibrarySnapshot.Groups
            .Where(group => !group.IsGlobal && !string.IsNullOrWhiteSpace(group.ProcessFilter))
            .Select(group => group.ProcessFilter)
            .ToArray();
        _ = Task.Run(() =>
        {
            var names = QueryRunningProcessNames();
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ApplyAutomaticCoreSelection(filters, names);
                }
                finally
                {
                    Interlocked.Exchange(ref coreSelectionPollInFlight, 0);
                }
            });
        });
    }

    private void ApplyAutomaticCoreSelection(IReadOnlyList<string> filters, IReadOnlyList<string> runningProcessNames)
    {
        if (runtimePrecisionSettings.Precision != PrecisionMode.UltraLowJitter)
        {
            return;
        }

        var blocked = IsCoreSelectionBlocked();
        if (!coreSelectionSchedule.TryConsumeAutomatic(filters, runningProcessNames, blocked))
        {
            return;
        }

        var mask = runtimePrecisionSettings.AffinityMask;
        if (string.IsNullOrWhiteSpace(mask))
        {
            var defaults = LogicalProcessorInventory.DefaultSelection(LogicalProcessorInventory.Query());
            if (defaults.Count == 0)
            {
                return;
            }

            mask = LogicalProcessorInventory.MaskFromProcessors(defaults);
            runtimePrecisionSettings = runtimePrecisionSettings with { AffinityMask = mask };
            LibraryPanel.SetRuntimePrecisionControls(runtimePrecisionSettings);
            RuntimePrecisionSettingsStore.Save(runtimePrecisionSettings);
        }

        if (IsCoreSelectionBlocked())
        {
            return;
        }

        NativePlaybackWarmup.QueueWarmUpForAffinityMask(mask, force: true);
    }

    private bool IsCoreSelectionBlocked()
    {
        if (playbackController?.Status is PlaybackStatus.Running or PlaybackStatus.Stopping)
        {
            return true;
        }

        return listeningControllers.Values.Any(controller =>
            controller.Status is PlaybackStatus.Running or PlaybackStatus.Stopping);
    }

    private static string[] QueryRunningProcessNames()
    {
        try
        {
            var processes = Process.GetProcesses();
            try
            {
                return processes
                    .Select(process => process.ProcessName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            return [];
        }
    }

    private async void RestartListeningWithCurrentSettings()
    {
        if (!listening || shutdownStarted) return;
        var activeIds = listeningControllers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int requested = ++listeningUpdateGeneration;
        try
        {
            var candidates = await Task.Run(() => BuildListeningCandidates().Where(c => activeIds.Contains(c.Item.Id)).ToList());
            if (requested != listeningUpdateGeneration || shutdownStarted) return;
            if (candidates.Count == 0) { StopListeningControllers(); listening = false; RefreshKeyboardHook(); return; }
            await StartListeningCandidates(candidates, editorState.SelectedMacroId is { } id ? [id] : null);
        }
        catch (Exception ex) { AppLog.Error("Listening", "Update failed; retaining previous definitions", ex); SetErrorStatus(ex); }
    }

    private void OnLibraryStructureEdited()
    {
        RestartListeningWithCurrentSettings();
        RefreshLibraryListeningState();
    }

    private void AutoSaveCurrentMacro(bool updateStatus)
    {
        // Always prefer the macro open in the editor. Library selection of a condition pack
        // must not retarget autosave, or the open document overwrites that pack file.
        var targetMacroId = activeEditorMacroId;
        if (targetMacroId is null
            && editorState.SelectedMacroId is { } selectedId
            && libraryStore.TryGetItem(selectedId)?.IsConditionMacro != true)
        {
            targetMacroId = selectedId;
        }

        var existingItem = targetMacroId is null
            ? null
            : libraryStore.Load().Items.FirstOrDefault(candidate => string.Equals(candidate.Id, targetMacroId, StringComparison.OrdinalIgnoreCase));
        if (existingItem?.IsLocked == true)
        {
            if (updateStatus) SetStatus(L("MacroLockedReadOnly"));
            return;
        }

        var document = GetDocumentWithPlayback();
        MacroLibraryItem item;
        if (targetMacroId is not null && existingItem is not null)
        {
            item = SaveCurrentRevision(targetMacroId, document);
        }
        else
        {
            item = libraryStore.CreateMacro(document, groupId: LibraryPanel.CurrentDatabaseGroupId);
        }

        activeEditorMacroId = item.Id;
        if (editorState.SelectedMacroId is null
            || string.Equals(editorState.SelectedMacroId, item.Id, StringComparison.OrdinalIgnoreCase)
            || libraryStore.TryGetItem(editorState.SelectedMacroId)?.IsConditionMacro != true)
        {
            editorState.SelectedMacroId = item.Id;
        }

        LibraryPanel.RefreshList();
        RefreshLibraryListeningState();
        if (updateStatus)
        {
            SetStatus(L("LibrarySaved"));
        }
    }

    private void ScheduleAutoSave()
    {
        if (editorState.IsEditLocked) return;
        autoSaveTimer.Stop();
        autoSaveTimer.Start();
    }

    private async void PerformDebouncedAutoSave()
    {
        autoSaveTimer.Stop();
        if (!jsonValid || shutdownStarted) return;
        try
        {
            var id = activeEditorMacroId;
            if (id is null) { AutoSaveCurrentMacro(false); return; }
            var document = GetDocumentWithPlayback();
            var revision = saveRevisions.AddOrUpdate(id, 1, (_, current) => current + 1);
            var previous = pendingAutoSave;
            pendingAutoSave = Task.Run(async () => {
                try { await previous.ConfigureAwait(false); } catch { }
                lock (libraryStore.SyncRoot)
                {
                    if (saveRevisions.GetValueOrDefault(id) != revision || libraryStore.TryGetItem(id) is null) return;
                    libraryStore.SaveMacro(id, document);
                }
            });
            await pendingAutoSave;
            if (shutdownStarted || saveRevisions.GetValueOrDefault(id) != revision) return;
            if (activeEditorMacroId == id) LibraryPanel.RefreshList();
            RestartListeningWithCurrentSettings();
        }
        catch (Exception ex) { AppLog.Error("AutoSave", "Save failed", ex); SetErrorStatus(ex); }
    }

    private void OnStepSelectionChanged(int stepIndex)
    {
        editorState.SelectedStepIndex = stepIndex;
    }

    private void OnOpenReferencedMacroRequested(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        var snapshot = libraryStore.Load();
        var preferredGroupId = snapshot.Items.FirstOrDefault(item =>
            string.Equals(item.Id, editorState.SelectedMacroId ?? activeEditorMacroId, StringComparison.OrdinalIgnoreCase))?.GroupId;
        var target = MacroLibraryResolver.Resolve(snapshot.Items, reference, preferredGroupId);
        if (target is null)
        {
            SetStatus(LocalizationService.Format("SubmacroNotFound", reference.Trim()));
            return;
        }

        var currentId = editorState.SelectedMacroId ?? activeEditorMacroId;
        if (currentId is not null
            && string.Equals(currentId, target.Id, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(L("AlreadyEditingMacro"));
            return;
        }

        if (!TrySaveActiveEditorForNavigation(target.Id))
        {
            return;
        }

        if (currentId is not null)
        {
            macroNavigationStack.Push(currentId);
        }

        suppressMacroNavigationStackClear = true;
        try
        {
            LoadMacroFromLibrary(target.Id);
            LibraryPanel.RefreshList();
        }
        finally
        {
            suppressMacroNavigationStackClear = false;
        }

        RefreshMacroNavigationUi();
    }

    private void OnReturnPreviousMacroRequested()
    {
        if (macroNavigationStack.Count == 0)
        {
            RefreshMacroNavigationUi();
            return;
        }

        var previousId = macroNavigationStack.Pop();
        var previous = libraryStore.Load().Items.FirstOrDefault(item =>
            string.Equals(item.Id, previousId, StringComparison.OrdinalIgnoreCase));
        if (previous is null)
        {
            ClearMacroNavigationStack();
            RefreshMacroNavigationUi();
            SetStatus(L("ReturnMacroMissing"));
            return;
        }

        if (!TrySaveActiveEditorForNavigation(previous.Id))
        {
            macroNavigationStack.Push(previousId);
            RefreshMacroNavigationUi();
            return;
        }

        suppressMacroNavigationStackClear = true;
        try
        {
            LoadMacroFromLibrary(previous.Id);
            LibraryPanel.RefreshList();
        }
        finally
        {
            suppressMacroNavigationStackClear = false;
        }

        RefreshMacroNavigationUi();
    }

    private bool TrySaveActiveEditorForNavigation(string? nextMacroId)
    {
        if (activeEditorMacroId is not { } activeId
            || string.Equals(activeId, nextMacroId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        autoSaveTimer.Stop();
        var activeItem = libraryStore.Load().Items.FirstOrDefault(item =>
            string.Equals(item.Id, activeId, StringComparison.OrdinalIgnoreCase));
        if (activeItem is null || activeItem.IsLocked)
        {
            return true;
        }

        try
        {
            SaveCurrentRevision(activeId, GetDocumentWithPlayback());
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
            return false;
        }
    }

    private void ClearMacroNavigationStackUnlessSuppressed()
    {
        if (!suppressMacroNavigationStackClear)
        {
            ClearMacroNavigationStack();
        }
    }

    private void ClearMacroNavigationStack()
    {
        macroNavigationStack.Clear();
        RefreshMacroNavigationUi();
    }

    private void RefreshMacroNavigationUi()
    {
        SequencePanelControl.SetCanReturnPreviousMacro(macroNavigationStack.Count > 0);
    }

    private void SaveActiveEditorBeforeSwitch(string? nextMacroId)
    {
        if (activeEditorMacroId is not { } activeId
            || string.Equals(activeId, nextMacroId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        autoSaveTimer.Stop();
        var activeItem = libraryStore.Load().Items.FirstOrDefault(item => string.Equals(item.Id, activeId, StringComparison.OrdinalIgnoreCase));
        if (activeItem is null || activeItem.IsLocked)
        {
            return;
        }

        try
        {
            SaveCurrentRevision(activeId, GetDocumentWithPlayback());
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
        }
    }

    private void OnEditLockChanged(bool isLocked)
    {
        var macroId = activeEditorMacroId ?? editorState.SelectedMacroId;
        if (macroId is null)
        {
            ApplyEditLock(false);
            SetStatus(L("SelectMacroToLock"));
            return;
        }

        try
        {
            if (isLocked)
            {
                autoSaveTimer.Stop();
                AutoSaveCurrentMacro(updateStatus: false);
            }

            var item = libraryStore.SetMacroLocked(macroId, isLocked);
            editorState.ReloadLibrary();
            ApplyEditLock(item.IsLocked);
            LibraryPanel.RefreshList();
            SetStatus(item.IsLocked ? L("MacroLocked") : L("MacroUnlocked"));
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            var persisted = libraryStore.Load().Items.FirstOrDefault(item => string.Equals(item.Id, macroId, StringComparison.OrdinalIgnoreCase));
            ApplyEditLock(persisted?.IsLocked == true);
            SetErrorStatus(ex);
        }
    }

    private void ApplyEditLock(bool isLocked)
    {
        editorState.SetEditLocked(isLocked);
        SequencePanelControl.SetReadOnly(isLocked);
        ConditionPanel.SetReadOnly(isLocked);
        JsonPanel.SetReadOnly(isLocked);
        ActionPalette.SetReadOnly(isLocked);
        PlaybackPanelControl.SetReadOnly(isLocked);
        OpenButton.IsEnabled = !isLocked;
    }

    private bool RejectLockedEdit()
    {
        if (!editorState.IsEditLocked)
        {
            return false;
        }

        SetStatus(L("MacroLockedReadOnly"));
        return true;
    }

    private void OnActionPaletteClicked(MacroActionTemplateKind kind)
    {
        if (RejectLockedEdit()) return;
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
        if (RejectLockedEdit()) return;
        if (!actionTemplateInsertGate.TryAccept(kind)) return;
        SequencePanelControl.InsertStepsAtPath(MacroActionTemplateFactory.CreateSteps(kind), parentPathText, insertIndex);
        RefreshConditionStepChoices();
    }

    private void OnMacroLibraryDropped(string macroId, string parentPathText, int insertIndex)
    {
        if (RejectLockedEdit()) return;
        try
        {
            var item = libraryStore.TryGetItem(macroId);
            var document = libraryStore.ReadMacro(macroId);
            if (item?.IsConditionMacro == true || document.IsConditionMacro)
            {
                InsertConditionMacroPack(document);
                return;
            }

            SequencePanelControl.InsertStepsAtPath(
                [new MacroCallStep(document.Name)],
                parentPathText,
                insertIndex);
            RefreshConditionStepChoices();
            SetStatus(LocalizationService.Format("InsertedMacroCall", document.Name));
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
        }
    }

    private void OnConditionListMacroLibraryDropped(string macroId)
    {
        if (RejectLockedEdit()) return;
        try
        {
            var item = libraryStore.TryGetItem(macroId);
            var document = libraryStore.ReadMacro(macroId);
            if (item?.IsConditionMacro == true || document.IsConditionMacro)
            {
                InsertConditionMacroPack(document);
                return;
            }

            SetStatus(LocalizationService.Get("NormalMacroDropOnConditionsHint"));
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
        }
    }

    private void OnInsertSelectedLibraryConditionMacro()
    {
        if (RejectLockedEdit()) return;

        var candidates = LibraryPanel.ListConditionMacrosInCurrentDatabase();
        if (candidates.Count == 0)
        {
            SetStatus(L("NoConditionMacrosInDatabase"));
            return;
        }

        var dialog = new ConditionMacroPickerDialog(candidates);
        if (DialogOwnerService.ShowDialogSafe(dialog, this) != true
            || string.IsNullOrWhiteSpace(dialog.SelectedMacroId))
        {
            return;
        }

        OnConditionListMacroLibraryDropped(dialog.SelectedMacroId);
    }

    private void InsertConditionMacroPack(MacroDocument document)
    {
        var pack = document.EffectiveConditions;
        if (pack.Count == 0)
        {
            SetStatus(LocalizationService.Format("InsertedConditionPack", document.Name, 0));
            return;
        }

        ConditionPanel.InsertConditionPack(pack, document.Name);
        RefreshConditionStepChoices();
        AutoSaveCurrentMacro(updateStatus: false);
        SetStatus(LocalizationService.Format("InsertedConditionPack", document.Name, pack.Count));
    }

    private async void OnStartListeningGroups(IReadOnlyList<string> groupIds)
    {
        if (BlockListeningWhilePaused())
        {
            return;
        }

        try
        {
            AutoSaveCurrentMacro(updateStatus: false);
            var selectedCandidates = BuildListeningCandidatesForGroups(groupIds);
            if (selectedCandidates.Count == 0)
            {
                throw new InvalidOperationException(L("ChooseTriggerBeforeListening"));
            }

            var desiredIds = listeningControllers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in selectedCandidates)
            {
                desiredIds.Add(candidate.Item.Id);
            }

            var candidates = BuildListeningCandidates()
                .Where(candidate => desiredIds.Contains(candidate.Item.Id))
                .ToList();
            var selectedIds = selectedCandidates.Select(candidate => candidate.Item.Id).ToArray();
            var count = await StartListeningCandidates(candidates, selectedIds);
            PlaybackPanelControl.SetPlaybackStatus($"{L("PlaybackStatusListening")} ({count})");
            SetStatus($"{L("Listening")} ({count})");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusError"));
            PlaybackPanelControl.SetPlaybackResult(ex.Message);
            SetStatus(L("PlaybackError"));
            RefreshLibraryListeningState();
        }
    }

    private void OnStopListeningGroups(IReadOnlyList<string> groupIds)
    {
        var stopped = StopListeningGroups(groupIds);
        PlaybackPanelControl.SetPlaybackStatus(listening ? $"{L("PlaybackStatusListening")} ({listeningControllers.Count})" : L("PlaybackStatusIdle"));
        PlaybackPanelControl.SetPlaybackResult(L("HotkeyListenerStopped"));
        SetStatus(listening ? $"{L("Listening")} ({listeningControllers.Count})" : L("Idle"));
        if (stopped == 0)
        {
            RefreshLibraryListeningState();
        }
    }

    private async void OnStartCurrentListening()
    {
        if (BlockListeningWhilePaused())
        {
            return;
        }

        try
        {
            AutoSaveCurrentMacro(updateStatus: false);
            var current = BuildCurrentListeningCandidate();
            var desiredIds = listeningControllers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            desiredIds.Add(current.Item.Id);

            var candidates = BuildListeningCandidates()
                .Where(candidate => desiredIds.Contains(candidate.Item.Id))
                .ToList();
            if (!candidates.Any(candidate => string.Equals(candidate.Item.Id, current.Item.Id, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(current);
            }

            var count = await StartListeningCandidates(candidates, [current.Item.Id]);
            PlaybackPanelControl.SetPlaybackStatus($"{L("PlaybackStatusListening")} ({count})");
            SetStatus($"{L("Listening")} ({count})");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusError"));
            PlaybackPanelControl.SetPlaybackResult(ex.Message);
            SetStatus(L("PlaybackError"));
            RefreshLibraryListeningState();
        }
    }

    private void OnStopListeningAll()
    {
        listening = false;
        StopListeningControllers();
        playbackController?.Stop();
        if (listeningPaused)
        {
            pausedListeningIds.Clear();
        }

        RefreshKeyboardHook();
        RefreshLibraryListeningState();
        PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusIdle"));
        PlaybackPanelControl.SetPlaybackResult(L("HotkeyListenerStopped"));
        SetStatus(L("Idle"));
    }

    private void PauseListeningButton_Click(object sender, RoutedEventArgs e)
    {
        if (listeningPaused)
        {
            ResumePausedListening();
            return;
        }

        PauseListening(userRequested: true);
    }

    private void PauseListening(bool userRequested)
    {
        if (!listeningPaused)
        {
            pausedListeningIds.Clear();
            pausedListeningIds.AddRange(listeningControllers.Keys);
            OnStopPlayback();
            listening = false;
            StopListeningControllers();
            RefreshKeyboardHook();
            RefreshLibraryListeningState();
            PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusIdle"));
            PlaybackPanelControl.SetPlaybackResult(L("HotkeyListenerStopped"));
        }

        listeningPaused = true;
        if (userRequested)
        {
            pauseOwnedByCapture = false;
        }

        UpdatePauseListeningButton();
        SetStatus(L("ListeningPausedStatus"));
    }

    private async void ResumePausedListening()
    {
        listeningPaused = false;
        pauseOwnedByCapture = false;
        coreHotkeysSuspended = false;
        UpdatePauseListeningButton();
        var ids = pausedListeningIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        pausedListeningIds.Clear();
        if (ids.Count == 0)
        {
            RefreshKeyboardHook();
            SetStatus(L("Idle"));
            return;
        }

        try
        {
            var candidates = BuildListeningCandidates()
                .Where(candidate => ids.Contains(candidate.Item.Id))
                .ToList();
            if (candidates.Count == 0)
            {
                RefreshKeyboardHook();
                SetStatus(L("Idle"));
                return;
            }

            var count = await StartListeningCandidates(candidates, ids);
            PlaybackPanelControl.SetPlaybackStatus($"{L("PlaybackStatusListening")} ({count})");
            SetStatus(LocalizationService.Format("ListeningResumePartial", count));
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
        }
    }

    private void PauseListeningForCapture()
    {
        coreHotkeysSuspended = true;
        if (listeningPaused)
        {
            RefreshKeyboardHook();
            return;
        }

        PauseListening(userRequested: false);
        pauseOwnedByCapture = true;
    }

    private void ResumeListeningAfterCapture()
    {
        coreHotkeysSuspended = false;
        if (!listeningPaused || !pauseOwnedByCapture)
        {
            RefreshKeyboardHook();
            return;
        }

        ResumePausedListening();
    }

    private bool BlockListeningWhilePaused()
    {
        if (!listeningPaused)
        {
            return false;
        }

        SetStatus(L("ListeningPausedHint"));
        return true;
    }

    private void UpdatePauseListeningButton()
    {
        PauseListeningButton.Content = L(listeningPaused ? "ResumeListening" : "PauseListening");
        PauseListeningButton.Style = (Style)FindResource(
            listeningPaused ? "DangerButton" : "CommandBarButton");
    }

    private void OnStopCurrentListening()
    {
        if (editorState.SelectedMacroId is not { } id)
        {
            return;
        }

        StopListeningController(id);
        RestartKeyboardHookFromListeningControllers();
        RefreshLibraryListeningState();
        PlaybackPanelControl.SetPlaybackStatus(listening ? L("PlaybackStatusListening") : L("PlaybackStatusIdle"));
        PlaybackPanelControl.SetPlaybackResult(L("HotkeyListenerStopped"));
        SetStatus(listening ? L("Listening") : L("Idle"));
    }

    private int StopListeningGroups(IReadOnlyList<string> groupIds)
    {
        var groupSet = groupIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (groupSet.Count == 0)
        {
            return 0;
        }

        var snapshot = libraryStore.Load();
        var macroIds = snapshot.Items
            .Where(item => groupSet.Contains(item.GroupId))
            .Select(item => item.Id)
            .Where(id => listeningControllers.ContainsKey(id))
            .ToList();
        foreach (var id in macroIds)
        {
            StopListeningController(id);
        }

        RestartKeyboardHookFromListeningControllers();
        RefreshLibraryListeningState();
        return macroIds.Count;
    }

    private async Task<int> StartListeningCandidates(
        IReadOnlyList<ListeningCandidate> candidates,
        IReadOnlyCollection<string>? preferredIds = null)
    {
        if (shutdownStarted) return 0;
        using var preparation = TrackPreparation();
        var colorSample = runtimePrecisionSettings.ColorSampleGesture;
        var forceStop = runtimePrecisionSettings.ForceStopGesture;
        var colorConflicts = candidates
            .Where(candidate => CoreHotkeys.GesturesEqual(candidate.Trigger, colorSample))
            .Select(candidate => candidate.Document.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var stopConflicts = forceStop is null
            ? []
            : candidates
                .Where(candidate => CoreHotkeys.GesturesEqual(candidate.Trigger, forceStop))
                .Select(candidate => candidate.Document.Name)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        var eligible = candidates
            .Where(candidate => !CoreHotkeys.GesturesEqual(candidate.Trigger, colorSample)
                && (forceStop is null || !CoreHotkeys.GesturesEqual(candidate.Trigger, forceStop)))
            .ToList();

        var resolved = ListeningTriggerResolver.Resolve(
            eligible,
            candidate => candidate.Item.Id,
            (left, right) => string.Equals(left.Trigger.ToString(), right.Trigger.ToString(), StringComparison.OrdinalIgnoreCase)
                && ProcessFiltersOverlap(left, right),
            preferredIds);

        var revision = ++listeningUpdateGeneration;
        var optionsById = resolved.ToDictionary(c => c.Item.Id, c => CreatePlaybackOptions(c.Document));
        var known = new Dictionary<string, string>(listeningFingerprints);
        var prepared = await Task.Run(() => PrepareListeningDefinitions(resolved, optionsById, known));
        if (revision != listeningUpdateGeneration || shutdownStarted)
        {
            foreach (var update in prepared.Values) update.Executor.Dispose();
            return listeningControllers.Count;
        }
        var conflictMessage = FormatReservedHotkeyConflict(colorConflicts, stopConflicts);
        if (resolved.Count == 0) throw new InvalidOperationException(L("ChooseTriggerBeforeListening"));
        var desired = resolved.Select(c => c.Item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in listeningControllers.Keys.Where(id => !desired.Contains(id)).ToArray()) StopListeningController(id);
        foreach (var candidate in resolved)
        {
            string id = candidate.Item.Id;
            if (prepared.TryGetValue(id, out var update))
            {
                if (listeningControllers.TryGetValue(id, out var existing))
                    existing.UpdateDefinition(candidate.Document, update.Executor, optionsById[id]);
                else listeningControllers[id] = new(candidate.Document, update.Executor, optionsById[id]);
                listeningFingerprints[id] = update.Fingerprint;
            }
            listeningMacroNames[id] = candidate.Document.Name;
            listeningGroupProcessFilters[id] = candidate.GroupProcessFilter;
            listeningBindings[id] = candidate.Trigger;
        }
        listening = listeningControllers.Count > 0;
        RefreshKeyboardHook();
        RefreshLibraryListeningState();
        if (!string.IsNullOrEmpty(conflictMessage))
        {
            SetStatus(conflictMessage);
        }

        return listeningControllers.Count;
    }

    private void StopListeningControllers()
    {
        listeningUpdateGeneration++;
        listeningBindings.Clear();
        listeningFingerprints.Clear();
        foreach (var controller in listeningControllers.Values)
        {
            controller.Stop();
            RetireController(controller);
        }

        listeningControllers.Clear();
        listeningMacroNames.Clear();
        listeningGroupProcessFilters.Clear();
        activeTriggeredControllerIds.Clear();
    }

    private void StopListeningController(string id)
    {
        listeningUpdateGeneration++;
        listeningBindings.Remove(id);
        listeningFingerprints.Remove(id);
        if (!listeningControllers.Remove(id, out var controller))
        {
            return;
        }

        controller.Stop();
        RetireController(controller);
        listeningMacroNames.Remove(id);
        listeningGroupProcessFilters.Remove(id);
        activeTriggeredControllerIds.Remove(id);
        if (ReferenceEquals(playbackController, controller))
        {
            playbackController = null;
        }
    }

    private void RestartKeyboardHookFromListeningControllers()
    {
        listening = listeningControllers.Count > 0;
        RefreshKeyboardHook();
    }

    private void RefreshKeyboardHook()
    {
        var bindings = new List<HotkeyBinding>();
        if (runNowEmergencyStopArmed) bindings.Add(new(CoreHotkeys.EmergencyStopId, new(HidModifier.None, HidKey.Escape)));
        if (!coreHotkeysSuspended && macroRecorder is null)
        {
            bindings.Add(new(CoreHotkeys.ColorSampleId, runtimePrecisionSettings.ColorSampleGesture));
            if (runtimePrecisionSettings.ForceStopGesture is { } stop) bindings.Add(new(CoreHotkeys.ForceStopId, stop));
        }
        foreach (var pair in listeningBindings) bindings.Add(new(pair.Key, pair.Value));
        Volatile.Write(ref hotkeyRoutes, listeningControllers.ToDictionary(pair => pair.Key,
            pair => new HotkeyRoute(pair.Value, listeningGroupProcessFilters.GetValueOrDefault(pair.Key, ""),
                listeningMacroNames.GetValueOrDefault(pair.Key, ""), runtimePrecisionSettings.BackgroundWindow)));
        listening = listeningControllers.Count > 0;
        UpdatePrecisionWarmLease();
        if (bindings.Count == 0) { keyboardHook?.Stop(); return; }
        if (keyboardHook is not null && keyboardHook.Mode != runtimePrecisionSettings.HotkeyInput)
        {
            // Detection mode changed: restart so the new front end takes over (held-key state is rebuilt from scratch).
            keyboardHook.Stop();
            keyboardHook.Mode = runtimePrecisionSettings.HotkeyInput;
        }
        if (keyboardHook is null)
        {
            keyboardHook = new GlobalKeyboardHook { Mode = runtimePrecisionSettings.HotkeyInput };
            keyboardHook.TriggerPressed += KeyboardHook_TriggerPressed;
            keyboardHook.TriggerReleased += KeyboardHook_TriggerReleased;
        }
        keyboardHook.Start(bindings);
    }

    private List<ListeningCandidate> BuildListeningCandidates()
    {
        var candidates = new List<ListeningCandidate>();
        var snapshot = libraryStore.Load();
        var groups = snapshot.Groups.ToDictionary(group => group.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot.Items)
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

            if (item.IsConditionMacro || document.IsConditionMacro)
            {
                continue;
            }

            candidates.Add(new ListeningCandidate(
                item,
                document,
                trigger,
                groups.TryGetValue(item.GroupId, out var group) ? group.ProcessFilter : string.Empty));
        }

        return candidates;
    }

    private List<ListeningCandidate> BuildListeningCandidatesForGroups(IReadOnlyList<string> groupIds)
    {
        var groupSet = groupIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (groupSet.Count == 0)
        {
            return [];
        }

        return BuildListeningCandidates()
            .Where(candidate => groupSet.Contains(candidate.Item.GroupId))
            .ToList();
    }

    private ListeningCandidate BuildCurrentListeningCandidate()
    {
        if (editorState.SelectedMacroId is not { } id)
        {
            throw new InvalidOperationException(L("ChooseTriggerBeforeListening"));
        }

        var snapshot = libraryStore.Load();
        var item = snapshot.Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(L("ChooseTriggerBeforeListening"));
        var document = libraryStore.ReadMacro(item.Id);
        if (document.Playback.Trigger is not { } trigger)
        {
            throw new InvalidOperationException(L("ChooseTriggerBeforeListening"));
        }

        return new ListeningCandidate(
            item,
            document,
            trigger,
            snapshot.Groups.FirstOrDefault(group => string.Equals(group.Id, item.GroupId, StringComparison.OrdinalIgnoreCase))?.ProcessFilter ?? string.Empty);
    }

    private IReadOnlyList<(ListeningCandidate Left, ListeningCandidate Right)> FindListeningConflicts(
        IReadOnlyList<ListeningCandidate> candidates)
    {
        var conflicts = new List<(ListeningCandidate Left, ListeningCandidate Right)>();
        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i + 1; j < candidates.Count; j++)
            {
                var left = candidates[i];
                var right = candidates[j];
                if (string.Equals(left.Trigger.ToString(), right.Trigger.ToString(), StringComparison.OrdinalIgnoreCase)
                    && ProcessFiltersOverlap(left, right))
                {
                    conflicts.Add((left, right));
                }
            }
        }

        return conflicts;
    }

    private static bool ProcessFiltersOverlap(ListeningCandidate left, ListeningCandidate right)
    {
        return MacroLibraryActivationFilter.FiltersOverlap(
            left.GroupProcessFilter,
            right.GroupProcessFilter);
    }

    private void RefreshLibraryListeningState()
    {
        var candidates = BuildListeningCandidates();
        var listeningCandidates = candidates
            .Where(candidate => listeningControllers.ContainsKey(candidate.Item.Id))
            .ToList();
        var conflicts = FindListeningConflicts(listeningCandidates);
        var conflictIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (left, right) in conflicts)
        {
            conflictIds.Add(left.Item.Id);
            conflictIds.Add(right.Item.Id);
        }

        var colorSample = runtimePrecisionSettings.ColorSampleGesture;
        var forceStop = runtimePrecisionSettings.ForceStopGesture;
        foreach (var candidate in candidates)
        {
            if (CoreHotkeys.GesturesEqual(candidate.Trigger, colorSample)
                || (forceStop is not null && CoreHotkeys.GesturesEqual(candidate.Trigger, forceStop)))
            {
                conflictIds.Add(candidate.Item.Id);
            }
        }

        var states = new Dictionary<string, MacroLibraryListenState>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            states[candidate.Item.Id] = new MacroLibraryListenState(
                listeningControllers.ContainsKey(candidate.Item.Id),
                conflictIds.Contains(candidate.Item.Id),
                candidate.Trigger.ToString(),
                FormatPlaybackModeSummary(candidate.Document.Playback),
                string.IsNullOrWhiteSpace(candidate.EffectiveProcessFilter) ? L("AllProcesses") : candidate.EffectiveProcessFilter);
        }

        LibraryPanel.SetListeningStates(states);
    }

    private static string FormatPlaybackModeSummary(PlaybackSettings playback) => playback.Mode switch
    {
        PlaybackMode.ToggleLoop => LocalizationService.Get("ModeToggleLoop"),
        PlaybackMode.HoldLoop => LocalizationService.Get("ModeHoldLoop"),
        PlaybackMode.FixedCount => LocalizationService.Format("ModeFixedCountSummary", playback.Count),
        _ => playback.Mode.ToString()
    };

    private static string FormatListeningConflictMessage(
        IReadOnlyList<(ListeningCandidate Left, ListeningCandidate Right)> conflicts)
    {
        var lines = conflicts
            .Take(8)
            .Select(pair => $"{pair.Left.Document.Name} / {pair.Right.Document.Name}: {pair.Left.Trigger}");
        return $"{LocalizationService.Get("ListeningConflict")}\n{string.Join("\n", lines)}";
    }

    private void CopyColorSample()
    {
        if (!GetCursorPos(out var point) || !Controls.ScreenPixelSampler.TryReadPixel(point.x, point.y, out var color))
        {
            return;
        }

        var text = ColorSampleText.Format(point.x, point.y, color);
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            return;
        }

        colorSampleOverlay.ShowSample(text);
        SetStatus($"{L("ColorSampleCopied")}  {text}");
    }

    private void WarnColorSampleConflicts()
    {
        var colorSample = runtimePrecisionSettings.ColorSampleGesture;
        var forceStop = runtimePrecisionSettings.ForceStopGesture;
        var candidates = BuildListeningCandidates();
        var colorNames = candidates
            .Where(candidate => CoreHotkeys.GesturesEqual(candidate.Trigger, colorSample))
            .Select(candidate => candidate.Document.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var stopNames = forceStop is null
            ? []
            : candidates
                .Where(candidate => CoreHotkeys.GesturesEqual(candidate.Trigger, forceStop))
                .Select(candidate => candidate.Document.Name)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        if (colorNames.Count == 0 && stopNames.Count == 0)
        {
            return;
        }

        SetStatus(FormatReservedHotkeyConflict(colorNames, stopNames));
        RefreshLibraryListeningState();
    }

    private static string FormatReservedHotkeyConflict(IReadOnlyList<string> colorNames, IReadOnlyList<string> stopNames)
    {
        var message = colorNames.Count > 0
            ? LocalizationService.Format("ListeningColorSampleConflict", string.Join("、", colorNames))
            : string.Empty;
        if (stopNames.Count == 0)
        {
            return message;
        }

        var stopMessage = LocalizationService.Format("ListeningForceStopConflict", string.Join("、", stopNames));
        return string.IsNullOrEmpty(message) ? stopMessage : message + " " + stopMessage;
    }

    private void OnBasicControlRequested()
    {
        var dialog = new MacroBasicControlDialog(runtimePrecisionSettings, LibraryPanel.BackgroundProcessChoiceRequired);
        dialog.CaptureStarted += PauseListeningForCapture;
        dialog.CaptureFinished += ResumeListeningAfterCapture;
        bool accepted;
        try
        {
            accepted = DialogOwnerService.ShowDialogSafe(dialog, this) == true;
        }
        finally
        {
            dialog.CaptureStarted -= PauseListeningForCapture;
            dialog.CaptureFinished -= ResumeListeningAfterCapture;
        }

        if (!accepted)
        {
            return;
        }

        LibraryPanel.SetRuntimePrecisionControls(dialog.Settings);
        OnPrecisionSettingsEdited();
    }

    private void OnPrecisionControlRequested()
    {
        string? pickedMask = null;
        var dialog = new PrecisionControlDialog(runtimePrecisionSettings, currentMask =>
        {
            var selected = TryPickAffinityMask(currentMask);
            if (selected is not null)
            {
                pickedMask = selected;
            }

            return selected;
        });
        if (DialogOwnerService.ShowDialogSafe(dialog, this) != true)
        {
            if (pickedMask is not null
                && !string.Equals(pickedMask, runtimePrecisionSettings.AffinityMask, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(runtimePrecisionSettings.AffinityMask))
                {
                    NativePlaybackWarmup.QueueWarmUpForPrecision(runtimePrecisionSettings.Precision, "", runtimePrecisionSettings.Generation);
                }
                else
                {
                    NativePlaybackWarmup.QueueWarmUpForAffinityMask(runtimePrecisionSettings.AffinityMask, force: true);
                }
            }

            return;
        }

        LibraryPanel.SetRuntimePrecisionControls(dialog.Settings);
        OnPrecisionSettingsEdited();
    }

    private string? TryPickAffinityMask(string currentMask)
    {
        if (IsCoreSelectionBlocked())
        {
            SetStatus(L("CoreSelectionBusy"));
            return null;
        }

        var dialog = new CorePickerDialog(currentMask, IsCoreSelectionBlocked);
        if (DialogOwnerService.ShowDialogSafe(dialog, this) != true)
        {
            RestoreMaskAfterCancelledTest(currentMask, dialog);
            return null;
        }

        if (IsCoreSelectionBlocked())
        {
            SetStatus(L("CoreSelectionBusy"));
            RestoreMaskAfterCancelledTest(currentMask, dialog);
            return null;
        }

        var alreadyMeasured = dialog.LastMeasureSucceeded
            && string.Equals(dialog.LastMeasuredMask, dialog.SelectedMask, StringComparison.OrdinalIgnoreCase);
        if (!alreadyMeasured)
        {
            NativePlaybackWarmup.QueueWarmUpForAffinityMask(dialog.SelectedMask, force: true);
        }

        SetStatus(string.IsNullOrWhiteSpace(dialog.LastResultText) ? L("CoreSelectionStarted") : dialog.LastResultText);
        return dialog.SelectedMask;
    }

    private void SetLibraryPathForItem(MacroLibraryItem? item, string fallbackName)
    {
        if (item is null)
        {
            SequencePanelControl.SetLibraryPath(fallbackName);
            return;
        }

        var groupName = libraryStore.Load().Groups
            .FirstOrDefault(group => string.Equals(group.Id, item.GroupId, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? item.GroupId;
        SequencePanelControl.SetLibraryPath(MacroLibraryPath.Format(groupName, item.Folder, item.Name));
    }

    private void OnLibraryPathNavigate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!LibraryPanel.TryReveal(path, out var item))
        {
            SetStatus(L("LibraryPathNotFound"));
            return;
        }

        if (item is not null && !item.IsConditionMacro)
        {
            if (!string.Equals(item.Id, activeEditorMacroId, StringComparison.OrdinalIgnoreCase))
            {
                OnMacroSelected(item.Id);
                return;
            }
        }

        SetStatus(path);
    }

    private async void OnRunNow()
    {
        if (shutdownStarted || preparingImmediate) return;
        if (immediateController is { Status: PlaybackStatus.Running or PlaybackStatus.Stopping })
        { SetStatus(L("PlaybackStatusStopping")); return; }
        using var preparation = TrackPreparation();
        preparingImmediate = true;
        MacroPlaybackExecutor? prepared = null;
        long epoch = Interlocked.Read(ref stopEpoch);
        try
        {
            if (macroRecorder is not null) StopMacroRecording(insertSteps: true, restoreWindow: true);
            var document = GetDocumentWithPlayback();
            var id = activeEditorMacroId;
            var options = CreatePlaybackOptions(document).ForImmediateRun();
            runNowEmergencyStopArmed = true;
            RefreshKeyboardHook();
            if (immediateController is not null) await immediateController.DisposeAsync();
            prepared = await Task.Run(() => {
                var frozen = CaptureResolver(document, id);
                var executor = new MacroPlaybackExecutor(inputSink, macroResolver: frozen.Resolver);
                try { executor.Prepare(document, options); return executor; }
                catch { executor.Dispose(); throw; }
            });
            if (shutdownStarted || epoch != Interlocked.Read(ref stopEpoch)) { DisarmRunNowEmergencyStop(); return; }
            lock (triggerGate)
            {
                if (shutdownStarted || epoch != Interlocked.Read(ref stopEpoch)) return;
                playbackController = immediateController = new(document, prepared, options);
                prepared = null;
                _ = playbackController.RunNowAsync();
            }
            PlaybackPanelControl.SetPlaybackResult(L("RunNowOnceHint"));
            UpdatePlaybackStatus();
            WatchPlayback(playbackController);
        }
        catch (Exception ex)
        {
            AppLog.Error("Playback", "Start failed", ex);
            DisarmRunNowEmergencyStop();
            PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusError"));
            PlaybackPanelControl.SetPlaybackResult(ex.Message);
        }
        finally { prepared?.Dispose(); preparingImmediate = false; }
    }

    private void DisarmRunNowEmergencyStop()
    {
        if (!runNowEmergencyStopArmed)
        {
            return;
        }

        runNowEmergencyStopArmed = false;
        RefreshKeyboardHook();
    }

    private PlaybackExecutionOptions CreatePlaybackOptions(MacroDocument document)
    {
        return new PlaybackExecutionOptions(
            document.Playback.Mode,
            document.Playback.Count,
            PixelEvaluationMode.Live,
            NoWait: false,
            runtimePrecisionSettings.Precision,
            runtimePrecisionSettings.AffinityMask,
            runtimePrecisionSettings.BackgroundWindow,
            ResolveBackgroundProcess(document))
        {
            PressFloor = runtimePrecisionSettings.PressFloor,
            StartLeadMicroseconds = runtimePrecisionSettings.StartLeadMicroseconds,
            CompensateSendInputCost = runtimePrecisionSettings.CompensateSendInputCost
                && runtimePrecisionSettings.Generation == PrecisionGeneration.Latest
        };
    }

    private string ResolveBackgroundProcess(MacroDocument document)
    {
        var snapshot = libraryStore.Load();
        var item = !string.IsNullOrWhiteSpace(document.Id)
            ? snapshot.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, document.Id, StringComparison.OrdinalIgnoreCase))
            : null;
        item ??= !string.IsNullOrWhiteSpace(editorState.SelectedMacroId)
            ? snapshot.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, editorState.SelectedMacroId, StringComparison.OrdinalIgnoreCase))
            : null;
        var group = item is null
            ? null
            : snapshot.Groups.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, item.GroupId, StringComparison.OrdinalIgnoreCase));
        var databaseProcess = group is { IsGlobal: false } ? group.ProcessFilter : string.Empty;
        return BackgroundProcessSelection.Resolve(runtimePrecisionSettings.BackgroundProcess, databaseProcess);
    }

    private void OnStopPlayback()
    {
        RequestAllStops();
        DisarmRunNowEmergencyStop();

        PlaybackInputSafetyRegistry.ReleaseAllActiveInputs();

        UpdatePlaybackStatus();
        if (playbackController is not null)
            WatchPlayback(playbackController);
    }

    private void OnImportApplied(MacroDocument document)
    {
        ClearMacroNavigationStack();
        SaveActiveEditorBeforeSwitch(nextMacroId: null);
        var item = !string.IsNullOrWhiteSpace(document.Id)
            ? libraryStore.Load().Items.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, document.Id, StringComparison.OrdinalIgnoreCase))
            : null;
        item ??= libraryStore.CreateMacro(document, groupId: LibraryPanel.CurrentDatabaseGroupId);
        editorState.SelectedMacroId = item.Id;
        activeEditorMacroId = item.Id;
        LibraryPanel.RefreshList();
        RefreshLibraryListeningState();
        SequencePanelControl.SetEditorDocument(document);
        SequencePanelControl.ClearUndoHistory();
        PlaybackPanelControl.SetPlaybackControls(document.Playback);
        RefreshConditionStepChoices();
        ConditionPanel.LoadConditions(document.EffectiveConditions);
        SequencePanelControl.SetConditionHighlights(document.EffectiveConditions, i => ConditionPanel.GetConditionColor(i));
        ApplyEditLock(false);
        SetStatus(L("ConversionApplied"));
    }

    private void OnSequenceUndoApplied()
    {
        RefreshEditorAfterSequenceDocumentChange(L("Undo"));
    }

    private void OnStartRecording(MacroRecordingMode mode) => StartMacroRecording(targetsCondition: false, mode);

    private void OnStartConditionRecording(MacroRecordingMode mode) => StartMacroRecording(targetsCondition: true, mode);

    private void StartMacroRecording(bool targetsCondition, MacroRecordingMode mode)
    {
        if (macroRecorder is not null)
        {
            return;
        }

        if (SequencePanelControl.IsReadOnly)
        {
            SetStatus(L("RecordingLocked"));
            return;
        }

        if (targetsCondition && !ConditionPanel.CanRecordThenActions)
        {
            SetStatus(L("ConditionRecordingUnavailable"));
            return;
        }

        try
        {
            macroRecordingTargetsCondition = targetsCondition;
            OnStopPlayback();

            // Recording input must not also activate macros that are listening.
            coreHotkeysSuspended = true;
            keyboardHook?.Dispose();
            keyboardHook = null;

            var options = MacroRecordingOptions.ForUserMode(mode);
            var recorder = new MacroInputRecorder(options);
            recorder.StopHotkeyPressed += MacroRecorder_StopHotkeyPressed;
            recorder.Start();
            macroRecorder = recorder;

            SetRecordingUiState(true);
            SetStatus(L("RecordingStarted"));
            windowStateBeforeRecording = WindowState;
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    taskbarMinimizeDepth++;
                    try
                    {
                        WindowState = WindowState.Minimized;
                    }
                    finally
                    {
                        taskbarMinimizeDepth--;
                    }
                }),
                DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            DisposeMacroRecorder();
            RestartKeyboardHookAfterRecording();
            SetRecordingUiState(false);
            macroRecordingTargetsCondition = false;
            SetStatus(LocalizationService.Format("RecordingFailed", ex.Message));
        }
    }

    private void OnStopRecording() => StopMacroRecording(insertSteps: true, restoreWindow: true);

    private void MacroRecorder_StopHotkeyPressed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(
            new Action(() => StopMacroRecording(insertSteps: true, restoreWindow: true)),
            DispatcherPriority.Send);
    }

    private void StopMacroRecording(bool insertSteps, bool restoreWindow)
    {
        var recorder = macroRecorder;
        if (recorder is null)
        {
            SetRecordingUiState(false);
            return;
        }

        var targetsCondition = macroRecordingTargetsCondition;
        IReadOnlyList<MacroStep> recordedSteps;
        var inputCount = recorder.InputCount;
        try
        {
            recordedSteps = recorder.Stop();
            inputCount = recorder.InputCount;
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            recordedSteps = [];
            SetStatus(LocalizationService.Format("RecordingFailed", ex.Message));
        }
        finally
        {
            DisposeMacroRecorder();
            RestartKeyboardHookAfterRecording();
            SetRecordingUiState(false);
        }

        if (insertSteps && recordedSteps.Count > 0)
        {
            var inserted = true;
            if (targetsCondition)
            {
                inserted = ConditionPanel.InsertRecordedThenSteps(recordedSteps);
            }
            else
            {
                SequencePanelControl.InsertSteps(recordedSteps);
            }

            SetStatus(inserted
                ? LocalizationService.Format("RecordingCompleted", inputCount, recordedSteps.Count)
                : L("ConditionRecordingUnavailable"));
        }
        else if (insertSteps)
        {
            SetStatus(L("RecordingEmpty"));
        }

        if (restoreWindow)
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = windowStateBeforeRecording == WindowState.Minimized
                    ? WindowState.Normal
                    : windowStateBeforeRecording;
            }

            Activate();
        }

        macroRecordingTargetsCondition = false;
    }

    private void SetRecordingUiState(bool recording)
    {
        SequencePanelControl.SetRecordingState(recording);
        ConditionPanel.SetRecordingState(recording);
    }

    private void DisposeMacroRecorder()
    {
        if (macroRecorder is null)
        {
            return;
        }

        macroRecorder.StopHotkeyPressed -= MacroRecorder_StopHotkeyPressed;
        macroRecorder.Dispose();
        macroRecorder = null;
    }

    private void RestartKeyboardHookAfterRecording()
    {
        try
        {
            coreHotkeysSuspended = false;
            RestartKeyboardHookFromListeningControllers();
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            keyboardHook?.Dispose();
            keyboardHook = null;
            listening = false;
            SetStatus(LocalizationService.Format("RecordingListenerRestoreFailed", ex.Message));
        }
    }

    private void OnSequenceRedoApplied()
    {
        RefreshEditorAfterSequenceDocumentChange(L("Redo"));
    }

    private void OnSequenceDocumentEdited()
    {
        if (editorState.IsEditLocked) return;
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
            ScheduleAutoSave();
            if (!string.IsNullOrWhiteSpace(status))
            {
                SetStatus(status);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
        }
    }

    private MacroDocument GetDocumentWithPlayback()
    {
        if (ConditionPanel.HasValidationErrors)
        {
            throw new InvalidOperationException(L("ConditionValidationFailed"));
        }

        ApplyPlaybackSettingsToEditor();
        var document = SequencePanelControl.GetCurrentDocument();
        var merged = document with { Conditions = ConditionPanel.Conditions.ToList() };
        if (!ConditionsMatch(document.EffectiveConditions, merged.EffectiveConditions))
        {
            SequencePanelControl.SetEditorDocument(merged);
        }

        return merged;
    }

    private static bool ConditionsMatch(IReadOnlyList<ConditionalDirective> first, IReadOnlyList<ConditionalDirective> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var i = 0; i < first.Count; i++)
        {
            if (!Equals(first[i], second[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyPlaybackSettingsToEditor()
    {
        var triggerText = PlaybackPanelControl.TriggerText;
        var trigger = string.IsNullOrWhiteSpace(triggerText) ? null : McrxParser.ParseHotkeyGesture(triggerText);
        var mode = PlaybackPanelControl.GetSelectedPlaybackMode();
        if (!int.TryParse(PlaybackPanelControl.CountText, out var count) || count < 1) count = 1;

        var document = SequencePanelControl.GetCurrentDocument();
        var settings = document.Playback with { Trigger = trigger, Mode = mode, Count = count };
        if (settings != document.Playback) SequencePanelControl.SetEditorDocument(document with { Playback = settings });
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

        var snapshot = libraryStore.Load();
        var preferredGroupId = snapshot.Items.FirstOrDefault(item =>
            string.Equals(item.Id, editorState.SelectedMacroId ?? activeEditorMacroId, StringComparison.OrdinalIgnoreCase))?.GroupId;
        var item = MacroLibraryResolver.Resolve(snapshot.Items, name, preferredGroupId);
        return item is null ? null : libraryStore.ReadMacro(item.Id);
    }

    private void KeyboardHook_TriggerPressed(object? sender, HotkeyTriggeredEventArgs e)
    {
        if (shutdownStarted) return;
        if (e.Id == CoreHotkeys.EmergencyStopId || e.Id == CoreHotkeys.ForceStopId)
        {
            RequestAllStops();
            Dispatcher.BeginInvoke(() => { PlaybackPanelControl.SetPlaybackResult(L("ForceStopTriggered")); UpdatePlaybackStatus(); });
            return;
        }
        if (e.Id == CoreHotkeys.ColorSampleId) { Dispatcher.BeginInvoke(CopyColorSample); return; }
        if (!Volatile.Read(ref hotkeyRoutes).TryGetValue(e.Id, out var route)) return;
        long epoch = Interlocked.Read(ref stopEpoch);
        if (!route.Background && !MacroLibraryActivationFilter.Matches(route.Filter, ForegroundProcessService.GetForegroundProcessName())) return;
        lock (triggerGate)
        {
            if (shutdownStarted || epoch != Interlocked.Read(ref stopEpoch)) return;
            route.Controller.TriggerPressedAsync(e.CallbackQpc);
            heldControllers[e.Id] = route.Controller;
        }
        long run = route.Controller.RunId;
        var completion = route.Controller.WhenIdleAsync();
        Dispatcher.BeginInvoke(() => {
            if (epoch != Interlocked.Read(ref stopEpoch) || run != route.Controller.RunId) return;
            playbackController = route.Controller;
            UpdatePlaybackStatus();
            SetStatus(route.Controller.Status == PlaybackStatus.Stopping ? L("PlaybackStatusStopping") : route.Name);
            ObservePlayback(route.Controller, run, completion);
        });
    }

    private void KeyboardHook_TriggerReleased(object? sender, HotkeyTriggeredEventArgs e)
    {
        if (heldControllers.TryRemove(e.Id, out var controller)) controller.TriggerReleased();
    }

    private void ObservePlayback(MacroPlaybackController controller, long run, Task<PlaybackRunResult> completion)
    {
        if (run == 0) return;
        var observed = observedRuns.GetValue(controller, _ => new());
        if (observed.Value == run) return;
        observed.Value = run;
        _ = WatchPlaybackAsync(controller, run, completion);
    }
    private void WatchPlayback(MacroPlaybackController controller)
        => ObservePlayback(controller, controller.RunId, controller.WhenIdleAsync());

    private async Task WatchPlaybackAsync(MacroPlaybackController controller, long run, Task<PlaybackRunResult> completion)
    {
        var result = await completion;
        if (shutdownStarted || run != controller.RunId || !ReferenceEquals(playbackController, controller)) return;
        if (ReferenceEquals(immediateController, controller)) DisarmRunNowEmergencyStop();
        if (result.Status == PlaybackRunStatus.Failed)
        {
            PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusError"));
            PlaybackPanelControl.SetPlaybackResult(controller.LastError?.Message ?? L("PlaybackError"));
            SetStatus(L("PlaybackError"));
        }
        else
        {
            UpdatePlaybackStatus();
            PlaybackPanelControl.SetPlaybackResult(LocalizationService.Format("LastResultRunSummary", result.IterationsCompleted, result.ActionsSubmitted, result.Cancelled));
            SetStatus(listening ? L("Listening") : L("Idle"));
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

    private enum StatusLevel { Info, Warning, Error }

    private void SetStatus(string text) => SetStatus(text, StatusLevel.Info);

    private void SetStatus(string text, StatusLevel level)
    {
        if (StatusText.Text != text) AppLog.Info("Status", text);
        StatusText.Text = text;
        StatusText.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, level switch
        {
            StatusLevel.Error => "Danger",
            StatusLevel.Warning => "AccentOrange",
            _ => "Accent"
        });
    }

    /// <summary>Shows a failure in the status pill: the message only (full exception goes to the log), marked as an error.</summary>
    private void SetErrorStatus(Exception ex)
    {
        var message = (ex is AggregateException { InnerException: { } inner } ? inner : ex).Message;
        var firstLine = message.Split('\n', 2)[0].Trim();
        SetStatus(LocalizationService.Format("StatusErrorPrefix", firstLine), StatusLevel.Error);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLog.DirectoryPath);
            Process.Start(new ProcessStartInfo(AppLog.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("App", "Unable to open log directory", ex);
            SetErrorStatus(ex);
        }
    }

    private void UnlockerSettings_Click(object sender, RoutedEventArgs e)
    {
        try { DialogOwnerService.ShowDialogSafe(new UnlockerSettingsWindow(), this); }
        catch (Exception ex) { SetErrorStatus(ex); }
    }

    // --- Window chrome ---

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        if (source is null)
        {
            return;
        }

        source.RemoveHook(WindowProcedure);
        source.AddHook(WindowProcedure);
        windowSource = source;
        lastMonitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!exitConfirmed && (msg == WM_CLOSE
            || (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_CLOSE)))
        {
            handled = true;
            QueueClosePrompt();
        }
        else if (msg == WM_GETMINMAXINFO)
        {
            ApplyWorkAreaMaximizeBounds(hwnd, lParam);
            handled = true;
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            NoteDisplayTransition();
        }
        else if (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED)
        {
            NoteDisplayTransition();
        }
        else if (msg == WM_WINDOWPOSCHANGING)
        {
            NoteMonitorTransition(lParam);
        }

        return IntPtr.Zero;
    }

    private void NoteDisplayTransition() => displayTransitionTick = Environment.TickCount;

    private bool IsDisplayTransitionActive()
    {
        var elapsed = unchecked(Environment.TickCount - displayTransitionTick);
        return (uint)elapsed < DisplayTransitionGraceMs;
    }

    private void NoteMonitorTransition(IntPtr lParam)
    {
        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((pos.flags & (SWP_NOMOVE | SWP_NOSIZE)) == (SWP_NOMOVE | SWP_NOSIZE))
        {
            return;
        }

        if (pos.x <= -32000 || pos.y <= -32000 || pos.cx <= 0 || pos.cy <= 0)
        {
            return;
        }

        var rect = new RECT
        {
            left = pos.x,
            top = pos.y,
            right = pos.x + pos.cx,
            bottom = pos.y + pos.cy
        };
        var monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        if (lastMonitor != IntPtr.Zero && monitor != lastMonitor)
        {
            NoteDisplayTransition();
        }

        lastMonitor = monitor;
    }

    private static bool IsPointerOverTaskbar()
    {
        if (!GetCursorPos(out var point))
        {
            return false;
        }

        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var onMonitor = point.x >= monitorInfo.rcMonitor.left && point.x < monitorInfo.rcMonitor.right
            && point.y >= monitorInfo.rcMonitor.top && point.y < monitorInfo.rcMonitor.bottom;
        var inWorkArea = point.x >= monitorInfo.rcWork.left && point.x < monitorInfo.rcWork.right
            && point.y >= monitorInfo.rcWork.top && point.y < monitorInfo.rcWork.bottom;
        return onMonitor && !inWorkArea;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsWinKeyDown() => IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);

    private static bool IsWinDownMinimizeChord() => IsWinKeyDown() && IsKeyDown(VK_DOWN);

    private static bool IsMonitorMoveChord() =>
        IsWinKeyDown() && IsKeyDown(VK_SHIFT) && (IsKeyDown(VK_LEFT) || IsKeyDown(VK_RIGHT));

    private static void ApplyWorkAreaMaximizeBounds(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var rcWork = monitorInfo.rcWork;
        var rcMonitor = monitorInfo.rcMonitor;
        minMaxInfo.ptMaxPosition.x = Math.Abs(rcWork.left - rcMonitor.left);
        minMaxInfo.ptMaxPosition.y = Math.Abs(rcWork.top - rcMonitor.top);
        minMaxInfo.ptMaxSize.x = Math.Abs(rcWork.right - rcWork.left);
        minMaxInfo.ptMaxSize.y = Math.Abs(rcWork.bottom - rcWork.top);
        Marshal.StructureToPtr(minMaxInfo, lParam, true);
    }

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
            && (modifiers & (ModifierKeys.Shift | ModifierKeys.Alt)) == 0
            && (SequencePanelControl.IsKeyboardFocusWithin || ConditionPanel.IsKeyboardFocusWithin)
            && !IsTextEditingSource(e.OriginalSource as DependencyObject))
        {
            SequencePanelControl.UndoLastChange();
            e.Handled = true;
            return;
        }

        if ((modifiers & ModifierKeys.Control) != 0
            && (modifiers & ModifierKeys.Alt) == 0
            && (key == Key.Y || (key == Key.Z && (modifiers & ModifierKeys.Shift) != 0))
            && (SequencePanelControl.IsKeyboardFocusWithin || ConditionPanel.IsKeyboardFocusWithin)
            && !IsTextEditingSource(e.OriginalSource as DependencyObject))
        {
            SequencePanelControl.RedoLastChange();
            e.Handled = true;
            return;
        }

        if (ConditionPanel.IsKeyboardFocusWithin
            && !IsTextEditingSource(e.OriginalSource as DependencyObject)
            && ConditionPanel.HandleExplorerShortcut(key, modifiers))
        {
            e.Handled = true;
            return;
        }

        if (SequencePanelControl.IsKeyboardFocusWithin
            && !IsTextEditingSource(e.OriginalSource as DependencyObject)
            && SequencePanelControl.HandleExplorerShortcut(key, modifiers))
        {
            e.Handled = true;
            return;
        }

        if ((modifiers & ModifierKeys.Control) != 0
            && key == Key.Delete
            && SequencePanelControl.IsKeyboardFocusWithin
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
        RefreshThemeToggleButton();
    }

    private void RefreshThemeToggleButton()
    {
        ThemeToggleButton.Content = ThemeService.CurrentTheme == AppTheme.Dark ? "\uE708" : "\uE706";
        RefreshWindowChromeButtons();
    }

    private void RefreshWindowChromeButtons()
    {
        if (MaximizeWindowButton is null)
        {
            return;
        }

        MaximizeWindowButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeWindowButton.ToolTip = L(WindowState == WindowState.Maximized ? "RestoreWindow" : "MaximizeWindow");
    }

    // --- File operations ---

    private void OpenMacro_Click(object sender, RoutedEventArgs e)
    {
        if (RejectLockedEdit()) return;
        var dialog = new OpenFileDialog { Filter = L("MacroFileFilter"), Title = L("OpenMacroTitle") };
        if (DialogOwnerService.ShowDialogSafe(dialog, this) != true) return;

        try
        {
            ClearMacroNavigationStack();
            var imported = MacroConversionService.ImportToMcrx(new MacroImportRequest(
                File.ReadAllText(dialog.FileName),
                dialog.FileName,
                MacroConversionFormat.MacroHidMcrx));
            var document = imported.Document;
            SequencePanelControl.SetEditorDocument(document);
            SequencePanelControl.ClearUndoHistory();
            PlaybackPanelControl.SetPlaybackControls(document.Playback);
            RefreshConditionStepChoices();
            ConditionPanel.LoadConditions(document.EffectiveConditions);
            SequencePanelControl.SetConditionHighlights(document.EffectiveConditions, i => ConditionPanel.GetConditionColor(i));
            CheckAndWarnConflicts(document);
            SetStatus(Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
            DialogOwnerService.MessageBoxSafe(
                this,
                ex.Message,
                L("ConversionImportErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SaveMacro_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = L("MacroFileFilter"), Title = L("SaveMacroTitle"), DefaultExt = ".mcrx" };
        if (DialogOwnerService.ShowDialogSafe(dialog, this) == true)
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
        MinimizeWindowButton.ToolTip = L("Minimize");
        CloseWindowButton.ToolTip = L("CloseWindow");
        ThemeToggleButton.ToolTip = L("ToggleTheme");
        UnlockerSettingsButton.Content = L("UnlockerSettings");
        RefreshWindowChromeButtons();
        ApplyPanelChromeToolTips(this);
        UpdateTrayMenuText();
        LanguageLabelText.Text = L("Language");
        OpenButton.Content = L("OpenMacroFile");
        SaveButton.Content = L("SaveMacroAs");
        WindowMenuButton.Content = L("WorkspaceWindowMenu");
        OpenLogsButton.Content = L("OpenLogs");
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
        ConditionPanel.ApplyLocalization();
        JsonPanel.ApplyLocalization();
        ActionPalette.ApplyLocalization();
        PlaybackPanelControl.ApplyLocalization();
        UpdatePauseListeningButton();
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveWorkspaceLayout();
        windowSource?.RemoveHook(WindowProcedure);
        windowSource = null;
        DisposeTrayIcon();
        keyboardHook?.Dispose();
        colorSampleOverlay.Close();
        DisposeMacroRecorder();
        StopListeningControllers();
        playbackController?.Stop();
        foreach (var panel in workspacePanels.Values)
        {
            panel.Window?.ForceClose();
        }

        base.OnClosed(e);
    }

    // Panel header float/close buttons are unnamed in XAML (one pair per panel); find them by their click handlers' tags.
    private static void ApplyPanelChromeToolTips(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button { Style: { } style } button && button.Tag is string
                && ReferenceEquals(style, button.TryFindResource("ToolWindowChromeButton")))
            {
                button.ToolTip = (button.Content as string) == "\uE740" ? L("FloatPanel") : L("ClosePanel");
            }
            ApplyPanelChromeToolTips(child);
        }
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

        LibraryToolTitle.Text = WorkspaceTitle("WorkspacePanelLibrary");
        SequenceToolTitle.Text = WorkspaceTitle("WorkspacePanelSequence");
        ConditionToolTitle.Text = WorkspaceTitle("WorkspacePanelConditions");
        ActionPaletteToolTitle.Text = WorkspaceTitle("WorkspacePanelActions");
        PlaybackToolTitle.Text = WorkspaceTitle("WorkspacePanelPlayback");
        JsonToolTitle.Text = WorkspaceTitle("AdvancedJson");

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
        if (editorState.IsEditLocked) return;
        try
        {
            var doc = SequencePanelControl.GetCurrentDocument();
            var updated = doc with { Conditions = ConditionPanel.Conditions.ToList() };
            SequencePanelControl.CaptureUndoSnapshot();
            SequencePanelControl.SetEditorDocument(updated);
            RefreshConditionStepChoices();
            SequencePanelControl.SetConditionHighlights(updated.EffectiveConditions, i => ConditionPanel.GetConditionColor(i));
            CheckAndWarnConflicts(updated);
            ScheduleAutoSave();
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
        }
    }

    private void OnExtractConditionPackRequested(IReadOnlyList<ConditionalDirective> pack, string suggestedName)
    {
        try
        {
            if (pack.Count == 0)
            {
                SetStatus(L("ExtractConditionPackEmpty"));
                return;
            }

            var document = new MacroDocument(
                1,
                suggestedName,
                PlaybackSettings.Default,
                [],
                pack.ToList(),
                Kind: MacroKind.Condition);
            var item = libraryStore.CreateMacro(
                document,
                folder: LibraryPanel.CurrentDatabaseFolder,
                groupId: LibraryPanel.CurrentDatabaseGroupId);
            LibraryPanel.RefreshList();
            SetStatus(LocalizationService.Format("ExtractedConditionPackCreated", item.Name, pack.Count));
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindow", "Operation failed", ex);
            SetErrorStatus(ex);
        }
    }

    private void OnPickRegionRequested(object? sender, EventArgs e)
    {
        if (RejectLockedEdit()) return;
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

    private static string ToPrecisionModeText(PrecisionMode mode) => mode switch
    {
        PrecisionMode.Balanced => "balanced",
        PrecisionMode.UltraLowJitter => "ultraLowJitter",
        _ => "extremeDuringPlayback"
    };

    private static string WorkspaceTitle(string key) => LocalizationService.Get(key);
    private static string L(string key) => LocalizationService.Get(key);

    private sealed class WorkspacePanelRegistration
    {
        public WorkspacePanelRegistration(
            string id,
            string title,
            FrameworkElement dockContainer,
            ContentControl dockHost,
            FrameworkElement element,
            WorkspaceDockRegion defaultDockRegion,
            double defaultDockedSize,
            int defaultOrder,
            bool defaultIsVisible)
        {
            Id = id;
            Title = title;
            DockContainer = dockContainer;
            DockHost = dockHost;
            Element = element;
            DefaultDockRegion = defaultDockRegion;
            DefaultDockedSize = defaultDockedSize;
            DefaultOrder = defaultOrder;
            DefaultIsVisible = defaultIsVisible;
        }

        public string Id { get; }
        public string Title { get; set; }
        public FrameworkElement DockContainer { get; }
        public ContentControl DockHost { get; }
        public FrameworkElement Element { get; }
        public WorkspaceDockRegion DefaultDockRegion { get; }
        public double DefaultDockedSize { get; }
        public int DefaultOrder { get; }
        public bool DefaultIsVisible { get; }
        public WorkspacePanelWindow? Window { get; set; }
        public bool IsVisible { get; set; }
        public bool IsFloating { get; set; }
        public WorkspaceDockRegion DockRegion { get; set; }
        public WorkspacePanelLayout? SavedLayout { get; set; }
    }

    private System.Windows.Forms.NotifyIcon? trayIcon;
    private System.Windows.Forms.ContextMenuStrip? trayMenu;
    private System.Windows.Forms.ToolStripMenuItem? trayShowItem;
    private System.Windows.Forms.ToolStripMenuItem? trayExitItem;
    private System.Drawing.Icon? trayIconImage;
    private bool isInTray;
    private bool exitConfirmed;
    private bool closePromptQueued;
    private int taskbarMinimizeDepth;
    private int displayTransitionTick = int.MinValue;
    private IntPtr lastMonitor;
    private WindowState stateBeforeTray = WindowState.Normal;

    internal void PushTaskbarMinimize() => taskbarMinimizeDepth++;

    internal void PopTaskbarMinimize() => taskbarMinimizeDepth = Math.Max(0, taskbarMinimizeDepth - 1);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (exitConfirmed)
        {
            if (!shutdownFinished) { e.Cancel = true; FinishShutdownAsync(); return; }
            DisposeTrayIcon();
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        QueueClosePrompt();
    }

    private void QueueClosePrompt()
    {
        if (closePromptQueued)
        {
            return;
        }

        closePromptQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                ApplyCloseBehavior();
            }
            finally
            {
                closePromptQueued = false;
            }
        }));
    }

    private void ApplyCloseBehavior()
    {
        var behavior = runtimePrecisionSettings.CloseButton;
        if (behavior == CloseButtonBehavior.Ask)
        {
            // Taskbar close can arrive while minimized or inactive; make the owned prompt visible.
            if (isInTray) RestoreFromTray();
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            var dialog = new CloseWindowPrompt();
            dialog.ContentRendered += (_, _) => dialog.Activate();
            if (DialogOwnerService.ShowDialogSafe(dialog, this) != true)
            {
                return;
            }

            behavior = dialog.Choice;
            if (dialog.Remember)
            {
                runtimePrecisionSettings = runtimePrecisionSettings with { CloseButton = behavior };
                RuntimePrecisionSettingsStore.Save(runtimePrecisionSettings);
                LibraryPanel.SetRuntimePrecisionControls(runtimePrecisionSettings);
            }
        }

        if (behavior == CloseButtonBehavior.MinimizeToTray)
        {
            MinimizeToTray();
            return;
        }

        if (behavior == CloseButtonBehavior.Exit)
        {
            exitConfirmed = true;
            Close();
        }
    }

    private void MinimizeToTray()
    {
        if (isInTray)
        {
            return;
        }

        stateBeforeTray = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        EnsureTrayIcon();
        isInTray = true;
        ShowInTaskbar = false;
        Hide();
        if (trayIcon is not null)
        {
            trayIcon.Visible = true;
        }
    }

    private void RestoreFromTray()
    {
        isInTray = false;
        ShowInTaskbar = true;
        Show();
        WindowState = stateBeforeTray == WindowState.Minimized ? WindowState.Normal : stateBeforeTray;
        Activate();
        if (trayIcon is not null)
        {
            trayIcon.Visible = false;
        }
    }

    private void EnsureTrayIcon()
    {
        if (trayIcon is not null)
        {
            UpdateTrayMenuText();
            return;
        }

        trayShowItem = new System.Windows.Forms.ToolStripMenuItem();
        trayExitItem = new System.Windows.Forms.ToolStripMenuItem();
        trayShowItem.Click += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
        trayExitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            exitConfirmed = true;
            Close();
        }));
        trayMenu = new System.Windows.Forms.ContextMenuStrip();
        trayMenu.Items.Add(trayShowItem);
        trayMenu.Items.Add(trayExitItem);

        trayIconImage = LoadTrayIcon();
        trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = trayIconImage,
            ContextMenuStrip = trayMenu,
            Visible = false
        };
        trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Dispatcher.BeginInvoke(RestoreFromTray);
            }
        };
        trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
        UpdateTrayMenuText();
    }

    private void UpdateTrayMenuText()
    {
        if (trayIcon is not null)
        {
            var title = L("AppTitle");
            trayIcon.Text = title.Length <= 63 ? title : title[..63];
        }

        if (trayShowItem is not null)
        {
            trayShowItem.Text = L("TrayShow");
        }

        if (trayExitItem is not null)
        {
            trayExitItem.Text = L("TrayExit");
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path))
        {
            var associated = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (associated is not null)
            {
                return associated;
            }
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void DisposeTrayIcon()
    {
        if (trayIcon is not null)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayIcon = null;
        }

        trayMenu?.Dispose();
        trayMenu = null;

        trayShowItem = null;
        trayExitItem = null;
        trayIconImage?.Dispose();
        trayIconImage = null;
        isInTray = false;
    }

    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_CLOSE = 0x0010;
    private const long SC_CLOSE = 0xF060;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_DPICHANGED = 0x02E0;
    private const int MONITOR_DEFAULTTONULL = 0;
    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint DisplayTransitionGraceMs = 800;
    private const int VK_SHIFT = 0x10;
    private const int VK_LEFT = 0x25;
    private const int VK_RIGHT = 0x27;
    private const int VK_DOWN = 0x28;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
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
