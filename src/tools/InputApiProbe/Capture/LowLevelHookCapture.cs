using MacroHid.Core;
using MacroStudio;

namespace InputApiProbe;

/// <summary>A0 baseline: the production GlobalKeyboardHook (low-level keyboard + mouse hook on an AboveNormal dispatcher thread).</summary>
internal sealed class LowLevelHookCapture(ushort vk) : ICaptureStrategy
{
    private GlobalKeyboardHook? hook;
    private volatile bool held;

    public string Id => "ll-hook";
    public event Action<long>? Captured;

    public void Start()
    {
        held = false;
        hook = new GlobalKeyboardHook { ProbeInjectedInputTag = new IntPtr((long)Native.Tag) };
        // Only the first down after an up counts: holding a key makes Windows repeat the down every ~31ms.
        hook.ProbeInputObserved += (key, down, tick) =>
        {
            if (key != vk) return;
            if (!down) { held = false; return; }
            if (held) return;
            held = true;
            Captured?.Invoke(tick);
        };
        hook.Start(new HotkeyGesture(HidModifier.None, HidKey.F8));
    }

    public void Stop() { hook?.Dispose(); hook = null; }
    public void Dispose() => Stop();
}
