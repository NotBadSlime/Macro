using System.Globalization;
using System.Text.Json;

namespace MacroHid.Control;

public sealed record ControlRequest(string Command, string? Id = null, string? Library = null,
    string? Name = null, string? RunId = null, double DelayMilliseconds = 0, string? RequestId = null,
    int? Count = null, bool Loop = false);
public sealed record ControlMacro(string Id, string Name, string Library, string Folder);
public sealed record ControlJob(string RunId, string MacroId, string Name, string State,
    DateTimeOffset DueAt, double RemainingMilliseconds, string? Error = null);
public sealed record ControlResponse(bool Ok, string Code, string? Message = null,
    ControlJob? Job = null, ControlJob[]? Jobs = null, ControlMacro[]? Macros = null,
    bool Busy = false, string[]? Capabilities = null)
{
    public static ControlResponse Fail(string code, string message) => new(false, code, message);
}
public static class ControlProtocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxMessageBytes = 1024 * 1024;
    public const string PlaybackRepeatCapability = "playback-repeat-v1";
    public static bool IsTerminal(string state) => state is "completed" or "cancelled" or "failed" or "busy";
    public static void ValidatePlayback(int? count, bool loop)
    {
        if (count is <= 0) throw new ArgumentException("--count must be a positive integer.");
        if (loop && count is not null) throw new ArgumentException("Use either --count N or --loop, not both.");
    }
    public static double ParseDelay(string value)
    {
        double factor = 1000;
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) { factor = 1; value = value[..^2]; }
        else if (value.EndsWith('s')) value = value[..^1];
        else if (value.EndsWith('m')) { factor = 60000; value = value[..^1]; }
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number) || number < 0 || number * factor > TimeSpan.FromDays(7).TotalMilliseconds)
            throw new ArgumentException("Delay must be between 0 and 7 days, for example 500ms, 3s or 1m.");
        return number * factor;
    }
}
