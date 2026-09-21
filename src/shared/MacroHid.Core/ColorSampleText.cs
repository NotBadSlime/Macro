namespace MacroHid.Core;

public static class ColorSampleText
{
    public static string Format(int x, int y, RgbColor color) =>
        $"X={x} Y={y} 坐标  色号：#{color.R:X2}{color.G:X2}{color.B:X2}";
}

public static class CoreHotkeys
{
    public const string ColorSampleId = "__core.colorSample";

    public static HotkeyGesture DefaultColorSample { get; } = new(HidModifier.None, HidKey.UpArrow);

    public static string DefaultColorSampleText => DefaultColorSample.ToString();

    public static HotkeyGesture ParseOrDefault(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultColorSample;
        }

        try
        {
            return McrxParser.ParseHotkeyGesture(text);
        }
        catch
        {
            return DefaultColorSample;
        }
    }

    public static string NormalizeText(string? text) => ParseOrDefault(text).ToString();

    public static bool GesturesEqual(HotkeyGesture left, HotkeyGesture right) =>
        string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
}
