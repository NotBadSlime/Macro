# 一键打包

双击仓库根目录的 `打包.cmd`，或运行 `scripts/Build-Installer.ps1`。`scripts/Build-LocalRun.ps1` 也进入同一完整流程。

每次成功打包固定更新：

| 产物 | 相对项目根目录的路径 |
| --- | --- |
| 安装包 | `artifacts/packages/MacroHID-Setup-x64.exe` |
| 便携包 | `artifacts/packages/MacroHID-Portable-x64.zip` |
| 本地运行程序 | `artifacts/local-run/MacroStudio/MacroStudio.exe` |

快捷方式固定放在 `D:\360MoveData\Users\12849\Desktop\Start Item\MacroHID 本地版.lnk`，指向上述本地运行程序。整个本地运行目录包含 .NET 运行库和 OCR 组件，不要单独移动 EXE。安装包、便携包仍需 .NET 8 Desktop Runtime x64。便携包解压后运行根目录的 `MacroStudio.cmd`，也可以直接运行 `MacroStudio/MacroStudio.exe`。

打包前以及替换产物前，脚本检查构建目录和输出目录内运行的程序。若有占用，会弹出确认框，列出程序名、PID、完整路径。请先保存工作；选择“否”会取消，选择“是”后请求退出，10 秒仍未退出则终止该程序。不按进程名批量结束其他目录里的软件。无法读取的受保护进程或其他文件占用仍可能导致打包失败，此时应手动解除占用再运行。

先在新的 `artifacts/packaging-stage-*` 目录构建全部产物，成功后才替换固定输出；替换失败会尝试回滚。旧文件移入该 staging 目录中的 `previous-*`，不会删除用户宏数据库。为便于恢复，构建中间文件和备份保留，可在确认新版正常且没有程序使用后手动清理。禁止并发打包。

可选参数：`-Version 2.0.0`、`-Configuration Release`、`-Launch`、`-InstallInno`。`-OutputRoot` 可指定项目 `artifacts` 下的固定发行包目录；`-ShortcutDirectory` 可指定快捷方式目录。本地运行版始终位于 `artifacts/local-run`。

日志目录为 `%APPDATA%\MacroHID\log`；点击软件顶部“打开日志”查看。
