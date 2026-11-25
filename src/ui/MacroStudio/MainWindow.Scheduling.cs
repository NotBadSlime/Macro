using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio;

public partial class MainWindow
{
    private int listeningUpdateGeneration;
    private volatile bool shutdownStarted;
    private bool shutdownFinished;
    private long stopEpoch;
    private readonly object triggerGate = new();
    private readonly PlaybackRunRegistry playbackRuns = new();
    private readonly Dictionary<string, (bool Daemon, bool Background)> listeningPlaybackModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> listeningFingerprints = [];
    private readonly Dictionary<string, HotkeyGesture> listeningBindings = [];
    private Dictionary<string, HotkeyRoute> hotkeyRoutes = [];
    private readonly ConcurrentDictionary<string, MacroPlaybackController> heldControllers = new();
    private bool preparingImmediate;
    private string? preparingMacroId;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MacroPlaybackController, ObservedRun> observedRuns = new();
    private sealed class ObservedRun { internal long Value = -1; }
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
    private sealed record HotkeyRoute(MacroPlaybackController Controller, string Filter, string Name, bool Background, bool Daemon);
    private sealed record StartedPlayback(MacroPlaybackController Controller, Task<PlaybackRunResult> Completion);

    private async Task<StartedPlayback?> StartDirectPlaybackAsync(
        MacroDocument document, string? macroId, Func<string, MacroDocument?>? resolver,
        PlaybackExecutionOptions options, bool immediate, CancellationToken cancellation = default)
    {
        if (preparingImmediate || macroRecorder is not null
            || IsPlaybackBlocked(macroId, document.Playback.RunAsDaemon, options.BackgroundWindow))
            return null;

        using var preparation = TrackPreparation();
        var epoch = Interlocked.Read(ref stopEpoch);
        var version = runtimePrecisionSettings.Version;
        preparingImmediate = true;
        preparingMacroId = macroId;
        IMacroPlaybackExecutor? prepared = null;
        try
        {
            RefreshDirectStopBinding();
            prepared = await Task.Run(() => PlaybackEngineHost.Prepare(new SendInputMacroSink(), document,
                resolver ?? CaptureResolver(document, macroId).Resolver, options, immediate, version, OnStartupGateWaitChanged),
                cancellation);

            MacroPlaybackController controller;
            Task<PlaybackRunResult> completion;
            lock (triggerGate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (shutdownStarted || epoch != Interlocked.Read(ref stopEpoch)) throw new OperationCanceledException();
                if (macroRecorder is not null || playbackRuns.BlocksStart(macroId, document.Playback.RunAsDaemon, options.BackgroundWindow))
                    return null;

                controller = new(document, prepared, options);
                prepared = null;
                _ = controller.RunNowAsync();
                playbackRuns.Track(macroId, document.Playback.RunAsDaemon, options.BackgroundWindow, direct: true, controller, controller.RunId);
                completion = controller.WhenIdleAsync();
                playbackController = controller;
            }
            ObservePlayback(controller, controller.RunId, completion);
            UpdatePlaybackStatus();
            return new StartedPlayback(controller, completion);
        }
        finally
        {
            (prepared as IDisposable)?.Dispose();
            preparingImmediate = false;
            preparingMacroId = null;
            RefreshDirectStopBinding();
        }
    }

    private bool IsPlaybackBlocked(string? macroId, bool daemon = false, bool background = false)
    {
        lock (triggerGate) return playbackRuns.BlocksStart(macroId, daemon, background);
    }

    private void RefreshDirectStopBinding()
    {
        if (shutdownStarted) return;
        bool armed;
        lock (triggerGate) armed = preparingImmediate || playbackRuns.Active.Any(run => run.Direct);
        if (runNowEmergencyStopArmed == armed) return;
        runNowEmergencyStopArmed = armed;
        RefreshKeyboardHook();
    }

    // Held only while a low-jitter hotkey macro can fire, so its first run skips switching timer resolution and
    // power throttling on. UI thread only. MACROHID_NO_PRECISION_WARM=1 disables it (power-sensitive setups).
    private IDisposable? precisionWarmLease;
    private static readonly bool PrecisionWarmDisabled = Environment.GetEnvironmentVariable("MACROHID_NO_PRECISION_WARM") == "1";
    private void UpdatePrecisionWarmLease()
    {
        var needed = !PrecisionWarmDisabled && !shutdownStarted && Volatile.Read(ref hotkeyRoutes).Values
            .Any(route => route.Controller.Precision is PrecisionMode.ExtremeDuringPlayback or PrecisionMode.UltraLowJitter);
        precisionWarmLease = PlaybackEngineHost.UpdateListeningWarm(needed, precisionWarmLease);
    }
    private sealed record PreparedDefinition(string Fingerprint, IMacroPlaybackExecutor Executor);

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
        var version = runtimePrecisionSettings.Version;
        try
        {
            foreach (var candidate in candidates)
            {
                var captured = CaptureResolver(candidate.Document, candidate.Item.Id);
                // The engine is part of the definition: switching the release rebuilds every listening macro.
                string fingerprint = captured.Fingerprint + options[candidate.Item.Id] + version.Id;
                if (known.GetValueOrDefault(candidate.Item.Id) == fingerprint) continue;
                var executor = PlaybackEngineHost.Prepare(new SendInputMacroSink(), candidate.Document, captured.Resolver,
                    options[candidate.Item.Id], immediate: false, version, OnStartupGateWaitChanged);
                prepared.Add(candidate.Item.Id, new(fingerprint, executor));
            }
            return prepared;
        }
        catch { foreach (var update in prepared.Values) (update.Executor as IDisposable)?.Dispose(); throw; }
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
        externalJobs.CancelAll();
        lock (triggerGate)
        {
            Interlocked.Increment(ref stopEpoch);
            foreach (var run in playbackRuns.Active) run.Controller.Stop();
        }
    }

    private async void FinishShutdownAsync()
    {
        if (shutdownStarted) return;
        if (!TrySaveActiveEditorForNavigation())
        {
            shutdownQueued = false;
            exitConfirmed = false;
            if (isInTray) RestoreFromTray();
            return;
        }
        shutdownStarted = true;
        await ((App)Application.Current).StopControlServerAsync();
        UpdatePrecisionWarmLease();
        listeningUpdateGeneration++;
        RequestAllStops();
        autoSaveTimer.Stop();
        coreSelectionTimer.Stop();
        keyboardHook?.Stop();
        MacroPlaybackController[] controllers;
        lock (triggerGate)
            controllers = listeningControllers.Values.Concat(playbackRuns.Active.Select(run => run.Controller))
                .Distinct().ToArray();
        var cleanup = Task.WhenAll(controllers.Select(c => c.DisposeAsync().AsTask())
            .Concat(retiredControllers.ToArray()).Concat(pendingPreparations.ToArray()));
        if (await Task.WhenAny(cleanup, Task.Delay(5000)) != cleanup) SetStatus("仍在停止，请稍候…");
        try
        {
            await cleanup;
            shutdownFinished = true;
            Close();
        }
        catch (Exception ex) { AppLog.Error("Shutdown", "Cleanup failed", ex); SetErrorStatus(ex); }
    }
}
