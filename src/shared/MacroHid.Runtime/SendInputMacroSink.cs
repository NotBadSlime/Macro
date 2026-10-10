using System.ComponentModel;
using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed record InputSubmissionStats(
    int ActionsSubmitted,
    int NativeInputsSubmitted,
    int FailedSubmissions,
    int LastWin32Error,
    long LastSubmitQpc,
    long LastSubmitDurationMicroseconds = 0,
    PlaybackTimingStats? Timing = null,
    string BackendName = "managed",
    string NativeFallbackReason = "",
    int SelectedCpuSet = -1,
    int CpuMigrationCount = 0,
    int CpuSetAppliedCount = 0,
    long MaxLateMicroseconds = 0,
    long P999LateMicroseconds = 0,
    int OutliersOverThreshold = 0,
    long MaxLoopEndLateMicroseconds = 0,
    long P999LoopEndLateMicroseconds = 0,
    int LoopEndOutliersOverThreshold = 0,
    long NativePlanCreateMicroseconds = 0,
    long NativeStartupMicroseconds = 0,
    long NativeRunOverheadMicroseconds = 0,
    long NativeSubmitDurationMicroseconds = 0,
    string WaitPathBreakdown = "",
    bool StandbyUsed = false,
    int PrimaryWorkerWins = 0,
    int HelperWorkerWins = 0,
    long EngineWakeCostMicroseconds = 0,
    int MaxLateBatchIndex = -1,
    int MaxLateCpu = -1,
    int MaxLateWorker = -1,
    int ProcessPriorityApplied = 0,
    int ThreadPriorityApplied = 0,
    int MmcssApplied = 0,
    int ProcessPriorityBoostDisabled = 0,
    int ThreadPriorityBoostDisabled = 0,
    int WorkerPriorityAppliedCount = 0,
    int WorkerMmcssAppliedCount = 0,
    int WorkerPriorityBoostDisabledCount = 0,
    string PriorityState = "");

public sealed record PlaybackTimingStats(
    int Count,
    long MinJitterMicroseconds,
    long MaxJitterMicroseconds,
    string Summary);

public sealed class SendInputMacroSink : IMacroInputSink
{
    private readonly object gate = new();
    private int actionsSubmitted;
    private int nativeInputsSubmitted;
    private int failedSubmissions;
    private int lastWin32Error;
    private long lastSubmitQpc;
    private long lastSubmitDurationMicroseconds;
    private PlaybackTimingStats? timingStats;
    private string backendName = "managed";
    private string nativeFallbackReason = string.Empty;
    private int selectedCpuSet = -1;
    private int cpuMigrationCount;
    private int cpuSetAppliedCount;
    private long maxLateMicroseconds;
    private long p999LateMicroseconds;
    private int outliersOverThreshold;
    private long maxLoopEndLateMicroseconds;
    private long p999LoopEndLateMicroseconds;
    private int loopEndOutliersOverThreshold;
    private long nativePlanCreateMicroseconds;
    private long nativeStartupMicroseconds;
    private long nativeRunOverheadMicroseconds;
    private long nativeSubmitDurationMicroseconds;
    private string waitPathBreakdown = string.Empty;
    private bool standbyUsed;
    private int primaryWorkerWins;
    private int helperWorkerWins;
    private long engineWakeCostMicroseconds;
    private int maxLateBatchIndex = -1;
    private int maxLateCpu = -1;
    private int maxLateWorker = -1;
    private int processPriorityApplied;
    private int threadPriorityApplied;
    private int mmcssApplied;
    private int processPriorityBoostDisabled;
    private int threadPriorityBoostDisabled;
    private int workerPriorityAppliedCount;
    private int workerMmcssAppliedCount;
    private int workerPriorityBoostDisabledCount;
    private string priorityState = string.Empty;
    private readonly ModifierChordClock modifierChord = new();
    public TimeSpan ModifierChordHold { get; set; } = InputPressTiming.ModifierChordHold;
    private bool splittingModifierChord;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public void Submit(uint sequence, InputAction action)
    {
        SubmitActions(sequence, [action]);
    }

    public void SubmitBatch(uint startSequence, IReadOnlyList<InputAction> actions)
    {
        SubmitActions(startSequence, actions);
    }

    public void SubmitPrepared(uint startSequence, PreparedInputBatch prepared)
    {
        if (prepared.Actions.Any(action => action is TextInputAction))
        {
            SubmitActions(startSequence, prepared.Actions);
            return;
        }

        SubmitNativeBatch(prepared);
    }

