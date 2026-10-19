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

## 7. 延迟的四段与可修正性（2026-10-16 追加）

原则：**恒定的延迟可以预先扣除，波动的不行**。宏内部步骤之间的间隔是绝对时间线，整体晚开始不会改变间隔。

| 段 | 本机实测 | 处理 |
|---|---|---|
| ① 热键 → 首次 SendInput | 0 提前量：P50 48–50、P90 64–77、P99 106–135µs（波动） | **热键启动对齐**（精度控制，默认 0 关闭）：首轮锚定到钩子回调时刻 + 提前量。400µs：P50 = P90 = 400.0、P99 400.1µs；600µs：P99 600.1µs。来不及时立即开始（`PlaybackTrigger.StartCounts` 计数）。首个批次在 1ms 内改为自旋，避免计时器唤醒误差破坏恒定值 |
| ② SendInput 本身 | 按键中位约 190–300µs、标准差约 210µs；鼠标移动中位约 105µs。按键慢是因为要同步经过系统里**所有**低级键盘钩子（本机有输入法、QQ、MSI Afterburner、Armoury Crate、雷蛇） | **按耗时提前提交**（默认开，仅最新精度版本）：原生调度器按「纯按键 / 含鼠标」两类各记最近 64 次调用耗时的中位数（上限 1ms），提前这么多提交；释放依赖与组合键仍从按下的实际开始时刻起算，按住时长不会短于写下的值（原生测试：学到 600µs，按下提前 600µs，按住正好 5000µs）。热键 → SendInput **返回**（提前量 500）：中位 607–609 → 502µs。**Raw Input 热键**（精度控制，默认关）：本进程退出键盘钩子链 |
| ③ 到目标窗口 | 约 2.9ms（P50，系统输入队列 + 目标程序取输入节奏，游戏通常每帧一次） | 程序侧无法消除；可用 `EndToEndProbe` 对具体目标测平均值，在宏里扣除 |
| ④ 偶发最大值 | P99.9/最大值仍有数毫秒的系统卡顿 | 不可预测，不能修正；减少概率：高性能电源计划、避开中断多的核心（已有核心扫描），时间线为绝对时刻不会累积 |

### 低级钩子对 SendInput 的影响（`HotkeyInputProbe`，8 轮交替取中位，后台约 20% CPU 负载，单位 µs）

| 本进程的热键前端 | 按键 P50 / P90 / P99 | 鼠标移动 P50 / P90 / P99 |
|---|---|---|
| 无 | 168 / 630 / 902 | 121 / 190 / 359 |
| 低级钩子（原方案） | 249 / 954 / 1280 | 113 / 145 / 228 |
| Raw Input（仅键盘） | 188 / 679 / 976 | 156 / 242 / 353 |

- 低级钩子让本进程注入的每个按键多等约 80µs（P50）/ 380µs（P99），而且这也作用于用户自己打的每个字。Raw Input 把按键几乎恢复到无钩子水平。
- 代价：Raw Input 在所有钩子之后才收到按键，热键被察觉约晚 190µs（独立实验：低级钩子检测 P50 44–54µs，Raw Input 233–244µs）。
- 鼠标：只有绑定用到鼠标按键时才注册鼠标 Raw Input——注册后每个注入的鼠标移动都会产生 WM_INPUT，实测拖慢移动。表中 Raw Input 行未注册鼠标，鼠标仍略慢于钩子，原因未定（与钩子模式下鼠标反而比无钩子快一样，可能是系统调度差异，噪声内）。
- 取色键的吞键改用 `RegisterHotKey`；无法注册（被占用或是鼠标/多键手势）时整体回退低级钩子。
- 默认仍为低级钩子：热键检测更早，且 Raw Input 模式尚未经过完整人工回归。追求「每个按键都快、稳」时可在精度控制中开启。
- 已知问题（与本轮无关，原方案就有）：低级钩子模式下进程退出时偶发 WPF `ManagedWndProcTracker` 无效窗口句柄异常（探针 3 次中 1–3 次）。
## 8. 嘈杂核心检测与规避（2026-10-16 追加）

偶发最大值集中在少数「嘈杂核心」上：设备中断/DPC 默认落在 CPU 0，另有某个设备的中断目标核（本机 CPU 24）。逐核自旋 1 秒记录线程无法运行的时段：

