using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MacroStudio.Controls;

public class SmoothGridSplitter : GridSplitter
{
    private bool isDragging;
    private Border? gripBar;

    public SmoothGridSplitter()
    {
        Width = 14;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = Brushes.Transparent;
        Cursor = Cursors.SizeWE;
        ShowsPreview = false;

        DragStarted += OnDragStarted;
        DragCompleted += OnDragCompleted;
        MouseDoubleClick += OnDoubleClick;
        MouseEnter += OnSplitterMouseEnter;
        MouseLeave += OnSplitterMouseLeave;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (gripBar != null || Parent is not Grid grid)
            return;

        gripBar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = TryFindResource("SecondaryText") as Brush ?? Brushes.Gray,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Height = 40,
            IsHitTestVisible = false
        };

        var col = Grid.GetColumn(this);
        gripBar.SetValue(Grid.ColumnProperty, col);
        grid.Children.Add(gripBar);
    }

    private void OnSplitterMouseEnter(object sender, MouseEventArgs e)
    {
        if (gripBar == null) return;
        var fadeIn = new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        gripBar.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void OnSplitterMouseLeave(object sender, MouseEventArgs e)
    {
        if (gripBar == null || isDragging) return;
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        gripBar.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void OnDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        isDragging = true;
        // GridSplitter already resizes both adjacent columns and preserves star sizing.
    }

    private void OnDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        isDragging = false;

        if (!IsMouseOver && gripBar != null)
        {
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            gripBar.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var targetColumn = GetResizeColumn();

        if (targetColumn is null)
            return;

        var defaultWidth = targetColumn.MinWidth > 0 ? targetColumn.MinWidth * 1.2 : 300;
        var animation = new DoubleAnimation(targetColumn.ActualWidth, defaultWidth, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        var helper = new GridColumnAnimationHelper(targetColumn);
        helper.Animate(animation);
    }

    private ColumnDefinition? GetResizeColumn()
    {
        if (Parent is not Grid grid)
            return null;

        var col = Grid.GetColumn(this);
        var resizeCol = col > 0 ? col - 1 : col + 1;
        if (resizeCol >= 0 && resizeCol < grid.ColumnDefinitions.Count)
        {
            var previous = grid.ColumnDefinitions[resizeCol];
            // On the right dock, reset the fixed right column, not the flexible center.
            if (previous.Width.IsStar && col + 1 < grid.ColumnDefinitions.Count)
                return grid.ColumnDefinitions[col + 1];
            return previous;
        }

        return null;
    }

    private sealed class GridColumnAnimationHelper
    {
        private readonly ColumnDefinition column;
        private DoubleAnimation? animation;
        private long startTick;

        public GridColumnAnimationHelper(ColumnDefinition column)
        {
            this.column = column;
        }

        public void Animate(DoubleAnimation anim)
        {
            animation = anim;
            startTick = Environment.TickCount64;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            if (animation is null)
            {
                CompositionTarget.Rendering -= OnFrame;
                return;
            }

            var elapsed = Environment.TickCount64 - startTick;
            var duration = animation.Duration.TimeSpan.TotalMilliseconds;
            var progress = Math.Min(1.0, elapsed / duration);

            var easedProgress = animation.EasingFunction?.Ease(progress) ?? progress;
            var from = animation.From ?? column.ActualWidth;
            var to = animation.To ?? column.ActualWidth;
            var value = from + (to - from) * easedProgress;
            column.Width = new GridLength(value, GridUnitType.Pixel);

            if (progress >= 1.0)
            {
                CompositionTarget.Rendering -= OnFrame;
                animation = null;
            }
        }
    }
}
