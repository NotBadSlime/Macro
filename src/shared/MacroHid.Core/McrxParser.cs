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
        var id = root.TryGetProperty("id", out var idProperty)
            ? idProperty.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = null;
        }

        var kind = root.TryGetProperty("kind", out var kindProperty)
            ? ParseMacroKind(kindProperty.GetString())
            : MacroKind.Normal;

        return MacroStepNormalizer.Normalize(new MacroDocument(version, name, playback, steps, conditions, id, kind));
    }

    private static MacroKind ParseMacroKind(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "condition" or "conditionmacro" or "条件宏" => MacroKind.Condition,
            _ => MacroKind.Normal
        };
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
        var affinityMask = playbackElement.TryGetProperty("affinityMask", out var affinityMaskElement)
            ? PlaybackAffinityMask.NormalizeOrThrow(GetScalarString(affinityMaskElement))
            : PlaybackSettings.Default.AffinityMask;

        if (count < 1)
        {
            throw new JsonException("'playback.count' must be at least 1.");
        }

        return new PlaybackSettings(trigger, mode, count, processFilter.Trim(), precision, affinityMask);
    }

    public static HotkeyGesture ParseHotkeyGesture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException("Playback trigger value is required.");
        }

        var modifiers = HidModifier.None;
        var keys = new List<HidKey>();
        var mouseButtons = new List<MouseButton>();
        var trimmedValue = value.Trim();
        var includesNumpadPlusAlias = trimmedValue.EndsWith('+');
        foreach (var rawPart in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseModifier(rawPart, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (TryParseTriggerMouseButton(rawPart, out var parsedMouseButton))
            {
                if (!mouseButtons.Contains(parsedMouseButton))
                {
                    mouseButtons.Add(parsedMouseButton);
                }
                continue;
            }

            var parsedKey = ParseHidKey(rawPart);
            if (!keys.Contains(parsedKey))
            {
                keys.Add(parsedKey);
            }
        }

        if (includesNumpadPlusAlias && !keys.Contains(HidKey.NumpadPlus))
        {
            keys.Add(HidKey.NumpadPlus);
        }

        if (keys.Count == 0 && mouseButtons.Count == 0 && modifiers == HidModifier.None)
        {
            throw new JsonException($"Playback trigger '{value}' must include a key, mouse button, or modifier.");
        }

        return new HotkeyGesture(modifiers, keys, mouseButtons);
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
            "comment" => new CommentStep(GetString(stepElement, "text", string.Empty) ?? string.Empty),
            "mouse.move" => ParseMouseMove(stepElement),
            "mouse.down" => ParseMouseButton(stepElement, ButtonActionKind.Down),
            "mouse.up" => ParseMouseButton(stepElement, ButtonActionKind.Up),
            "mouse.click" => ParseMouseButton(stepElement, ButtonActionKind.Click),
            "mouse.wheel" => ParseMouseWheel(stepElement),
            "window.activate" => ParseWindowActivate(stepElement),
            "ocr.extract-text" => ParseOcrExtractText(stepElement),
            "ocr.click" => ParseOcrClick(stepElement),
            "consumer.down" => ParseConsumer(stepElement, ButtonActionKind.Down),
            "consumer.up" => ParseConsumer(stepElement, ButtonActionKind.Up),
            "consumer.tap" => ParseConsumer(stepElement, ButtonActionKind.Click),
            "wait" => ParseWait(stepElement),
            "repeat" => new RepeatStep(GetInt(stepElement, "count", 1), ParseSteps(stepElement.GetProperty("steps"))),
            "macro.call" => new MacroCallStep(GetString(stepElement, "macro", string.Empty) ?? string.Empty),
            "sequence.stop-current" => new StopCurrentSequenceStep(),
            "sequence.stop-iteration" => new StopCurrentIterationStep(),
            "sequence.stop-all" => new StopAllSequencesStep(),
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

    private static WindowActivateStep ParseWindowActivate(JsonElement stepElement)
    {
        return new WindowActivateStep(
            GetString(stepElement, "processName", string.Empty) ?? string.Empty,
            GetString(stepElement, "windowTitle", string.Empty) ?? string.Empty,
            stepElement.TryGetProperty("useTitleRegex", out var regexProperty) && regexProperty.GetBoolean(),
            Math.Max(1, GetInt(stepElement, "matchIndex", 1)),
            TimeSpan.FromMilliseconds(Math.Max(0, GetDouble(stepElement, "timeoutMs", 3000))),
            !stepElement.TryGetProperty("restore", out var restoreProperty) || restoreProperty.GetBoolean(),
            !stepElement.TryGetProperty("failIfNotFound", out var failProperty) || failProperty.GetBoolean());
    }

    private static OcrClickStep ParseOcrClick(JsonElement stepElement)
    {
        return new OcrClickStep(
            ParseScreenRegion(stepElement),
            GetString(stepElement, "expectedText", string.Empty) ?? string.Empty,
            !stepElement.TryGetProperty("contains", out var containsProperty) || containsProperty.GetBoolean(),
            GetString(stepElement, "language", "ch") ?? "ch",
            stepElement.TryGetProperty("useRegex", out var regexProperty) && regexProperty.GetBoolean(),
            ParseEnum<MouseButton>(GetString(stepElement, "button", "Left"), "OCR click mouse button"),
            Math.Clamp(GetInt(stepElement, "clickCount", 1), 1, 3),
            Math.Max(1, GetInt(stepElement, "matchIndex", 1)),
            TimeSpan.FromMilliseconds(Math.Max(0, GetDouble(stepElement, "holdMs", 20))),
            TimeSpan.FromMilliseconds(Math.Max(0, GetDouble(stepElement, "intervalMs", 80))),
            GetInt(stepElement, "offsetX", 0),
            GetInt(stepElement, "offsetY", 0));
    }

    private static OcrExtractTextStep ParseOcrExtractText(JsonElement stepElement)
    {
        return new OcrExtractTextStep(
            ParseScreenRegion(stepElement),
            GetString(stepElement, "pattern", string.Empty) ?? string.Empty,
            GetString(stepElement, "language", "ch") ?? "ch",
            !stepElement.TryGetProperty("useRegex", out var regexProperty) || regexProperty.GetBoolean(),
            Math.Max(1, GetInt(stepElement, "matchIndex", 1)),
            Math.Max(0, GetInt(stepElement, "captureGroup", 0)),
            GetString(stepElement, "filterTerms", string.Empty) ?? string.Empty,
            stepElement.TryGetProperty("keepDigitsOnly", out var digitsProperty) && digitsProperty.GetBoolean(),
            !stepElement.TryGetProperty("normalizeWhitespace", out var normalizeProperty) || normalizeProperty.GetBoolean(),
            !stepElement.TryGetProperty("failIfNotFound", out var failProperty) || failProperty.GetBoolean());
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

        var trimmed = value.Trim();
        if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
        {
            return ParseEnum<HidKey>($"D{trimmed}", "HID key");
        }

        if (TryParseHidKeyAlias(trimmed, out var alias))
        {
            return alias;
        }

        return ParseEnum<HidKey>(trimmed, "HID key");
    }

    private static bool TryParseHidKeyAlias(string value, out HidKey key)
    {
        key = value.ToLowerInvariant() switch
        {
            "-" or "_" or "oemminus" => HidKey.Minus,
            "=" or "oemplus" => HidKey.Equal,
            "[" or "{" or "oemopenbrackets" => HidKey.LeftBracket,
            "]" or "}" or "oemclosebrackets" => HidKey.RightBracket,
            "\\" or "|" or "oempipe" or "oem5" => HidKey.Backslash,
            ";" or ":" or "oemsemicolon" => HidKey.Semicolon,
            "'" or "\"" or "oemquotes" => HidKey.Quote,
            "`" or "~" or "oemtilde" => HidKey.Grave,
            "," or "<" or "oemcomma" => HidKey.Comma,
            "." or ">" or "oemperiod" => HidKey.Period,
            "/" or "?" or "oemquestion" => HidKey.Slash,
            "num0" or "kp0" or "keypad0" => HidKey.Numpad0,
            "num1" or "kp1" or "keypad1" => HidKey.Numpad1,
            "num2" or "kp2" or "keypad2" => HidKey.Numpad2,
            "num3" or "kp3" or "keypad3" => HidKey.Numpad3,
            "num4" or "kp4" or "keypad4" => HidKey.Numpad4,
            "num5" or "kp5" or "keypad5" => HidKey.Numpad5,
            "num6" or "kp6" or "keypad6" => HidKey.Numpad6,
            "num7" or "kp7" or "keypad7" => HidKey.Numpad7,
            "num8" or "kp8" or "keypad8" => HidKey.Numpad8,
            "num9" or "kp9" or "keypad9" => HidKey.Numpad9,
            "numadd" or "kpadd" or "keypadadd" => HidKey.NumpadPlus,
            "numsubtract" or "kpsubtract" or "keypadsubtract" => HidKey.NumpadMinus,
            "nummultiply" or "kpmultiply" or "keypadmultiply" or "*" => HidKey.NumpadMultiply,
            "numdivide" or "kpdivide" or "keypaddivide" => HidKey.NumpadDivide,
            "numdecimal" or "kpdecimal" or "keypaddecimal" => HidKey.NumpadDecimal,
            "numenter" or "kpenter" or "keypadenter" => HidKey.NumpadEnter,
            _ => HidKey.None
        };

        return key != HidKey.None;
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
            case "left":
            case "mouseleft":
            case "button1":
            case "mouse1":
                button = MouseButton.Left;
                return true;
            case "right":
            case "mouseright":
            case "button2":
            case "mouse2":
                button = MouseButton.Right;
                return true;
            case "middle":
            case "mousemiddle":
            case "button3":
            case "mouse3":
                button = MouseButton.Middle;
                return true;
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

    private static string? GetScalarString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Null => null,
            _ => throw new JsonException("Expected a string or number value.")
        };
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
        if (startStep < 0 || endStep < 0)
        {
            startStep = -1;
            endStep = -1;
            startPath = null;
            endPath = null;
        }
        var executionMode = elem.TryGetProperty("executionMode", out var executionModeProp)
            ? ParseEnum<ConditionExecutionMode>(executionModeProp.GetString(), "condition execution mode")
            : ConditionExecutionMode.Parallel;
        var timeBase = elem.TryGetProperty("timeBase", out var timeBaseProp)
            ? ParseEnum<ConditionTimeBase>(timeBaseProp.GetString(), "condition time base")
            : ConditionTimeBase.PlaybackTrigger;
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
            endPath,
            executionMode,
            timeBase);
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
                GetString(elem, "language", "ch") ?? "ch",
                elem.TryGetProperty("useRegex", out var regexProp) && regexProp.GetBoolean()),
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
