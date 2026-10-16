# MacroHID 用户态性能档位与验收

2026-10-14 单宏尾部与真实接收补充：保留原生线程预配置与空闲配置保留，撤回两项尾部退化的实验；最终版本通过 30 次物理热键及 Win32/WPF 各 2,000 次自动接收。具体数值、最大值波动、测量起止点和复现命令见 [实测报告](single-macro-tail-and-end-to-end-2026-10-14.md)。原生微秒级调度误差不能代替端到端接收验收。

## 当前策略

新安装默认均衡；既有配置与宏文件中的枚举、序列化值不变。档位修改只影响下一次运行。Windows 用户态 + SendInput，不调整游戏画质、驱动、中断亲和性或全局电源计划。

| 显示名称 | 原枚举 | 静态执行 | 最终自旋窗口 | 执行优先级 | 场景 |
|---|---|---|---|---|---|
| 低占用 | Balanced | 托管预编译 | 100µs | AboveNormal | CPU 紧张、普通自动化 |
| 均衡（推荐） | ExtremeDuringPlayback | 原生预编译 | 300µs | Highest | 多数游戏、混合宏 |
| 极限 | UltraLowJitter | 原生预编译、两个常驻工作线程 | 1000µs | MMCSS Pro Audio，失败时 Highest | 对 1–2ms 节奏敏感 |

进程维持正常优先级；托管协调线程不注册 MMCSS，也不绑核。原生两个执行线程共享所有运行；只有极限启用迟于截止时间 2µs 的辅助领取。极限准备阶段选择不同物理核心上的两个逻辑处理器，配置不满足时使用兼容路径并显示原因。原生空闲线程事件等待。

自旋窗口不是精度承诺。1ms 一个截止时间时，100/300/1000µs 忙等可能消耗约一个逻辑处理器的 10%/30%/100%，另加提交开销。超过两个并发宏仍执行，界面提示超出验证规模。

2026-10-14 单宏增量：只有一个可运行计划时，原生均衡使用连续执行路径，运行中按 timer 实际超时量将最终自旋窗口从 300µs 有界提高到最多 800µs；单次运行内不降低窗口。适应只采纳计时器到期唤醒，不采纳控制事件唤醒。该策略优先改善调度尾部，可能增加 CPU 时间，尚未通过游戏帧时间验收。极限的间隔事件继续使用双工作线程辅助领取；仅当前有效截止已临近、相邻名义批次间隔不超过 20µs 的单宏密集段使用连续路径。暂停、取消或新任务改变 generation 时交回共享调度。设置环境变量 `MACROHID_DISABLE_SINGLE_RUN_FASTPATH=1` 并重新启动进程可关闭连续路径及其自旋适应；计划预计算和统计缓冲复用仍保留。

## 已实现的路径

- 监听准备时校准、编译静态计划及可缓存的条件动作，初始化原生工作线程和测核。校准按精度档及效率核心类别缓存。触发构造等待策略不再执行校准或 CPU 扫描。
- 原生计划保留核心选择快照；运行保留配置、计划、控制对象与完成事件。后台重新测核不重启正在运行的引擎，也不更改进程亲和性。
- 原生共享队列每个运行仅暴露下一个批次；等待和 SendInput 均在队列锁外。每个运行仅一个在途批次；完成后才发布下一批。长等待不会独占执行权。
- MhpSubmitPlanAt 提交并返回运行句柄及借用的完成事件；MhpGetRunResult / MhpGetRunTrace 查询完成结果；MhpReleaseRun 取消并等待退出后释放。调用方必须保留诊断缓冲到完成，且释放后不再操作句柄。
- 取消、暂停、恢复、新任务唤醒工作线程。部分提交失败立即停止，托管层不得重放整个计划；已有安全释放路径负责释放输入。
- 沿用原始时间线起点；按实际按下提交完成时间约束对应释放的最早时间，避免迟到后压缩按住时长。迟到统计仍对照原始计划截止时间。
- 不再进入可触发完整阻塞 GC 的 NoGCRegion。会话共享 SustainedLowLatency 并恢复原状态。静态计划复用两套原生统计工作区和诊断数组；额外并发独立租用。
- 静态宏不因 PixelMode.Live 每轮重编译；随机等待与动态条件仍在原执行语义下求值。
- 截图、模板、像素哈希与 OCR 内部使用连续 BGRA 缓冲；缓存持有所有权，消费者持有独立租约。归还前等待消费者结束，公开 RgbColor 模型与序列化不变。
- 模板预计算搜索亮度与积分统计量；阈值、最佳位置竞争、0.999 提前退出边界附近及低方差窗口按原累加顺序回算。
- 同批次、无需相对鼠标重定位的输入批量 SendInput，不跨截止时间合并。
- 条件识别线程 AboveNormal、100µs 轮询等待尾部；不增加识别并发，不引入全局识别队列。

