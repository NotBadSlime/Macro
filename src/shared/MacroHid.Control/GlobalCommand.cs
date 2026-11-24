using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;
using MacroHid.Core;

namespace MacroHid.Control;

public sealed record CommandRegistration(string StudioPath);
public static class GlobalCommand
{
    public const string CommandName = "MacroCtlUI";
    private const string ExecutableName = CommandName + ".exe";

    public static string BinDirectory => Path.Combine(ApplicationStorage.LocalDirectory, "bin");
    private static string RegistrationPath => Path.Combine(BinDirectory, "registration.json");
    public static string FindCommandDirectory(string studioDirectory)
    {
        var sibling = Path.GetFullPath(Path.Combine(studioDirectory, "..", "MacroCtl"));
        if (File.Exists(Path.Combine(sibling, ExecutableName))) return sibling;
        for (var directory = new DirectoryInfo(studioDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "tools", "MacroCtl", "bin", "Release", "net8.0-windows");
            if (File.Exists(Path.Combine(candidate, ExecutableName))) return candidate;
        }
        throw new FileNotFoundException($"{ExecutableName} is missing. Use a complete package.");
    }
    public static CommandRegistration? Read() => File.Exists(RegistrationPath)
        ? JsonSerializer.Deserialize<CommandRegistration>(File.ReadAllText(RegistrationPath), ControlProtocol.Json) : null;
    public static string Describe()
    {
        var registration = Read();
        var executablePath = Path.Combine(BinDirectory, ExecutableName);
        return $"Command: {executablePath}\nLauncher exists: {File.Exists(executablePath)}\nStudio: {registration?.StudioPath ?? "(not registered)"}\nTarget exists: {File.Exists(registration?.StudioPath)}\nUser PATH registered: {HasPath()}\nReopen terminals after changing registration.";
    }
    public static void Register(string studioPath, string commandDirectory)
    {
        studioPath = Path.GetFullPath(studioPath);
        if (!File.Exists(studioPath) || !string.Equals(Path.GetFileName(studioPath), "MacroStudio.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Studio path must point to an existing MacroStudio.exe.");
        if (!File.Exists(Path.Combine(commandDirectory, ExecutableName))) throw new IOException($"{ExecutableName} is missing. Use a complete build/package.");
        Directory.CreateDirectory(BinDirectory);
        if (!string.Equals(Path.GetFullPath(commandDirectory).TrimEnd('\\'), Path.GetFullPath(BinDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            foreach (var file in Directory.GetFiles(commandDirectory))
                if (Path.GetExtension(file) is ".exe" or ".dll" or ".json" && Path.GetFileName(file) != "registration.json")
                    File.Copy(file, Path.Combine(BinDirectory, Path.GetFileName(file)), true);
        }
        var temporary = RegistrationPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new CommandRegistration(studioPath), ControlProtocol.Json));
        File.Move(temporary, RegistrationPath, true);
        using var environment = Registry.CurrentUser.CreateSubKey("Environment");
        var path = environment.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
        if (!ContainsPath(path)) environment.SetValue("Path", string.IsNullOrEmpty(path) ? BinDirectory : path.TrimEnd(';') + ";" + BinDirectory, environment.GetValueKindOrDefault("Path"));
        Broadcast();
    }
    public static bool Unregister(string studioPath)
    {
        // Uninstalling an old version must not remove another version's registration.
        if (!string.Equals(Read()?.StudioPath, Path.GetFullPath(studioPath), StringComparison.OrdinalIgnoreCase)) return false;
        using var environment = Registry.CurrentUser.CreateSubKey("Environment");
        var path = environment.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
        var retained = path.Split(';').Where(entry => !SamePath(entry, BinDirectory));
        environment.SetValue("Path", string.Join(';', retained), environment.GetValueKindOrDefault("Path"));
        File.Delete(RegistrationPath);
        // Keep launcher files so a running MacroCtlUI process is never deleted underneath itself.
        Broadcast(); return true;
    }
    public static bool HasPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment");
        return ContainsPath(key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "");
    }
    private static bool ContainsPath(string path) => path.Split(';').Any(entry => SamePath(entry, BinDirectory));
    private static bool SamePath(string left, string right) => string.Equals(Environment.ExpandEnvironmentVariables(left.Trim().Trim('"')).TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static RegistryValueKind GetValueKindOrDefault(this RegistryKey key, string name) => key.GetValue(name) is null ? RegistryValueKind.ExpandString : key.GetValueKind(name);
    private static void Broadcast() => SendMessageTimeout(new IntPtr(0xffff), 0x1a, IntPtr.Zero, "Environment", 2, 1000, out _);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr parameter, string text, uint flags, uint timeout, out IntPtr result);
}
