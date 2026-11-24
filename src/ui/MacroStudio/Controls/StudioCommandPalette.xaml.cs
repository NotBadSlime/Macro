using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace MacroStudio.Controls;

internal sealed record StudioCommand(string Glyph, string Label, string SearchTerms, Action Execute);

public partial class StudioCommandPalette : UserControl
{
    private IReadOnlyList<StudioCommand> commands = [];

    public StudioCommandPalette()
    {
        InitializeComponent();
    }

    internal event Action? Dismissed;

    internal void Open(IReadOnlyList<StudioCommand> items, bool animate)
    {
        commands = items;
        var title = LocalizationService.Get("StudioCommandSearch");
        QueryHintText.Text = title;
        AutomationProperties.SetName(QueryBox, title);
        AutomationProperties.SetName(CommandList, title);
        DismissButton.ToolTip = LocalizationService.Get("CloseWindow");
        NoResultsText.Text = LocalizationService.Get("StudioNoMatchingCommands");
        QueryBox.Clear();
        ApplyFilter();
        Visibility = Visibility.Visible;
        if (animate && SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130))
            {
                FillBehavior = FillBehavior.Stop
            });
        }
        Keyboard.Focus(QueryBox);
    }

    internal void Close()
    {
        if (Visibility != Visibility.Visible) return;
        Visibility = Visibility.Collapsed;
        Dismissed?.Invoke();
    }

    private void ApplyFilter()
    {
        var terms = QueryBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var comparer = CultureInfo.CurrentUICulture.CompareInfo;
        var matches = commands.Where(command => terms.All(term =>
            comparer.IndexOf(command.Label, term, CompareOptions.IgnoreCase) >= 0 ||
            comparer.IndexOf(command.SearchTerms, term, CompareOptions.IgnoreCase) >= 0)).ToArray();
        CommandList.ItemsSource = matches;
        CommandList.SelectedIndex = matches.Length > 0 ? 0 : -1;
        NoResultsText.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Palette_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Enter when !DismissButton.IsKeyboardFocusWithin:
                Invoke(CommandList.SelectedItem as StudioCommand);
                break;
            case Key.Up or Key.Down when CommandList.HasItems:
                var offset = e.Key == Key.Down ? 1 : -1;
                var index = (CommandList.SelectedIndex + offset + CommandList.Items.Count) % CommandList.Items.Count;
                CommandList.SelectedIndex = index;
                CommandList.ScrollIntoView(CommandList.SelectedItem);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void CommandList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(CommandList, e.OriginalSource as DependencyObject) is ListBoxItem item)
        {
            Invoke(item.DataContext as StudioCommand);
            e.Handled = true;
        }
    }

    private void Backdrop_Click(object sender, MouseButtonEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Invoke(StudioCommand? command)
    {
        if (command is null) return;
        Close();
        command.Execute();
    }
}
