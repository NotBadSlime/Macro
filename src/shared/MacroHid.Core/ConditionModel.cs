namespace MacroHid.Core;

public sealed record ScreenPoint(int X, int Y);

public sealed record ScreenRegion(
    ScreenPoint TopLeft,
    ScreenPoint TopRight,
    ScreenPoint BottomRight,
    ScreenPoint BottomLeft)
{
    public static ScreenRegion FromSinglePixel(int x, int y)
    {
        var p = new ScreenPoint(x, y);
        return new ScreenRegion(p, new ScreenPoint(x + 1, y), new ScreenPoint(x + 1, y + 1), new ScreenPoint(x, y + 1));
    }

    public static ScreenRegion FromRect(int left, int top, int right, int bottom)
    {
        return new ScreenRegion(
            new ScreenPoint(left, top),
            new ScreenPoint(right, top),
            new ScreenPoint(right, bottom),
            new ScreenPoint(left, bottom));
    }

    public int Width => Math.Max(Math.Abs(TopRight.X - TopLeft.X), 1);
    public int Height => Math.Max(Math.Abs(BottomLeft.Y - TopLeft.Y), 1);
}

public enum ConflictBehavior
{
    Warn,
    Skip,
    Force
}

public interface IConditionMatcher
{
    string Type { get; }
}

public sealed record PixelMatcher(
    ScreenRegion Region,
    RgbColor Expected,
    byte Tolerance) : IConditionMatcher
{
    public string Type => "pixel";
}

public sealed record TemplateMatcher(
    ScreenRegion Region,
    byte[] TemplateImageData,
    double Threshold = 0.85) : IConditionMatcher
{
    public string Type => "template";
}

public sealed record PixelHashMatcher(
    ScreenRegion Region,
    byte[] ReferenceHash,
    double SimilarityThreshold = 0.9) : IConditionMatcher
{
    public string Type => "pixelHash";
}

public sealed record TextMatcher(
    ScreenRegion Region,
    string ExpectedText,
    bool Contains = true,
    string Language = "ch") : IConditionMatcher
{
    public string Type => "text";
}

public sealed record ConditionalDirective(
    string Id,
    string Name,
    int StartStepIndex,
    int EndStepIndex,
    IConditionMatcher Condition,
    IReadOnlyList<MacroStep> ThenSteps,
    ConflictBehavior OnConflict = ConflictBehavior.Warn,
    TimeSpan? PollInterval = null,
    TimeSpan? WindowStart = null,
    TimeSpan? WindowEnd = null,
    IReadOnlyList<int>? StartStepPath = null,
    IReadOnlyList<int>? EndStepPath = null)
{
    public TimeSpan EffectivePollInterval => PollInterval is { } value && value > TimeSpan.Zero
        ? value
        : TimeSpan.FromMilliseconds(25);

    public string StartStepPathText => FormatPath(StartStepPath);
    public string EndStepPathText => FormatPath(EndStepPath);
    public bool HasStepPaths => StartStepPath is { Count: > 0 } && EndStepPath is { Count: > 0 };

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    private static string FormatPath(IReadOnlyList<int>? path)
    {
        return path is { Count: > 0 }
            ? string.Join(".", path)
            : string.Empty;
    }
}