| 核心 | >50µs 次数/秒 | >200µs 次数/秒 | 最长 |
|---|---|---|---|
| CPU 0 | 340–656 | 11–79 | 8.7–9.9 ms |
| CPU 24 | 410 | 234 | 0.67 ms |
| 其余大多数 | 0–10 | 0 | 11–170 µs（偶有一次 1.5 ms） |

实现：
- 原生 `MeasureCandidateCore`（核心扫描）除计划时刻迟到外，同时记录相邻两次计数器读数之间的间隔（线程未运行的时段）。按**速率**判定嘈杂：>50µs 每秒 ≥100 次、或 >200µs 每秒 ≥20 次、或任一停顿 ≥2ms。嘈杂核心在评分中排到最后，极限档有足够安静核心时完全不选它们。
- `MhpScanCoreStalls` 导出逐核结果；`MhpSetQuietCores` 设置安静核心集合，作为**均衡档**原生工作线程的软亲和性（在集合内浮动，不固定单核；少于 2 个安静核心时不限制）。低占用档走托管路径，不受影响。
- `SystemLatencyCheck`：启动 3 秒后做 3 次短检查（每核 60ms，约 2 秒/次）；一个核心在**最近 3 次中 2 次**嘈杂才被规避，避免单次偶发事件让播放来回迁移。
- 界面：精度控制 → 系统延迟检查 → 逐核柱状图（对数刻度、嘈杂为红色）、汇总与建议；「自动避开嘈杂核心」默认开启并持久化。不修改任何系统设置。

一致性（本机，每核 60ms × 4 次、200ms × 4 次）：CPU 0 与 24 每次都判为嘈杂；CPU 2、9 各有一次因单个 2–4ms 事件被判嘈杂，被「3 次中 2 次」规则过滤。

效果（`LatencyProbe` 极限档，1ms × 10000，6 轮交替，单位 µs）：

| | 最大值中位 | 最大值最差 | P99.9 中位 |
|---|---|---|---|
| 旧扫描（只看计划时刻迟到） | 179 | 1243 | 2 |
| 新扫描（排除嘈杂核心） | 130 | 645 | 3 |

均衡档（软亲和性）6 轮交替：最大值中位 209 → 226、最差 1145 → 1340，差异在噪声内，未见改善也未见退化。均衡档单宏走单线程连续路径，在 31 个核心间浮动时本就很少落到 CPU 0/24，因此收益主要在极限档（固定核心）。

系统级的处理（迁移设备中断、MSI 模式、ReservedCpuSets）不在 MacroHID 内做，见独立工具。
## 9. Max（2.1，抢占档，2026-10-19）

「精度控制 → 精度模式 → Max（抢占）」，存储为 极限 + `MaxPreemption`（持久化的 `PrecisionMode` 不变），只在当前播放版本下生效。允许牺牲游戏帧率：监听和播放期间占满两个核心。

| 手段 | 位置 | 结论 |
|---|---|---|
| 工作线程 TimeCritical、进程 RealTime、MMCSS CRITICAL | `SharedPlaybackScheduler.inl` `configure`/`ApplyProcessClass` | 保留。单独开关 `MACROHID_MAX_NORT=1` 的对照差异在噪声内 |
| 所有等待纯自旋（不用计时器睡眠），50ms 连续自旋且离下个截止 >3ms 时歇 0.5ms（防止饿死内核线程） | `Breather` | 保留 |
| 电源保护：显示器关闭/变暗、睡眠或现代待机期间，以及返回后 120s 内，Max 退回极限行为（不自旋、不双核竞速、不热备、不占 SMT、HIGHEST、恢复进程优先级类） | `SharedPlaybackScheduler.inl` `RegisterPower`/`UpdatePowerHold`，`MhpGetPowerHold` | 新增。2026-10-19 本机在退出现代待机 67s 后 0x9F 蓝屏（`amdacpbus.sys` 电源 IRP 未完成），当时 8 核优先级 31 自旋 |
| **无锁双核竞速**：两个线程都自旋到截止时刻，一次 CAS 认领批次、不拿队列锁就提交 | `RaceSingleRun`/`CloseRace` | 保留。早期用队列锁认领的版本，持锁方被中断时另一方也被堵住，1ms 间隔下出现成串迟到；改为无锁后消失。原生测试覆盖：2000 批 20µs 间隔无丢失/重复、竞速中第二个运行到来时交接、中途取消 |
| 热键待命自旋：原生工作线程与下一个托管播放线程空闲时自旋（每 2ms Yield） | `hotStandby`、`PlaybackWorkers.SpinForWork` | 保留 |
| SMT 兄弟核停车（实时优先级 pause 循环占住超线程兄弟） | `StartParkers` | 默认关（`MACROHID_MAX_PARK=1` 开）：10ms 间隔压低了个别最大值，1ms 间隔 P99.9 反而变差 |
| 只在系统延迟检查判定的安静核心里选核 | — | 撤回：P99.9 中位 6.5 → 22µs |

