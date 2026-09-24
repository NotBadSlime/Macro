namespace MacroHid.Core;

public sealed class CoreSelectionSchedule
{
    private bool startupConsumed;
    private readonly Dictionary<string, bool> filterWasRunning = new(StringComparer.OrdinalIgnoreCase);

    public bool TryConsumeAutomatic(
        IEnumerable<string> processFilters,
        IEnumerable<string> runningProcessNames,
        bool playbackBusy)
    {
        var measure = false;
        if (!startupConsumed)
        {
            startupConsumed = true;
            measure = !playbackBusy;
        }

        var running = new HashSet<string>(
            runningProcessNames.Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in processFilters)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                continue;
            }

            var key = filter.Trim();
            seen.Add(key);
            var isRunning = running.Any(name => PlaybackProcessFilter.Matches(key, name));
            filterWasRunning.TryGetValue(key, out var wasRunning);
            if (isRunning && !wasRunning && !playbackBusy)
            {
                measure = true;
            }

            filterWasRunning[key] = isRunning;
        }

        foreach (var key in filterWasRunning.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            filterWasRunning.Remove(key);
        }

        return measure;
    }
}
