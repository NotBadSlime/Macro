using System.Diagnostics;
using System.IO;

namespace MacroStudio;

public sealed record MacroConverterStatus(bool Available, string? ExecutablePath, string Detail);

public static class MacroConverterIntegration
{
    private const string ConverterExecutableName = "MacroConverter.exe";

    public static MacroConverterStatus Probe(string? startDirectory = null)
    {
        var executable = FindConverterExecutable(startDirectory ?? AppContext.BaseDirectory);
        return executable is null
            ? new MacroConverterStatus(false, null, "MacroConverter executable not found")
            : new MacroConverterStatus(true, executable, executable);
    }

    public static string? FindConverterExecutable(string startDirectory)
    {
        var start = new DirectoryInfo(Path.GetFullPath(startDirectory));
        if (!start.Exists)
        {
            return null;
        }

        foreach (var directory in EnumerateSelfAndParents(start))
        {
            var candidates = new[]
            {
                Path.Combine(directory.FullName, "MacroConverter", ConverterExecutableName),
                Path.Combine(directory.FullName, "MacroConverter", "dist", "MacroConverter-win32-x64", ConverterExecutableName),
                Path.Combine(directory.FullName, "..", "MacroConverter", ConverterExecutableName),
                Path.Combine(directory.FullName, "..", "MacroConverter", "dist", "MacroConverter-win32-x64", ConverterExecutableName)
            };

            foreach (var candidate in candidates)
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    public static void Launch(string executablePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = true
        });
    }

    private static IEnumerable<DirectoryInfo> EnumerateSelfAndParents(DirectoryInfo start)
    {
        for (var current = start; current is not null; current = current.Parent)
        {
            yield return current;
        }
    }
}
