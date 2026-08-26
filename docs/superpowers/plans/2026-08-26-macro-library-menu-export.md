# Macro Library Menu & Export Wizard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Simplify the macro library toolbar/context menus and replace always-visible export format with a two-step export wizard that can package a folder plus nested `macro.call` dependencies as two subfolders or a ZIP.

**Architecture:** Keep UI changes inside `MacroLibraryPanel`. Put dependency collection and bundle layout in `MacroHid.Core` (`MacroCallReferenceCollector`, `MacroLibraryExportBundle`) so they are unit-tested without WPF. Add `ExportWizardDialog` for format (+ optional packaging) then reuse `MacroConversionService.ExportFromMcrx` to write files.

**Tech Stack:** C# / .NET 8, WPF, existing `MacroLibraryStore` / `MacroConversionService`, `System.IO.Compression.ZipFile`, project test runner `tests/MacroHid.Core.Tests`.

**Spec:** `docs/superpowers/specs/2026-08-26-macro-library-menu-export-design.md`

---

## File map

| File | Responsibility |
|------|----------------|
| `src/shared/MacroHid.Core/MacroCallReferenceCollector.cs` | Walk steps/conditions; collect `macro.call` reference strings |
| `src/shared/MacroHid.Core/MacroLibraryExportBundle.cs` | Build primary macros + external dependency sets for folder/multi export |
| `src/ui/MacroStudio/Controls/ExportWizardDialog.xaml(.cs)` | Two-step themed dialog: format → packaging |
| `src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml(.cs)` | Toolbar, context menus, export entry wiring |
| `src/ui/MacroStudio/Resources/Strings*.resx` | New menu/wizard strings |
| `docs/usage.md` | Short user-facing export note |
| `tests/MacroHid.Core.Tests/Program.cs` | Unit + UI source assertions |

---

### Task 1: Collect `macro.call` references

**Files:**
- Create: `src/shared/MacroHid.Core/MacroCallReferenceCollector.cs`
- Modify: `tests/MacroHid.Core.Tests/Program.cs`

- [ ] **Step 1: Add failing test**

In `tests/MacroHid.Core.Tests/Program.cs`, register and implement:

```csharp
("Macro call reference collector walks nested structures", MacroCallReferenceCollectorWalksNestedStructures),
```

```csharp
static void MacroCallReferenceCollectorWalksNestedStructures()
{
    var document = new MacroDocument(
        1,
        "Main",
        PlaybackSettings.Default,
        [
            new MacroCallStep("child-id"),
            new RepeatStep(2, [new MacroCallStep("loop-child")]),
            new PixelWhenStep(
                new PixelCondition(new PixelCoordinate(CoordinateScope.Screen, 1, 2), new RgbColor(1, 2, 3), 4),
                [new MacroCallStep("pixel-child")])
        ],
        [
            new ConditionalDirective(
                "c1",
                "cond",
                0,
                0,
                new PixelMatcher(ScreenRegion.FromSinglePixel(1, 1), new RgbColor(1, 2, 3), 4),
                [new MacroCallStep("cond-child")])
        ]);

    var refs = MacroCallReferenceCollector.Collect(document);
    Assert.Equal(4, refs.Count);
    Assert.True(refs.Contains("child-id"));
    Assert.True(refs.Contains("loop-child"));
    Assert.True(refs.Contains("pixel-child"));
    Assert.True(refs.Contains("cond-child"));
}
```

Adjust `PixelWhenStep` / `PixelCondition` constructor to match existing types in `MacroModel.cs` / `PixelModel.cs` if the snippet differs (read those files first; keep the same nesting coverage).

- [ ] **Step 2: Run test — expect fail**

Run: `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj`

Expected: compile error or FAIL — `MacroCallReferenceCollector` missing.

- [ ] **Step 3: Implement collector**

Create `src/shared/MacroHid.Core/MacroCallReferenceCollector.cs`:

```csharp
namespace MacroHid.Core;

public static class MacroCallReferenceCollector
{
    public static IReadOnlyList<string> Collect(MacroDocument document)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSteps(document.Steps, set);
        foreach (var condition in document.EffectiveConditions)
        {
            CollectSteps(condition.ThenSteps, set);
        }

        return set.ToList();
    }

    private static void CollectSteps(IReadOnlyList<MacroStep> steps, HashSet<string> set)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case MacroCallStep call when !string.IsNullOrWhiteSpace(call.Macro):
                    set.Add(call.Macro.Trim());
                    break;
                case RepeatStep repeat:
                    CollectSteps(repeat.Steps, set);
                    break;
                case PixelWhenStep pixel:
                    CollectSteps(pixel.ThenSteps, set);
                    break;
            }
        }
    }
}
```

