# Startup Gate Condition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `GateMainSequence` condition mode that delays main sequence until gate matchers succeed (AND), without running ThenSteps.

**Architecture:** Extend `ConditionExecutionMode`; executor waits on gate conditions after trigger, then starts main playback and non-gate monitors as today.

**Tech Stack:** C# / .NET 8, existing ConditionMonitor matchers, WPF ConditionDirectivePanel, MacroHid.Core.Tests.

**Spec:** `docs/superpowers/specs/2026-08-29-startup-gate-condition-design.md`

---

## File map

| File | Change |
|------|--------|
| `ConditionModel.cs` | Add `GateMainSequence` |
| `McrxParser` / `McrxSerializer` | Already enum-based; verify |
| `MacroPlaybackExecutor.cs` | Pre-wait gates before main run |
| `ConditionDirectivePanel` | Combo item + hide Then UI |
| `Strings*.resx` | Labels |
| `tests/.../Program.cs` | Assertions + runtime test if feasible |

### Tasks

1. Model + UI + strings
2. Executor gate wait (reuse matcher evaluation)
3. Tests + local build

---
