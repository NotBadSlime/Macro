# 播放精度方案（时间间隔与启动延迟）

本文整理 MacroHID 当前实现中决定"间隔准不准"和"热键按下后多快开始"的全部机制，给出代码位置、已有实测值、本轮优化及其 A/B 结果，以及已经试过并撤回的方案。以函数名为主索引（行号会随修改漂移）。

相关文档：[precision.md](precision.md)（面向用户的档位说明）、[single-macro-tail-and-end-to-end-2026-10-14.md](single-macro-tail-and-end-to-end-2026-10-14.md)（端到端实测）、[阶段计划](superpowers/plans/2026-10-14-scheduling-latency-phases.md)。

## 1. 两个独立的选择：档位与代际

| 界面名 | `PrecisionMode` | 执行后端 | 最终自旋窗口 | 执行线程 |
|---|---|---|---|---|
| 低占用 | `Balanced` | 托管 `QpcPlaybackDelayStrategy` | 100µs | AboveNormal |
| 均衡（默认） | `ExtremeDuringPlayback` | 原生共享调度器（原生 mode 1） | 300µs，单宏自适应到 800µs | HIGHEST |
| 极限 | `UltraLowJitter` | 原生共享调度器（原生 mode 2）+ 辅助线程救援 | 1000µs | HIGHEST + MMCSS "Pro Audio" + 绑核 |

注意界面名与枚举名错位："低占用"的枚举叫 `Balanced`，"均衡"的枚举叫 `ExtremeDuringPlayback`。持久化的是枚举名，不能改。来源：`src/shared/MacroHid.Core/PlaybackPerformancePolicy.cs`。

**精度代际**（`src/shared/MacroHid.Core/PrecisionGeneration.cs`）与档位正交，决定算法版本：

| 代际 | 原生引擎 | 时间线规则（按键下限只推迟自身抬起、循环按写下周期） | 按住时长起点 |
|---|---|---|---|
| `Latest`（默认） | 共享调度器 | 新 | SendInput **开始**时刻 |
| `V2_0_2` | 共享调度器 | 新 | SendInput 返回时刻 |
| `V2_0` | 共享调度器 | 旧 | SendInput 返回时刻 |
| `V1_6` | 旧待命/内联引擎 | 旧 | SendInput 返回时刻 |

## 2. 时间间隔：计划如何变成准时的输入

### 2.1 编译期
- `InputActionCompiler` 把宏展开成带 `DueTick`（QPC 刻度）的动作序列。
- 按键下限：无间隔按键默认按住 10ms、组合键 30ms（`InputPressTiming` / `InputPressFloor`，`PressReleaseGapTracker.cs`）。`Latest`/`V2_0_2` 下普通按键下限只推迟该次抬起，组合键下限仍会推迟后续动作，避免 "Ctrl+A、点击" 变成 "Ctrl+点击"。
- `CompiledPlaybackPlan.BuildBatches`：**DueTick 相同的动作合并为一个批次**（两个鼠标移动、同一键的按下与抬起除外）。不同截止时刻永不合并——提前合并会直接破坏间隔。
- `ExportNativeTimeline` 把全部批次展开成一个连续 INPUT 数组，`SendInputEncoder` 在此时完成绝对坐标换算和扫描码填写。原生侧 `MhpCreatePlan` 复制该数组并预计算释放依赖与组合键边沿。

### 2.2 原生共享调度器（均衡、极限）
`src/native/MacroHid.NativePlayback/SharedPlaybackScheduler.inl`：
- 两个常驻工作线程，各持一个 `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` 计时器，优先级 HIGHEST；极限档额外注册 MMCSS Pro Audio 并绑定到测得抖动最低的两个物理核。
- 每个批次：剩余时间大于"自旋窗口 + 50µs"时用可等待计时器睡眠（单段 ≤10ms），进入窗口后 `_mm_pause` 自旋到截止时刻，然后 `SubmitClaimedBatch` → **每批一次 `SendInput(count, ptr, sizeof(INPUT))`**，直接指向计划内数组，无复制、无分配。
- 单宏连续路径 `PumpSingleRun`：队列里只有一个运行时不再加队列锁、不 Notify；均衡档自旋窗口按计时器实际超时量从 300µs 单调增大到最多 800µs。
- 极限档非 owner 线程以 +2µs 余量参与救援（owner 被抢占时由另一线程提交）。
- 释放依赖：按键抬起不早于按下 + 下限；`Latest` 从按下批次 SendInput 开始时刻起算，其余代际从返回时刻起算。
- 例外：批次中有相对鼠标移动时，每个移动单独 SendInput，发送前查询一次光标与 clip 范围（用于回中/解除 ClipCursor）；相邻的按键、按钮仍合并为一次调用（`SubmitSplittingRelativeMoves`）。
- 提交只唤醒 owner 线程（极限档两个都唤醒，因为非 owner 会参与救援）；完成事件来自小型复用池。