- [ ] **Step 4: Run test — expect pass**

Run: `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj`

Expected: `PASS Macro call reference collector walks nested structures`

- [ ] **Step 5: Commit**

```bash
git add src/shared/MacroHid.Core/MacroCallReferenceCollector.cs tests/MacroHid.Core.Tests/Program.cs
git commit -m "feat: collect nested macro.call references for export"
```

---

### Task 2: Build export bundle (folder macros + external deps)

**Files:**
- Create: `src/shared/MacroHid.Core/MacroLibraryExportBundle.cs`
- Modify: `tests/MacroHid.Core.Tests/Program.cs`

- [ ] **Step 1: Add failing test**

```csharp
("Macro library export bundle separates folder macros from dependencies", MacroLibraryExportBundleSeparatesFolderMacrosFromDependencies),
```

```csharp
static void MacroLibraryExportBundleSeparatesFolderMacrosFromDependencies()
{
    var root = Path.Combine(Path.GetTempPath(), "MacroHID-tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new MacroLibraryStore(root);
        store.CreateFolder("Raid");
        var dep = store.CreateMacro("Shared Burst", steps: [new WaitStep(TimeSpan.FromMilliseconds(1))]);
        var inside = store.CreateMacro(
            new MacroDocument(1, "Opener", PlaybackSettings.Default, [new MacroCallStep(dep.Id)]),
            folder: "Raid");

        var snapshot = store.Load();
        var bundle = MacroLibraryExportBundle.FromFolder(
            store,
            snapshot,
            groupId: MacroLibraryStore.GlobalGroupId,
            folder: "Raid");

        Assert.Equal(1, bundle.Primary.Count);
        Assert.Equal(inside.Id, bundle.Primary[0].Item.Id);
        Assert.Equal(1, bundle.Dependencies.Count);
        Assert.Equal(dep.Id, bundle.Dependencies[0].Item.Id);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
```

- [ ] **Step 2: Run test — expect fail**

Expected: `MacroLibraryExportBundle` missing.

- [ ] **Step 3: Implement bundle helper**

```csharp
namespace MacroHid.Core;

public sealed record MacroLibraryExportEntry(MacroLibraryItem Item, MacroDocument Document, string RelativePath);

public sealed record MacroLibraryExportBundle(
    IReadOnlyList<MacroLibraryExportEntry> Primary,
    IReadOnlyList<MacroLibraryExportEntry> Dependencies);

public static class MacroLibraryExportBundle
{
    public static MacroLibraryExportBundle FromFolder(
        MacroLibraryStore store,
        MacroLibrarySnapshot snapshot,
        string groupId,
        string folder)
    {
        var primaryItems = snapshot.Items
            .Where(item =>
                string.Equals(item.GroupId, groupId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Folder, folder, StringComparison.Ordinal))
            .ToList();

        var primary = primaryItems
            .Select(item => new MacroLibraryExportEntry(item, store.ReadMacro(item.Id) with { Id = item.Id }, item.FileName))
            .ToList();

        var primaryIds = primaryItems.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deps = new Dictionary<string, MacroLibraryExportEntry>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(primary.SelectMany(entry => MacroCallReferenceCollector.Collect(entry.Document)));
        var visitedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.TryDequeue(out var reference))
        {
            if (!visitedRefs.Add(reference)) continue;
            var item = snapshot.Items.FirstOrDefault(candidate => candidate.MatchesReference(reference));
            if (item is null || primaryIds.Contains(item.Id) || deps.ContainsKey(item.Id)) continue;
            var document = store.ReadMacro(item.Id) with { Id = item.Id };
            deps[item.Id] = new MacroLibraryExportEntry(item, document, item.FileName);
            foreach (var nested in MacroCallReferenceCollector.Collect(document))
            {
                pending.Enqueue(nested);
            }
        }

        return new MacroLibraryExportBundle(primary, deps.Values.ToList());
    }

    public static MacroLibraryExportBundle FromItems(
        MacroLibraryStore store,
        MacroLibrarySnapshot snapshot,
        IReadOnlyList<MacroLibraryItem> items)
    {
        var primaryItems = items.ToList();
        var primary = primaryItems
            .Select(item => new MacroLibraryExportEntry(item, store.ReadMacro(item.Id) with { Id = item.Id }, item.FileName))
            .ToList();
        var primaryIds = primaryItems.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deps = new Dictionary<string, MacroLibraryExportEntry>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(primary.SelectMany(entry => MacroCallReferenceCollector.Collect(entry.Document)));
        var visitedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.TryDequeue(out var reference))
        {
            if (!visitedRefs.Add(reference)) continue;
            var item = snapshot.Items.FirstOrDefault(candidate => candidate.MatchesReference(reference));
            if (item is null || primaryIds.Contains(item.Id) || deps.ContainsKey(item.Id)) continue;
            var document = store.ReadMacro(item.Id) with { Id = item.Id };
            deps[item.Id] = new MacroLibraryExportEntry(item, document, item.FileName);
            foreach (var nested in MacroCallReferenceCollector.Collect(document))
            {
                pending.Enqueue(nested);
            }
        }

        return new MacroLibraryExportBundle(primary, deps.Values.ToList());
    }
}
```

