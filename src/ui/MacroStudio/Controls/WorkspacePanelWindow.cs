using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MacroStudio.Controls;

public sealed class WorkspacePanelWindow : Window
{
    private bool forceClosing;
    private readonly Button dockButton;
    private readonly TextBlock titleText;

    public WorkspacePanelWindow(string panelId, string title)
    {
        PanelId = panelId;
        Title = title;
        Width = 520;
        Height = 460;
        MinWidth = 280;
        MinHeight = 220;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;

        ContentHost = new ContentControl();

        dockButton = new Button
        {
            Content = "停靠",
            MinWidth = 72,
            Margin = new Thickness(8, 0, 0, 0)
        };
        dockButton.Click += (_, _) => DockRequested?.Invoke(this, EventArgs.Empty);

        titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var header = new DockPanel { Margin = new Thickness(12, 10, 12, 8) };
        DockPanel.SetDock(dockButton, Dock.Right);
        header.Children.Add(dockButton);
        header.Children.Add(titleText);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(ContentHost);
        Content = root;
    }

    public string PanelId { get; }
    public ContentControl ContentHost { get; }

    public event EventHandler? DockRequested;
    public event EventHandler? HideRequested;

    public void SetTitle(string title, string dockText)
    {
        Title = title;
        titleText.Text = title;
        dockButton.Content = dockText;
    }

    public void ForceClose()
    {
        forceClosing = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!forceClosing)
        {
            e.Cancel = true;
            Hide();
            HideRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.OnClosing(e);
    }
}
