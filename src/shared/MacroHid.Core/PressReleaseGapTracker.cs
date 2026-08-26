namespace MacroHid.Core;

public static class InputPressTiming
{
    public static readonly TimeSpan ZeroDelayHold = TimeSpan.FromMilliseconds(10);
    public static readonly TimeSpan DefaultPressReleaseGap = TimeSpan.FromMilliseconds(5);

    public static long ToTicks(TimeSpan duration, long qpcFrequency)
    {
        return Math.Max(0, (long)Math.Round(duration.TotalSeconds * qpcFrequency, MidpointRounding.AwayFromZero));
    }

    public static long ZeroDelayHoldTicks(long qpcFrequency)
    {
        return Math.Max(1, ToTicks(ZeroDelayHold, qpcFrequency));
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
    private readonly long zeroDelayHoldTicks;

    public PressReleaseGapTracker(long qpcFrequency)
    {
        zeroDelayHoldTicks = InputPressTiming.ZeroDelayHoldTicks(qpcFrequency);
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
            return dueTick;
        }

        if (lastDownTicks.TryGetValue(identity, out var downTick) && dueTick <= downTick)
        {
            dueTick = downTick + zeroDelayHoldTicks;
        }

        lastDownTicks.Remove(identity);
        return dueTick;
    }
}
