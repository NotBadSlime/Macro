using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace MacroStudio.Controls;

public sealed class DragGhostAdorner : Adorner
{
    private readonly ImageBrush snapshotBrush;
    private readonly Size ghostSize;
    private readonly Point grabOffset;
    private readonly string? badgeText;
    private Point currentPosition;
    private const double GhostScale = 1.03;
    private const double TargetOpacity = 0.85;

    public DragGhostAdorner(UIElement adornedElement, UIElement ghostSource, Point startPosition, Point grabOffsetInItem, string? badgeText = null)
        : base(adornedElement)
    {
        ghostSize = new Size(ghostSource.RenderSize.Width, ghostSource.RenderSize.Height);
        grabOffset = grabOffsetInItem;
        this.badgeText = badgeText;
        currentPosition = new Point(startPosition.X - grabOffset.X, startPosition.Y - grabOffset.Y);
        IsHitTestVisible = false;

        var dpi = VisualTreeHelper.GetDpi(ghostSource);
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(ghostSize.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(ghostSize.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        rtb.Render(ghostSource);
        rtb.Freeze();

        snapshotBrush = new ImageBrush(rtb)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };

        Opacity = 0;
        var fadeIn = new DoubleAnimation(0, TargetOpacity, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fadeIn);
    }

    public DragGhostAdorner(UIElement adornedElement, UIElement ghostSource, Point startPosition)
        : this(adornedElement, ghostSource, startPosition, new Point(0, 0))
    {
    }

    public void UpdatePosition(Point position)
    {
        currentPosition = new Point(position.X - grabOffset.X, position.Y - grabOffset.Y);
        InvalidateVisual();
    }

    public void FadeOut(Action? completed = null)
    {
        var animation = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }

        BeginAnimation(OpacityProperty, animation);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var scaledWidth = ghostSize.Width * GhostScale;
        var scaledHeight = ghostSize.Height * GhostScale;
        var offsetX = currentPosition.X - (scaledWidth - ghostSize.Width) / 2;
        var offsetY = currentPosition.Y - (scaledHeight - ghostSize.Height) / 2;

        var ghostRect = new Rect(offsetX, offsetY, scaledWidth, scaledHeight);

        // Shadow first (behind ghost)
        var shadowRect = new Rect(offsetX + 3, offsetY + 4, scaledWidth, scaledHeight);
        var shadowBrush = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0));
        dc.DrawRoundedRectangle(shadowBrush, null, shadowRect, 8, 8);

        // Ghost on top
        dc.PushTransform(new ScaleTransform(GhostScale, GhostScale,
            currentPosition.X + ghostSize.Width / 2,
            currentPosition.Y + ghostSize.Height / 2));
        var drawRect = new Rect(currentPosition, ghostSize);
        dc.DrawRoundedRectangle(snapshotBrush, null, drawRect, 6, 6);
        dc.Pop();

        if (!string.IsNullOrWhiteSpace(badgeText))
        {
            var badgeRect = new Rect(offsetX + scaledWidth - 34, offsetY - 8, 32, 24);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(52, 199, 89)), null, badgeRect, 12, 12);
            var text = new FormattedText(
                badgeText,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"),
                12,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(badgeRect.X + (badgeRect.Width - text.Width) / 2, badgeRect.Y + (badgeRect.Height - text.Height) / 2));
        }
    }
}
