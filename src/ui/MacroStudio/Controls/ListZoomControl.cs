using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MacroStudio.Controls;

public sealed class ListZoomControl : StackPanel
{
    public static readonly DependencyProperty TargetElementProperty = DependencyProperty.Register(
        nameof(TargetElement), typeof(FrameworkElement), typeof(ListZoomControl),
        new PropertyMetadata(null, OnTargetChanged));

    private readonly ScaleTransform scale = new(1, 1);
    private readonly Button minus;
    private readonly Button reset;
    private readonly Button plus;

    public ListZoomControl()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        minus = CreateButton("−", 26, "缩小列表（Ctrl + 滚轮）");
        reset = CreateButton("100%", 48, "恢复原始大小");
        plus = CreateButton("+", 26, "放大列表（Ctrl + 滚轮）");
        minus.Click += (_, _) => SetZoom(scale.ScaleX - 0.1);
        reset.Click += (_, _) => SetZoom(1);
        plus.Click += (_, _) => SetZoom(scale.ScaleX + 0.1);
    }

    public FrameworkElement? TargetElement
    {
        get => (FrameworkElement?)GetValue(TargetElementProperty);
        set => SetValue(TargetElementProperty, value);
    }

    private Button CreateButton(string text, double width, string hint)
    {
        var button = new Button { Content = text, Width = width, Height = 26,
            Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), FontSize = 12, ToolTip = hint };
        Children.Add(button);
        return button;
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ListZoomControl)d;
        if (e.OldValue is FrameworkElement oldTarget)
        {
            oldTarget.PreviewMouseWheel -= control.OnMouseWheel;
            if (ReferenceEquals(oldTarget.LayoutTransform, control.scale)) oldTarget.ClearValue(LayoutTransformProperty);
        }
        if (e.NewValue is FrameworkElement target)
        {
            target.LayoutTransform = control.scale;
            target.PreviewMouseWheel += control.OnMouseWheel;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || Mouse.LeftButton == MouseButtonState.Pressed) return;
        SetZoom(scale.ScaleX + (e.Delta > 0 ? 0.1 : -0.1));
        e.Handled = true;
    }

    private void SetZoom(double value)
    {
        var zoom = Math.Round(Math.Clamp(value, 0.6, 1.5), 1);
        scale.ScaleX = scale.ScaleY = zoom;
        reset.Content = $"{zoom * 100:0}%";
        minus.IsEnabled = zoom > 0.6;
        plus.IsEnabled = zoom < 1.5;
    }
}
