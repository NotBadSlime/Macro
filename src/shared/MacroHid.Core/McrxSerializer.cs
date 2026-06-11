using System.Text.Json;
using System.Text.Json.Nodes;

namespace MacroHid.Core;

public static class McrxSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static string Serialize(MacroDocument document)
    {
        var root = new JsonObject
        {
            ["version"] = document.Version,
            ["name"] = document.Name,
            ["playback"] = SerializePlayback(document.Playback),
            ["steps"] = SerializeSteps(document.Steps)
        };

        return root.ToJsonString(Options);
    }

    private static JsonObject SerializePlayback(PlaybackSettings playback)
    {
        var result = new JsonObject
        {
            ["mode"] = playback.Mode switch
            {
                PlaybackMode.ToggleLoop => "toggleLoop",
                PlaybackMode.HoldLoop => "holdLoop",
                _ => "fixedCount"
            },
            ["count"] = playback.Count
        };

        if (playback.Trigger is not null)
        {
            result["trigger"] = playback.Trigger.ToString();
        }

        return result;
    }

    private static JsonArray SerializeSteps(IReadOnlyList<MacroStep> steps)
    {
        var result = new JsonArray();
        foreach (var step in steps)
        {
            result.Add(SerializeStep(step));
        }

        return result;
    }

    private static JsonObject SerializeStep(MacroStep step)
    {
        return step switch
        {
            KeyStep key => SerializeKey(key),
            TextStep text => new JsonObject
            {
                ["type"] = "key.text",
                ["text"] = text.Text
            },
            MouseMoveStep move => SerializeMouseMove(move),
            MouseButtonStep button => SerializeMouseButton(button),
            MouseWheelStep wheel => SerializeMouseWheel(wheel),
            ConsumerStep consumer => SerializeConsumer(consumer),
            WaitStep wait => new JsonObject
            {
                ["type"] = "wait",
                ["ms"] = ToMilliseconds(wait.Duration)
            },
            RepeatStep repeat => new JsonObject
            {
                ["type"] = "repeat",
                ["count"] = repeat.Count,
                ["steps"] = SerializeSteps(repeat.Steps)
            },
            PixelWhenStep pixel => SerializePixelWhen(pixel),
            _ => throw new NotSupportedException($"Unsupported macro step '{step.GetType().Name}'.")
        };
    }

    private static JsonObject SerializeKey(KeyStep step)
    {
        var result = new JsonObject
        {
            ["type"] = step.Kind switch
            {
                KeyActionKind.Down => "key.down",
                KeyActionKind.Up => "key.up",
                _ => "key.tap"
            },
            ["key"] = step.Key.ToString()
        };

        if (step.Modifiers != HidModifier.None)
        {
            var modifiers = new JsonArray();
            foreach (var modifier in Enum.GetValues<HidModifier>())
            {
                if (modifier != HidModifier.None && (step.Modifiers & modifier) == modifier)
                {
                    modifiers.Add(modifier.ToString());
                }
            }

            result["modifiers"] = modifiers;
        }

        if (step.Hold > TimeSpan.Zero)
        {
            result["holdMs"] = ToMilliseconds(step.Hold);
        }

        return result;
    }

    private static JsonObject SerializeMouseMove(MouseMoveStep step)
    {
        var result = new JsonObject
        {
            ["type"] = "mouse.move",
            ["mode"] = step.Mode.ToString().ToLowerInvariant(),
            ["x"] = step.X,
            ["y"] = step.Y
        };

        if (step.Duration > TimeSpan.Zero)
        {
            result["durationMs"] = ToMilliseconds(step.Duration);
        }

        AddButtons(result, step.Buttons);
        return result;
    }

    private static JsonObject SerializeMouseButton(MouseButtonStep step)
    {
        var result = new JsonObject
        {
            ["type"] = step.Kind switch
            {
                ButtonActionKind.Down => "mouse.down",
                ButtonActionKind.Up => "mouse.up",
                _ => "mouse.click"
            },
            ["button"] = step.Button.ToString()
        };

        if (step.Hold > TimeSpan.Zero)
        {
            result["holdMs"] = ToMilliseconds(step.Hold);
        }

        return result;
    }

    private static JsonObject SerializeMouseWheel(MouseWheelStep step)
    {
        var result = new JsonObject
        {
            ["type"] = "mouse.wheel",
            ["vertical"] = step.Vertical,
            ["horizontal"] = step.Horizontal
        };
        AddButtons(result, step.Buttons);
        return result;
    }

    private static JsonObject SerializeConsumer(ConsumerStep step)
    {
        var result = new JsonObject
        {
            ["type"] = step.Kind switch
            {
                ButtonActionKind.Down => "consumer.down",
                ButtonActionKind.Up => "consumer.up",
                _ => "consumer.tap"
            },
            ["control"] = step.Control.ToString()
        };

        if (step.Hold > TimeSpan.Zero)
        {
            result["holdMs"] = ToMilliseconds(step.Hold);
        }

        return result;
    }

    private static JsonObject SerializePixelWhen(PixelWhenStep step)
    {
        return new JsonObject
        {
            ["type"] = "pixel.when",
            ["scope"] = step.Condition.Coordinate.Scope.ToString().ToLowerInvariant(),
            ["x"] = step.Condition.Coordinate.X,
            ["y"] = step.Condition.Coordinate.Y,
            ["windowTitle"] = step.Condition.Coordinate.WindowTitle,
            ["r"] = step.Condition.Expected.R,
            ["g"] = step.Condition.Expected.G,
            ["b"] = step.Condition.Expected.B,
            ["tolerance"] = step.Condition.Tolerance,
            ["then"] = SerializeSteps(step.ThenSteps)
        };
    }

    private static void AddButtons(JsonObject target, MouseButton buttons)
    {
        if (buttons == MouseButton.None)
        {
            return;
        }

        var array = new JsonArray();
        foreach (var button in Enum.GetValues<MouseButton>())
        {
            if (button != MouseButton.None && (buttons & button) == button)
            {
                array.Add(button.ToString());
            }
        }

        target["buttons"] = array;
    }

    private static int ToMilliseconds(TimeSpan duration)
    {
        return (int)Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero);
    }
}
