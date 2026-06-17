using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacroHid.Converter;
using MacroHid.Core;
using MacroStudio.Services;
using Microsoft.Win32;

namespace MacroStudio.Controls;

public partial class MacroLibraryPanel : UserControl
{
    private const string MacroLibraryDragFormat = "MacroHID.MacroLibraryItem";

    private MacroEditorState? state;
    private bool suppressSelection;
    private List<AuxiliaryMacroFile> razerModuleFiles = [];
    private string? selectedFolder;
    private MacroLibraryClipboardItem? clipboard;
    private MacroLibraryTreeNode? renamingNode;

    public event Action<string>? MacroSelected;
    public event Action<string>? MacroDuplicated;
    public event Action<string>? MacroDeleted;
    public event Action<MacroLibraryItem>? MacroCreated;
    public event Action<MacroDocument>? ImportApplied;
    public event Func<MacroDocument>? DocumentRequested;
    public event Action<string>? ResultMessage;

    public MacroLibraryPanel()
    {
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
        LibraryTitleText.Text = L("MacroLibrary");
        NewMacroButton.Content = L("NewMacro");
        NewFolderButton.Content = L("NewFolder");
        CopyMacroButton.Content = L("Copy");
        PasteMacroButton.Content = L("Paste");
        DeleteMacroButton.Content = L("Delete");
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
                "updated" => L("SortRecentlyUpdated"),
                _ => L("SortName")
            };
        }
    }

    public void RefreshList()
    {
        RefreshTree();
    }

    public void RefreshTree()
    {
        if (state is null) return;

        state.ReloadLibrary();
        var selectedId = state.SelectedMacroId ?? state.LibrarySnapshot.SelectedMacroId;
        var search = MacroSearchBox.Text.Trim();
        var sortMode = (LibrarySortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "name";
        var items = state.LibrarySnapshot.Items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            items = items.Where(item =>
                item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Folder.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        var materializedItems = (sortMode == "updated"
            ? items.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            : items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            .ToList();

        suppressSelection = true;
        var nodes = new List<MacroLibraryTreeNode>();
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
        suppressSelection = false;
    }

    private void MacroSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

    private void LibrarySortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MacroTreeView is not null) RefreshTree();
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

    private void MacroTreeView_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (MacroTreeView.SelectedItem is not MacroLibraryTreeNode { Item: { } item }) return;

        DragDrop.DoDragDrop(MacroTreeView, new DataObject(MacroLibraryDragFormat, item.Id), DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void MacroTreeView_Drop(object sender, DragEventArgs e)
    {
        if (state is null) return;
        if (!e.Data.GetDataPresent(MacroLibraryDragFormat)) return;
        if (e.Data.GetData(MacroLibraryDragFormat) is not string macroId) return;

        var targetFolder = GetDropTargetFolder(e.OriginalSource as DependencyObject);
        try
        {
            state.LibraryStore.MoveMacro(macroId, targetFolder);
            state.SelectedMacroId = macroId;
            selectedFolder = targetFolder;
            RefreshTree();
            ResultMessage?.Invoke(string.IsNullOrWhiteSpace(targetFolder)
                ? L("MacroMovedToRoot")
                : LF("MacroMovedToFolder", targetFolder));
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

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

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

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

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

            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

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
        return MacroLibraryTreeNode.Macro(item, document);
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

    private string GetCurrentFolder()
    {
        if (MacroTreeView.SelectedItem is MacroLibraryTreeNode node)
        {
            return node.IsFolder ? node.FolderName : node.Item?.Folder ?? string.Empty;
        }

        return selectedFolder ?? string.Empty;
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

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static string L(string key) => LocalizationService.Get(key);
    private static string LF(string key, params object[] args) => LocalizationService.Format(key, args);

    private sealed record MacroLibraryClipboardItem(MacroLibraryClipboardKind Kind, string Value);

    private enum MacroLibraryClipboardKind
    {
        Macro,
        Folder
    }
}
