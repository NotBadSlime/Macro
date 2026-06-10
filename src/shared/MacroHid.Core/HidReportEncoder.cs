namespace MacroHid.Core;

public static class HidReportEncoder
{
    public const byte KeyboardReportId = 0x01;
    public const byte MouseReportId = 0x02;
    public const byte ConsumerReportId = 0x03;

    public static byte[] EncodeKeyboard(KeyStep step)
    {
        if (step.Kind == KeyActionKind.Up)
        {
            return [KeyboardReportId, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        }

        return
        [
            KeyboardReportId,
            (byte)step.Modifiers,
            0x00,
            (byte)step.Key,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00
        ];
    }

    public static byte[] EncodeMouseMove(MouseMoveStep step)
    {
        var x = ClampInt16(step.X);
        var y = ClampInt16(step.Y);

        return
        [
            MouseReportId,
            (byte)step.Buttons,
            (byte)(x & 0xFF),
            (byte)((x >> 8) & 0xFF),
            (byte)(y & 0xFF),
            (byte)((y >> 8) & 0xFF),
            0x00,
            0x00
        ];
    }

    public static byte[] EncodeMouseButton(MouseButtonStep step)
    {
        var buttons = step.Kind == ButtonActionKind.Up ? MouseButton.None : step.Button;

        return
        [
            MouseReportId,
            (byte)buttons,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00
        ];
    }

    public static byte[] EncodeMouseWheel(MouseWheelStep step)
    {
        return
        [
            MouseReportId,
            (byte)step.Buttons,
            0x00,
            0x00,
            0x00,
            0x00,
            ClampInt8(step.Vertical),
            ClampInt8(step.Horizontal)
        ];
    }

    public static byte[] EncodeConsumer(ConsumerControl control)
    {
        var usage = (ushort)control;
        return [ConsumerReportId, (byte)(usage & 0xFF), (byte)((usage >> 8) & 0xFF)];
    }

    public static byte[] EncodeReleaseAll()
    {
        return [KeyboardReportId, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    }

    private static short ClampInt16(int value)
    {
        return (short)Math.Clamp(value, short.MinValue, short.MaxValue);
    }

    private static byte ClampInt8(int value)
    {
        return unchecked((byte)Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue));
    }
}
