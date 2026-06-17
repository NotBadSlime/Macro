using System.Diagnostics;
using System.Runtime;
using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed class PrecisionPlaybackContext : IDisposable
{
    private readonly PrecisionMode mode;
    private readonly Thread thread;
    private readonly ThreadPriority previousThreadPriority;
    private readonly ProcessPriorityClass? previousProcessPriority;
    private readonly GCLatencyMode previousLatencyMode;
    private readonly bool noGcRegionActive;
    private readonly UIntPtr previousAffinity;
    private readonly IDisposable? timerScope;
    private bool disposed;

    private PrecisionPlaybackContext(
        PrecisionMode mode,
        Thread thread,
        ThreadPriority previousThreadPriority,
        ProcessPriorityClass? previousProcessPriority,
        GCLatencyMode previousLatencyMode,
        bool noGcRegionActive,
        UIntPtr previousAffinity,
        IDisposable? timerScope)
    {
        this.mode = mode;
        this.thread = thread;
        this.previousThreadPriority = previousThreadPriority;
        this.previousProcessPriority = previousProcessPriority;
        this.previousLatencyMode = previousLatencyMode;
        this.noGcRegionActive = noGcRegionActive;
        this.previousAffinity = previousAffinity;
        this.timerScope = timerScope;
    }

    public static PrecisionPlaybackContext Enter(PrecisionMode mode)
    {
        var thread = Thread.CurrentThread;
        var previousThreadPriority = thread.Priority;
        ProcessPriorityClass? previousProcessPriority = null;
        var previousLatencyMode = GCSettings.LatencyMode;
        var previousAffinity = UIntPtr.Zero;
        IDisposable? timerScope = null;
        var noGcRegionActive = false;

        if (mode == PrecisionMode.ExtremeDuringPlayback)
        {
            try
            {
                thread.Priority = ThreadPriority.Highest;
                if (OperatingSystem.IsWindows())
                {
                    RuntimeNativeMethods.SetThreadPriority(
                        RuntimeNativeMethods.GetCurrentThread(),
                        RuntimeNativeMethods.ThreadPriorityTimeCritical);
                }
            }
            catch { }

            try
            {
                using var process = Process.GetCurrentProcess();
                previousProcessPriority = process.PriorityClass;
                process.PriorityClass = ProcessPriorityClass.High;
                RuntimeNativeMethods.SetProcessPriorityBoost(RuntimeNativeMethods.GetCurrentProcess(), true);
            }
            catch { }

            if (OperatingSystem.IsWindows())
            {
                var threadHandle = RuntimeNativeMethods.GetCurrentThread();
                previousAffinity = RuntimeNativeMethods.SetThreadAffinityMask(threadHandle, new UIntPtr(2));
                RuntimeNativeMethods.SetThreadIdealProcessor(threadHandle, 1);
                timerScope = TimerResolutionScope.TryBeginHighResolution();
            }

            try
            {
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                noGcRegionActive = GC.TryStartNoGCRegion(4 * 1024 * 1024, disallowFullBlockingGC: false);
            }
            catch
            {
                noGcRegionActive = false;
            }
        }

        return new PrecisionPlaybackContext(
            mode,
            thread,
            previousThreadPriority,
            previousProcessPriority,
            previousLatencyMode,
            noGcRegionActive,
            previousAffinity,
            timerScope);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (noGcRegionActive)
        {
            try { GC.EndNoGCRegion(); } catch { }
        }

        try { GCSettings.LatencyMode = previousLatencyMode; } catch { }

        if (mode == PrecisionMode.ExtremeDuringPlayback && OperatingSystem.IsWindows())
        {
            try
            {
                if (previousAffinity != UIntPtr.Zero)
                {
                    RuntimeNativeMethods.SetThreadAffinityMask(RuntimeNativeMethods.GetCurrentThread(), previousAffinity);
                }

                RuntimeNativeMethods.SetProcessPriorityBoost(RuntimeNativeMethods.GetCurrentProcess(), false);
            }
            catch { }
        }

        timerScope?.Dispose();

        if (previousProcessPriority is { } processPriority)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                process.PriorityClass = processPriority;
            }
            catch { }
        }

        try { thread.Priority = previousThreadPriority; } catch { }
    }

    private sealed class TimerResolutionScope : IDisposable
    {
        private readonly bool ntdllActive;
        private readonly bool winmmActive;
        private readonly uint ntdllResolution;

        private TimerResolutionScope(bool ntdllActive, uint ntdllResolution, bool winmmActive)
        {
            this.ntdllActive = ntdllActive;
            this.ntdllResolution = ntdllResolution;
            this.winmmActive = winmmActive;
        }

        public static TimerResolutionScope TryBeginHighResolution()
        {
            if (!OperatingSystem.IsWindows())
            {
                return new TimerResolutionScope(false, 0, false);
            }

            var status = RuntimeNativeMethods.NtSetTimerResolution(5000, true, out var actualResolution);
            if (status == 0)
            {
                return new TimerResolutionScope(ntdllActive: true, actualResolution, winmmActive: false);
            }

            var winmmResult = RuntimeNativeMethods.timeBeginPeriod(1) == 0;
            return new TimerResolutionScope(ntdllActive: false, 0, winmmActive: winmmResult);
        }

        public void Dispose()
        {
            if (ntdllActive)
            {
                RuntimeNativeMethods.NtSetTimerResolution(ntdllResolution, false, out _);
            }
            else if (winmmActive)
            {
                RuntimeNativeMethods.timeEndPeriod(1);
            }
        }
    }
}
