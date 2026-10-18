using System.Reflection;
using System.Runtime.InteropServices;

namespace MacroHid.Runtime;

internal static class NativeFiles
{
    public static void EnsureExtracted()
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        Extract("WinDivert.dll", Path.Combine(directory, "WinDivert.dll"));
        Extract("WinDivert64.sys", Path.Combine(directory, "WinDivert64.sys"));
        NativeLibrary.Load(Path.Combine(directory, "WinDivert.dll"));
    }

    private static void Extract(string resourceName, string destination)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"安装包里缺少 {resourceName}。");

        if (SameLength(destination, stream.Length))
        {
            return;
        }

        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.CopyTo(file);
            }

            IOException? last = null;
            for (var attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    File.Move(temporary, destination, overwrite: true);
                    return;
                }
                catch (IOException ex)
                {
                    last = ex;
                    if (SameLength(destination, stream.Length))
                    {
                        return;
                    }

                    Thread.Sleep(250);
                }
            }

            var fileName = Path.GetFileName(destination);
            throw new IOException($"无法释放 {fileName}。请允许 WinDivert 驱动后再试。", last);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static bool SameLength(string path, long length)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length == length;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
