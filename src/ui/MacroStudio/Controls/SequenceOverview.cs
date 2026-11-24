using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacroHid.Core;

namespace MacroStudio.Controls;

/// <summary>A compact view of the top-level steps. The horizontal axis follows step order.</summary>
public sealed class SequenceOverview : Control
{
    private const int MaxMarkers = 44;
    private const double Inset = 10;
    private IReadOnlyList<MacroStep> steps = [];
    private int selectedIndex = -1;
    private int hoverIndex = -1;

    static SequenceOverview()
    {
        ForegroundProperty.OverrideMetadata(typeof(SequenceOverview),
            new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));
        BorderBrushProperty.OverrideMetadata(typeof(SequenceOverview),
            new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));
    }

    internal event Action<int>? StepRequested;

    internal void SetSteps(IReadOnlyList<MacroStep> value)
    {
        steps = value;
        if (hoverIndex >= steps.Count) hoverIndex = -1;
        InvalidateVisual();
    }

    internal void SetSelectedIndex(int value)
    {
        selectedIndex = value;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var width = ActualWidth - Inset * 2;
        var height = ActualHeight - Inset * 2;
        if (width <= 0 || height <= 0) return;
        var bottom = Inset + height;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var guide = new Pen(BorderBrush, 0.7);
        drawing.DrawRoundedRectangle(null, guide, new Rect(Inset, Inset, width, height), 3, 3);
        for (var i = 1; i < 3; i++)
        {
            var y = Inset + i * height / 3;
            drawing.DrawLine(guide, new Point(Inset, y), new Point(Inset + width, y));
        }

        if (steps.Count == 0) return;
        var markerCount = Math.Min(MaxMarkers, steps.Count);
        var stride = width / markerCount;
        var trace = new StreamGeometry();
        using (var traceWriter = trace.Open())
        {
            traceWriter.BeginFigure(new Point(Inset, bottom - height * 0.15), false, false);

            for (var marker = 0; marker < markerCount; marker++)
            {
                var from = marker * (long)steps.Count / markerCount;
                var to = (marker + 1) * (long)steps.Count / markerCount;
                var sample = (int)((from + to - 1) / 2);
                var (level, resource) = StepStyle(steps[sample]);
                var fill = TryFindResource(resource) as Brush ?? Foreground;
                var selected = selectedIndex >= from && selectedIndex < to;
                var hovered = hoverIndex >= from && hoverIndex < to;
                var x = Inset + marker * stride;
                var y = bottom - level * height;
                var columnWidth = Math.Min(21, Math.Max(2, stride - 3));
                var column = new Rect(x + (stride - columnWidth) / 2, y, columnWidth, bottom - y);
                drawing.PushOpacity(selected || hovered ? 0.95 : 0.23);
                drawing.DrawRoundedRectangle(fill, null, column, 2, 2);
                drawing.Pop();
                var point = new Point(x + stride / 2, y);
                traceWriter.LineTo(point, true, false);
                drawing.DrawEllipse(fill, null, point, selected || hovered ? 3.2 : 1.9, selected || hovered ? 3.2 : 1.9);
                if (selected || hovered)
                {
                    drawing.DrawLine(new Pen(fill, 0.8), new Point(point.X, Inset), new Point(point.X, bottom));
                }
            }

            traceWriter.LineTo(new Point(Inset + width, bottom - height * 0.15), true, false);
        }
        drawing.PushOpacity(0.22);
        drawing.DrawGeometry(null, new Pen(Foreground, 4), trace);
        drawing.Pop();
        drawing.DrawGeometry(null, new Pen(Foreground, 1.2), trace);
        if (IsKeyboardFocused)
        {
            drawing.DrawRoundedRectangle(null, new Pen(Foreground, 1.5), new Rect(1, 1, ActualWidth - 2, ActualHeight - 2), 5, 5);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = StepAt(e.GetPosition(this).X);
        if (hoverIndex == index) return;
        hoverIndex = index;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        hoverIndex = -1;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var index = StepAt(e.GetPosition(this).X);
        if (index < 0) return;
        Focus();
        StepRequested?.Invoke(index);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (steps.Count == 0 || e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        var nextIndex = e.Key switch
        {
            Key.Home => 0,
            Key.End => steps.Count - 1,
            _ => Math.Clamp(selectedIndex + (e.Key == Key.Left ? -1 : 1), 0, steps.Count - 1)
        };
        StepRequested?.Invoke(nextIndex);
        e.Handled = true;
    }

    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        InvalidateVisual();
    }

    private int StepAt(double x)
    {
        var width = ActualWidth - Inset * 2;
        if (steps.Count == 0 || width <= 0) return -1;
        var position = Math.Clamp((x - Inset) / width, 0, 1);
        return Math.Min(steps.Count - 1, (int)(position * steps.Count));
    }

    private static (double Height, string Color) StepStyle(MacroStep step) => step switch
    {
        WaitStep => (0.27, "SecondaryText"),
        KeyStep or ConsumerStep => (0.63, "AccentBlue"),
        MouseMoveStep or MouseButtonStep or MouseWheelStep => (0.5, "StudioSuccess"),
        CommentStep => (0.13, "SecondaryText"),
        RepeatStep or ParallelStep or MacroCallStep => (0.88, "AccentPink"),
        _ => (0.74, "Accent")
    };
}
