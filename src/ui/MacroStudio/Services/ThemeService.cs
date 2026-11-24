using System.IO;
using System.Text.Json;
using System.Windows;
using MacroHid.Core;

namespace MacroStudio.Services;

public enum AppTheme
{
    Light,
    Dark
}

public static class ThemeService
{
    private static readonly Uri LightThemeUri = new("Themes/LightTheme.xaml", UriKind.Relative);
    private static readonly Uri DarkThemeUri = new("Themes/DarkTheme.xaml", UriKind.Relative);

    private static ResourceDictionary? currentThemeDictionary;

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public static event Action? ThemeChanged;

    public static void Initialize()
    {
        var saved = LoadSavedTheme();
        ApplyTheme(saved);
    }

    public static void Toggle()
    {
        var next = CurrentTheme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light;
        ApplyTheme(next);
        SaveTheme(next);
    }

    public static void ApplyTheme(AppTheme theme)
    {
        var uri = theme == AppTheme.Dark ? DarkThemeUri : LightThemeUri;
        var newDictionary = new ResourceDictionary { Source = uri };

        var mergedDictionaries = Application.Current.Resources.MergedDictionaries;
        var insertIndex = RemoveExistingThemeDictionaries(mergedDictionaries);
        mergedDictionaries.Insert(Math.Min(insertIndex, mergedDictionaries.Count), newDictionary);
        currentThemeDictionary = newDictionary;
        CurrentTheme = theme;
        ThemeChanged?.Invoke();
    }

    private static int RemoveExistingThemeDictionaries(IList<ResourceDictionary> mergedDictionaries)
    {
        var insertIndex = 0;
        for (var index = mergedDictionaries.Count - 1; index >= 0; index--)
        {
            if (!IsThemeDictionary(mergedDictionaries[index]))
            {
                continue;
            }

            insertIndex = index;
            mergedDictionaries.RemoveAt(index);
        }

        return insertIndex;
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        return dictionary.Source is { } source
            && (UriEquals(source, LightThemeUri) || UriEquals(source, DarkThemeUri));
    }

    private static bool UriEquals(Uri left, Uri right)
    {
        return string.Equals(left.OriginalString, right.OriginalString, StringComparison.OrdinalIgnoreCase);
    }

    private static string SettingsPath => Path.Combine(ApplicationStorage.RoamingDirectory, "settings.json");

    private static AppTheme LoadSavedTheme()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (document.RootElement.TryGetProperty("theme", out var theme) &&
                    string.Equals(theme.GetString(), "light", StringComparison.OrdinalIgnoreCase))
                {
                    return AppTheme.Light;
                }
            }
        }
        catch
        {
            // An unreadable profile uses the default studio theme.
        }

        return AppTheme.Dark;
    }

    private static void SaveTheme(AppTheme theme)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);

            Dictionary<string, string> settings;
            if (File.Exists(SettingsPath))
            {
                var existing = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<Dictionary<string, string>>(existing) ?? [];
            }
            else
            {
                settings = [];
            }

            settings["theme"] = theme == AppTheme.Dark ? "dark" : "light";
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort persistence.
        }
    }
}