Implement the class exactly as above (no omitted methods).

- [ ] **Step 4: Run tests — expect pass**

Expected: PASS for Task 1 and Task 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/shared/MacroHid.Core/MacroLibraryExportBundle.cs tests/MacroHid.Core.Tests/Program.cs
git commit -m "feat: build macro export bundles with external dependencies"
```

---

### Task 3: Toolbar — New dropdown only

**Files:**
- Modify: `src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml`
- Modify: `src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml.cs`
- Modify: `src/ui/MacroStudio/Resources/Strings.resx`
- Modify: `src/ui/MacroStudio/Resources/Strings.zh-CN.resx`
- Modify: `src/ui/MacroStudio/Resources/Strings.zh-TW.resx`
- Modify: `tests/MacroHid.Core.Tests/Program.cs`

- [ ] **Step 1: Add failing UI assertion test**

```csharp
("MacroStudio macro library toolbar uses new dropdown only", MacroStudioMacroLibraryToolbarUsesNewDropdownOnly),
```

```csharp
static void MacroStudioMacroLibraryToolbarUsesNewDropdownOnly()
{
    var xaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "MacroLibraryPanel.xaml"));
    Assert.Contains("x:Name=\"NewMacroMenuButton\"", xaml);
    Assert.Contains("ExplorerNewMacroMenuItem", xaml); // or NewMacroFromMenu_Click targets
    Assert.DoesNotContain("x:Name=\"CopyMacroButton\"", xaml);
    Assert.DoesNotContain("x:Name=\"PasteMacroButton\"", xaml);
    Assert.DoesNotContain("x:Name=\"DeleteMacroButton\"", xaml);
    Assert.DoesNotContain("x:Name=\"NewFolderButton\"", xaml);
}
```

- [ ] **Step 2: Run — expect fail**

- [ ] **Step 3: Replace toolbar WrapPanel**

In `MacroLibraryPanel.xaml` database toolbar `WrapPanel` (around lines 371–377), replace the five buttons with:

```xml
<Button x:Name="NewMacroMenuButton" MinWidth="88" Content="新建" Click="NewMacroMenuButton_Click">
    <Button.ContextMenu>
        <ContextMenu x:Name="NewMacroContextMenu">
            <MenuItem x:Name="ToolbarNewMacroMenuItem" Header="宏文件" InputGestureText="Ctrl+N" Click="NewMacro_Click" />
            <MenuItem x:Name="ToolbarNewFolderMenuItem" Header="文件夹" InputGestureText="Ctrl+Shift+N" Click="NewFolder_Click" />
        </ContextMenu>
    </Button.ContextMenu>
