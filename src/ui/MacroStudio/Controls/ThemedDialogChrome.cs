using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace MacroStudio.Controls;

public static class ThemedDialogChrome
{
    public static void Apply(Window window)
    {
        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        window.FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");

        var resizeThickness = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip
            ? new Thickness(6)
            : new Thickness(0);
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(12),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = resizeThickness,
            UseAeroCaptionButtons = false
        });

        if (window.Content is not FrameworkElement body)
        {
            return;
        }

        window.Content = null;
        body.Margin = new Thickness(16, 4, 16, 16);

        var title = new TextBlock();
        title.Style = (Style)window.FindResource("ToolWindowTitle");
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });

        var closeButton = new Button
        {
            Content = "\uE711",
            ToolTip = "关闭"
        };
        closeButton.Style = (Style)window.FindResource("ToolWindowChromeButton");
        closeButton.Click += (_, _) => window.Close();
        WindowChrome.SetIsHitTestVisibleInChrome(closeButton, true);

        var headerDock = new DockPanel();
        DockPanel.SetDock(closeButton, Dock.Right);
        headerDock.Children.Add(closeButton);
        headerDock.Children.Add(title);

        var header = new Border
        {
            Style = (Style)window.FindResource("ThemedDialogHeaderStyle"),
            Child = headerDock
        };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || IsInsideButton(e.OriginalSource as DependencyObject))
            {
                return;
            }

            try
            {
                window.DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);

        window.Content = new Border
        {
            Style = (Style)window.FindResource("ThemedDialogRootStyle"),
            Child = root
        };
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
