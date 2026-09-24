# MacroHID 1.4.1 正式版

MacroHID `1.4.1` 是当前正式版。本版本把极限档位的选核交给用户勾选，并只在程序或目标进程刚启动时自动测量一次。

## 主要变化

- 宏库「全局运行」增加「选择核心」。列表显示逻辑核、物理核、性能核或能效核，以及同一物理核上的其他逻辑核。默认勾选性能核，不含逻辑核 0。
- 「测试」只测量勾选的核，并显示挑中的播放核和最大延迟。确定后写入极限核心掩码。播放或停止过程中不会开始测量。
- 极限模式启动时测一次。填了进程名的库在该进程第一次出现时再测一次，进程保持运行期间不重复。播放调用本身不再做全机扫描。
- 已安装过 MacroHID 时，安装程序仍显示目录页，并预填上次的安装路径。

## 验证

- `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release`：`324` 项测试通过

## 版本信息

- .NET assembly/package version：`1.4.1`
- Installer `AppVersion`：`1.4.1`
- Git tag：`v1.4.1`
