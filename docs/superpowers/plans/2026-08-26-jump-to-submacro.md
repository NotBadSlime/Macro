# Jump to Submacro Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users double-click or right-click a `macro.call` step to open that submacro for editing, with autosave and a back stack.

**Architecture:** `StepSequencePanel` raises navigation events; `MainWindow` owns a `Stack<string>` of prior macro ids, resolves via `MatchesReference`, calls existing `SaveActiveEditorBeforeSwitch` + `LoadMacroFromLibrary`, and clears the stack on manual library selection.

**Tech Stack:** C# / .NET 8 WPF, existing `MacroLibraryItem.MatchesReference`, `MacroStepTreeEditor`, resx strings, `tests/MacroHid.Core.Tests`.

**Spec:** `docs/superpowers/specs/2026-08-26-jump-to-submacro-design.md`

---

## File map

| File | Responsibility |
|------|----------------|
| `StepSequencePanel.xaml(.cs)` | Double-click, context menu, back button, events |
| `SequencePanel.xaml.cs` | Forward events if needed |
| `MainWindow.xaml.cs` | Navigation stack + resolve/save/load |
| `Strings*.resx` | Localized labels/messages |
| `tests/.../Program.cs` | Source assertions |

### Task 1: Strings + panel events + MainWindow stack

- [ ] Add resx keys: EnterSubmacro, ReturnPreviousMacro, SubmacroNotFound, AlreadyEditingMacro, ReturnMacroMissing
- [ ] StepSequencePanel: `OpenReferencedMacroRequested`, `ReturnPreviousMacroRequested`, back button visibility API
- [ ] Double-click on MacroCallStep (not drag); context menu item when single MacroCall selected
- [ ] MainWindow: stack, handlers, clear on manual SelectMacro / new macro
- [ ] Tests asserting event names / menu / stack clear paths
- [ ] Run tests; build local-run

---
