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
    PrecisionMode Precision = PrecisionMode.ExtremeDuringPlayback,
    string AffinityMask = "")
{
    public static PlaybackSettings Default { get; } = new(
        null,
        PlaybackMode.FixedCount,
        1,
        string.Empty,
        PrecisionMode.ExtremeDuringPlayback,
        string.Empty);
}

public static class PlaybackAffinityMask
{
    public static bool TryParse(string? value, out ulong mask)
    {
        mask = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        try
        {
            mask = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt64(trimmed[2..], 16)
                : Convert.ToUInt64(trimmed, 10);
            return mask != 0 && mask <= long.MaxValue;
        }
        catch
        {
            mask = 0;
            return false;
        }
    }

    public static string NormalizeOrThrow(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!TryParse(value, out var mask))
        {
            throw new FormatException("Playback affinity mask must be a non-zero 64-bit hex or decimal CPU mask.");
        }

        return $"0x{mask:X}";
    }
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

public static class MacroLibraryActivationFilter
{
    public static bool Matches(string? groupProcessFilter, string? processName)
    {
        return PlaybackProcessFilter.Matches(groupProcessFilter, processName);
    }

    public static bool FiltersOverlap(string? leftGroupProcessFilter, string? rightGroupProcessFilter)
    {
        var left = NormalizeProcessFilterParts(leftGroupProcessFilter);
        var right = NormalizeProcessFilterParts(rightGroupProcessFilter);
        if (left.Count == 0 || right.Count == 0)
        {
            return true;
        }

        return left.Overlaps(right);
    }

    private static HashSet<string> NormalizeProcessFilterParts(string? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var token in value.Split([',', ';', '|', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = token.Trim();
            if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^4];
            }

            if (!string.IsNullOrWhiteSpace(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}

public sealed class HotkeyGesture : IEquatable<HotkeyGesture>
{
    public HotkeyGesture(HidModifier modifiers, HidKey key, MouseButton mouseButton = MouseButton.None)
        : this(
            modifiers,
            key == HidKey.None ? [] : [key],
            mouseButton == MouseButton.None ? [] : [mouseButton])
    {
    }

    public HotkeyGesture(
        HidModifier modifiers,
        IEnumerable<HidKey>? keys,
        IEnumerable<MouseButton>? mouseButtons = null)
    {
        Modifiers = modifiers;
        Keys = NormalizeKeys(keys);
        MouseButtons = NormalizeMouseButtons(mouseButtons);
    }

    public HidModifier Modifiers { get; }

    public HidKey Key => Keys.Count > 0 ? Keys[0] : HidKey.None;

    public MouseButton MouseButton => MouseButtons.Count > 0 ? MouseButtons[0] : MacroHid.Core.MouseButton.None;

    public IReadOnlyList<HidKey> Keys { get; }

    public IReadOnlyList<MouseButton> MouseButtons { get; }

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

        foreach (var button in MouseButtons)
        {
            parts.Add(button.ToString());
        }

        foreach (var key in Keys)
        {
            parts.Add(key.ToString());
        }

        return string.Join("+", parts);
    }

    public bool Equals(HotkeyGesture? other)
    {
        return other is not null
            && Modifiers == other.Modifiers
            && Keys.SequenceEqual(other.Keys)
            && MouseButtons.SequenceEqual(other.MouseButtons);
    }

    public override bool Equals(object? obj)
    {
        return obj is HotkeyGesture other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Modifiers);
        foreach (var key in Keys)
        {
            hash.Add(key);
        }

        foreach (var button in MouseButtons)
        {
            hash.Add(button);
        }

        return hash.ToHashCode();
    }

    private static IReadOnlyList<HidKey> NormalizeKeys(IEnumerable<HidKey>? keys)
    {
        return (keys ?? [])
            .Where(key => key != HidKey.None)
            .Distinct()
            .ToArray();
    }

    private static IReadOnlyList<MouseButton> NormalizeMouseButtons(IEnumerable<MouseButton>? buttons)
    {
        return (buttons ?? [])
            .Where(button => button != MouseButton.None)
            .Distinct()
            .ToArray();
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
    ExtremeDuringPlayback,
    UltraLowJitter
}

public sealed record PrecisionModeProfile(
    PrecisionMode Mode,
    string StableName,
    int TargetJitterMicroseconds,
    bool UsesNativeEngine);

public static class PrecisionModeProfiles
{
    public static PrecisionModeProfile ForMode(PrecisionMode mode)
    {
        return mode switch
        {
            PrecisionMode.Balanced => new PrecisionModeProfile(
                mode,
                StableName: "basic",
                TargetJitterMicroseconds: 500,
                UsesNativeEngine: false),
            PrecisionMode.UltraLowJitter => new PrecisionModeProfile(
                mode,
                StableName: "extreme",
                TargetJitterMicroseconds: 100,
                UsesNativeEngine: true),
            _ => new PrecisionModeProfile(
                PrecisionMode.ExtremeDuringPlayback,
                StableName: "highPerformance",
                TargetJitterMicroseconds: 250,
                UsesNativeEngine: false)
        };
    }
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
