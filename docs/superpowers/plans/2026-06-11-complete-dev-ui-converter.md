# Complete Development Build, UI, and Converter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make MacroHID usable on the development machine with real driver/pixel diagnostics, a softer MacroStudio UI, and an integrated MacroConverter workflow.

**Architecture:** Keep MacroHID as the main WPF shell. Runtime diagnostics live in `MacroHid.Runtime`, UI styling stays in MacroStudio resources, and converter support is added as a MacroStudio import/convert panel using the sibling MacroConverter project as the source of truth for supported formats.

**Tech Stack:** .NET 8 WPF, KMDF/VHF test driver scripts, Windows GDI visible pixel sampling for the first live sampler, and MacroConverter TypeScript/Electron assets for conversion workflow discovery.

---

### Task 1: Driver And Runtime State

**Files:**
- Modify: `src/shared/MacroHid.Runtime/ScreenPixelSampler.cs`
- Create: `src/shared/MacroHid.Runtime/RuntimeDiagnostics.cs`
- Modify: `src/ui/MacroStudio/MainWindow.xaml`
- Modify: `src/ui/MacroStudio/MainWindow.xaml.cs`
- Test: `tests/MacroHid.Core.Tests/Program.cs`

- [ ] Add tests for diagnostic objects reporting driver available/missing and pixel sampler available.
- [ ] Implement diagnostic records and safe runtime probes.
- [ ] Show live diagnostic state in MacroStudio instead of "pending" text.
- [ ] Run driver installer and `MacroRunner --send --pixels skip` to prove HID submission when Windows allows it.

### Task 2: Soft MacroStudio UI

**Files:**
- Modify: `src/ui/MacroStudio/MainWindow.xaml`
- Modify: `src/ui/MacroStudio/Resources/Strings*.resx`

- [ ] Add WPF styles for rounded buttons, soft background bands, refined group boxes, and status pills.
- [ ] Keep the existing dense tool layout while reducing default Windows chrome feel.
- [ ] Verify localized text still fits in English, Simplified Chinese, and Traditional Chinese.

### Task 3: MacroConverter Integration

**Files:**
- Create: `src/ui/MacroStudio/MacroConverterIntegration.cs`
- Modify: `src/ui/MacroStudio/MainWindow.xaml`
- Modify: `src/ui/MacroStudio/MainWindow.xaml.cs`
- Modify: `installer/MacroHID.iss`

- [ ] Detect the sibling `MacroConverter` project and packaged `MacroConverter.exe`.
- [ ] Add a MacroStudio converter panel that can open the full converter or import converter output into the MCRX editor.
- [ ] Include converter discovery/status in diagnostics.
- [ ] Include MacroConverter packaged assets in local dev output and installer when available.

### Task 4: Verification

**Commands:**
- `dotnet build MacroHID.sln --configuration Release`
- `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release`
- `dotnet run --project src\tools\MacroRunner\MacroRunner.csproj --configuration Release -- --macro samples\baseline.mcrx --pixels live`
- `dotnet run --project src\tools\MacroRunner\MacroRunner.csproj --configuration Release -- --macro samples\baseline.mcrx --send --pixels skip`
- `.\scripts\Build-LocalRun.ps1 -Configuration Release`
- `.\scripts\Build-Installer.ps1 -Configuration Release -SkipNativeBuild`

- [ ] Run full .NET build and tests.
- [ ] Verify MacroStudio starts from `artifacts\local-run`.
- [ ] Verify installer builds and includes language/resource assets.
- [ ] Push and wait for GitHub Actions.
