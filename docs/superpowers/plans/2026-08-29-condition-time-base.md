# Condition Time Base Implementation Plan

> **For agentic workers:** Implement task-by-task. Steps use checkbox syntax.

**Goal:** Add a single dropdown time base for condition windows (playback / after previous / main iteration) with runtime support.

**Architecture:** Extend `ConditionalDirective` + mcrx; resolve elapsed against a per-iteration timeline shared by monitors; soft ComboBox in ConditionDirectivePanel.

**Tech Stack:** .NET 8, WPF MacroStudio, MacroHid.Core/Runtime

---

### Task 1: Model + mcrx
- [x] Add `ConditionTimeBase` and `TimeBase` on `ConditionalDirective`
- [x] Parse/serialize `timeBase`
- [x] Tests for round-trip default

### Task 2: Runtime timeline
- [x] Track playback/iteration/previous-finished ticks
- [x] ConditionMonitor waits/evaluates using resolved base
- [x] Gates honor TimeBase
- [x] Tests for AfterPrevious + first-condition fallback

### Task 3: UI
- [x] TimeBase combo + localized hints
- [x] Persist on change; show in range badge
- [x] Build-LocalRun
