using MacroHid.Core;

namespace MacroHid.Runtime;

/// <summary>
/// Shared clocks for condition time windows within one playback run / iteration.
/// </summary>
public sealed class ConditionTimeline
{
    private readonly long[] finishedTicks;
    private readonly object gate = new();

    public ConditionTimeline(int conditionCount, long playbackTriggerTick, long mainIterationTick)
    {
        finishedTicks = new long[Math.Max(0, conditionCount)];
        PlaybackTriggerTick = playbackTriggerTick;
        MainIterationTick = mainIterationTick;
    }

    public long PlaybackTriggerTick { get; }

    public long MainIterationTick { get; private set; }

    public void SetMainIterationTick(long tick) => MainIterationTick = tick;

    public void MarkFinished(int conditionIndex, long tick)
    {
        if (conditionIndex < 0 || conditionIndex >= finishedTicks.Length)
        {
            return;
        }

        lock (gate)
        {
            if (finishedTicks[conditionIndex] == 0)
            {
                finishedTicks[conditionIndex] = tick;
            }
        }
    }

    public bool TryGetFinishedTick(int conditionIndex, out long tick)
    {
        tick = 0;
        if (conditionIndex < 0 || conditionIndex >= finishedTicks.Length)
        {
            return false;
        }

        lock (gate)
        {
            tick = finishedTicks[conditionIndex];
            return tick != 0;
        }
    }

    public long ResolveBaseTick(int conditionIndex, ConditionTimeBase timeBase)
    {
        return timeBase switch
        {
            ConditionTimeBase.MainIteration => MainIterationTick,
            ConditionTimeBase.AfterPreviousCondition when conditionIndex <= 0 => MainIterationTick,
            ConditionTimeBase.AfterPreviousCondition
                when TryGetFinishedTick(conditionIndex - 1, out var previous) => previous,
            ConditionTimeBase.AfterPreviousCondition => 0,
            _ => PlaybackTriggerTick
        };
    }
}
