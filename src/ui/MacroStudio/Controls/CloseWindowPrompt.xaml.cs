using System.Windows;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class CloseWindowPrompt : Window
{
    public CloseWindowPrompt()
    {
        InitializeComponent();
        ThemedDialogChrome.Apply(this);
        Title = L("CloseWindowPromptTitle");
        PromptText.Text = L("CloseWindowPromptText");
        TrayTitleText.Text = L("CloseToTray");
        TrayHintText.Text = L("CloseToTrayHint");
        ExitTitleText.Text = L("CloseAndExit");
        ExitHintText.Text = L("CloseAndExitHint");
        RememberCheck.Content = L("RememberCloseChoice");
    }

    public CloseButtonBehavior Choice { get; private set; } = CloseButtonBehavior.Ask;

    public bool Remember => RememberCheck.IsChecked == true;

    private void TrayButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseButtonBehavior.MinimizeToTray;
        DialogResult = true;
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseButtonBehavior.Exit;
        DialogResult = true;
    }

    private static string L(string key) => LocalizationService.Get(key);
}
