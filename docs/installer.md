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
artifacts\installer\MacroHID-Portable-x64.zip
```

安装包包含：

- `MacroStudio`：主编辑器和宏数据库。
- `MacroRunner`：命令行运行器。
- `LatencyProbe`：调度与提交延迟测试工具。
- `MacroHid.NativePlayback.dll`：极限模式的用户态 x64 native 播放引擎，随 MacroStudio/MacroRunner/LatencyProbe 一起安装。
- `samples`：示例 `.mcrx` 宏。
- `docs`：安装、使用、精度、架构和格式说明。
- 内置宏转换库：MacroHID、MacroConverter XML、Razer Synapse XML、Lua/Logitech Lua、XMouse、QMacro、GIMacros JSON、耕地机。

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

卸载时默认保留该目录，以便重新安装后继续使用。普通卸载会询问是否同时清除宏数据库、设置和界面布局；只有连续两次确认才会删除 `%APPDATA%\MacroHID`，静默卸载始终保留用户数据。

### 无驱动说明

MacroHID 当前版本完全使用 Windows `SendInput`：

- 不安装驱动。
- 不安装 Windows service。
- 不需要测试签名模式。
- 不需要关闭或修改 Secure Boot。
- 不使用 `pnputil`、`devcon`、VHF/KMDF 或 IOCTL。
- 卸载时不会卸载驱动或修改系统启动策略；用户数据默认保留，也可在卸载确认中主动清除。

### 从网上下载后被拦截

从 GitHub 下载的 `MacroHID-Setup-x64.exe` 会带上浏览器的「来自互联网」标记。安装程序还要在临时目录里再解出一份 `.tmp` 来执行。当前安装包没有向 CA 购买的代码签名证书，Windows 11 的智能应用控制 / 应用程序控制策略可能直接拦住这份临时文件。

典型提示：

- `Unable to execute file in the temporary directory. Setup aborted.`
- `Error 4551: 应用程序控制策略已阻止此文件。`
- 安全中心：「此应用的一部分已被阻止」「无法确认谁发布了 …tmp」。

按顺序试：

1. **解除锁定后再装。** 右键安装包 → 属性 → 若有「解除锁定」则勾选 → 应用 → 确定。或在 PowerShell 中执行：

   ```powershell
   Unblock-File -Path "$env:USERPROFILE\Downloads\MacroHID-Setup-x64.exe"
   ```

   文件名若被改成 `MacroHID-Setup-x64 (1).exe`，路径要写成实际文件名。然后右键「以管理员身份运行」。

2. **改用便携包。** 在同一 Releases 页下载 `MacroHID-Portable-x64.zip`，先对 zip 解除锁定，再解压。目录排布与安装版相同（`MacroStudio`、`MacroRunner`、`LatencyProbe`、`docs` 等）。双击根目录的 **MacroStudio** 快捷方式启动，不要把快捷方式单独拷走。

3. **仍被拦住。** 打开 Windows 安全中心 → 应用和浏览器控制 → **智能应用控制**。若处于「开」，未签名软件无法单独加白名单。可在确认文件来自本仓库 Releases 后，临时关掉智能应用控制再安装；微软说明关掉后有的版本不能再打开，除非重置系统，请自行权衡。

从本机直接编译出的安装包（未经过浏览器下载）一般不会带互联网标记，通常可以直接装。

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
artifacts\installer\MacroHID-Portable-x64.zip
```

The setup package includes:

- `MacroStudio`: the main editor and local macro database.
- `MacroRunner`: command-line runner.
- `LatencyProbe`: scheduler and submission latency probe.
- `MacroHid.NativePlayback.dll`: the user-mode x64 native playback engine for Extreme mode, installed alongside MacroStudio, MacroRunner, and LatencyProbe.
- `samples`: sample `.mcrx` macros.
- `docs`: installation, usage, precision, architecture, and format documentation.
- Built-in conversion libraries for MacroHID, MacroConverter XML, Razer Synapse XML, Lua/Logitech Lua, XMouse, QMacro, GIMacros JSON, and GengDiJi.

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

Uninstall keeps this directory by default so a later reinstall can reuse it. Interactive uninstall asks whether to remove the macro database, settings, and workspace layout, and requires a second confirmation before deleting `%APPDATA%\MacroHID`. Silent uninstall always preserves user data.

### No Driver

MacroHID uses Windows `SendInput` only:

- No driver is installed.
- No Windows service is installed.
- No test-signing mode is required.
- Secure Boot does not need to be changed.
- `pnputil`, `devcon`, VHF/KMDF, and IOCTL paths are not used.
- Uninstall does not remove a driver or modify boot policy. User data is preserved by default and can be explicitly removed from the uninstall confirmation.

### Blocked after downloading from the internet

GitHub downloads carry Mark of the Web. Inno Setup then unpacks a `.tmp` helper under `%TEMP%`. This build is not Authenticode-signed, so Windows 11 Smart App Control may abort with Error 4551.

Unblock the file first (`Properties` → Unblock, or `Unblock-File`), or use `MacroHID-Portable-x64.zip` after unblocking the zip. See the Chinese section above for Smart App Control notes.

### Troubleshooting

- Cannot control an elevated window: run MacroStudio/MacroRunner as Administrator.
- Cannot control UAC prompts or secure desktop: those are Windows security boundaries and are intentionally unsupported.
- Macro does not trigger: check the macro database trigger, playback mode, process filter, and whether listening is enabled.
- Imported Razer macro misses nested macros: import referenced module XML first, then the main macro. The built-in converter preserves macro-call relationships where possible.
