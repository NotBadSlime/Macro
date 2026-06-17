using System.Diagnostics;
using MacroHid.Core;

namespace MacroStudio.Controls;

public sealed class ActionTemplateInsertGate
{
    private readonly TimeSpan duplicateWindow;
    private MacroActionTemplateKind? lastKind;
    private long lastAcceptedTicks;

    public ActionTemplateInsertGate()
        : this(TimeSpan.FromMilliseconds(250))
    {
    }

    public ActionTemplateInsertGate(TimeSpan duplicateWindow)
    {
        this.duplicateWindow = duplicateWindow;
    }

    public bool TryAccept(MacroActionTemplateKind kind)
    {
        return TryAccept(kind, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
    }

    public bool TryAccept(MacroActionTemplateKind kind, long nowTicks, long frequency)
    {
        if (frequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequency), "Frequency must be positive.");
        }

        if (lastKind == kind)
        {
            var elapsed = TimeSpan.FromSeconds((double)(nowTicks - lastAcceptedTicks) / frequency);
            if (elapsed >= TimeSpan.Zero && elapsed <= duplicateWindow)
            {
                return false;
            }
        }

        lastKind = kind;
        lastAcceptedTicks = nowTicks;
        return true;
    }
}
