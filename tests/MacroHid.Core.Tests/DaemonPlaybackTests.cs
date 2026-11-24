using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio;
using MacroStudio.Services;

internal static class DaemonPlaybackTests
{
    internal static void Run()
    {
        var baseline = new MacroDocument(1, "ordinary", PlaybackSettings.Default with { Mode = PlaybackMode.ToggleLoop }, []);
        var daemon = baseline with { Playback = baseline.Playback with { RunAsDaemon = true } };
        Assert.True(McrxParser.Parse(McrxSerializer.Serialize(daemon)).Playback.RunAsDaemon);
        Assert.False(McrxParser.Parse(McrxSerializer.Serialize(baseline)).Playback.RunAsDaemon);
        Assert.True(MainWindow.ShouldRunNowContinuously(daemon));
        Assert.False(MainWindow.ShouldRunNowContinuously(baseline));
        var emptyCondition = baseline with { Kind = MacroKind.Condition };
        Assert.False(MainWindow.CanPlayStandalone(emptyCondition));
        var condition = emptyCondition with
        {
            Conditions = [new("pixel", "pixel", -1, -1,
                new PixelMatcher(ScreenRegion.FromSinglePixel(0, 0), new RgbColor(1, 2, 3), 0), [])]
        };
        Assert.True(MainWindow.CanPlayStandalone(condition));
        Assert.True(MainWindow.ShouldRunNowContinuously(condition));

        var registry = new PlaybackRunRegistry();
        using var first = Start(registry, baseline, "regular", direct: true);
        Assert.True(registry.BlocksStart("another-regular", daemon: false, background: false));
        Assert.False(registry.BlocksStart("watcher", daemon: true, background: false));
        Assert.True(registry.BlocksStart("watcher", daemon: true, background: true));
        using var watcher = Start(registry, daemon, "watcher", direct: false);
        using var otherWatcher = Start(registry, daemon, "watcher-2", direct: true);
        Assert.True(registry.BlocksStart("WATCHER", daemon: true, background: false));
        Assert.False(registry.BlocksStart("watcher", daemon: true, background: false, watcher));

        registry.Find("watcher")!.Controller.Stop();
        watcher.WhenIdleAsync().GetAwaiter().GetResult();
        registry.Finish(watcher, watcher.RunId);
        Assert.Equal(PlaybackStatus.Running, first.Status);
        Assert.Equal(PlaybackStatus.Running, otherWatcher.Status);
        first.Stop();
        first.WhenIdleAsync().GetAwaiter().GetResult();
        registry.Finish(first, first.RunId);
        Assert.False(registry.BlocksStart("new-regular", daemon: false, background: false));

        var backgroundRegistry = new PlaybackRunRegistry();
        using var background = Start(backgroundRegistry, daemon, "background", direct: true, background: true);
        Assert.True(backgroundRegistry.BlocksStart("foreground", daemon: true, background: false));
    }

    private static MacroPlaybackController Start(PlaybackRunRegistry registry, MacroDocument document, string id, bool direct, bool background = false)
    {
        var controller = new MacroPlaybackController(document, new PendingExecutor());
        controller.RunNowAsync().GetAwaiter().GetResult();
        registry.Track(id, document.Playback.RunAsDaemon, background, direct, controller, controller.RunId);
        return controller;
    }

    private sealed class PendingExecutor : IMacroPlaybackExecutor
    {
        public async Task<PlaybackRunResult> RunAsync(MacroDocument document, PlaybackExecutionOptions options, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Pending playback should only complete through cancellation.");
        }
    }
}
