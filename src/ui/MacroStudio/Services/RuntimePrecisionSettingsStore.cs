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

/// <summary>
/// Reads a precision generation by name and falls back to the latest for a name this build does not know (for example one
/// saved by a newer build). Without it the unknown name would fail the whole settings file and the user would lose every
/// other setting along with it.
/// </summary>
public sealed class LenientPrecisionGenerationConverter : JsonConverter<PrecisionGeneration>
{
    public override PrecisionGeneration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse<PrecisionGeneration>(reader.GetString(), ignoreCase: true, out var named)
            && Enum.IsDefined(named))
        {
            return named;
        }

        if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Skip();
        }

        return PrecisionGeneration.Latest;
    }

    public override void Write(Utf8JsonWriter writer, PrecisionGeneration value, JsonSerializerOptions options)
    {
        // Camel case by name, the same shape the other enums in this file use.
        var name = PrecisionGenerations.Normalize(value).ToString();
        writer.WriteStringValue(char.ToLowerInvariant(name[0]) + name[1..]);
    }
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
    CloseButtonBehavior CloseButton = CloseButtonBehavior.Ask,
    [property: JsonConverter(typeof(LenientPrecisionGenerationConverter))]
    PrecisionGeneration Generation = PrecisionGeneration.Latest,
    // Hotkey runs start exactly this long after the key (0 = as soon as ready). See PlaybackExecutionOptions.StartLeadMicroseconds.
    int StartLeadMicroseconds = 0,
    // Submit early by the learned SendInput cost. Only the latest generation uses it; older generations keep their timing.
    bool CompensateSendInputCost = true)
{
    public const int MaxStartLeadMicroseconds = 5000;
    public static RuntimePrecisionSettings Default { get; } = new(ColorSampleHotkey: CoreHotkeys.DefaultColorSampleText);

    public HotkeyGesture ColorSampleGesture => CoreHotkeys.ParseOrDefault(ColorSampleHotkey);

    public HotkeyGesture? ForceStopGesture => TryParseHotkey(ForceStopHotkey);

    /// <summary>The floor the engine runs with, carrying the selected precision generation (latest unless the user picked another).</summary>
    public InputPressFloor PressFloor => InputPressFloor.FromMicroseconds(ZeroGapHoldMicroseconds, ModifierChordHoldMicroseconds)
        with { Generation = PrecisionGenerations.Normalize(Generation) };

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

    public static RuntimePrecisionSettings Load() => Load(GetSettingsPath());

    /// <summary>Loads from an explicit path. An older file without <c>generation</c>, or with an unknown value, yields the latest.</summary>
    public static RuntimePrecisionSettings Load(string path)
    {
        try
        {
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

    public static void Save(RuntimePrecisionSettings settings) => Save(settings, GetSettingsPath());

    public static void Save(RuntimePrecisionSettings settings, string path)
    {
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
            ForceStopHotkey = NormalizeForceStop(settings.ForceStopHotkey),
            Generation = PrecisionGenerations.Normalize(settings.Generation),
            StartLeadMicroseconds = Math.Clamp(settings.StartLeadMicroseconds, 0, RuntimePrecisionSettings.MaxStartLeadMicroseconds)
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
