namespace MacroHid.LegacyContract;

// The boundary between the current app and an earlier release's playback code (src/legacy/<V>). Each earlier release is
// compiled, unchanged, into its own assembly that contains a LegacyBridge implementing ILegacyRuntime. Only primitive types
// and these contract types cross the boundary: the two sides have different MacroDocument, PlaybackStatus, ... types.

/// <summary>Playback options as the current UI builds them; the bridge maps them onto its release's own options.</summary>
public sealed record LegacyPlaybackOptions(
    string Mode,
    int Count,
    string Precision,
    string AffinityMask,
    bool BackgroundWindow,
    string BackgroundProcess,
    long ZeroGapHoldMicroseconds,
    long ModifierChordHoldMicroseconds,
    bool Immediate);

public sealed record LegacyRunResult(string Status, int IterationsCompleted, int ActionsSubmitted, bool Cancelled);

/// <summary>
/// Runs a card the release does not know (for example 断网 or 锁帧 in 1.2.0) with the current app's implementation, at the
/// point where the release's managed timeline reaches it. Only the effect cards listed in <see cref="LegacyExternalCards"/>.
/// </summary>
public interface ILegacyEffectHost
{
    /// <summary>One per playback run; disposing it restores effects the run left on, as the current app does.</summary>
    IDisposable BeginRun();

    void Execute(string stepJson, CancellationToken cancellationToken);
}

public interface ILegacyRuntime
{
    /// <summary>The release, for example "1.4.1".</summary>
    string Version { get; }

    /// <summary>Card types ("key.tap", "weak.net", ...) the release's own parser understands.</summary>
    IReadOnlyCollection<string> StepTypes { get; }

    /// <summary>Card types the release does not know but runs through <see cref="ILegacyEffectHost"/>.</summary>
    IReadOnlyCollection<string> ExternalStepTypes { get; }

    void SetEffectHost(ILegacyEffectHost host);

    /// <summary>What the release's App did at start-up (native warm-up, path warm-up).</summary>
    void Activate(string precision, string affinityMask);

    /// <summary>What the release's UI did after the precision settings were saved.</summary>
    void PrecisionChanged(string precision, string affinityMask);

    /// <summary>What the release's UI did after a core mask was chosen or selected automatically.</summary>
    void AffinityMaskApplied(string precision, string affinityMask);

    /// <summary>Releases that kept the timer resolution up while a precise hotkey macro listened (2.0.1+). Others ignore it.</summary>
    void SetListeningWarm(bool needed);

    /// <summary>What the release's App did on exit, also used when switching away from it.</summary>
    void Shutdown();

    /// <summary>
    /// The release's own "new MacroPlaybackExecutor(inputSink, resolver); executor.Prepare(document, options)".
    /// <paramref name="resolveMacroJson"/> returns a called macro as .mcrx text, or null when it does not exist.
    /// </summary>
    ILegacyPreparedMacro Prepare(string macroJson, Func<string, string?> resolveMacroJson, LegacyPlaybackOptions options);
}

public interface ILegacyPreparedMacro : IDisposable
{
    /// <summary>The release's own MacroPlaybackController for the prepared executor (owns it from now on).</summary>
    ILegacyController CreateController();
}

public interface ILegacyController : IDisposable
{
    /// <summary>The release's PlaybackStatus name: Idle, Listening, Running, Stopping, InputUnavailable or Error.</summary>
    string Status { get; }

    Exception? LastError { get; }

    Task TriggerPressedAsync();

    void TriggerReleased();

    Task RunNowAsync();

    void Stop();

    Task<LegacyRunResult> WhenIdleAsync();
}

/// <summary>Effect cards that may run in a release that predates them (they send no input of their own).</summary>
public static class LegacyExternalCards
{
    public static readonly IReadOnlyCollection<string> EffectTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "weak.net", "net.limit", "clock.shift", "frame.lock",
        "ysr.fps.toggle", "ysr.fps.start", "ysr.fps.stop",
        "ky3.fps.toggle", "ky3.fps.start", "ky3.fps.stop",
        "memory.fps.toggle", "memory.fps.start", "memory.fps.stop",
        "fufu.fps.toggle", "fufu.fps.start", "fufu.fps.stop",
        "window.activate",
    };
}
