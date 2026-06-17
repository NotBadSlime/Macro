using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed class PaddleOcrBridge : IDisposable
{
    private Process? ocrProcess;
    private readonly string executablePath;
    private readonly string modelDir;
    private readonly ConcurrentQueue<OcrRequest> requestQueue = new();
    private readonly SemaphoreSlim processLock = new(1, 1);
    private volatile bool disposed;

    public PaddleOcrBridge(string? executablePath = null, string? modelDir = null)
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        this.executablePath = executablePath ?? Path.Combine(appDir, "paddleocr", "ppocr_server.exe");
        this.modelDir = modelDir ?? Path.Combine(appDir, "paddleocr", "models");
    }

    public bool IsAvailable => File.Exists(executablePath);

    public async Task<string> RecognizeTextAsync(ScreenRegion region, string language = "ch", CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return string.Empty;

        var pixels = ScreenCaptureService.CaptureRegion(region);
        if (pixels == null || pixels.Length == 0)
            return string.Empty;

        var width = region.Width;
        var height = region.Height;

        await EnsureProcessRunning(cancellationToken);
        if (ocrProcess == null || ocrProcess.HasExited)
            return string.Empty;

        var request = new OcrRequest
        {
            Width = width,
            Height = height,
            Pixels = Convert.ToBase64String(pixels),
            Language = language
        };

        var json = JsonSerializer.Serialize(request);
        return await SendRequestAsync(json, cancellationToken);
    }

    public bool ContainsText(ScreenRegion region, string expectedText, bool contains = true, string language = "ch")
    {
        try
        {
            var result = RecognizeTextAsync(region, language, CancellationToken.None).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(result)) return false;
            return contains
                ? result.Contains(expectedText, StringComparison.OrdinalIgnoreCase)
                : result.Equals(expectedText, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureProcessRunning(CancellationToken cancellationToken)
    {
        if (ocrProcess != null && !ocrProcess.HasExited)
            return;

        await processLock.WaitAsync(cancellationToken);
        try
        {
            if (ocrProcess != null && !ocrProcess.HasExited)
                return;

            ocrProcess?.Dispose();
            ocrProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = $"--model_dir \"{modelDir}\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardInputEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            ocrProcess.Start();

            var readyLine = await ocrProcess.StandardOutput.ReadLineAsync(cancellationToken);
            if (readyLine == null || !readyLine.Contains("ready"))
            {
                ocrProcess.Kill();
                ocrProcess = null;
            }
        }
        finally
        {
            processLock.Release();
        }
    }

    private async Task<string> SendRequestAsync(string json, CancellationToken cancellationToken)
    {
        if (ocrProcess == null || ocrProcess.HasExited)
            return string.Empty;

        await processLock.WaitAsync(cancellationToken);
        try
        {
            await ocrProcess.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await ocrProcess.StandardInput.FlushAsync();

            var response = await ocrProcess.StandardOutput.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(response))
                return string.Empty;

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.TryGetProperty("text", out var textProp))
                return textProp.GetString() ?? string.Empty;

            if (root.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in resultsProp.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var itemText))
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(itemText.GetString());
                    }
                }
                return sb.ToString();
            }

            return string.Empty;
        }
        finally
        {
            processLock.Release();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (ocrProcess != null && !ocrProcess.HasExited)
        {
            try
            {
                ocrProcess.StandardInput.WriteLine("{\"command\":\"exit\"}");
                ocrProcess.StandardInput.Flush();
                if (!ocrProcess.WaitForExit(3000))
                    ocrProcess.Kill();
            }
            catch
            {
                try { ocrProcess.Kill(); } catch { }
            }
            ocrProcess.Dispose();
        }

        processLock.Dispose();
    }

    private sealed class OcrRequest
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public string Pixels { get; set; } = string.Empty;
        public string Language { get; set; } = "ch";
    }
}
