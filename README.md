# MacroHID

## 中文

MacroHID 是一个面向 Windows 10/11 x64 的本机宏自动化工具。当前版本采用纯用户态 `SendInput` 路线，不安装驱动、不启用测试签名、不修改 Secure Boot；它适用于合法的本机桌面自动化、普通应用以及以同等权限运行的管理员应用。

### 主要能力

- WPF 桌面编辑器 MacroStudio：宏数据库、IDEA 风格工具窗口、可视化序列、条件序列、MCRX JSON 面板、动作面板和播放控制。
- 全局触发键：支持单键、组合键、Ctrl/Alt/Shift/Win、鼠标侧键，以及每个宏独立的播放模式和进程筛选。
- 播放模式：按下切换循环、按住循环、播放 N 次，并支持执行中停止。
- 指令覆盖：键盘按下/抬起、Unicode 文本、鼠标按下/抬起、坐标点击、相对/绝对移动、滚轮、水平滚轮、媒体键、延迟、随机延迟、循环、调用宏、像素条件。
- 条件序列：一个宏包含基础序列和条件序列；基础序列直接执行，条件序列在播放后按像素/时间窗口等条件触发 then-actions。
- 内置转换器：支持 MacroHID `.mcrx`、MacroConverter XML、Razer Synapse XML、Lua/Logitech Lua、XMouse、按键精灵/QMacro 的导入导出，不依赖外部 Electron 转换器。
- 三档用户态精度模式：基础（目标 0.5ms）、高性能（目标 0.25ms）、极限（目标 0.1ms）；极限档会优先使用进程内 x64 native playback DLL。密集 1ms/2ms 循环在 auto 模式下优先使用 inline native 路径压低单步尖峰，显式 standby 模式保留 2 worker 低启动路径；DLL 不可用时自动回退到 managed ultra。

### 快速开始

```powershell
dotnet build MacroHID.sln --configuration Release
dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release
.\scripts\Build-LocalRun.ps1 -Configuration Release
.\artifacts\local-run\MacroStudio\MacroStudio.exe
```

`Build-LocalRun.ps1` 会先构建 `MacroHid.NativePlayback.dll`，再把它复制到 MacroStudio、MacroRunner 和 LatencyProbe 输出目录。

构建安装包：

```powershell
.\scripts\Build-Installer.ps1 -Configuration Release
```

安装包输出到：

```text
artifacts\installer\MacroHID-Setup-x64.exe
```

### 文档

- [安装说明](docs/installer.md)
- [使用说明](docs/usage.md)
- [精度说明](docs/precision.md)
- [架构说明](docs/architecture.md)
- [MCRX 格式](docs/mcrx-format.md)

### 边界

MacroHID 不绕过安全边界，不支持安全桌面、UAC 弹窗、反作弊保护、受保护进程或系统级输入隔离。要控制管理员权限窗口，请以管理员身份启动 MacroStudio 或 MacroRunner。

## English

MacroHID is a local Windows input macro tool for Windows 10/11 x64. The current version is fully user-mode and submits input through Windows `SendInput`; it does not install a driver, enable test-signing, or require Secure Boot changes. It is intended for legal local desktop automation, normal applications, and elevated applications running at the same integrity level.

### Highlights

- MacroStudio WPF editor: macro database, IDEA-style tool windows, visual sequence editing, condition sequences, MCRX JSON panel, action palette, and playback controls.
- Global triggers: single keys, key chords, Ctrl/Alt/Shift/Win, mouse side buttons, per-macro playback mode, and optional foreground process filter.
- Playback modes: toggle loop, hold loop, fixed N runs, and stop while running.
- Step coverage: keyboard down/up, Unicode text, mouse down/up, coordinate clicks, relative/absolute movement, vertical/horizontal wheel, media keys, fixed/random waits, loops, macro calls, and pixel conditions.
- Condition sequences: each macro has a base sequence and condition directives; the base sequence runs directly, while condition then-actions run only after their condition is met.
- Built-in converter: imports/exports MacroHID `.mcrx`, MacroConverter XML, Razer Synapse XML, Lua/Logitech Lua, XMouse, and QMacro formats without launching the external Electron converter.
- Three user-mode precision modes: Basic (0.5ms target), High Performance (0.25ms target), and Extreme (0.1ms target); Extreme prefers the in-process x64 native playback DLL. Dense 1ms/2ms loops prefer the inline native path in auto mode to reduce per-step spikes, while explicit standby mode keeps a two-worker low-startup path; MacroHID automatically falls back to managed ultra when the DLL is unavailable.

### Quick Start

```powershell
dotnet build MacroHID.sln --configuration Release
dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release
.\scripts\Build-LocalRun.ps1 -Configuration Release
.\artifacts\local-run\MacroStudio\MacroStudio.exe
```

`Build-LocalRun.ps1` builds `MacroHid.NativePlayback.dll` first, then copies it into the MacroStudio, MacroRunner, and LatencyProbe output folders.

Build the installer:

```powershell
.\scripts\Build-Installer.ps1 -Configuration Release
```

The setup package is written to:

```text
artifacts\installer\MacroHID-Setup-x64.exe
```

### Documentation

- [Installation](docs/installer.md)
- [Usage Guide](docs/usage.md)
- [Precision Notes](docs/precision.md)
- [Architecture](docs/architecture.md)
- [MCRX Format](docs/mcrx-format.md)

### Scope

MacroHID does not bypass security boundaries. Secure desktop, UAC prompts, anti-cheat contexts, protected processes, and system-level input isolation are out of scope. To control elevated windows, run MacroStudio or MacroRunner as Administrator.
