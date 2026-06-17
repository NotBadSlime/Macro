using System.IO;
using System.Text.Json;
using System.Windows;

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

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Light;

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

        if (currentThemeDictionary is not null)
        {
            mergedDictionaries.Remove(currentThemeDictionary);
        }

        mergedDictionaries.Insert(0, newDictionary);
        currentThemeDictionary = newDictionary;
        CurrentTheme = theme;
        ThemeChanged?.Invoke();
    }

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MacroHID",
            "settings.json");

    private static AppTheme LoadSavedTheme()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return AppTheme.Light;
            }

            var json = File.ReadAllText(SettingsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("theme", out var prop)
                && string.Equals(prop.GetString(), "dark", StringComparison.OrdinalIgnoreCase))
            {
                return AppTheme.Dark;
            }
        }
        catch
        {
            // Fall back to light theme on any error.
        }

        return AppTheme.Light;
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
