using System.Runtime.CompilerServices;

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

    internal static bool IsInputRelease(InputAction action) => action is
        KeyInputAction { Kind: KeyActionKind.Up } or
        MouseButtonInputAction { Kind: ButtonActionKind.Up, Button: not MouseButton.None } or
        ConsumerInputAction { Kind: ButtonActionKind.Up };
}

internal struct ModifierChordState
{
    private const int ConsumerUsageOffset = 256;
    private const int UsageCapacity = 1024;

    // Hold state can be copied while previewing an ordered batch without changing live state.
    [InlineArray(UsageCapacity / 64)]
    private struct HeldUsages
    {
        private ulong bits;
    }

    private HeldUsages usages;
    private HidModifier modifiers;
    private MouseButton mouseButtons;

    public bool IsActive { get; private set; }

    // A modifier begins a chord only when another input is newly pressed while it is held.
    public bool Observe(InputAction action)
    {
        bool chordPress = action switch
        {
            KeyInputAction key => ObserveKeyboard(key),
            MouseButtonInputAction button => ObserveMouse(button),
            ConsumerInputAction consumer => ObserveConsumer(consumer),
            _ => false
        };
        if (chordPress) IsActive = true;
        return chordPress;
    }

    private bool ObserveKeyboard(KeyInputAction key)
    {
        bool down = key.Kind == KeyActionKind.Down;
        if (key.Modifiers == HidModifier.None) return ObserveKey(key.Key, down);

        // Match SendInput's packet order for implicit modifiers and their primary key.
        if (down)
        {
            bool chordPress = false;
            for (var i = 0; i < 8; i++)
                if ((key.Modifiers & (HidModifier)(1 << i)) != 0)
                    chordPress |= ObserveKey((HidKey)((int)HidKey.LeftControl + i), true);
            return ObserveKey(key.Key, true) | chordPress;
        }

        ObserveKey(key.Key, false);
        for (var i = 7; i >= 0; i--)
            if ((key.Modifiers & (HidModifier)(1 << i)) != 0)
                ObserveKey((HidKey)((int)HidKey.LeftControl + i), false);
        return false;
    }

    private bool ObserveKey(HidKey key, bool down)
    {
        if (key == HidKey.None) return false;

        bool partner = TrackUsage((int)key, down) && modifiers != HidModifier.None;
        if (!ModifierChord.IsModifier(key)) return partner;

        var bit = (HidModifier)(1 << (key - HidKey.LeftControl));
        if (down)
        {
            modifiers |= bit;
        }
        else
        {
            modifiers &= ~bit;
            if (modifiers == HidModifier.None) IsActive = false;
        }
        return partner;
    }

    private bool ObserveMouse(MouseButtonInputAction button)
    {
        bool partner = (button.Button & ~mouseButtons) != MouseButton.None && modifiers != HidModifier.None;
        mouseButtons = button.Kind == ButtonActionKind.Down ? mouseButtons | button.Button : mouseButtons & ~button.Button;
        return button.Kind != ButtonActionKind.Up && partner;
    }

    private bool ObserveConsumer(ConsumerInputAction consumer)
    {
        int usage = ConsumerUsageOffset + (int)consumer.Control;
        bool pressed = TrackUsage(usage, consumer.Kind != ButtonActionKind.Up);
        if (consumer.Kind == ButtonActionKind.Click) TrackUsage(usage, false);
        return pressed && modifiers != HidModifier.None;
    }

    private bool TrackUsage(int usage, bool down)
    {
        if ((uint)usage >= UsageCapacity) return false;

        ulong mask = 1UL << (usage % 64);
        ref ulong word = ref usages[usage / 64];
        bool pressed = down && (word & mask) == 0;
        word = down ? word | mask : word & ~mask;
        return pressed;
    }
}

public sealed class ModifierChordClock
{
    private ModifierChordState chord;
    private long anchor;

    public long AdjustDueTick(IReadOnlyList<InputAction> actions, long dueTick, long holdTicks)
    {
        if (!chord.IsActive || anchor <= 0 || !ReleasesHeldChord(actions))
        {
            return dueTick;
        }

        var earliest = anchor + holdTicks;
        return dueTick < earliest ? earliest : dueTick;
    }

    public void NoteSent(IReadOnlyList<InputAction> actions, long actualTick)
    {
        foreach (var action in actions)
        {
            if (chord.Observe(action)) anchor = actualTick;
        }

        if (!chord.IsActive) anchor = 0;
    }

    public static bool TrySplit(IReadOnlyList<InputAction> actions, out int splitAt) => TrySplit(actions, default, out splitAt);

    public bool TrySplitActiveBatch(IReadOnlyList<InputAction> actions, out int splitAt) => TrySplit(actions, chord, out splitAt);

    private static bool TrySplit(IReadOnlyList<InputAction> actions, ModifierChordState state, out int splitAt)
    {
        splitAt = -1;
        if (actions.Count < 2) return false;

        bool chordPressInBatch = false;
        for (var i = 0; i < actions.Count; i++)
        {
            if (chordPressInBatch && ModifierChord.IsInputRelease(actions[i]))
            {
                splitAt = i;
                return true;
            }

            chordPressInBatch |= state.Observe(actions[i]);
        }

        return false;
    }

    private static bool ReleasesHeldChord(IReadOnlyList<InputAction> actions)
    {
        foreach (var action in actions)
        {
            if (ModifierChord.IsInputRelease(action))
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
    private ModifierChordState chord;

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

        bool chordBeforeRelease = chord.IsActive;
        var due = AdjustDueTick(action, cursorTick);
        if (PressIdentity.TryGet(action, out identity, out isDown) && !isDown)
        {
            releaseDueTicks[identity] = due;
            // Chord holds keep the old behaviour: everything after them moves with them.
            if (due > cursorTick && chordBeforeRelease) moveCursorToDue = true;
        }

        return due;
    }
    public long AdjustDueTick(InputAction action, long dueTick)
    {
        bool chordBeforeAction = chord.IsActive;
        chord.Observe(action);
        if (!PressIdentity.TryGet(action, out var identity, out var isDown))
        {
            return dueTick;
        }

        if (isDown)
        {
            lastDownTicks[identity] = dueTick;
            return dueTick;
        }

        if (lastDownTicks.TryGetValue(identity, out var downTick))
        {
            if (chordBeforeAction)
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

        lastDownTicks.Remove(identity);
        return dueTick;
    }
}
