namespace MacroHid.Core;

/// <summary>
/// Which generation of the timing behaviour a run uses. Separate from <see cref="PrecisionMode"/> (how much CPU is spent):
/// this picks the algorithm. The default is always the latest; older generations exist so a user can compare or fall back.
/// </summary>
/// <remarks>
/// What differs, by generation:
/// <list type="bullet">
/// <item>Native engine: <see cref="V1_6"/> uses the older standby/inline engine, the others the shared scheduler.</item>
/// <item>Timeline rules (press floor delays only its own release, loops repeat at the written period, loop starts are
/// phase-locked): in <see cref="Latest"/> and <see cref="V2_0_2"/>; not in <see cref="V2_0"/> and <see cref="V1_6"/>.</item>
/// <item>Release anchor (a key's hold is counted from when its SendInput started): only in <see cref="Latest"/>. Every other
/// generation counts it from when SendInput returned.</item>
/// </list>
/// 2.0.0 and 2.0.1 differ only in tracing and warm-up code, not in timing, so they are one generation.
/// Values are stored by name in the settings file and passed to the native engine as a number: add new generations at the end
/// with the next number and never reuse or renumber one.
/// </remarks>
public enum PrecisionGeneration
{
    /// <summary>Current behaviour. The default.</summary>
    Latest = 0,

    /// <summary>MacroHID 2.0.0 and 2.0.1: shared scheduler, earlier timeline rules, hold counted from SendInput's return.</summary>
    V2_0 = 1,

    /// <summary>MacroHID 1.6.0: the older standby/inline native engine and the earlier timeline rules.</summary>
    V1_6 = 2,

    /// <summary>MacroHID 2.0.2: the timeline fixes, with the hold still counted from SendInput's return.</summary>
    V2_0_2 = 3,
}

public static class PrecisionGenerations
{
    /// <summary>True for the generations before the timeline fixes (2.0.0, 2.0.1 and 1.6.0).</summary>
    public static bool UsesEarlierTimeline(PrecisionGeneration generation) =>
        generation is PrecisionGeneration.V2_0 or PrecisionGeneration.V1_6;

    /// <summary>True when the older standby/inline native engine runs the plan.</summary>
    public static bool UsesLegacyNativeEngine(PrecisionGeneration generation) => generation == PrecisionGeneration.V1_6;

    /// <summary>True when a key's hold is counted from when SendInput started; only the latest does this.</summary>
    public static bool CountsHoldFromSendStart(PrecisionGeneration generation) => generation == PrecisionGeneration.Latest;

    /// <summary>A stored or parsed value outside the enum falls back to the latest rather than to an arbitrary old behaviour.</summary>
    public static PrecisionGeneration Normalize(PrecisionGeneration generation) =>
        Enum.IsDefined(generation) ? generation : PrecisionGeneration.Latest;
}
