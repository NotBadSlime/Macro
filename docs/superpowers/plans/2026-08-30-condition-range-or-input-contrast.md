# Condition Range OR + Input Contrast Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow empty step ranges on conditions, treat step range and time window as OR (two intervals, gap inactive), and make time/expected-text inputs more visible.

**Architecture:** Model empty step range as `StartStepIndex/EndStepIndex = -1` with null paths. Stop intersecting step-derived and explicit windows in `ApplyConditionTimeWindow`; instead resolve a list of active intervals and teach `ConditionMonitor` / gate wait to poll only inside any interval. UI adds「不限」combo items, validation, stronger TextBox style, and OR hints.

**Tech Stack:** .NET 8, WPF MacroStudio, MacroHid.Core/Runtime, MacroHid.Core.Tests

**Spec:** `docs/superpowers/specs/2026-08-30-condition-range-or-input-contrast-design.md`

---

### Task 1: Model + mcrx empty step range
- [x] Add `HasStepRange` helper on `ConditionalDirective` (`StartStepIndex >= 0 && EndStepIndex >= 0`)
- [x] Parser: missing/negative start/end → -1 and null paths; serializer omits or writes -1 when no range
- [x] Tests: round-trip empty step range; old files still parse

### Task 2: Runtime OR intervals
- [x] Replace AND merge in `ApplyConditionTimeWindow` with interval list builder (step-only / time-only / both / neither)
- [x] Update `ConditionMonitor` wait/eval to use intervals (enter next start; expire after all ends)
- [x] Gate wait path honors same intervals
- [x] Tests: non-overlapping OR; only-step; only-time; both-empty rejected or no-op

### Task 3: UI empty step + OR copy + input contrast
- [x] 「不限」items in step combos; clear → -1; both-empty validation
- [x] Hint + RangeBadge OR wording (zh-CN/zh-TW/en)
- [x] Stronger style on `WindowStartMsBox`, `WindowEndMsBox`, `ExpectedTextBox`
- [x] File/smoke tests; Build-LocalRun

---
