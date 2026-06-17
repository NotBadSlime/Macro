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
        var conditions = root.TryGetProperty("conditions", out var conditionsProperty)
            ? ParseConditions(conditionsProperty)
            : null;

        return MacroStepNormalizer.Normalize(new MacroDocument(version, name, playback, steps, conditions));
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
        var processFilter = GetString(playbackElement, "processFilter", PlaybackSettings.Default.ProcessFilter) ?? string.Empty;
        var precision = ParseEnum<PrecisionMode>(
            GetString(playbackElement, "precision", PlaybackSettings.Default.Precision.ToString()),
            "precision mode");

        if (count < 1)
        {
            throw new JsonException("'playback.count' must be at least 1.");
        }

        return new PlaybackSettings(trigger, mode, count, processFilter.Trim(), precision);
    }

    public static HotkeyGesture ParseHotkeyGesture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException("Playback trigger value is required.");
        }

        var modifiers = HidModifier.None;
        HidKey? key = null;
        MouseButton mouseButton = MouseButton.None;
        foreach (var rawPart in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseModifier(rawPart, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (TryParseTriggerMouseButton(rawPart, out var parsedMouseButton))
            {
                if (mouseButton != MouseButton.None || key is not null)
                {
                    throw new JsonException($"Playback trigger '{value}' contains more than one non-modifier key or mouse button.");
                }

                mouseButton = parsedMouseButton;
                continue;
            }

            if (key is not null)
            {
                throw new JsonException($"Playback trigger '{value}' contains more than one non-modifier key.");
            }

            key = ParseHidKey(rawPart);
        }

        if (key is null && mouseButton == MouseButton.None && modifiers == HidModifier.None)
        {
            throw new JsonException($"Playback trigger '{value}' must include a key, mouse button, or modifier.");
        }

        return new HotkeyGesture(modifiers, key ?? HidKey.None, mouseButton);
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
            "key.text" => new TextStep(GetString(stepElement, "text", string.Empty) ?? string.Empty),
            "mouse.move" => ParseMouseMove(stepElement),
            "mouse.down" => ParseMouseButton(stepElement, ButtonActionKind.Down),
            "mouse.up" => ParseMouseButton(stepElement, ButtonActionKind.Up),
            "mouse.click" => ParseMouseButton(stepElement, ButtonActionKind.Click),
            "mouse.wheel" => ParseMouseWheel(stepElement),
            "consumer.down" => ParseConsumer(stepElement, ButtonActionKind.Down),
            "consumer.up" => ParseConsumer(stepElement, ButtonActionKind.Up),
            "consumer.tap" => ParseConsumer(stepElement, ButtonActionKind.Click),
            "wait" => ParseWait(stepElement),
            "repeat" => new RepeatStep(GetInt(stepElement, "count", 1), ParseSteps(stepElement.GetProperty("steps"))),
            "macro.call" => new MacroCallStep(GetString(stepElement, "macro", string.Empty) ?? string.Empty),
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
            TimeSpan.FromMilliseconds(GetDouble(stepElement, "holdMs", 0)));
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
            TimeSpan.FromMilliseconds(GetDouble(stepElement, "durationMs", 0)),
            buttons);
    }

    private static MouseButtonStep ParseMouseButton(JsonElement stepElement, ButtonActionKind kind)
    {
        var hasX = stepElement.TryGetProperty("x", out var xProperty);
        var hasY = stepElement.TryGetProperty("y", out var yProperty);
        return new MouseButtonStep(
            ParseEnum<MouseButton>(stepElement.GetProperty("button").GetString(), "mouse button"),
            kind,
            TimeSpan.FromMilliseconds(GetDouble(stepElement, "holdMs", 0)),
            hasX && hasY
                ? ParseEnum<MouseMoveMode>(GetString(stepElement, "mode", "absolute"), "mouse button coordinate mode")
                : null,
            hasX ? xProperty.GetInt32() : null,
            hasY ? yProperty.GetInt32() : null);
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
            TimeSpan.FromMilliseconds(GetDouble(stepElement, "holdMs", 0)));
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

        return new PixelWhenStep(
            condition,
            ParseSteps(stepElement.GetProperty("then")),
            GetOptionalTimeSpan(stepElement, "windowStartMs"),
            GetOptionalTimeSpan(stepElement, "windowEndMs"),
            GetOptionalTimeSpan(stepElement, "pollIntervalMs"));
    }

    private static WaitStep ParseWait(JsonElement stepElement)
    {
        if (stepElement.TryGetProperty("minMs", out var minProperty)
            || stepElement.TryGetProperty("maxMs", out _))
        {
            var minMs = minProperty.ValueKind != JsonValueKind.Undefined
                ? minProperty.GetDouble()
                : GetDouble(stepElement, "ms", 0);
            var maxMs = GetDouble(stepElement, "maxMs", minMs);
            if (maxMs < minMs)
            {
                throw new JsonException("'wait.maxMs' must be greater than or equal to 'wait.minMs'.");
            }

            return new WaitStep(
                TimeSpan.FromMilliseconds(minMs),
                TimeSpan.FromMilliseconds(maxMs));
        }

        return new WaitStep(TimeSpan.FromMilliseconds(GetDouble(stepElement, "ms", 0)));
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

    private static bool TryParseTriggerMouseButton(string value, out MouseButton button)
    {
        switch (value.ToLowerInvariant())
        {
            case "x1":
            case "mousex1":
            case "button4":
            case "mouse4":
                button = MouseButton.X1;
                return true;
            case "x2":
            case "mousex2":
            case "button5":
            case "mouse5":
                button = MouseButton.X2;
                return true;
            default:
                button = MouseButton.None;
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

    private static double GetDouble(JsonElement element, string name, double defaultValue)
    {
        return element.TryGetProperty(name, out var property) ? property.GetDouble() : defaultValue;
    }

    private static TimeSpan? GetOptionalTimeSpan(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property)
            ? TimeSpan.FromMilliseconds(property.GetDouble())
            : null;
    }

    private static string? GetString(JsonElement element, string name, string? defaultValue)
    {
        return element.TryGetProperty(name, out var property) ? property.GetString() : defaultValue;
    }

    private static IReadOnlyList<ConditionalDirective> ParseConditions(JsonElement conditionsElement)
    {
        if (conditionsElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("'conditions' must be an array.");

        var conditions = new List<ConditionalDirective>();
        foreach (var elem in conditionsElement.EnumerateArray())
            conditions.Add(ParseCondition(elem));
        return conditions;
    }

    private static ConditionalDirective ParseCondition(JsonElement elem)
    {
        var id = GetString(elem, "id", null) ?? ConditionalDirective.NewId();
        var name = GetString(elem, "name", null) ?? "Condition";
        var startStep = GetInt(elem, "startStep", 0);
        var endStep = GetInt(elem, "endStep", 0);
        var onConflict = elem.TryGetProperty("onConflict", out var conflictProp)
            ? ParseEnum<ConflictBehavior>(conflictProp.GetString(), "conflict behavior")
            : ConflictBehavior.Warn;
        var pollInterval = GetOptionalTimeSpan(elem, "pollMs");
        var windowStart = GetOptionalTimeSpan(elem, "windowStartMs");
        var windowEnd = GetOptionalTimeSpan(elem, "windowEndMs");
        var startPath = ParseOptionalStepPath(elem, "startPath");
        var endPath = ParseOptionalStepPath(elem, "endPath");
        var thenSteps = elem.TryGetProperty("then", out var thenProp)
            ? ParseSteps(thenProp)
            : Array.Empty<MacroStep>();
        var condition = ParseConditionMatcher(elem);

        return new ConditionalDirective(
            id,
            name,
            startStep,
            endStep,
            condition,
            thenSteps,
            onConflict,
            pollInterval,
            windowStart,
            windowEnd,
            startPath,
            endPath);
    }

    private static IReadOnlyList<int>? ParseOptionalStepPath(JsonElement elem, string name)
    {
        if (!elem.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return ParseStepPathText(value);
        }

        if (property.ValueKind == JsonValueKind.Array)
        {
            var path = new List<int>();
            foreach (var part in property.EnumerateArray())
            {
                var index = part.GetInt32();
                if (index < 0)
                {
                    throw new JsonException($"'{name}' cannot contain negative indexes.");
                }

                path.Add(index);
            }

            return path.Count > 0 ? path : null;
        }

        throw new JsonException($"'{name}' must be a dot-separated string or an array of indexes.");
    }

    private static IReadOnlyList<int> ParseStepPathText(string value)
    {
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var index) || index < 0)
            {
                throw new JsonException($"Unsupported step path '{value}'.");
            }

            result.Add(index);
        }

        if (result.Count == 0)
        {
            throw new JsonException($"Unsupported step path '{value}'.");
        }

        return result;
    }

    private static IConditionMatcher ParseConditionMatcher(JsonElement elem)
    {
        var type = GetString(elem, "type", "pixel") ?? "pixel";
        var region = ParseScreenRegion(elem);

        return type.ToLowerInvariant() switch
        {
            "pixel" => new PixelMatcher(
                region,
                new RgbColor(
                    checked((byte)GetInt(elem, "r", 0)),
                    checked((byte)GetInt(elem, "g", 0)),
                    checked((byte)GetInt(elem, "b", 0))),
                checked((byte)GetInt(elem, "tolerance", 10))),
            "template" => new TemplateMatcher(
                region,
                ParseBase64(elem, "templateData"),
                GetDouble(elem, "threshold", 0.85)),
            "pixelHash" => new PixelHashMatcher(
                region,
                ParseBase64(elem, "referenceHash"),
                GetDouble(elem, "similarity", 0.9)),
            "text" => new TextMatcher(
                region,
                GetString(elem, "expectedText", "") ?? "",
                !elem.TryGetProperty("contains", out var containsProp) || containsProp.GetBoolean(),
                GetString(elem, "language", "ch") ?? "ch"),
            _ => throw new JsonException($"Unsupported condition type '{type}'.")
        };
    }

    private static ScreenRegion ParseScreenRegion(JsonElement elem)
    {
        if (elem.TryGetProperty("region", out var regionProp))
        {
            return new ScreenRegion(
                ParseScreenPoint(regionProp, "topLeft"),
                ParseScreenPoint(regionProp, "topRight"),
                ParseScreenPoint(regionProp, "bottomRight"),
                ParseScreenPoint(regionProp, "bottomLeft"));
        }

        var x = GetInt(elem, "x", 0);
        var y = GetInt(elem, "y", 0);
        return ScreenRegion.FromSinglePixel(x, y);
    }

    private static ScreenPoint ParseScreenPoint(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var pointProp))
            return new ScreenPoint(0, 0);
        return new ScreenPoint(GetInt(pointProp, "x", 0), GetInt(pointProp, "y", 0));
    }

    private static byte[] ParseBase64(JsonElement elem, string name)
    {
        if (elem.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var str = prop.GetString();
            return string.IsNullOrEmpty(str) ? [] : Convert.FromBase64String(str);
        }
        return [];
    }
}
