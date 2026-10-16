using System.Windows.Media;

namespace MacroStudio.Controls;

/// <summary>
/// Saturated accent colours used for icons, badges and condition ranges. They are foreground/stroke colours that keep at
/// least 3:1 contrast on both the light and dark panels, so they are shared by both themes; surfaces use theme brushes.
/// Brushes are frozen: safe to share across threads and cheaper to render.
/// </summary>
public static class AccentPalette
{
    public static readonly Brush Green = Frozen(52, 199, 89);
    public static readonly Brush Blue = Frozen(0, 122, 255);
    public static readonly Brush Orange = Frozen(255, 149, 0);
    public static readonly Brush Pink = Frozen(255, 45, 85);
    public static readonly Brush Red = Frozen(255, 59, 48);
    public static readonly Brush Gray = Frozen(142, 142, 147);
    public static readonly Brush Loop = Frozen(239, 108, 0);

    /// <summary>Default colours for condition ranges, cycled by index.</summary>
    public static readonly IReadOnlyList<Brush> Conditions =
    [
        Frozen(99, 102, 241),
        Frozen(16, 185, 129),
        Frozen(245, 158, 11),
        Frozen(239, 68, 68),
        Frozen(139, 92, 246),
        Frozen(6, 182, 212),
    ];

    public static Brush Condition(int index) => Conditions[((index % Conditions.Count) + Conditions.Count) % Conditions.Count];

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
