using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MacroStudio.Controls;

public class CollapsibleSection : HeaderedContentControl
{
    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(CollapsibleSection),
            new PropertyMetadata(true, OnIsExpandedChanged));

    private Border? contentHost;
    private ScaleTransform? contentScale;

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    static CollapsibleSection()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CollapsibleSection),
            new FrameworkPropertyMetadata(typeof(CollapsibleSection)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        contentHost = GetTemplateChild("PART_ContentHost") as Border;
        if (contentHost != null)
        {
            contentScale = new ScaleTransform(1, IsExpanded ? 1 : 0);
            contentHost.LayoutTransform = contentScale;
        }

        if (GetTemplateChild("PART_ToggleButton") is Button toggleBtn)
            toggleBtn.Click += (_, _) => IsExpanded = !IsExpanded;
    }

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CollapsibleSection section)
            section.AnimateExpansion((bool)e.NewValue);
    }

    private void AnimateExpansion(bool expand)
    {
        if (contentScale == null) return;

        var animation = new DoubleAnimation(expand ? 1 : 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = expand ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        contentScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
