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
    public PlaybackStatus Status => status;
    public long RunId => Interlocked.Read(ref runId);
    public Exception? LastError { get; private set; }
    private PlaybackMode activeMode;

    public MacroPlaybackController(MacroDocument document, IMacroPlaybackExecutor executor, PlaybackExecutionOptions? executionOptions = null)
    {
        this.document = document;
        this.executor = executor;
        this.executionOptions = executionOptions ?? new(document.Playback.Mode, document.Playback.Count,
            PixelEvaluationMode.Live, false, document.Playback.Precision, document.Playback.AffinityMask);
    }

    public void UpdateDefinition(MacroDocument next, IMacroPlaybackExecutor nextExecutor, PlaybackExecutionOptions options)
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
            if (disposed || status == PlaybackStatus.Stopping) return Task.CompletedTask;
            if (status == PlaybackStatus.Running) StopCore();
            else StartRunCore(callbackQpc);
            return Task.CompletedTask;
        }
    }
    public void TriggerReleased()
    {
        lock (gate) if (activeMode == PlaybackMode.HoldLoop && status == PlaybackStatus.Running) StopCore();
    }
    public Task RunNowAsync()
    {
        lock (gate)
        {
            if (!disposed && status is not (PlaybackStatus.Running or PlaybackStatus.Stopping)) StartRunCore();
            return Task.CompletedTask;
        }
    }
    public void Stop() { lock (gate) StopCore(); }
    public Task StopAsync()
    {
        lock (gate) { StopCore(); return activeRun ?? Task.CompletedTask; }
    }
    public Task<PlaybackRunResult> WhenIdleAsync()
    {
        lock (gate) return activeRun ?? Task.FromResult(new PlaybackRunResult(PlaybackRunStatus.Completed, 0, 0, false, null));
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
        AppLog.Info("Playback", $"Start run={RunId}; macro={currentDocument.Name}; mode={options.Mode}");
        long previousTrace = PlaybackTrace.Run.Value;
        PlaybackTrace.Run.Value = RunId;
        try
        {
            PlaybackTrace.Mark(PlaybackTraceStage.Hotkey, callbackQpc);
            PlaybackTrace.Mark(PlaybackTraceStage.Dispatch);
            _ = RunAndResetAsync(currentExecutor, currentDocument, options, cancellation, completion);
        }
        finally { PlaybackTrace.Run.Value = previousTrace; }
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
