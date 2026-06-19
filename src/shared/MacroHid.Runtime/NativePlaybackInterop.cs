using System.Runtime.InteropServices;

namespace MacroHid.Runtime;

internal enum MhpStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    Win32Error = 2,
    Cancelled = 3
}

internal enum NativeEngineMode : int
{
    Auto = 0,
    Standby = 1,
    Inline = 2
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct MhpBatch
{
    public MhpBatch(long dueTicks, uint inputOffset, uint inputCount, uint actionCount)
    {
        DueTicks = dueTicks;
        InputOffset = inputOffset;
        InputCount = inputCount;
        ActionCount = actionCount;
    }

    public readonly long DueTicks;
    public readonly uint InputOffset;
    public readonly uint InputCount;
    public readonly uint ActionCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MhpRunOptions
{
    public int PrecisionMode;
    public int EnableCpuScan;
    public int OutlierThresholdUs;
    public int LoopStepCount;
    public long QpcFrequency;
    public int NativeEngineMode;
    public IntPtr OutlierEvents;
    public uint OutlierEventCapacity;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MhpRunStats
{
    public uint BatchesSubmitted;
    public uint ActionsSubmitted;
    public uint NativeInputsSubmitted;
    public uint FailedSubmissions;
    public uint SelectedCpuSet;
    public uint CpuMigrationCount;
    public uint CpuSetAppliedCount;
    public uint OutliersOverThreshold;
    public uint LoopEndOutliersOverThreshold;
    public long MaxLateUs;
    public long P999LateUs;
    public long MaxLoopEndLateUs;
    public long P999LoopEndLateUs;
    public long NativeRunOverheadUs;
    public long NativeSubmitDurationUs;
    public uint WaitPathSpinCount;
    public uint WaitPathTimerCount;
    public uint WaitPathLateCount;
    public int LastWin32Error;
    public uint StandbyUsed;
    public uint PrimaryWorkerWins;
    public uint HelperWorkerWins;
    public long EngineWakeCostUs;
    public uint MaxLateBatchIndex;
    public uint MaxLateCpu;
    public int MaxLateWorker;
    public uint OutlierEventsWritten;
    public uint ProcessPriorityApplied;
    public uint ThreadPriorityApplied;
    public uint MmcssApplied;
    public uint ProcessPriorityBoostDisabled;
    public uint ThreadPriorityBoostDisabled;
    public uint WorkerPriorityAppliedCount;
    public uint WorkerMmcssAppliedCount;
    public uint WorkerPriorityBoostDisabledCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeOutlierEvent
{
    public uint BatchIndex;
    public uint Cpu;
    public int Worker;
    public int LoopEnd;
    public long DueTick;
    public long ActualTick;
    public long LateUs;
}

internal static partial class NativePlaybackInterop
{
    private const string LibraryName = "MacroHid.NativePlayback.dll";

    [DllImport(LibraryName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern MhpStatus MhpWarmEngine(
        ref MhpRunOptions options,
        out MhpRunStats stats);

    [DllImport(LibraryName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MhpShutdownEngine();

    [DllImport(LibraryName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern MhpStatus MhpCreatePlan(
        [In] NativeInput[] inputs,
        uint inputCount,
        [In] MhpBatch[] batches,
        uint batchCount,
        out IntPtr plan);

    [DllImport(LibraryName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern MhpStatus MhpRunPlan(
        IntPtr plan,
        ref MhpRunOptions options,
        ref int cancelFlag,
        out MhpRunStats stats);

    [DllImport(LibraryName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MhpCancel(ref int cancelFlag);

    [DllImport(LibraryName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    public static extern void MhpDestroyPlan(IntPtr plan);

    [DllImport(LibraryName, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr MhpGetLastErrorText();

    public static string GetLastErrorText()
    {
        try
        {
            var pointer = MhpGetLastErrorText();
            return pointer == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUni(pointer) ?? string.Empty;
        }
        catch (DllNotFoundException)
        {
            return "MacroHid.NativePlayback.dll was not found.";
        }
        catch (EntryPointNotFoundException ex)
        {
            return ex.Message;
        }
    }
}
