using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroHid.Runtime;

internal enum NativePlaybackBackendPreference
{
    Auto,
    Managed,
    Native
}

internal enum NativePlaybackEngineMode
{
    Auto,
    Standby,
    Inline
}

internal sealed record NativePlaybackRunDiagnostics(
    string BackendName,
    string NativeFallbackReason,
    int SelectedCpuSet,
    int CpuMigrationCount,
    int CpuSetAppliedCount,
    long MaxLateMicroseconds,
    long P999LateMicroseconds,
    int OutliersOverThreshold,
    long MaxLoopEndLateMicroseconds,
    long P999LoopEndLateMicroseconds,
    int LoopEndOutliersOverThreshold,
    long NativePlanCreateMicroseconds,
    long NativeStartupMicroseconds,
    long NativeRunOverheadMicroseconds,
    long NativeSubmitDurationMicroseconds,
    string WaitPathBreakdown,
    int LastWin32Error,
    int ActionsSubmitted,
    int NativeInputsSubmitted,
    int FailedSubmissions,
    bool StandbyUsed,
    int PrimaryWorkerWins,
    int HelperWorkerWins,
    long EngineWakeCostMicroseconds,
    int MaxLateBatchIndex,
    int MaxLateCpu,
    int MaxLateWorker,
    int ProcessPriorityApplied,
    int ThreadPriorityApplied,
    int MmcssApplied,
    int ProcessPriorityBoostDisabled,
    int ThreadPriorityBoostDisabled,
    int WorkerPriorityAppliedCount,
    int WorkerMmcssAppliedCount,
    int WorkerPriorityBoostDisabledCount,
    string PriorityState,
    IReadOnlyList<NativeOutlierTraceEvent> OutlierEvents,
    bool Cancelled);

internal sealed record NativeOutlierTraceEvent(
    int BatchIndex,
    int Cpu,
    int Worker,
    bool LoopEnd,
    long DueTick,
    long ActualTick,
    long LateMicroseconds);

internal static class NativePlaybackEngine
{
    private static bool CanUseNativePrecision(PrecisionMode precision)
    {
        return precision is PrecisionMode.ExtremeDuringPlayback or PrecisionMode.UltraLowJitter;
    }

    private static int ToNativePrecisionMode(PrecisionMode precision)
    {
        return precision switch
        {
            PrecisionMode.ExtremeDuringPlayback => 1,
            PrecisionMode.UltraLowJitter => 2,
            _ => 0
        };
    }

    public static NativePlaybackRunDiagnostics CreateFallbackDiagnostics(string fallbackReason)
    {
        return EmptyDiagnostics("managed", fallbackReason);
    }

    public static bool TryRun(
        CompiledPlaybackPlan plan,
        PrecisionMode precision,
        CancellationToken cancellationToken,
        out NativePlaybackRunDiagnostics diagnostics,
        out string fallbackReason,
        int outlierThresholdUs = 250,
        bool enableCpuScan = false,
        NativePlaybackEngineMode engineMode = NativePlaybackEngineMode.Auto)
    {
        return TryRunTimeline(
            plan.ExportNativeTimeline(),
            plan.QpcFrequency,
            precision,
            cancellationToken,
            out diagnostics,
            out fallbackReason,
            outlierThresholdUs,
            enableCpuScan,
            engineMode);
    }

    public static bool TryRunTimeline(
        NativePlaybackTimeline timeline,
        long qpcFrequency,
        PrecisionMode precision,
        CancellationToken cancellationToken,
        out NativePlaybackRunDiagnostics diagnostics,
        out string fallbackReason,
        int outlierThresholdUs = 250,
        bool enableCpuScan = false,
        NativePlaybackEngineMode engineMode = NativePlaybackEngineMode.Auto)
    {
        if (!TryCreatePreparedPlan(timeline, qpcFrequency, out var preparedPlan, out diagnostics, out fallbackReason))
        {
            return false;
        }

        using var plan = preparedPlan!;
        return TryRunPrepared(
            plan,
            precision,
            cancellationToken,
            out diagnostics,
            out fallbackReason,
            outlierThresholdUs,
            enableCpuScan,
            engineMode,
            includePlanCreateInStartup: true);
    }

