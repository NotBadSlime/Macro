using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using MacroHid.Core;
using MacroHid.Runtime;

namespace MacroStudio;

public partial class MainWindow
{
    private int listeningUpdateGeneration;
    private volatile bool shutdownStarted;
    private bool shutdownFinished;
    private long stopEpoch;
    private readonly object triggerGate = new();
    private MacroPlaybackController? immediateController;
    private readonly Dictionary<string, string> listeningFingerprints = [];
    private readonly Dictionary<string, HotkeyGesture> listeningBindings = [];
    private Dictionary<string, HotkeyRoute> hotkeyRoutes = [];
    private readonly ConcurrentDictionary<string, MacroPlaybackController> heldControllers = new();
    private bool preparingImmediate;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MacroPlaybackController, ObservedRun> observedRuns = new();
    private sealed class ObservedRun { internal long Value; }
    private readonly List<Task> retiredControllers = [];
    private readonly List<Task> pendingPreparations = [];
    private IDisposable TrackPreparation()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingPreparations.Add(completion.Task);
        return new PreparationScope(() => {
            pendingPreparations.Remove(completion.Task);
            completion.TrySetResult();
        });
    }
    private sealed class PreparationScope(Action finish) : IDisposable
    {
        private Action? completion = finish;
        public void Dispose() => Interlocked.Exchange(ref completion, null)?.Invoke();
    }
    private sealed record HotkeyRoute(MacroPlaybackController Controller, string Filter, string Name, bool Background);

    // Held only while a low-jitter hotkey macro can fire, so its first run skips switching timer resolution and
    // power throttling on. UI thread only. MACROHID_NO_PRECISION_WARM=1 disables it (power-sensitive setups).
    private IDisposable? precisionWarmLease;
    private static readonly bool PrecisionWarmDisabled = Environment.GetEnvironmentVariable("MACROHID_NO_PRECISION_WARM") == "1";
    private void UpdatePrecisionWarmLease()
    {
        var needed = !PrecisionWarmDisabled && !shutdownStarted && Volatile.Read(ref hotkeyRoutes).Values
            .Any(route => route.Controller.Precision is PrecisionMode.ExtremeDuringPlayback or PrecisionMode.UltraLowJitter);
        if (needed && precisionWarmLease is null) precisionWarmLease = PrecisionPlaybackContext.AcquireWarmLease();
        else if (!needed && precisionWarmLease is not null) { precisionWarmLease.Dispose(); precisionWarmLease = null; }
    }
    private sealed record PreparedDefinition(string Fingerprint, MacroPlaybackExecutor Executor);

    private (Func<string, MacroDocument?> Resolver, string Fingerprint) CaptureResolver(MacroDocument root, string? rootId)
    {
        var documents = new Dictionary<string, MacroDocument>(StringComparer.OrdinalIgnoreCase);
        lock (libraryStore.SyncRoot)
        {
            var library = libraryStore.Load();
            string? group = library.Items.FirstOrDefault(item => item.Id == rootId)?.GroupId;
            var pending = new Queue<MacroDocument>();
            var visited = new HashSet<MacroDocument>(ReferenceEqualityComparer.Instance);
            pending.Enqueue(root);
            while (pending.TryDequeue(out var document))
            {
                if (!visited.Add(document)) continue;
                if (visited.Count > 4096) throw new InvalidOperationException("宏依赖数量超过安全上限。");
                foreach (var reference in MacroCallReferenceCollector.Collect(document))
                {
                    var name = reference.Trim();
                    if (documents.ContainsKey(name)) continue;
                    MacroDocument target;
                    if (name == root.Name || name == root.Id) target = root;
                    else
                    {
                        var item = MacroLibraryResolver.Resolve(library.Items, name, group)
                            ?? throw new InvalidOperationException($"找不到调用的宏：{name}");
                        target = libraryStore.ReadMacro(item.Id);
                    }
                    documents[name] = target;
                    pending.Enqueue(target);
                }
            }
        }
        var text = new StringBuilder(McrxSerializer.Serialize(root));
        foreach (var pair in documents.OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append('\n').Append(pair.Key).Append('\n').Append(McrxSerializer.Serialize(pair.Value));
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        return (name => documents.GetValueOrDefault(name.Trim()), fingerprint);
    }

    private Dictionary<string, PreparedDefinition> PrepareListeningDefinitions(
        IReadOnlyList<ListeningCandidate> candidates, Dictionary<string, PlaybackExecutionOptions> options,
        Dictionary<string, string> known)
    {
        var prepared = new Dictionary<string, PreparedDefinition>();
        try
        {
            foreach (var candidate in candidates)
            {
                var captured = CaptureResolver(candidate.Document, candidate.Item.Id);
                string fingerprint = captured.Fingerprint + options[candidate.Item.Id];
                if (known.GetValueOrDefault(candidate.Item.Id) == fingerprint) continue;
                var executor = new MacroPlaybackExecutor(inputSink, macroResolver: captured.Resolver);
                try { executor.Prepare(candidate.Document, options[candidate.Item.Id]); }
                catch { executor.Dispose(); throw; }
                prepared.Add(candidate.Item.Id, new(fingerprint, executor));
            }
            return prepared;
        }
        catch { foreach (var update in prepared.Values) update.Executor.Dispose(); throw; }
    }

    private void RetireController(MacroPlaybackController controller)
    {
        var task = controller.DisposeAsync().AsTask();
        retiredControllers.Add(task);
        _ = ObserveRetirement(task);
    }
    private async Task ObserveRetirement(Task task)
    {
        try { await task; }
        catch (Exception ex) { AppLog.Error("Playback", "Retirement failed", ex); }
        finally { retiredControllers.Remove(task); }
    }

    private void RequestAllStops()
    {
        lock (triggerGate)
        {
            Interlocked.Increment(ref stopEpoch);
            foreach (var route in Volatile.Read(ref hotkeyRoutes).Values) route.Controller.Stop();
            Volatile.Read(ref immediateController)?.Stop();
            Volatile.Read(ref playbackController)?.Stop();
        }
    }

    private async void FinishShutdownAsync()
    {
        if (shutdownStarted) return;
        if (autoSaveTimer.IsEnabled && jsonValid) PerformDebouncedAutoSave();
        shutdownStarted = true;
        UpdatePrecisionWarmLease();
        listeningUpdateGeneration++;
        RequestAllStops();
        autoSaveTimer.Stop();
        coreSelectionTimer.Stop();
        keyboardHook?.Stop();
        var controllers = listeningControllers.Values.Append(immediateController).Append(playbackController)
            .OfType<MacroPlaybackController>().Distinct().ToArray();
        var cleanup = Task.WhenAll(controllers.Select(c => c.DisposeAsync().AsTask())
            .Concat(retiredControllers.ToArray()).Concat(pendingPreparations.ToArray()));
        if (await Task.WhenAny(cleanup, Task.Delay(5000)) != cleanup) SetStatus("仍在停止，请稍候…");
        try
        {
            await cleanup;
            await pendingAutoSave;
            shutdownFinished = true;
            Close();
        }
        catch (Exception ex) { AppLog.Error("Shutdown", "Cleanup failed", ex); SetStatus(ex.Message); }
    }
}
