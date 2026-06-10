namespace MacroHid.Core;

public enum CoordinateScope
{
    Screen,
    Window
}

public sealed record RgbColor(byte R, byte G, byte B);

public sealed class PixelCoordinate
{
    public PixelCoordinate(CoordinateScope scope, int x, int y, string? WindowTitle = null)
    {
        Scope = scope;
        X = x;
        Y = y;
        this.WindowTitle = WindowTitle;
    }

    public CoordinateScope Scope { get; }

    public int X { get; }

    public int Y { get; }

    public string? WindowTitle { get; }
}

public sealed class PixelSample
{
    public PixelSample(int x, int y, RgbColor color)
    {
        X = x;
        Y = y;
        Color = color;
    }

    public int X { get; }

    public int Y { get; }

    public RgbColor Color { get; }
}

public sealed class PixelCondition
{
    public PixelCondition(PixelCoordinate coordinate, RgbColor expected, byte tolerance)
    {
        Coordinate = coordinate;
        Expected = expected;
        Tolerance = tolerance;
    }

    public PixelCoordinate Coordinate { get; }

    public RgbColor Expected { get; }

    public byte Tolerance { get; }

    public bool Matches(PixelSample sample)
    {
        return Math.Abs(sample.Color.R - Expected.R) <= Tolerance
            && Math.Abs(sample.Color.G - Expected.G) <= Tolerance
            && Math.Abs(sample.Color.B - Expected.B) <= Tolerance;
    }
}
