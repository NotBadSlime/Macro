using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MacroStudio.Controls;

public sealed class ColorSampleOverlayWindow : Window
{
    private readonly DispatcherTimer hideTimer;

    public ColorSampleOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;
        hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        hideTimer.Tick += (_, _) => HideOverlay();
    }

    public void ShowSample(string text)
    {
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 28, 30, 34)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 84, 92)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10, 16, 10),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold
            }
        };

        if (GetCursorPos(out var point))
        {
            Left = point.X + 18;
            Top = point.Y + 18;
        }

        hideTimer.Stop();
        if (!IsVisible)
        {
            Show();
        }

        hideTimer.Start();
    }

    private void HideOverlay()
    {
        hideTimer.Stop();
        Hide();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
