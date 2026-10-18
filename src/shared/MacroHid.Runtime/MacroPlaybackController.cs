using MacroHid.Core;

namespace MacroHid.Runtime;

public enum PlaybackStatus
{
    Idle,
    Listening,
    Running,
    Stopping,
    InputUnavailable,
    Error
}

public enum PlaybackRunStatus
{
    Completed,
    InputUnavailable,
    Failed
}

public enum PixelEvaluationMode
{
    Skip,
    MatchAll,
    Live
}

public sealed record PlaybackExecutionOptions(
    PlaybackMode Mode,
    int Count,
    PixelEvaluationMode PixelMode,
    bool NoWait,
    PrecisionMode Precision = PrecisionMode.ExtremeDuringPlayback,
    string AffinityMask = "",
    bool BackgroundWindow = false,
    string BackgroundProcess = "")
{
    public InputPressFloor PressFloor { get; init; } = InputPressFloor.Factory;
    /// <summary>
    /// Hotkey runs start this long after the hook saw the key, instead of whenever preparation finishes, so the start
    /// delay is constant. 0 keeps the old behaviour (start as soon as ready). Overruns start immediately.
    /// </summary>
    public int StartLeadMicroseconds { get; init; }
    /// <summary>
    /// Submit each batch this much earlier for the expected SendInput call cost, measured per input kind while running.
    /// </summary>
    public bool CompensateSendInputCost { get; init; }
    public PlaybackExecutionOptions ForImmediateRun()
    {
        if (Mode == PlaybackMode.FixedCount)
        {
            return this;
        }

        return this with { Mode = PlaybackMode.FixedCount, Count = 1 };
    }
}

public sealed record PlaybackRunResult(
    PlaybackRunStatus Status,
    int IterationsCompleted,
    int ActionsSubmitted,
    bool Cancelled,
    InputSubmissionStats? InputStats);

public interface IMacroPlaybackExecutor
{
    Task<PlaybackRunResult> RunAsync(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CancellationToken cancellationToken);
}

public sealed class MacroPlaybackController : IDisposable, IAsyncDisposable
{
    private readonly object gate = new();
    private MacroDocument document;
    private IMacroPlaybackExecutor executor;
    private PlaybackExecutionOptions executionOptions;
    private CancellationTokenSource? activeCancellation;
    private Task<PlaybackRunResult>? activeRun;
    private Task? disposal;
    private bool disposed;
    private static long nextRunId;
    private long runId;
    private volatile PlaybackStatus status;
    public PlaybackStatus Status => legacy is { } route ? ParseLegacyStatus(route.Controller.Status) : status;
    public long RunId => Interlocked.Read(ref runId);
    public PrecisionMode Precision { get { lock (gate) return executionOptions.Precision; } }
    public Exception? LastError { get; private set; }
    private PlaybackMode activeMode;

    public MacroPlaybackController(MacroDocument document, IMacroPlaybackExecutor executor, PlaybackExecutionOptions? executionOptions = null)
    {
        this.document = document;
        this.executor = executor;
        this.executionOptions = executionOptions ?? new(document.Playback.Mode, document.Playback.Count,
            PixelEvaluationMode.Live, false, document.Playback.Precision, document.Playback.AffinityMask);
        if (executor is LegacyPreparedExecutor prepared) legacy = new LegacyRoute(prepared);
    }

    // An earlier release's macro: its own MacroPlaybackController decides start/stop/hold/toggle, as it did in that release.
    private LegacyRoute? legacy;
    private sealed class LegacyRoute(LegacyPreparedExecutor prepared)
    {
        public readonly LegacyPreparedExecutor Prepared = prepared;
        public readonly MacroHid.LegacyContract.ILegacyController Controller = prepared.TakeController();
        public Task<PlaybackRunResult>? Observed;
    }

    private static PlaybackStatus ParseLegacyStatus(string status) =>
        Enum.TryParse<PlaybackStatus>(status, out var parsed) ? parsed : PlaybackStatus.Error;

    // Turns one legacy run into the current result type and a new RunId, so the UI observes it like any other run.
    private void ObserveLegacyRun(LegacyRoute route, PlaybackStatus before)
    {
        if (before is PlaybackStatus.Running or PlaybackStatus.Stopping) return;
        if (ParseLegacyStatus(route.Controller.Status) is not (PlaybackStatus.Running or PlaybackStatus.Stopping)) return;
        Interlocked.Exchange(ref runId, Interlocked.Increment(ref nextRunId));
        PlaybackRuntimeStatus.RunStarted();
        route.Observed = ObserveLegacyAsync(route);
    }

