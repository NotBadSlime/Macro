using MacroHid.Core;

namespace MacroHid.Runtime;

/// <summary>
/// Max precision (2.1): 极限 plus preemption. While enabled, every 极限 native run and warm-up asks the native scheduler for
/// time-critical workers in a realtime-class process with MMCSS critical, spinning through every wait (no timer sleeps), and
/// the managed playback thread runs time-critical too. Game frame rate may drop: the two worker cores are taken.
/// Hot standby keeps those workers spinning between runs while a Max hotkey is listening, so a hotkey does not wait for them.
/// </summary>
public static class MaxPreemption
{
    private static int enabled;

    public static bool Enabled => Volatile.Read(ref enabled) != 0;

    /// <summary>Set before warming up for 极限. Off restores the process priority class at the next warm-up.</summary>
    public static void Set(bool on) => Volatile.Write(ref enabled, on ? 1 : 0);

    internal static uint Flags(PrecisionMode precision) =>
        Enabled && precision == PrecisionMode.UltraLowJitter ? MhpFlagMaxPreemption : 0u;

    internal const uint MhpFlagMaxPreemption = 2;

    /// <summary>Idle workers spin while a Max hotkey listens. Ignored by the engine unless a Max profile is warm.</summary>
    public static void SetHotStandby(bool on)
    {
        Volatile.Write(ref hotStandby, on && Enabled ? 1 : 0);
        try { NativePlaybackInterop.MhpSetHotStandby(on && Enabled ? 1 : 0); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
    }

    private static int hotStandby;
    private static int powerHoldUnavailable;

    /// <summary>
    /// The native power guard holds Max (display off/dimmed, suspend or Modern Standby, and 120 s after returning): the
    /// workers then behave like 极限, and the managed side does not spin or run time-critical either.
    /// </summary>
    internal static bool PowerHeld
    {
        get
        {
            if (Volatile.Read(ref powerHoldUnavailable) != 0) return false;
            try { return NativePlaybackInterop.MhpGetPowerHold() != 0; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                Volatile.Write(ref powerHoldUnavailable, 1);
                return false;
            }
        }
    }

    /// <summary>Max hot standby is on: the next idle managed playback worker spins for its next run instead of blocking.</summary>
    internal static bool HotStandby => Volatile.Read(ref hotStandby) != 0 && !PowerHeld;

    /// <summary>Max preemption is in force for a run now (enabled and not held by the power guard).</summary>
    internal static bool Live => Enabled && !PowerHeld;
}
