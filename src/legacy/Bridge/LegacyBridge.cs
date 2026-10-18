// Compiled into every earlier-release snapshot (src/legacy/<V>), next to that release's unchanged Core and Runtime sources.
// It only does what that release's own App/MainWindow did around playback; everything else is the release's code.
// Feature symbols (set per snapshot in its .csproj):
//   LEGACY_BACKGROUND_OPTIONS  1.6.0+  PlaybackExecutionOptions(..., BackgroundWindow, BackgroundProcess) { PressFloor }, ForImmediateRun
//   LEGACY_LAST_ERROR          1.6.0+  MacroPlaybackController.LastError
//   LEGACY_MASK_WARMUP         1.4.1+  the UI re-warmed with QueueWarmUpForAffinityMask(mask, force: true) after a core mask change
//   LEGACY_MASK_CLEAR_SHUTDOWN 1.6.0   clearing the mask shut the native engine down (2.0.x warmed for the precision instead)
//   LEGACY_PATH_WARMUP         2.0.1+  PlaybackPathWarmup.QueueRun() at start-up, warm lease while a precise hotkey listens
//   LEGACY_EXTERNAL_HOOK       <= 1.6.0  the snapshot carries the external-card hook (LegacyExternalStep)
using System.Reflection;
using System.Runtime.InteropServices;
using MacroHid.Core;
using MacroHid.LegacyContract;
using MacroHid.Runtime;

namespace MacroHid.LegacyBridge;

public sealed class LegacyBridge : ILegacyRuntime
{
    private readonly SendInputMacroSink inputSink = new();
    private ILegacyEffectHost? effectHost;
#if LEGACY_PATH_WARMUP
    private IDisposable? warmLease;
#endif

