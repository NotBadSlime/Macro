# MacroHID 1.2.0 正式版

MacroHID `1.2.0` 是当前正式版。本版本补齐宏播放控制流、条件动作与 native 主时间线的协作，并新增 GIMacros 格式适配和全新软件图标。

## 主要变化

- 播放 N 次模式支持再次按下触发键立即停止，行为与循环播放一致。
- 基础指令库和条件动作库新增“停止当前宏序列”与“停止所有宏序列”。前者只返回当前嵌套调用，后者终止整次播放。
- 宏允许调用自身；尾部自调用使用可取消执行路径，不会持续增长调用栈。
- 条件动作新增“同时执行”和“暂停主时间线”两种模式，可按条件选择并行执行，或完成条件动作后再恢复基础序列。
- 高性能/极限档的条件动作会优先使用 native-inline；暂停模式通过 native 控制句柄冻结主计划，并在恢复时平移剩余 deadline，避免集中补发。
- 条件动作中的“停止所有宏序列”可取消正在运行的 native 主计划，停止语义在 managed/native 混合执行中保持一致。
- 内置转换器新增 GIMacros JSON 适配，支持 `kd/ku/md/mu/wait/view/loop/import` 命令，并自动解析同目录及子目录中的导入文件。
- 软件图标更新为透明背景的绿色/青色轨道标识，并同步用于程序文件和安装器。

## 验证

- `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release`：`211 test(s) passed`
- `scripts\Build-NativePlayback.ps1 -Configuration Release`：成功，`0` 警告，`0` 错误
- `dotnet build MacroHID.sln --configuration Release`：成功，`0` 警告，`0` 错误
- `scripts\Invoke-SmokeTest.ps1 -Pixels skip`：通过
- `scripts\Build-Installer.ps1 -Version 1.2.0 -Configuration Release`：成功生成 x64 安装包

## 精度与执行后端

- 基础档继续使用 managed 调度，面向通用桌面自动化。
- 高性能档使用 native-lite，采用较轻的 native-inline 调度，不启用极限档的实时优先级、自动绑核和 standby worker。
- 极限档继续使用 native-ultra；可用时启用预创建计划、standby worker 和可选 affinity，以压低长尾抖动。
- OCR、像素和模板条件继续在 managed 辅助线程运行；确定性基础时间线与可转换的条件动作保留 native 执行能力。

## 版本信息

- .NET assembly/package version：`1.2.0`
- Installer `AppVersion`：`1.2.0`
- Git tag：`v1.2.0`
