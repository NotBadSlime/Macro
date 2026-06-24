# MacroHID 1.1.0 正式版

MacroHID `1.1.0` 是当前正式版。这个版本把宏数据库升级为进程分组管理，并把高性能精度档切到 native-lite，同时保留极限档 native-ultra。

## 主要变化

- 宏数据库管理新增进程组/数据库层级：宏可以按前台进程启用，同一个热键可以在不同进程组里分别生效。
- 旧宏库会无损迁移到“全局”进程组；删除进程组时宏会迁回全局，不直接丢失。
- 宏数据库管理支持渐进式进入数据库视图、多选数据库监听、一键监听/停止指定数据库，并用紧凑状态标识显示监听状态。
- 进程过滤改为进程组能力，支持选择正在运行的进程或选择可执行文件路径。
- 高性能档改为 C++ native-lite：使用 native-inline、目标 250us，不启用 standby、CPU scan、自动绑核或救援 worker。
- 极限档继续使用 native-ultra：standby 优先、inline fallback，可配合 affinity 压低尾部抖动。
- 条件序列/OCR/像素/模板识别改为混合执行：主时间线保持 native，条件监控继续在 managed 辅助线程触发 then-actions。
- 内嵌 `PixelWhenStep` 第一阶段仍按 managed fallback，并提供明确 fallback reason。
- 宏数据库拖拽、滚轮滚动、拖放位置标识、夜间模式文字颜色、重复标题和主题切换等 UI 问题已修复。

## 验证

- `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj`：`200 test(s) passed`
- `dotnet build MacroHID.sln --configuration Release`：成功，`0` 警告，`0` 错误
- `scripts\Build-LocalRun.ps1 -Configuration Release`：成功生成本机运行目录
- `scripts\Build-Installer.ps1 -Configuration Release`：成功生成 x64 安装包

## 精度状态

- 基础档：managed，面向通用稳定使用。
- 高性能档：native-lite，低侵入 native-inline；本机短测显示普通尾部明显优于 managed，但由于不绑核、不 standby，仍可能出现数百微秒级偶发尖峰。
- 极限档：native-ultra，继续作为压制长尾抖动的推荐方案。

## 版本信息

- .NET assembly/package version：`1.1.0`
- Installer `AppVersion`：`1.1.0`
- Git tag：`v1.1.0`
