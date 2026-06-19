using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed record RuntimeDiagnosticItem(bool Available, string Detail);

public sealed record RuntimeDiagnosticsSnapshot(
    RuntimeDiagnosticItem PixelSampler,
    RuntimeDiagnosticItem InputBackend)
{
    public static RuntimeDiagnosticsSnapshot Collect()
    {
        return new RuntimeDiagnosticsSnapshot(
            PixelSampler: ProbePixelSampler(),
            InputBackend: ProbeInputBackend());
    }

    public static RuntimeDiagnosticsSnapshot FromInputStats(InputSubmissionStats? stats)
    {
        return new RuntimeDiagnosticsSnapshot(
            PixelSampler: new RuntimeDiagnosticItem(true, "visible desktop sampler ready"),
            InputBackend: CreateInputBackendItem(stats));
    }

    private static RuntimeDiagnosticItem ProbePixelSampler()
    {
        try
        {
            return ScreenPixelSampler.TrySample(new PixelCoordinate(CoordinateScope.Screen, 0, 0), out var sample)
                ? new RuntimeDiagnosticItem(true, $"visible desktop sampler ready, rgb={sample.Color.R},{sample.Color.G},{sample.Color.B}")
                : new RuntimeDiagnosticItem(false, "visible desktop sampler unavailable");
        }
        catch (Exception ex)
        {
            return new RuntimeDiagnosticItem(false, $"visible desktop sampler error: {ex.Message}");
        }
    }

    private static RuntimeDiagnosticItem ProbeInputBackend()
    {
        return OperatingSystem.IsWindows()
            ? new RuntimeDiagnosticItem(true, "SendInput ready")
            : new RuntimeDiagnosticItem(false, "SendInput is only available on Windows");
    }

    private static RuntimeDiagnosticItem CreateInputBackendItem(InputSubmissionStats? stats)
    {
        return stats is null
            ? ProbeInputBackend()
            : new RuntimeDiagnosticItem(
                true,
                $"SendInput backend={stats.BackendName} actions={stats.ActionsSubmitted} nativeInputs={stats.NativeInputsSubmitted} failures={stats.FailedSubmissions} lastError={stats.LastWin32Error} submit={stats.LastSubmitDurationMicroseconds}us nativePlanCreate={stats.NativePlanCreateMicroseconds}us nativeStartup={stats.NativeStartupMicroseconds}us nativeRunOverhead={stats.NativeRunOverheadMicroseconds}us nativeSubmit={stats.NativeSubmitDurationMicroseconds}us jitter={stats.Timing?.Summary ?? "n/a"} selectedCpu={stats.SelectedCpuSet} cpuMigrationCount={stats.CpuMigrationCount} cpuSetApplied={stats.CpuSetAppliedCount} maxLate={stats.MaxLateMicroseconds}us p99.9Late={stats.P999LateMicroseconds}us outliers={stats.OutliersOverThreshold} maxLoopEndLate={stats.MaxLoopEndLateMicroseconds}us p99.9LoopEndLate={stats.P999LoopEndLateMicroseconds}us loopEndOutliers={stats.LoopEndOutliersOverThreshold} waitPath={stats.WaitPathBreakdown} standbyUsed={stats.StandbyUsed} workerWins={stats.PrimaryWorkerWins}/{stats.HelperWorkerWins} engineWake={stats.EngineWakeCostMicroseconds}us maxLateBatch={stats.MaxLateBatchIndex} maxLateCpu={stats.MaxLateCpu} maxLateWorker={stats.MaxLateWorker} priorityState={stats.PriorityState} ProcessPriorityApplied={stats.ProcessPriorityApplied} nativeFallback={stats.NativeFallbackReason}");
    }
}
