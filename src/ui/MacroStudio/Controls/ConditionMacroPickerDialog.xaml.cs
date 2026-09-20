using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class ConditionMacroPickerDialog : Window
{
    private readonly IReadOnlyList<ConditionMacroPickItem> items;

    public ConditionMacroPickerDialog(IReadOnlyList<ConditionMacroPickItem> items)
    {
        InitializeComponent();
        ThemedDialogChrome.Apply(this);
        this.items = items;
        Title = L("ConditionMacroPickerTitle");
        CancelButton.Content = L("Cancel");
        SelectButton.Content = L("InsertConditionMacro");
        EmptyText.Text = L("NoConditionMacrosInDatabase");
        FilterPlaceholderText.Text = L("FilterConditionMacrosPlaceholder");
        FilterBox.ToolTip = L("FilterConditionMacrosPlaceholder");
        Loaded += (_, _) => ApplyFilter();
    }

    public string? SelectedMacroId { get; private set; }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void MacroList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

    private void SelectButton_Click(object sender, RoutedEventArgs e) => AcceptSelection();

    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? items
            : items.Where(item =>
                    item.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                    || item.LocationText.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        MacroList.ItemsSource = filtered;
        if (MacroList.Items.Count > 0 && MacroList.SelectedIndex < 0)
        {
            MacroList.SelectedIndex = 0;
        }

        EmptyText.Visibility = MacroList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectButton.IsEnabled = MacroList.Items.Count > 0;
    }

    private void AcceptSelection()
    {
        if (MacroList.SelectedItem is not ConditionMacroPickItem item) return;
        SelectedMacroId = item.Id;
        DialogResult = true;
    }

    private static string L(string key) => LocalizationService.Get(key);
}

public sealed record ConditionMacroPickItem(string Id, string Name, string LocationText)
{
    public static ConditionMacroPickItem FromLibraryItem(MacroLibraryItem item)
    {
        var location = string.IsNullOrWhiteSpace(item.Folder)
            ? LocalizationService.Get("DatabaseRootFolder")
            : item.Folder;
        return new ConditionMacroPickItem(item.Id, item.Name, location);
    }
}