实测（假输入，`scripts/Compare-MaxPrecision.ps1`，8 轮交替，单位 µs）：

| 间隔 | 档位 | 最大值中位 | 最大值最差 | P99.9 中位 | >100µs 次数 |
|---|---|---|---|---|---|
| 10ms × 1200 | 极限 | 17 | 671 | 2 | 4 / 9600 |
| 10ms × 1200 | Max | **2** | **173** | 1 | 2 / 9600 |
| 1ms × 5000 | 极限 | 181 | 1832 | 2 | 10 / 40000 |
| 1ms × 5000 | Max | **87** | 1675 | 1 | 15 / 40000 |

热键 → 首个 SendInput（`StartupLatencyProbe --precision extreme [--max]`，4 轮 × 600 次）：P50 112 → **69**、P90 161 → **115**、P99 256 → **197**；P99.9 两边都约 0.9–1.1ms。

**剩下的最差值从哪来（更正）**：早先用 `tmp/stallsync` 看到「每 30 秒约 2 次、同时落在所有核上的 330–1300µs 停顿」，当时归因为固件 SMI 或监控/杀毒驱动，这个结论是错的。加上内核跟踪（`tmp/stalletw`，记录 DPC/ISR、线程切换、离核原因，以及 .NET 自身的 GC 事件）复查后：

- 30 秒周期来自开发环境里残留的 C# 编译服务器 `VBCSCompiler`，它每 30 秒在所有核上同时跑一次线程（优先级 11）。这是普通抢占，抢不动 Max 的优先级 31 线程，用户机器上也不存在。
- 带跟踪的测量工具中 5/5 次全核停顿都与它**自身**的 GC 挂起重合（离核原因全是 `wait=Suspended`）。这是测量工具自己造成的。
- 排除这两项后，测量线程优先级 15、合计约 8 分钟的测试里只剩 1 次全核停顿（510µs，类型未确认）。关闭 ASUS 全套服务、NVIDIA 容器/Overlay、WSL，或禁用 NVIDIA Platform Controllers and Framework（`nvpcf`），都没有可见差别。

这也说明 MacroHID 自己的 GC 会在托管路径上造成数毫秒停顿，于是有了第 10 节。

## 10. 播放期间禁止 GC（2.1）

`PlaybackGcPause` / `PlaybackGcOptions`，规则在「高级设置」里由用户设定。只对极致、极限（含 Max）档、当前引擎生效；1.6.0 代数保留它当年每次运行一个 no-GC region 的做法。

- 发键的原生工作线程不是托管线程，GC 从来停不住它们。会被 GC 停住的是托管路径：热键交接、循环两轮之间的交接、托管批次循环、条件监控。
- `GC.TryStartNoGCRegion` 进入时自身可能先回收一次（实测：热堆 1–40µs，冷启动 64MB 约 11–33ms）。所以不放在第一批之前：原生运行在计划被接收后（`SubmitAndWait` 提交成功后）进入，托管批次循环在第一批发出后进入。
- 单轮写下的时长不超过「单轮不超过」秒数（默认 120 秒，填 0 关闭）的宏才进入；更长的宏照常回收。
- 循环播放：只在一轮最后一批之后、下一轮开始之前回收一次（结束并重新进入 region）。需要同时满足：离下一轮不少于设定的毫秒数（默认 3ms），预留内存已用到设定的百分比（默认 50%）或 region 已失效（可关），没有条件监控在跑（可改为允许）。
- 预留内存默认 32MB（4–1024MB）。一轮用超了，运行时会自己结束 region、照常回收；`disallowFullBlockingGC: true` 保证进入时不会做完整的阻塞回收。
- `MACROHID_NO_GC_PLAYBACK=0` 整体关闭，用于对照测试。
- 托管控制流路径（带条件分支、跳转、子宏调用的宏）暂未接入。
