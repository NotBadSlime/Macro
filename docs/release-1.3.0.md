# MacroHID 1.3.0 正式版

MacroHID `1.3.0` 是当前正式版。本版本整理宏库工具栏与右键菜单，新增导出向导与文件夹依赖打包，并延续此前对嵌套 `macro.call` 的 `.mcrx` ID 导入重映射能力。

## 主要变化

- 宏库工具栏仅保留「新建」下拉（宏文件 / 文件夹），去掉复制、粘贴、删除工具栏按钮；上述操作改由右键与快捷键完成。
- 右键菜单按目标区分：宏文件、宏文件夹、空白区域，菜单项更清晰、更贴合操作上下文。
- 导出改为向导流程：先选择导出格式；导出宏文件夹时再选择打包方式。
- 文件夹导出会打包主宏，并自动收集依赖的 `macro.call` 子宏，可选「两个子文件夹」或 ZIP 压缩包，便于整包分享。
- `.mcrx` 宏 ID 与导入时的嵌套 `macro.call` 重映射（本线此前提交）继续生效，导入后子宏引用不会错位。
- 触发键 TextBox 显示已修正垂直内边距与居中对齐，文字不再贴边或偏上。

## 验证

- `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj`：约 `278` 项测试通过
- `scripts\Build-LocalRun.ps1 -Configuration Release`：成功
- `scripts\Build-Installer.ps1 -Version 1.3.0 -Configuration Release`：成功生成 x64 安装包

## 版本信息

- .NET assembly/package version：`1.3.0`
- Installer `AppVersion`：`1.3.0`
- Git tag：`v1.3.0`
