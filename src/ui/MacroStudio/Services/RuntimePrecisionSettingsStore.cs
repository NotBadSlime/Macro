using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MacroHid.Core;

namespace MacroStudio.Services;

public enum CloseButtonBehavior
{
    Ask,
    MinimizeToTray,
    Exit
}

public sealed record RuntimePrecisionSettings(
    PrecisionMode Precision = PrecisionMode.ExtremeDuringPlayback,
    string AffinityMask = "",
    string ColorSampleHotkey = "",
    bool BackgroundWindow = false,
    string BackgroundProcess = "",
    long ZeroGapHoldMicroseconds = InputPressTiming.DefaultZeroGapMicroseconds,
    long ModifierChordHoldMicroseconds = InputPressTiming.DefaultModifierChordMicroseconds,
    string ForceStopHotkey = "",
    CloseButtonBehavior CloseButton = CloseButtonBehavior.Ask)
{
    public static RuntimePrecisionSettings Default { get; } = new(ColorSampleHotkey: CoreHotkeys.DefaultColorSampleText);

    public HotkeyGesture ColorSampleGesture => CoreHotkeys.ParseOrDefault(ColorSampleHotkey);

    public HotkeyGesture? ForceStopGesture => TryParseHotkey(ForceStopHotkey);

    public InputPressFloor PressFloor => InputPressFloor.FromMicroseconds(ZeroGapHoldMicroseconds, ModifierChordHoldMicroseconds);

    private static HotkeyGesture? TryParseHotkey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return McrxParser.ParseHotkeyGesture(text);
        }
        catch
        {
            return null;
        }
    }
}

public static class RuntimePrecisionSettingsStore
{
    private const string SettingsFileName = "MacroStudioRuntimeSettings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    static RuntimePrecisionSettingsStore()
    {
        Options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public static RuntimePrecisionSettings Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return RuntimePrecisionSettings.Default;
            }

            var settings = JsonSerializer.Deserialize<RuntimePrecisionSettings>(File.ReadAllText(path), Options)
                ?? RuntimePrecisionSettings.Default;
            return Normalize(settings);
        }
        catch
        {
            return RuntimePrecisionSettings.Default;
        }
    }

    public static void Save(RuntimePrecisionSettings settings)
    {
        var path = GetSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Normalize(settings), Options));
    }

    private static RuntimePrecisionSettings Normalize(RuntimePrecisionSettings settings)
    {
        var affinityMask = PlaybackAffinityMask.NormalizeOrThrow(settings.AffinityMask);
        return settings with
        {
            AffinityMask = affinityMask,
            ColorSampleHotkey = CoreHotkeys.NormalizeText(settings.ColorSampleHotkey),
            BackgroundProcess = NormalizeProcessName(settings.BackgroundProcess),
            ZeroGapHoldMicroseconds = ClampMicroseconds(settings.ZeroGapHoldMicroseconds),
            ModifierChordHoldMicroseconds = ClampMicroseconds(settings.ModifierChordHoldMicroseconds),
            ForceStopHotkey = NormalizeForceStop(settings.ForceStopHotkey)
        };
    }

    private static long ClampMicroseconds(long value)
    {
        if (value < 0)
        {
            return 0;
        }

        return value > InputPressTiming.MaxHoldMicroseconds ? InputPressTiming.MaxHoldMicroseconds : value;
    }

    private static string NormalizeForceStop(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            return McrxParser.ParseHotkeyGesture(text).ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeProcessName(string? processName)
    {
        var name = Path.GetFileName(processName?.Trim() ?? string.Empty);
        if (name.Length == 0)
        {
            return string.Empty;
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    private static string GetSettingsPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MacroHID");
        return Path.Combine(root, SettingsFileName);
    }
}