    private async Task<PlaybackRunResult> ObserveLegacyAsync(LegacyRoute route)
    {
        var result = await route.Controller.WhenIdleAsync().ConfigureAwait(false);
        PlaybackRuntimeStatus.RunFinished();
        lock (gate) LastError = route.Controller.LastError;
        var status = Enum.TryParse<PlaybackRunStatus>(result.Status, out var parsed) ? parsed : PlaybackRunStatus.Failed;
        AppLog.Info("Playback", $"End macro={document.Name}; engine={route.Prepared.Version.Label}; status={status}; cancelled={result.Cancelled}; actions={result.ActionsSubmitted}");
        return new PlaybackRunResult(status, result.IterationsCompleted, result.ActionsSubmitted, result.Cancelled, null);
    }

    public void UpdateDefinition(MacroDocument next, IMacroPlaybackExecutor nextExecutor, PlaybackExecutionOptions options)
    {
        LegacyRoute? retiredLegacy;
        IMacroPlaybackExecutor? retiredCurrent = null;
        lock (gate)
        {
            if (legacy is null && nextExecutor is not LegacyPreparedExecutor)
            {
                retiredLegacy = null;
            }
            else
            {
                if (disposed) { (nextExecutor as IDisposable)?.Dispose(); return; }
                // Engine or definition changed: the earlier release's controller finishes its run, then is released.
                retiredLegacy = legacy;
                legacy = nextExecutor is LegacyPreparedExecutor prepared ? new LegacyRoute(prepared) : null;
                if (legacy is not null)
                {
                    if (executor is not LegacyPreparedExecutor && !ReferenceEquals(executor, runningExecutor)) retiredCurrent = executor;
                    document = next;
                    executor = nextExecutor;
                    executionOptions = options;
                }
            }
        }
        if (retiredLegacy is not null) _ = RetireLegacyAsync(retiredLegacy);
        (retiredCurrent as IDisposable)?.Dispose();
        lock (gate) if (legacy is not null) return;
        UpdateCurrentDefinition(next, nextExecutor, options);
    }

    private static async Task RetireLegacyAsync(LegacyRoute route)
    {
        route.Controller.Stop();
        try { await route.Controller.WhenIdleAsync().ConfigureAwait(false); } catch { }
        route.Controller.Dispose();
        route.Prepared.Dispose();
    }

    private void UpdateCurrentDefinition(MacroDocument next, IMacroPlaybackExecutor nextExecutor, PlaybackExecutionOptions options)
    {
        IMacroPlaybackExecutor? release;
        lock (gate)
        {
            if (disposed) { (nextExecutor as IDisposable)?.Dispose(); return; }
            release = executor;
            document = next;
            executor = nextExecutor;
            executionOptions = options;
            // The running invocation owns its executor until its completion.
            if (ReferenceEquals(release, runningExecutor) || ReferenceEquals(release, nextExecutor)) release = null;
        }
        (release as IDisposable)?.Dispose();
    }

