namespace MacroHid.Core;

public static class ApplicationStorage
{
    public const string FolderName = "MacroHID-UI";

    public static string RoamingDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    public static string LocalDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
}
