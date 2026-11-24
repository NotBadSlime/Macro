# MacroHID UI 优化版

UI 优化版可以与同版本号的正式版并列安装，也可以从同一个 GitHub Release 下载两版各自的文件。UI 优化版使用自己的宏库、设置和工作区布局。

工作台默认使用暖纸色主题，也可切换到暖石墨。使用顶部的按键提示打开命令搜索，再用方向键选中命令、Enter 执行，或按 Esc 关闭。

点击步骤表格中的一行即可查看或修改属性。键盘和鼠标步骤用向下、向上箭头标记按下和抬起。动作库支持按名称、说明和分类筛选，也可以将动作拖到序列中的指定位置。

宽窗口的右侧上方并列显示步骤属性和动作库，下方显示宏播放设置。窄窗口下可用右侧页签切换。顶部「条件」打开条件序列，「JSON」打开下方的 MCRX 编辑器。「窗口」菜单仍可显示、停靠或浮动面板，也可打开基础控制、精度控制、高级设置和全局命令。宏库标题旁的管理图标可返回数据库列表。

| 项目 | 正式版 | UI 优化版 |
| --- | --- | --- |
| 默认安装目录 | `%ProgramFiles%\MacroHID` | `%ProgramFiles%\MacroHID UI Edition` |
| 开始菜单 | MacroHID | MacroHID UI Edition |
| 用户数据 | `%APPDATA%\MacroHID` | `%APPDATA%\MacroHID-UI` |
| 全局命令 | `MacroCtl` | `MacroCtlUI` |
| 便携版解压根目录 | `MacroHID` | `MacroHID-UI` |

要在 UI 优化版沿用已有宏，可从正式版导出宏文件或 ZIP，再拖入 UI 优化版窗口。如果在两个版本中使用同一个系统热键，建议只在当前使用的版本启用监听。影响系统或同一外部程序的动作也应由一个版本执行。

## 本机打包

在 UI 优化分支运行仓库根目录的 `打包.cmd`，或运行：

```powershell
.\scripts\Build-Installer.ps1 -Version 2.2.0 -Configuration Release
```

`artifacts/packages-ui` 包含发布文件。`artifacts/local-run-ui/MacroStudio/MacroStudio.exe` 是包含 .NET 运行库的本地版本，运行时保留整个目录。其他选项见 [打包说明](packaging.md)。

## 与正式版同页发布

从正式版提交生成正式版附件，从 UI 优化分支提交生成 UI 优化版附件。把以下文件追加到对应版本的 Release。发布说明应标出 UI 优化版附件对应的分支或提交，方便定位该构建的源码。

- `MacroHID-UI-Setup-x64.exe`
- `MacroHID-UI-Portable-x64.zip`
- `MacroHID-UI-SHA256SUMS.txt`

UI 优化分支的 GitHub Actions 使用单独的产物名称，安装包、便携包和校验文件位于 `MacroHID-UI-installer-<commit>`。发布前核对安装包和便携包的版本号与 SHA-256。
