using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed class PreparedInputBatch
{
    private PreparedInputBatch(
        IReadOnlyList<InputAction> actions,
        IReadOnlyList<SendInputPacket> packets,
        NativeInput[] nativeInputs)
    {
        Actions = actions;
        Packets = packets;
        NativeInputs = nativeInputs;
    }

    public IReadOnlyList<InputAction> Actions { get; }

    public IReadOnlyList<SendInputPacket> Packets { get; }

    public int ActionCount => Actions.Count;

    public int NativeInputCount => NativeInputs.Length;

    internal NativeInput[] NativeInputs { get; }

    public static PreparedInputBatch FromActions(
        IReadOnlyList<InputAction> actions,
        VirtualDesktopBounds? virtualDesktop = null)
    {
        var bounds = virtualDesktop ?? SendInputEncoder.GetVirtualDesktopBounds();
        var copiedActions = actions.ToArray();
        var packets = new List<SendInputPacket>();
        foreach (var action in copiedActions)
        {
            packets.AddRange(SendInputEncoder.Encode(action, bounds));
        }

        var nativeInputs = new NativeInput[packets.Count];
        for (var i = 0; i < packets.Count; i++)
        {
            nativeInputs[i] = ToNativeInput(packets[i]);
        }

        return new PreparedInputBatch(copiedActions, packets, nativeInputs);
    }

    internal static NativeInput ToNativeInput(SendInputPacket packet)
    {
        if (packet.Kind == SendInputPacketKind.Keyboard)
        {
            return new NativeInput
            {
                Type = RuntimeNativeMethods.InputKeyboard,
                Union = new NativeInputUnion
                {
                    Keyboard = new NativeKeyboardInput
                    {
                        VirtualKey = packet.VirtualKey,
                        ScanCode = packet.ScanCode,
                        Flags = packet.Flags
                    }
                }
            };
        }

        return new NativeInput
        {
            Type = RuntimeNativeMethods.InputMouse,
            Union = new NativeInputUnion
            {
                Mouse = new NativeMouseInput
                {
                    Dx = packet.MouseX,
                    Dy = packet.MouseY,
                    MouseData = packet.MouseData,
                    Flags = packet.Flags
                }
            }
        };
    }
}
