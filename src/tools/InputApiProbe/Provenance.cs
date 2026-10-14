using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace InputApiProbe;

internal static class Provenance
{
    public static void Write(string path, string[] args)
    {
        string code = typeof(Provenance).Assembly.Location;
        var data = new Dictionary<string, object?>
        {
            ["date_utc"] = DateTime.UtcNow.ToString("o"),
            ["args"] = args,
            ["os"] = RuntimeInformation.OSDescription,
            ["cpu"] = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            ["logical_processors"] = Environment.ProcessorCount,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["timer_resolution_request_ms"] = 1,
            ["power_scheme"] = Run("powercfg", "/getactivescheme"),
            ["assembly"] = Path.GetFileName(code),
            ["assembly_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(code))),
            ["note"] = "Latency of synthetic input only; see docs/superpowers/specs/2026-10-15-input-api-comparison-design.md for limits."
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            string text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);
            return text;
        }
        catch (Exception ex) { return "unavailable: " + ex.Message; }
    }
}
