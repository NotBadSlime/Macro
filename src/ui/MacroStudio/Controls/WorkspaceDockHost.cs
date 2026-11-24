using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public sealed class WorkspaceDockHost : Grid
{
    public const double MinBottomHeight = 150;
    public const double MaxBottomHeightRatio = 0.55;

    private ColumnDefinition? leftColumn;
    private ColumnDefinition? centerColumn;
    private ColumnDefinition? rightColumn;
    private double leftMinimum;
    private double rightMinimum;
    private double viewportWidth = double.PositiveInfinity;
    private RowDefinition? bottomRow;
    private FrameworkElement? leftSplitter;
    private FrameworkElement? rightSplitter;
    private FrameworkElement? bottomSplitter;
    private FrameworkElement? leftContent;
    private FrameworkElement? rightContent;
    private FrameworkElement? bottomContent;
    private bool suppressSizeChanged;
    private WorkspaceDockSizes lastDockSizes = new();

    public event EventHandler? DockSizeChanged;
    public event EventHandler? BottomResizeRequested;

    public void AttachRegions(
        ColumnDefinition leftColumn,
        ColumnDefinition centerColumn,
        ColumnDefinition rightColumn,
        RowDefinition bottomRow,
        FrameworkElement leftSplitter,
        FrameworkElement rightSplitter,
        FrameworkElement bottomSplitter,
        FrameworkElement leftContent,
        FrameworkElement rightContent,
        FrameworkElement bottomContent)
    {
        this.leftColumn = leftColumn;
        this.centerColumn = centerColumn;
        this.rightColumn = rightColumn;
        leftMinimum = leftColumn.MinWidth;
        rightMinimum = rightColumn.MinWidth;
        this.bottomRow = bottomRow;
        this.leftSplitter = leftSplitter;
        this.rightSplitter = rightSplitter;
        this.bottomSplitter = bottomSplitter;
        this.leftContent = leftContent;
        this.rightContent = rightContent;
        this.bottomContent = bottomContent;

        SizeChanged += (_, e) =>
        {
            if (e.HeightChanged) ClampCurrentBottomHeight();
        };
        AttachSplitter(leftSplitter);
        AttachSplitter(rightSplitter);
        AttachSplitter(bottomSplitter);
        bottomSplitter.PreviewMouseLeftButtonDown += OnBottomSplitterPreviewMouseLeftButtonDown;
    }

    public void FitWithin(double width)
    {
        viewportWidth = width;
        MaxWidth = width;
        FitSideColumns();
    }

    public void ApplyDockSizes(WorkspaceDockSizes sizes)
    {
        suppressSizeChanged = true;
        lastDockSizes = sizes;
        try
        {
            FitSideColumns();
            if (bottomRow is not null)
                bottomRow.Height = new GridLength(ClampBottomHeight(sizes.BottomHeight), GridUnitType.Pixel);
        }
        finally
        {
            suppressSizeChanged = false;
        }
    }

    public WorkspaceDockSizes CaptureDockSizes()
    {
        var bottom = bottomContent?.Visibility == Visibility.Visible && bottomRow?.ActualHeight > 0
            ? bottomRow.ActualHeight
            : lastDockSizes.BottomHeight;

        lastDockSizes = lastDockSizes with { BottomHeight = bottom };
        return lastDockSizes;
    }

    public void SetRegionVisible(WorkspaceDockRegion region, bool visible)
    {
        switch (region)
        {
            case WorkspaceDockRegion.Left:
                SetColumnVisible(leftSplitter, leftContent, visible);
                FitSideColumns();
                break;
            case WorkspaceDockRegion.Right:
                SetColumnVisible(rightSplitter, rightContent, visible);
                FitSideColumns();
                break;
            case WorkspaceDockRegion.Bottom:
                SetBottomVisible(visible);
                break;
        }
    }

    public double ClampBottomHeight(double requestedHeight)
    {
        var available = ActualHeight > 0 ? ActualHeight : 720;
        var max = Math.Max(MinBottomHeight, available * MaxBottomHeightRatio);
        return Math.Clamp(requestedHeight, MinBottomHeight, max);
    }

    private static void SetColumnVisible(
        FrameworkElement? splitter,
        FrameworkElement? content,
        bool visible)
    {
        if (splitter is not null)
            splitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (content is not null)
            content.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FitSideColumns()
    {
        if (leftColumn is null || centerColumn is null || rightColumn is null) return;

        var leftVisible = leftContent?.Visibility == Visibility.Visible;
        var rightVisible = rightContent?.Visibility == Visibility.Visible;
        var room = viewportWidth - centerColumn.MinWidth - SplitterWidth(leftSplitter) - SplitterWidth(rightSplitter);
        var minimumLeft = leftVisible ? leftMinimum : 0;
        var minimumRight = rightVisible ? rightMinimum : 0;
        var desiredLeft = leftVisible ? Math.Max(minimumLeft, lastDockSizes.LeftWidth) : 0;
        var desiredRight = rightVisible ? Math.Max(minimumRight, lastDockSizes.RightWidth) : 0;
        var flexible = desiredLeft + desiredRight - minimumLeft - minimumRight;
        var scale = flexible > 0 ? Math.Clamp((room - minimumLeft - minimumRight) / flexible, 0, 1) : 0;
        leftColumn.MinWidth = minimumLeft;
        rightColumn.MinWidth = minimumRight;
        leftColumn.Width = new GridLength(minimumLeft + (desiredLeft - minimumLeft) * scale);
        rightColumn.Width = new GridLength(minimumRight + (desiredRight - minimumRight) * scale);
    }

    private static double SplitterWidth(FrameworkElement? splitter) =>
        splitter?.Visibility != Visibility.Visible ? 0 : splitter.Width;

    private void SetBottomVisible(bool visible)
    {
        if (bottomRow is not null)
        {
            if (!visible && bottomRow.ActualHeight > 0)
            {
                lastDockSizes = lastDockSizes with { BottomHeight = bottomRow.ActualHeight };
            }

            bottomRow.MinHeight = visible ? MinBottomHeight : 0;
            var height = visible ? ClampBottomHeight(lastDockSizes.BottomHeight) : 0;
            bottomRow.Height = new GridLength(height, GridUnitType.Pixel);
        }

        if (bottomSplitter is not null)
            bottomSplitter.Visibility = Visibility.Visible;
        if (bottomContent is not null)
            bottomContent.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBottomSplitterPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (bottomContent?.Visibility == Visibility.Visible)
            return;

        OpenBottomRegionForResize();
    }

    private void OpenBottomRegionForResize()
    {
        BottomResizeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ClampCurrentBottomHeight()
    {
        if (bottomRow is null || bottomContent?.Visibility != Visibility.Visible)
            return;

        var clamped = ClampBottomHeight(bottomRow.ActualHeight > 0 ? bottomRow.ActualHeight : bottomRow.Height.Value);
        if (Math.Abs(bottomRow.Height.Value - clamped) > 0.5)
            bottomRow.Height = new GridLength(clamped, GridUnitType.Pixel);
    }

    private void AttachSplitter(FrameworkElement splitter)
    {
        if (splitter is SmoothGridSplitter smoothGridSplitter)
        {
            smoothGridSplitter.WidthReset += () => RememberSideWidth(smoothGridSplitter);
        }

        if (splitter is GridSplitter gridSplitter)
        {
            gridSplitter.DragCompleted += OnDockSplitterDragCompleted;
        }
        else if (splitter is Thumb thumb)
        {
            thumb.DragCompleted += OnDockSplitterDragCompleted;
        }
    }

    private void OnDockSplitterDragCompleted(object? sender, DragCompletedEventArgs e)
    {
        if (suppressSizeChanged)
            return;

        if (ReferenceEquals(sender, leftSplitter) || ReferenceEquals(sender, rightSplitter))
        {
            RememberSideWidth(sender);
            return;
        }

        ClampCurrentBottomHeight();
        DockSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RememberSideWidth(object? splitter)
    {
        lastDockSizes = ReferenceEquals(splitter, leftSplitter)
            ? lastDockSizes with { LeftWidth = leftColumn!.Width.Value }
            : lastDockSizes with { RightWidth = rightColumn!.Width.Value };
        FitSideColumns();
        DockSizeChanged?.Invoke(this, EventArgs.Empty);
    }
}
