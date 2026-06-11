using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MacroStudio;

public sealed record SupportedLanguage(string CultureName, string DisplayNameResourceKey);

public static class LocalizationService
{
    private const string DefaultCultureName = "en-US";
    private const string SettingsFolderName = "MacroHID";
    private const string SettingsFileName = "MacroStudio.settings.json";
    private static readonly CultureInfo DefaultCulture = CultureInfo.GetCultureInfo(DefaultCultureName);
    private static readonly ResourceManager Resources = new("MacroStudio.Resources.Strings", typeof(LocalizationService).Assembly);

    public static IReadOnlyList<SupportedLanguage> SupportedLanguages { get; } =
    [
        new("en-US", "English"),
        new("zh-CN", "SimplifiedChinese"),
        new("zh-TW", "TraditionalChinese")
    ];

    public static CultureInfo CurrentCulture { get; private set; } = DefaultCulture;

    public static void Initialize()
    {
        var preferredCulture = LoadPreferredCultureName();
        var cultureName = preferredCulture ?? NormalizeCultureName(CultureInfo.CurrentUICulture);
        SetCurrentCulture(cultureName, persist: false);
    }

    public static void SetLanguage(string cultureName)
    {
        SetCurrentCulture(cultureName, persist: true);
    }

    public static string NormalizeCultureName(CultureInfo culture)
    {
        foreach (var name in EnumerateCultureNames(culture))
        {
            if (name.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
                || name.Equals("zh-HK", StringComparison.OrdinalIgnoreCase)
                || name.Equals("zh-MO", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase))
            {
                return "zh-TW";
            }

            if (name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                || name.Equals("zh-SG", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase))
            {
                return "zh-CN";
            }
        }

        return culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : DefaultCultureName;
    }

    public static string NormalizeCultureName(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return DefaultCultureName;
        }

        try
        {
            return NormalizeCultureName(CultureInfo.GetCultureInfo(cultureName));
        }
        catch (CultureNotFoundException)
        {
            return DefaultCultureName;
        }
    }

    public static string Get(string key)
    {
        return Get(key, CurrentCulture);
    }

    public static string Get(string key, CultureInfo culture)
    {
        return Resources.GetString(key, culture)
            ?? Resources.GetString(key, DefaultCulture)
            ?? key;
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(CurrentCulture, Get(key), args);
    }

    private static void SetCurrentCulture(string cultureName, bool persist)
    {
        var normalized = NormalizeCultureName(cultureName);
        CurrentCulture = CultureInfo.GetCultureInfo(normalized);
        CultureInfo.DefaultThreadCurrentUICulture = CurrentCulture;
        Thread.CurrentThread.CurrentUICulture = CurrentCulture;

        if (persist)
        {
            SavePreferredCultureName(normalized);
        }
    }

    private static IEnumerable<string> EnumerateCultureNames(CultureInfo culture)
    {
        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            yield return current.Name;
        }
    }

    private static string? LoadPreferredCultureName()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path))
            {
                return null;
            }

            var settings = JsonSerializer.Deserialize<MacroStudioSettings>(File.ReadAllText(path));
            return settings?.Language;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void SavePreferredCultureName(string cultureName)
    {
        try
        {
            var path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var settings = new MacroStudioSettings(cultureName);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            }));
        }
        catch (Exception)
        {
            // Settings persistence must never prevent the UI from switching language.
        }
    }

    private static string SettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, SettingsFolderName, SettingsFileName);
    }

    private sealed record MacroStudioSettings(string? Language);
}
