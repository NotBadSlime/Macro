using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed class PrecisionPlaybackContext : IDisposable
{
    private static readonly object ProcessorSelectionGate = new();
    private static readonly object ProcessStateGate = new();
    private static int preciseUsers;
    private static int allUsers;
    private static PrecisionPlaybackContext? processOwner;
    private static IDisposable? sharedPowerScope;
    private static IDisposable? sharedTimerScope;
    private static UIntPtr cachedProcessorMask;

    private readonly PrecisionMode mode;
    private readonly Thread thread;
    private readonly ThreadPriority previousThreadPriority;
    private readonly ProcessPriorityClass? previousProcessPriority;
    private readonly GCLatencyMode previousLatencyMode;
    private readonly bool noGcRegionActive;
    private readonly bool threadAffinityActive;
    private readonly bool criticalRegionActive;
    private readonly bool threadPriorityBoostCaptured;
    private readonly bool previousThreadPriorityBoostDisabled;
    private readonly bool processPriorityBoostCaptured;
    private readonly bool previousProcessPriorityBoostDisabled;
    private readonly UIntPtr previousAffinity;
    private readonly IDisposable? timerScope;
    private readonly IDisposable? powerThrottlingScope;
    private readonly IDisposable? mmcssScope;
    private bool disposed;

    private PrecisionPlaybackContext(
        PrecisionMode mode,
        Thread thread,
        ThreadPriority previousThreadPriority,
        ProcessPriorityClass? previousProcessPriority,
        GCLatencyMode previousLatencyMode,
        bool noGcRegionActive,
        bool threadAffinityActive,
        bool criticalRegionActive,
        bool threadPriorityBoostCaptured,
        bool previousThreadPriorityBoostDisabled,
        bool processPriorityBoostCaptured,
        bool previousProcessPriorityBoostDisabled,
        UIntPtr previousAffinity,
        IDisposable? timerScope,
        IDisposable? powerThrottlingScope,
        IDisposable? mmcssScope)
    {
        this.mode = mode;
        this.thread = thread;
        this.previousThreadPriority = previousThreadPriority;
        this.previousProcessPriority = previousProcessPriority;
        this.previousLatencyMode = previousLatencyMode;
        this.noGcRegionActive = noGcRegionActive;
        this.threadAffinityActive = threadAffinityActive;
        this.criticalRegionActive = criticalRegionActive;
        this.threadPriorityBoostCaptured = threadPriorityBoostCaptured;
        this.previousThreadPriorityBoostDisabled = previousThreadPriorityBoostDisabled;
        this.processPriorityBoostCaptured = processPriorityBoostCaptured;
        this.previousProcessPriorityBoostDisabled = previousProcessPriorityBoostDisabled;
        this.previousAffinity = previousAffinity;
        this.timerScope = timerScope;
        this.powerThrottlingScope = powerThrottlingScope;
        this.mmcssScope = mmcssScope;
    }

    internal static int ActiveUsers { get { lock (ProcessStateGate) return allUsers; } }

    /// <summary>
    /// Keeps only the process-wide timer resolution and power-throttling opt-out alive while no run is active.
    /// An isolated hotkey run otherwise pays for switching both on (measured 20-130us) before its first input.
    /// Thread priority, affinity and GC mode stay per-run. Costs power while held, so callers should hold it only
    /// while a low-jitter macro can actually be triggered.
    /// </summary>
    public static IDisposable AcquireWarmLease()
    {
        lock (ProcessStateGate)
        {
            BeginSharedScopesIfIdle();
            allUsers++;
            return new WarmLease();
        }
    }

    private static void BeginSharedScopesIfIdle()
    {
        if (OperatingSystem.IsWindows() && allUsers == 0)
        {
            sharedTimerScope = TimerResolutionScope.TryBeginHighResolution();
            sharedPowerScope = ProcessPowerThrottlingScope.TryBegin();
        }
    }

    private static void EndSharedScopes()
    {
        sharedPowerScope?.Dispose(); sharedPowerScope = null;
        sharedTimerScope?.Dispose(); sharedTimerScope = null;
    }

    private sealed class WarmLease : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            lock (ProcessStateGate)
            {
                if (--allUsers == 0) EndSharedScopes();
            }
        }
    }

    public static PrecisionPlaybackContext Enter(PrecisionMode mode)
    {
        lock (ProcessStateGate) return EnterCore(mode);
    }

    /// <summary>
    /// <paramref name="generation"/> 1.6.0 also applies what that release did on the playback thread and was removed in
    /// 2.0.0: time-critical thread priority, High (极致: RealTime) process priority with priority boost off, MMCSS Pro Audio,
    /// pinning 极致 to the lowest-jitter core, and a no-GC region.
    /// </summary>
    public static PrecisionPlaybackContext Enter(PrecisionMode mode, PrecisionGeneration generation)
    {
        lock (ProcessStateGate)
        {
            var context = EnterCore(mode);
            if (PrecisionGenerations.UsesLegacyNativeEngine(PrecisionGenerations.Normalize(generation)))
                context.ApplyV1_6();
            return context;
        }
    }

    // State applied by ApplyV1_6, restored by this context alone (as 1.6.0 did per run).
    private bool v16Applied;
    private ProcessPriorityClass? v16ProcessPriority;
    private bool v16ProcessBoostCaptured;
    private bool v16PreviousProcessBoostDisabled;
    private IDisposable? v16Mmcss;
    private UIntPtr v16PreviousAffinity;
    private bool v16NoGcRegion;

    private void ApplyV1_6()
    {
        if (mode is not (PrecisionMode.ExtremeDuringPlayback or PrecisionMode.UltraLowJitter) || !OperatingSystem.IsWindows())
            return;
        v16Applied = true;
        try
        {
            RuntimeNativeMethods.SetThreadPriority(RuntimeNativeMethods.GetCurrentThread(), RuntimeNativeMethods.ThreadPriorityTimeCritical);
        }
        catch { }

        try
        {
            using var process = Process.GetCurrentProcess();
            v16ProcessPriority = process.PriorityClass;
            process.PriorityClass = mode == PrecisionMode.UltraLowJitter ? ProcessPriorityClass.RealTime : ProcessPriorityClass.High;
            if (RuntimeNativeMethods.GetProcessPriorityBoost(RuntimeNativeMethods.GetCurrentProcess(), out v16PreviousProcessBoostDisabled))
                v16ProcessBoostCaptured = true;
            RuntimeNativeMethods.SetProcessPriorityBoost(RuntimeNativeMethods.GetCurrentProcess(), true);
        }
        catch { }

        try
        {
            var threadHandle = RuntimeNativeMethods.GetCurrentThread();
            v16Mmcss = MmcssScope.TryBegin();
            if (mode == PrecisionMode.UltraLowJitter)
            {
                var selectedProcessorMask = SelectLowestJitterProcessorMask();
                if (selectedProcessorMask != UIntPtr.Zero)
                {
                    v16PreviousAffinity = RuntimeNativeMethods.SetThreadAffinityMask(threadHandle, selectedProcessorMask);
                    var idealProcessor = GetFirstProcessorIndex(selectedProcessorMask);
                    if (idealProcessor >= 0)
                        RuntimeNativeMethods.SetThreadIdealProcessor(threadHandle, (uint)idealProcessor);
                }
            }
        }
        catch { }

        try { v16NoGcRegion = GC.TryStartNoGCRegion(4 * 1024 * 1024, disallowFullBlockingGC: false); }
        catch { v16NoGcRegion = false; }
    }

    private void RevertV1_6()
    {
        if (!v16Applied) return;
        v16Applied = false;
        if (v16NoGcRegion)
        {
            try { GC.EndNoGCRegion(); } catch { }
        }

        try
        {
            if (v16PreviousAffinity != UIntPtr.Zero)
                RuntimeNativeMethods.SetThreadAffinityMask(RuntimeNativeMethods.GetCurrentThread(), v16PreviousAffinity);
            if (v16ProcessBoostCaptured)
                RuntimeNativeMethods.SetProcessPriorityBoost(RuntimeNativeMethods.GetCurrentProcess(), v16PreviousProcessBoostDisabled);
        }
        catch { }

        v16Mmcss?.Dispose();
        if (v16ProcessPriority is { } processPriority)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                process.PriorityClass = processPriority;
            }
            catch { }
        }
    }

    private static PrecisionPlaybackContext EnterCore(PrecisionMode mode)
    {
        var thread = Thread.CurrentThread;
        var previousThreadPriority = thread.Priority;
        ProcessPriorityClass? previousProcessPriority = null;
        var previousLatencyMode = GCSettings.LatencyMode;
        var previousAffinity = UIntPtr.Zero;
        IDisposable? timerScope = null;
        IDisposable? powerThrottlingScope = null;
        IDisposable? mmcssScope = null;
        var noGcRegionActive = false;
        var threadAffinityActive = false;
        var criticalRegionActive = false;
        var threadPriorityBoostCaptured = false;
        var previousThreadPriorityBoostDisabled = false;
        var processPriorityBoostCaptured = false;
        var previousProcessPriorityBoostDisabled = false;

        BeginSharedScopesIfIdle();

        if (mode is PrecisionMode.ExtremeDuringPlayback or PrecisionMode.UltraLowJitter)
        {
            try
            {
                thread.Priority = ThreadPriority.Highest;
                if (OperatingSystem.IsWindows())
                {
                    if (RuntimeNativeMethods.GetThreadPriorityBoost(
                        RuntimeNativeMethods.GetCurrentThread(),
                        out previousThreadPriorityBoostDisabled))
                    {
                        threadPriorityBoostCaptured = true;
                    }

                    RuntimeNativeMethods.SetThreadPriorityBoost(RuntimeNativeMethods.GetCurrentThread(), true);
                    RuntimeNativeMethods.SetThreadPriority(
                        RuntimeNativeMethods.GetCurrentThread(),
                        2); // THREAD_PRIORITY_HIGHEST; never time critical
                }
            }
            catch { }

            try
            {
                Thread.BeginThreadAffinity();
                threadAffinityActive = true;
                Thread.BeginCriticalRegion();
                criticalRegionActive = true;
            }
            catch
            {
                if (criticalRegionActive)
                {
                    try { Thread.EndCriticalRegion(); } catch { }
                    criticalRegionActive = false;
                }

                if (threadAffinityActive)
                {
                    try { Thread.EndThreadAffinity(); } catch { }
                    threadAffinityActive = false;
                }
            }

            // MMCSS and core binding belong exclusively to the two native timing workers.

            if (preciseUsers == 0) try
            {
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                // Do not induce a full collection to enter NoGCRegion.
            }
            catch
            {
                noGcRegionActive = false;
            }
        }

        var result = new PrecisionPlaybackContext(
            mode,
            thread,
            previousThreadPriority,
            previousProcessPriority,
            previousLatencyMode,
            noGcRegionActive,
            threadAffinityActive,
            criticalRegionActive,
            threadPriorityBoostCaptured,
            previousThreadPriorityBoostDisabled,
            processPriorityBoostCaptured,
            previousProcessPriorityBoostDisabled,
            previousAffinity,
            timerScope,
            powerThrottlingScope,
            mmcssScope);
        allUsers++;
        if (mode is PrecisionMode.ExtremeDuringPlayback or PrecisionMode.UltraLowJitter)
        {
            if (preciseUsers++ == 0) processOwner = result;
        }
        return result;
    }

    public void Dispose()
    {
        lock (ProcessStateGate) DisposeCore();
    }

    private void DisposeCore()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        RevertV1_6();

        var owner = mode is PrecisionMode.ExtremeDuringPlayback or PrecisionMode.UltraLowJitter
            && --preciseUsers == 0 ? processOwner : null;
        if (owner?.noGcRegionActive == true)
        {
            try { GC.EndNoGCRegion(); } catch { }
        }

        if (owner is not null) try { GCSettings.LatencyMode = owner.previousLatencyMode; } catch { }

        if ((mode is PrecisionMode.ExtremeDuringPlayback or PrecisionMode.UltraLowJitter) && OperatingSystem.IsWindows())
        {
            try
            {
                if (previousAffinity != UIntPtr.Zero)
                {
                    RuntimeNativeMethods.SetThreadAffinityMask(RuntimeNativeMethods.GetCurrentThread(), previousAffinity);
                }

                if (threadPriorityBoostCaptured)
                {
                    RuntimeNativeMethods.SetThreadPriorityBoost(
                        RuntimeNativeMethods.GetCurrentThread(),
                        previousThreadPriorityBoostDisabled);
                }

                if (owner?.processPriorityBoostCaptured == true)
                {
                    RuntimeNativeMethods.SetProcessPriorityBoost(
                        RuntimeNativeMethods.GetCurrentProcess(),
                        owner.previousProcessPriorityBoostDisabled);
                }
            }
            catch { }
        }

        mmcssScope?.Dispose();
        if (--allUsers == 0) EndSharedScopes();
        timerScope?.Dispose();

        if (owner?.previousProcessPriority is { } processPriority)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                process.PriorityClass = processPriority;
            }
            catch { }
        }

        try { thread.Priority = previousThreadPriority; } catch { }

        if (criticalRegionActive)
        {
            try { Thread.EndCriticalRegion(); } catch { }
        }

        if (threadAffinityActive)
        {
            try { Thread.EndThreadAffinity(); } catch { }
        }
    }

    public static void Prepare(PrecisionMode mode)
    {
        if (mode == PrecisionMode.UltraLowJitter) SelectLowestJitterProcessorMask();
    }

    private static UIntPtr SelectLowestJitterProcessorMask()
    {
        if (!OperatingSystem.IsWindows())
        {
            return UIntPtr.Zero;
        }

        lock (ProcessorSelectionGate)
        {
            if (cachedProcessorMask != UIntPtr.Zero)
            {
                return cachedProcessorMask;
            }
        }

        var allowedMask = GetAllowedProcessorMask();
        if (allowedMask == 0)
        {
            return UIntPtr.Zero;
        }

        var threadHandle = RuntimeNativeMethods.GetCurrentThread();
        UIntPtr originalAffinity = UIntPtr.Zero;
        UIntPtr bestMask = UIntPtr.Zero;
        var bestWorstCaseTicks = long.MaxValue;

        try
        {
            var hasMultipleProcessors = (allowedMask & (allowedMask - 1)) != 0;
            var allowedProcessorCount = 0;
            for (var processor = 0; processor < Math.Min(Environment.ProcessorCount, 63); processor++)
            {
                if ((allowedMask & (1L << processor)) != 0)
                {
                    allowedProcessorCount++;
                }
            }

            var noisyCoreSkipCount = allowedProcessorCount > 8 ? 4 : (hasMultipleProcessors ? 1 : 0);
            for (var processor = 0; processor < Math.Min(Environment.ProcessorCount, 63); processor++)
            {
                if (processor < noisyCoreSkipCount)
                {
                    continue;
                }

                var maskValue = 1L << processor;
                if ((allowedMask & maskValue) == 0)
                {
                    continue;
                }

                var candidateMask = new UIntPtr((ulong)maskValue);
                var previous = RuntimeNativeMethods.SetThreadAffinityMask(threadHandle, candidateMask);
                if (previous == UIntPtr.Zero)
                {
                    continue;
                }

                originalAffinity = originalAffinity == UIntPtr.Zero ? previous : originalAffinity;
                var worstCaseTicks = MeasureProcessorJitterTicks();
                if (worstCaseTicks < bestWorstCaseTicks)
                {
                    bestWorstCaseTicks = worstCaseTicks;
                    bestMask = candidateMask;
                }
            }
        }
        catch
        {
            bestMask = UIntPtr.Zero;
        }
        finally
        {
            if (originalAffinity != UIntPtr.Zero)
            {
                RuntimeNativeMethods.SetThreadAffinityMask(threadHandle, originalAffinity);
            }
        }

        if (bestMask != UIntPtr.Zero)
        {
            lock (ProcessorSelectionGate)
            {
                cachedProcessorMask = bestMask;
            }
        }

        return bestMask;
    }

    private static long GetAllowedProcessorMask()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