    static LegacyBridge()
    {
        // The release's NativePlaybackInterop imports "MacroHid.NativePlayback"; this snapshot ships its own build of that
        // release's DLL under a suffixed name, next to this assembly.
        NativeLibrary.SetDllImportResolver(typeof(LegacyBridge).Assembly, (name, assembly, _) =>
        {
            if (!name.StartsWith("MacroHid.NativePlayback", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            var directory = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
            var path = Path.Combine(directory, $"MacroHid.NativePlayback.{LegacySnapshot.Suffix}.dll");
            return NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
        });
    }

    public string Version => LegacySnapshot.Version;

    public IReadOnlyCollection<string> StepTypes => LegacySnapshot.StepTypes;

    public IReadOnlyCollection<string> ExternalStepTypes =>
#if LEGACY_EXTERNAL_HOOK
        LegacyExternalCards.EffectTypes.Where(type => !LegacySnapshot.StepTypes.Contains(type)).ToArray();
#else
        [];
#endif

    public void SetEffectHost(ILegacyEffectHost host)
    {
        effectHost = host;
#if LEGACY_EXTERNAL_HOOK
        LegacyExternalStep.Executor = host.Execute;
#endif
    }

    public void Activate(string precision, string affinityMask)
    {
        NativePlaybackWarmup.QueueWarmUpForPrecision(ParsePrecision(precision), affinityMask);
#if LEGACY_PATH_WARMUP
        PlaybackPathWarmup.QueueRun();
#endif
    }

    public void PrecisionChanged(string precision, string affinityMask) =>
        NativePlaybackWarmup.QueueWarmUpForPrecision(ParsePrecision(precision), affinityMask);

    public void AffinityMaskApplied(string precision, string affinityMask)
    {
#if LEGACY_MASK_WARMUP
        if (string.IsNullOrWhiteSpace(affinityMask))
        {
#if LEGACY_MASK_CLEAR_SHUTDOWN
            NativePlaybackWarmup.Shutdown();
#else
            NativePlaybackWarmup.QueueWarmUpForPrecision(ParsePrecision(precision), "");
#endif
            return;
        }
        NativePlaybackWarmup.QueueWarmUpForAffinityMask(affinityMask, force: true);
#else
        NativePlaybackWarmup.QueueWarmUpForPrecision(ParsePrecision(precision), affinityMask);
#endif
    }

    public void SetListeningWarm(bool needed)
    {
#if LEGACY_PATH_WARMUP
        if (needed && warmLease is null) warmLease = PrecisionPlaybackContext.AcquireWarmLease();
        else if (!needed && warmLease is not null) { warmLease.Dispose(); warmLease = null; }
#endif
    }

    public void Shutdown()
    {
        SetListeningWarm(false);
        NativePlaybackWarmup.Shutdown();
    }

    public ILegacyPreparedMacro Prepare(string macroJson, Func<string, string?> resolveMacroJson, LegacyPlaybackOptions options)
    {
        var document = McrxParser.Parse(macroJson);
        var parsed = new Dictionary<string, MacroDocument?>(StringComparer.OrdinalIgnoreCase);
        MacroDocument? Resolve(string name)
        {
            lock (parsed)
            {
                if (parsed.TryGetValue(name, out var known)) return known;
                var text = resolveMacroJson(name);
                return parsed[name] = text is null ? null : McrxParser.Parse(text);
            }
        }

        var playbackOptions = CreateOptions(document, options);
        var executor = new MacroPlaybackExecutor(inputSink, macroResolver: Resolve);
        try { executor.Prepare(document, playbackOptions); }
        catch { executor.Dispose(); throw; }
        return new Prepared(document, executor, playbackOptions, effectHost);
    }

    // The release's own MainWindow.CreatePlaybackOptions, with its settings coming from the current UI.
    private static PlaybackExecutionOptions CreateOptions(MacroDocument document, LegacyPlaybackOptions options)
    {
#if LEGACY_BACKGROUND_OPTIONS
        var result = new PlaybackExecutionOptions(
            document.Playback.Mode,
            document.Playback.Count,
            PixelEvaluationMode.Live,
            NoWait: false,
            ParsePrecision(options.Precision),
            options.AffinityMask,
            options.BackgroundWindow,
            options.BackgroundProcess)
        {
            PressFloor = InputPressFloor.FromMicroseconds(options.ZeroGapHoldMicroseconds, options.ModifierChordHoldMicroseconds)
        };
        return options.Immediate ? result.ForImmediateRun() : result;
#else
        // Before 1.6.0 "run now" used the macro's own mode; there was no background window or press floor setting.
        return new PlaybackExecutionOptions(
            document.Playback.Mode,
            document.Playback.Count,
            PixelEvaluationMode.Live,
            NoWait: false,
            ParsePrecision(options.Precision),
            options.AffinityMask);
#endif
    }

    private static PrecisionMode ParsePrecision(string precision) =>
        Enum.TryParse<PrecisionMode>(precision, out var value) ? value : PrecisionMode.ExtremeDuringPlayback;

    private sealed class Prepared(MacroDocument document, MacroPlaybackExecutor executor, PlaybackExecutionOptions options,
        ILegacyEffectHost? effectHost) : ILegacyPreparedMacro
    {
        private MacroPlaybackExecutor? owned = executor;

        public ILegacyController CreateController()
        {
            var taken = Interlocked.Exchange(ref owned, null) ?? throw new InvalidOperationException("prepared macro already used");
            IMacroPlaybackExecutor run = effectHost is null ? taken : new EffectScopedExecutor(taken, effectHost);
            return new Controller(new MacroPlaybackController(document, run, options));
        }

        public void Dispose() => Interlocked.Exchange(ref owned, null)?.Dispose();
    }

    // Opens the current app's effect scope around each run so cards it ran for this release are restored at the end.
    private sealed class EffectScopedExecutor(MacroPlaybackExecutor inner, ILegacyEffectHost host) : IMacroPlaybackExecutor, IDisposable
    {
        public async Task<PlaybackRunResult> RunAsync(MacroDocument document, PlaybackExecutionOptions options, CancellationToken cancellationToken)
        {
            using var scope = host.BeginRun();
            return await inner.RunAsync(document, options, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class Controller(MacroPlaybackController inner) : ILegacyController
    {
        private Exception? thrown;
        public string Status => inner.Status.ToString();
#if LEGACY_LAST_ERROR
        public Exception? LastError => inner.LastError ?? thrown;
#else
        public Exception? LastError => thrown;
#endif
        public Task TriggerPressedAsync() => inner.TriggerPressedAsync();
        public void TriggerReleased() => inner.TriggerReleased();
        public Task RunNowAsync() => inner.RunNowAsync();
        public void Stop() => inner.Stop();

        public async Task<LegacyRunResult> WhenIdleAsync()
        {
            try
            {
                var result = await inner.WhenIdleAsync().ConfigureAwait(false);
                return new LegacyRunResult(result.Status.ToString(), result.IterationsCompleted, result.ActionsSubmitted, result.Cancelled);
            }
            catch (Exception ex)
            {
                // Before 1.6.0 the controller rethrew a failed run from its completion task.
                thrown = ex;
                return new LegacyRunResult("Failed", 0, 0, ex is OperationCanceledException);
            }
        }

        public void Dispose() => inner.Dispose();
    }
}

internal static class LegacySnapshot
{
    public static string Version => ReadMetadata("LegacyVersion");
    public static string Suffix => ReadMetadata("LegacySuffix");

    private static string ReadMetadata(string key) =>
        typeof(LegacySnapshot).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "";

    private static IReadOnlyCollection<string>? stepTypes;

    // The step type names the release's McrxParser switch accepts, read from its source at build time (LegacyStepTypes metadata).
    public static IReadOnlyCollection<string> StepTypes => stepTypes ??=
        new HashSet<string>(ReadMetadata("LegacyStepTypes").Split(';', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
}
