using System.Text.Json;

namespace MacroHid.Core;

public static class McrxParser
{
    public static MacroDocument Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var root = document.RootElement;
        var version = root.TryGetProperty("version", out var versionProperty) ? versionProperty.GetInt32() : 1;
        var name = root.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? "macro" : "macro";
        var playback = root.TryGetProperty("playback", out var playbackProperty)
            ? ParsePlayback(playbackProperty)
            : PlaybackSettings.Default;
        var steps = ParseSteps(root.GetProperty("steps"));

        return new MacroDocument(version, name, playback, steps);
    }

    private static PlaybackSettings ParsePlayback(JsonElement playbackElement)
    {
        if (playbackElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("'playback' must be an object.");
        }

        var trigger = playbackElement.TryGetProperty("trigger", out var triggerElement)
            ? ParseHotkeyGesture(triggerElement.GetString())
            : null;
        var mode = ParseEnum<PlaybackMode>(GetString(playbackElement, "mode", PlaybackSettings.Default.Mode.ToString()), "playback mode");
        var count = GetInt(playbackElement, "count", PlaybackSettings.Default.Count);

        if (count < 1)
        {
            throw new JsonException("'playback.count' must be at least 1.");
        }

        return new PlaybackSettings(trigger, mode, count);
    }

    public static HotkeyGesture ParseHotkeyGesture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException("Playback trigger value is required.");
        }

        var modifiers = HidModifier.None;
        HidKey? key = null;
        foreach (var rawPart in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseModifier(rawPart, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (key is not null)
            {
                throw new JsonException($"Playback trigger '{value}' contains more than one non-modifier key.");
            }

            key = ParseHidKey(rawPart);
        }

        if (key is null)
        {
            throw new JsonException($"Playback trigger '{value}' must include a non-modifier key.");
        }

        return new HotkeyGesture(modifiers, key.Value);
    }

    private static IReadOnlyList<MacroStep> ParseSteps(JsonElement stepsElement)
    {
        if (stepsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("'steps' must be an array.");
        }

        var steps = new List<MacroStep>();
        foreach (var stepElement in stepsElement.EnumerateArray())
        {
            steps.Add(ParseStep(stepElement));
        }

        return steps;
    }

    private static MacroStep ParseStep(JsonElement stepElement)
    {
        var type = stepElement.GetProperty("type").GetString();
        return type?.ToLowerInvariant() switch
        {
            "key.down" => ParseKey(stepElement, KeyActionKind.Down),
            "key.up" => ParseKey(stepElement, KeyActionKind.Up),
            "key.tap" => ParseKey(stepElement, KeyActionKind.Tap),
            "mouse.move" => ParseMouseMove(stepElement),
            "mouse.down" => ParseMouseButton(stepElement, ButtonActionKind.Down),
            "mouse.up" => ParseMouseButton(stepElement, ButtonActionKind.Up),
            "mouse.click" => ParseMouseButton(stepElement, ButtonActionKind.Click),
            "mouse.wheel" => ParseMouseWheel(stepElement),
            "consumer.down" => ParseConsumer(stepElement, ButtonActionKind.Down),
            "consumer.up" => ParseConsumer(stepElement, ButtonActionKind.Up),
            "consumer.tap" => ParseConsumer(stepElement, ButtonActionKind.Click),
            "wait" => new WaitStep(TimeSpan.FromMilliseconds(GetInt(stepElement, "ms", 0))),
            "repeat" => new RepeatStep(GetInt(stepElement, "count", 1), ParseSteps(stepElement.GetProperty("steps"))),
            "pixel.when" => ParsePixelWhen(stepElement),
            _ => throw new JsonException($"Unsupported macro step type '{type}'.")
        };
    }

    private static KeyStep ParseKey(JsonElement stepElement, KeyActionKind kind)
    {
        var key = ParseHidKey(stepElement.GetProperty("key").GetString());
        var modifiers = HidModifier.None;
        if (stepElement.TryGetProperty("modifiers", out var modifiersElement))
        {
            foreach (var modifierElement in modifiersElement.EnumerateArray())
            {
                modifiers |= ParseEnum<HidModifier>(modifierElement.GetString(), "modifier");
            }
        }

        return new KeyStep(
            kind,
            key,
            modifiers,
            TimeSpan.FromMilliseconds(GetInt(stepElement, "holdMs", 0)));
    }

    private static MouseMoveStep ParseMouseMove(JsonElement stepElement)
    {
        var mode = ParseEnum<MouseMoveMode>(GetString(stepElement, "mode", "relative"), "mouse move mode");
        var buttons = MouseButton.None;
        if (stepElement.TryGetProperty("buttons", out var buttonsElement))
        {
            foreach (var buttonElement in buttonsElement.EnumerateArray())
            {
                buttons |= ParseEnum<MouseButton>(buttonElement.GetString(), "mouse button");
            }
        }

        return new MouseMoveStep(
            mode,
            GetInt(stepElement, "x", 0),
            GetInt(stepElement, "y", 0),
            TimeSpan.FromMilliseconds(GetInt(stepElement, "durationMs", 0)),
            buttons);
    }

    private static MouseButtonStep ParseMouseButton(JsonElement stepElement, ButtonActionKind kind)
    {
        return new MouseButtonStep(
            ParseEnum<MouseButton>(stepElement.GetProperty("button").GetString(), "mouse button"),
            kind,
            TimeSpan.FromMilliseconds(GetInt(stepElement, "holdMs", 0)));
    }

    private static MouseWheelStep ParseMouseWheel(JsonElement stepElement)
    {
        return new MouseWheelStep(
            GetInt(stepElement, "vertical", 0),
            GetInt(stepElement, "horizontal", 0),
            ParseMouseButtons(stepElement));
    }

    private static ConsumerStep ParseConsumer(JsonElement stepElement, ButtonActionKind kind)
    {
        return new ConsumerStep(
            ParseEnum<ConsumerControl>(stepElement.GetProperty("control").GetString(), "consumer control"),
            kind,
            TimeSpan.FromMilliseconds(GetInt(stepElement, "holdMs", 0)));
    }

    private static PixelWhenStep ParsePixelWhen(JsonElement stepElement)
    {
        var scope = ParseEnum<CoordinateScope>(GetString(stepElement, "scope", "screen"), "coordinate scope");
        var coordinate = new PixelCoordinate(
            scope,
            GetInt(stepElement, "x", 0),
            GetInt(stepElement, "y", 0),
            GetString(stepElement, "windowTitle", null));

        var condition = new PixelCondition(
            coordinate,
            new RgbColor(
                checked((byte)GetInt(stepElement, "r", 0)),
                checked((byte)GetInt(stepElement, "g", 0)),
                checked((byte)GetInt(stepElement, "b", 0))),
            checked((byte)GetInt(stepElement, "tolerance", 0)));

        return new PixelWhenStep(condition, ParseSteps(stepElement.GetProperty("then")));
    }

    private static HidKey ParseHidKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException("HID key value is required.");
        }

        if (value.Length == 1 && char.IsDigit(value[0]))
        {
            return ParseEnum<HidKey>($"D{value}", "HID key");
        }

        return ParseEnum<HidKey>(value, "HID key");
    }

    private static bool TryParseModifier(string value, out HidModifier modifier)
    {
        switch (value.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
            case "leftctrl":
                modifier = HidModifier.LeftCtrl;
                return true;
            case "rightctrl":
                modifier = HidModifier.RightCtrl;
                return true;
            case "shift":
            case "leftshift":
                modifier = HidModifier.LeftShift;
                return true;
            case "rightshift":
                modifier = HidModifier.RightShift;
                return true;
            case "alt":
            case "leftalt":
                modifier = HidModifier.LeftAlt;
                return true;
            case "rightalt":
                modifier = HidModifier.RightAlt;
                return true;
            case "win":
            case "gui":
            case "leftwin":
            case "leftgui":
                modifier = HidModifier.LeftGui;
                return true;
            case "rightwin":
            case "rightgui":
                modifier = HidModifier.RightGui;
                return true;
            default:
                modifier = HidModifier.None;
                return false;
        }
    }

    private static MouseButton ParseMouseButtons(JsonElement stepElement)
    {
        var buttons = MouseButton.None;
        if (!stepElement.TryGetProperty("buttons", out var buttonsElement))
        {
            return buttons;
        }

        foreach (var buttonElement in buttonsElement.EnumerateArray())
        {
            buttons |= ParseEnum<MouseButton>(buttonElement.GetString(), "mouse button");
        }

        return buttons;
    }

    private static TEnum ParseEnum<TEnum>(string? value, string label)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            throw new JsonException($"Unsupported {label} '{value}'.");
        }

        return parsed;
    }

    private static int GetInt(JsonElement element, string name, int defaultValue)
    {
        return element.TryGetProperty(name, out var property) ? property.GetInt32() : defaultValue;
    }

    private static string? GetString(JsonElement element, string name, string? defaultValue)
    {
        return element.TryGetProperty(name, out var property) ? property.GetString() : defaultValue;
    }
}