#pragma warning disable CA1416
            return process.ProcessorAffinity.ToInt64();
#pragma warning restore CA1416
        }
        catch
        {
            var processorCount = Math.Clamp(Environment.ProcessorCount, 1, 62);
            return (1L << processorCount) - 1;
        }
    }

    private static long MeasureProcessorJitterTicks()
    {
        if (!RuntimeNativeMethods.QueryPerformanceFrequency(out var frequency)
            || !RuntimeNativeMethods.QueryPerformanceCounter(out var dueTick))
        {
            return long.MaxValue;
        }

        var intervalTicks = Math.Max(1, frequency / 1_000);
        var worst = 0L;
        dueTick += intervalTicks;

        for (var i = 0; i < 4; i++)
        {
            long current;
            do
            {
                Thread.SpinWait(64);
                if (!RuntimeNativeMethods.QueryPerformanceCounter(out current))
                {
                    return long.MaxValue;
                }
            }
            while (current < dueTick);

            worst = Math.Max(worst, current - dueTick);
            dueTick += intervalTicks;
        }

        if (worst == 0 && RuntimeNativeMethods.QueryPerformanceCounter(out var endTick))
        {
            var expectedEnd = dueTick - intervalTicks;
            if (endTick > expectedEnd)
            {
                worst = endTick - expectedEnd;
            }
        }

        return worst;
    }

    private static int GetFirstProcessorIndex(UIntPtr mask)
    {
        var value = unchecked((long)mask.ToUInt64());
        for (var i = 0; i < Math.Min(Environment.ProcessorCount, 63); i++)
        {
            if ((value & (1L << i)) != 0)
            {
                return i;
            }
        }

        return -1;
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

    private sealed class ProcessPowerThrottlingScope : IDisposable
    {
        private readonly bool hasPrevious;
        private readonly ProcessPowerThrottlingState previous;
        private bool disposed;

        private ProcessPowerThrottlingScope(bool hasPrevious, ProcessPowerThrottlingState previous)
        {
            this.hasPrevious = hasPrevious;
            this.previous = previous;
        }

        public static ProcessPowerThrottlingScope? TryBegin()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var size = (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();
            var processHandle = RuntimeNativeMethods.GetCurrentProcess();
            var previous = new ProcessPowerThrottlingState
            {
                Version = RuntimeNativeMethods.ProcessPowerThrottlingCurrentVersion
            };
            var hasPrevious = RuntimeNativeMethods.GetProcessInformation(
                processHandle,
                RuntimeNativeMethods.ProcessPowerThrottling,
                ref previous,
                size);

            var state = new ProcessPowerThrottlingState
            {
                Version = RuntimeNativeMethods.ProcessPowerThrottlingCurrentVersion,
                ControlMask = RuntimeNativeMethods.ProcessPowerThrottlingExecutionSpeed
                    | RuntimeNativeMethods.ProcessPowerThrottlingIgnoreTimerResolution,
                StateMask = 0
            };

            RuntimeNativeMethods.SetProcessInformation(
                processHandle,
                RuntimeNativeMethods.ProcessPowerThrottling,
                ref state,
                size);
            return new ProcessPowerThrottlingScope(hasPrevious, previous);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (!hasPrevious || !OperatingSystem.IsWindows())
            {
                return;
            }

            var restore = previous;
            RuntimeNativeMethods.SetProcessInformation(
                RuntimeNativeMethods.GetCurrentProcess(),
                RuntimeNativeMethods.ProcessPowerThrottling,
                ref restore,
                (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
    }

    private sealed class MmcssScope : IDisposable
    {
        private readonly IntPtr handle;
        private bool disposed;

        private MmcssScope(IntPtr handle)
        {
            this.handle = handle;
        }

        public static MmcssScope? TryBegin()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var handle = RuntimeNativeMethods.AvSetMmThreadCharacteristicsW("Pro Audio", out _);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            RuntimeNativeMethods.AvSetMmThreadPriority(handle, 2);
            return new MmcssScope(handle);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (handle != IntPtr.Zero && OperatingSystem.IsWindows())
            {
                RuntimeNativeMethods.AvRevertMmThreadCharacteristics(handle);
            }
        }
    }
}
