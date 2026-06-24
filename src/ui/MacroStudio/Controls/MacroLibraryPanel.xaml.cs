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

public partial class MacroLibraryPanel : UserControl
{
    private const string MacroLibraryDragFormat = "MacroHID.MacroLibraryItem";
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEWHEEL_LOW_LEVEL = 0x020A;
    private const int WH_MOUSE_LL = 14;
    private const long LibraryAutoScrollIntervalMilliseconds = 120;
    private const double WheelDelta = 120.0;

    private readonly LowLevelMouseProc libraryDragMouseHookProc;
    private MacroEditorState? state;
    private bool suppressSelection;
    private List<AuxiliaryMacroFile> razerModuleFiles = [];
    private readonly HashSet<string> expandedGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expandedFolders = new(StringComparer.Ordinal);
    private readonly HashSet<string> selectedManagerGroupIds = new(StringComparer.OrdinalIgnoreCase);
    private string selectedGroupId = MacroLibraryStore.GlobalGroupId;
    private string activeDatabaseGroupId = MacroLibraryStore.GlobalGroupId;
    private bool showingDatabaseContents;
    private bool managerSelectionInitialized;
    private string? selectedFolder;
    private MacroLibraryClipboardItem? clipboard;
    private MacroLibraryTreeNode? renamingNode;
    private bool updatingRuntimePrecisionControls;
    private bool updatingGroupControls;
    private bool libraryDragInProgress;
    private ScrollViewer? macroTreeScrollViewer;
    private HwndSource? macroTreeHwndSource;
    private IntPtr libraryDragMouseHookHandle;
    private long lastLibraryAutoScrollTick;
    private MacroLibraryTreeNode? macroDropIndicatorNode;
    private IReadOnlyDictionary<string, MacroLibraryListenState> listeningStates =
        new Dictionary<string, MacroLibraryListenState>(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? MacroSelected;
    public event Action<string>? MacroDuplicated;
    public event Action<string>? MacroDeleted;
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
    }

    public void Initialize(MacroEditorState editorState)
    {
        state = editorState;
        InitializeExportFormatBox();
        RefreshConversionText();
        RefreshTree();
    }

    public void ApplyLocalization()
    {
        NewGroupButton.Content = L("NewDatabase");
        DeleteDatabaseButton.Content = L("DeleteDatabase");
        BackToManagerButton.Content = L("ReturnToManager");
        NewMacroButton.Content = L("NewMacro");
        NewFolderButton.Content = L("NewFolder");
        CopyMacroButton.Content = L("Copy");
        PasteMacroButton.Content = L("Paste");
        DeleteMacroButton.Content = L("Delete");
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
        ImportExportTitleText.Text = L("ImportExport");
        ImportMacroButton.Content = L("ImportMacro");
        ImportRazerModulesButton.Content = L("ImportRazerModules");
        ExportFormatLabelText.Text = L("ExportFormat");
        ExportMacroButton.Content = L("ExportMacro");
        MacroSearchBox.ToolTip = L("SearchMacros");
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
        RefreshTree();
    }

    public void RefreshTree()
    {
        if (state is null) return;

        var search = MacroSearchBox.Text.Trim();
        var previousScrollOffset = GetMacroTreeScrollViewer()?.VerticalOffset ?? 0;
        if (string.IsNullOrWhiteSpace(search))
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
                ? BuildDatabaseNodes(materializedItems, search, selectedId)
                : BuildManagerNodes(materializedItems, search);

            if (!state.LibrarySnapshot.Groups.Any(group => string.Equals(group.Id, selectedGroupId, StringComparison.OrdinalIgnoreCase)))
            {
                selectedGroupId = MacroLibraryStore.GlobalGroupId;
            }

            var selectedGroup = state.LibrarySnapshot.Groups.FirstOrDefault(group => string.Equals(group.Id, selectedGroupId, StringComparison.OrdinalIgnoreCase))
                ?? state.LibrarySnapshot.Groups.FirstOrDefault();
            SetGroupEditor(selectedGroup);
            MacroTreeView.ItemsSource = nodes;
            ApplyProgressiveViewState();
        }
        finally
        {
            suppressSelection = false;
        }

