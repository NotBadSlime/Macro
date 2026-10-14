using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Windows.Input;

namespace SkirkFps;
public sealed class FpsRule : INotifyPropertyChanged
{
    private int virtualKey;
    private uint modifiers;
    private string fps = "117";
    private bool enabled = true;
    public int VirtualKey { get => virtualKey; set { virtualKey = value; Notify(); Notify(nameof(HotkeyText)); } }
    public uint Modifiers { get => modifiers; set { modifiers = value; Notify(); Notify(nameof(HotkeyText)); } }
    public string Fps { get => fps; set { fps = value; Notify(); } }
    public bool Enabled { get => enabled; set { enabled = value; Notify(); } }
    [JsonIgnore] public string HotkeyText => virtualKey == 0 ? "点击录入快捷键" :
        ((modifiers & 2) != 0 ? "Ctrl + " : "") + ((modifiers & 1) != 0 ? "Alt + " : "") +
        ((modifiers & 4) != 0 ? "Shift + " : "") + ((modifiers & 8) != 0 ? "Win + " : "") + KeyInterop.KeyFromVirtualKey(virtualKey);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed class Settings
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    public static Settings ParseJson(string json) => JsonSerializer.Deserialize<Settings>(json, ReadOptions)
        ?? throw new InvalidOperationException("配置为空");
    public List<FpsRule> Rules { get; set; } = [new() { VirtualKey = 0x70, Fps = "117" }, new() { VirtualKey = 0x71, Fps = "217" }, new() { VirtualKey = 0x72, Fps = "384" }];
    public FpsRule Restore { get; set; } = new() { VirtualKey = 0x7B, Modifiers = 6 };
}
