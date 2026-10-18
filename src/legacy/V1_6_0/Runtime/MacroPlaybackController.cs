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

public sealed class MacroPlaybackController : IDisposable
{
    private readonly object gate = new();
    private readonly MacroDocument document;
    private readonly IMacroPlaybackExecutor executor;
    private readonly PlaybackExecutionOptions executionOptions;
    private CancellationTokenSource? activeCancellation;
    private Task<PlaybackRunResult>? activeRun;

    public MacroPlaybackController(
        MacroDocument document,
        IMacroPlaybackExecutor executor,
        PlaybackExecutionOptions? executionOptions = null)
    {
        this.document = document;
        this.executor = executor;
        this.executionOptions = executionOptions ?? new PlaybackExecutionOptions(
            document.Playback.Mode,
            document.Playback.Count,
            PixelEvaluationMode.Live,
            NoWait: false,
            document.Playback.Precision,
            document.Playback.AffinityMask);
    }

    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Idle;

    public Exception? LastError { get; private set; }

    public Task TriggerPressedAsync()
    {
        lock (gate)
        {
            if (Status is PlaybackStatus.Running or PlaybackStatus.Stopping)
            {
                StopCore();
                return Task.CompletedTask;
            }

            StartRunCore();
            return Task.CompletedTask;
        }
    }

    public void TriggerReleased()
    {
        lock (gate)
        {
            if (document.Playback.Mode == PlaybackMode.HoldLoop && Status == PlaybackStatus.Running)
            {
                StopCore();
            }
        }
    }

    public Task RunNowAsync()
    {
        lock (gate)
        {
            if (Status is PlaybackStatus.Running or PlaybackStatus.Stopping)
            {
                return Task.CompletedTask;
            }

            StartRunCore();
            return Task.CompletedTask;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            StopCore();
        }
    }

    public Task<PlaybackRunResult> WhenIdleAsync()
    {
        lock (gate)
        {
            return activeRun ?? Task.FromResult(new PlaybackRunResult(PlaybackRunStatus.Completed, 0, 0, Cancelled: false, InputStats: null));
        }
    }

    private void StartRunCore()
    {
        activeCancellation = new CancellationTokenSource();
        LastError = null;
        Status = PlaybackStatus.Running;

        activeRun = RunAndResetAsync(executionOptions, activeCancellation);
    }

    private async Task<PlaybackRunResult> RunAndResetAsync(
        PlaybackExecutionOptions options,
        CancellationTokenSource cancellation)
    {
        try
        {
            var result = await executor.RunAsync(document, options, cancellation.Token).ConfigureAwait(false);
            lock (gate)
            {
                Status = result.Status == PlaybackRunStatus.InputUnavailable
                    ? PlaybackStatus.InputUnavailable
                    : PlaybackStatus.Idle;
                activeCancellation = null;
            }

            return result;
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                LastError = ex;
                Status = PlaybackStatus.Error;
                activeCancellation = null;
            }

            // Playback runs are launched from global hook callbacks and are intentionally
            // observed in the background. Contain injection/native failures here so a
            // foreground-window change cannot turn one failed run into a faulted UI task.
            return new PlaybackRunResult(
                PlaybackRunStatus.Failed,
                IterationsCompleted: 0,
                ActionsSubmitted: 0,
                Cancelled: cancellation.IsCancellationRequested,
                InputStats: null);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void StopCore()
    {
        if (activeCancellation is null)
        {
            return;
        }

        Status = PlaybackStatus.Stopping;
        activeCancellation.Cancel();
    }

    public void Dispose()
    {
        Stop();
        if (executor is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
