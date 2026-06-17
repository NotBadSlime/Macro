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
    PrecisionMode Precision = PrecisionMode.ExtremeDuringPlayback);

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

public sealed class MacroPlaybackController
{
    private readonly object gate = new();
    private readonly MacroDocument document;
    private readonly IMacroPlaybackExecutor executor;
    private CancellationTokenSource? activeCancellation;
    private Task<PlaybackRunResult>? activeRun;

    public MacroPlaybackController(MacroDocument document, IMacroPlaybackExecutor executor)
    {
        this.document = document;
        this.executor = executor;
    }

    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Idle;

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
        var options = new PlaybackExecutionOptions(
            document.Playback.Mode,
            document.Playback.Count,
            PixelEvaluationMode.Live,
            NoWait: false,
            document.Playback.Precision);
        Status = PlaybackStatus.Running;

        activeRun = RunAndResetAsync(options, activeCancellation);
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
        catch
        {
            lock (gate)
            {
                Status = PlaybackStatus.Error;
                activeCancellation = null;
            }

            throw;
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
}
