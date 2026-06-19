using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class ScreenRegionPicker : Window
{
    private Point? startPoint;
    private bool isDragging;
    public ScreenRegion? SelectedRegion { get; private set; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    public ScreenRegionPicker()
    {
        InitializeComponent();
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        startPoint = e.GetPosition(SelectionCanvas);
        isDragging = true;
        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, startPoint.Value.X);
        Canvas.SetTop(SelectionRect, startPoint.Value.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(SelectionCanvas);
        var screenPos = PointToScreen(pos);

        CoordText.Text = $"X: {(int)screenPos.X}  Y: {(int)screenPos.Y}";

        UpdatePixelPreview((int)screenPos.X, (int)screenPos.Y, pos);

        if (!isDragging || startPoint == null) return;

        var x = Math.Min(pos.X, startPoint.Value.X);
        var y = Math.Min(pos.Y, startPoint.Value.Y);
        var w = Math.Abs(pos.X - startPoint.Value.X);
        var h = Math.Abs(pos.Y - startPoint.Value.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
        SizeLabel.Text = $"{(int)w} × {(int)h}";
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!isDragging || startPoint == null) return;
        ReleaseMouseCapture();
        isDragging = false;

        var endPoint = e.GetPosition(SelectionCanvas);
        var screenStart = PointToScreen(startPoint.Value);
        var screenEnd = PointToScreen(endPoint);

        var left = (int)Math.Min(screenStart.X, screenEnd.X);
        var top = (int)Math.Min(screenStart.Y, screenEnd.Y);
        var right = (int)Math.Max(screenStart.X, screenEnd.X);
        var bottom = (int)Math.Max(screenStart.Y, screenEnd.Y);

        if (right - left < 2 && bottom - top < 2)
        {
            SelectedRegion = ScreenRegion.FromSinglePixel(left, top);
        }
        else
        {
            SelectedRegion = ScreenRegion.FromRect(left, top, right, bottom);
        }

        DialogResult = true;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void UpdatePixelPreview(int screenX, int screenY, Point canvasPos)
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return;

        try
        {
            var colorRef = GetPixel(hdc, screenX, screenY);
            if (colorRef == 0xFFFFFFFF) return;

            byte r = (byte)(colorRef & 0xFF);
            byte g = (byte)((colorRef >> 8) & 0xFF);
            byte b = (byte)((colorRef >> 16) & 0xFF);

            PixelColorPreview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            PixelColorText.Text = $"#{r:X2}{g:X2}{b:X2}";
            CursorPreview.Visibility = Visibility.Visible;

            Canvas.SetLeft(CursorPreview, canvasPos.X + 20);
            Canvas.SetTop(CursorPreview, canvasPos.Y + 20);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    public static ScreenRegion? PickRegion(Window? owner = null)
    {
        var picker = new ScreenRegionPicker();
        var result = DialogOwnerService.ShowDialogSafe(picker, owner);
        return result == true ? picker.SelectedRegion : null;
    }
}
