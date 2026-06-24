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
    private RuntimePrecisionSettings runtimePrecisionSettings = RuntimePrecisionSettingsStore.Load();

    private GlobalKeyboardHook? keyboardHook;
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
    private bool updatingLanguageComboBox;
    private bool updatingWorkspaceMenu;
    private bool syncingJsonPanel;
    private bool workspaceContentRendered;
    private string activeRightToolPanelId = "actions";

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
        autoSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        autoSaveTimer.Tick += (_, _) => PerformDebouncedAutoSave();
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
        LibraryPanel.MacroCreated += OnMacroCreated;
        LibraryPanel.MacroDuplicated += OnMacroDuplicated;
        LibraryPanel.MacroDeleted += OnMacroDeleted;
        LibraryPanel.ImportApplied += OnImportApplied;
        LibraryPanel.DocumentRequested += () => GetDocumentWithPlayback();
        LibraryPanel.ResultMessage += msg => SetStatus(msg);
        LibraryPanel.StartListeningGroupsRequested += OnStartListeningGroups;
        LibraryPanel.StopListeningGroupsRequested += OnStopListeningGroups;
        LibraryPanel.StopListeningAllRequested += OnStopListeningAll;
        LibraryPanel.PrecisionSettingsEdited += OnPrecisionSettingsEdited;
        LibraryPanel.LibraryStructureEdited += OnLibraryStructureEdited;

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

        PlaybackPanelControl.StartListeningRequested += OnStartCurrentListening;
        PlaybackPanelControl.StopListeningRequested += OnStopCurrentListening;
        PlaybackPanelControl.RunNowRequested += OnRunNow;
        PlaybackPanelControl.StopPlaybackRequested += OnStopPlayback;
        PlaybackPanelControl.PlaybackSettingsEdited += OnPlaybackSettingsEdited;

        ConditionPanel.ConditionSelectionChanged += OnConditionSelectionChanged;
        ConditionPanel.ConditionsModified += OnConditionsModified;
        ConditionPanel.PickRegionRequested += OnPickRegionRequested;
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
            FloatingBounds: new WorkspaceFloatingBounds(120, 120, 720, 520));
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
        syncingJsonPanel = true;
        JsonPanel.EditorText = text;
        syncingJsonPanel = false;
    }

    private void RefreshJsonPanelText()
    {
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
        var result = DialogOwnerService.MessageBoxSafe(
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
            ScheduleAutoSave();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void OnPrecisionSettingsEdited()
    {
        try
        {
            runtimePrecisionSettings = ReadRuntimePrecisionSettingsFromPanel();
            RuntimePrecisionSettingsStore.Save(runtimePrecisionSettings);
            WarmUpRuntimePrecision();
            RestartListeningWithCurrentSettings();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private RuntimePrecisionSettings ReadRuntimePrecisionSettingsFromPanel()
    {
        var precision = LibraryPanel.GetSelectedPrecisionMode();
        var affinityMask = PlaybackAffinityMask.NormalizeOrThrow(LibraryPanel.AffinityMaskText);
        return new RuntimePrecisionSettings(precision, affinityMask);
    }

    private void WarmUpRuntimePrecision()
    {
        NativePlaybackWarmup.QueueWarmUpForPrecision(
            runtimePrecisionSettings.Precision,
            runtimePrecisionSettings.AffinityMask);
    }

    private void RestartListeningWithCurrentSettings()
    {
        if (!listening)
        {
            return;
        }

        var activeIds = listeningControllers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        StopListeningControllers();
        if (activeIds.Count == 0)
        {
            listening = false;
            keyboardHook?.Dispose();
            keyboardHook = null;
            RefreshLibraryListeningState();
            return;
        }

        var candidates = BuildListeningCandidates()
            .Where(candidate => activeIds.Contains(candidate.Item.Id))
            .ToList();
        if (candidates.Count == 0)
        {
            listening = false;
            keyboardHook?.Dispose();
            keyboardHook = null;
            RefreshLibraryListeningState();
            return;
        }

        StartListeningCandidates(candidates);
    }

    private void OnLibraryStructureEdited()
    {
        RestartListeningWithCurrentSettings();
        RefreshLibraryListeningState();
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
        RefreshLibraryListeningState();
        if (updateStatus)
        {
            SetStatus(L("LibrarySaved"));
        }
    }

    private void ScheduleAutoSave()
    {
        autoSaveTimer.Stop();
        autoSaveTimer.Start();
    }

    private void PerformDebouncedAutoSave()
    {
        autoSaveTimer.Stop();
        try
        {
            AutoSaveCurrentMacro(updateStatus: false);
            RestartListeningWithCurrentSettings();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
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

    private async void OnStartListeningGroups(IReadOnlyList<string> groupIds)
    {
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
            var count = StartListeningCandidates(candidates);
            PlaybackPanelControl.SetPlaybackStatus($"{L("PlaybackStatusListening")} ({count})");
            SetStatus($"{L("Listening")} ({count})");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
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

            var count = StartListeningCandidates(candidates);
            PlaybackPanelControl.SetPlaybackStatus($"{L("PlaybackStatusListening")} ({count})");
            SetStatus($"{L("Listening")} ({count})");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusError"));
            PlaybackPanelControl.SetPlaybackResult(ex.Message);
            SetStatus(L("PlaybackError"));
            RefreshLibraryListeningState();
        }
    }

    private void OnStopListeningAll()
    {
        listening = false;
        keyboardHook?.Dispose();
        keyboardHook = null;
        StopListeningControllers();
        playbackController?.Stop();
        RefreshLibraryListeningState();
        PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusIdle"));
        PlaybackPanelControl.SetPlaybackResult(L("HotkeyListenerStopped"));
        SetStatus(L("Idle"));
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

    private int StartListeningCandidates(IReadOnlyList<ListeningCandidate> candidates)
    {
        var conflicts = FindListeningConflicts(candidates);
        if (conflicts.Count > 0)
        {
            RefreshLibraryListeningState(conflicts);
            throw new InvalidOperationException(FormatListeningConflictMessage(conflicts));
        }

        var bindings = new List<HotkeyBinding>();
        var controllers = new Dictionary<string, MacroPlaybackController>(StringComparer.OrdinalIgnoreCase);
        var macroNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupProcessFilters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var item = candidate.Item;
            var document = candidate.Document;
            bindings.Add(new HotkeyBinding(item.Id, candidate.Trigger));
            var executor = new MacroPlaybackExecutor(inputSink, macroResolver: ResolveMacroForPlayback);
            var options = CreatePlaybackOptions(document);
            executor.Prepare(document, options);
            controllers[item.Id] = new MacroPlaybackController(document, executor, options);
            macroNames[item.Id] = document.Name;
            groupProcessFilters[item.Id] = candidate.GroupProcessFilter;
        }

        if (bindings.Count == 0)
            throw new InvalidOperationException(L("ChooseTriggerBeforeListening"));

        keyboardHook?.Dispose();
        keyboardHook = null;
        StopListeningControllers();
        listening = false;

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

        foreach (var (id, groupProcessFilter) in groupProcessFilters)
        {
            listeningGroupProcessFilters[id] = groupProcessFilter;
        }

        playbackController = null;
        listening = true;
        RefreshLibraryListeningState();
        return bindings.Count;
    }

    private void StopListeningControllers()
    {
        foreach (var controller in listeningControllers.Values)
        {
            controller.Stop();
            controller.Dispose();
        }

        listeningControllers.Clear();
        listeningMacroNames.Clear();
        listeningGroupProcessFilters.Clear();
        activeTriggeredControllerIds.Clear();
    }

    private void StopListeningController(string id)
    {
        if (!listeningControllers.Remove(id, out var controller))
        {
            return;
        }

        controller.Stop();
        controller.Dispose();
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
        keyboardHook?.Dispose();
        keyboardHook = null;

        var activeIds = listeningControllers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (activeIds.Count == 0)
        {
            listening = false;
            return;
        }

        var candidates = BuildListeningCandidates()
            .Where(candidate => activeIds.Contains(candidate.Item.Id))
            .ToList();
        var candidateIds = candidates.Select(candidate => candidate.Item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in activeIds.Where(id => !candidateIds.Contains(id)).ToList())
        {
            StopListeningController(id);
        }

        if (candidates.Count == 0)
        {
            listening = false;
            return;
        }

        keyboardHook = new GlobalKeyboardHook();
        keyboardHook.TriggerPressed += KeyboardHook_TriggerPressed;
        keyboardHook.TriggerReleased += KeyboardHook_TriggerReleased;
        keyboardHook.Start(candidates.Select(candidate => new HotkeyBinding(candidate.Item.Id, candidate.Trigger)));
        listening = true;
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

    private void RefreshLibraryListeningState(
        IReadOnlyList<(ListeningCandidate Left, ListeningCandidate Right)>? conflicts = null)
    {
        var candidates = BuildListeningCandidates();
        conflicts ??= FindListeningConflicts(candidates);
        var conflictIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (left, right) in conflicts)
        {
            conflictIds.Add(left.Item.Id);
            conflictIds.Add(right.Item.Id);
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

    private async void OnRunNow()
    {
        try
        {
            var document = GetDocumentWithPlayback();
            playbackController?.Dispose();
            var executor = new MacroPlaybackExecutor(inputSink, macroResolver: ResolveMacroForPlayback);
            var options = CreatePlaybackOptions(document);
            executor.Prepare(document, options);
            playbackController = new MacroPlaybackController(document, executor, options);
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

    private PlaybackExecutionOptions CreatePlaybackOptions(MacroDocument document)
    {
        return new PlaybackExecutionOptions(
            document.Playback.Mode,
            document.Playback.Count,
            PixelEvaluationMode.Live,
            NoWait: false,
            runtimePrecisionSettings.Precision,
            runtimePrecisionSettings.AffinityMask);
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
        var item = libraryStore.CreateMacro(document, groupId: LibraryPanel.CurrentDatabaseGroupId);
        editorState.SelectedMacroId = item.Id;
        LibraryPanel.RefreshList();
        RefreshLibraryListeningState();
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
            ScheduleAutoSave();
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
            var foregroundProcess = ForegroundProcessService.GetForegroundProcessName();
            var groupProcessFilter = listeningGroupProcessFilters.TryGetValue(e.Id, out var groupFilter)
                ? groupFilter
                : string.Empty;
            if (!MacroLibraryActivationFilter.Matches(groupProcessFilter, foregroundProcess))
            {
                return;
            }

            activeTriggeredControllerIds.Add(e.Id);
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
            if (!activeTriggeredControllerIds.Remove(e.Id)) return;
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
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            ApplyWorkAreaMaximizeBounds(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

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
        RefreshThemeToggleButton();
    }

    private void RefreshThemeToggleButton()
    {
        ThemeToggleButton.Content = ThemeService.CurrentTheme == AppTheme.Dark ? "☾" : "☀";
    }

    // --- File operations ---

    private void OpenMacro_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = L("MacroFileFilter"), Title = L("OpenMacroTitle") };
        if (DialogOwnerService.ShowDialogSafe(dialog, this) == true)
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
        LanguageLabelText.Text = L("Language");
        OpenButton.Content = L("OpenMacroFile");
        SaveButton.Content = L("SaveMacroAs");
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
        windowSource?.RemoveHook(WindowProcedure);
        windowSource = null;
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

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

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
