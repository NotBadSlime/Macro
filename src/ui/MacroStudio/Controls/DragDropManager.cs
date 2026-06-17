using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MacroStudio.Controls;

public sealed class DragDropManager
{
    private readonly ListBox listBox;
    private readonly string dragFormat;
    private readonly Func<object, object?> getDragData;

    private Point? dragStartPoint;
    private DragGhostAdorner? ghostAdorner;
    private int dragSourceIndex = -1;
    private int currentInsertIndex = -1;
    private ListBoxItem? dragSourceContainer;
    private double sourceItemHeight = 48;
    private DispatcherTimer? autoScrollTimer;

    public event Action<int, int>? ItemMoved;
    public event Action<object, int>? ExternalItemDropped;

    public DragDropManager(ListBox listBox, string dragFormat, Func<object, object?> getDragData)
    {
        this.listBox = listBox;
        this.dragFormat = dragFormat;
        this.getDragData = getDragData;

        listBox.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        listBox.PreviewMouseMove += OnPreviewMouseMove;
        listBox.PreviewMouseLeftButtonUp += OnPreviewMouseUp;
        listBox.AllowDrop = true;
        listBox.DragOver += OnDragOver;
        listBox.DragLeave += OnDragLeave;
        listBox.Drop += OnDrop;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStartPoint = e.GetPosition(listBox);
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        dragStartPoint = null;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragStartPoint is null)
            return;

        var currentPos = e.GetPosition(listBox);
        var diff = currentPos - dragStartPoint.Value;

        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = GetItemAtPoint(dragStartPoint.Value);
        if (item is null)
            return;

        dragSourceIndex = listBox.Items.IndexOf(item);
        if (dragSourceIndex < 0)
            return;

        var container = (ListBoxItem)listBox.ItemContainerGenerator.ContainerFromItem(item);
        if (container is null)
            return;

        dragSourceContainer = container;
        sourceItemHeight = container.ActualHeight;

        var data = getDragData(item);
        if (data is null)
            return;

        // Calculate grab offset within the item
        var offsetVec = dragStartPoint.Value - container.TranslatePoint(new Point(0, 0), listBox);
        var grabOffset = new Point(offsetVec.X, offsetVec.Y);

        // Fade source item
        container.Opacity = 0.3;

        ShowGhost(container, e.GetPosition(listBox), grabOffset);
        StartAutoScroll();

        var dataObject = new DataObject(dragFormat, data);
        DragDrop.DoDragDrop(listBox, dataObject, DragDropEffects.Move);

        // Restore source item
        if (dragSourceContainer != null)
            dragSourceContainer.Opacity = 1.0;

        StopAutoScroll();
        RemoveGhost();
        ClearInsertAnimation();
        dragStartPoint = null;
        dragSourceIndex = -1;
        dragSourceContainer = null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var pos = e.GetPosition(listBox);
        UpdateGhostPosition(pos);
        HandleAutoScroll(pos);

        var insertIndex = GetInsertIndex(pos);
        if (insertIndex != currentInsertIndex)
        {
            ClearInsertAnimation();
            currentInsertIndex = insertIndex;
            AnimateGap(insertIndex);
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        ClearInsertAnimation();
        currentInsertIndex = -1;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var pos = e.GetPosition(listBox);
        var insertIndex = GetInsertIndex(pos);

        if (e.Data.GetDataPresent(dragFormat))
        {
            if (dragSourceIndex >= 0 && insertIndex >= 0 && insertIndex != dragSourceIndex)
            {
                var targetIndex = insertIndex > dragSourceIndex ? insertIndex - 1 : insertIndex;
                ItemMoved?.Invoke(dragSourceIndex, targetIndex);
                AnimateDropBounce(targetIndex);
            }
            else if (dragSourceIndex < 0)
            {
                var data = e.Data.GetData(dragFormat);
                if (data is not null)
                {
                    ExternalItemDropped?.Invoke(data, insertIndex);
                    AnimateDropBounce(insertIndex);
                }
            }
        }

        ClearInsertAnimation();
        currentInsertIndex = -1;
        e.Handled = true;
    }

