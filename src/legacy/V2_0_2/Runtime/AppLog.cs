using System.Text;

namespace MacroHid.Runtime;

public static class AppLog
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroHID", "log");
    private static readonly RollingLogFile Writer = new(DirectoryPath,
        $"runtime-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}");

    private static readonly BufferedLogQueue Queue = new(Writer.Write);
    public static void Info(string source, string message) => Queue.Write("INFO", source, message);
    public static void Error(string source, string message, Exception? exception = null) =>
        Queue.Write("ERROR", source, exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    public static bool Flush(TimeSpan timeout) => Queue.Flush(timeout);

    public static void Initialize()
    {
        // Only our own old log files are eligible for retention cleanup.
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "runtime-*.log*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14)) File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
        Info("App", $"Starting; version={System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version}; " +
            $"pid={Environment.ProcessId}; 64bit={Environment.Is64BitProcess}; OS={Environment.OSVersion}; " +
            $"runtime={Environment.Version}; base={AppContext.BaseDirectory}");
    }
}

internal sealed class RollingLogFile(string directory, string name, long maxBytes = 4 * 1024 * 1024)
{
    private readonly object gate = new();
    private readonly string path = Path.Combine(directory, name + ".log");
    internal void Write(string level, string source, string message)
    {
        // Logging must not interrupt playback, error reporting, or application shutdown.
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(directory);
                // Bound individual records as well as file sizes.
                if (message.Length > 65536) message = message[..65536] + " [truncated]";
                string record = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] [{source}] {message}{Environment.NewLine}";
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(record) > maxBytes)
                {
                    if (File.Exists(path + ".2")) File.Delete(path + ".2");
                    if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2");
                    File.Move(path, path + ".1");
                }
                File.AppendAllText(path, record, new UTF8Encoding(false));
            }
        }
        catch { }
    }
}

internal sealed class BufferedLogQueue
{
    private readonly object gate = new();
    private readonly LinkedList<(string Level, string Source, string Message)> queue = new();
    private readonly Action<string, string, string> write;
    private readonly ManualResetEventSlim drained = new(true);
    private int dropped;
    internal BufferedLogQueue(Action<string, string, string> write)
    {
        this.write = write;
        var worker = new Thread(Consume) { IsBackground = true, Name = "MacroHID-Log" };
        using (ExecutionContext.SuppressFlow()) worker.Start();
    }
    internal void Write(string level, string source, string message)
    {
        lock (gate)
        {
            if (queue.Count >= 4096)
            {
                var victim = queue.First;
                if (level == "ERROR")
                    while (victim is not null && victim.Value.Level == "ERROR") victim = victim.Next;
                if (level != "ERROR" || victim is null) { dropped++; return; }
                queue.Remove(victim);
                dropped++;
            }
            // Bound memory before enqueueing large exceptions or status messages.
            queue.AddLast((level, source, message.Length > 65536 ? message[..65536] + " [truncated]" : message));
            drained.Reset();
            Monitor.Pulse(gate);
        }
    }
    internal bool Flush(TimeSpan timeout) => drained.Wait(timeout);
    private void Consume()
    {
        while (true)
        {
            (string Level, string Source, string Message) item;
            int lost;
            lock (gate)
            {
                while (queue.Count == 0) Monitor.Wait(gate);
                item = queue.First!.Value;
                queue.RemoveFirst();
                lost = dropped; dropped = 0;
            }
            try
            {
                if (lost > 0) write("WARN", "Log", $"Dropped {lost} records due to queue capacity");
                write(item.Level, item.Source, item.Message);
            }
            catch { }
            lock (gate) if (queue.Count == 0) drained.Set();
        }
    }
}