</Button>
```

In code-behind:

```csharp
private void NewMacroMenuButton_Click(object sender, RoutedEventArgs e)
{
    if (NewMacroContextMenu is null) return;
    NewMacroContextMenu.PlacementTarget = NewMacroMenuButton;
    NewMacroContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
    NewMacroContextMenu.IsOpen = true;
}
```

Update `ApplyLocalization` to set `NewMacroMenuButton.Content`, menu headers (`NewMacroFile`, `NewFolder`), and **remove** localization assignments for deleted buttons.

Add strings:
- `NewMacroFile` = 宏文件 / 宏檔案 / Macro file
- Keep `NewFolder` if present; else add.

Remove `Initialize`/`IsEnabled` references to `CopyMacroButton`, `PasteMacroButton`, `DeleteMacroButton`, `NewFolderButton`. Keep `UpdateClipboardControls` enabling paste **menu** items only.

- [ ] **Step 4: Run test — expect pass**

- [ ] **Step 5: Commit**

```bash
git add src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml.cs src/ui/MacroStudio/Resources/*.resx tests/MacroHid.Core.Tests/Program.cs
git commit -m "feat: replace library toolbar with New dropdown"
```

---

### Task 4: Context menus by target

**Files:**
- Modify: `src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml` (Explorer `ContextMenu`)
- Modify: `src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml.cs` (`ExplorerContextMenu_Opened`)
- Modify: `tests/MacroHid.Core.Tests/Program.cs`
- Modify: strings as needed (`ExportThisFolder`, `DeleteMacroFolder`, `LockMacroFile`)

- [ ] **Step 1: Failing test**

```csharp
("MacroStudio explorer context menu switches by target kind", MacroStudioExplorerContextMenuSwitchesByTargetKind),
```

Assert code contains visibility branching such as:

```csharp
Assert.Contains("ConfigureExplorerContextMenu", libraryCode);
Assert.Contains("ExportFolderMenuItem", libraryCode);
Assert.Contains("ExportMacroMenuItem", libraryCode);
Assert.DoesNotContain("ExplorerOpenMenuItem.IsEnabled", libraryCode); // open removed from file menu flow OR item removed
```

Prefer asserting XAML no longer has `ExplorerOpenMenuItem` / `ExplorerDuplicateMenuItem` / `ExplorerSelectAllMenuItem` if removed per spec.

- [ ] **Step 2: Run — expect fail**

- [ ] **Step 3: Rebuild Explorer context menu**

Replace Explorer `ContextMenu` items with a flat set that is shown/hidden in `Opened`:

Always declare (names illustrative — keep consistent in XAML + code):

- `ExplorerExportMacroMenuItem` → `ExportMacro_Click` / dedicated handler
- `ExplorerExportFolderMenuItem` → folder export handler
- `ExplorerNewMenuItem` (submenu macro/folder)
- `ExplorerRenameMenuItem`, `ExplorerCopyMenuItem`, `ExplorerPasteMenuItem`
- `ExplorerToggleLockMenuItem`
- `ExplorerDeleteMenuItem` (header switches: 删除 / 删除宏文件夹)
- `ExplorerViewMenuItem`, `ExplorerSortMenuItem`, `ExplorerRefreshMenuItem`

In `ExplorerContextMenu_Opened`:

```csharp
private void ExplorerContextMenu_Opened(object sender, RoutedEventArgs e)
{
    var selected = GetSelectedExplorerNodes();
    var kind = selected.Count == 0
        ? ContextMenuKind.Blank
        : selected.All(node => node.IsFolder) && selected.Count == 1
            ? ContextMenuKind.Folder
            : selected.All(node => node.Item is not null)
                ? ContextMenuKind.Macro
                : ContextMenuKind.Blank; // mixed → treat as blank-safe subset or disable destructive ops

    ConfigureExplorerContextMenu(kind, selected);
}
```

Visibility rules (match spec exactly):

| Item | Macro | Folder | Blank |
|------|-------|--------|-------|
| Export macro | ✓ | ✗ | ✗ |
| Export folder | ✗ | ✓ | ✗ |
| New submenu | ✗ | ✗ | ✓ |
| Copy | ✓ | ✓ | ✗ |
| Paste | ✓ | ✓ | ✓ |
| Rename | ✓ | ✓ | ✗ |
| Lock | ✓ | ✗ | ✗ |
| Delete | ✓ | ✓ | ✗ |
| View/Sort/Refresh | ✗ | ✗ | ✓ |

Remove Open / Duplicate / Select All from the menu.

TreeView context menu: either mirror the same rules for tree mode or keep minimal rename/copy/paste/delete if tree is still used; if Explorer is the primary progressive view, apply the same configure helper when tree menu opens.

- [ ] **Step 4: Run test — expect pass**

- [ ] **Step 5: Commit**

```bash
git add src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml.cs src/ui/MacroStudio/Resources/*.resx tests/MacroHid.Core.Tests/Program.cs
git commit -m "feat: switch explorer context menu by macro folder or blank"
```

---

### Task 5: Export wizard dialog

**Files:**
- Create: `src/ui/MacroStudio/Controls/ExportWizardDialog.xaml`
- Create: `src/ui/MacroStudio/Controls/ExportWizardDialog.xaml.cs`
- Modify: strings
- Modify: `tests/MacroHid.Core.Tests/Program.cs`

- [ ] **Step 1: Failing test**

```csharp
("MacroStudio export wizard has format and packaging steps", MacroStudioExportWizardHasFormatAndPackagingSteps),
```

```csharp
static void MacroStudioExportWizardHasFormatAndPackagingSteps()
{
    var xaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ExportWizardDialog.xaml"));
    var code = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ExportWizardDialog.xaml.cs"));
    Assert.Contains("x:Name=\"FormatList\"", xaml);
    Assert.Contains("x:Name=\"PackagingPanel\"", xaml);
    Assert.Contains("ExportPackagingMode", code);
    Assert.Contains("ThemedDialogChrome.Apply", code);
}
```

- [ ] **Step 2: Run — expect fail**

- [ ] **Step 3: Implement dialog**

`ExportWizardDialog.xaml.cs` shape:

```csharp
public enum ExportPackagingMode
{
    TwoFolders,
    Zip
}

public sealed class ExportWizardResult
{
    public MacroConversionFormat Format { get; init; }
    public ExportPackagingMode? Packaging { get; init; }
}

public partial class ExportWizardDialog : Window
{
    public ExportWizardResult? Result { get; private set; }

    public ExportWizardDialog(bool showPackagingStep)
    {
        InitializeComponent();
        ThemedDialogChrome.Apply(this);
        // populate FormatList from MacroConversionService.GetFormats().Where(f => f.CanExport)
        // default MacroHidMcrx
        PackagingPanel.Visibility = showPackagingStep ? Visibility.Visible : Visibility.Collapsed;
        WeakFormatWarningText.Visibility = Visibility.Collapsed;
    }
}
```

XAML: radio/list for formats; when packaging visible, radios for 两个子文件夹 / ZIP; Next/Export and Cancel. If `showPackagingStep` is false, primary button finishes after format. If true, step 1 Next reveals packaging (or show both pages via `step` int). Keep implementation to two panels toggled by `step` index 0/1.

On format change: if format != `MacroHidMcrx`, show warning text that nested calls may be lost.

- [ ] **Step 4: Run test — expect pass**

- [ ] **Step 5: Commit**

```bash
git add src/ui/MacroStudio/Controls/ExportWizardDialog.xaml src/ui/MacroStudio/Controls/ExportWizardDialog.xaml.cs src/ui/MacroStudio/Resources/*.resx tests/MacroHid.Core.Tests/Program.cs
git commit -m "feat: add themed export format packaging wizard"
```

---

### Task 6: Wire export paths (single / multi / folder + ZIP)

**Files:**
- Modify: `src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml` (remove `ExportFormatBox` / label)
- Modify: `src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml.cs`
- Modify: `docs/usage.md`
- Modify: `tests/MacroHid.Core.Tests/Program.cs`

- [ ] **Step 1: Failing tests**

```csharp
("MacroStudio export uses wizard instead of format combo", MacroStudioExportUsesWizardInsteadOfFormatCombo),
("Macro library export writer materializes two-folder layout", MacroLibraryExportWriterMaterializesTwoFolderLayout),
```

UI test: XAML does **not** contain `ExportFormatBox`; code contains `ExportWizardDialog` and `WriteExportBundle`.

Writer test (temp dir): build a small bundle with 1 primary + 1 dep, call a static writer (put in Core as `MacroLibraryExportWriter` or private testable method in Core):

```csharp
MacroLibraryExportWriter.WriteTwoFolders(bundle, parentDirectory, folderName: "Raid", format: MacroConversionFormat.MacroHidMcrx);
Assert.True(File.Exists(Path.Combine(parentDirectory, "Raid-导出", "Raid", /* primary file */)));
Assert.True(Directory.Exists(Path.Combine(parentDirectory, "Raid-导出", "依赖子宏")));
```

Use English folder suffix in code constants with localized display names from UI only — **lock constants in Core to**:

```csharp
public const string ExportRootSuffix = "-export"; // or localized via parameter
public const string DependenciesFolderName = "dependencies";
```

Spec used Chinese names `依赖子宏` / `{名}-导出`. Prefer **passing localized names from UI** into the writer so Core stays language-neutral:

```csharp
public static void WriteTwoFolders(
    MacroLibraryExportBundle bundle,
    string parentDirectory,
    string exportRootName,      // e.g. "Raid-导出"
    string primaryFolderName,   // e.g. "Raid"
    string dependenciesFolderName, // e.g. "依赖子宏"
    MacroConversionFormat format)