    private void ShowGhost(UIElement source, Point position, Point grabOffset)
    {
        var layer = AdornerLayer.GetAdornerLayer(listBox);
        if (layer is null)
            return;

        ghostAdorner = new DragGhostAdorner(listBox, source, position, grabOffset);
        layer.Add(ghostAdorner);
    }

    private void UpdateGhostPosition(Point position)
    {
        ghostAdorner?.UpdatePosition(position);
    }

    private void RemoveGhost()
    {
        if (ghostAdorner is null)
            return;

        var layer = AdornerLayer.GetAdornerLayer(listBox);
        ghostAdorner.FadeOut(() =>
        {
            layer?.Remove(ghostAdorner);
            ghostAdorner = null;
        });
    }

    private int GetInsertIndex(Point position)
    {
        for (var i = 0; i < listBox.Items.Count; i++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
                continue;

            var itemPos = container.TranslatePoint(new Point(0, container.ActualHeight / 2), listBox);
            if (position.Y < itemPos.Y)
                return i;
        }

        return listBox.Items.Count;
    }

    private void AnimateGap(int insertIndex)
    {
        var gapHeight = sourceItemHeight + 8;

        for (var i = 0; i < listBox.Items.Count; i++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
                continue;

            var transform = container.RenderTransform as TranslateTransform;
            if (transform is null)
            {
                transform = new TranslateTransform();
                container.RenderTransform = transform;
            }

            double targetY = 0;
            if (i >= insertIndex && i != dragSourceIndex)
                targetY = gapHeight;

            var animation = new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(TranslateTransform.YProperty, animation);
        }
    }

    private void ClearInsertAnimation()
    {
        for (var i = 0; i < listBox.Items.Count; i++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
                continue;

            if (container.RenderTransform is TranslateTransform transform)
            {
                var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                transform.BeginAnimation(TranslateTransform.YProperty, animation);
            }
        }
    }

    private void AnimateDropBounce(int targetIndex)
    {
        listBox.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (targetIndex < 0 || targetIndex >= listBox.Items.Count)
                return;
            if (listBox.ItemContainerGenerator.ContainerFromIndex(targetIndex) is not ListBoxItem container)
                return;

            container.RenderTransformOrigin = new Point(0.5, 0.5);
            var scaleTransform = new ScaleTransform(1, 0);
            container.RenderTransform = scaleTransform;

            var animY = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var animX = new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        });
    }

    // Auto-scroll when dragging near edges
    private double autoScrollSpeed;

    private void StartAutoScroll()
    {
        autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        autoScrollTimer.Tick += AutoScrollTick;
        autoScrollTimer.Start();
    }

    private void StopAutoScroll()
    {
        autoScrollTimer?.Stop();
        autoScrollTimer = null;
        autoScrollSpeed = 0;
    }

    private void HandleAutoScroll(Point pos)
    {
        const double edgeZone = 40;
        var height = listBox.ActualHeight;

        if (pos.Y < edgeZone)
            autoScrollSpeed = -(1.0 - pos.Y / edgeZone) * 6;
        else if (pos.Y > height - edgeZone)
            autoScrollSpeed = ((pos.Y - (height - edgeZone)) / edgeZone) * 6;
        else
            autoScrollSpeed = 0;
    }

    private void AutoScrollTick(object? sender, EventArgs e)
    {
        if (Math.Abs(autoScrollSpeed) < 0.1) return;

        var scrollViewer = FindScrollViewer(listBox);
        if (scrollViewer is null) return;

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + autoScrollSpeed);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            var result = FindScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    private object? GetItemAtPoint(Point point)
    {
        var hitElement = listBox.InputHitTest(point) as DependencyObject;
        while (hitElement is not null)
        {
            if (hitElement is ListBoxItem item)
                return item.Content ?? item.DataContext;
            hitElement = VisualTreeHelper.GetParent(hitElement);
        }
        return null;
    }
}