### 2.3 托管路径（低占用、或无法走原生时）
`QpcPlaybackDelayStrategy.WaitUntil`（`MacroPlaybackExecutor.cs`）：高精度计时器睡眠到自旋窗口外，然后按剩余时间分层 `Sleep(1)` / `Sleep(0)` / `SpinWait`。`SendInputMacroSink` 用预编码并缓存的 `NativeInput[]` 每批一次 SendInput；组合键保持的等待同样用线程级高精度计时器，最后 300µs 自旋。

走原生的条件：原生准备成功、非后台窗口、档位为均衡/极限、无 NoWait、sink 为 `SendInputMacroSink`、不含实时像素条件（`CanAttemptNativeIteration`）。原生一旦提交过输入就不再托管重放，防止重复输入。

### 2.4 系统级设置
`PrecisionPlaybackContext`：
- 计时器分辨率：先 `NtSetTimerResolution(0.5ms)`，失败退回 `timeBeginPeriod(1)`。
- 退出节能限制（`EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION`）。
- 存在均衡/极限热键时持有"预热租约"，空闲也保持上面两项，省掉每次 20–130µs 的开启开销；低占用档不持有。
- 均衡/极限：线程 Highest、关闭优先级提升、GC `SustainedLowLatency`（不进入 NoGCRegion）。

## 3. 启动延迟：热键到首个 SendInput

```mermaid
flowchart LR
  Hook["钩子线程 MacroHID-Hotkeys: 回调/过滤/匹配"] --> Ctrl["MacroPlaybackController.StartRunCore"]
  Ctrl -->|"交接 H1: Monitor.PulseAll"| MW["托管工作线程: Enter/Scopes/TryGetPreparedPlan/安全注册"]
  MW --> Submit["NativePlaybackEngine.TryRunPrepared -> MhpSubmitPlanAt"]
  Submit -->|"交接 H2: SetEvent"| NW["原生工作线程: 扫描/自旋"]
  NW --> SI["SendInput"]
```

| 段 | 内容 | 位置 |
|---|---|---|
| 钩子回调 | QPC 时间戳、过滤注入事件、手势匹配、前台进程缓存匹配 | `GlobalKeyboardHook.cs`、`MainWindow.KeyboardHook_TriggerPressed` |
| 分发 | 新建 CTS/TCS、追踪点、投递到常驻托管工作线程 | `MacroPlaybackController.StartRunCore`、`PlaybackWorkers` |
| 托管准备 | 精度上下文、作用域、命中预备计划缓存、注册输入安全 | `MacroPlaybackExecutor.RunCore` |
| 原生提交 | 固定内存、`MhpSubmitPlanAt`（时间线起点为托管侧 `iterationStartTick`），唤醒原生工作线程 | `NativePlaybackEngine`、`NativePlaybackInterop.SubmitAndWait` |

预备工作都在进入监听时完成（`executor.Prepare`：自旋校准、`CompiledPlaybackPlan`、`MhpCreatePlan`、输入安全计划），触发时只查缓存。应用启动时 `PlaybackPathWarmup` 先用惰性 sink 跑一遍托管路径，再用无输入步骤的宏跑一遍真实原生分支，使首个热键不再支付 JIT。

本轮之前的实测（2026-10-14，N=2000，Win32 接收端）：钩子 → 原生首次提交 P50 296µs、P99 616µs、P99.9 2387µs、最大 4982µs；原生启动到首次提交 13–31µs；冷启动首个热键因 JIT 约 44ms（`TriggerPressedAsync`）。钩子返回到目标窗口收到 `WM_KEYDOWN` 另有约 2.9ms（P50），属于系统输入队列与目标程序，不在本程序控制范围内。SendInput 单次调用本身约 0.05–0.65ms。

## 4. 试过并撤回/否决的方案

| 方案 | 结论 | 依据 |
|---|---|---|
| 把 TriggerPressed 移出钩子线程（阶段 2.2） | 不实施：钩子同步段 P99 从 131µs 降到 74µs，但多一次交接，启动 P50 无净收益 | 阶段计划 |
| 定向唤醒，每批只唤醒 owner（阶段 3.1） | 撤回：双宏均衡 P99.9 485µs，基线 409µs | 阶段计划 |
| 自旋窗口衰减（阶段 3.2） | 未实施：底噪下分辨不出收益 | 阶段计划 |
| 两工作线程原子令牌接力同一计划 | 撤回：最大迟到 1933µs，对照 1202µs | 单宏尾部实测 |
| 取消极限档 2µs 救援余量、立即竞争 | 撤回：P99.9 2.7 → 3.4µs | 单宏尾部实测 |
| 极限档关闭辅助线程 | 否决：CPU 减半但 P99.9 从 3–5µs 升到 22–87µs | 单宏优化记录 |
| 用 `INPUT.time` 定时 / 跨截止时刻预合并 | 否决：破坏间隔语义 | 调研文档 |
| 驱动级注入 | 暂缓：不必然更快，引入签名与兼容风险 | 调研文档 |

