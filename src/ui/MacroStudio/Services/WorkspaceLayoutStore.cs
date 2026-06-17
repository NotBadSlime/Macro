using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MacroStudio.Services;

public sealed record WorkspacePanelLayout(
    bool IsVisible = true,
    bool IsFloating = false,
    double Left = 120,
    double Top = 120,
    double Width = 520,
    double Height = 460);

public sealed record WorkspaceLayout(
    Dictionary<string, WorkspacePanelLayout> Panels);

public static class WorkspaceLayoutStore
{
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

            return JsonSerializer.Deserialize<WorkspaceLayout>(File.ReadAllText(path), Options) ?? Empty();
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

    private static WorkspaceLayout Empty() => new([]);

    private static string GetLayoutPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MacroHID");
        return Path.Combine(root, LayoutFileName);
    }
}
