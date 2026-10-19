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
    bool CompensateSendInputCost = true,
    // How hotkeys are detected. RawInput keeps this process out of the synchronous keyboard hook chain.
    HotkeyInputMode HotkeyInput = HotkeyInputMode.LowLevelHook,
    // Keep native playback workers off cores the system latency check found interrupt-heavy (also checked at start-up).
    bool AvoidNoisyCores = true,
    // Which release plays macros (PlaybackVersions.All). Empty = derived from Generation (settings from before the switcher).
    string PlaybackVersion = "",
    // Max (2.1): 极限 plus preemption (time-critical workers, realtime process, no timer sleeps, hot standby). Only with
    // Precision = UltraLowJitter on the current engine; game frame rate may drop.
    bool MaxPreemption = false,
    // No-GC playback (2.1), 高级设置: which 极致/极限 macros play inside a .NET no-GC region and when a loop may collect.
    MacroHid.Runtime.PlaybackGcOptions? NoGcPlayback = null)
{
    /// <summary>No-GC playback rules in effect (defaults when the file has none).</summary>
    public MacroHid.Runtime.PlaybackGcOptions NoGc => (NoGcPlayback ?? MacroHid.Runtime.PlaybackGcOptions.Default).Normalize();
    /// <summary>Max is in effect: the setting, the 极限 profile and the current engine.</summary>
    public bool MaxActive => MaxPreemption && Precision == PrecisionMode.UltraLowJitter && Version.Folder is null;
    public const int MaxStartLeadMicroseconds = 5000;
    public static RuntimePrecisionSettings Default { get; } = new(ColorSampleHotkey: CoreHotkeys.DefaultColorSampleText);

    public HotkeyGesture ColorSampleGesture => CoreHotkeys.ParseOrDefault(ColorSampleHotkey);

    public HotkeyGesture? ForceStopGesture => TryParseHotkey(ForceStopHotkey);

    /// <summary>The selected release; the current engine when nothing (or an unknown id) is stored.</summary>
    public PlaybackVersionInfo Version => PlaybackVersions.Resolve(PlaybackVersion);

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
            // The switcher replaced precision generations: an old file's generation picks the release, the current engine
            // then runs its own (latest) timing.
            Generation = PrecisionGeneration.Latest,
            PlaybackVersion = string.IsNullOrWhiteSpace(settings.PlaybackVersion)
                ? PlaybackVersions.FromGeneration(settings.Generation)
                : PlaybackVersions.Resolve(settings.PlaybackVersion).Id,
            StartLeadMicroseconds = Math.Clamp(settings.StartLeadMicroseconds, 0, RuntimePrecisionSettings.MaxStartLeadMicroseconds),
            NoGcPlayback = settings.NoGc
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
