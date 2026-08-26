using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Windows;
using System.Windows.Media;
using MacroHid.Core;

namespace MacroStudio.Services;

public enum ActionAppearanceRole
{
    Text,
    Icon,
    Background
}

public sealed record ActionAppearanceBrushes(Brush Text, Brush Icon, Brush Background);

public static class ActionAppearanceService
{
    private const string SettingsFileName = "ActionAppearanceSettings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static readonly Dictionary<string, SavedActionAppearance> CustomAppearances = Load();

    static ActionAppearanceService()
    {
        Options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        ThemeService.ThemeChanged += () => AppearanceChanged?.Invoke();
    }

    public static event Action? AppearanceChanged;

    public static IReadOnlyList<MacroActionTemplateKind> EditableKinds { get; } =
    [
        MacroActionTemplateKind.Delay,
        MacroActionTemplateKind.Keyboard,
        MacroActionTemplateKind.MouseButton,
        MacroActionTemplateKind.MouseMove,
        MacroActionTemplateKind.MouseWheel,
        MacroActionTemplateKind.WindowActivate,
        MacroActionTemplateKind.OcrExtractText,
        MacroActionTemplateKind.OcrClick,
        MacroActionTemplateKind.Text,
        MacroActionTemplateKind.Macro,
        MacroActionTemplateKind.Loop,
        MacroActionTemplateKind.Pixel,
        MacroActionTemplateKind.StopCurrent,
        MacroActionTemplateKind.StopIteration,
        MacroActionTemplateKind.StopAll
    ];

    public static ActionAppearanceBrushes GetBrushes(MacroActionTemplateKind kind)
    {
        return new ActionAppearanceBrushes(
            CreateBrush(GetResolvedColor(kind, ActionAppearanceRole.Text)),
            CreateBrush(GetResolvedColor(kind, ActionAppearanceRole.Icon)),
            CreateBrush(GetResolvedColor(kind, ActionAppearanceRole.Background)));
    }

    public static Color GetResolvedColor(MacroActionTemplateKind kind, ActionAppearanceRole role)
    {
        var key = kind.ToString();
        CustomAppearances.TryGetValue(key, out var saved);
        var custom = role switch
        {
            ActionAppearanceRole.Text => saved?.Text,
            ActionAppearanceRole.Icon => saved?.Icon,
            ActionAppearanceRole.Background => saved?.Background,
            _ => null
        };

        if (TryParseColor(custom, out var color))
        {
            return color;
        }

        return GetDefaultColor(kind, role);
    }

    public static string GetResolvedHex(MacroActionTemplateKind kind, ActionAppearanceRole role)
    {
        return ToHex(GetResolvedColor(kind, role));
    }

    public static void SetColor(MacroActionTemplateKind kind, ActionAppearanceRole role, Color color)
    {
        var key = kind.ToString();
        CustomAppearances.TryGetValue(key, out var current);
        current ??= new SavedActionAppearance();
        var hex = ToHex(color);
        CustomAppearances[key] = role switch
        {
            ActionAppearanceRole.Text => current with { Text = hex },
            ActionAppearanceRole.Icon => current with { Icon = hex },
            ActionAppearanceRole.Background => current with { Background = hex },
            _ => current
        };
        Save();
        AppearanceChanged?.Invoke();
    }

    public static void Reset(MacroActionTemplateKind kind)
    {
        if (!CustomAppearances.Remove(kind.ToString()))
        {
            return;
        }

        Save();
        AppearanceChanged?.Invoke();
    }

    private static Color GetDefaultColor(MacroActionTemplateKind kind, ActionAppearanceRole role)
    {
        if (role == ActionAppearanceRole.Text)
        {
            return FindResourceColor("PrimaryText", ThemeService.CurrentTheme == AppTheme.Dark ? "#F4F6FA" : "#252A31");
        }

        if (role == ActionAppearanceRole.Icon)
        {
            var resourceKey = kind switch
            {
                MacroActionTemplateKind.Delay => "SecondaryText",
                MacroActionTemplateKind.Keyboard or MacroActionTemplateKind.Macro or MacroActionTemplateKind.WindowActivate => "Accent",
                MacroActionTemplateKind.MouseButton or MacroActionTemplateKind.MouseMove or MacroActionTemplateKind.StopCurrent or MacroActionTemplateKind.StopIteration => "AccentOrange",
                MacroActionTemplateKind.MouseWheel or MacroActionTemplateKind.Text or MacroActionTemplateKind.OcrExtractText => "AccentBlue",
                MacroActionTemplateKind.Pixel or MacroActionTemplateKind.OcrClick => "AccentPink",
                MacroActionTemplateKind.Loop or MacroActionTemplateKind.StopAll => "Danger",
                _ => "SecondaryText"
            };
            return FindResourceColor(resourceKey, "#2F80ED");
        }

        var isDark = ThemeService.CurrentTheme == AppTheme.Dark;
        var hex = kind switch
        {
            MacroActionTemplateKind.Delay => isDark ? "#30353D" : "#E3E7EB",
            MacroActionTemplateKind.Keyboard => isDark ? "#293847" : "#DCEAF5",
            MacroActionTemplateKind.MouseButton or MacroActionTemplateKind.MouseMove => isDark ? "#3A342B" : "#F2E8D8",
            MacroActionTemplateKind.MouseWheel => isDark ? "#293847" : "#DCEAF5",
            MacroActionTemplateKind.WindowActivate => isDark ? "#293A32" : "#DDECE2",
            MacroActionTemplateKind.OcrExtractText => isDark ? "#293847" : "#DCEAF5",
            MacroActionTemplateKind.OcrClick => isDark ? "#3A2D39" : "#EDE0EA",
            MacroActionTemplateKind.Text => isDark ? "#343044" : "#E7E2F0",
            MacroActionTemplateKind.Macro => isDark ? "#293A32" : "#DDECE2",
            MacroActionTemplateKind.Loop => isDark ? "#3C2D31" : "#F0E0E3",
            MacroActionTemplateKind.Pixel => isDark ? "#3A2D39" : "#EDE0EA",
            MacroActionTemplateKind.StopCurrent => isDark ? "#3A332A" : "#F2E6D7",
            MacroActionTemplateKind.StopIteration => isDark ? "#3E302A" : "#F3E2D8",
            MacroActionTemplateKind.StopAll => isDark ? "#422D31" : "#F1DCDF",
            _ => isDark ? "#2B3038" : "#E7EAEE"
        };
        return ParseColor(hex);
    }

    private static Color FindResourceColor(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            return brush.Color;
        }

        return ParseColor(fallback);
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color ParseColor(string value)
    {
        return (Color)ColorConverter.ConvertFromString(value)!;
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                color = ParseColor(value);
                return true;
            }
        }
        catch
        {
        }

        color = default;
        return false;
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Dictionary<string, SavedActionAppearance> Load()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return new Dictionary<string, SavedActionAppearance>(StringComparer.OrdinalIgnoreCase);
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, SavedActionAppearance>>(File.ReadAllText(path), Options)
                ?? [];
            return new Dictionary<string, SavedActionAppearance>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, SavedActionAppearance>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save()
    {
        try
        {
            var path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(CustomAppearances, Options));
        }
        catch
        {
            // Appearance persistence is best effort; the live choice remains active.
        }
    }

    private static string GetSettingsPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MacroHID");
        return Path.Combine(root, SettingsFileName);
    }

    private sealed record SavedActionAppearance(string? Text = null, string? Icon = null, string? Background = null);
}
