using System.Text;

namespace MacroHid.Runtime;

public static class OcrDisplayText
{
    public static string Format(OcrRecognitionResult result, int maxLength = 160)
    {
        var text = FormatRaw(result.Text, result.Boxes);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength
            ? text
            : text[..(maxLength - 1)] + "…";
    }

    private static string FormatRaw(string text, IReadOnlyList<OcrTextBox>? boxes)
    {
        if (boxes is { Count: > 0 })
        {
            var parts = new List<string>();
            foreach (var box in boxes.OrderBy(box => box.Top).ThenBy(box => box.Left))
            {
                var candidate = box.Text.Trim();
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (IsLikelyNoiseToken(candidate))
                {
                    continue;
                }

                if (parts.Any(existing => existing.Contains(candidate, StringComparison.Ordinal)))
                {
                    continue;
                }

                parts.Add(candidate);
            }

            if (parts.Count > 0)
            {
                return CollapseWhitespace(string.Join(' ', parts));
            }
        }

        return CollapseWhitespace(text);
    }

    private static bool IsLikelyNoiseToken(string value)
    {
        if (value.Length == 1 && (char.IsAsciiLetterOrDigit(value[0]) || char.IsDigit(value[0])))
        {
            return true;
        }

        return value.Length <= 2
            && value.All(ch => char.IsDigit(ch) || char.IsWhiteSpace(ch));
    }

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        return builder.ToString();
    }
}
