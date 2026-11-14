using System.IO;
using System.Windows;
using System.Windows.Controls;
using MacroHid.Control;

namespace MacroStudio.Controls;

public sealed class GlobalCommandDialog : Window
{
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
    private readonly string studioPath = Path.Combine(AppContext.BaseDirectory, "MacroStudio.exe");
    public GlobalCommandDialog()
    {
        Title = LocalizationService.Get("GlobalCommandTitle");
        Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock { Text = LocalizationService.Get("GlobalCommandHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var buttons = new WrapPanel();
        AddButton(buttons, "GlobalCommandRegister", () => GlobalCommand.Register(studioPath, GlobalCommand.FindCommandDirectory(AppContext.BaseDirectory)));
        AddButton(buttons, "GlobalCommandUnregister", () => { if (!GlobalCommand.Unregister(studioPath)) throw new InvalidOperationException(LocalizationService.Get("GlobalCommandOtherVersion")); });
        AddButton(buttons, "GlobalCommandCheck", () => { });
        panel.Children.Add(buttons);
        Content = panel;
        ThemedDialogChrome.Apply(this);
        RefreshStatus();
    }
    private void AddButton(Panel panel, string key, Action action)
    {
        var button = new Button { Content = LocalizationService.Get(key), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        button.Click += (_, _) =>
        {
            try { action(); RefreshStatus(); }
            catch (Exception ex) { status.Text = ex.Message + "\n\n" + GlobalCommand.Describe(); }
        };
        panel.Children.Add(button);
    }
    private void RefreshStatus()
    {
        try { status.Text = GlobalCommand.Describe(); }
        catch (Exception ex) { status.Text = ex.Message; }
    }
}
