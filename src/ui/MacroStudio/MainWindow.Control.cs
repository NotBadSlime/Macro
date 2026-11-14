using System.IO;
using System.Windows;
using MacroHid.Control;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio;

public partial class MainWindow
{
    private readonly ControlJobs externalJobs = new();
    private readonly Dictionary<string, ControlResponse> acceptedExternalRequests = new();
    internal void HideToTrayForExternalLaunch() => MinimizeToTray();
    internal ControlResponse HandleControlRequest(ControlRequest request)
        => HandleDeduplicatedControlRequest(request) with { Capabilities = [ControlProtocol.PlaybackRepeatCapability] };

    private ControlResponse HandleDeduplicatedControlRequest(ControlRequest request)
    {
        if (request.Command != "run") return HandleControlRequestCore(request);
        if (!Guid.TryParse(request.RequestId, out _)) return ControlResponse.Fail("invalid", "run requires a unique requestId (GUID).");
        if (acceptedExternalRequests.TryGetValue(request.RequestId!, out var previous)) return previous;
        var response = HandleControlRequestCore(request);
        if (response.Ok)
        {
            if (acceptedExternalRequests.Count >= 512) acceptedExternalRequests.Remove(acceptedExternalRequests.Keys.First());
            acceptedExternalRequests.Add(request.RequestId!, response);
        }
        return response;
    }

    private ControlResponse HandleControlRequestCore(ControlRequest request)
    {
        if (shutdownStarted || shutdownQueued) return ControlResponse.Fail("shutdown", "Studio is shutting down.");
        try
        {
            if (request.Command == "show") { RestoreFromTray(); return new(true, "ok"); }
            if (request.Command == "list")
            {
                var snapshot = libraryStore.Load();
                return new(true, "ok", Macros: snapshot.Items.Select(item => new ControlMacro(item.Id, item.Name,
                    snapshot.Groups.Single(g => g.Id == item.GroupId).Name, item.Folder)).ToArray());
            }
            if (request.Command == "status")
            {
                if (request.RunId is null) return new(true, "ok", Jobs: externalJobs.List(), Busy: OtherMacroIsActive(null) || preparingImmediate);
                return externalJobs.Get(request.RunId) is { } job ? new(true, "ok", Job: job) : ControlResponse.Fail("not_found", "Task was not found (Studio may have restarted).");
            }
            if (request.Command == "stop")
            {
                if (request.RunId is null) return ControlResponse.Fail("invalid", "runId is required.");
                return externalJobs.Cancel(request.RunId) ? new(true, "ok", Job: externalJobs.Get(request.RunId)) : ControlResponse.Fail("not_found", "Task was not found.");
            }
            if (request.Command != "run") return ControlResponse.Fail("invalid", "Unknown command.");
            ControlProtocol.ValidatePlayback(request.Count, request.Loop);
            if (!double.IsFinite(request.DelayMilliseconds) || request.DelayMilliseconds < 0 || request.DelayMilliseconds > TimeSpan.FromDays(7).TotalMilliseconds)
                return ControlResponse.Fail("invalid", "Delay must be between 0 and 7 days.");
            if ((request.Id is not null) == (request.Name is not null) || (request.Name is not null && request.Library is null)
                || (request.Id is not null && request.Library is not null))
                return ControlResponse.Fail("invalid", "Specify an ID, or a library and name.");
            var library = libraryStore.Load();
            var matches = request.Id is not null
                ? library.Items.Where(item => string.Equals(item.Id, request.Id, StringComparison.OrdinalIgnoreCase)).ToArray()
                : library.Items.Where(item => item.Name == request.Name && library.Groups.Any(group => group.Id == item.GroupId && (group.Name == request.Library || group.Id == request.Library))).ToArray();
            if (matches.Length == 0) return ControlResponse.Fail("not_found", "Macro was not found.");
            if (matches.Length > 1) return ControlResponse.Fail("ambiguous", "Several macros match. Use the macro ID.");
            var selected = matches[0];
            if (selected.IsConditionMacro) return ControlResponse.Fail("invalid", "A condition macro cannot be run as a standalone sequence.");
            // Freeze the saved document at acceptance; never switch the user's active editor.
            var document = libraryStore.ReadMacro(selected.Id);
            var frozen = CaptureResolver(document, selected.Id);
            var macro = new ControlMacro(selected.Id, selected.Name, library.Groups.Single(g => g.Id == selected.GroupId).Name, selected.Folder);
            var group = library.Groups.Single(g => g.Id == selected.GroupId);
            var jobSnapshot = externalJobs.Schedule(macro, request.DelayMilliseconds,
                (token, started) => RunExternalMacroAsync(document, frozen.Resolver, group, request, token, started));
            return new(true, "accepted", Job: jobSnapshot);
        }
        catch (ArgumentException ex) { return ControlResponse.Fail("invalid", ex.Message); }
        catch (Exception ex) { AppLog.Error("Control", "Command failed", ex); return ControlResponse.Fail("failed", ex.Message); }
    }

