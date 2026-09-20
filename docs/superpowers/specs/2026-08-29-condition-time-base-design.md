# Condition Time Base Design

**Date:** 2026-08-29  
**Status:** Approved for implementation

## Summary

Each condition has one active time window base, chosen from a soft dropdown:

1. **PlaybackTrigger** — since play/trigger pressed for this run  
2. **AfterPreviousCondition** — since the previous condition in list order finished (match+then done, or left the poll loop); first condition falls back to MainIteration  
3. **MainIteration** — since the current main-sequence iteration started (resets each toggle/hold round)

Start/end remain optional ms fields (`WindowStart` / `WindowEnd`). Empty = open-ended on that side.

## Model

```csharp
public enum ConditionTimeBase
{
    PlaybackTrigger = 0,
    AfterPreviousCondition = 1,
    MainIteration = 2
}

// ConditionalDirective += ConditionTimeBase TimeBase = ConditionTimeBase.PlaybackTrigger
```

`.mcrx`: `"timeBase": "PlaybackTrigger" | "AfterPreviousCondition" | "MainIteration"` (default PlaybackTrigger for old files).

## Runtime

Shared per-iteration timeline:

- `playbackTriggerTick` — run/trigger start (before gates)  
- `mainIterationTick` — start of current iteration (after gates / loop restart)  
- `conditionFinishedTick[i]` — set when monitor `i` finishes its poll loop (then-actions included)

`ConditionMonitor` resolves `baseTick` from `TimeBase` + index; for `AfterPreviousCondition` waits until previous finished (or uses `mainIterationTick` if index 0).

Gates use the same `TimeBase` when evaluating `WindowStart`/`WindowEnd`.

## UI

Replace the fixed “触发后时间范围” label with:

- Dropdown: 按下播放 / 进入该条件 / 主序列本轮  
- Soft hint text switches with selection  
- Existing start/end ms boxes unchanged style

## Compatibility

Missing `timeBase` → PlaybackTrigger (current behavior).
