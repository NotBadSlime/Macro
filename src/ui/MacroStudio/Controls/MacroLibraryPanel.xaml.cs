using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacroHid.Converter;
using MacroHid.Core;
using MacroStudio.Services;
using Microsoft.Win32;

namespace MacroStudio.Controls;

public sealed record MacroLibraryDeleteItem(
    string? MacroId,
    string GroupId,
    string FolderName,
    string DisplayName,
    bool IsFolder,
    bool IsLocked);

public partial class MacroLibraryPanel : UserControl
{
    private const string MacroLibraryDragFormat = "MacroHID.MacroLibraryItem";
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WM_MOUSEWHEEL_LOW_LEVEL = 0x020A;
    private const int WH_MOUSE_LL = 14;
    private const long LibraryAutoScrollIntervalMilliseconds = 120;
    private const double WheelDelta = 120.0;

    private readonly LowLevelMouseProc libraryDragMouseHookProc;
    private readonly DispatcherTimer explorerHorizontalScrollTimer;
    private MacroEditorState? state;
    private bool suppressSelection;
    private readonly HashSet<string> expandedGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expandedFolders = new(StringComparer.Ordinal);
    private readonly HashSet<string> selectedManagerGroupIds = new(StringComparer.OrdinalIgnoreCase);
    private string selectedGroupId = MacroLibraryStore.GlobalGroupId;
    private string activeDatabaseGroupId = MacroLibraryStore.GlobalGroupId;
    private string currentDatabaseFolder = string.Empty;
    private bool showingDatabaseContents;
    private bool managerSelectionInitialized;
    private string? selectedFolder;
    private IReadOnlyList<MacroLibraryClipboardItem> clipboard = [];
    private MacroLibraryTreeNode? renamingNode;
    private MacroLibraryTreeNode? contextMenuTargetNode;
    private bool renameCommitQueued;
    private bool refreshTreeAfterRename;
    private bool updatingRuntimePrecisionControls;
    private bool updatingGroupControls;
    private bool libraryDragInProgress;
    private ScrollViewer? macroTreeScrollViewer;
    private ScrollViewer? explorerScrollViewer;
    private HwndSource? macroTreeHwndSource;
    private IntPtr libraryDragMouseHookHandle;
    private long lastLibraryAutoScrollTick;
    private double explorerHorizontalScrollTarget;
    private Point? explorerDragStartPoint;
    private MacroLibraryTreeNode? macroDropIndicatorNode;
    private IReadOnlyDictionary<string, MacroLibraryListenState> listeningStates =
        new Dictionary<string, MacroLibraryListenState>(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? MacroSelected;
    public event Action<string>? MacroDuplicated;
    public event Action<string>? MacroDeleted;
    public event Action<IReadOnlyList<MacroLibraryDeleteItem>>? LibraryItemsDeleteRequested;
    public event Action<string, bool>? MacroLockChanged;
    public event Action<MacroLibraryItem>? MacroCreated;
    public event Action<MacroDocument>? ImportApplied;
    public event Func<MacroDocument>? DocumentRequested;
    public event Action<string>? ResultMessage;
    public event Action<IReadOnlyList<string>>? StartListeningGroupsRequested;
    public event Action<IReadOnlyList<string>>? StopListeningGroupsRequested;
    public event Action? StopListeningAllRequested;
    public event Action? PrecisionSettingsEdited;
    public event Action? LibraryStructureEdited;

    public MacroLibraryPanel()
    {
        libraryDragMouseHookProc = LibraryDragMouseHookCallback;
        InitializeComponent();
        explorerHorizontalScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        explorerHorizontalScrollTimer.Tick += ExplorerHorizontalScrollTimer_Tick;
    }

    public void Initialize(MacroEditorState editorState)
    {
        state = editorState;
        RefreshConversionText();
        RefreshTree();
    }

    public void ApplyLocalization()
    {
        NewGroupButton.Content = L("NewDatabase");
        DeleteDatabaseButton.Content = L("DeleteDatabase");
        BackToManagerButton.Content = L("ReturnToManager");
        NewMacroMenuButton.Content = L("NewMacro");
        ToolbarNewMacroMenuItem.Header = L("NewMacroFile");
        ToolbarNewFolderMenuItem.Header = L("NewFolder");
        GroupProcessFilterLabelText.Text = L("GroupProcessFilter");
        GroupProcessFilterBox.ToolTip = L("GroupProcessFilterHelp");
        SelectProcessButton.Content = L("SelectProcess");
        SelectProcessFileButton.Content = L("SelectProcessFile");
        ApplyGroupButton.Content = L("Apply");
        GlobalRuntimeTitleText.Text = L("GlobalRuntime");
        PrecisionModeLabelText.Text = L("PrecisionMode");
        AffinityMaskLabelText.Text = L("AffinityMask");
        AffinityMaskHelpText.Text = L("AffinityMaskHelp");
        AffinityMaskBox.ToolTip = L("AffinityMaskHelp");
        ListeningTitleText.Text = L("Listening");
        StopEveryListeningButton.Content = L("StopListeningAll");
        RenameMenuItem.Header = L("Rename");
        CopyMenuItem.Header = L("Copy");
        PasteMenuItem.Header = L("Paste");
        DeleteMenuItem.Header = L("Delete");
        UpFolderButton.Content = L("UpOneLevel");
        UpFolderButton.ToolTip = L("UpOneLevel");
        ExplorerNameHeaderText.Text = L("Name");
        ExplorerTypeHeaderText.Text = L("Type");
        ExplorerTriggerHeaderText.Text = L("Trigger");
        ExplorerModifiedHeaderText.Text = L("DateModified");
        ExplorerExportMacroMenuItem.Header = L("ExportMacro");
        ExplorerExportFolderMenuItem.Header = L("ExportThisFolder");
        ExplorerNewMenuItem.Header = L("New");
        ExplorerNewMacroMenuItem.Header = L("NewMacroFile");
        ExplorerNewFolderMenuItem.Header = L("NewFolder");
        ExplorerRenameMenuItem.Header = L("Rename");
        ExplorerCopyMenuItem.Header = L("Copy");
        ExplorerPasteMenuItem.Header = L("Paste");
        ExplorerToggleLockMenuItem.Header = L("LockMacroFile");
        ExplorerViewMenuItem.Header = L("View");
        ExplorerDetailsMenuItem.Header = L("ViewDetails");
        ExplorerListMenuItem.Header = L("ViewList");
        ExplorerSmallIconsMenuItem.Header = L("ViewSmallIcons");
        ExplorerLargeIconsMenuItem.Header = L("ViewLargeIcons");
        ExplorerSortMenuItem.Header = L("SortBy");
        ExplorerSortManualMenuItem.Header = L("SortManual");
        ExplorerSortNameMenuItem.Header = L("Name");
        ExplorerSortUpdatedMenuItem.Header = L("DateModified");
        ExplorerRefreshMenuItem.Header = L("Refresh");
        ExplorerDeleteMenuItem.Header = L("Delete");
        ImportExportTitleText.Text = L("ImportExport");
        ImportMacroButton.Content = L("ImportMacro");
        ExportMacroButton.Content = L("ExportMacro");
        MacroSearchBox.ToolTip = L("SearchMacros");
        MacroSearchPlaceholderText.Text = L("SearchMacros");
        GroupProcessFilterPlaceholderText.Text = L("ProcessFilterPlaceholder");
        RefreshConversionText();

        foreach (var item in LibrarySortBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = item.Tag?.ToString() switch
            {
                "manual" => L("SortManual"),
                "updated" => L("SortRecentlyUpdated"),
                _ => L("SortName")
            };
        }

        foreach (var item in PrecisionModeBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = item.Tag?.ToString() switch
            {
                "balanced" => L("PrecisionBalanced"),
                "extremeDuringPlayback" => L("PrecisionExtremeDuringPlayback"),
                "ultraLowJitter" => L("PrecisionUltraLowJitter"),
                _ => item.Content
            };
        }

        ApplyProgressiveViewState();
        UpdateManagerListeningButtons();
    }

    public string AffinityMaskText => AffinityMaskBox.Text.Trim();

    public string CurrentDatabaseGroupId => showingDatabaseContents
        ? activeDatabaseGroupId
        : selectedGroupId;

    public PrecisionMode GetSelectedPrecisionMode()
    {
        var selected = PrecisionModeBox.SelectedItem as ComboBoxItem;
        var value = selected?.Tag?.ToString() ?? "extremeDuringPlayback";
        return value switch
        {
            "balanced" => PrecisionMode.Balanced,
            "ultraLowJitter" => PrecisionMode.UltraLowJitter,
            _ => PrecisionMode.ExtremeDuringPlayback
        };
    }

    public void SetRuntimePrecisionControls(RuntimePrecisionSettings settings)
    {
        updatingRuntimePrecisionControls = true;
        try
        {
            AffinityMaskBox.Text = settings.AffinityMask;
            foreach (var item in PrecisionModeBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), ToPrecisionModeText(settings.Precision), StringComparison.Ordinal))
                {
                    PrecisionModeBox.SelectedItem = item;
                    break;
                }
            }
        }
        finally
        {
            updatingRuntimePrecisionControls = false;
        }
    }

    public void RefreshList()
    {
        RefreshTree();
    }

    public void SetListeningStates(IReadOnlyDictionary<string, MacroLibraryListenState> states)
    {
        listeningStates = new Dictionary<string, MacroLibraryListenState>(states, StringComparer.OrdinalIgnoreCase);
        if (renamingNode is not null)
        {
            refreshTreeAfterRename = true;
            return;
        }

        foreach (var item in LibraryViewBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = item.Tag?.ToString() switch
            {
                "list" => L("ViewList"),
                "smallIcons" => L("ViewSmallIcons"),
                "largeIcons" => L("ViewLargeIcons"),
                _ => L("ViewDetails")
            };
        }

        RefreshTree();
    }

    public void RefreshTree()
    {
        if (state is null) return;
        if (renamingNode is not null)
        {
            refreshTreeAfterRename = true;
            return;
        }

        refreshTreeAfterRename = false;

        var search = MacroSearchBox.Text.Trim();
        var previousScrollOffset = showingDatabaseContents
            ? FindVisualChild<ScrollViewer>(ExplorerListView)?.VerticalOffset ?? 0
            : GetMacroTreeScrollViewer()?.VerticalOffset ?? 0;
        IReadOnlySet<string> selectedExplorerKeys = showingDatabaseContents
            ? GetSelectedExplorerKeys()
            : new HashSet<string>(StringComparer.Ordinal);
        if (!showingDatabaseContents && string.IsNullOrWhiteSpace(search))
        {
            CaptureExpandedGroups();
            CaptureExpandedFolders();
        }

        state.ReloadLibrary();
        EnsureActiveDatabaseGroup();
        EnsureManagerSelection();
        var selectedId = showingDatabaseContents
            ? state.SelectedMacroId ?? state.LibrarySnapshot.SelectedMacroId
            : null;
        var sortMode = (LibrarySortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "manual";
        var materializedItems = sortMode switch
        {
            "updated" => state.LibrarySnapshot.Items.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            "name" => state.LibrarySnapshot.Items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => state.LibrarySnapshot.Items.ToList()
        };

        var nodes = new List<MacroLibraryTreeNode>();
        try
        {
            suppressSelection = true;
            nodes = showingDatabaseContents
                ? BuildExplorerNodes(materializedItems, search)
                : BuildManagerNodes(materializedItems, search);

            if (!state.LibrarySnapshot.Groups.Any(group => string.Equals(group.Id, selectedGroupId, StringComparison.OrdinalIgnoreCase)))
            {
                selectedGroupId = MacroLibraryStore.GlobalGroupId;
            }

            var selectedGroup = state.LibrarySnapshot.Groups.FirstOrDefault(group => string.Equals(group.Id, selectedGroupId, StringComparison.OrdinalIgnoreCase))
                ?? state.LibrarySnapshot.Groups.FirstOrDefault();
            SetGroupEditor(selectedGroup);
            if (showingDatabaseContents)
            {
                MacroTreeView.ItemsSource = null;
                ExplorerListView.ItemsSource = nodes;
                RestoreExplorerSelection(nodes, selectedExplorerKeys, selectedId);
            }
            else
            {
                ExplorerListView.ItemsSource = null;
                MacroTreeView.ItemsSource = nodes;
            }
            ApplyProgressiveViewState();
        }
        finally
        {
            suppressSelection = false;
        }

        RestoreLibraryScroll(previousScrollOffset);
    }

    private List<MacroLibraryTreeNode> BuildManagerNodes(IReadOnlyList<MacroLibraryItem> materializedItems, string search)
    {
        var nodes = new List<MacroLibraryTreeNode>();
        foreach (var group in state!.LibrarySnapshot.Groups)
        {
            var groupItems = materializedItems
                .Where(item => string.Equals(item.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var matchesSearch = string.IsNullOrWhiteSpace(search)
                || group.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || group.ProcessFilter.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || groupItems.Any(item =>
                    item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                    || item.Folder.Contains(search, StringComparison.CurrentCultureIgnoreCase));
            if (!matchesSearch)
            {
                continue;
            }

            var groupNode = MacroLibraryTreeNode.Group(
                group,
                [],
                CreateGroupListenState(group, groupItems),
                selectedManagerGroupIds.Contains(group.Id));
            groupNode.IsExpanded = false;
            groupNode.IsSelected = string.Equals(selectedGroupId, group.Id, StringComparison.OrdinalIgnoreCase);
            nodes.Add(groupNode);
        }

        return nodes;
    }

    private MacroLibraryListenState? CreateGroupListenState(MacroLibraryGroup group, IReadOnlyList<MacroLibraryItem> groupItems)
    {
        var macroStates = groupItems
            .Select(item => listeningStates.TryGetValue(item.Id, out var listenState) ? listenState : null)
            .Where(state => state is not null)
            .Cast<MacroLibraryListenState>()
            .ToList();
        if (macroStates.Count == 0)
        {
            return null;
        }

        var listeningCount = macroStates.Count(state => state.IsListening);
        var hasConflict = macroStates.Any(state => state.IsConflict);
        return new MacroLibraryListenState(
            listeningCount > 0,
            hasConflict,
            string.Empty,
            string.Empty,
            group.IsGlobal || string.IsNullOrWhiteSpace(group.ProcessFilter) ? L("AllProcesses") : group.ProcessFilter,
            listeningCount,
            macroStates.Count,
            IsGroupSummary: true);
    }

    private List<MacroLibraryTreeNode> BuildDatabaseNodes(IReadOnlyList<MacroLibraryItem> materializedItems, string search, string? selectedId)
    {
        var group = GetActiveDatabaseGroup();
        if (group is null)
        {
            return [];
        }

        var groupItems = materializedItems
            .Where(item => string.Equals(item.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(search)
                || item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Folder.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        var nodes = new List<MacroLibraryTreeNode>();
        var folderNames = state!.LibrarySnapshot.GroupFolders
            .Where(folder => string.Equals(folder.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
            .Select(folder => folder.Name)
            .Concat(groupItems.Select(item => item.Folder))
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(folder => folder, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var folder in folderNames)
        {
            var children = groupItems
                .Where(item => string.Equals(item.Folder, folder, StringComparison.Ordinal))
                .Select(CreateMacroNode)
                .ToList();
            if (!string.IsNullOrWhiteSpace(search) && children.Count == 0 && !folder.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            var folderNode = MacroLibraryTreeNode.Folder(group.Id, folder, children);
            folderNode.IsExpanded = !string.IsNullOrWhiteSpace(search)
                || expandedFolders.Contains(FormatFolderExpansionKey(group.Id, folder));
            folderNode.IsSelected = selectedId is null
                && string.Equals(selectedGroupId, group.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(selectedFolder, folder, StringComparison.Ordinal);
            MarkSelectedMacro(folderNode.Children, selectedId);
            nodes.Add(folderNode);
        }

        foreach (var item in groupItems.Where(item => string.IsNullOrWhiteSpace(item.Folder)))
        {
            var macroNode = CreateMacroNode(item);
            macroNode.IsSelected = item.Id == selectedId;
            nodes.Add(macroNode);
        }

        return nodes;
    }

    private List<MacroLibraryTreeNode> BuildExplorerNodes(IReadOnlyList<MacroLibraryItem> materializedItems, string search)
    {
        var group = GetActiveDatabaseGroup();
        if (group is null)
        {
            return [];
        }

        var groupItems = materializedItems
            .Where(item => string.Equals(item.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var nodes = new List<MacroLibraryTreeNode>();
        if (string.IsNullOrWhiteSpace(currentDatabaseFolder))
        {
            var folderNames = state!.LibrarySnapshot.GroupFolders
                .Where(folder => string.Equals(folder.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
                .Select(folder => folder.Name)
                .Concat(groupItems.Select(item => item.Folder))
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(folder => folder, StringComparer.CurrentCultureIgnoreCase);
            foreach (var folder in folderNames)
            {
                var children = groupItems
                    .Where(item => string.Equals(item.Folder, folder, StringComparison.Ordinal))
                    .Select(CreateMacroNode)
                    .ToList();
                if (!string.IsNullOrWhiteSpace(search)
                    && !folder.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                    && !children.Any(child => child.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
                {
                    continue;
                }

                nodes.Add(MacroLibraryTreeNode.Folder(group.Id, folder, children));
            }
        }

        nodes.AddRange(groupItems
            .Where(item => string.Equals(item.Folder, currentDatabaseFolder, StringComparison.Ordinal))
            .Where(item => string.IsNullOrWhiteSpace(search)
                || item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .Select(CreateMacroNode));
        return nodes;
    }

    private void EnsureActiveDatabaseGroup()
    {
        if (state is null) return;
        var groups = state.LibrarySnapshot.Groups;
        if (groups.Count == 0)
        {
            activeDatabaseGroupId = MacroLibraryStore.GlobalGroupId;
            selectedGroupId = MacroLibraryStore.GlobalGroupId;
            showingDatabaseContents = false;
            return;
        }

        if (!groups.Any(group => string.Equals(activeDatabaseGroupId, group.Id, StringComparison.OrdinalIgnoreCase)))
        {
            activeDatabaseGroupId = groups.FirstOrDefault(group => group.IsGlobal)?.Id ?? groups[0].Id;
            currentDatabaseFolder = string.Empty;
            showingDatabaseContents = false;
        }

        else if (showingDatabaseContents && !string.IsNullOrWhiteSpace(currentDatabaseFolder))
        {
            var folderStillExists = state.LibrarySnapshot.GroupFolders.Any(folder =>
                    string.Equals(folder.GroupId, activeDatabaseGroupId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(folder.Name, currentDatabaseFolder, StringComparison.Ordinal))
                || state.LibrarySnapshot.Items.Any(item =>
                    string.Equals(item.GroupId, activeDatabaseGroupId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Folder, currentDatabaseFolder, StringComparison.Ordinal));
            if (!folderStillExists) currentDatabaseFolder = string.Empty;
        }
    }

    private void EnsureManagerSelection()
    {
        if (state is null) return;
        var groupIds = state.LibrarySnapshot.Groups
            .Select(group => group.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        selectedManagerGroupIds.RemoveWhere(groupId => !groupIds.Contains(groupId));

        if (!groupIds.Contains(selectedGroupId))
        {
            selectedGroupId = state.LibrarySnapshot.Groups.FirstOrDefault(group => group.IsGlobal)?.Id
                ?? state.LibrarySnapshot.Groups.FirstOrDefault()?.Id
                ?? MacroLibraryStore.GlobalGroupId;
        }

        if (!managerSelectionInitialized && state.LibrarySnapshot.Groups.Count > 0)
        {
            selectedManagerGroupIds.Add(selectedGroupId);
            managerSelectionInitialized = true;
        }
    }

    private MacroLibraryGroup? GetActiveDatabaseGroup()
    {
        return state?.LibrarySnapshot.Groups.FirstOrDefault(group =>
            string.Equals(group.Id, activeDatabaseGroupId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsSelectedMacro(MacroLibraryTreeNode node)
    {
        return node.IsSelected || node.Children.Any(ContainsSelectedMacro);
    }

    private void SetGroupEditor(MacroLibraryGroup? group)
    {
        updatingGroupControls = true;
        try
        {
            GroupProcessFilterBox.Text = group?.ProcessFilter ?? string.Empty;
            GroupProcessFilterBox.IsEnabled = group is { IsGlobal: false };
            SelectProcessButton.IsEnabled = group is { IsGlobal: false };
            SelectProcessFileButton.IsEnabled = group is { IsGlobal: false };
            ApplyGroupButton.IsEnabled = group is { IsGlobal: false };
            DeleteDatabaseButton.IsEnabled = !showingDatabaseContents && group is { IsGlobal: false };
        }
        finally
        {
            updatingGroupControls = false;
        }
    }

    private void ApplyProgressiveViewState()
    {
        var managerVisibility = showingDatabaseContents ? Visibility.Collapsed : Visibility.Visible;
        var databaseVisibility = showingDatabaseContents ? Visibility.Visible : Visibility.Collapsed;
        ManagerOnlyControls.Visibility = managerVisibility;
        ManagerRuntimeControls.Visibility = managerVisibility;
        DatabaseOnlyControls.Visibility = databaseVisibility;
        DatabaseNavigationBar.Visibility = databaseVisibility;
        LibraryImportExportPanel.Visibility = databaseVisibility;
        MacroTreeView.Visibility = managerVisibility;
        ExplorerView.Visibility = databaseVisibility;
        MacroTreeView.AllowDrop = false;
        ExplorerListView.AllowDrop = showingDatabaseContents;
        UpFolderButton.IsEnabled = showingDatabaseContents;
        ImportMacroButton.IsEnabled = showingDatabaseContents;
        ExportMacroButton.IsEnabled = showingDatabaseContents;
        CopyMenuItem.Visibility = databaseVisibility;
        PasteMenuItem.Visibility = databaseVisibility;
        CurrentDatabaseTitleText.Text = GetDatabaseTitleText();
        DeleteMenuItem.Header = showingDatabaseContents ? L("Delete") : L("DeleteDatabase");
        DeleteDatabaseButton.IsEnabled = !showingDatabaseContents && GetSelectedEditableGroup() is not null;
        ApplyExplorerViewMode();
        UpdateExplorerSelectionStatus();
        UpdateManagerListeningButtons();
    }

    private void UpdateManagerListeningButtons()
    {
        var selectedCount = showingDatabaseContents ? 0 : selectedManagerGroupIds.Count;
        StartAllListeningButton.IsEnabled = selectedCount > 0;
        StopAllListeningButton.IsEnabled = selectedCount > 0;

        StartAllListeningButton.Content = selectedCount switch
        {
            0 => L("SelectDatabase"),
            1 => L("StartSelectedDatabaseListening"),
            _ => LF("StartSelectedDatabasesListening", selectedCount)
        };
        StopAllListeningButton.Content = selectedCount switch
        {
            0 => L("SelectDatabase"),
            1 => L("StopSelectedDatabaseListening"),
            _ => LF("StopSelectedDatabasesListening", selectedCount)
        };
    }

    private string GetDatabaseTitleText()
    {
        var group = GetActiveDatabaseGroup();
        if (group is null)
        {
            return L("MacroLibrary");
        }

        return string.IsNullOrWhiteSpace(currentDatabaseFolder)
            ? $"{L("MacroLibrary")} / {group.Name}"
            : $"{L("MacroLibrary")} / {group.Name} / {currentDatabaseFolder}";
    }

    private void MacroSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

    private void LibrarySortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MacroTreeView is not null) RefreshTree();
    }

    private void PrecisionModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        NotifyPrecisionSettingsEdited();
    }

    private void RuntimePrecision_TextChanged(object sender, TextChangedEventArgs e)
    {
        NotifyPrecisionSettingsEdited();
    }

    private void NotifyPrecisionSettingsEdited()
    {
        if (updatingRuntimePrecisionControls)
        {
            return;
        }

        PrecisionSettingsEdited?.Invoke();
    }

    private void MacroTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (suppressSelection) return;
        if (MacroTreeView.SelectedItem is not MacroLibraryTreeNode node) return;
        UpdateClipboardControls();
        selectedGroupId = node.GroupId;
        selectedFolder = node.IsFolder ? node.FolderName : node.Item?.Folder;
        SetGroupEditor(node.ProcessGroup ?? state!.LibrarySnapshot.Groups.FirstOrDefault(group => string.Equals(group.Id, selectedGroupId, StringComparison.OrdinalIgnoreCase)));
        if (node.Item is not { } item)
        {
            state!.SelectedMacroId = null;
            state.LibraryStore.SetSelected(null);
            return;
        }

        state!.SelectedMacroId = item.Id;
        state.LibraryStore.SetSelected(item.Id);
        MacroSelected?.Invoke(item.Id);
    }

    private void ExplorerListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSelection || state is null) return;
        UpdateClipboardControls();
        UpdateExplorerSelectionStatus();
        var node = e.AddedItems.OfType<MacroLibraryTreeNode>().LastOrDefault()
            ?? ExplorerListView.SelectedItem as MacroLibraryTreeNode;
        if (node is null)
        {
            selectedFolder = null;
            return;
        }

        selectedGroupId = node.GroupId;
        selectedFolder = node.IsFolder ? node.FolderName : node.Item?.Folder;
        if (node.Item is not { } item)
        {
            state.SelectedMacroId = null;
            state.LibraryStore.SetSelected(null);
            return;
        }

        state.SelectedMacroId = item.Id;
        state.LibraryStore.SetSelected(item.Id);
        MacroSelected?.Invoke(item.Id);
    }

    private void ExplorerListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var container = FindVisualParent<ListViewItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not MacroLibraryTreeNode node) return;
        OpenExplorerNode(node);
        e.Handled = true;
    }

    private void ExplorerListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        contextMenuTargetNode = null;
        var container = FindVisualParent<ListViewItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not MacroLibraryTreeNode node)
        {
            ExplorerListView.SelectedItems.Clear();
            return;
        }
        contextMenuTargetNode = node;
        if (!container.IsSelected)
        {
            ExplorerListView.SelectedItems.Clear();
            container.IsSelected = true;
        }

        container.Focus();
    }

    private void ExplorerContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedExplorerNodes();
        var kind = selected.Count == 0
            ? ExplorerContextMenuKind.Blank
            : selected.Count == 1 && selected[0].IsFolder
                ? ExplorerContextMenuKind.Folder
                : selected.All(node => node.Item is not null)
                    ? ExplorerContextMenuKind.Macro
                    : ExplorerContextMenuKind.Blank;

        ConfigureExplorerContextMenu(kind, selected);
    }

    private void ConfigureExplorerContextMenu(ExplorerContextMenuKind kind, IReadOnlyList<MacroLibraryTreeNode> selected)
    {
        var isMacro = kind == ExplorerContextMenuKind.Macro;
        var isFolder = kind == ExplorerContextMenuKind.Folder;
        var isBlank = kind == ExplorerContextMenuKind.Blank;

        ExplorerExportMacroMenuItem.Visibility = isMacro ? Visibility.Visible : Visibility.Collapsed;
        ExplorerExportFolderMenuItem.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
        ExplorerNewMenuItem.Visibility = isBlank ? Visibility.Visible : Visibility.Collapsed;
        ExplorerRenameMenuItem.Visibility = (isMacro || isFolder) ? Visibility.Visible : Visibility.Collapsed;
        ExplorerCopyMenuItem.Visibility = (isMacro || isFolder) ? Visibility.Visible : Visibility.Collapsed;
        ExplorerPasteMenuItem.Visibility = Visibility.Visible;
        ExplorerToggleLockMenuItem.Visibility = isMacro ? Visibility.Visible : Visibility.Collapsed;
        ExplorerViewMenuItem.Visibility = isBlank ? Visibility.Visible : Visibility.Collapsed;
        ExplorerSortMenuItem.Visibility = isBlank ? Visibility.Visible : Visibility.Collapsed;
        ExplorerRefreshMenuItem.Visibility = isBlank ? Visibility.Visible : Visibility.Collapsed;
        ExplorerDeleteMenuItem.Visibility = (isMacro || isFolder) ? Visibility.Visible : Visibility.Collapsed;

        ExplorerContextMenuPrimarySeparator.Visibility = Visibility.Visible;
        ExplorerContextMenuViewSeparator.Visibility = isBlank ? Visibility.Visible : Visibility.Collapsed;
        ExplorerContextMenuDeleteSeparator.Visibility = (isMacro || isFolder) ? Visibility.Visible : Visibility.Collapsed;

        ExplorerDeleteMenuItem.Header = isFolder ? L("DeleteMacroFolder") : L("Delete");

        var single = selected.Count == 1 ? selected[0] : null;
        var selectedMacros = selected.Where(node => node.Item is not null).ToList();
        ExplorerRenameMenuItem.IsEnabled = single is not null && single.Item?.IsLocked != true;
        ExplorerCopyMenuItem.IsEnabled = selected.Count > 0;
        ExplorerPasteMenuItem.IsEnabled = clipboard.Count > 0;
        ExplorerToggleLockMenuItem.IsEnabled = selectedMacros.Count > 0;
        ExplorerToggleLockMenuItem.Header = selectedMacros.Count > 0 && selectedMacros.All(node => node.Item!.IsLocked)
            ? L("UnlockSelected")
            : L("LockMacroFile");
        ExplorerDeleteMenuItem.IsEnabled = selected.Count > 0 && selected.Any(CanDeleteNode);
        ApplyExplorerViewMode();
    }

    private void ExportFolderMenuItem_Click(object sender, RoutedEventArgs e) =>
        BeginExport(BuildExportTarget(preferFolder: true));

    private void OpenExplorerNode(MacroLibraryTreeNode node)
    {
        if (node.IsFolder)
        {
            currentDatabaseFolder = node.FolderName;
            selectedFolder = null;
            state!.SelectedMacroId = null;
            state.LibraryStore.SetSelected(null);
            RefreshTree();
            return;
        }

        if (node.Item is { } item)
        {
            state!.SelectedMacroId = item.Id;
            state.LibraryStore.SetSelected(item.Id);
            MacroSelected?.Invoke(item.Id);
        }
    }

    private void ExplorerToggleLockMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        var macros = GetSelectedExplorerNodes()
            .Select(node => node.Item)
            .Where(item => item is not null)
            .Cast<MacroLibraryItem>()
            .ToList();
        if (macros.Count == 0) return;
        var lockMacros = macros.Any(item => !item.IsLocked);
        try
        {
            foreach (var item in macros)
            {
                state.LibraryStore.SetMacroLocked(item.Id, lockMacros);
                MacroLockChanged?.Invoke(item.Id, lockMacros);
            }

            RefreshTree();
            ResultMessage?.Invoke(lockMacros
                ? LF("SelectedMacrosLocked", macros.Count)
                : LF("SelectedMacrosUnlocked", macros.Count));
        }
        catch (Exception ex)
        {
            ResultMessage?.Invoke(ex.Message);
        }
    }

    private void ExplorerRefreshMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RefreshTree();
    }

    private void ExplorerViewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string mode })
        {
            SelectExplorerViewMode(mode);
        }
    }

    private void ExplorerSortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string mode })
        {
            SelectLibrarySortMode(mode);
        }
    }

    private void ExplorerListView_KeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Enter && modifiers == ModifierKeys.None)
        {
            if (ExplorerListView.SelectedItem is MacroLibraryTreeNode node) OpenExplorerNode(node);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && modifiers == ModifierKeys.None)
        {
            if (GetSelectedExplorerNodes().Count == 1) BeginRename(GetSelectedNode());
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && modifiers == ModifierKeys.None)
        {
            DeleteSelectedNodes();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.A)
        {
            ExplorerListView.SelectAll();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            CopySelectionToClipboard();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            PasteClipboard();
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.D)
        {
            DuplicateMacro_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            NewMacro_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            NewFolder_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Back && modifiers == ModifierKeys.None)
        {
            NavigateUpFromDatabase();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && modifiers == ModifierKeys.None)
        {
            RefreshTree();
            e.Handled = true;
        }
    }

    private void MacroTreeNode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MacroLibraryTreeNode node) return;

        if (!showingDatabaseContents && node.ProcessGroup is not null)
        {
            if (e.ClickCount == 2)
            {
                EnterDatabaseView(node.ProcessGroup.Id);
            }
            else
            {
                ToggleManagerGroupSelection(node, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            }

            e.Handled = true;
            return;
        }

        if (e.ClickCount != 2) return;
        BeginRename(node);
        e.Handled = true;
    }

    private void ToggleManagerGroupSelection(MacroLibraryTreeNode node, bool additive)
    {
        if (state is null || node.ProcessGroup is null) return;
        selectedGroupId = node.GroupId;
        selectedFolder = null;
        state.SelectedMacroId = null;
        state.LibraryStore.SetSelected(null);

        if (!additive)
        {
            selectedManagerGroupIds.Clear();
            selectedManagerGroupIds.Add(node.GroupId);
        }
        else if (!selectedManagerGroupIds.Remove(node.GroupId))
        {
            selectedManagerGroupIds.Add(node.GroupId);
        }

        SetGroupEditor(node.ProcessGroup);
        RefreshTree();
    }

    private void EnterDatabaseView(string groupId)
    {
        if (state is null) return;
        showingDatabaseContents = true;
        activeDatabaseGroupId = groupId;
        currentDatabaseFolder = string.Empty;
        selectedGroupId = groupId;
        selectedFolder = null;
        state.SelectedMacroId = null;
        state.LibraryStore.SetSelected(null);
        RefreshTree();
    }

    private void ReturnToManagerView()
    {
        showingDatabaseContents = false;
        currentDatabaseFolder = string.Empty;
        selectedFolder = null;
        state!.SelectedMacroId = null;
        state.LibraryStore.SetSelected(null);
        RefreshTree();
    }

    private void BackToManager_Click(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        ReturnToManagerView();
    }

    private void UpFolder_Click(object sender, RoutedEventArgs e)
    {
        NavigateUpFromDatabase();
    }

    private void NavigateUpFromDatabase()
    {
        if (!showingDatabaseContents) return;
        if (string.IsNullOrWhiteSpace(currentDatabaseFolder))
        {
            ReturnToManagerView();
            return;
        }

        currentDatabaseFolder = string.Empty;
        selectedFolder = null;
        state!.SelectedMacroId = null;
        state.LibraryStore.SetSelected(null);
        RefreshTree();
    }

    private void MacroTreeView_KeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.F2 && modifiers == ModifierKeys.None)
        {
            BeginRename(GetSelectedNode());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && modifiers == ModifierKeys.None)
        {
            DeleteNode(GetSelectedNode());
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            if (!showingDatabaseContents) return;
            CopySelectionToClipboard();
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            if (!showingDatabaseContents) return;
            PasteClipboard();
            e.Handled = true;
            return;
        }

        if (showingDatabaseContents && modifiers == ModifierKeys.Control && e.Key == Key.D)
        {
            DuplicateMacro_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (showingDatabaseContents && modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            NewMacro_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (showingDatabaseContents && modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            NewFolder_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void RenameTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        if (textBox.DataContext is not MacroLibraryTreeNode { IsRenaming: true }) return;

        textBox.Dispatcher.BeginInvoke(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        });
    }

    private void RenameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MacroLibraryTreeNode node) return;

        if (e.Key == Key.Enter)
        {
            QueueCommitRename(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename(node);
            e.Handled = true;
        }
    }

    private void RenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MacroLibraryTreeNode node) return;
        if (!node.IsRenaming) return;

        QueueCommitRename(node);
    }

    private void LibraryViewBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExplorerListView is null) return;
        ApplyExplorerViewMode();
    }

    private string GetExplorerViewMode()
    {
        return (LibraryViewBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "details";
    }

    private void ApplyExplorerViewMode()
    {
        if (ExplorerListView is null) return;
        var mode = GetExplorerViewMode();
        var templateKey = mode switch
        {
            "list" => "ExplorerListTemplate",
            "smallIcons" => "ExplorerSmallIconTemplate",
            "largeIcons" => "ExplorerLargeIconTemplate",
            _ => "ExplorerDetailsTemplate"
        };
        var panelKey = mode is "smallIcons" or "largeIcons" ? "ExplorerTilesPanel" : "ExplorerRowsPanel";
        ExplorerListView.ItemTemplate = (DataTemplate)Resources[templateKey];
        ExplorerListView.ItemsPanel = (ItemsPanelTemplate)Resources[panelKey];
        ExplorerDetailsHeader.Visibility = mode == "details" ? Visibility.Visible : Visibility.Collapsed;

        if (ExplorerDetailsMenuItem is null) return;
        ExplorerDetailsMenuItem.IsChecked = mode == "details";
        ExplorerListMenuItem.IsChecked = mode == "list";
        ExplorerSmallIconsMenuItem.IsChecked = mode == "smallIcons";
        ExplorerLargeIconsMenuItem.IsChecked = mode == "largeIcons";
    }

    private IReadOnlyList<MacroLibraryTreeNode> GetSelectedExplorerNodes()
    {
        return ExplorerListView.SelectedItems.OfType<MacroLibraryTreeNode>().ToList();
    }

    private IReadOnlySet<string> GetSelectedExplorerKeys()
    {
        return GetSelectedExplorerNodes()
            .Select(GetExplorerNodeKey)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string GetExplorerNodeKey(MacroLibraryTreeNode node)
    {
        return node.Item is { } item ? $"M:{item.Id}" : $"F:{node.GroupId}:{node.FolderName}";
    }

    private void RestoreExplorerSelection(
        IReadOnlyList<MacroLibraryTreeNode> nodes,
        IReadOnlySet<string> selectedKeys,
        string? selectedMacroId)
    {
        ExplorerListView.SelectedItems.Clear();
        foreach (var node in nodes)
        {
            if (selectedKeys.Contains(GetExplorerNodeKey(node))
                || (selectedKeys.Count == 0 && string.Equals(node.Item?.Id, selectedMacroId, StringComparison.Ordinal)))
            {
                ExplorerListView.SelectedItems.Add(node);
            }
        }
    }

    private void UpdateExplorerSelectionStatus()
    {
        if (ExplorerSelectionStatusText is null) return;
        var selectedCount = ExplorerListView?.SelectedItems.Count ?? 0;
        var totalCount = ExplorerListView?.Items.Count ?? 0;
        ExplorerSelectionStatusText.Text = selectedCount > 0
            ? LF("ExplorerSelectedCount", selectedCount, totalCount)
            : LF("ExplorerItemCount", totalCount);
    }

    private void QueueCommitRename(MacroLibraryTreeNode node)
    {
        if (renameCommitQueued) return;
        renameCommitQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            renameCommitQueued = false;
            if (node.IsRenaming && ReferenceEquals(renamingNode, node))
            {
                CommitRename(node);
            }
        }), DispatcherPriority.Input);
    }

    private void MacroTreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        contextMenuTargetNode = null;
        var treeViewItem = FindVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (treeViewItem is null) return;
        if (treeViewItem.DataContext is not MacroLibraryTreeNode node) return;

        contextMenuTargetNode = node;
        treeViewItem.Focus();
        treeViewItem.IsSelected = true;
    }

    private void MacroTreeContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var node = contextMenuTargetNode ?? GetSelectedNode();
        RenameMenuItem.IsEnabled = node is not null && node.Item?.IsLocked != true;
        CopyMenuItem.IsEnabled = showingDatabaseContents && node is not null && !node.IsGroup;
        DeleteMenuItem.IsEnabled = CanDeleteNode(node);
    }

    private void MacroTreeContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            () => contextMenuTargetNode = null,
            DispatcherPriority.ContextIdle);
    }

    private static bool CanDeleteNode(MacroLibraryTreeNode? node)
    {
        return node switch
        {
            null => false,
            { IsGroup: true, ProcessGroup.IsGlobal: true } => false,
            { Item.IsLocked: true } => false,
            _ => true
        };
    }

    private void MacroTreeView_Loaded(object sender, RoutedEventArgs e)
    {
        macroTreeScrollViewer = FindVisualChild<ScrollViewer>(MacroTreeView);
        macroTreeHwndSource = HwndSource.FromVisual(MacroTreeView) as HwndSource;
        macroTreeHwndSource?.RemoveHook(MacroTreeWndProc);
        macroTreeHwndSource?.AddHook(MacroTreeWndProc);
    }

    private void ExplorerListView_Loaded(object sender, RoutedEventArgs e)
    {
        explorerScrollViewer = FindVisualChild<ScrollViewer>(ExplorerListView);
        explorerHorizontalScrollTarget = explorerScrollViewer?.HorizontalOffset ?? 0;
    }

    private void MacroTreeView_Unloaded(object sender, RoutedEventArgs e)
    {
        StopLibraryDragWheelHook();
        macroTreeHwndSource?.RemoveHook(MacroTreeWndProc);
        macroTreeHwndSource = null;
        macroTreeScrollViewer = null;
        explorerScrollViewer = null;
        explorerHorizontalScrollTimer.Stop();
    }

    private void ExplorerListView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            return;
        }

        e.Handled = QueueExplorerHorizontalScroll(-e.Delta / WheelDelta * 72.0);
    }

    private bool QueueExplorerHorizontalScroll(double pixelDelta)
    {
        explorerScrollViewer ??= FindVisualChild<ScrollViewer>(ExplorerListView);
        if (explorerScrollViewer is null)
        {
            return false;
        }

        var maximum = Math.Max(0, explorerScrollViewer.ExtentWidth - explorerScrollViewer.ViewportWidth);
        if (maximum <= 0.5)
        {
            return false;
        }

        var start = explorerHorizontalScrollTimer.IsEnabled
            ? explorerHorizontalScrollTarget
            : explorerScrollViewer.HorizontalOffset;
        explorerHorizontalScrollTarget = Math.Clamp(start + pixelDelta, 0, maximum);
        explorerHorizontalScrollTimer.Start();
        return true;
    }

    private void ExplorerHorizontalScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (explorerScrollViewer is null)
        {
            explorerHorizontalScrollTimer.Stop();
            return;
        }

        var maximum = Math.Max(0, explorerScrollViewer.ExtentWidth - explorerScrollViewer.ViewportWidth);
        explorerHorizontalScrollTarget = Math.Clamp(explorerHorizontalScrollTarget, 0, maximum);
        var remaining = explorerHorizontalScrollTarget - explorerScrollViewer.HorizontalOffset;
        if (Math.Abs(remaining) <= 0.5)
        {
            explorerScrollViewer.ScrollToHorizontalOffset(explorerHorizontalScrollTarget);
            explorerHorizontalScrollTimer.Stop();
            return;
        }

        explorerScrollViewer.ScrollToHorizontalOffset(explorerScrollViewer.HorizontalOffset + remaining * 0.34);
    }

    private void MacroTreeView_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!showingDatabaseContents) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var source = e.OriginalSource as DependencyObject;
        if (IsScrollbarDragSource(source)) return;
        var treeViewItem = FindVisualParent<TreeViewItem>(source);
        if (treeViewItem is null) return;
        if (treeViewItem.DataContext is not MacroLibraryTreeNode { Item: { } item }) return;

        try
        {
            libraryDragInProgress = true;
            StartLibraryDragWheelHook();
            DragDrop.DoDragDrop(MacroTreeView, new DataObject(MacroLibraryDragFormat, item.Id), DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            StopLibraryDragWheelHook();
            ClearMacroDropIndicator();
            libraryDragInProgress = false;
        }
    }

    private void MacroTreeView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!libraryDragInProgress)
        {
            return;
        }

        ScrollMacroTreeByWheelDelta(e.Delta);
        e.Handled = true;
    }

    private void MacroTreeView_DragOver(object sender, DragEventArgs e)
    {
        if (!showingDatabaseContents)
        {
            ClearMacroDropIndicator();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (!e.Data.GetDataPresent(MacroLibraryDragFormat))
        {
            ClearMacroDropIndicator();
            e.Effects = DragDropEffects.None;
            return;
        }

        AutoScrollMacroTree(e.GetPosition(MacroTreeView));
        if (e.Data.GetData(MacroLibraryDragFormat) is not string macroId)
        {
            ClearMacroDropIndicator();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var target = GetMacroDropTarget(e.GetPosition(MacroTreeView), macroId);
        SetMacroDropIndicator(target);
        e.Effects = target.IsNoOp ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private void MacroTreeView_DragLeave(object sender, DragEventArgs e)
    {
        ClearMacroDropIndicator();
    }

    private IntPtr MacroTreeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEHWHEEL && showingDatabaseContents && IsScreenPointInsideExplorer(lParam))
        {
            handled = QueueExplorerHorizontalScroll(GetWheelDelta(wParam) / WheelDelta * 72.0);
        }

        if (msg == WM_MOUSEWHEEL && libraryDragInProgress && IsScreenPointInsideMacroTree(lParam))
        {
            ScrollMacroTreeByWheelDelta(GetWheelDelta(wParam));
            handled = true;
        }

        return IntPtr.Zero;
    }

    private bool IsScreenPointInsideExplorer(IntPtr lParam)
    {
        if (!ExplorerListView.IsLoaded || !ExplorerListView.IsVisible)
        {
            return false;
        }

        var packed = lParam.ToInt64();
        var screenPoint = new Point(
            unchecked((short)(packed & 0xFFFF)),
            unchecked((short)((packed >> 16) & 0xFFFF)));
        var localPoint = ExplorerListView.PointFromScreen(screenPoint);
        return localPoint.X >= 0
            && localPoint.Y >= 0
            && localPoint.X <= ExplorerListView.ActualWidth
            && localPoint.Y <= ExplorerListView.ActualHeight;
    }

    private void StartLibraryDragWheelHook()
    {
        if (libraryDragMouseHookHandle != IntPtr.Zero)
        {
            return;
        }

        libraryDragMouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, libraryDragMouseHookProc, IntPtr.Zero, 0);
    }

    private void StopLibraryDragWheelHook()
    {
        if (libraryDragMouseHookHandle == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(libraryDragMouseHookHandle);
        libraryDragMouseHookHandle = IntPtr.Zero;
    }

    private IntPtr LibraryDragMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0
            && wParam.ToInt32() == WM_MOUSEWHEEL_LOW_LEVEL
            && libraryDragInProgress
            && libraryDragMouseHookHandle != IntPtr.Zero)
        {
            var data = Marshal.PtrToStructure<MouseLowLevelHookStruct>(lParam);
            if (IsScreenPointInsideMacroTree(data.Point.X, data.Point.Y))
            {
                var delta = unchecked((short)((data.MouseData >> 16) & 0xFFFF));
                Dispatcher.BeginInvoke(new Action(() => ScrollMacroTreeByWheelDelta(delta)), DispatcherPriority.Input);
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(libraryDragMouseHookHandle, nCode, wParam, lParam);
    }

    private static bool IsScrollbarDragSource(DependencyObject? source)
    {
        return FindVisualParent<ScrollBar>(source) is not null
            || FindVisualParent<Thumb>(source) is not null
            || FindVisualParent<Track>(source) is not null;
    }

    private bool IsScreenPointInsideMacroTree(IntPtr lParam)
    {
        if (!MacroTreeView.IsLoaded)
        {
            return false;
        }

        var packed = lParam.ToInt64();
        var screenPoint = new Point(
            unchecked((short)(packed & 0xFFFF)),
            unchecked((short)((packed >> 16) & 0xFFFF)));
        return IsScreenPointInsideMacroTree(screenPoint.X, screenPoint.Y);
    }

    private bool IsScreenPointInsideMacroTree(double screenX, double screenY)
    {
        if (!MacroTreeView.IsLoaded)
        {
            return false;
        }

        var screenPoint = new Point(screenX, screenY);
        var treePoint = MacroTreeView.PointFromScreen(screenPoint);
        return treePoint.X >= 0
            && treePoint.Y >= 0
            && treePoint.X <= MacroTreeView.ActualWidth
            && treePoint.Y <= MacroTreeView.ActualHeight;
    }

    private static int GetWheelDelta(IntPtr wParam)
    {
        return unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
    }

    private void AutoScrollMacroTree(Point position)
    {
        var scrollViewer = GetMacroTreeScrollViewer();
        if (scrollViewer is null)
        {
            return;
        }

        const double edgeSize = 20;
        var now = Environment.TickCount64;
        if (now - lastLibraryAutoScrollTick < LibraryAutoScrollIntervalMilliseconds)
        {
            return;
        }

        if (position.Y < edgeSize)
        {
            lastLibraryAutoScrollTick = now;
            scrollViewer.LineUp();
        }
        else if (position.Y > MacroTreeView.ActualHeight - edgeSize)
        {
            lastLibraryAutoScrollTick = now;
            scrollViewer.LineDown();
        }
        else
        {
            lastLibraryAutoScrollTick = 0;
        }
    }

    private void ScrollMacroTreeByWheelDelta(int delta)
    {
        var scrollViewer = GetMacroTreeScrollViewer();
        if (scrollViewer is null)
        {
            return;
        }

        var lines = SystemParameters.WheelScrollLines <= 0 ? 3 : SystemParameters.WheelScrollLines;
        var offsetDelta = -(delta / WheelDelta) * lines;
        var target = Math.Clamp(scrollViewer.VerticalOffset + offsetDelta, 0, scrollViewer.ScrollableHeight);
        scrollViewer.ScrollToVerticalOffset(target);
    }

    private void MacroTreeView_Drop(object sender, DragEventArgs e)
    {
        if (state is null) return;
        if (!showingDatabaseContents) return;
        if (!e.Data.GetDataPresent(MacroLibraryDragFormat)) return;
        if (e.Data.GetData(MacroLibraryDragFormat) is not string macroId) return;

        var target = GetMacroDropTarget(e.GetPosition(MacroTreeView), macroId);
        if (target.IsNoOp)
        {
            ClearMacroDropIndicator();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        try
        {
            ClearMacroDropIndicator();
            state.LibraryStore.MoveMacro(macroId, target.Folder, target.BeforeMacroId, target.GroupId);
            state.SelectedMacroId = macroId;
            selectedGroupId = target.GroupId;
            selectedFolder = target.Folder;
            SelectLibrarySortMode("manual");
            RefreshTree();
            LibraryStructureEdited?.Invoke();
            ResultMessage?.Invoke(string.IsNullOrWhiteSpace(target.Folder)
                ? L("MacroMovedToRoot")
                : LF("MacroMovedToFolder", target.Folder));
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            ResultMessage?.Invoke(ex.Message);
        }
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        var group = state.LibraryStore.CreateGroup(NextGroupName(), string.Empty);
        selectedGroupId = group.Id;
        activeDatabaseGroupId = group.Id;
        selectedManagerGroupIds.Clear();
        selectedManagerGroupIds.Add(group.Id);
        showingDatabaseContents = false;
        selectedFolder = null;
        state.SelectedMacroId = null;
        expandedGroups.Add(group.Id);
        RefreshTree();
        LibraryStructureEdited?.Invoke();
    }

    private void DeleteDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        if (GetSelectedEditableGroup() is not { } group)
        {
            ResultMessage?.Invoke(L("SelectDatabaseToDelete"));
            return;
        }

        DeleteGroupWithConfirmation(group);
    }

    private void ApplyGroup_Click(object sender, RoutedEventArgs e)
    {
        if (state is null || updatingGroupControls) return;
        ApplySelectedGroupProcessFilter(GroupProcessFilterBox.Text);
    }

    private void SelectProcess_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedEditableGroup() is null) return;

        var menu = new ContextMenu();
        foreach (var process in EnumerateRunningProcesses())
        {
            var item = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(process.WindowTitle)
                    ? process.ProcessName
                    : $"{process.ProcessName} - {process.WindowTitle}",
                Tag = process.ProcessName
            };
            item.Click += (_, _) => ApplySelectedGroupProcessFilter(process.ProcessName);
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            ResultMessage?.Invoke(L("NoProcessesFound"));
            return;
        }

        menu.PlacementTarget = SelectProcessButton;
        menu.IsOpen = true;
    }

    private void SelectProcessFile_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedEditableGroup() is null) return;

        var dialog = new OpenFileDialog
        {
            Filter = L("ProcessExecutableFileFilter"),
            Title = L("SelectProcessFileTitle")
        };

        if (DialogOwnerService.ShowDialogSafe(dialog, this) != true) return;

        var processName = Path.GetFileName(dialog.FileName);
        ApplySelectedGroupProcessFilter(processName);
    }

    private void ApplySelectedGroupProcessFilter(string processFilter)
    {
        if (state is null || GetSelectedEditableGroup() is not { } group) return;
        try
        {
            GroupProcessFilterBox.Text = processFilter.Trim();
            state.LibraryStore.UpdateGroup(group.Id, group.Name, GroupProcessFilterBox.Text);
            RefreshTree();
            LibraryStructureEdited?.Invoke();
            ResultMessage?.Invoke(LF("GroupUpdated", group.Name));
        }
        catch (Exception ex)
        {
            ResultMessage?.Invoke(ex.Message);
        }
    }

    private MacroLibraryGroup? GetSelectedEditableGroup()
    {
        if (state is null) return null;
        var group = state.LibraryStore.Load().Groups.FirstOrDefault(group => string.Equals(group.Id, selectedGroupId, StringComparison.OrdinalIgnoreCase));
        return group is { IsGlobal: false } ? group : null;
    }

    private static IEnumerable<RunningProcessChoice> EnumerateRunningProcesses()
    {
        return Process.GetProcesses()
            .Select(TryCreateRunningProcessChoice)
            .Where(choice => choice is not null)
            .Cast<RunningProcessChoice>()
            .GroupBy(choice => choice.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(choice => !string.IsNullOrWhiteSpace(choice.WindowTitle)).First())
            .OrderBy(choice => choice.ProcessName, StringComparer.CurrentCultureIgnoreCase);
    }

    private static RunningProcessChoice? TryCreateRunningProcessChoice(Process process)
    {
        try
        {
            var processName = $"{process.ProcessName}.exe";
            var title = process.MainWindowTitle;
            return string.IsNullOrWhiteSpace(processName)
                ? null
                : new RunningProcessChoice(processName, title);
        }
        catch
        {
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    private void NewMacroMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (NewMacroContextMenu is null)
        {
            return;
        }

        NewMacroContextMenu.PlacementTarget = NewMacroMenuButton;
        NewMacroContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        NewMacroContextMenu.IsOpen = true;
    }

    private void NewMacro_Click(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        var name = NextMacroName("Macro");
        var item = state.LibraryStore.CreateMacro(name, GetCurrentFolder(), groupId: GetCurrentGroupId());
        state.SelectedMacroId = item.Id;
        selectedGroupId = item.GroupId;
        RefreshTree();
        MacroCreated?.Invoke(item);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        var folder = NextFolderName();
        state.LibraryStore.CreateFolder(folder, GetCurrentGroupId());
        state.SelectedMacroId = null;
        selectedFolder = folder;
        RefreshTree();
    }

    private void CopyMacro_Click(object sender, RoutedEventArgs e) => CopySelectionToClipboard();

    private void PasteMacro_Click(object sender, RoutedEventArgs e) => PasteClipboard();

    private void RenameMenuItem_Click(object sender, RoutedEventArgs e) => BeginRename(GetSelectedNode());

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e) => CopySelectionToClipboard();

    private void PasteMenuItem_Click(object sender, RoutedEventArgs e) => PasteClipboard();

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (showingDatabaseContents)
        {
            DeleteSelectedNodes();
            return;
        }

        DeleteNode(contextMenuTargetNode ?? GetSelectedNode());
    }

    private void ExplorerListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        explorerDragStartPoint = e.GetPosition(ExplorerListView);
        if (FindVisualParent<ListViewItem>(e.OriginalSource as DependencyObject) is null
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            ExplorerListView.SelectedItems.Clear();
        }
    }

    private void ExplorerListView_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || explorerDragStartPoint is not { } start)
        {
            return;
        }

        var current = e.GetPosition(ExplorerListView);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        explorerDragStartPoint = null;
        var sourceItem = FindVisualParent<ListViewItem>(e.OriginalSource as DependencyObject);
        if (sourceItem?.DataContext is not MacroLibraryTreeNode { Item: not null } sourceNode) return;
        if (!sourceItem.IsSelected)
        {
            ExplorerListView.SelectedItems.Clear();
            sourceItem.IsSelected = true;
        }

        var macroIds = GetSelectedExplorerNodes()
            .Select(node => node.Item?.Id)
            .Where(id => id is not null)
            .Cast<string>()
            .ToArray();
        if (macroIds.Length == 0) return;

        DragDrop.DoDragDrop(
            ExplorerListView,
            new DataObject(MacroLibraryDragFormat, macroIds),
            DragDropEffects.Move);
    }

    private void ExplorerListView_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(MacroLibraryDragFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ExplorerListView_Drop(object sender, DragEventArgs e)
    {
        if (state is null || e.Data.GetData(MacroLibraryDragFormat) is not string[] macroIds || macroIds.Length == 0)
        {
            return;
        }

        var targetContainer = FindVisualParent<ListViewItem>(e.OriginalSource as DependencyObject);
        var targetNode = targetContainer?.DataContext as MacroLibraryTreeNode;
        var targetFolder = targetNode?.IsFolder == true ? targetNode.FolderName : currentDatabaseFolder;
        var beforeMacroId = targetNode?.Item?.Id;
        try
        {
            foreach (var macroId in macroIds)
            {
                if (string.Equals(macroId, beforeMacroId, StringComparison.Ordinal)) continue;
                state.LibraryStore.MoveMacro(macroId, targetFolder, beforeMacroId, activeDatabaseGroupId);
                beforeMacroId = null;
            }

            selectedFolder = targetFolder;
            SelectLibrarySortMode("manual");
            RefreshTree();
            LibraryStructureEdited?.Invoke();
            ResultMessage?.Invoke(LF("SelectedItemsMoved", macroIds.Length));
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            ResultMessage?.Invoke(ex.Message);
        }
    }

    private void DuplicateMacro_Click(object sender, RoutedEventArgs e)
    {
        var macroIds = showingDatabaseContents
            ? GetSelectedExplorerNodes().Select(node => node.Item?.Id).Where(id => id is not null).Cast<string>().ToList()
            : state?.SelectedMacroId is { } id ? [id] : [];
        foreach (var macroId in macroIds)
        {
            MacroDuplicated?.Invoke(macroId);
        }
    }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedNodes();
    }

    private void DeleteSelectedNodes()
    {
        var selected = showingDatabaseContents
            ? GetSelectedExplorerNodes()
            : GetSelectedNode() is { } node ? [node] : [];
        if (selected.Count <= 1)
        {
            DeleteNode(selected.FirstOrDefault() ?? FindMacroNode(state?.SelectedMacroId));
            return;
        }

        var requests = selected
            .Where(node => !node.IsGroup)
            .Select(node => new MacroLibraryDeleteItem(
                node.Item?.Id,
                node.GroupId,
                node.FolderName,
                node.Title,
                node.IsFolder,
                node.Item?.IsLocked == true))
            .ToList();
        if (requests.Count == 0)
        {
            ResultMessage?.Invoke(L("SelectItemToDelete"));
            return;
        }

        LibraryItemsDeleteRequested?.Invoke(requests);
    }

    private void DeleteNode(MacroLibraryTreeNode? node)
    {
        if (state is null) return;
        if (node is null)
        {
            ResultMessage?.Invoke(L("SelectItemToDelete"));
            return;
        }

        if (node is { IsGroup: true, ProcessGroup: { } group })
        {
            if (group.IsGlobal)
            {
                ResultMessage?.Invoke(L("GlobalDatabaseCannotDelete"));
                return;
            }

            DeleteGroupWithConfirmation(group);
            return;
        }

        if (node is { IsFolder: true } folder)
        {
            if (!ConfirmDelete(LF("DeleteFolderConfirm", folder.FolderName), L("Delete"))) return;

            try
            {
                state.LibraryStore.DeleteFolder(folder.FolderName, deleteMacros: false, folder.GroupId);
                state.SelectedMacroId = null;
                selectedFolder = null;
                RefreshTree();
                LibraryStructureEdited?.Invoke();
                ResultMessage?.Invoke(LF("FolderDeleted", folder.FolderName));
            }
            catch (Exception ex)
            {
                ReportDeleteFailure(ex);
            }

            return;
        }

        if (node.Item is { } item)
        {
            if (item.IsLocked)
            {
                ResultMessage?.Invoke(L("MacroLockedReadOnly"));
                return;
            }

            MacroDeleted?.Invoke(item.Id);
            return;
        }

        ResultMessage?.Invoke(L("SelectItemToDelete"));
    }

    private void DeleteGroupWithConfirmation(MacroLibraryGroup group)
    {
        if (state is null) return;
        if (!ConfirmDelete(LF("DeleteDatabaseConfirm", group.Name), L("DeleteDatabase"))) return;

        try
        {
            state.LibraryStore.DeleteGroup(group.Id);
            state.SelectedMacroId = null;
            selectedManagerGroupIds.Remove(group.Id);
            selectedManagerGroupIds.Clear();
            selectedManagerGroupIds.Add(MacroLibraryStore.GlobalGroupId);
            selectedGroupId = MacroLibraryStore.GlobalGroupId;
            activeDatabaseGroupId = MacroLibraryStore.GlobalGroupId;
            selectedFolder = null;
            showingDatabaseContents = false;
            RefreshTree();
            LibraryStructureEdited?.Invoke();
            ResultMessage?.Invoke(LF("GroupDeleted", group.Name));
        }
        catch (Exception ex)
        {
            ReportDeleteFailure(ex);
        }
    }

    private bool ConfirmDelete(string message, string caption)
    {
        return DialogOwnerService.MessageBoxSafe(
            this,
            message,
            caption,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void ReportDeleteFailure(Exception exception)
    {
        var message = LF("DeleteFailed", exception.Message);
        ResultMessage?.Invoke(message);
        DialogOwnerService.MessageBoxSafe(
            this,
            message,
            L("Delete"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private MacroLibraryTreeNode? FindMacroNode(string? macroId)
    {
        if (string.IsNullOrWhiteSpace(macroId)
            || MacroTreeView.ItemsSource is not IEnumerable<MacroLibraryTreeNode> nodes)
        {
            return null;
        }

        return FindMacroNode(nodes, macroId);
    }

    private static MacroLibraryTreeNode? FindMacroNode(IEnumerable<MacroLibraryTreeNode> nodes, string macroId)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.Item?.Id, macroId, StringComparison.Ordinal)) return node;
            var childMatch = FindMacroNode(node.Children, macroId);
            if (childMatch is not null) return childMatch;
        }

        return null;
    }

    private void StartAllListening_Click(object sender, RoutedEventArgs e)
    {
        var groupIds = GetSelectedManagerGroupIds();
        if (groupIds.Count == 0) return;
        StartListeningGroupsRequested?.Invoke(groupIds);
    }

    private void StopAllListening_Click(object sender, RoutedEventArgs e)
    {
        var groupIds = GetSelectedManagerGroupIds();
        if (groupIds.Count == 0) return;
        StopListeningGroupsRequested?.Invoke(groupIds);
    }

    private void StopEveryListening_Click(object sender, RoutedEventArgs e)
    {
        StopListeningAllRequested?.Invoke();
    }

    public IReadOnlyList<string> GetSelectedManagerGroupIds()
    {
        return showingDatabaseContents
            ? []
            : selectedManagerGroupIds.ToList();
    }

    private void RefreshConversionText()
    {
        ConversionText.Text = L("ConversionAutoDetectHelp");
    }

    private void ImportMacro_Click(object sender, RoutedEventArgs e)
    {
        if (state is null || !showingDatabaseContents) return;
        var dialog = new OpenFileDialog
        {
            Filter = L("ConverterImportFileFilter"),
            Title = L("ImportMacroTitle"),
            Multiselect = true
        };

        if (DialogOwnerService.ShowDialogSafe(dialog, this) != true) return;

        var selectedFiles = new List<SmartImportFile>();
        var failureMessages = new List<string>();
        foreach (var fileName in dialog.FileNames)
        {
            try
            {
                var content = File.ReadAllText(fileName);
                var format = MacroConversionService.DetectFormat(content, fileName);
                MacroConversionService.TryGetRazerMacroGuid(content, out var razerGuid);
                selectedFiles.Add(new SmartImportFile(
                    Path.GetFullPath(fileName),
                    Path.GetFileName(fileName),
                    content,
                    format,
                    string.IsNullOrWhiteSpace(razerGuid) ? null : razerGuid));
            }
            catch (Exception ex)
            {
                failureMessages.Add(FormatImportFailure(Path.GetFileName(fileName), ex));
            }
        }

        var auxiliaryFiles = BuildSmartImportCatalog(selectedFiles);
        var modulesByGuid = auxiliaryFiles
            .Select(file => MacroConversionService.TryGetRazerMacroGuid(file.Content, out var guid)
                ? (Guid: guid, File: file)
                : (Guid: string.Empty, File: file))
            .Where(item => !string.IsNullOrWhiteSpace(item.Guid))
            .GroupBy(item => item.Guid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().File, StringComparer.OrdinalIgnoreCase);
        var referencedModuleGuids = ResolveReferencedRazerModuleClosure(selectedFiles, modulesByGuid);
        var selectedModuleGuids = selectedFiles
            .Where(file => file.RazerGuid is not null && referencedModuleGuids.Contains(file.RazerGuid))
            .Select(file => file.RazerGuid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var importedCount = 0;
        var automaticallyDetectedCount = 0;
        var diagnosticLines = new List<string>();
        string? singleSourceFormat = null;
        var singleStepCount = 0;
        MacroLibraryItem? lastImportedMacro = null;
        var pendingDocuments = new List<MacroDocument>();

        foreach (var guid in referencedModuleGuids)
        {
            if (!modulesByGuid.TryGetValue(guid, out var module)) continue;
            try
            {
                var imported = ImportRazerWithNestedCalls(module, auxiliaryFiles);
                lastImportedMacro = StoreRazerMacro(imported, module.Content);
                importedCount++;
                if (!selectedModuleGuids.Contains(guid)) automaticallyDetectedCount++;
                diagnosticLines.Add($"{module.FileName}: {FormatDiagnostics(imported.Diagnostics)}");
            }
            catch (Exception ex)
            {
                failureMessages.Add(FormatImportFailure(module.FileName, ex));
            }
        }

        foreach (var file in selectedFiles.Where(file => file.RazerGuid is null || !referencedModuleGuids.Contains(file.RazerGuid)))
        {
            try
            {
                var import = file.Format == MacroConversionFormat.RazerSynapseXml
                    ? ImportRazerWithNestedCalls(new AuxiliaryMacroFile(file.DisplayName, file.Content), auxiliaryFiles)
                    : MacroConversionService.ImportToMcrx(new MacroImportRequest(
                        file.Content,
                        file.FullPath,
                        file.Format,
                        auxiliaryFiles));
                if (file.Format == MacroConversionFormat.RazerSynapseXml)
                {
                    lastImportedMacro = StoreRazerMacro(import, file.Content);
                }
                else
                {
                    pendingDocuments.Add(import.Document);
                }

                importedCount++;
                singleSourceFormat = import.SourceFormat.ToString();
                singleStepCount = import.Document.Steps.Count;
                diagnosticLines.Add($"{file.DisplayName}: {FormatDiagnostics(import.Diagnostics)}");
            }
            catch (Exception ex)
            {
                failureMessages.Add(FormatImportFailure(file.DisplayName, ex));
            }
        }

        if (pendingDocuments.Count > 0)
        {
            var importedItems = state.LibraryStore.ImportMacros(
                pendingDocuments,
                GetCurrentFolder(),
                CurrentDatabaseGroupId);
            if (importedItems.Count > 0)
            {
                lastImportedMacro = importedItems[^1];
                ImportApplied?.Invoke(state.LibraryStore.ReadMacro(lastImportedMacro.Id));
            }
        }

        if (lastImportedMacro is not null)
        {
            state.SelectedMacroId = lastImportedMacro.Id;
            RefreshTree();
            MacroSelected?.Invoke(lastImportedMacro.Id);
        }

        var message = selectedFiles.Count == 1 && importedCount == 1 && automaticallyDetectedCount == 0
            ? LF("ConversionImported", singleSourceFormat ?? L("Macro"), singleStepCount)
            : LF("ConversionSmartImported", importedCount, selectedFiles.Count, automaticallyDetectedCount);
        var details = diagnosticLines.Concat(failureMessages).ToList();
        ConversionText.Text = details.Count == 0
            ? message
            : message + Environment.NewLine + string.Join(Environment.NewLine, details);
        ResultMessage?.Invoke(message);

        if (failureMessages.Count > 0)
        {
            var failureSummary = LF("ConversionBatchImportFailed", failureMessages.Count);
            ResultMessage?.Invoke(failureSummary);
            DialogOwnerService.MessageBoxSafe(
                this,
                failureSummary + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, failureMessages),
                L("ConversionImportErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private MacroImportResult ImportRazerWithNestedCalls(
        AuxiliaryMacroFile file,
        IReadOnlyList<AuxiliaryMacroFile> auxiliaryFiles)
    {
        var referencedMacros = auxiliaryFiles
            .Where(candidate => MacroConversionService.TryGetRazerMacroGuid(candidate.Content, out _))
            .ToList();
        return MacroConversionService.ImportToMcrx(new MacroImportRequest(
            file.Content,
            file.FileName,
            MacroConversionFormat.RazerSynapseXml,
            referencedMacros,
            PreserveRazerModuleCalls: true));
    }

    private MacroLibraryItem StoreRazerMacro(MacroImportResult imported, string content)
    {
        IReadOnlyList<string> aliases = MacroConversionService.TryGetRazerMacroGuid(content, out var guid)
            ? [guid]
            : [];
        var targetGroupId = CurrentDatabaseGroupId;
        var existing = state!.LibraryStore.Load().Items.FirstOrDefault(item =>
            string.Equals(item.GroupId, targetGroupId, StringComparison.OrdinalIgnoreCase)
            && (item.MatchesReference(guid) || string.Equals(item.Name, imported.Document.Name, StringComparison.CurrentCultureIgnoreCase)));
        return existing is not null
            ? state.LibraryStore.AddAliasesToMacro(existing.Id, aliases)
            : state.LibraryStore.CreateMacro(imported.Document, aliases: aliases, groupId: targetGroupId);
    }

    private static IReadOnlyList<AuxiliaryMacroFile> BuildSmartImportCatalog(IReadOnlyList<SmartImportFile> selectedFiles)
    {
        var files = selectedFiles
            .Select(file => new AuxiliaryMacroFile(file.DisplayName, file.Content))
            .ToList();
        foreach (var selectedFile in selectedFiles)
        {
            files.AddRange(LoadConversionAuxiliaryFiles(selectedFile.FullPath));
        }

        return files
            .DistinctBy(file => $"{file.FileName}\n{file.Content}", StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlySet<string> ResolveReferencedRazerModuleClosure(
        IReadOnlyList<SmartImportFile> selectedFiles,
        IReadOnlyDictionary<string, AuxiliaryMacroFile> modulesByGuid)
    {
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(selectedFiles
            .Where(file => file.Format == MacroConversionFormat.RazerSynapseXml)
            .SelectMany(file => MacroConversionService.GetRazerModuleReferences(file.Content))
            .Select(reference => reference.Guid));
        while (pending.TryDequeue(out var guid))
        {
            if (!resolved.Add(guid) || !modulesByGuid.TryGetValue(guid, out var module)) continue;
            foreach (var nested in MacroConversionService.GetRazerModuleReferences(module.Content))
            {
                pending.Enqueue(nested.Guid);
            }
        }

        return resolved;
    }

    private static IReadOnlyList<AuxiliaryMacroFile> LoadConversionAuxiliaryFiles(string selectedFileName)
    {
        var directory = Path.GetDirectoryName(selectedFileName);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var selectedFullPath = Path.GetFullPath(selectedFileName);
        var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".json",
            ".mcrx",
            ".xml",
            ".lua",
            ".xmbcs",
            ".mq",
            ".txt"
        };
        var files = new List<AuxiliaryMacroFile>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(file), selectedFullPath, StringComparison.OrdinalIgnoreCase)
                || !supportedExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            try
            {
                files.Add(new AuxiliaryMacroFile(Path.GetRelativePath(directory, file), File.ReadAllText(file)));
            }
            catch
            {
                // Auxiliary files are best-effort; the selected macro should still import if one neighbor is unreadable.
            }
        }

        return files;
    }

    private void ExportMacro_Click(object sender, RoutedEventArgs e) =>
        BeginExport(BuildExportTarget(preferFolder: false));

    private void BeginExport(ExportTarget target)
    {
        if (target.IsEmpty)
        {
            ResultMessage?.Invoke(L("ExportNothingSelected"));
            return;
        }

        try
        {
            MacroLibraryExportBundle? itemBundle = null;
            var showPackaging = target.IsFolder;
            if (!target.IsFolder && state is not null)
            {
                itemBundle = BuildItemExportBundle(target, state.LibraryStore.Load());
                showPackaging = itemBundle.Dependencies.Count > 0;
            }

            var wizard = new ExportWizardDialog(showPackagingStep: showPackaging);
            if (DialogOwnerService.ShowDialogSafe(wizard, this) != true || wizard.Result is null)
                return;
            ExecuteExport(target, wizard.Result, itemBundle);
        }
        catch (Exception ex)
        {
            var message = LF("ConversionExportFailed", ex.Message);
            ConversionText.Text = message;
            ResultMessage?.Invoke(ex.Message);
        }
    }

    private ExportTarget BuildExportTarget(bool preferFolder)
    {
        if (state is null || !showingDatabaseContents)
            return ExportTarget.Empty;

        var selectedNodes = GetSelectedExplorerNodes();
        if (preferFolder)
        {
            var folderNode = selectedNodes.FirstOrDefault(node => node.IsFolder)
                ?? (contextMenuTargetNode?.IsFolder == true ? contextMenuTargetNode : null);
            if (folderNode is null || string.IsNullOrWhiteSpace(folderNode.FolderName))
                return ExportTarget.Empty;

            return new ExportTarget(
                IsFolder: true,
                FolderName: folderNode.FolderName,
                GroupId: activeDatabaseGroupId,
                Macros: []);
        }

        var selected = selectedNodes
            .Select(node => node.Item)
            .Where(item => item is not null)
            .Cast<MacroLibraryItem>()
            .ToList();
        if (selected.Count == 0 && state.SelectedMacroId is { } selectedId)
        {
            var current = state.LibraryStore.Load().Items.FirstOrDefault(item =>
                string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            if (current is not null)
                selected.Add(current);
        }

        return new ExportTarget(
            IsFolder: false,
            FolderName: null,
            GroupId: activeDatabaseGroupId,
            Macros: selected);
    }

    private void ExecuteExport(
        ExportTarget target,
        ExportWizardResult wizardResult,
        MacroLibraryExportBundle? precomputedItemBundle = null)
    {
        if (state is null) return;
        var format = wizardResult.Format;
        var snapshot = state.LibraryStore.Load();

        if (target.IsFolder)
        {
            ExecuteFolderExport(target, wizardResult, snapshot);
            return;
        }

        var bundle = precomputedItemBundle ?? BuildItemExportBundle(target, snapshot);
        if (bundle.Primary.Count == 0)
        {
            ResultMessage?.Invoke(L("ExportNothingSelected"));
            return;
        }

        // Single macro with no nested dependencies: keep classic Save As.
        if (bundle.Primary.Count == 1 && bundle.Dependencies.Count == 0)
        {
            var entry = bundle.Primary[0];
            var export = MacroConversionService.ExportFromMcrx(entry.Document, format);
            var dialog = new SaveFileDialog
            {
                Filter = FormatFilter(format),
                Title = L("ExportMacroTitle"),
                DefaultExt = MacroConversionService.GetDefaultExtension(format),
                FileName = export.FileName
            };
            if (DialogOwnerService.ShowDialogSafe(dialog, this) != true) return;
            File.WriteAllText(dialog.FileName, export.Output);
            ConversionText.Text = FormatDiagnostics(export.Diagnostics);
            ResultMessage?.Invoke(LF("ConversionExported", Path.GetFileName(dialog.FileName)));
            return;
        }

        var dependenciesFolderName = L("ExportDependenciesFolder");
        var primaryFolderName = bundle.Primary.Count == 1
            ? SanitizeExportFileName(bundle.Primary[0].Item.Name)
            : SanitizeExportFileName(L("ExportSelectedMacrosFolder"));
        var exportRootName = primaryFolderName + L("ExportFolderRootSuffix");

        if (wizardResult.Packaging == ExportPackagingMode.Zip)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "ZIP (*.zip)|*.zip",
                Title = L("ExportMacroTitle"),
                DefaultExt = ".zip",
                FileName = SanitizeExportFileName(exportRootName) + ".zip"
            };
            if (DialogOwnerService.ShowDialogSafe(dialog, this) != true) return;
            MacroLibraryExportWriter.WriteZip(
                bundle,
                dialog.FileName,
                exportRootName,
                primaryFolderName,
                dependenciesFolderName,
                format);
            ResultMessage?.Invoke(LF("ConversionExported", Path.GetFileName(dialog.FileName)));
            return;
        }

        if (wizardResult.Packaging == ExportPackagingMode.TwoFolders)
        {
            var parentDirectory = PromptForExportDirectory();
            if (string.IsNullOrWhiteSpace(parentDirectory)) return;
            MacroLibraryExportWriter.WriteTwoFolders(
                bundle,
                parentDirectory,
                exportRootName,
                primaryFolderName,
                dependenciesFolderName,
                format);
            ResultMessage?.Invoke(LF("ConversionExported", exportRootName));
            return;
        }

        var directory = PromptForExportDirectory();
        if (string.IsNullOrWhiteSpace(directory)) return;

        var diagnostics = new List<MacroConversionDiagnostic>();
        var exportedNames = new List<string>();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in bundle.Primary)
        {
            var export = MacroConversionService.ExportFromMcrx(entry.Document, format, entry.RelativePath);
            var path = AllocateExportPath(directory, export.FileName, written);
            File.WriteAllText(path, export.Output);
            written.Add(Path.GetFullPath(path));
            diagnostics.AddRange(export.Diagnostics);
            exportedNames.Add(Path.GetFileName(path));
        }

        if (bundle.Dependencies.Count > 0)
        {
            var depsDirectory = Path.Combine(directory, dependenciesFolderName);
            Directory.CreateDirectory(depsDirectory);
            foreach (var entry in bundle.Dependencies)
            {
                var export = MacroConversionService.ExportFromMcrx(entry.Document, format, entry.RelativePath);
                var path = AllocateExportPath(depsDirectory, export.FileName, written);
                File.WriteAllText(path, export.Output);
                written.Add(Path.GetFullPath(path));
                diagnostics.AddRange(export.Diagnostics);
                exportedNames.Add(Path.Combine(dependenciesFolderName, Path.GetFileName(path)));
            }
        }

        ConversionText.Text = FormatDiagnostics(diagnostics);
        ResultMessage?.Invoke(LF("ConversionExported", string.Join(", ", exportedNames)));
    }

    private MacroLibraryExportBundle BuildItemExportBundle(ExportTarget target, MacroLibrarySnapshot snapshot)
    {
        if (state is null)
            return new MacroLibraryExportBundle([], []);

        return MacroLibraryExportBundles.FromItems(
            state.LibraryStore,
            snapshot,
            target.Macros,
            item => PrepareExportDocument(item));
    }

    private void ExecuteFolderExport(ExportTarget target, ExportWizardResult wizardResult, MacroLibrarySnapshot snapshot)
    {
        if (state is null || string.IsNullOrWhiteSpace(target.FolderName) || string.IsNullOrWhiteSpace(target.GroupId))
            return;

        var format = wizardResult.Format;
        var packaging = wizardResult.Packaging ?? ExportPackagingMode.TwoFolders;
        var folderName = target.FolderName!;
        var exportRootName = folderName + L("ExportFolderRootSuffix");
        var dependenciesFolderName = L("ExportDependenciesFolder");
        var bundle = MacroLibraryExportBundles.FromFolder(
            state.LibraryStore,
            snapshot,
            groupId: target.GroupId!,
            folder: folderName);

        if (packaging == ExportPackagingMode.Zip)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "ZIP (*.zip)|*.zip",
                Title = L("ExportMacroTitle"),
                DefaultExt = ".zip",
                FileName = SanitizeExportFileName(exportRootName) + ".zip"
            };
            if (DialogOwnerService.ShowDialogSafe(dialog, this) != true) return;
            MacroLibraryExportWriter.WriteZip(
                bundle,
                dialog.FileName,
                exportRootName,
                folderName,
                dependenciesFolderName,
                format);
            ResultMessage?.Invoke(LF("ConversionExported", Path.GetFileName(dialog.FileName)));
            return;
        }

        var parentDirectory = PromptForExportDirectory();
        if (string.IsNullOrWhiteSpace(parentDirectory)) return;
        MacroLibraryExportWriter.WriteTwoFolders(
            bundle,
            parentDirectory,
            exportRootName,
            folderName,
            dependenciesFolderName,
            format);
        ResultMessage?.Invoke(LF("ConversionExported", exportRootName));
    }

    private string? PromptForExportDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = L("ExportMacroTitle")
        };
        return DialogOwnerService.ShowDialogSafe(dialog, this) == true
            ? dialog.FolderName
            : null;
    }

    private static string SanitizeExportFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "export" : cleaned;
    }

    private MacroDocument PrepareExportDocument(MacroLibraryItem? item)
    {
        MacroDocument document;
        if (item is not null
            && state is not null
            && string.Equals(item.Id, state.SelectedMacroId, StringComparison.OrdinalIgnoreCase)
            && DocumentRequested is not null)
        {
            document = DocumentRequested.Invoke()
                ?? throw new InvalidOperationException("No document available.");
        }
        else if (item is not null && state is not null)
        {
            document = state.LibraryStore.ReadMacro(item.Id);
        }
        else
        {
            document = DocumentRequested?.Invoke()
                ?? throw new InvalidOperationException("No document available.");
        }

        return item is null ? document : document with { Id = item.Id };
    }

    private static string AllocateExportPath(string directory, string fileName, IReadOnlySet<string> written)
    {
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "macro";
        }

        for (var index = 0; ; index++)
        {
            var candidateName = index == 0 ? $"{stem}{extension}" : $"{stem}-{index}{extension}";
            var path = Path.GetFullPath(Path.Combine(directory, candidateName));
            if (!written.Contains(path) && !File.Exists(path))
            {
                return path;
            }

            if (index >= 999)
            {
                return Path.GetFullPath(Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}"));
            }
        }
    }

    private static string FormatFilter(MacroConversionFormat format)
    {
        return MacroConversionService.GetFormats().FirstOrDefault(item => item.Format == format)?.FileDialogFilter
            ?? "All files (*.*)|*.*";
    }

    private static string FormatDiagnostics(IReadOnlyList<MacroConversionDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0) return L("ConversionDiagnosticsNone");
        return string.Join(Environment.NewLine, diagnostics.Select(item =>
        {
            var location = item.LineNumber is { } line
                ? LF("ConversionDiagnosticLocation", line, item.ColumnNumber ?? 1)
                : string.Empty;
            return $"{item.Severity}: {location}{item.Message}";
        }));
    }

    private static string FormatImportFailure(string fileName, Exception exception)
    {
        if (exception is not MacroImportException importException)
        {
            return $"{fileName}: {exception.Message}";
        }

        var location = importException.LineNumber is { } line
            ? LF("ConversionImportLocation", line, importException.ColumnNumber ?? 1)
            : L("ConversionImportUnknownLocation");
        var source = string.IsNullOrWhiteSpace(importException.SourceLine)
            ? string.Empty
            : Environment.NewLine + LF("ConversionImportSourceLine", importException.SourceLine);
        return $"{fileName}: {location}{Environment.NewLine}{importException.Reason}{source}";
    }

    private string NextMacroName(string prefix)
    {
        var groupId = GetCurrentGroupId();
        var folder = GetCurrentFolder();
        var used = state!.LibraryStore.Load().Items
            .Where(item => string.Equals(item.GroupId, groupId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Folder, folder, StringComparison.Ordinal))
            .Select(item => item.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{prefix} {i}";
            if (!used.Contains(candidate)) return candidate;
        }
        return $"{prefix} {DateTime.Now:HHmmss}";
    }

    private string NextFolderName()
    {
        var groupId = GetCurrentGroupId();
        var used = state!.LibraryStore.Load().GroupFolders
            .Where(folder => string.Equals(folder.GroupId, groupId, StringComparison.OrdinalIgnoreCase))
            .Select(folder => folder.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{L("Folder")} {i}";
            if (!used.Contains(candidate)) return candidate;
        }
        return $"{L("Folder")} {DateTime.Now:HHmmss}";
    }

    private string NextGroupName()
    {
        var used = state!.LibraryStore.Load().Groups
            .Select(group => group.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{L("ProcessGroup")} {i}";
            if (!used.Contains(candidate)) return candidate;
        }

        return $"{L("ProcessGroup")} {DateTime.Now:HHmmss}";
    }

    private void BeginRename(MacroLibraryTreeNode? node)
    {
        if (node is null) return;
        if (node.Item?.IsLocked == true)
        {
            ResultMessage?.Invoke(L("MacroLockedReadOnly"));
            return;
        }
        if (renamingNode is not null && !ReferenceEquals(renamingNode, node))
        {
            CancelRename(renamingNode);
        }

        renamingNode = node;
        node.RenameText = node.Title;
        node.IsRenaming = true;
    }

    private void CommitRename(MacroLibraryTreeNode node)
    {
        if (state is null) return;
        if (!ReferenceEquals(renamingNode, node)) return;

        renamingNode = null;
        var requestedName = node.RenameText.Trim();
        if (string.IsNullOrWhiteSpace(requestedName) || string.Equals(requestedName, node.Title, StringComparison.CurrentCulture))
        {
            node.IsRenaming = false;
            RefreshAfterRenameIfNeeded();
            return;
        }

        try
        {
            if (node.IsGroup && node.ProcessGroup is { } group)
            {
                var newName = CreateUniqueGroupName(requestedName, group.Id);
                var renamed = state.LibraryStore.UpdateGroup(group.Id, newName, group.ProcessFilter);
                selectedGroupId = renamed.Id;
                selectedFolder = null;
                state.SelectedMacroId = null;
                ResultMessage?.Invoke(LF("GroupRenamed", newName));
            }
            else if (node.IsFolder)
            {
                var newName = CreateUniqueFolderName(requestedName, node.FolderName, node.GroupId);
                state.LibraryStore.RenameFolder(node.FolderName, newName, node.GroupId);
                selectedGroupId = node.GroupId;
                selectedFolder = newName;
                state.SelectedMacroId = null;
                ResultMessage?.Invoke(LF("FolderRenamed", newName));
            }
            else if (node.Item is { } item)
            {
                var newName = CreateUniqueMacroName(requestedName, item.GroupId, item.Folder, item.Id);
                var renamed = state.LibraryStore.RenameMacro(item.Id, newName);
                state.SelectedMacroId = renamed.Id;
                selectedGroupId = renamed.GroupId;
                ResultMessage?.Invoke(LF("MacroRenamed", newName));
                MacroSelected?.Invoke(renamed.Id);
            }

            RefreshTree();
        }
        catch (Exception ex)
        {
            node.IsRenaming = false;
            ResultMessage?.Invoke(ex.Message);
            RefreshAfterRenameIfNeeded();
        }
    }

    private void CancelRename(MacroLibraryTreeNode node)
    {
        if (ReferenceEquals(renamingNode, node))
        {
            renamingNode = null;
        }

        node.RenameText = node.Title;
        node.IsRenaming = false;
        RefreshAfterRenameIfNeeded();
    }

    private void RefreshAfterRenameIfNeeded()
    {
        if (!refreshTreeAfterRename) return;
        refreshTreeAfterRename = false;
        RefreshTree();
    }

    private void CopySelectionToClipboard()
    {
        var nodes = showingDatabaseContents
            ? GetSelectedExplorerNodes()
            : GetSelectedNode() is { } selected ? [selected] : [];
        clipboard = nodes
            .Where(node => !node.IsGroup)
            .Select(node => node.IsFolder
                ? new MacroLibraryClipboardItem(MacroLibraryClipboardKind.Folder, node.FolderName, node.GroupId)
                : new MacroLibraryClipboardItem(MacroLibraryClipboardKind.Macro, node.Item!.Id, node.GroupId))
            .ToList();
        if (clipboard.Count == 0) return;
        UpdateClipboardControls();
        ResultMessage?.Invoke(clipboard.Count == 1
            ? (nodes[0].IsFolder ? LF("FolderCopied", nodes[0].FolderName) : LF("MacroCopied", nodes[0].Title))
            : LF("SelectedItemsCopied", clipboard.Count));
    }

    private void PasteClipboard()
    {
        if (state is null) return;
        if (clipboard.Count == 0)
        {
            ResultMessage?.Invoke(L("ClipboardEmpty"));
            return;
        }

        try
        {
            var targetGroupId = GetCurrentGroupId();
            var targetFolder = GetCurrentFolder();
            foreach (var clipboardItem in clipboard.ToList())
            {
                switch (clipboardItem.Kind)
                {
                    case MacroLibraryClipboardKind.Macro:
                        PasteMacro(clipboardItem.Value, targetGroupId, targetFolder);
                        break;
                    case MacroLibraryClipboardKind.Folder:
                        PasteFolder(clipboardItem.Value, clipboardItem.GroupId, targetGroupId);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            ResultMessage?.Invoke(ex.Message);
        }
    }

    private void PasteMacro(string macroId, string targetGroupId, string targetFolder)
    {
        var snapshot = state!.LibraryStore.Load();
        var source = snapshot.Items.First(item => item.Id == macroId);
        var document = state.LibraryStore.ReadMacro(macroId);
        var copyName = CreateUniqueMacroName($"{source.Name} Copy", targetGroupId, targetFolder, null);
        var created = state.LibraryStore.CreateMacro(document with { Name = copyName }, targetFolder, groupId: targetGroupId);

        state.SelectedMacroId = created.Id;
        selectedGroupId = created.GroupId;
        selectedFolder = created.Folder;
        RefreshTree();
        MacroSelected?.Invoke(created.Id);
        ResultMessage?.Invoke(LF("MacroPasted", copyName));
    }

    private void PasteFolder(string folder, string sourceGroupId, string targetGroupId)
    {
        var snapshot = state!.LibraryStore.Load();
        var copiedFolder = CreateUniqueFolderName($"{folder} Copy", null, targetGroupId);
        state.LibraryStore.CreateFolder(copiedFolder, targetGroupId);

        foreach (var item in snapshot.Items.Where(item =>
            string.Equals(item.GroupId, sourceGroupId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Folder, folder, StringComparison.Ordinal)))
        {
            var document = state.LibraryStore.ReadMacro(item.Id);
            state.LibraryStore.CreateMacro(document with { Name = item.Name }, copiedFolder, groupId: targetGroupId);
        }

        state.SelectedMacroId = null;
        selectedGroupId = targetGroupId;
        selectedFolder = copiedFolder;
        RefreshTree();
        ResultMessage?.Invoke(LF("FolderPasted", copiedFolder));
    }

    private void UpdateClipboardControls()
    {
        if (PasteMenuItem is not null)
        {
            PasteMenuItem.IsEnabled = clipboard.Count > 0;
        }

        if (ExplorerPasteMenuItem is not null)
        {
            ExplorerPasteMenuItem.IsEnabled = clipboard.Count > 0;
        }
    }

    private MacroLibraryTreeNode? GetSelectedNode()
    {
        return showingDatabaseContents
            ? contextMenuTargetNode ?? ExplorerListView.SelectedItem as MacroLibraryTreeNode
            : contextMenuTargetNode ?? MacroTreeView.SelectedItem as MacroLibraryTreeNode;
    }

    private string CreateUniqueGroupName(string requestedName, string? excludingGroupId)
    {
        var baseName = string.IsNullOrWhiteSpace(requestedName) ? L("ProcessGroup") : requestedName.Trim();
        var used = state!.LibraryStore.Load().Groups
            .Where(group => !string.Equals(group.Id, excludingGroupId, StringComparison.OrdinalIgnoreCase))
            .Select(group => group.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        return CreateUniqueName(baseName, used);
    }

    private string CreateUniqueMacroName(string requestedName, string groupId, string folder, string? excludingId)
    {
        var baseName = string.IsNullOrWhiteSpace(requestedName) ? L("Macro") : requestedName.Trim();
        var used = state!.LibraryStore.Load().Items
            .Where(item => !string.Equals(item.Id, excludingId, StringComparison.Ordinal)
                && string.Equals(item.GroupId, groupId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Folder, folder, StringComparison.Ordinal))
            .Select(item => item.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        return CreateUniqueName(baseName, used);
    }

    private string CreateUniqueFolderName(string requestedName, string? excludingFolder, string groupId)
    {
        var baseName = string.IsNullOrWhiteSpace(requestedName) ? L("Folder") : requestedName.Trim();
        var used = state!.LibraryStore.Load().GroupFolders
            .Where(folder => string.Equals(folder.GroupId, groupId, StringComparison.OrdinalIgnoreCase))
            .Select(folder => folder.Name)
            .Where(folder => !string.Equals(folder, excludingFolder, StringComparison.Ordinal))
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        return CreateUniqueName(baseName, used);
    }

    private static string CreateUniqueName(string baseName, ISet<string> used)
    {
        if (!used.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; i < 10_000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName} {DateTime.Now:HHmmss}";
    }

    private MacroLibraryTreeNode CreateMacroNode(MacroLibraryItem item)
    {
        MacroDocument? document = null;
        try { document = state!.LibraryStore.ReadMacro(item.Id); } catch { }
        listeningStates.TryGetValue(item.Id, out var listenState);
        return MacroLibraryTreeNode.Macro(item, document, listenState);
    }

    private static void MarkSelectedMacro(IEnumerable<MacroLibraryTreeNode> nodes, string? selectedId)
    {
        if (selectedId is null) return;
        foreach (var node in nodes)
        {
            if (node.Item?.Id == selectedId)
            {
                node.IsSelected = true;
                return;
            }

            MarkSelectedMacro(node.Children, selectedId);
        }
    }

    private void CaptureExpandedGroups()
    {
        expandedGroups.Clear();
        if (MacroTreeView.ItemsSource is not IEnumerable<MacroLibraryTreeNode> nodes)
        {
            return;
        }

        foreach (var node in nodes.Where(node => node.IsGroup && node.IsExpanded))
        {
            expandedGroups.Add(node.GroupId);
        }
    }

    private void CaptureExpandedFolders()
    {
        expandedFolders.Clear();
        if (MacroTreeView.ItemsSource is not IEnumerable<MacroLibraryTreeNode> nodes)
        {
            return;
        }

        foreach (var node in FlattenNodes(nodes).Where(node => node.IsFolder && node.IsExpanded))
        {
            expandedFolders.Add(FormatFolderExpansionKey(node.GroupId, node.FolderName));
        }
    }

    private static IEnumerable<MacroLibraryTreeNode> FlattenNodes(IEnumerable<MacroLibraryTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in FlattenNodes(node.Children))
            {
                yield return child;
            }
        }
    }

    private void RestoreMacroTreeScroll(double verticalOffset)
    {
        MacroTreeView.Dispatcher.BeginInvoke(new Action(() =>
        {
            GetMacroTreeScrollViewer()?.ScrollToVerticalOffset(verticalOffset);
        }), DispatcherPriority.Loaded);
    }

    private void RestoreLibraryScroll(double verticalOffset)
    {
        if (!showingDatabaseContents)
        {
            RestoreMacroTreeScroll(verticalOffset);
            return;
        }

        ExplorerListView.Dispatcher.BeginInvoke(new Action(() =>
        {
            FindVisualChild<ScrollViewer>(ExplorerListView)?.ScrollToVerticalOffset(verticalOffset);
        }), DispatcherPriority.Loaded);
    }

    private ScrollViewer? GetMacroTreeScrollViewer()
    {
        return macroTreeScrollViewer ??= FindVisualChild<ScrollViewer>(MacroTreeView);
    }

    private string GetCurrentFolder()
    {
        if (!showingDatabaseContents)
        {
            return string.Empty;
        }

        if (contextMenuTargetNode is { IsFolder: true } contextFolder)
        {
            return contextFolder.FolderName;
        }

        return currentDatabaseFolder;
    }

    private string GetCurrentGroupId()
    {
        if (showingDatabaseContents)
        {
            return activeDatabaseGroupId;
        }

        if (MacroTreeView.SelectedItem is MacroLibraryTreeNode node)
        {
            return node.GroupId;
        }

        return string.IsNullOrWhiteSpace(selectedGroupId)
            ? MacroLibraryStore.GlobalGroupId
            : selectedGroupId;
    }

    private static string FormatFolderExpansionKey(string groupId, string folder)
    {
        return $"{groupId}\n{folder}";
    }

    private void SelectLibrarySortMode(string tag)
    {
        foreach (var item in LibrarySortBox.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                continue;
            }

            if (!ReferenceEquals(LibrarySortBox.SelectedItem, item))
            {
                LibrarySortBox.SelectedItem = item;
            }

            return;
        }
    }

    private void SelectExplorerViewMode(string tag)
    {
        foreach (var item in LibraryViewBox.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal)) continue;
            if (!ReferenceEquals(LibraryViewBox.SelectedItem, item)) LibraryViewBox.SelectedItem = item;
            ApplyExplorerViewMode();
            return;
        }
    }

    private MacroLibraryDropTarget GetMacroDropTarget(Point point, string sourceMacroId)
    {
        var source = MacroTreeView.InputHitTest(point) as DependencyObject;
        var treeViewItem = FindVisualParent<TreeViewItem>(source);
        if (treeViewItem?.DataContext is not MacroLibraryTreeNode node)
        {
            return MacroLibraryDropTarget.NoOp;
        }

        if (node.IsGroup)
        {
            return new MacroLibraryDropTarget(node.GroupId, string.Empty, null, node, MacroLibraryDropIndicator.Into);
        }

        if (node.IsFolder)
        {
            return new MacroLibraryDropTarget(node.GroupId, node.FolderName, null, node, MacroLibraryDropIndicator.Into);
        }

        if (node.Item is not { } target)
        {
            return MacroLibraryDropTarget.NoOp;
        }

        if (string.Equals(target.Id, sourceMacroId, StringComparison.Ordinal))
        {
            return MacroLibraryDropTarget.NoOp;
        }

        var pointInsideItem = MacroTreeView.TranslatePoint(point, treeViewItem);
        var insertBeforeTarget = pointInsideItem.Y < treeViewItem.ActualHeight / 2;
        var beforeMacroId = insertBeforeTarget
            ? target.Id
            : GetNextMacroIdInFolder(target.Id, target.GroupId, target.Folder, sourceMacroId);
        var indicator = insertBeforeTarget
            ? MacroLibraryDropIndicator.Before
            : MacroLibraryDropIndicator.After;
        return new MacroLibraryDropTarget(target.GroupId, target.Folder, beforeMacroId, node, indicator);
    }

    private void SetMacroDropIndicator(MacroLibraryDropTarget target)
    {
        if (target.IsNoOp || target.Node is null || target.Indicator == MacroLibraryDropIndicator.None)
        {
            ClearMacroDropIndicator();
            return;
        }

        if (ReferenceEquals(macroDropIndicatorNode, target.Node) && target.Node.DropIndicator == target.Indicator)
        {
            return;
        }

        ClearMacroDropIndicator();
        macroDropIndicatorNode = target.Node;
        target.Node.DropIndicator = target.Indicator;
    }

    private void ClearMacroDropIndicator()
    {
        if (macroDropIndicatorNode is null)
        {
            return;
        }

        macroDropIndicatorNode.DropIndicator = MacroLibraryDropIndicator.None;
        macroDropIndicatorNode = null;
    }

    private string? GetNextMacroIdInFolder(string targetMacroId, string groupId, string folder, string sourceMacroId)
    {
        var seenTarget = false;
        foreach (var node in GetMacroNodesInFolder(groupId, folder))
        {
            if (string.Equals(node.Item?.Id, targetMacroId, StringComparison.Ordinal))
            {
                seenTarget = true;
                continue;
            }

            if (!seenTarget || string.Equals(node.Item?.Id, sourceMacroId, StringComparison.Ordinal))
            {
                continue;
            }

            return node.Item?.Id;
        }

        return null;
    }

    private IEnumerable<MacroLibraryTreeNode> GetMacroNodesInFolder(string groupId, string folder)
    {
        if (MacroTreeView.ItemsSource is not IEnumerable<MacroLibraryTreeNode> nodes)
        {
            yield break;
        }

        var groupNode = nodes.FirstOrDefault(node => node.IsGroup && string.Equals(node.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
        if (groupNode is null)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            foreach (var node in groupNode.Children.Where(node => node.Item is not null))
            {
                yield return node;
            }

            yield break;
        }

        var folderNode = groupNode.Children.FirstOrDefault(node => node.IsFolder && string.Equals(node.FolderName, folder, StringComparison.Ordinal));
        if (folderNode is null)
        {
            yield break;
        }

        foreach (var child in folderNode.Children.Where(node => node.Item is not null))
        {
            yield return child;
        }
    }

    private static string GetDropTargetFolder(DependencyObject? source)
    {
        var treeViewItem = FindVisualParent<TreeViewItem>(source);
        if (treeViewItem?.DataContext is MacroLibraryTreeNode node)
        {
            return node.IsFolder ? node.FolderName : node.Item?.Folder ?? string.Empty;
        }

        return string.Empty;
    }

    private string GetDropTargetFolder(Point point)
    {
        return GetDropTargetFolder(MacroTreeView.InputHitTest(point) as DependencyObject);
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? source) where T : DependencyObject
    {
        if (source is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static string ToPrecisionModeText(PrecisionMode mode) => mode switch
    {
        PrecisionMode.Balanced => "balanced",
        PrecisionMode.UltraLowJitter => "ultraLowJitter",
        _ => "extremeDuringPlayback"
    };

    private static string L(string key) => LocalizationService.Get(key);
    private static string LF(string key, params object[] args) => LocalizationService.Format(key, args);

    private sealed record ExportTarget(
        bool IsFolder,
        string? FolderName,
        string? GroupId,
        IReadOnlyList<MacroLibraryItem> Macros)
    {
        public static ExportTarget Empty { get; } = new(false, null, null, []);

        public bool IsEmpty => IsFolder
            ? string.IsNullOrWhiteSpace(FolderName)
            : Macros.Count == 0;
    }

    private sealed record MacroLibraryClipboardItem(MacroLibraryClipboardKind Kind, string Value, string GroupId);

    private sealed record SmartImportFile(
        string FullPath,
        string DisplayName,
        string Content,
        MacroConversionFormat Format,
        string? RazerGuid);

    private sealed record RunningProcessChoice(string ProcessName, string WindowTitle);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseLowLevelHookStruct
    {
        public NativePoint Point;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hmod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    private enum ExplorerContextMenuKind
    {
        Blank,
        Folder,
        Macro
    }

    private enum MacroLibraryClipboardKind
    {
        Macro,
        Folder
    }

    private sealed record MacroLibraryDropTarget(
        string GroupId,
        string Folder,
        string? BeforeMacroId,
        MacroLibraryTreeNode? Node,
        MacroLibraryDropIndicator Indicator,
        bool IsNoOp = false)
    {
        public static MacroLibraryDropTarget NoOp { get; } = new(
            MacroLibraryStore.GlobalGroupId,
            string.Empty,
            null,
            null,
            MacroLibraryDropIndicator.None,
            IsNoOp: true);
    }
}