旧宏未设条件轮询间隔时仍为 25ms，切档不覆盖。新建快速像素条件可手动参考 10/2/1ms；模板和 OCR 不使用这些建议。

设置窗口展示响应目标、CPU 影响、适用场景；主窗口状态条展示准备状态、实际后端，悬停查看回退原因。没有基于 FPS 自动换档。若运行期间请求了尚未缓存的新核心配置，准备会被推迟并显示原因；停止后重新监听可重新准备。该次兼容运行不适用原生精度目标。

## 目标（不是实测值）

以下目标只覆盖预热后的静态键鼠路径、1–2 个并发宏。

| 指标 | 低占用 | 均衡 | 极限 |
|---|---:|---:|---:|
| 热键回调入口至首次提交开始 P99 | ≤1ms | ≤0.5ms | ≤0.25ms |
| 原始截止时间至提交开始迟到 P99.9 | ≤500µs | ≤250µs | ≤100µs |
| 假输入路径取消到清理完成 P99 | ≤1ms | ≤1ms | ≤1ms |
| 300ms 长计划旁的 1ms 短计划总耗时 | <5ms | <5ms | <5ms |
| 独立取消待执行短计划 | <5ms | <5ms | <5ms |
| 平均 FPS 下降预算 | ≤1% | ≤2% | ≤3% |
| P99 帧时间增加预算 | ≤2% | ≤3% | ≤5% |

640×360 转换阶段目标：相对 RgbColor 对象数组，分配下降至少 85%、中位耗时下降至少 50%。截图、模板、OCR 和条件端到端耗时另测。

## 验证与追踪

普通 CI 包含托管功能回归、原生假输入回归和帧时间比较器测试。原生测试以编译开关替代输入 API，不向桌面发送输入；生产 DLL 不包含此替代路径。

    .\scripts\Build-NativePlayback.ps1 -Configuration Release
    dotnet build tests\MacroHid.Core.Tests -c Release -m:1
    .\tests\MacroHid.Core.Tests\bin\Release\net8.0-windows\MacroHid.Core.Tests.exe
    msbuild tests\MacroHid.NativePlayback.Tests\MacroHid.NativePlayback.Tests.vcxproj /p:Configuration=Release /p:Platform=x64
    .\tests\MacroHid.NativePlayback.Tests\x64\Release\MacroHid.NativePlayback.Tests.exe
    python tests/test_frame_comparison.py

固定性能机可设置 MACROHID_PERF_GATES=1，启用原生 5ms 回归和转换耗时门槛。绝对性能门槛不进入共享 CI。

双宏原生调度探针（每条 50000 批，1ms 节奏；总计十万个事件；预热完成后申请与托管播放会话一致的 0.5ms 计时器分辨率）：

    .\tests\MacroHid.NativePlayback.Tests\x64\Release\MacroHid.NativePlayback.Tests.exe --benchmark 1 50000 balanced.csv
    .\tests\MacroHid.NativePlayback.Tests\x64\Release\MacroHid.NativePlayback.Tests.exe --benchmark 2 50000 extreme.csv

CSV 保留 run、batch、due_qpc、submit_qpc、frequency、late_us。这是原生调度层假输入探针，不能替代热键入口、真实 SendInput、接收程序或游戏测试。百分位采用最近秩；不得删除长尾、重置时间线或只展示均值。

可启用 EventSource **MacroHid-Playback**（Event ID 1）采集 runId/stage/qpc/frequency，区分 Hotkey、Dispatch、WorkerStart、PlanReady、Deadline、SubmitStart/End、CancelRequested、CleanupComplete 与原生排队/首次/最后提交/完成。默认未启用，无事件分配。接收程序必须自行在处理入口调用 PlaybackTrace.Mark(Receiver, qpc, runId) 或记录同机 QPC；SendInput 返回不能代表游戏已处理输入。取消请求、最后提交、原生退出及托管清理完成不得混为同一时间点。

