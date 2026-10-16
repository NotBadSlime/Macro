using System.Globalization;

namespace MacroHid.Core;

/// <summary>
/// Hold times are stored in microseconds but edited in milliseconds (the unit every delay in the editor uses).
/// Up to three decimals, so any whole-microsecond value round-trips.
/// </summary>
public static class HoldTimeText
{
    public const long MaxMilliseconds = InputPressTiming.MaxHoldMicroseconds / 1000;

    public static string FormatMilliseconds(long microseconds)
    {
        var ms = microseconds / 1000m;
        return ms.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Accepts "10", "10.5", "0.25" (either '.' or ','); rejects negatives, more than three decimals and over the maximum.</summary>
    public static bool TryParseMilliseconds(string? text, out long microseconds)
    {
        microseconds = 0;
        var trimmed = (text ?? string.Empty).Trim().Replace(',', '.');
        if (trimmed.Length == 0
            || !decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var ms)
            || ms < 0
            || ms > MaxMilliseconds
            || decimal.Round(ms, 3) != ms)
        {
            return false;
        }

        microseconds = (long)(ms * 1000m);
        return true;
    }
}
