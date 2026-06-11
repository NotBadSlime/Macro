using System.ComponentModel;
using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed record InputSubmissionStats(
    int ActionsSubmitted,
    int NativeInputsSubmitted,
    int FailedSubmissions,
    int LastWin32Error,
    long LastSubmitQpc);

public sealed class SendInputMacroSink : IMacroInputSink
{
    private readonly object gate = new();
    private int actionsSubmitted;
    private int nativeInputsSubmitted;
    private int failedSubmissions;
    private int lastWin32Error;
    private long lastSubmitQpc;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public void Submit(uint sequence, InputAction action)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("SendInput is only available on Windows.");
        }

        var packets = SendInputEncoder.Encode(action, SendInputEncoder.GetVirtualDesktopBounds());
        RuntimeNativeMethods.QueryPerformanceCounter(out var submitQpc);

        if (packets.Count == 0)
        {
            RecordSuccess(nativeInputCount: 0, submitQpc);
            return;
        }

        var inputs = packets.Select(ToNativeInput).ToArray();
        var sent = RuntimeNativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeInput>());
        if (sent != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            RecordFailure(submitQpc, error);
            throw new Win32Exception(error, $"SendInput submitted {sent} of {inputs.Length} native inputs.");
        }

        RecordSuccess(inputs.Length, submitQpc);
    }

    public InputSubmissionStats? GetStats()
    {
        lock (gate)
        {
            return new InputSubmissionStats(
                actionsSubmitted,
                nativeInputsSubmitted,
                failedSubmissions,
                lastWin32Error,
                lastSubmitQpc);
        }
    }

    private void RecordSuccess(int nativeInputCount, long submitQpc)
    {
        lock (gate)
        {
            actionsSubmitted++;
            nativeInputsSubmitted += nativeInputCount;
            lastWin32Error = 0;
            lastSubmitQpc = submitQpc;
        }
    }

    private void RecordFailure(long submitQpc, int error)
    {
        lock (gate)
        {
            actionsSubmitted++;
            failedSubmissions++;
            lastWin32Error = error;
            lastSubmitQpc = submitQpc;
        }
    }

    private static NativeInput ToNativeInput(SendInputPacket packet)
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
