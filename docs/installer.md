# MacroHID Installation / MacroHID 安装说明

## 中文

### 运行环境

- Windows 10/11 x64。
- .NET 8 Desktop Runtime。当前发布采用框架依赖模式，安装包不内置 .NET 运行时。
- 如果要控制管理员权限应用，请以管理员身份启动 MacroStudio 或 MacroRunner。

### 安装包构建

```powershell
.\scripts\Build-Installer.ps1 -Configuration Release
```

输出文件：

```text
artifacts\installer\MacroHID-Setup-x64.exe
```

安装包包含：

- `MacroStudio`：主编辑器和宏数据库。
- `MacroRunner`：命令行运行器。
- `LatencyProbe`：调度与提交延迟测试工具。
- `MacroHid.NativePlayback.dll`：极限模式的用户态 x64 native 播放引擎，随 MacroStudio/MacroRunner/LatencyProbe 一起安装。
- `samples`：示例 `.mcrx` 宏。
- `docs`：安装、使用、精度、架构和格式说明。
- 内置宏转换库：MacroHID、MacroConverter XML、Razer Synapse XML、Lua/Logitech Lua、XMouse、QMacro。

### 本机开发验证

如果只是要在开发机上直接运行，不需要生成安装包：

```powershell
.\scripts\Build-LocalRun.ps1 -Configuration Release
.\artifacts\local-run\MacroStudio\MacroStudio.exe
```

也可以构建后立刻启动：

```powershell
.\scripts\Build-LocalRun.ps1 -Configuration Release -Launch
```

### 安装后的数据位置

MacroStudio 的宏数据库位于：

```text
%APPDATA%\MacroHID\MacroLibrary
```

每个宏仍以普通 `.mcrx` 文件保存，`library.json` 保存文件夹、排序、选择状态、触发键、播放模式和更新时间等索引信息。

### 无驱动说明

MacroHID 当前版本完全使用 Windows `SendInput`：

- 不安装驱动。
- 不安装 Windows service。
- 不需要测试签名模式。
- 不需要关闭或修改 Secure Boot。
- 不使用 `pnputil`、`devcon`、VHF/KMDF 或 IOCTL。
- 卸载时只移除应用文件，不会卸载驱动或修改系统启动策略。

### 常见问题

- 无法控制管理员窗口：请以管理员身份启动 MacroStudio/MacroRunner。
- 无法控制 UAC 弹窗或安全桌面：这是 Windows 安全边界，MacroHID 不支持绕过。
- 宏没有触发：检查宏数据库中该宏的触发键、播放模式、进程筛选，以及是否已点击“开始监听”。
- 导入雷云宏后嵌套宏不完整：先导入被引用的模块 XML，再导入主宏；内置转换器会尽量保留宏调用关系。

## English

### Runtime Requirements

- Windows 10/11 x64.
- .NET 8 Desktop Runtime. Current builds are framework-dependent and do not bundle the .NET runtime.
- To automate elevated applications, run MacroStudio or MacroRunner as Administrator.

### Build the Installer

```powershell
.\scripts\Build-Installer.ps1 -Configuration Release
```

Output:

```text
artifacts\installer\MacroHID-Setup-x64.exe
```

The setup package includes:

- `MacroStudio`: the main editor and local macro database.
- `MacroRunner`: command-line runner.
- `LatencyProbe`: scheduler and submission latency probe.
- `MacroHid.NativePlayback.dll`: the user-mode x64 native playback engine for Extreme mode, installed alongside MacroStudio, MacroRunner, and LatencyProbe.
- `samples`: sample `.mcrx` macros.
- `docs`: installation, usage, precision, architecture, and format documentation.
- Built-in conversion libraries for MacroHID, MacroConverter XML, Razer Synapse XML, Lua/Logitech Lua, XMouse, and QMacro.

### Local Development Run

For development verification without creating a setup package:

```powershell
.\scripts\Build-LocalRun.ps1 -Configuration Release
.\artifacts\local-run\MacroStudio\MacroStudio.exe
```

Build and launch immediately:

```powershell
.\scripts\Build-LocalRun.ps1 -Configuration Release -Launch
```

### User Data

MacroStudio stores the local macro database in:

```text
%APPDATA%\MacroHID\MacroLibrary
```

Each macro is stored as a normal `.mcrx` file. `library.json` stores folders, ordering, selection state, trigger keys, playback modes, and timestamps.

### No Driver

MacroHID uses Windows `SendInput` only:

- No driver is installed.
- No Windows service is installed.
- No test-signing mode is required.
- Secure Boot does not need to be changed.
- `pnputil`, `devcon`, VHF/KMDF, and IOCTL paths are not used.
- Uninstall removes application files only; there is no driver or boot policy to undo.

### Troubleshooting

- Cannot control an elevated window: run MacroStudio/MacroRunner as Administrator.
- Cannot control UAC prompts or secure desktop: those are Windows security boundaries and are intentionally unsupported.
- Macro does not trigger: check the macro database trigger, playback mode, process filter, and whether listening is enabled.
- Imported Razer macro misses nested macros: import referenced module XML first, then the main macro. The built-in converter preserves macro-call relationships where possible.
