using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        ContentHost = new ContentControl
        {
            Style = (Style)Application.Current.FindResource("ToolWindowFloatingContentStyle")
        };

        dockButton = new Button
        {
            Content = "\uE73F",
            ToolTip = "停靠",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol")
        };
        dockButton.Style = (Style)Application.Current.FindResource("ToolWindowChromeButton");
        dockButton.Click += (_, _) => DockRequested?.Invoke(this, EventArgs.Empty);

        var closeButton = new Button
        {
            Content = "\uE711",
            ToolTip = "关闭",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol")
        };
        closeButton.Style = (Style)Application.Current.FindResource("ToolWindowChromeButton");
        closeButton.Click += (_, _) => HidePanel();

        titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleText.Style = (Style)Application.Current.FindResource("ToolWindowTitle");

        var header = new Border
        {
            Style = (Style)Application.Current.FindResource("ToolWindowHeaderStyle")
        };
        header.MouseLeftButtonDown += ToolWindowHeader_MouseLeftButtonDown;
        var headerDock = new DockPanel();
        DockPanel.SetDock(closeButton, Dock.Right);
        DockPanel.SetDock(dockButton, Dock.Right);
        headerDock.Children.Add(closeButton);
        headerDock.Children.Add(dockButton);
        headerDock.Children.Add(titleText);
        header.Child = headerDock;

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(ContentHost);

        var chromeRoot = new Border
        {
            Style = (Style)Application.Current.FindResource("ToolWindowFloatingRootStyle"),
            Child = root
        };

        var ResizeBottomThumb = new Thumb
        {
            Cursor = Cursors.SizeNS,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Template = CreateTransparentThumbTemplate(),
            Focusable = false
        };
        ResizeBottomThumb.DragDelta += (_, e) => ResizeWindow(0, e.VerticalChange);
        Panel.SetZIndex(ResizeBottomThumb, 20);

        var ResizeRightThumb = new Thumb
        {
            Cursor = Cursors.SizeWE,
            Width = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Template = CreateTransparentThumbTemplate(),
            Focusable = false
        };
        ResizeRightThumb.DragDelta += (_, e) => ResizeWindow(e.HorizontalChange, 0);
        Panel.SetZIndex(ResizeRightThumb, 20);

        var ResizeCornerThumb = new Thumb
        {
            Cursor = Cursors.SizeNWSE,
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Template = CreateTransparentThumbTemplate(),
            Focusable = false
        };
        ResizeCornerThumb.DragDelta += (_, e) => ResizeWindow(e.HorizontalChange, e.VerticalChange);
        Panel.SetZIndex(ResizeCornerThumb, 21);

        var windowRoot = new Grid();
        windowRoot.Children.Add(chromeRoot);
        windowRoot.Children.Add(ResizeBottomThumb);
        windowRoot.Children.Add(ResizeRightThumb);
        windowRoot.Children.Add(ResizeCornerThumb);
        Content = windowRoot;
    }

    public string PanelId { get; }
    public ContentControl ContentHost { get; }

    public event EventHandler? DockRequested;
    public event EventHandler? HideRequested;

    public void SetTitle(string title, string dockText)
    {
        Title = title;
        titleText.Text = title;
        dockButton.ToolTip = dockText;
    }

    private void HidePanel()
    {
        Hide();
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ToolWindowHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the mouse button state changes during activation.
        }
    }

    private void ResizeWindow(double widthDelta, double heightDelta)
    {
        if (Math.Abs(widthDelta) > 0.01)
        {
            Width = Math.Max(MinWidth, Width + widthDelta);
        }

        if (Math.Abs(heightDelta) > 0.01)
        {
            Height = Math.Max(MinHeight, Height + heightDelta);
        }
    }

    private static ControlTemplate CreateTransparentThumbTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        return new ControlTemplate(typeof(Thumb))
        {
            VisualTree = border
        };
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
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
            HidePanel();
            return;
        }

        base.OnClosing(e);
    }
}
