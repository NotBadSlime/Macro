using MacroHid.Core;
using MacroHid.LegacyContract;
using MacroHid.Runtime;

namespace MacroStudio.Services;

/// <summary>
/// Everything the UI does to "the engine" goes through here, so a selected earlier release gets exactly the calls its own
/// App/MainWindow made (warm-up, mask re-warm, shutdown, prepare) and the current engine keeps the current ones.
/// Core testing (core picker, latency check) stays the current app's: those dialogs measure with the current DLL and the
/// result, a core mask, is then handed to the selected release the way that release used it.
/// </summary>
internal static class PlaybackEngineHost
{
    private static PlaybackVersionInfo active = PlaybackVersions.All[0];
    private static ILegacyRuntime? activeLegacy;

    public static PlaybackVersionInfo Active => active;

    /// <summary>The release's start-up (App.OnStartup) warm-up. Called once at start-up and whenever the version changes.</summary>
    public static void Activate(RuntimePrecisionSettings settings)
    {
        var version = settings.Version;
        if (!ReferenceEquals(version, active) && activeLegacy is not null)
        {
            // Switching away: what that release did on exit.
            try { activeLegacy.Shutdown(); } catch (Exception ex) { AppLog.Error("Playback", "Legacy shutdown failed", ex); }
            activeLegacy = null;
        }
        active = version;
        if (version.Folder is null)
        {
            NativePlaybackWarmup.QueueWarmUpForPrecision(settings.Precision, settings.AffinityMask, settings.Generation);
            PlaybackPathWarmup.QueueRun(settings.Precision, settings.AffinityMask, settings.Generation);
            return;
        }
        try
        {
            activeLegacy = LegacyPlaybackRuntimes.Get(version);
            activeLegacy.Activate(settings.Precision.ToString(), settings.AffinityMask);
            AppLog.Info("Playback", $"Engine MacroHID {version.Label}");
        }
        catch (Exception ex)
        {
            activeLegacy = null;
            AppLog.Error("Playback", $"Engine {version.Label} unavailable", ex);
        }
    }

    /// <summary>Precision settings saved (MainWindow.WarmUpRuntimePrecision in every release).</summary>
    public static void PrecisionChanged(RuntimePrecisionSettings settings)
    {
        if (!ReferenceEquals(settings.Version, active)) { Activate(settings); return; }
        if (activeLegacy is { } legacy) legacy.PrecisionChanged(settings.Precision.ToString(), settings.AffinityMask);
        else NativePlaybackWarmup.QueueWarmUpForPrecision(settings.Precision, settings.AffinityMask, settings.Generation);
    }

    /// <summary>A core mask was chosen, restored after a cancelled test, or selected automatically.</summary>
    public static void AffinityMaskApplied(RuntimePrecisionSettings settings, string mask)
    {
        if (activeLegacy is { } legacy) { legacy.AffinityMaskApplied(settings.Precision.ToString(), mask); return; }
        if (string.IsNullOrWhiteSpace(mask)) NativePlaybackWarmup.QueueWarmUpForPrecision(settings.Precision, "", settings.Generation);
        else NativePlaybackWarmup.QueueWarmUpForAffinityMask(mask, force: true);
    }

    /// <summary>The 2.0.1+ warm lease while a precise hotkey macro listens.</summary>
    public static IDisposable? UpdateListeningWarm(bool needed, IDisposable? lease)
    {
        if (activeLegacy is { } legacy)
        {
            lease?.Dispose();
            legacy.SetListeningWarm(needed);
            return null;
        }
        if (needed && lease is null) return PrecisionPlaybackContext.AcquireWarmLease();
        if (!needed && lease is not null) { lease.Dispose(); return null; }
        return lease;
    }

    public static void Shutdown()
    {
        if (activeLegacy is { } legacy)
        {
            try { legacy.Shutdown(); } catch (Exception ex) { AppLog.Error("Playback", "Legacy shutdown failed", ex); }
        }
        NativePlaybackWarmup.Shutdown();
    }

    /// <summary>
    /// "new MacroPlaybackExecutor(inputSink, resolver); executor.Prepare(document, options)" of the selected release.
    /// </summary>
    public static IMacroPlaybackExecutor Prepare(SendInputMacroSink inputSink, MacroDocument document,
        Func<string, MacroDocument?> resolver, PlaybackExecutionOptions options, bool immediate, PlaybackVersionInfo version)
    {
        // Parallel cards were removed: one macro runs at a time. Old macros still open, but do not play until it is deleted.
        if (ParallelCards.FindIn(document, resolver) is { } owner)
            throw new NotSupportedException(LocalizationService.Format("ParallelCardRemoved", owner));
        if (version.Folder is not null)
            return LegacyPreparedExecutor.Prepare(version, document, resolver, options, immediate);
        var executor = new MacroPlaybackExecutor(inputSink, macroResolver: resolver);
        try { executor.Prepare(document, options); return executor; }
        catch { executor.Dispose(); throw; }
    }
}