    private async Task<ControlExecution> RunExternalMacroAsync(MacroDocument document, Func<string, MacroDocument?> resolver,
        MacroLibraryGroup group, ControlRequest request, CancellationToken cancellation, Action started)
    {
        if (shutdownStarted || shutdownQueued) return new("cancelled");
        if (macroRecorder is not null || preparingImmediate || OtherMacroIsActive(null)) return new("busy", "Studio is recording, preparing or playing another macro.");
        using var preparation = TrackPreparation();
        preparingImmediate = true;
        IMacroPlaybackExecutor? prepared = null;
        MacroPlaybackController? controller = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var epoch = Interlocked.Read(ref stopEpoch);
            var options = CreateExternalPlaybackOptions(CreatePlaybackOptions(document), request) with
            {
                BackgroundProcess = BackgroundProcessSelection.Resolve(runtimePrecisionSettings.BackgroundProcess, group.IsGlobal ? string.Empty : group.ProcessFilter)
            };
            var version = runtimePrecisionSettings.Version;
            // Historical engines derive mode/count from the document itself. Override
            // only this execution snapshot; never persist command options to the library.
            document = CreateExternalPlaybackDocument(document, options);
            if (!options.BackgroundWindow && !MacroLibraryActivationFilter.Matches(group.IsGlobal ? string.Empty : group.ProcessFilter, ForegroundProcessService.GetForegroundProcessName()))
                return new("failed", "The foreground process does not match this macro library's process filter.");
            if (immediateController is not null) await immediateController.DisposeAsync();
            cancellation.ThrowIfCancellationRequested();
            // External commands already specify their mode. Legacy immediate preparation
            // would convert ToggleLoop back into one iteration.
            prepared = await Task.Run(() => PlaybackEngineHost.Prepare(inputSink, document, resolver, options, immediate: false, version), cancellation);
            cancellation.ThrowIfCancellationRequested();
            Task<PlaybackRunResult> completion;
            lock (triggerGate)
            {
                if (shutdownStarted || epoch != Interlocked.Read(ref stopEpoch)) return new("cancelled");
                if (OtherMacroIsActive(null)) return new("busy", "Another macro is running.");
                playbackController = immediateController = controller = new(document, prepared, options);
                prepared = null;
                runNowEmergencyStopArmed = true;
                RefreshKeyboardHook();
                using var initialCancel = cancellation.Register(controller.Stop);
                _ = controller.RunNowAsync();
                completion = controller.WhenIdleAsync();
            }
            using var stop = cancellation.Register(controller.Stop);
            started();
            UpdatePlaybackStatus();
            WatchPlayback(controller);
            var result = await completion;
            return new(result.Cancelled || cancellation.IsCancellationRequested ? "cancelled"
                : result.Status == PlaybackRunStatus.Completed ? "completed" : "failed", controller.LastError?.Message ?? (result.Status == PlaybackRunStatus.Completed ? null : result.Status.ToString()));
        }
        finally
        {
            (prepared as IDisposable)?.Dispose();
            try { if (controller is not null) await controller.DisposeAsync(); }
            finally { preparingImmediate = false; }
        }
    }

    private void GlobalCommandSettings_Click(object sender, RoutedEventArgs e) => new Controls.GlobalCommandDialog { Owner = this }.ShowDialog();

    internal static PlaybackExecutionOptions CreateExternalPlaybackOptions(PlaybackExecutionOptions source, ControlRequest request)
    {
        ControlProtocol.ValidatePlayback(request.Count, request.Loop);
        return source with { Mode = request.Loop ? PlaybackMode.ToggleLoop : PlaybackMode.FixedCount, Count = request.Count ?? 1 };
    }

    internal static MacroDocument CreateExternalPlaybackDocument(MacroDocument source, PlaybackExecutionOptions options) =>
        source with { Playback = source.Playback with { Mode = options.Mode, Count = options.Count } };
}