```

ZIP API:

```csharp
public static void WriteZip(
    MacroLibraryExportBundle bundle,
    string zipPath,
    string exportRootName,
    string primaryFolderName,
    string dependenciesFolderName,
    MacroConversionFormat format)
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "MacroHID-export", Guid.NewGuid().ToString("N"));
    try
    {
        WriteTwoFolders(bundle, tempRoot, exportRootName, primaryFolderName, dependenciesFolderName, format);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(Path.Combine(tempRoot, exportRootName), zipPath);
    }
    finally
    {
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
    }
}
```

Uses `System.IO.Compression.ZipFile`.

- [ ] **Step 2: Run — expect fail**

- [ ] **Step 3: Implement writer + panel wiring**

1. Add `MacroLibraryExportWriter.cs` in Core.
2. Remove `ExportFormatBox`, `ExportFormatLabelText`, `InitializeExportFormatBox`, `GetSelectedExportFormat` fallback combo.
3. Replace `ExportSelectedMacros` flow:

```csharp
private void ExportMacro_Click(object sender, RoutedEventArgs e) => BeginExport(ExportTarget.FromSelection(this));

private void BeginExport(ExportTarget target)
{
    if (target.IsEmpty)
    {
        ResultMessage?.Invoke(L("ExportNothingSelected"));
        return;
    }

    var wizard = new ExportWizardDialog(showPackagingStep: target.IsFolder);
    wizard.Owner = Window.GetWindow(this);
    if (DialogOwnerService.ShowDialogSafe(wizard, this) != true || wizard.Result is null) return;
    ExecuteExport(target, wizard.Result);
}
```

Define a small private `ExportTarget` record in the panel (or nested type) with `IsEmpty`, `IsFolder`, selected macros/folder fields, and `FromSelection` reading explorer selection.
- Folder → `FromFolder` + packaging TwoFolders/Zip via folder browser / save zip dialog.
- Single macro → SaveFileDialog.
- Multi macros → pick folder (`OpenFolderDialog` on net8-windows) + `FromItems` + write primary flat + `依赖子宏` if any.

4. Update conversion help text string to mention sharing folder + dependencies.
5. Update `docs/usage.md` CN/EN export bullets.

- [ ] **Step 4: Run full test suite**

Run: `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj`

Expected: all PASS (including updated old tests that asserted `ExportFormatBox` / `MacroConverterXml` default — **update those assertions** in the same change: search `ExportFormatBox`, `InitializeExportFormatBox`, `MacroConverterXml` default export selection).

- [ ] **Step 5: Commit**

```bash
git add src/shared/MacroHid.Core/MacroLibraryExportWriter.cs src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml src/ui/MacroStudio/Controls/MacroLibraryPanel.xaml.cs docs/usage.md src/ui/MacroStudio/Resources/*.resx tests/MacroHid.Core.Tests/Program.cs
git commit -m "feat: export macros through wizard with folder dependency packages"
```

---

### Task 7: Verification + local run

- [ ] **Step 1: Full tests**

```powershell
dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj
```

Expected: `N test(s) passed.` with 0 failed.

- [ ] **Step 2: Build local EXE**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-LocalRun.ps1 -Configuration Release
```

Expected: `MacroStudio.exe` under `artifacts\local-run\MacroStudio\`.

- [ ] **Step 3: Manual smoke (engineer)**

1. Toolbar only New ▾ → macro / folder.  
2. Right-click macro / folder / blank menus match spec.  
3. Export folder → wizard → two folders and ZIP each once.  
4. Import the package on a clean library; parent `macro.call` resolves.

- [ ] **Step 4: Commit any leftover string/doc fixes**

```bash
git add -u
git commit -m "chore: finalize library export menu polish"
```

(Skip empty commit if clean.)

---

## Spec coverage check

| Spec requirement | Task |
|------------------|------|
| New dropdown; remove copy/paste/delete toolbar | 3 |
| Macro / folder / blank context menus | 4 |
| No always-visible format combo; two-step wizard | 5–6 |
| Folder export + deps; two folders or ZIP | 2, 6 |
| `.mcrx` id + import remap (existing) | reused; covered by prior tests |
| Strings / usage docs | 3–6 |
| Acceptance tests | 1–7 |

## Placeholder / consistency notes

- Writer takes localized folder names from UI — Core has no hard-coded Chinese.
- Old tests mentioning `ExportFormatBox` must be updated in Task 6, not left failing.
- `PixelWhenStep` constructors in Task 1 must match current Core types (read before coding).
