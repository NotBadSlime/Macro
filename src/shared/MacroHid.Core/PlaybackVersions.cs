namespace MacroHid.Core;

/// <summary>One release the version switcher can run. <see cref="Folder"/> is null for the current engine.</summary>
public sealed record PlaybackVersionInfo(string Id, string Label, string? Folder, string NotesKey);

/// <summary>
/// The release line 1.2.0 → 2.0.3. Every entry except the current one runs that release's own playback code
/// (src/legacy/&lt;Folder&gt;). 1.3.0 and 1.4.0 share one entry: their playback code is identical.
/// </summary>
public static class PlaybackVersions
{
    public const string Current = "2.1.0";

    public static IReadOnlyList<PlaybackVersionInfo> All { get; } =
    [
        new(Current, "2.1.0", null, "PlaybackVersionNotes210"),
        new("2.0.2", "2.0.2", "V2_0_2", "PlaybackVersionNotes202"),
        new("2.0.1", "2.0.1", "V2_0_1", "PlaybackVersionNotes201"),
        new("2.0.0", "2.0.0", "V2_0_0", "PlaybackVersionNotes200"),
        new("1.6.0", "1.6.0", "V1_6_0", "PlaybackVersionNotes160"),
        new("1.5.0", "1.5.0", "V1_5_0", "PlaybackVersionNotes150"),
        new("1.4.1", "1.4.1", "V1_4_1", "PlaybackVersionNotes141"),
        new("1.4.0", "1.3.0 / 1.4.0", "V1_4_0", "PlaybackVersionNotes140"),
        new("1.2.0", "1.2.0", "V1_2_0", "PlaybackVersionNotes120"),
    ];

    /// <summary>A known id, or the current one for an empty or unknown id (for example one saved by a newer build).</summary>
    public static PlaybackVersionInfo Resolve(string? id)
    {
        var trimmed = id?.Trim();
        if (trimmed is "1.3.0") trimmed = "1.4.0";
        // 2.0.3 and 2.0.4 played with the engine that 2.1.0 extends; their saved setting means the current engine.
        if (trimmed is "2.0.3" or "2.0.4") trimmed = Current;
        return All.FirstOrDefault(v => string.Equals(v.Id, trimmed, StringComparison.Ordinal)) ?? All[0];
    }

    public static bool IsCurrent(string? id) => Resolve(id).Folder is null;

    /// <summary>The version a settings file from before the switcher meant with its precision generation.</summary>
    public static string FromGeneration(PrecisionGeneration generation) => PrecisionGenerations.Normalize(generation) switch
    {
        PrecisionGeneration.V1_6 => "1.6.0",
        PrecisionGeneration.V2_0 => "2.0.1",
        PrecisionGeneration.V2_0_2 => "2.0.2",
        _ => Current
    };
}
