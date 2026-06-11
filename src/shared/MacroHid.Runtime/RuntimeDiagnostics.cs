using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed record RuntimeDiagnosticItem(bool Available, string Detail);

public sealed record RuntimeDiagnosticsSnapshot(
    RuntimeDiagnosticItem PixelSampler,
    RuntimeDiagnosticItem Driver)
{
    public static RuntimeDiagnosticsSnapshot Collect()
    {
        return new RuntimeDiagnosticsSnapshot(
            PixelSampler: ProbePixelSampler(),
            Driver: ProbeDriver());
    }

    public static RuntimeDiagnosticsSnapshot FromDriverStats(MacroDriverStats? stats)
    {
        return new RuntimeDiagnosticsSnapshot(
            PixelSampler: new RuntimeDiagnosticItem(true, "visible desktop sampler ready"),
            Driver: CreateDriverItem(stats));
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

    private static RuntimeDiagnosticItem ProbeDriver()
    {
        try
        {
            using var sink = DriverMacroReportSink.OpenFirst();
            return CreateDriverItem(sink?.GetStats());
        }
        catch (Exception ex)
        {
            return new RuntimeDiagnosticItem(false, $"MacroHID driver error: {ex.Message}");
        }
    }

    private static RuntimeDiagnosticItem CreateDriverItem(MacroDriverStats? stats)
    {
        return stats is null
            ? new RuntimeDiagnosticItem(false, "MacroHID device not found")
            : new RuntimeDiagnosticItem(
                true,
                $"protocol={stats.ProtocolVersion} submitted={stats.ReportsSubmitted} rejected={stats.ReportsRejected} lastStatus=0x{stats.LastNtStatus:X8}");
    }
}
