// External-card hook, compiled only into snapshots that predate some effect cards (1.2.0 - 1.6.0). It is not part of the
// release: it lets the release's managed timeline run a card it does not know (断网, 锁帧, ...) with the current app's
// implementation. A macro without such cards never creates one, so its playback is the release's own.
// The three edits that use it are listed in each snapshot's patched-files.txt.
using System.Text.Json;
using MacroHid.LegacyContract;

namespace MacroHid.Core;

public sealed record LegacyExternalStep(string Type, string Json) : MacroStep
{
    /// <summary>Set by the bridge; null means the hook is off and unknown cards fail to parse as they did in the release.</summary>
    public static Action<string, CancellationToken>? Executor { get; set; }

    /// <summary>Called by the release's parser for a card type it does not know.</summary>
    public static MacroStep? TryCreate(string? type, JsonElement element) =>
        Executor is not null && type is not null && LegacyExternalCards.EffectTypes.Contains(type)
            ? new LegacyExternalStep(type, element.GetRawText())
            : null;

    public static void Run(LegacyExternalStep step, CancellationToken cancellationToken) =>
        (Executor ?? throw new InvalidOperationException($"Card '{step.Type}' needs the current app's implementation."))(step.Json, cancellationToken);
}