    private void SubmitActions(uint startSequence, IReadOnlyList<InputAction> actions)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("SendInput is only available on Windows.");
        }

        var pending = new List<InputAction>();
        foreach (var action in actions)
        {
            if (action is TextInputAction text)
            {
                FlushPending(pending);
                SubmitText(text.Text);
                continue;
            }

            pending.Add(action);
        }

        FlushPending(pending);
    }

    private void FlushPending(List<InputAction> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        SubmitNativeBatch(PreparedInputBatch.FromActions(pending));
        pending.Clear();
    }

    private void SubmitText(string text)
    {
        if (BackgroundWindowSession.IsActive)
        {
            BackgroundWindowInput.Submit([new TextInputAction(text)]);
            lock (gate)
            {
                actionsSubmitted++;
                lastWin32Error = 0;
            }

            return;
        }

        if (ClipboardTextSender.TrySend(text, this, CancellationToken.None, out _))
        {
            return;
        }

        SubmitNativeBatch(PreparedInputBatch.FromActions([new TextInputAction(text)]));
    }

    private void SubmitNativeBatch(PreparedInputBatch prepared)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("SendInput is only available on Windows.");
        }

        if (!splittingModifierChord && ModifierChordClock.TrySplit(prepared.Actions, out var splitAt))
        {
            splittingModifierChord = true;
            try
            {
                SubmitNativeBatch(PreparedInputBatch.FromActions(prepared.Actions.Take(splitAt).ToArray()));
                SubmitNativeBatch(PreparedInputBatch.FromActions(prepared.Actions.Skip(splitAt).ToArray()));
            }
            finally
            {
                splittingModifierChord = false;
            }

            return;
        }

        if (BackgroundWindowSession.IsActive)
        {
            BackgroundWindowInput.Submit(prepared.Actions);
            lock (gate)
            {
                actionsSubmitted += prepared.ActionCount;
                lastWin32Error = 0;
            }

            return;
        }

        RuntimeNativeMethods.QueryPerformanceFrequency(out var frequency);
        RuntimeNativeMethods.QueryPerformanceCounter(out var submitQpc);
        var chordDue = modifierChord.AdjustDueTick(
            prepared.Actions,
            submitQpc,
            InputPressTiming.HoldTicks(ModifierChordHold, frequency));
        WaitUntilTick(chordDue);
        RuntimeNativeMethods.QueryPerformanceCounter(out submitQpc);

        if (prepared.NativeInputCount == 0)
        {
            RuntimeNativeMethods.QueryPerformanceCounter(out var afterEmptySubmit);
            lock (gate)
            {
                actionsSubmitted += prepared.ActionCount;
                lastWin32Error = 0;
                lastSubmitQpc = submitQpc;
                lastSubmitDurationMicroseconds = ToMicroseconds(afterEmptySubmit - submitQpc, frequency);
            }
            return;
        }

        var sent = SubmitNativeInputs(prepared.NativeInputs);
        RuntimeNativeMethods.QueryPerformanceCounter(out var afterSubmit);
        var durationUs = ToMicroseconds(afterSubmit - submitQpc, frequency);
        if (sent != prepared.NativeInputCount)
        {
            var error = Marshal.GetLastWin32Error();
            lock (gate)
            {
                actionsSubmitted += prepared.ActionCount;
                failedSubmissions++;
                lastWin32Error = error;
                lastSubmitQpc = submitQpc;
                lastSubmitDurationMicroseconds = durationUs;
            }
            throw new Win32Exception(error, $"SendInput submitted {sent} of {prepared.NativeInputCount} native inputs (batch of {prepared.ActionCount} actions).");
        }

        lock (gate)
        {
            actionsSubmitted += prepared.ActionCount;
            nativeInputsSubmitted += prepared.NativeInputCount;
            lastWin32Error = 0;
            lastSubmitQpc = submitQpc;
            lastSubmitDurationMicroseconds = durationUs;
        }

        modifierChord.NoteSent(prepared.Actions, afterSubmit);
    }

    private static void WaitUntilTick(long dueTick)
    {
        RuntimeNativeMethods.QueryPerformanceFrequency(out var frequency);
        if (frequency <= 0)
        {
            return;
        }

        while (true)
        {
            RuntimeNativeMethods.QueryPerformanceCounter(out var now);
            if (now >= dueTick)
            {
                return;
            }

            var remainingMs = (dueTick - now) * 1000.0 / frequency;
            if (remainingMs >= 2)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(50);
            }
        }
    }

    public void SetTimingStats(PlaybackTimingStats timing)
    {
        lock (gate)
        {
            timingStats = timing;
        }
    }

    internal void SetNativePlaybackDiagnostics(NativePlaybackRunDiagnostics diagnostics)
    {
        lock (gate)
        {
            backendName = diagnostics.BackendName;
            nativeFallbackReason = diagnostics.NativeFallbackReason;
            selectedCpuSet = diagnostics.SelectedCpuSet;
            cpuMigrationCount = diagnostics.CpuMigrationCount;
            cpuSetAppliedCount = diagnostics.CpuSetAppliedCount;
            maxLateMicroseconds = diagnostics.MaxLateMicroseconds;
            p999LateMicroseconds = diagnostics.P999LateMicroseconds;
            outliersOverThreshold = diagnostics.OutliersOverThreshold;
            maxLoopEndLateMicroseconds = diagnostics.MaxLoopEndLateMicroseconds;
            p999LoopEndLateMicroseconds = diagnostics.P999LoopEndLateMicroseconds;
            loopEndOutliersOverThreshold = diagnostics.LoopEndOutliersOverThreshold;
            nativePlanCreateMicroseconds = diagnostics.NativePlanCreateMicroseconds;
            nativeStartupMicroseconds = diagnostics.NativeStartupMicroseconds;
            nativeRunOverheadMicroseconds = diagnostics.NativeRunOverheadMicroseconds;
            nativeSubmitDurationMicroseconds = diagnostics.NativeSubmitDurationMicroseconds;
            waitPathBreakdown = diagnostics.WaitPathBreakdown;
            standbyUsed = diagnostics.StandbyUsed;
            primaryWorkerWins = diagnostics.PrimaryWorkerWins;
            helperWorkerWins = diagnostics.HelperWorkerWins;
            engineWakeCostMicroseconds = diagnostics.EngineWakeCostMicroseconds;
            maxLateBatchIndex = diagnostics.MaxLateBatchIndex;
            maxLateCpu = diagnostics.MaxLateCpu;
            maxLateWorker = diagnostics.MaxLateWorker;
            processPriorityApplied = diagnostics.ProcessPriorityApplied;
            threadPriorityApplied = diagnostics.ThreadPriorityApplied;
            mmcssApplied = diagnostics.MmcssApplied;
            processPriorityBoostDisabled = diagnostics.ProcessPriorityBoostDisabled;
            threadPriorityBoostDisabled = diagnostics.ThreadPriorityBoostDisabled;
            workerPriorityAppliedCount = diagnostics.WorkerPriorityAppliedCount;
            workerMmcssAppliedCount = diagnostics.WorkerMmcssAppliedCount;
            workerPriorityBoostDisabledCount = diagnostics.WorkerPriorityBoostDisabledCount;
            priorityState = diagnostics.PriorityState;
            lastWin32Error = diagnostics.LastWin32Error;

            if (diagnostics.BackendName.StartsWith("native", StringComparison.OrdinalIgnoreCase))
            {
                actionsSubmitted += diagnostics.ActionsSubmitted;
                nativeInputsSubmitted += diagnostics.NativeInputsSubmitted;
                failedSubmissions += diagnostics.FailedSubmissions;
                lastSubmitDurationMicroseconds = diagnostics.NativeSubmitDurationMicroseconds;
            }
        }
    }

    public InputSubmissionStats? GetStats()
    {
        lock (gate)
        {
            return new InputSubmissionStats(
                actionsSubmitted,
                nativeInputsSubmitted,
                failedSubmissions,
                lastWin32Error,
                lastSubmitQpc,
                lastSubmitDurationMicroseconds,
                timingStats,
                backendName,
                nativeFallbackReason,
                selectedCpuSet,
                cpuMigrationCount,
                cpuSetAppliedCount,
                maxLateMicroseconds,
                p999LateMicroseconds,
                outliersOverThreshold,
                maxLoopEndLateMicroseconds,
                p999LoopEndLateMicroseconds,
                loopEndOutliersOverThreshold,
                nativePlanCreateMicroseconds,
                nativeStartupMicroseconds,
                nativeRunOverheadMicroseconds,
                nativeSubmitDurationMicroseconds,
                waitPathBreakdown,
                standbyUsed,
                primaryWorkerWins,
                helperWorkerWins,
                engineWakeCostMicroseconds,
                maxLateBatchIndex,
                maxLateCpu,
                maxLateWorker,
                processPriorityApplied,
                threadPriorityApplied,
                mmcssApplied,
                processPriorityBoostDisabled,
                threadPriorityBoostDisabled,
                workerPriorityAppliedCount,
                workerMmcssAppliedCount,
                workerPriorityBoostDisabledCount,
                priorityState);
        }
    }

    private static uint SubmitNativeInputs(NativeInput[] inputs)
    {
        if (inputs.Length == 0)
        {
            return 0;
        }

        if (!inputs.Any(MouseCursorUnclip.IsRelativeMouse))
        {
            return RuntimeNativeMethods.SendInput((uint)inputs.Length, inputs, NativeInputSize.Value);
        }

        uint sent = 0;
        for (var i = 0; i < inputs.Length; i++)
        {
            if (MouseCursorUnclip.IsRelativeMouse(inputs[i]))
            {
                MouseCursorUnclip.RecenterIfNeeded(inputs[i].Union.Mouse.Dx, inputs[i].Union.Mouse.Dy);
            }

            var one = RuntimeNativeMethods.SendInput(1, [inputs[i]], NativeInputSize.Value);
            sent += one;
            if (one != 1)
            {
                break;
            }
        }

        return sent;
    }

    private static long ToMicroseconds(long ticks, long frequency)
    {
        return frequency <= 0 ? 0 : (long)Math.Round(ticks * 1_000_000.0 / frequency, MidpointRounding.AwayFromZero);
    }

    private static class NativeInputSize
    {
        public static readonly int Value = Marshal.SizeOf<NativeInput>();
    }
}
