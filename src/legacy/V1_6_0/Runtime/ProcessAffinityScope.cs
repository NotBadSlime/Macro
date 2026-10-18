using System.Diagnostics;
using MacroHid.Core;

namespace MacroHid.Runtime;

internal sealed class ProcessAffinityScope : IDisposable
{
    private readonly Process process;
    private readonly IntPtr previousAffinity;
    private bool disposed;

    private ProcessAffinityScope(Process process, IntPtr previousAffinity)
    {
        this.process = process;
        this.previousAffinity = previousAffinity;
    }

    public static ProcessAffinityScope? TryEnter(string? affinityMask)
    {
        if (!OperatingSystem.IsWindows()
            || !PlaybackAffinityMask.TryParse(affinityMask, out var mask)
            || mask > long.MaxValue)
        {
            return null;
        }

        try
        {
            var process = Process.GetCurrentProcess();
            var previousAffinity = process.ProcessorAffinity;
            var requestedAffinity = new IntPtr(unchecked((long)mask));
            if (previousAffinity == requestedAffinity)
            {
                return new ProcessAffinityScope(process, previousAffinity);
            }

            process.ProcessorAffinity = requestedAffinity;
            return new ProcessAffinityScope(process, previousAffinity);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (!OperatingSystem.IsWindows())
        {
            process.Dispose();
            return;
        }

        try
        {
            process.ProcessorAffinity = previousAffinity;
        }
        catch
        {
            // Affinity restore is best effort; the process may already be shutting down.
        }
        finally
        {
            process.Dispose();
        }
    }
}
