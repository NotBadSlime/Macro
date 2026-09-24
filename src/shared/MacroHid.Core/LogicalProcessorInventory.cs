using System.Runtime.InteropServices;

namespace MacroHid.Core;

public readonly record struct LogicalProcessor(
    int ProcessorNumber,
    int PhysicalCoreId,
    int EfficiencyClass)
{
    public bool IsPerformanceCore => EfficiencyClass == 0;

    public string KindText => IsPerformanceCore ? "性能核" : "能效核";
}

public static class LogicalProcessorInventory
{
    public static IReadOnlyList<LogicalProcessor> Query()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            var queried = QueryWindowsCores();
            if (queried.Count > 0)
            {
                return queried;
            }
        }
        catch
        {
            // Fall back to one entry per logical processor when topology cannot be read.
        }

        return Enumerable.Range(0, Environment.ProcessorCount)
            .Select(index => new LogicalProcessor(index, index, 0))
            .ToArray();
    }

    public static IReadOnlyList<int> DefaultSelection(IReadOnlyList<LogicalProcessor> cores)
    {
        var performance = cores
            .Where(core => core.IsPerformanceCore && core.ProcessorNumber != 0)
            .Select(core => core.ProcessorNumber)
            .ToArray();
        if (performance.Length > 0)
        {
            return performance;
        }

        return cores
            .Where(core => core.ProcessorNumber != 0)
            .Select(core => core.ProcessorNumber)
            .ToArray();
    }

    public static string MaskFromProcessors(IEnumerable<int> processors)
    {
        ulong mask = 0;
        foreach (var processor in processors.Distinct())
        {
            if (processor is < 0 or > 63)
            {
                continue;
            }

            mask |= 1UL << processor;
        }

        if (mask == 0)
        {
            throw new InvalidOperationException("At least one CPU core must be selected.");
        }

        return "0x" + mask.ToString("X");
    }

    public static IReadOnlyList<int> DistinctPhysicalWorkers(IReadOnlyList<LogicalProcessor> ranked, int count)
    {
        var selected = new List<int>();
        var usedPhysical = new HashSet<int>();
        foreach (var core in ranked)
        {
            if (core.ProcessorNumber == 0 && ranked.Count > count)
            {
                continue;
            }

            if (!usedPhysical.Add(core.PhysicalCoreId))
            {
                continue;
            }

            selected.Add(core.ProcessorNumber);
            if (selected.Count == count)
            {
                break;
            }
        }

        return selected;
    }

    private static List<LogicalProcessor> QueryWindowsCores()
    {
        var length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length <= 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
            {
                return [];
            }

            var cores = new List<LogicalProcessor>();
            var offset = 0;
            var physicalCoreId = 0;
            while (offset + 8 < length)
            {
                var current = IntPtr.Add(buffer, offset);
                var relationship = Marshal.ReadInt32(current);
                var size = Marshal.ReadInt32(current, 4);
                if (size <= 0 || offset + size > length)
                {
                    break;
                }

                if (relationship == RelationProcessorCore)
                {
                    var processor = IntPtr.Add(current, 8);
                    var efficiencyClass = Marshal.ReadByte(processor, 1);
                    var groupCount = Marshal.ReadInt16(processor, 22);
                    var groupMask = IntPtr.Add(processor, 24);
                    for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
                    {
                        var group = IntPtr.Add(groupMask, groupIndex * 16);
                        var groupNumber = Marshal.ReadInt16(group, 8);
                        if (groupNumber != 0)
                        {
                            continue;
                        }

                        var affinity = unchecked((ulong)Marshal.ReadInt64(group));
                        for (var bit = 0; bit < 64; bit++)
                        {
                            if ((affinity & (1UL << bit)) == 0)
                            {
                                continue;
                            }

                            cores.Add(new LogicalProcessor(bit, physicalCoreId, efficiencyClass));
                        }
                    }

                    physicalCoreId++;
                }

                offset += size;
            }

            cores.Sort((left, right) => left.ProcessorNumber.CompareTo(right.ProcessorNumber));
            return cores;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationship,
        IntPtr buffer,
        ref int returnedLength);
}
