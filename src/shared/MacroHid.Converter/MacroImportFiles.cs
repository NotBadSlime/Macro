using System.IO.Compression;
using System.Text;

namespace MacroHid.Converter;

public static class MacroImportFiles
{
    private const int MaxEntries = 4096;
    private const int MaxArchiveBytes = 64 * 1024 * 1024;
    private const int MaxMacroBytes = 16 * 1024 * 1024;
    private const int MaxExpandedBytes = 64 * 1024 * 1024;

    public static bool IsSupportedMacro(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".mcrx" or ".xml" or ".lua" or ".xmbcs" or ".mq" or ".txt" or ".json";

    public static bool IsZip(string path) => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<AuxiliaryMacroFile> ReadZip(string path)
    {
        using var source = File.OpenRead(path);
        if (source.Length > MaxArchiveBytes)
            throw new InvalidDataException("The ZIP file exceeds 64 MiB.");

        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxEntries)
            throw new InvalidDataException($"The ZIP file contains more than {MaxEntries} entries.");

        var files = new List<AuxiliaryMacroFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            string relativePath = entry.FullName.Replace('\\', '/');
            int unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (relativePath.EndsWith('/') || (entry.ExternalAttributes & 0x10) != 0 || unixFileType == 0x4000
                || !IsSupportedMacro(relativePath))
                continue;

            if (unixFileType == 0xA000 || !IsValidRelativePath(relativePath))
                throw new InvalidDataException("The ZIP file contains an unsafe macro entry path.");
            if (!paths.Add(relativePath))
                throw new InvalidDataException($"The ZIP file contains a duplicate macro entry: {relativePath}");
            if (entry.Length > MaxMacroBytes || entry.Length > MaxExpandedBytes - totalBytes)
                throw new InvalidDataException("The ZIP exceeds the import limits of 16 MiB per macro and 64 MiB combined.");

            // Read the recorded length, then check for an overrun before decoding. This bounds
            // decompression even if the size in the archive directory is incorrect.
            var bytes = new byte[(int)entry.Length];
            using var data = entry.Open();
            data.ReadExactly(bytes);
            if (data.ReadByte() != -1)
                throw new InvalidDataException($"The ZIP macro entry has an inconsistent size: {relativePath}");

            using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            files.Add(new AuxiliaryMacroFile(relativePath, reader.ReadToEnd()));
            totalBytes += bytes.Length;
        }

        return files;
    }

    private static bool IsValidRelativePath(string path) => path.Split('/').All(part => part.Length > 0
        && part is not "." and not ".."
        && part.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) < 0
        && !part.EndsWith('.') && !part.EndsWith(' ')
        && !part.Any(char.IsControl));
}