## 5. 本轮优化（启动延迟）

按"先测后改、逐项保留或撤回"进行，每项都有独立提交与 A/B 数据：

| 改动 | 位置 | 结论 |
|---|---|---|
| 预热按实际档位配置原生工作线程；空闲线程在档位变化时重新配置 | `NativePlaybackWarmup.cs`、`SharedPlaybackScheduler.inl` `Warm()`/`Loop()` | 保留。此前固定以极限（mode 2）预热，均衡档首次运行要在首个 SendInput 前撤销 MMCSS 与亲和性。均衡稳态 P50 85.7 → 61.6µs |
| 启动时预热真实原生分支（无输入步骤的宏，不注入任何输入） | `PlaybackPathWarmup.RunNative` | 保留。冷启动首个热键 均衡 2004 → 266µs、极限 2014 → 231µs |
| `DOTNET_TieredPGO=0` / `TieredCompilation=0` | 环境变量 | 不采用：前者两轮结果相反，后者冷启动变差（379 vs 286µs） |
| 缓存 `MACROHID_LEGACY_PLAYBACK`、静态引擎顺序数组、直接等待原生完成句柄、宏调用引用缓存、启动门快速路径、钩子手势判断去 LINQ | `NativePlaybackEngine.cs`、`NativePlaybackInterop.cs`、`PlaybackMacroSnapshot.cs`、`MacroPlaybackExecutor.cs`、`GlobalKeyboardHook.cs` | 保留。去掉每次运行的分配与系统调用；启动差异在轮间噪声内，尾部略低 |
| 原生完成事件池化 | `SharedPlaybackScheduler.inl` `AcquireCompletionEvent` | 保留。假输入 2000 次：提交调用 P50 3.7 → 2.8µs，提交到首次调用 P99.9 87–126 → 15–35µs |
| 非极限档提交只唤醒 owner | `SharedPlaybackScheduler.inl` `Submit`/`NotifyWorker` | 保留。提交调用 P50 2.8 → 1.5–2.0µs；双宏并发均衡 P99.9 不变（338/184 vs 330–349/175–177µs）。与已撤回的 3.1（每批定向唤醒）不同，每批 Notify 未改 |
| 相对鼠标移动：一次 clip 查询、相邻按键合并一次 SendInput、托管复用单元素数组 | `NativePlayback.cpp` `SubmitSplittingRelativeMoves`、`MouseCursorUnclip.cs`、`SendInputMacroSink.cs` | 保留。减少调用与分配；启动探针只含按键，未单独测量 |
| 托管 sink 缓存 QPC 频率；组合键等待改高精度计时器 + 300µs 自旋 | `SendInputMacroSink.cs` | 保留。原 `Thread.Sleep(1)` 可能多睡一个计时器周期 |

## 6. 测量记录

工具：`scripts/Measure-StartupLatency.ps1`（单次测量）、`scripts/Compare-StartupLatency.ps1 -Baseline <rev>`（基线在临时 worktree 构建，A/B 交替运行，取各轮中位数）。探针 `src/tools/StartupLatencyProbe` 复现 MacroStudio 进入监听前的准备（启动预热、路径预热、精度预热租约、`executor.Prepare`），在 AboveNormal 线程调用 `TriggerPressedAsync`，每次向前台窗口注入 F24 按下/抬起，阶段时间取自 `MacroHid-Playback` EventSource。终点：原生档为 `NativeFirstSubmit`，低占用为托管 `SubmitStart`。

最终对比（基线 `aa230ac` = 本轮开始时的代码合入 2.0.2；3 轮交替，每轮 8 个冷进程 + 1000 次稳态；单位 µs）：

| 档位 | 冷启动首个热键 | 稳态 P50 | 稳态 P99 | 稳态 P99.9 |
|---|---|---|---|---|
| 低占用 | 236 → 231 | 90.7 → 90.6 | 243 → 218 | 5182 → 6397 |
| 均衡 | **1969 → 243** | 60.0 → 51.6 | 145 → 117 | 7193 → 970 |
| 极限 | **1908 → 207** | 45.6 → 45.1 | 124 → 117 | 942 → 930 |

说明与局限：
- 数据来自同一台 Windows 11 / Ryzen 9 7945HX 电脑，机器同时在运行其他程序；P99.9 每轮只有 1–2 个样本落在尾部，轮间波动可达数毫秒（例如低占用两侧都在 5–6ms），不应解读为回归或改进。
- 测量终点是本程序调用 SendInput 的时刻，不含 SendInput 本身（0.05–0.65ms）以及系统输入队列到目标窗口（约 2.9ms P50）。
- 未重新测量物理按键端到端；`EndToEndProbe` 需要手动按键与前台接收窗口。
