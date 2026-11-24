using MacroHid.Runtime;

namespace MacroStudio.Services;

// MainWindow uses this registry under its trigger gate. Flags describe the running
// invocation and do not change if a listening definition is edited mid-run.
internal sealed class PlaybackRunRegistry
{
    internal sealed record Run(string? MacroId, bool Daemon, bool Background, bool Direct, MacroPlaybackController Controller, long RunId)
    {
        internal bool Active => Controller.RunId == RunId
            && Controller.Status is PlaybackStatus.Running or PlaybackStatus.Stopping;
    }

    private readonly Dictionary<MacroPlaybackController, Run> runs = [];

    internal IEnumerable<Run> Active => runs.Values.Where(run => run.Active);

    internal bool BlocksStart(string? macroId, bool daemon, bool background, MacroPlaybackController? listener = null)
    {
        // The active listener must still see its own trigger as a stop request.
        if (listener is not null && runs.TryGetValue(listener, out var own) && own.Active)
            return false;

        // The window-targeted input backend owns a process-wide target.
        return Active.Any(run => SameMacro(run.MacroId, macroId)
            || background || run.Background || (!daemon && !run.Daemon));
    }

    internal void Track(string? macroId, bool daemon, bool background, bool direct, MacroPlaybackController controller, long runId)
    {
        if (!runs.TryGetValue(controller, out var existing) || existing.RunId != runId)
            runs[controller] = new Run(macroId, daemon, background, direct, controller, runId);
    }

    internal Run? Finish(MacroPlaybackController controller, long runId)
    {
        if (!runs.TryGetValue(controller, out var run) || run.RunId != runId) return null;
        runs.Remove(controller);
        return run;
    }

    internal Run? Find(string? macroId) => Active.FirstOrDefault(run => SameMacro(run.MacroId, macroId));

    private static bool SameMacro(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