## 游戏对照验收

使用固定回放或可重复场景，固定游戏版本、设备、分辨率、画质、FPS 上限、电源状态和后台负载。纯播放与混合识别分别测试；冷启动、预热、CPU 受限、GPU 受限、像素、模板/OCR 并发分别记录。每组至少五次交替顺序测试，每次十分钟；每个时序场景至少十万个有效事件。

采用 [PresentMon 官方命令行工具](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md)。示例：指定一个进程，--v1_metrics --timed 600 --terminate_after_timed --output_file capture.csv。比较器默认使用 MsBetweenPresents；这是呈现间隔，其他帧时间口径须显式指定，不得混用。若文件含多个交换链，需显式筛选，不能相加计算 FPS。

    python scripts/compare_frame_times.py --profile balanced --scenario playback --baseline off1.csv off2.csv off3.csv off4.csv off5.csv --candidate on1.csv on2.csv on3.csv on4.csv on5.csv --output balanced-playback.json

平均 FPS 为有效帧数/总帧间隔，不平均瞬时 FPS。比较器逐组判定，保留异常行数，拒绝不足五组、时长不足和基线波动超预算的通过结论；退出码 2 表示失败或不确定。它无法替用户确认画质、电源、回放和采集顺序一致，应随数据保存实验记录。极限精度达标但帧时间超预算时仍推荐均衡，不自动更改用户档位。

## 精度版本

「精度控制」窗口里的「精度版本」下拉选择使用哪一代定时行为，和低占用/均衡/极限（CPU 投入）互相独立，默认「最新」，仅影响下一次运行。旧设置文件没有该字段时按「最新」读取；设置里出现本版本不认识的名字时只把该项回落为「最新」，其余设置保留。

| 版本 | 按键时长起算点 | 按键下限（默认 10ms） | 带尾部等待、无条件的循环 | 原生引擎 |
|---|---|---|---|---|
| 最新 | 发送调用开始时 | 只推迟它自己的释放，时间线不动 | 按所写周期重复，相位锁定 | 共享调度器 |
| 2.0.2 | 发送调用返回时（多约 0.05–0.65ms） | 只推迟它自己的释放，时间线不动 | 按所写周期重复，相位锁定 | 共享调度器 |
| 2.0.x（2.0.0 与 2.0.1 定时行为相同） | 发送调用返回时（多约 0.05–0.65ms） | 同时推迟其后的时间线 | 约每 10ms 重复一次，尾部等待被丢弃 | 共享调度器 |
| 1.6.0 | 同 2.0.x | 同 2.0.x | 同 2.0.x | 旧的待命/内联引擎 |

1.6.0 到 2.0.1 之间的编译、按键下限与循环规则没有变化（相关源文件逐一比对过）；2.0.2 加入时间线修复；「最新」再加上按键时长从发送调用开始起算（2.0.2 发布说明里该项属于未提交的原生调度改动，不含在 2.0.2 内）。所以这四档只有上表这几处差别。选 1.6.0 只切换到当时的原生引擎；它之后对共享调度器的其他改动（批次追踪、预热等）不属于定时行为，不随版本切换。`MACROHID_LEGACY_PLAYBACK=1` 仍作为进程级诊断开关保留，效果等同选择 1.6.0 的引擎。
## 回退与历史工具

- MACROHID_LEGACY_PLAYBACK=1：恢复旧 native standby/inline 路径，仅用于诊断比较；不适用新调度器的并发和资源预算结论。
- MACROHID_DISABLE_PREPARE=1：跳过预准备，检查冷路径兼容性；不适用预热响应目标。
- MACROHID_LEGACY_IMAGES=1：恢复对象颜色转换与逐窗口统计的比较路径。公开模型没有迁移。
- 历史 Measure-PrecisionProfiles.ps1、-FailOnTargetMiss、-AutoTuneAffinity、Tune-LatencyAffinity.ps1、-TunePasses 与 edge-drop 核心候选策略仍可用于单路径诊断，不能代替双宏 + 游戏联合验收。
- Trace-LatencyOutliers.ps1 可捕获 WPR CPU 数据；用 Windows Performance Analyzer 对齐 DPC/ISR、上下文切换与 QPC 长尾。

当前实测和未完成的验收见 [current-precision-status.md](current-precision-status.md)。本机结果不能推广到未测试的游戏、版本和设备。
