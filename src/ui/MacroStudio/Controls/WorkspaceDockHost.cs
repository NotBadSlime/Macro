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
    private ColumnDefinition? rightColumn;
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
        this.rightColumn = rightColumn;
        this.bottomRow = bottomRow;
        this.leftSplitter = leftSplitter;
        this.rightSplitter = rightSplitter;
        this.bottomSplitter = bottomSplitter;
        this.leftContent = leftContent;
        this.rightContent = rightContent;
        this.bottomContent = bottomContent;

        SizeChanged += (_, _) => ClampCurrentBottomHeight();
        AttachSplitter(leftSplitter);
        AttachSplitter(rightSplitter);
        AttachSplitter(bottomSplitter);
        bottomSplitter.PreviewMouseLeftButtonDown += OnBottomSplitterPreviewMouseLeftButtonDown;
    }

    public void ApplyDockSizes(WorkspaceDockSizes sizes)
    {
        suppressSizeChanged = true;
        lastDockSizes = sizes;
        try
        {
            if (leftColumn is not null)
                leftColumn.Width = new GridLength(Math.Max(leftColumn.MinWidth, sizes.LeftWidth), GridUnitType.Pixel);
            if (rightColumn is not null)
                rightColumn.Width = new GridLength(Math.Max(rightColumn.MinWidth, sizes.RightWidth), GridUnitType.Pixel);
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
        var left = leftContent?.Visibility == Visibility.Visible && leftColumn?.ActualWidth > 0
            ? leftColumn.ActualWidth
            : lastDockSizes.LeftWidth;
        var right = rightContent?.Visibility == Visibility.Visible && rightColumn?.ActualWidth > 0
            ? rightColumn.ActualWidth
            : lastDockSizes.RightWidth;
        var bottom = bottomContent?.Visibility == Visibility.Visible && bottomRow?.ActualHeight > 0
            ? bottomRow.ActualHeight
            : lastDockSizes.BottomHeight;

        lastDockSizes = new WorkspaceDockSizes(left, right, bottom);
        return lastDockSizes;
    }

    public void SetRegionVisible(WorkspaceDockRegion region, bool visible)
    {
        switch (region)
        {
            case WorkspaceDockRegion.Left:
                SetColumnVisible(leftColumn, leftSplitter, leftContent, visible);
                break;
            case WorkspaceDockRegion.Right:
                SetColumnVisible(rightColumn, rightSplitter, rightContent, visible);
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

    private void SetColumnVisible(
        ColumnDefinition? column,
        FrameworkElement? splitter,
        FrameworkElement? content,
        bool visible)
    {
        if (column is not null)
        {
            var requestedWidth = ReferenceEquals(column, leftColumn) ? lastDockSizes.LeftWidth : lastDockSizes.RightWidth;
            if (!visible && column.ActualWidth > 0)
            {
                lastDockSizes = ReferenceEquals(column, leftColumn)
                    ? lastDockSizes with { LeftWidth = column.ActualWidth }
                    : lastDockSizes with { RightWidth = column.ActualWidth };
            }

            var width = visible ? Math.Max(column.MinWidth, requestedWidth) : 0;
            column.Width = new GridLength(width, GridUnitType.Pixel);
        }

        if (splitter is not null)
            splitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (content is not null)
            content.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

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

        ClampCurrentBottomHeight();
        DockSizeChanged?.Invoke(this, EventArgs.Empty);
    }
}
