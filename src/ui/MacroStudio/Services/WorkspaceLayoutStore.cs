using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MacroStudio.Services;

public enum WorkspaceDockRegion
{
    Left,
    Center,
    Right,
    Bottom,
    Floating
}

public sealed record WorkspaceFloatingBounds(
    double Left = 120,
    double Top = 120,
    double Width = 520,
    double Height = 460);

public sealed record WorkspacePanelLayout(
    bool IsVisible = true,
    WorkspaceDockRegion DockRegion = WorkspaceDockRegion.Right,
    double DockedSize = 320,
    int Order = 0,
    bool IsPinned = true,
    WorkspaceFloatingBounds? FloatingBounds = null)
{
    public bool IsFloating => DockRegion == WorkspaceDockRegion.Floating;

    public static WorkspacePanelLayout FromLegacy(LegacyWorkspacePanelLayout legacy)
    {
        var region = legacy.IsFloating ? WorkspaceDockRegion.Floating : WorkspaceDockRegion.Right;
        return new WorkspacePanelLayout(
            legacy.IsVisible,
            region,
            legacy.Width,
            0,
            true,
            new WorkspaceFloatingBounds(legacy.Left, legacy.Top, legacy.Width, legacy.Height));
    }
}

public sealed record WorkspaceDockSizes(
    double LeftWidth = 260,
    double RightWidth = 390,
    double BottomHeight = 230);

public sealed record WorkspaceLayout(
    Dictionary<string, WorkspacePanelLayout> Panels,
    int Version = WorkspaceLayoutStore.CurrentVersion,
    WorkspaceDockSizes? DockSizes = null);

public sealed record LegacyWorkspacePanelLayout(
    bool IsVisible = true,
    bool IsFloating = false,
    double Left = 120,
    double Top = 120,
    double Width = 520,
    double Height = 460);

public static class WorkspaceLayoutStore
{
    public const int CurrentVersion = 4;
    private const string LayoutFileName = "MacroStudioWorkspaceLayout.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static WorkspaceLayout Load()
    {
        try
        {
            var path = GetLayoutPath();
            if (!File.Exists(path))
            {
                return Empty();
            }

            return DeserializeLayout(File.ReadAllText(path));
        }
        catch
        {
            return Empty();
        }
    }

    public static void Save(WorkspaceLayout layout)
    {
        var path = GetLayoutPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(layout, Options));
    }

    public static void Reset()
    {
        var path = GetLayoutPath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static WorkspaceLayout Empty() => new([], CurrentVersion);

    private static WorkspaceLayout DeserializeLayout(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Panels", out var panelsElement)
            || panelsElement.ValueKind != JsonValueKind.Object)
        {
            return Empty();
        }

        var panels = new Dictionary<string, WorkspacePanelLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var panel in panelsElement.EnumerateObject())
        {
            panels[panel.Name] = DeserializePanelLayout(panel.Value);
        }

        var version = 1;
        if (document.RootElement.TryGetProperty(nameof(WorkspaceLayout.Version), out var versionElement)
            && versionElement.TryGetInt32(out var parsedVersion))
        {
            version = parsedVersion;
        }

        var dockSizes = new WorkspaceDockSizes();
        if (document.RootElement.TryGetProperty(nameof(WorkspaceLayout.DockSizes), out var dockSizesElement)
            && dockSizesElement.ValueKind == JsonValueKind.Object)
        {
            dockSizes = JsonSerializer.Deserialize<WorkspaceDockSizes>(dockSizesElement.GetRawText(), Options)
                ?? new WorkspaceDockSizes();
        }

        return new WorkspaceLayout(panels, version, dockSizes);
    }

    private static WorkspacePanelLayout DeserializePanelLayout(JsonElement panel)
    {
        if (panel.TryGetProperty(nameof(WorkspacePanelLayout.DockRegion), out _)
            || panel.TryGetProperty(nameof(WorkspacePanelLayout.FloatingBounds), out _))
        {
            return JsonSerializer.Deserialize<WorkspacePanelLayout>(panel.GetRawText(), Options)
                ?? new WorkspacePanelLayout();
        }

        var legacy = JsonSerializer.Deserialize<LegacyWorkspacePanelLayout>(panel.GetRawText(), Options)
            ?? new LegacyWorkspacePanelLayout();
        return WorkspacePanelLayout.FromLegacy(legacy);
    }

    private static string GetLayoutPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MacroHID");
        return Path.Combine(root, LayoutFileName);
    }
}
