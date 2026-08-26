using System.IO.Compression;
using MacroHid.Core;

namespace MacroHid.Converter;

public static class MacroLibraryExportWriter
{
    public static void WriteTwoFolders(
        MacroLibraryExportBundle bundle,
        string parentDirectory,
        string exportRootName,
        string primaryFolderName,
        string dependenciesFolderName,
        MacroConversionFormat format)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (string.IsNullOrWhiteSpace(parentDirectory))
            throw new ArgumentException("Parent directory is required.", nameof(parentDirectory));
        if (string.IsNullOrWhiteSpace(exportRootName))
            throw new ArgumentException("Export root name is required.", nameof(exportRootName));
        if (string.IsNullOrWhiteSpace(primaryFolderName))
            throw new ArgumentException("Primary folder name is required.", nameof(primaryFolderName));

        var exportRoot = Path.Combine(parentDirectory, exportRootName);
        var primaryDirectory = Path.Combine(exportRoot, primaryFolderName);
        WriteEntries(bundle.Primary, primaryDirectory, format);

        if (bundle.Dependencies.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(dependenciesFolderName))
                throw new ArgumentException("Dependencies folder name is required when dependencies exist.", nameof(dependenciesFolderName));

            var dependenciesDirectory = Path.Combine(exportRoot, dependenciesFolderName);
            WriteEntries(bundle.Dependencies, dependenciesDirectory, format);
        }
    }

    public static void WriteZip(
        MacroLibraryExportBundle bundle,
        string zipPath,
        string exportRootName,
        string primaryFolderName,
        string dependenciesFolderName,
        MacroConversionFormat format)
    {
        if (string.IsNullOrWhiteSpace(zipPath))
            throw new ArgumentException("ZIP path is required.", nameof(zipPath));

        var tempRoot = Path.Combine(Path.GetTempPath(), "MacroHID-export", Guid.NewGuid().ToString("N"));
        try
        {
            WriteTwoFolders(bundle, tempRoot, exportRootName, primaryFolderName, dependenciesFolderName, format);
            var zipParent = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrWhiteSpace(zipParent))
                Directory.CreateDirectory(zipParent);
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            ZipFile.CreateFromDirectory(Path.Combine(tempRoot, exportRootName), zipPath);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void WriteEntries(
        IReadOnlyList<MacroLibraryExportEntry> entries,
        string directory,
        MacroConversionFormat format)
    {
        Directory.CreateDirectory(directory);
        foreach (var entry in entries)
        {
            var export = MacroConversionService.ExportFromMcrx(entry.Document, format, entry.RelativePath);
            var relativePath = ResolveRelativePath(entry.RelativePath, export.FileName);
            var path = Path.Combine(directory, relativePath);
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(path, export.Output);
        }
    }

    private static string ResolveRelativePath(string? relativePath, string exportFileName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return exportFileName;

        var directory = Path.GetDirectoryName(relativePath);
        return string.IsNullOrWhiteSpace(directory)
            ? exportFileName
            : Path.Combine(directory, exportFileName);
    }
}
