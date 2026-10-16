namespace MacroHid.Core;

public static class InputPressTiming
{
    public const long DefaultZeroGapMicroseconds = 10_000;
    public const long DefaultModifierChordMicroseconds = 30_000;
    public const long MaxHoldMicroseconds = 60_000_000;

    public static readonly TimeSpan ZeroDelayHold = TimeSpan.FromMilliseconds(10);
    public static readonly TimeSpan ModifierChordHold = TimeSpan.FromMilliseconds(30);
    public static readonly TimeSpan DefaultPressReleaseGap = TimeSpan.FromMilliseconds(5);

    public static long ToTicks(TimeSpan duration, long qpcFrequency)
    {
        return Math.Max(0, (long)Math.Round(duration.TotalSeconds * qpcFrequency, MidpointRounding.AwayFromZero));
    }

    public static TimeSpan FromMicroseconds(long microseconds)
    {
        if (microseconds <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromTicks(microseconds * 10);
    }

    public static long HoldTicks(TimeSpan duration, long qpcFrequency)
    {
        if (duration <= TimeSpan.Zero || qpcFrequency <= 0)
        {
            return 0;
        }

        return Math.Max(1, ToTicks(duration, qpcFrequency));
    }

    public static long ZeroDelayHoldTicks(long qpcFrequency)
    {
        return Math.Max(1, ToTicks(ZeroDelayHold, qpcFrequency));
    }

    public static long ModifierChordHoldTicks(long qpcFrequency)
    {
        return Math.Max(1, ToTicks(ModifierChordHold, qpcFrequency));
    }
}

public readonly record struct InputPressFloor(TimeSpan ZeroGapHold, TimeSpan ModifierChordHold)
{
    public static InputPressFloor Factory { get; } = new(InputPressTiming.ZeroDelayHold, InputPressTiming.ModifierChordHold);

    /// <summary>
    /// Which generation of timing behaviour compiles and runs with this floor. It rides on the floor because the floor already
    /// reaches the compiler, the tracker, the managed runner and the condition monitor. Defaults to the latest; set with
    /// <c>floor with { Generation = ... }</c>, which keeps the two-argument constructor unchanged.
    /// </summary>
    public PrecisionGeneration Generation { get; init; } = PrecisionGeneration.Latest;

    public static InputPressFloor FromMicroseconds(long zeroGapMicroseconds, long modifierChordMicroseconds)
    {
        return new(
            InputPressTiming.FromMicroseconds(zeroGapMicroseconds),
            InputPressTiming.FromMicroseconds(modifierChordMicroseconds));
    }
}

public static class ModifierChord
{
    public static bool IsModifier(HidKey key)
    {
        return key is HidKey.LeftControl or HidKey.RightControl
            or HidKey.LeftShift or HidKey.RightShift
            or HidKey.LeftAlt or HidKey.RightAlt
            or HidKey.LeftGui or HidKey.RightGui;
    }

    public static bool Presses(InputAction action)
    {
        return action is KeyInputAction { Kind: KeyActionKind.Down } key
            && (key.Modifiers != HidModifier.None || IsModifier(key.Key));
    }

    public static bool Releases(InputAction action)
    {
        return action is KeyInputAction { Kind: KeyActionKind.Up } key
            && (key.Modifiers != HidModifier.None || IsModifier(key.Key));
    }
}

public sealed class ModifierChordClock
{
    private int held;
    private long anchor;

    public long AdjustDueTick(IReadOnlyList<InputAction> actions, long dueTick, long holdTicks)
    {
        if (held <= 0 || anchor <= 0 || !ReleasesHeldChord(actions))
        {
            return dueTick;
        }

        var earliest = anchor + holdTicks;
        return dueTick < earliest ? earliest : dueTick;
    }

    public void NoteSent(IReadOnlyList<InputAction> actions, long actualTick)
    {
        var presses = 0;
        var releases = 0;
        var otherKeyDown = false;
        foreach (var action in actions)
        {
            if (ModifierChord.Presses(action))
            {
                presses++;
            }

            if (ModifierChord.Releases(action))
            {
                releases++;
            }

            if (action is KeyInputAction { Kind: KeyActionKind.Down, Modifiers: HidModifier.None } key
                && !ModifierChord.IsModifier(key.Key))
            {
                otherKeyDown = true;
            }
        }

        if (presses > 0)
        {
            held += presses;
            anchor = actualTick;
        }

        if (otherKeyDown && held > 0)
        {
            anchor = actualTick;
        }

        held -= releases;
        if (held <= 0)
        {
            held = 0;
            anchor = 0;
        }
    }

    public static bool TrySplit(IReadOnlyList<InputAction> actions, out int splitAt)
    {
        splitAt = -1;
        var seenPress = false;
        for (var i = 0; i < actions.Count; i++)
        {
            if (ModifierChord.Presses(actions[i]))
            {
                seenPress = true;
            }

            if (seenPress && ModifierChord.Releases(actions[i]))
            {
                splitAt = i;
                return splitAt > 0;
            }
        }

        return false;
    }

