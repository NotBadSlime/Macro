# MacroHID Studio

MacroHID Studio 可与同版本号的正式版并列安装。Studio 使用自己的宏库、设置和工作区布局。

工作台默认使用暖纸色主题，也可切换到暖石墨。使用顶部的按键提示打开命令搜索，再用方向键选中命令、Enter 执行，或按 Esc 关闭。

点击步骤表格中的一行即可查看或修改属性。键盘和鼠标步骤用向下、向上箭头标记按下和抬起。动作库支持按名称、说明和分类筛选，也可以将动作拖到序列中的指定位置。

普通宏在宽窗口的右侧上方并列显示步骤属性和动作库，下方显示播放设置。打开独立条件宏时，中央显示条件列表和所选条件的触发动作，右侧显示该条件的设置。顶部「条件」打开普通宏的条件序列或回到独立条件宏的设置，「JSON」打开下方的 MCRX 编辑器。「窗口」菜单可显示、停靠或浮动面板，也可打开基础控制、精度控制、高级设置和全局命令。宏库标题旁的管理图标可返回数据库列表。

| 项目 | 正式版 | MacroHID Studio |
| --- | --- | --- |
| 默认安装目录 | `%ProgramFiles%\MacroHID` | `%ProgramFiles%\MacroHID Studio` |
| 开始菜单 | MacroHID | MacroHID Studio |
| 用户数据 | `%APPDATA%\MacroHID` | `%APPDATA%\MacroHID-UI` |
| 全局命令 | `MacroCtl` | `MacroCtlUI` |
| 便携版解压根目录 | `MacroHID` | `MacroHID-Studio` |

Studio 启动时会检查当前 Windows 用户的 `%APPDATA%\MacroHID\MacroLibrary`。发现已有宏时，会自动把宏、分组和文件夹复制到自己的宏库。Studio 中已有的同 ID 宏会保留，复制完成后两套宏库各自保存。自动复制只执行一次。旧宏库尚未建立、为空或复制失败时，下次启动会再次检查。

之后可把从正式版导出的宏文件或 ZIP 拖入 Studio 窗口。如果在两个版本中使用同一个系统热键，可只在当前使用的版本启用监听。

## 本机打包

在 `codex/ui-optimized` 分支运行仓库根目录的 `打包.cmd`，也可使用 PowerShell。

```powershell
.\scripts\Build-Installer.ps1 -Version 2.2.0 -Configuration Release
```

`artifacts/packages-ui` 包含发布文件。`artifacts/local-run-ui/MacroStudio/MacroStudio.exe` 是包含 .NET 运行库的本地版本，运行时保留整个目录。其他选项见 [打包说明](packaging.md)。

## Studio 发布

从 `codex/ui-optimized` 的发布提交生成 Studio 附件。Studio 2.2.0 使用单独的 `v2.2.0-studio` 标签，发布说明标出构建对应的提交。

- `MacroHID-Studio-Setup-x64.exe`
- `MacroHID-Studio-Portable-x64.zip`
- `MacroHID-Studio-SHA256SUMS.txt`

Studio 分支的 GitHub Actions 分别上传安装包、便携包和本地运行版。发布前核对版本号和 SHA-256。
