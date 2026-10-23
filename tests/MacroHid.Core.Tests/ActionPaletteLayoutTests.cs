using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacroStudio.Controls;
using MacroStudio;

internal static class ActionPaletteLayoutTests
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifyLayout(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Palette layout audit failed", failure);
    }

    private static void VerifyLayout()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MacroStudio;component/Themes/DarkTheme.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MacroStudio;component/Themes/SharedStyles.xaml", UriKind.Relative) });
        LocalizationService.Initialize();
        var palette = new ActionPalettePanel();
        var scroll = new ScrollViewer
        {
            Content = new ContentControl { Content = palette }, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var window = new Window
        {
            Content = scroll, Width = 450, Height = 850, WindowStyle = WindowStyle.None,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            Background = (Brush)app.FindResource("PanelBackground")
        };
        try
        {
            window.Show();
            VerifyDockResize(window, scroll);
            window.Content = scroll;
            foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
            {
                LocalizationService.SetLanguage(language, persist: false);
                palette.ApplyLocalization();
                // Exercise both directions through the one/two-column breakpoint, including
                // the vertical scrollbar's reduction of the available viewport width.
                foreach (var width in new[] { 450, 519, 520, 540, 650, 900, 540, 500, 320 })
                {
                    window.Width = width;
                    window.UpdateLayout();
                    app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                    if (palette.ActualWidth > scroll.ViewportWidth + 1)
                        throw new Exception($"Palette overflows viewport at width {width}: {palette.ActualWidth:F1} > {scroll.ViewportWidth:F1}");
                    foreach (var name in new[] { "BasicCommandGrid", "AdvancedCommandGrid", "LoopCommandGrid", "YsrCommandGrid" })
                    {
                        var grid = (UniformGrid)palette.FindName(name);
                        var contentWidth = ((FrameworkElement)grid.Parent).ActualWidth;
                        Console.WriteLine($"{language} width={width} {name} columns={grid.Columns} actual={grid.ActualWidth:F1} parent={contentWidth:F1}");
                        var bounds = grid.TransformToAncestor(palette).TransformBounds(new Rect(grid.RenderSize));
                        if (bounds.Right > palette.ActualWidth + 1)
                            throw new Exception($"{name} overflows at width {width}: {bounds.Right:F1} > {palette.ActualWidth:F1}");
                        var cards = grid.Children.OfType<Button>().Where(button => button.IsVisible).ToArray();
                        foreach (var card in cards)
                        {
                            var cardBounds = card.TransformToAncestor(grid).TransformBounds(new Rect(card.RenderSize));
                            if (cardBounds.Right > grid.ActualWidth + 1 || cardBounds.Width > grid.ActualWidth / grid.Columns + 1)
                                throw new Exception($"Card {card.Name} overflows its column at width {width}.");
                        }
                    }
                    if (language == "zh-CN" && width == 650)
                    {
                        var output = Path.GetFullPath("artifacts/palette-layout-fixed.png");
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(output);
                        encoder.Save(file);
                    }
                }
            }
            Console.WriteLine("PASS palette resize layout in all three languages");
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static void VerifyDockResize(Window window, ScrollViewer scroll)
    {
        window.Content = null;
        var dock = new Grid();
        dock.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260), MinWidth = 210 });
        dock.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var center = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 480 };
        dock.ColumnDefinitions.Add(center);
        dock.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dock.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390), MinWidth = 320 });
        var splitter = new SmoothGridSplitter { ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter, 3);
        dock.Children.Add(splitter);
        Grid.SetColumn(scroll, 4);
        dock.Children.Add(scroll);
        window.Content = dock;
        window.Width = 1600;
        window.UpdateLayout();
        foreach (var delta in new[] { 120d, -180d, 80d })
        {
            splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            splitter.RaiseEvent(new DragDeltaEventArgs(delta, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            splitter.RaiseEvent(new DragCompletedEventArgs(delta, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            window.UpdateLayout();
            if (!center.Width.IsStar)
                throw new Exception("Dragging the dock splitter converted the flexible center column to pixels.");
            var bounds = scroll.TransformToAncestor(dock).TransformBounds(new Rect(scroll.RenderSize));
            if (bounds.Right > dock.ActualWidth + 1)
                throw new Exception("Right dock extends beyond the window after splitter drag.");
        }
        splitter.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = Control.MouseDoubleClickEvent });
        var resetFrame = new System.Windows.Threading.DispatcherFrame();
        var resetTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        resetTimer.Tick += (_, _) => { resetTimer.Stop(); resetFrame.Continue = false; };
        resetTimer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(resetFrame);
        window.UpdateLayout();
        if (!center.Width.IsStar || Math.Abs(dock.ColumnDefinitions[4].ActualWidth - 384) > 1)
            throw new Exception("Double-click did not reset the right dock while preserving the flexible center.");
        window.Content = null;
        dock.Children.Remove(scroll);
        Grid.SetColumn(scroll, 0);
    }
}
