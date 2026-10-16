# 播放精度方案（时间间隔与启动延迟）

本文整理 MacroHID 当前实现中决定"间隔准不准"和"热键按下后多快开始"的全部机制，给出代码位置、已有实测值，以及已经试过并撤回的方案。行号以 `perf/startup-latency` 分支合入 2.0.2 后的代码为准，后续改动会漂移，以函数名为主索引。

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
- 例外：批次中有相对鼠标移动时拆成逐个 SendInput，每个移动前查询光标与虚拟屏幕边界（用于回中/解除 ClipCursor）。

### 2.3 托管路径（低占用、或无法走原生时）
`QpcPlaybackDelayStrategy.WaitUntil`（`MacroPlaybackExecutor.cs`）：高精度计时器睡眠到自旋窗口外，然后按剩余时间分层 `Sleep(1)` / `Sleep(0)` / `SpinWait`。`SendInputMacroSink` 用预编码并缓存的 `NativeInput[]` 每批一次 SendInput。

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

预备工作都在进入监听时完成（`executor.Prepare`：自旋校准、`CompiledPlaybackPlan`、`MhpCreatePlan`、输入安全计划），触发时只查缓存。

已有实测（2026-10-14，N=2000，Win32 接收端）：钩子 → 原生首次提交 P50 296µs、P99 616µs、P99.9 2387µs、最大 4982µs；原生启动到首次提交 13–31µs；冷启动首个热键因 JIT 约 44ms（`TriggerPressedAsync`）。钩子返回到目标窗口收到 `WM_KEYDOWN` 另有约 2.9ms（P50），属于系统输入队列与目标程序，不在本程序控制范围内。SendInput 单次调用本身约 0.05–0.65ms。

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

见第 6 节测量。改动按"先测后改、逐项保留或撤回"进行：

1. **预热按实际档位配置原生工作线程**。此前 `NativePlaybackWarmup.Prepare` 固定以极限（mode 2）配置，均衡档首次运行要在首个 SendInput 前撤销 MMCSS 与亲和性。
2. **预热真实原生分支**。此前启动预热用惰性 sink，不经过 `TryRunNativeIteration`/`TryRunPrepared`/`SubmitAndWait`，这些方法在首个真实热键时才 JIT。
3. **缩短托管同步段**：缓存 `MACROHID_LEGACY_PLAYBACK`、去掉每次运行的小分配与 LINQ。
4. **原生每次运行开销**：复用完成事件。
5. **相对鼠标移动与托管 sink**：缓存 QPC 频率、组合键等待使用高精度计时器。

## 6. 测量记录

见文末"本轮测量"小节，由验证阶段填写。
