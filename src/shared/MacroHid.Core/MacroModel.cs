namespace MacroHid.Core;

public sealed record MacroDocument(
    int Version,
    string Name,
    PlaybackSettings Playback,
    IReadOnlyList<MacroStep> Steps,
    IReadOnlyList<ConditionalDirective>? Conditions = null)
{
    public MacroDocument(int Version, string Name, IReadOnlyList<MacroStep> Steps)
        : this(Version, Name, PlaybackSettings.Default, Steps, null)
    {
    }

    public IReadOnlyList<ConditionalDirective> EffectiveConditions =>
        Conditions ?? Array.Empty<ConditionalDirective>();
}

public sealed record PlaybackSettings(
    HotkeyGesture? Trigger,
    PlaybackMode Mode,
    int Count,
    string ProcessFilter = "",
    PrecisionMode Precision = PrecisionMode.ExtremeDuringPlayback)
{
    public static PlaybackSettings Default { get; } = new(
        null,
        PlaybackMode.FixedCount,
        1,
        string.Empty,
        PrecisionMode.ExtremeDuringPlayback);
}

public static class PlaybackProcessFilter
{
    public static bool Matches(string? filter, string? processName)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var normalizedProcess = Normalize(processName);
        return filter
            .Split([',', ';', '|', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Any(candidate => string.Equals(candidate, normalizedProcess, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string value)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }
}

public sealed record HotkeyGesture(HidModifier Modifiers, HidKey Key, MouseButton MouseButton = MouseButton.None)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & (HidModifier.LeftCtrl | HidModifier.RightCtrl)) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & (HidModifier.LeftShift | HidModifier.RightShift)) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & (HidModifier.LeftAlt | HidModifier.RightAlt)) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & (HidModifier.LeftGui | HidModifier.RightGui)) != 0)
        {
            parts.Add("Win");
        }

        if (Key != HidKey.None)
        {
            parts.Add(Key.ToString());
        }
        else if (MouseButton != MouseButton.None)
        {
            parts.Add(MouseButton.ToString());
        }

        return string.Join("+", parts);
    }
}

public enum PlaybackMode
{
    ToggleLoop,
    HoldLoop,
    FixedCount
}

public enum PrecisionMode
{
    Balanced,
    ExtremeDuringPlayback
}

public abstract record MacroStep;

public sealed record KeyStep(
    KeyActionKind Kind,
    HidKey Key,
    HidModifier Modifiers,
    TimeSpan Hold) : MacroStep;

public sealed record TextStep(string Text) : MacroStep;

public sealed record MouseMoveStep(
    MouseMoveMode Mode,
    int X,
    int Y,
    TimeSpan Duration,
    MouseButton Buttons = MouseButton.None) : MacroStep;

public sealed record MouseButtonStep(
    MouseButton Button,
    ButtonActionKind Kind,
    TimeSpan Hold,
    MouseMoveMode? CoordinateMode = null,
    int? X = null,
    int? Y = null) : MacroStep
{
    public bool HasCoordinate => X.HasValue && Y.HasValue;
}

public sealed record MouseWheelStep(
    int Vertical,
    int Horizontal,
    MouseButton Buttons = MouseButton.None) : MacroStep;

public sealed record ConsumerStep(
    ConsumerControl Control,
    ButtonActionKind Kind,
    TimeSpan Hold) : MacroStep;

public sealed record WaitStep(TimeSpan Duration, TimeSpan? MaxDuration = null) : MacroStep
{
    public TimeSpan MinDuration => Duration;
    public bool IsRandom => MaxDuration is { } max && max > Duration;

    public TimeSpan Sample(Random random)
    {
        if (!IsRandom || MaxDuration is not { } max)
        {
            return Duration;
        }

        var minTicks = Duration.Ticks;
        var maxTicks = max.Ticks;
        if (maxTicks <= minTicks)
        {
            return Duration;
        }

        var offset = (long)Math.Round(random.NextDouble() * (maxTicks - minTicks), MidpointRounding.AwayFromZero);
        return TimeSpan.FromTicks(minTicks + offset);
    }
}

public sealed record RepeatStep(int Count, IReadOnlyList<MacroStep> Steps) : MacroStep;

public sealed record MacroCallStep(string Macro) : MacroStep;

public sealed record PixelWhenStep(
    PixelCondition Condition,
    IReadOnlyList<MacroStep> ThenSteps,
    TimeSpan? WindowStart = null,
    TimeSpan? WindowEnd = null,
    TimeSpan? PollInterval = null) : MacroStep
{
    public TimeSpan EffectivePollInterval => PollInterval is { } value && value > TimeSpan.Zero
        ? value
        : TimeSpan.FromMilliseconds(25);
}

public enum KeyActionKind
{
    Down,
    Up,
    Tap
}

public enum MouseMoveMode
{
    Relative,
    Absolute
}

public enum ButtonActionKind
{
    Down,
    Up,
    Click
}

[Flags]
public enum HidModifier : byte
{
    None = 0x00,
    LeftCtrl = 0x01,
    LeftShift = 0x02,
    LeftAlt = 0x04,
    LeftGui = 0x08,
    RightCtrl = 0x10,
    RightShift = 0x20,
    RightAlt = 0x40,
    RightGui = 0x80
}

[Flags]
public enum MouseButton : byte
{
    None = 0x00,
    Left = 0x01,
    Right = 0x02,
    Middle = 0x04,
    X1 = 0x08,
    X2 = 0x10,
    Button6 = 0x20,
    Button7 = 0x40,
    Button8 = 0x80
}
