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
    PlaybackTimingStats? Timing = null);

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

    public bool IsAvailable => OperatingSystem.IsWindows();

    public void Submit(uint sequence, InputAction action)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("SendInput is only available on Windows.");
        }

        SubmitPrepared(sequence, PreparedInputBatch.FromActions([action]));
    }

    public void SubmitBatch(uint startSequence, IReadOnlyList<InputAction> actions)
    {
        SubmitPrepared(startSequence, PreparedInputBatch.FromActions(actions));
    }

    public void SubmitPrepared(uint startSequence, PreparedInputBatch prepared)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("SendInput is only available on Windows.");
        }

        RuntimeNativeMethods.QueryPerformanceFrequency(out var frequency);
        RuntimeNativeMethods.QueryPerformanceCounter(out var submitQpc);

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

        var sent = RuntimeNativeMethods.SendInput(
            (uint)prepared.NativeInputCount,
            prepared.NativeInputs,
            NativeInputSize.Value);
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
    }

    public void SetTimingStats(PlaybackTimingStats timing)
    {
        lock (gate)
        {
            timingStats = timing;
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
                timingStats);
        }
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
