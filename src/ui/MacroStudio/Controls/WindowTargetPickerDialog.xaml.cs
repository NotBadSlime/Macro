using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class WindowTargetPickerDialog : Window
{
    private IReadOnlyList<WindowTargetInfo> targets = [];

    public WindowTargetPickerDialog()
    {
        InitializeComponent();
        ThemedDialogChrome.Apply(this);
        Title = L("WindowTargetPickerTitle");
        RefreshButton.Content = L("Refresh");
        CancelButton.Content = L("Cancel");
        SelectButton.Content = L("SelectWindow");
        EmptyText.Text = L("NoWindowsFound");
        FilterPlaceholderText.Text = L("FilterWindowsPlaceholder");
        FilterBox.ToolTip = L("FilterWindowsPlaceholder");
        Loaded += (_, _) => RefreshTargets();
    }

    public WindowTargetInfo? SelectedTarget { get; private set; }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshTargets();

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void WindowList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

    private void SelectButton_Click(object sender, RoutedEventArgs e) => AcceptSelection();

    private void RefreshTargets()
    {
        targets = WindowActivationService.EnumerateVisibleWindows()
            .OrderBy(target => target.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(target => target.WindowTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? targets
            : targets.Where(target =>
                    target.ProcessName.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                    || target.WindowTitle.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        WindowList.ItemsSource = filtered;
        if (WindowList.Items.Count > 0 && WindowList.SelectedIndex < 0)
        {
            WindowList.SelectedIndex = 0;
        }
        EmptyText.Visibility = WindowList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectButton.IsEnabled = WindowList.Items.Count > 0;
    }

    private void AcceptSelection()
    {
        if (WindowList.SelectedItem is not WindowTargetInfo target) return;
        SelectedTarget = target;
        DialogResult = true;
    }

    private static string L(string key) => LocalizationService.Get(key);
}