    public static bool TryCreatePreparedPlan(
        NativePlaybackTimeline timeline,
        long qpcFrequency,
        out NativePlaybackPreparedPlan? preparedPlan,
        out NativePlaybackRunDiagnostics diagnostics,
        out string fallbackReason)
    {
        preparedPlan = null;
        diagnostics = EmptyDiagnostics("native", string.Empty);
        fallbackReason = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            fallbackReason = "native playback is only available on Windows x64.";
            diagnostics = EmptyDiagnostics("managed", fallbackReason);
            return false;
        }

        try
        {
            if (!NativePlaybackPreparedPlan.TryCreate(timeline, qpcFrequency, out preparedPlan, out var planCreateUs, out var createStatus))
            {
                fallbackReason = $"native plan creation failed: {DescribeStatus(createStatus)}";
                diagnostics = EmptyDiagnostics("managed", fallbackReason) with
                {
                    NativePlanCreateMicroseconds = planCreateUs,
                    NativeStartupMicroseconds = planCreateUs
                };
                return false;
            }

            diagnostics = EmptyDiagnostics("native-prepared", string.Empty) with
            {
                NativePlanCreateMicroseconds = preparedPlan!.PlanCreateMicroseconds
            };
            return true;
        }
        catch (DllNotFoundException ex)
        {
            fallbackReason = $"native DLL unavailable: {ex.Message}";
            diagnostics = EmptyDiagnostics("managed", fallbackReason);
            return false;
        }
        catch (BadImageFormatException ex)
        {
            fallbackReason = $"native DLL architecture mismatch: {ex.Message}";
            diagnostics = EmptyDiagnostics("managed", fallbackReason);
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            fallbackReason = $"native ABI mismatch: {ex.Message}";
            diagnostics = EmptyDiagnostics("managed", fallbackReason);
            return false;
        }
    }

    public static bool TryRunPrepared(
        NativePlaybackPreparedPlan preparedPlan,
        PrecisionMode precision,
        CancellationToken cancellationToken,
        out NativePlaybackRunDiagnostics diagnostics,
        out string fallbackReason,
        int outlierThresholdUs = 250,
        bool enableCpuScan = false,
        NativePlaybackEngineMode engineMode = NativePlaybackEngineMode.Auto,
        bool includePlanCreateInStartup = false)
    {
        diagnostics = EmptyDiagnostics("native", string.Empty);
        fallbackReason = string.Empty;

        if (!CanUseNativePrecision(precision))
        {
            fallbackReason = "native playback is not used by Balanced.";
            diagnostics = EmptyDiagnostics("managed", fallbackReason);
            return false;
        }

        var effectiveEngineMode = precision == PrecisionMode.ExtremeDuringPlayback
            ? NativePlaybackEngineMode.Inline
            : engineMode;
        var cancelFlag = 0;
        var timeline = preparedPlan.Timeline;
        var qpcFrequency = preparedPlan.QpcFrequency;
        var planCreateUs = preparedPlan.PlanCreateMicroseconds;
        var outlierEventCapacity = timeline.Batches.Length == 0
            ? 0
            : Math.Min(timeline.Batches.Length, 65_536);
        var nativeOutlierEvents = outlierEventCapacity == 0
            ? Array.Empty<NativeOutlierEvent>()
            : new NativeOutlierEvent[outlierEventCapacity];
        GCHandle outlierEventsHandle = default;

        try
        {
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                cancelFlag = 1;
                try
                {
                    NativePlaybackInterop.MhpCancel(ref cancelFlag);
                }
                catch
                {
                    // Cancellation must remain best effort; the managed fallback path will still observe the token.
                }
            });

            if (nativeOutlierEvents.Length > 0)
            {
                outlierEventsHandle = GCHandle.Alloc(nativeOutlierEvents, GCHandleType.Pinned);
            }

            var options = new MhpRunOptions
            {
                PrecisionMode = ToNativePrecisionMode(precision),
                EnableCpuScan = precision == PrecisionMode.UltraLowJitter && enableCpuScan ? 1 : 0,
                OutlierThresholdUs = Math.Max(0, outlierThresholdUs),
                LoopStepCount = Math.Max(0, timeline.LoopStepCount),
                QpcFrequency = qpcFrequency,
                NativeEngineMode = (int)NativeEngineMode.Auto,
                OutlierEvents = outlierEventsHandle.IsAllocated
                    ? outlierEventsHandle.AddrOfPinnedObject()
                    : IntPtr.Zero,
                OutlierEventCapacity = checked((uint)nativeOutlierEvents.Length)
            };

            var modes = effectiveEngineMode switch
            {
                NativePlaybackEngineMode.Standby => new[] { NativeEngineMode.Standby, NativeEngineMode.Inline },
                NativePlaybackEngineMode.Inline => new[] { NativeEngineMode.Inline },
                _ => new[] { NativeEngineMode.Standby, NativeEngineMode.Inline }
            };
            foreach (var mode in modes)
            {
                options.NativeEngineMode = (int)mode;
                var backendName = mode == NativeEngineMode.Standby ? "native-standby" : "native-inline";
                var runStatus = NativePlaybackInterop.MhpRunPlan(preparedPlan.Handle, ref options, ref cancelFlag, out var stats);
                diagnostics = FromStats(
                    stats,
                    runStatus == MhpStatus.Cancelled ? $"{backendName}-cancelled" : backendName,
                    string.Empty,
                    runStatus == MhpStatus.Cancelled,
                    planCreateUs,
                    nativeOutlierEvents,
                    includePlanCreateInStartup);

                if (runStatus == MhpStatus.Ok || runStatus == MhpStatus.Cancelled)
                {
                    return true;
                }

                var statusText = DescribeStatus(runStatus);
                if (mode == NativeEngineMode.Standby && IsStandbyUnavailable(statusText))
                {
                    diagnostics = FromStats(
                        stats,
                        "native-standby-unavailable",
                        $"standby unavailable: {statusText}",
                        cancelled: false,
                        planCreateUs,
                        nativeOutlierEvents,
                        includePlanCreateInStartup);
                    continue;
                }

                fallbackReason = $"native playback failed: {statusText}";
                diagnostics = FromStats(stats, "managed", fallbackReason, cancelled: false, planCreateUs, nativeOutlierEvents, includePlanCreateInStartup);
                return false;
            }

            fallbackReason = "native playback did not find a runnable engine.";
            diagnostics = diagnostics with { NativeFallbackReason = fallbackReason };
            return false;
        }
        catch (DllNotFoundException ex)
        {
            fallbackReason = $"native DLL unavailable: {ex.Message}";
            diagnostics = EmptyDiagnostics("managed", fallbackReason);
            return false;
        }
        catch (BadImageFormatException ex)
        {
            fallbackReason = $"native DLL architecture mismatch: {ex.Message}";
            diagnostics = EmptyDiagnostics("managed", fallbackReason);
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            fallbackReason = $"native ABI mismatch: {ex.Message}";
            diagnostics = EmptyDiagnostics("managed", fallbackReason);
            return false;
        }
        finally
        {
            if (outlierEventsHandle.IsAllocated)
            {
                outlierEventsHandle.Free();
            }
        }
    }

    private static string DescribeStatus(MhpStatus status)
    {
        var nativeText = NativePlaybackInterop.GetLastErrorText();
        return string.IsNullOrWhiteSpace(nativeText)
            ? status.ToString()
            : $"{status}: {nativeText}";
    }

    private static bool IsStandbyUnavailable(string text)
    {
        return text.Contains("standby unavailable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("EntryPointNotFound", StringComparison.OrdinalIgnoreCase);
    }

    private static NativePlaybackRunDiagnostics EmptyDiagnostics(string backend, string fallbackReason)
    {
        return new NativePlaybackRunDiagnostics(
            backend,
            fallbackReason,
            SelectedCpuSet: -1,
            CpuMigrationCount: 0,
            CpuSetAppliedCount: 0,
            MaxLateMicroseconds: 0,
            P999LateMicroseconds: 0,
            OutliersOverThreshold: 0,
            MaxLoopEndLateMicroseconds: 0,
            P999LoopEndLateMicroseconds: 0,
            LoopEndOutliersOverThreshold: 0,
            NativePlanCreateMicroseconds: 0,
            NativeStartupMicroseconds: 0,
            NativeRunOverheadMicroseconds: 0,
            NativeSubmitDurationMicroseconds: 0,
            WaitPathBreakdown: "spin=0 timer=0 late=0",
            LastWin32Error: 0,
            ActionsSubmitted: 0,
            NativeInputsSubmitted: 0,
            FailedSubmissions: 0,
            StandbyUsed: false,
            PrimaryWorkerWins: 0,
            HelperWorkerWins: 0,
            EngineWakeCostMicroseconds: 0,
            MaxLateBatchIndex: -1,
            MaxLateCpu: -1,
            MaxLateWorker: -1,
            ProcessPriorityApplied: 0,
            ThreadPriorityApplied: 0,
            MmcssApplied: 0,
            ProcessPriorityBoostDisabled: 0,
            ThreadPriorityBoostDisabled: 0,
            WorkerPriorityAppliedCount: 0,
            WorkerMmcssAppliedCount: 0,
            WorkerPriorityBoostDisabledCount: 0,
            PriorityState: FormatPriorityState(0, 0, 0, 0, 0, 0, 0, 0),
            OutlierEvents: Array.Empty<NativeOutlierTraceEvent>(),
            Cancelled: false);
    }

    private static NativePlaybackRunDiagnostics FromStats(
        MhpRunStats stats,
        string backend,
        string fallbackReason,
        bool cancelled,
        long planCreateUs,
        NativeOutlierEvent[]? nativeOutlierEvents = null,
        bool includePlanCreateInStartup = true)
    {
        var startupUs = includePlanCreateInStartup
            ? planCreateUs + stats.NativeRunOverheadUs
            : stats.NativeRunOverheadUs;
        return new NativePlaybackRunDiagnostics(
            backend,
            fallbackReason,
            SelectedCpuSet: unchecked((int)stats.SelectedCpuSet),
            CpuMigrationCount: unchecked((int)stats.CpuMigrationCount),
            CpuSetAppliedCount: unchecked((int)stats.CpuSetAppliedCount),
            MaxLateMicroseconds: stats.MaxLateUs,
            P999LateMicroseconds: stats.P999LateUs,
            OutliersOverThreshold: unchecked((int)stats.OutliersOverThreshold),
            MaxLoopEndLateMicroseconds: stats.MaxLoopEndLateUs,
            P999LoopEndLateMicroseconds: stats.P999LoopEndLateUs,
            LoopEndOutliersOverThreshold: unchecked((int)stats.LoopEndOutliersOverThreshold),
            NativePlanCreateMicroseconds: planCreateUs,
            NativeStartupMicroseconds: startupUs,
            NativeRunOverheadMicroseconds: stats.NativeRunOverheadUs,
            NativeSubmitDurationMicroseconds: stats.NativeSubmitDurationUs,
            WaitPathBreakdown: $"spin={stats.WaitPathSpinCount} timer={stats.WaitPathTimerCount} late={stats.WaitPathLateCount}",
            LastWin32Error: stats.LastWin32Error,
            ActionsSubmitted: unchecked((int)stats.ActionsSubmitted),
            NativeInputsSubmitted: unchecked((int)stats.NativeInputsSubmitted),
            FailedSubmissions: unchecked((int)stats.FailedSubmissions),
            StandbyUsed: stats.StandbyUsed != 0,
            PrimaryWorkerWins: unchecked((int)stats.PrimaryWorkerWins),
            HelperWorkerWins: unchecked((int)stats.HelperWorkerWins),
            EngineWakeCostMicroseconds: stats.EngineWakeCostUs,
            MaxLateBatchIndex: stats.MaxLateBatchIndex == uint.MaxValue ? -1 : unchecked((int)stats.MaxLateBatchIndex),
            MaxLateCpu: stats.MaxLateCpu == uint.MaxValue ? -1 : unchecked((int)stats.MaxLateCpu),
            MaxLateWorker: stats.MaxLateWorker,
            ProcessPriorityApplied: unchecked((int)stats.ProcessPriorityApplied),
            ThreadPriorityApplied: unchecked((int)stats.ThreadPriorityApplied),
            MmcssApplied: unchecked((int)stats.MmcssApplied),
            ProcessPriorityBoostDisabled: unchecked((int)stats.ProcessPriorityBoostDisabled),
            ThreadPriorityBoostDisabled: unchecked((int)stats.ThreadPriorityBoostDisabled),
            WorkerPriorityAppliedCount: unchecked((int)stats.WorkerPriorityAppliedCount),
            WorkerMmcssAppliedCount: unchecked((int)stats.WorkerMmcssAppliedCount),
            WorkerPriorityBoostDisabledCount: unchecked((int)stats.WorkerPriorityBoostDisabledCount),
            PriorityState: FormatPriorityState(
                unchecked((int)stats.ProcessPriorityApplied),
                unchecked((int)stats.ThreadPriorityApplied),
                unchecked((int)stats.MmcssApplied),
                unchecked((int)stats.ProcessPriorityBoostDisabled),
                unchecked((int)stats.ThreadPriorityBoostDisabled),
                unchecked((int)stats.WorkerPriorityAppliedCount),
                unchecked((int)stats.WorkerMmcssAppliedCount),
                unchecked((int)stats.WorkerPriorityBoostDisabledCount)),
            OutlierEvents: ConvertOutlierEvents(stats, nativeOutlierEvents),
            Cancelled: cancelled);
    }

    private static string FormatPriorityState(
        int processPriorityApplied,
        int threadPriorityApplied,
        int mmcssApplied,
        int processPriorityBoostDisabled,
        int threadPriorityBoostDisabled,
        int workerPriorityAppliedCount,
        int workerMmcssAppliedCount,
        int workerPriorityBoostDisabledCount)
    {
        return $"process={processPriorityApplied} thread={threadPriorityApplied} mmcss={mmcssApplied} boost={processPriorityBoostDisabled}/{threadPriorityBoostDisabled} workerPriority={workerPriorityAppliedCount} workerMmcss={workerMmcssAppliedCount} workerBoost={workerPriorityBoostDisabledCount}";
    }

    private static IReadOnlyList<NativeOutlierTraceEvent> ConvertOutlierEvents(
        MhpRunStats stats,
        NativeOutlierEvent[]? nativeOutlierEvents)
    {
        if (nativeOutlierEvents is null || nativeOutlierEvents.Length == 0)
        {
            return Array.Empty<NativeOutlierTraceEvent>();
        }

        var count = Math.Min(unchecked((int)stats.OutlierEventsWritten), nativeOutlierEvents.Length);
        if (count <= 0)
        {
            return Array.Empty<NativeOutlierTraceEvent>();
        }

        var result = new NativeOutlierTraceEvent[count];
        for (var i = 0; i < count; i++)
        {
            var item = nativeOutlierEvents[i];
            result[i] = new NativeOutlierTraceEvent(
                unchecked((int)item.BatchIndex),
                unchecked((int)item.Cpu),
                item.Worker,
                item.LoopEnd != 0,
                item.DueTick,
                item.ActualTick,
                item.LateUs);
        }

        return result;
    }

}
