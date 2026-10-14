using MacroHid.Core;
using MacroStudio;

namespace InputApiProbe;

/// <summary>A0 baseline: the production GlobalKeyboardHook (low-level keyboard + mouse hook on an AboveNormal dispatcher thread).</summary>
internal sealed class LowLevelHookCapture(ushort vk) : ICaptureStrategy
{
    private GlobalKeyboardHook? hook;

    public string Id => "ll-hook";
    public event Action<long>? Captured;

    public void Start()
    {
        hook = new GlobalKeyboardHook { ProbeInjectedInputTag = new IntPtr((long)Native.Tag) };
        hook.ProbeInputObserved += (key, down, tick) => { if (key == vk && down) Captured?.Invoke(tick); };
        hook.Start(new HotkeyGesture(HidModifier.None, HidKey.F8));
    }

    public void Stop() { hook?.Dispose(); hook = null; }
    public void Dispose() => Stop();
}
