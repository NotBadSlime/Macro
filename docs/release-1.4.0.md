# MacroHID 1.4.0 正式版

MacroHID `1.4.0` 是当前正式版。本版本新增耕地机脚本导入导出，并补齐取色全局热键、宏库路径定位，以及探险器拖拽时的滚轮投放。

## 主要变化

- 内置转换器支持耕地机 `.txt`：命名 `{ }` 路段导入为一条宏，0–65535 坐标映射到虚拟桌面；`tpc` 展开为选点点击加常见传送确认。导出向导可选「耕地机」。
- 软件核心全局宏「取色」：MacroStudio 运行期间始终可用（暂停监听后仍可触发），默认 Up，可在设置中改键；复制光标 X/Y 与 `#RRGGBB`，并显示约 1.5 秒覆盖层。与用户宏触发键冲突时给出警告。
- 序列面板显示宏所在数据库，并提供类似资源管理器的可编辑路径栏，用于在宏库中跳转定位。
- 宏库探险器拖拽时可用鼠标滚轮滚动，多选拖入顶层文件夹可正常投放。

## 验证

- `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release`：`319` 项测试通过
- `scripts\Build-Installer.ps1 -Version 1.4.0 -Configuration Release`：成功生成 x64 安装包与便携 zip

## 版本信息

- .NET assembly/package version：`1.4.0`
- Installer `AppVersion`：`1.4.0`
- Git tag：`v1.4.0`
