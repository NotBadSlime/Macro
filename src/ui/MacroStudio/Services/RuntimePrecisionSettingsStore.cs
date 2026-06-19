using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MacroHid.Core;

namespace MacroStudio.Services;

public sealed record RuntimePrecisionSettings(
    PrecisionMode Precision = PrecisionMode.ExtremeDuringPlayback,
    string AffinityMask = "")
{
    public static RuntimePrecisionSettings Default { get; } = new();
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
        return settings with { AffinityMask = affinityMask };
    }

    private static string GetSettingsPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MacroHID");
        return Path.Combine(root, SettingsFileName);
    }
}