        RestoreMacroTreeScroll(previousScrollOffset);
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
            showingDatabaseContents = false;
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
        MacroTreeView.AllowDrop = showingDatabaseContents;
        ImportMacroButton.IsEnabled = showingDatabaseContents;
        ImportRazerModulesButton.IsEnabled = showingDatabaseContents;
        ExportMacroButton.IsEnabled = showingDatabaseContents;
        CopyMenuItem.Visibility = databaseVisibility;
        PasteMenuItem.Visibility = databaseVisibility;
        CurrentDatabaseTitleText.Text = GetDatabaseTitleText();
        DeleteMenuItem.Header = showingDatabaseContents ? L("Delete") : L("DeleteDatabase");
        DeleteDatabaseButton.IsEnabled = !showingDatabaseContents && GetSelectedEditableGroup() is not null;
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
        return group is null
            ? L("MacroLibrary")
            : $"{L("MacroLibrary")} / {group.Name}";
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
        selectedGroupId = groupId;
        selectedFolder = null;
        state.SelectedMacroId = null;
        state.LibraryStore.SetSelected(null);
        RefreshTree();
    }

    private void ReturnToManagerView()
    {
        showingDatabaseContents = false;
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

    private void MacroTreeView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            BeginRename(GetSelectedNode());
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.C)
        {
            if (!showingDatabaseContents) return;
            CopySelectionToClipboard();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.V)
        {
            if (!showingDatabaseContents) return;
            PasteClipboard();
            e.Handled = true;
            return;
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
            CommitRename(node);
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

        CommitRename(node);
    }

    private void MacroTreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var treeViewItem = FindVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (treeViewItem is null) return;

        treeViewItem.Focus();
        treeViewItem.IsSelected = true;
    }

    private void MacroTreeView_Loaded(object sender, RoutedEventArgs e)
    {
        macroTreeScrollViewer = FindVisualChild<ScrollViewer>(MacroTreeView);
        macroTreeHwndSource = HwndSource.FromVisual(MacroTreeView) as HwndSource;
        macroTreeHwndSource?.RemoveHook(MacroTreeWndProc);
        macroTreeHwndSource?.AddHook(MacroTreeWndProc);
    }

    private void MacroTreeView_Unloaded(object sender, RoutedEventArgs e)
    {
        StopLibraryDragWheelHook();
        macroTreeHwndSource?.RemoveHook(MacroTreeWndProc);
        macroTreeHwndSource = null;
        macroTreeScrollViewer = null;
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
        if (msg == WM_MOUSEWHEEL && libraryDragInProgress && IsScreenPointInsideMacroTree(lParam))
        {
            ScrollMacroTreeByWheelDelta(GetWheelDelta(wParam));
            handled = true;
        }

        return IntPtr.Zero;
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
        if (GetSelectedEditableGroup() is not { } group) return;

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

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e) => DeleteMacro_Click(sender, e);

    private void DuplicateMacro_Click(object sender, RoutedEventArgs e)
    {
        if (state?.SelectedMacroId is null) return;
        MacroDuplicated?.Invoke(state.SelectedMacroId);
    }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        if (MacroTreeView.SelectedItem is MacroLibraryTreeNode { IsGroup: true, ProcessGroup: { } group })
        {
            if (group.IsGlobal) return;
            state.LibraryStore.DeleteGroup(group.Id);
            state.SelectedMacroId = null;
            selectedGroupId = MacroLibraryStore.GlobalGroupId;
            selectedFolder = null;
            RefreshTree();
            LibraryStructureEdited?.Invoke();
            ResultMessage?.Invoke(LF("GroupDeleted", group.Name));
            return;
        }

        if (MacroTreeView.SelectedItem is MacroLibraryTreeNode { IsFolder: true } folder)
        {
            state.LibraryStore.DeleteFolder(folder.FolderName, deleteMacros: false, folder.GroupId);
            state.SelectedMacroId = null;
            selectedFolder = null;
            RefreshTree();
            ResultMessage?.Invoke(LF("FolderDeleted", folder.FolderName));
            return;
        }

        if (state.SelectedMacroId is null) return;
        MacroDeleted?.Invoke(state.SelectedMacroId);
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
                ExportFormatBox.SelectedItem = item;
        }

        if (ExportFormatBox.SelectedItem is null && ExportFormatBox.Items.Count > 0)
            ExportFormatBox.SelectedIndex = 0;
    }

    private void RefreshConversionText()
    {
        ConversionText.Text = razerModuleFiles.Count > 0
            ? LF("ConversionModulesLoaded", razerModuleFiles.Count)
            : L("ConversionReady");
    }

    private void ImportMacro_Click(object sender, RoutedEventArgs e)
    {
        if (!showingDatabaseContents) return;
        var dialog = new OpenFileDialog
        {
            Filter = L("ConverterImportFileFilter"),
            Title = L("ImportMacroTitle")
        };

        if (DialogOwnerService.ShowDialogSafe(dialog, this) != true) return;

        try
        {
            var content = File.ReadAllText(dialog.FileName);
            var import = MacroConversionService.ImportToMcrx(new MacroImportRequest(
                content, dialog.FileName, MacroConversionFormat.Auto, []));
            ImportApplied?.Invoke(import.Document);
            var message = LF("ConversionImported", import.SourceFormat, import.Document.Steps.Count);
            ConversionText.Text = message + Environment.NewLine + FormatDiagnostics(import.Diagnostics);
            ResultMessage?.Invoke(message);
        }
        catch (Exception ex)
        {
            var message = LF("ConversionImportFailed", ex.Message);
            ConversionText.Text = message;
            ResultMessage?.Invoke(ex.Message);
        }
    }

    private void ImportRazerModules_Click(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        if (!showingDatabaseContents) return;

        var dialog = new OpenFileDialog
        {
            Filter = L("RazerModuleFileFilter"),
            Title = L("ImportRazerModulesTitle"),
            Multiselect = true
        };

        if (DialogOwnerService.ShowDialogSafe(dialog, this) != true) return;

        razerModuleFiles = dialog.FileNames
            .Select(fileName => new AuxiliaryMacroFile(Path.GetFileName(fileName), File.ReadAllText(fileName)))
            .ToList();

        var importedCount = 0;
        MacroLibraryItem? lastImported = null;
        foreach (var file in razerModuleFiles)
        {
            try
            {
                var imported = MacroConversionService.ImportToMcrx(new MacroImportRequest(
                    file.Content, file.FileName, MacroConversionFormat.RazerSynapseXml, []));
                IReadOnlyList<string> aliases = MacroConversionService.TryGetRazerMacroGuid(file.Content, out var guid)
                    ? [guid]
                    : [];
                var targetGroupId = CurrentDatabaseGroupId;
                var existing = state.LibraryStore.Load().Items.FirstOrDefault(item =>
                    string.Equals(item.GroupId, targetGroupId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Name, imported.Document.Name, StringComparison.CurrentCultureIgnoreCase));
                lastImported = existing is not null && aliases.Count > 0
                    ? state.LibraryStore.AddAliasesToMacro(existing.Id, aliases)
                    : state.LibraryStore.CreateMacro(imported.Document, aliases: aliases, groupId: targetGroupId);
                importedCount++;
            }
            catch
            {
                // Keep importing the rest of a module batch even if one file is malformed.
            }
        }

        if (lastImported is not null)
        {
            state.SelectedMacroId = lastImported.Id;
        }

        RefreshTree();
        if (lastImported is not null)
        {
            MacroSelected?.Invoke(lastImported.Id);
        }

        var message = LF("ConversionModulesImported", razerModuleFiles.Count, importedCount);
        ConversionText.Text = message;
        ResultMessage?.Invoke(message);
    }

    private void ExportMacro_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var document = DocumentRequested?.Invoke()
                ?? throw new InvalidOperationException("No document available.");
            var format = GetSelectedExportFormat();
            var export = MacroConversionService.ExportFromMcrx(document, format);
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
        }
        catch (Exception ex)
        {
            var message = LF("ConversionExportFailed", ex.Message);
            ConversionText.Text = message;
            ResultMessage?.Invoke(ex.Message);
        }
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
        if (diagnostics.Count == 0) return L("ConversionDiagnosticsNone");
        return string.Join(Environment.NewLine, diagnostics.Select(item => $"{item.Severity}: {item.Message}"));
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
    }

    private void CopySelectionToClipboard()
    {
        var node = GetSelectedNode();
        if (node is null) return;
        if (node.IsGroup) return;

        clipboard = node.IsFolder
            ? new MacroLibraryClipboardItem(MacroLibraryClipboardKind.Folder, node.FolderName, node.GroupId)
            : new MacroLibraryClipboardItem(MacroLibraryClipboardKind.Macro, node.Item!.Id, node.GroupId);
        UpdateClipboardControls();
        ResultMessage?.Invoke(node.IsFolder ? LF("FolderCopied", node.FolderName) : LF("MacroCopied", node.Title));
    }

    private void PasteClipboard()
    {
        if (state is null) return;
        if (clipboard is null)
        {
            ResultMessage?.Invoke(L("ClipboardEmpty"));
            return;
        }

        try
        {
            var targetGroupId = GetCurrentGroupId();
            var targetFolder = GetCurrentFolder();
            switch (clipboard.Kind)
            {
                case MacroLibraryClipboardKind.Macro:
                    PasteMacro(clipboard.Value, targetGroupId, targetFolder);
                    break;
                case MacroLibraryClipboardKind.Folder:
                    PasteFolder(clipboard.Value, clipboard.GroupId, targetGroupId);
                    break;
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
        if (PasteMacroButton is not null)
        {
            PasteMacroButton.IsEnabled = clipboard is not null;
        }

        if (PasteMenuItem is not null)
        {
            PasteMenuItem.IsEnabled = clipboard is not null;
        }
    }

    private MacroLibraryTreeNode? GetSelectedNode()
    {
        return MacroTreeView.SelectedItem as MacroLibraryTreeNode;
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

        if (MacroTreeView.SelectedItem is MacroLibraryTreeNode node)
        {
            return node.IsFolder ? node.FolderName : node.Item?.Folder ?? string.Empty;
        }

        return selectedFolder ?? string.Empty;
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

    private sealed record MacroLibraryClipboardItem(MacroLibraryClipboardKind Kind, string Value, string GroupId);

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