    private static bool ReleasesHeldChord(IReadOnlyList<InputAction> actions)
    {
        foreach (var action in actions)
        {
            if (action is KeyInputAction { Kind: KeyActionKind.Up })
            {
                return true;
            }
        }

        return false;
    }
}

public readonly record struct PressIdentity(int Kind, int Code)
{
    public static bool TryGet(InputAction action, out PressIdentity identity, out bool isDown)
    {
        switch (action)
        {
            case KeyInputAction { Kind: KeyActionKind.Down, Key: not HidKey.None } key:
                identity = new(0, (int)key.Key);
                isDown = true;
                return true;
            case KeyInputAction { Kind: KeyActionKind.Up, Key: not HidKey.None } key:
                identity = new(0, (int)key.Key);
                isDown = false;
                return true;
            case MouseButtonInputAction { Kind: ButtonActionKind.Down, Button: not MouseButton.None } button:
                identity = new(1, (int)button.Button);
                isDown = true;
                return true;
            case MouseButtonInputAction { Kind: ButtonActionKind.Up, Button: not MouseButton.None } button:
                identity = new(1, (int)button.Button);
                isDown = false;
                return true;
            case ConsumerInputAction { Kind: ButtonActionKind.Down } consumer:
                identity = new(2, (int)consumer.Control);
                isDown = true;
                return true;
            case ConsumerInputAction { Kind: ButtonActionKind.Up } consumer:
                identity = new(2, (int)consumer.Control);
                isDown = false;
                return true;
            default:
                identity = default;
                isDown = false;
                return false;
        }
    }
}

public sealed class PressReleaseGapTracker
{
    private readonly Dictionary<PressIdentity, long> lastDownTicks = new();
    // Per key: when its pending (possibly floor-delayed) release is due. The same key may not be pressed again earlier.
    private readonly Dictionary<PressIdentity, long> releaseDueTicks = new();
    private readonly long zeroDelayHoldTicks;
    private readonly long modifierChordHoldTicks;
    private readonly bool earlierTimeline;
    private int modifiersHeld;

    public PressReleaseGapTracker(long qpcFrequency, InputPressFloor? floor = null)
    {
        var resolved = floor ?? InputPressFloor.Factory;
        earlierTimeline = PrecisionGenerations.UsesEarlierTimeline(resolved.Generation);
        zeroDelayHoldTicks = InputPressTiming.HoldTicks(resolved.ZeroGapHold, qpcFrequency);
        modifierChordHoldTicks = InputPressTiming.HoldTicks(resolved.ModifierChordHold, qpcFrequency);
    }

    /// <summary>
    /// Schedules an action on a timeline the user wrote. Unlike <see cref="AdjustDueTick(InputAction, long)"/>, the press floor
    /// delays only this one release: the caller's cursor (the user's timeline) does not move, so a wait written after it still
    /// counts from where the user put it ({LMB}{WAITMS:50}{LMB} is 50 ms apart). Order is kept per key, not globally: pressing
    /// the same key again waits for its pending release, other keys are untouched.
    /// </summary>
    /// <param name="cursorTick">Where the user's timeline says this action happens.</param>
    /// <param name="moveCursorToDue">
    /// True when the caller must also move its cursor to the returned tick: a modifier-chord release (later actions must not
    /// fire while the modifier is still held, or Ctrl+A then a click would become Ctrl+click) and a press that had to wait for
    /// its own key's release. False for the plain zero-gap hold, which delays only that release.
    /// </param>
    public long ScheduleDueTick(InputAction action, long cursorTick, out bool moveCursorToDue)
    {
        if (earlierTimeline)
        {
            // Generations before the timeline fix: the floor delays the release AND everything written after it. The cursor
            // always follows the returned tick, which is what the callers did when they used AdjustDueTick directly.
            var earlier = AdjustDueTick(action, cursorTick);
            moveCursorToDue = true;
            return earlier;
        }

        moveCursorToDue = false;
        if (PressIdentity.TryGet(action, out var identity, out var isDown) && isDown
            && releaseDueTicks.TryGetValue(identity, out var pendingRelease) && cursorTick < pendingRelease)
        {
            cursorTick = pendingRelease; // the same key's earlier release has not happened yet
            moveCursorToDue = true;
        }

        var modifiersHeldBeforeRelease = modifiersHeld; // AdjustDueTick updates the count; the chord test needs the value before
        var due = AdjustDueTick(action, cursorTick);
        if (PressIdentity.TryGet(action, out identity, out isDown) && !isDown)
        {
            releaseDueTicks[identity] = due;
            // Chord holds keep the old behaviour: everything after them moves with them.
            if (due > cursorTick && (modifiersHeldBeforeRelease > 0 || ModifierChord.Releases(action))) moveCursorToDue = true;
        }

        return due;
    }
    public long AdjustDueTick(InputAction action, long dueTick)
    {
        if (!PressIdentity.TryGet(action, out var identity, out var isDown))
        {
            return dueTick;
        }

        if (isDown)
        {
            lastDownTicks[identity] = dueTick;
            if (ModifierChord.Presses(action))
            {
                modifiersHeld++;
            }

            return dueTick;
        }

        if (lastDownTicks.TryGetValue(identity, out var downTick))
        {
            if (modifiersHeld > 0 || ModifierChord.Releases(action))
            {
                var earliest = downTick + modifierChordHoldTicks;
                if (dueTick < earliest)
                {
                    dueTick = earliest;
                }
            }
            else if (dueTick <= downTick)
            {
                dueTick = downTick + zeroDelayHoldTicks;
            }
        }

        if (ModifierChord.Releases(action))
        {
            modifiersHeld = Math.Max(0, modifiersHeld - 1);
        }

        lastDownTicks.Remove(identity);
        return dueTick;
    }
}
