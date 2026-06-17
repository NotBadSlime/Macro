using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace MacroStudio.Controls;

public partial class ScreenCoordinatePickerWindow : Window
{
    public ScreenCoordinatePickerWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    public int SelectedX { get; private set; }
    public int SelectedY { get; private set; }

    private void PickerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Focus();
    }

    private void PickerWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        SelectedX = point.X;
        SelectedY = point.Y;
        DialogResult = true;
        Close();
    }

    private void PickerWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        DialogResult = false;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