    private IMacroPlaybackExecutor? runningExecutor;
    public Task TriggerPressedAsync(long callbackQpc = 0)
    {
        lock (gate)
        {
            if (legacy is { } route)
            {
                if (disposed) return Task.CompletedTask;
                var before = ParseLegacyStatus(route.Controller.Status);
                var pressed = route.Controller.TriggerPressedAsync();
                ObserveLegacyRun(route, before);
                return pressed;
            }
            if (disposed || status == PlaybackStatus.Stopping) return Task.CompletedTask;
            if (status == PlaybackStatus.Running) StopCore();
            else StartRunCore(callbackQpc);
            return Task.CompletedTask;
        }
    }
    public void TriggerReleased()
    {
        lock (gate)
        {
            if (legacy is { } route) { route.Controller.TriggerReleased(); return; }
            if (activeMode == PlaybackMode.HoldLoop && status == PlaybackStatus.Running) StopCore();
        }
    }
    public Task RunNowAsync()
    {
        lock (gate)
        {
            if (legacy is { } route)
            {
                if (disposed) return Task.CompletedTask;
                var before = ParseLegacyStatus(route.Controller.Status);
                var started = route.Controller.RunNowAsync();
                ObserveLegacyRun(route, before);
                return started;
            }
            if (!disposed && status is not (PlaybackStatus.Running or PlaybackStatus.Stopping)) StartRunCore();
            return Task.CompletedTask;
        }
    }
    public void Stop() { lock (gate) { if (legacy is { } route) route.Controller.Stop(); else StopCore(); } }
    public Task StopAsync()
    {
        lock (gate)
        {
            if (legacy is { } route) { route.Controller.Stop(); return route.Observed ?? Task.CompletedTask; }
            StopCore(); return activeRun ?? Task.CompletedTask;
        }
    }
    public Task<PlaybackRunResult> WhenIdleAsync()
    {
        lock (gate)
        {
            if (legacy is { } route)
                return route.Observed is { IsCompleted: false } observed
                    ? observed
                    : Task.FromResult(route.Observed is { } done ? done.Result : new PlaybackRunResult(PlaybackRunStatus.Completed, 0, 0, false, null));
            return activeRun ?? Task.FromResult(new PlaybackRunResult(PlaybackRunStatus.Completed, 0, 0, false, null));
        }
    }
    private void StartRunCore(long callbackQpc = 0)
    {
        var cancellation = activeCancellation = new CancellationTokenSource();
        var currentExecutor = runningExecutor = executor;
        var currentDocument = document;
        var options = executionOptions;
        activeMode = options.Mode;
        LastError = null;
        status = PlaybackStatus.Running;
        PlaybackRuntimeStatus.RunStarted();
        Interlocked.Exchange(ref runId, Interlocked.Increment(ref nextRunId));
        var completion = new TaskCompletionSource<PlaybackRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        activeRun = completion.Task;
        long previousTrace = PlaybackTrace.Run.Value;
        long previousTrigger = PlaybackTrigger.CallbackQpc.Value;
        PlaybackTrace.Run.Value = RunId;
        PlaybackTrigger.CallbackQpc.Value = callbackQpc;
        try
        {
            PlaybackTrace.Mark(PlaybackTraceStage.Hotkey, callbackQpc);
            PlaybackTrace.Mark(PlaybackTraceStage.Dispatch);
            _ = RunAndResetAsync(currentExecutor, currentDocument, options, cancellation, completion);
            // After the hand-off: the worker is already starting, so logging no longer sits on the trigger path.
            AppLog.Info("Playback", $"Start run={RunId}; macro={currentDocument.Name}; mode={options.Mode}");
        }
        finally { PlaybackTrace.Run.Value = previousTrace; PlaybackTrigger.CallbackQpc.Value = previousTrigger; }
    }
    private async Task RunAndResetAsync(IMacroPlaybackExecutor currentExecutor, MacroDocument currentDocument,
        PlaybackExecutionOptions options, CancellationTokenSource cancellation, TaskCompletionSource<PlaybackRunResult> completion)
    {
        PlaybackRunResult result;
        Exception? failure = null;
        try { result = await currentExecutor.RunAsync(currentDocument, options, cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { result = new(PlaybackRunStatus.Completed, 0, 0, true, null); }
        catch (Exception ex)
        {
            failure = ex;
            result = new(PlaybackRunStatus.Failed, 0, 0, cancellation.IsCancellationRequested, null);
            AppLog.Error("Playback", $"Failed macro={currentDocument.Name}", ex);
        }
        lock (gate)
        {
            try
            {
                if (!ReferenceEquals(currentExecutor, executor)) (currentExecutor as IDisposable)?.Dispose();
            }
            catch (Exception ex) { failure ??= ex; result = result with { Status = PlaybackRunStatus.Failed }; }
            runningExecutor = null;
            activeCancellation = null;
            cancellation.Dispose();
            LastError = failure;
            status = result.Status switch {
                PlaybackRunStatus.Failed => PlaybackStatus.Error,
                PlaybackRunStatus.InputUnavailable => PlaybackStatus.InputUnavailable,
                _ => PlaybackStatus.Idle
            };
            // Publish completion before allowing another start to acquire the gate.
            PlaybackTrace.Mark(PlaybackTraceStage.CleanupComplete);
            PlaybackRuntimeStatus.RunFinished();
            completion.TrySetResult(result);
        }
        AppLog.Info("Playback", $"End macro={currentDocument.Name}; status={result.Status}; cancelled={result.Cancelled}; actions={result.ActionsSubmitted}");
    }
    private void StopCore()
    {
        if (activeCancellation is null || status == PlaybackStatus.Stopping) return;
        status = PlaybackStatus.Stopping;
        PlaybackTrace.Mark(PlaybackTraceStage.CancelRequested, runId: RunId);
        activeCancellation.Cancel();
        AppLog.Info("Playback", $"Stop requested run={RunId}");
    }
    public void Dispose() { _ = DisposeAsync(); }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            disposed = true;
            if (legacy is { } route)
            {
                legacy = null;
                disposal = RetireLegacyAsync(route);
                return new(disposal);
            }
            StopCore();
            disposal = ReleaseAsync(activeRun, executor);
            return new(disposal);
        }
    }
    private static async Task ReleaseAsync(Task<PlaybackRunResult>? run, IMacroPlaybackExecutor owned)
    {
        if (run is not null) await run.ConfigureAwait(false);
        (owned as IDisposable)?.Dispose();
    }
}
