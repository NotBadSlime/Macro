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
    private readonly HashSet<string> expandedFolders = new(StringComparer.Ordinal);
    private string? selectedFolder;
    private MacroLibraryClipboardItem? clipboard;
    private MacroLibraryTreeNode? renamingNode;
    private bool updatingRuntimePrecisionControls;
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
    public event Action? StartListeningAllRequested;
    public event Action? StopListeningAllRequested;
    public event Action? PrecisionSettingsEdited;

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
        NewMacroButton.Content = L("NewMacro");
        NewFolderButton.Content = L("NewFolder");
        CopyMacroButton.Content = L("Copy");
        PasteMacroButton.Content = L("Paste");
        DeleteMacroButton.Content = L("Delete");
        GlobalRuntimeTitleText.Text = L("GlobalRuntime");
        PrecisionModeLabelText.Text = L("PrecisionMode");
        AffinityMaskLabelText.Text = L("AffinityMask");
        AffinityMaskHelpText.Text = L("AffinityMaskHelp");
        AffinityMaskBox.ToolTip = L("AffinityMaskHelp");
        ListeningTitleText.Text = L("Listening");
        StartAllListeningButton.Content = L("StartListeningAll");
        StopAllListeningButton.Content = L("StopListeningAll");
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
    }

    public string AffinityMaskText => AffinityMaskBox.Text.Trim();

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
            CaptureExpandedFolders();
        }

        state.ReloadLibrary();
        var selectedId = state.SelectedMacroId ?? state.LibrarySnapshot.SelectedMacroId;
        var sortMode = (LibrarySortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "manual";
        var items = state.LibrarySnapshot.Items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            items = items.Where(item =>
                item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Folder.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        var materializedItems = sortMode switch
        {
            "updated" => items.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            "name" => items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => items.ToList()
        };

        var nodes = new List<MacroLibraryTreeNode>();
        try
        {
            suppressSelection = true;
            var folderNames = state.LibrarySnapshot.Folders
                .Concat(materializedItems.Select(item => item.Folder))
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(folder => folder, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (var folder in folderNames)
            {
                var children = materializedItems
                    .Where(item => string.Equals(item.Folder, folder, StringComparison.Ordinal))
                    .Select(CreateMacroNode)
                    .ToList();
                if (!string.IsNullOrWhiteSpace(search) && children.Count == 0 && !folder.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                    continue;

                var folderNode = MacroLibraryTreeNode.Folder(folder, children);
                folderNode.IsExpanded = !string.IsNullOrWhiteSpace(search)
                    || expandedFolders.Contains(folder);
                folderNode.IsSelected = selectedId is null && string.Equals(selectedFolder, folder, StringComparison.Ordinal);
                MarkSelectedMacro(folderNode.Children, selectedId);
                nodes.Add(folderNode);
            }

            foreach (var item in materializedItems.Where(item => string.IsNullOrWhiteSpace(item.Folder)))
            {
                var macroNode = CreateMacroNode(item);
                macroNode.IsSelected = item.Id == selectedId;
                nodes.Add(macroNode);
            }

            MacroTreeView.ItemsSource = nodes;
        }
        finally
        {
            suppressSelection = false;
        }

        RestoreMacroTreeScroll(previousScrollOffset);
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
        selectedFolder = node.IsFolder ? node.FolderName : node.Item!.Folder;
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
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not MacroLibraryTreeNode node) return;

        BeginRename(node);
        e.Handled = true;
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
            CopySelectionToClipboard();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.V)
        {
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
            state.LibraryStore.MoveMacro(macroId, target.Folder, target.BeforeMacroId);
            state.SelectedMacroId = macroId;
            selectedFolder = target.Folder;
            SelectLibrarySortMode("manual");
            RefreshTree();
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

    private void NewMacro_Click(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        var name = NextMacroName("Macro");
        var item = state.LibraryStore.CreateMacro(name, GetCurrentFolder());
        state.SelectedMacroId = item.Id;
        RefreshTree();
        MacroCreated?.Invoke(item);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        var folder = NextFolderName();
        state.LibraryStore.CreateFolder(folder);
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
        if (MacroTreeView.SelectedItem is MacroLibraryTreeNode { IsFolder: true } folder)
        {
            state.LibraryStore.DeleteFolder(folder.FolderName, deleteMacros: false);
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
        StartListeningAllRequested?.Invoke();
    }

    private void StopAllListening_Click(object sender, RoutedEventArgs e)
    {
        StopListeningAllRequested?.Invoke();
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
                var existing = state.LibraryStore.Load().Items.FirstOrDefault(item =>
                    string.Equals(item.Name, imported.Document.Name, StringComparison.CurrentCultureIgnoreCase));
                lastImported = existing is not null && aliases.Count > 0
                    ? state.LibraryStore.AddAliasesToMacro(existing.Id, aliases)
                    : state.LibraryStore.CreateMacro(imported.Document, aliases: aliases);
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
        var used = state!.LibraryStore.Load().Items.Select(item => item.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{prefix} {i}";
            if (!used.Contains(candidate)) return candidate;
        }
        return $"{prefix} {DateTime.Now:HHmmss}";
    }

    private string NextFolderName()
    {
        var used = state!.LibraryStore.Load().Items.Select(item => item.Folder).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{L("Folder")} {i}";
            if (!used.Contains(candidate)) return candidate;
        }
        return $"{L("Folder")} {DateTime.Now:HHmmss}";
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
            if (node.IsFolder)
            {
                var newName = CreateUniqueFolderName(requestedName, node.FolderName);
                state.LibraryStore.RenameFolder(node.FolderName, newName);
                selectedFolder = newName;
                state.SelectedMacroId = null;
                ResultMessage?.Invoke(LF("FolderRenamed", newName));
            }
            else if (node.Item is { } item)
            {
                var newName = CreateUniqueMacroName(requestedName, item.Folder, item.Id);
                var renamed = state.LibraryStore.RenameMacro(item.Id, newName);
                state.SelectedMacroId = renamed.Id;
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

        clipboard = node.IsFolder
            ? new MacroLibraryClipboardItem(MacroLibraryClipboardKind.Folder, node.FolderName)
            : new MacroLibraryClipboardItem(MacroLibraryClipboardKind.Macro, node.Item!.Id);
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
            var targetFolder = GetCurrentFolder();
            switch (clipboard.Kind)
            {
                case MacroLibraryClipboardKind.Macro:
                    PasteMacro(clipboard.Value, targetFolder);
                    break;
                case MacroLibraryClipboardKind.Folder:
                    PasteFolder(clipboard.Value);
                    break;
            }
        }
        catch (Exception ex)
        {
            ResultMessage?.Invoke(ex.Message);
        }
    }

    private void PasteMacro(string macroId, string targetFolder)
    {
        var snapshot = state!.LibraryStore.Load();
        var source = snapshot.Items.First(item => item.Id == macroId);
        var document = state.LibraryStore.ReadMacro(macroId);
        var copyName = CreateUniqueMacroName($"{source.Name} Copy", targetFolder, null);
        var created = state.LibraryStore.CreateMacro(document with { Name = copyName }, targetFolder);

        state.SelectedMacroId = created.Id;
        selectedFolder = created.Folder;
        RefreshTree();
        MacroSelected?.Invoke(created.Id);
        ResultMessage?.Invoke(LF("MacroPasted", copyName));
    }

    private void PasteFolder(string folder)
    {
        var snapshot = state!.LibraryStore.Load();
        var copiedFolder = CreateUniqueFolderName($"{folder} Copy", null);
        state.LibraryStore.CreateFolder(copiedFolder);

        foreach (var item in snapshot.Items.Where(item => string.Equals(item.Folder, folder, StringComparison.Ordinal)))
        {
            var document = state.LibraryStore.ReadMacro(item.Id);
            state.LibraryStore.CreateMacro(document with { Name = item.Name }, copiedFolder);
        }

        state.SelectedMacroId = null;
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

    private string CreateUniqueMacroName(string requestedName, string folder, string? excludingId)
    {
        var baseName = string.IsNullOrWhiteSpace(requestedName) ? L("Macro") : requestedName.Trim();
        var used = state!.LibraryStore.Load().Items
            .Where(item => !string.Equals(item.Id, excludingId, StringComparison.Ordinal)
                && string.Equals(item.Folder, folder, StringComparison.Ordinal))
            .Select(item => item.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        return CreateUniqueName(baseName, used);
    }

    private string CreateUniqueFolderName(string requestedName, string? excludingFolder)
    {
        var baseName = string.IsNullOrWhiteSpace(requestedName) ? L("Folder") : requestedName.Trim();
        var used = state!.LibraryStore.Load().Folders
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
        }
    }

    private void CaptureExpandedFolders()
    {
        expandedFolders.Clear();
        if (MacroTreeView.ItemsSource is not IEnumerable<MacroLibraryTreeNode> nodes)
        {
            return;
        }

        foreach (var node in nodes.Where(node => node.IsFolder && node.IsExpanded))
        {
            expandedFolders.Add(node.FolderName);
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
        if (MacroTreeView.SelectedItem is MacroLibraryTreeNode node)
        {
            return node.IsFolder ? node.FolderName : node.Item?.Folder ?? string.Empty;
        }

        return selectedFolder ?? string.Empty;
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

        if (node.IsFolder)
        {
            return new MacroLibraryDropTarget(node.FolderName, null, node, MacroLibraryDropIndicator.Into);
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
            : GetNextMacroIdInFolder(target.Id, target.Folder, sourceMacroId);
        var indicator = insertBeforeTarget
            ? MacroLibraryDropIndicator.Before
            : MacroLibraryDropIndicator.After;
        return new MacroLibraryDropTarget(target.Folder, beforeMacroId, node, indicator);
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

    private string? GetNextMacroIdInFolder(string targetMacroId, string folder, string sourceMacroId)
    {
        var seenTarget = false;
        foreach (var node in GetMacroNodesInFolder(folder))
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

    private IEnumerable<MacroLibraryTreeNode> GetMacroNodesInFolder(string folder)
    {
        if (MacroTreeView.ItemsSource is not IEnumerable<MacroLibraryTreeNode> nodes)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            foreach (var node in nodes.Where(node => node.Item is not null))
            {
                yield return node;
            }

            yield break;
        }

        var folderNode = nodes.FirstOrDefault(node => node.IsFolder && string.Equals(node.FolderName, folder, StringComparison.Ordinal));
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

    private sealed record MacroLibraryClipboardItem(MacroLibraryClipboardKind Kind, string Value);

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
        string Folder,
        string? BeforeMacroId,
        MacroLibraryTreeNode? Node,
        MacroLibraryDropIndicator Indicator,
        bool IsNoOp = false)
    {
        public static MacroLibraryDropTarget NoOp { get; } = new(
            string.Empty,
            null,
            null,
            MacroLibraryDropIndicator.None,
            IsNoOp: true);
    }
}
