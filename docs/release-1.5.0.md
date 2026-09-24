# MacroHID 1.5.0 正式版

MacroHID `1.5.0` 是当前正式版。本版本补上选核窗口的逐核结果、自动勾选和按延迟排序。

## 主要变化

- 选择核心的列表改成深色主题，每行直接显示该核的最大延迟。测试过程中标出正在测量的逻辑核。
- 增加全选、全不选。测试结束后按最大延迟从低到高排序，未测量的核排在后面。
- 填写数量后点「自动选择」，按延迟从低到高勾选这么多个核。优先不同物理核，还有其他核可选时跳过逻辑核 0；物理核不够时再按延迟补上同核的超线程。
- 便携包根目录的 MacroStudio 快捷方式直接指向包内的 `MacroStudio\MacroStudio.exe`。

## 验证

- `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release`：`325` 项测试通过

## 版本信息

- .NET assembly/package version：`1.5.0`
- Installer `AppVersion`：`1.5.0`
- Git tag：`v1.5.0`
