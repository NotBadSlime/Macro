# Precision Notes / 精度说明

## 中文

### 目标

MacroHID 的目标是在纯用户态 `SendInput` 方案下尽可能稳定地模拟硬件宏节奏，尤其减少 1ms 附近延迟、循环宏抖动、条件触发到动作提交的波动，以及播放热路径中的运行时分配。

### 精度模式

MacroHID 现在提供三档精度模式。这里的 0.5ms / 0.25ms / 0.1ms 是 LatencyProbe 的目标阈值和优化方向，不是 Windows 用户态硬实时承诺：

- 基础 / `Balanced`：目标 0.5ms。保留高分辨率计时器，但减少自旋时间，优先降低 CPU 占用。
- 高性能 / `ExtremeDuringPlayback`：目标 0.25ms，默认推荐。播放期间启用高优先级、QPC、短窗口自旋、低 GC 干扰和预编译输入批次。
- 极限 / `UltraLowJitter`：目标 0.1ms。播放期间允许更高 CPU 占用，优先使用 `MacroHid.NativePlayback.dll` 的 x64 C++ in-process 播放引擎；native DLL 缺失或初始化失败时自动回退到 managed ultra。native auto 默认使用已预热的 standby engine、预创建 native plan 和 2 worker delayed rescue 来降低单个核心被抢占时的长尾；standby 不可用时才回退 inline native。native 路径会进入 Windows 实时进程级别和最高线程调度优先级，1ms/2ms 区间不使用普通 `Sleep(0/1)`。MacroStudio 启动后先做不扫描 CPU 的快速 standby 预热，保证第一次触发不背负全核心扫描成本；需要压低最大尖峰时，可用 affinity tuner 或播放面板的核心掩码做低抖动核心绑定。

默认精度模式为高性能 / `ExtremeDuringPlayback`：只在宏播放期间进入高性能调度模式，播放结束、停止或异常后恢复系统状态。追求最低最大偏差时，可以在播放控制面板把单个宏切换为“极限”。这个模式会影响系统调度，建议只在真正需要 1ms/2ms 节奏稳定性时使用。

### Windows 调度调研结论

纯用户态低抖动的关键限制不是 `SendInput` 编码，而是 deadline 前后线程是否被调度到 CPU。当前实现按这些系统因素设计：

- QPC 是统一时间基准，避免 `DateTime` / 普通毫秒计时带来的量化误差。
- 高分辨率 waitable timer 用于较长等待，接近 deadline 后切换到 QPC 自旋，减少普通 sleep 尾部抖动。
- `timeBeginPeriod` / `NtSetTimerResolution` 提高系统计时器分辨率，但不能阻止线程被抢占。
- 高优先级、`REALTIME_PRIORITY_CLASS`、`THREAD_PRIORITY_TIME_CRITICAL` 和 MMCSS 只能提高被调度概率，不能把 Windows 变成硬实时系统。
- 动态 priority boost 会让普通动态优先级线程在 I/O、前台输入等场景下发生额外优先级变化。MacroHID 播放期间会保存并禁用进程/线程 priority boost，退出时恢复，减少高性能档和辅助线程的调度状态漂移。
- CPU affinity / ideal processor / CPU Sets 可减少核心迁移和跨核时序波动，但如果绑到被 DPC/ISR 打扰的核心，反而会放大 max outlier，所以极限档只在 CPU scan 完成后使用候选核心。CPU Sets 按 `GetSystemCpuSetInformation` 返回的真实 CPU Set ID 映射后再调用 `SetThreadSelectedCpuSets`，避免把逻辑 CPU 序号误当 CPU Set ID。
- Windows 11 的 power throttling / ignored timer resolution 会影响被最小化或后台时的计时行为，播放上下文会尽量关闭相关节流并在退出时恢复。

播放期间会启用：

- 进程高优先级和播放线程 `TimeCritical`。
- 播放期间禁用进程/线程动态 priority boost，并在退出时恢复原状态。
- 高分辨率计时器。
- `QueryPerformanceCounter` 统一时间基准。
- 分段等待策略：远距离 `Sleep(1)`，中距离 `Sleep(0)`，短距离 QPC 自旋。
- 预编译播放计划和 prepared SendInput batch。
- 随机延迟在每次执行到该步骤时重新采样，但复用输入编码。
- 条件监控专用线程，触发后的 then-actions 预编译提交。
- 播放期间低 GC 干扰策略，结束后恢复。
- Windows 11 下尽量关闭执行速度节流/被忽略的计时器分辨率，条件允许时注册 MMCSS 优先级。
- `UltraLowJitter` native 路径将已编译 `INPUT[]` 和 batch deadline 拷贝进 C++ plan，播放循环中不访问托管对象；auto 模式优先使用预热 standby worker，密集 1ms/2ms batch 使用 2 worker delayed rescue 降低单核心抢占带来的长尾；standby 不可用时才回退 inline native；每次结束后通过 RAII 恢复优先级、affinity、MMCSS、power throttling 和 timer resolution。
- MacroStudio 启动后会后台快速预热 native DLL、空 plan 和 standby worker；CPU scan 不在默认启动路径中运行，避免把数秒级扫描成本混入第一次触发。需要调优核心时使用 `Tune-LatencyAffinity.ps1` 或 `Measure-PrecisionProfiles.ps1 -AutoTuneAffinity`。

### 可以期待什么

- 比普通 `Thread.Sleep(ms)` 更低的调度抖动。
- 1ms/2ms 延迟在许多机器上更接近目标时间。
- 循环宏的节奏更稳定。
- 大量键鼠步骤播放时减少编码和分配开销。
- `LatencyProbe` 可输出 schedule jitter、submit duration、encode cost、condition trigger latency、native selected CPU、CPU migration、`cpuSetApplied`、wait path breakdown 和 outlier 统计。默认 `outliersOverTarget` 跟随档位：基础 500us、高性能 250us、极限 100us；可用 `--target-us` 覆盖。

### 不能承诺什么

Windows 用户态不是硬实时环境。MacroHID 不能保证在所有机器、所有负载、所有电源策略下达到硬件宏或驱动级别确定性，也不能绕过：

- UAC 安全桌面。
- 受保护进程。
- 反作弊或游戏保护环境。
- 系统输入隔离策略。
- 目标应用主动拦截或过滤的输入。

### 建议

- 使用 Release 构建。
- 关闭不必要的后台高负载任务。
- 笔记本连接电源并使用高性能电源计划。
- 对管理员程序，以管理员身份启动 MacroStudio/MacroRunner。
- 用 `LatencyProbe` 在目标机器上实测，而不是只看理论值。

示例：

```powershell
dotnet run --project src\tools\LatencyProbe\LatencyProbe.csproj --configuration Release -- --iterations 5000 --interval-us 1000
dotnet run --project src\tools\LatencyProbe\LatencyProbe.csproj --configuration Release -- --profile extreme --loop-steps 10 --loop-interval-us 1000 --iterations 5000 --cpu-scan
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile extreme --target-us 100 --loop-steps 10 --loop-interval-us 1000 --iterations 5000 --trace-outliers artifacts\native-extreme-outliers.csv
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile extreme --warm-native --cpu-scan --loop-steps 10 --loop-interval-us 1000 --iterations 5000
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile extreme --warm-native --cpu-scan --precreate-native-plan --affinity-mask 0xF0000000 --loop-steps 10 --loop-interval-us 1000 --iterations 5000
.\scripts\Tune-LatencyAffinity.ps1 -Iterations 1000 -WindowSize 4
.\scripts\Measure-PrecisionProfiles.ps1 -Iterations 1000 -AutoTuneAffinity -TunePasses 2
.\scripts\Measure-PrecisionProfiles.ps1 -Iterations 1000 -AffinityMask 0x1F
.\scripts\Measure-PrecisionProfiles.ps1 -Iterations 5000 -AffinityMask 0x1F -FailOnTargetMiss
.\scripts\Trace-LatencyOutliers.ps1 -Iterations 5000 -LoopSteps 10 -LoopIntervalUs 1000
```

`Measure-PrecisionProfiles.ps1` 会连续运行基础、高性能、极限三档，分别按 500us、250us、100us 阈值检查 step jitter 和 loop-end drift，并生成 `artifacts\precision-profiles\precision-*-summary.csv` 与 `precision-*-report.md`。这是判断一台机器当前是否达到三档目标的推荐验收入口。加上 `-AutoTuneAffinity` 后，脚本会先调用 `Tune-LatencyAffinity.ps1` 扫描本机更稳的核心窗口，再把推荐 mask 用于极限档；加上 `-TunePasses 2` 或更高值时，每个候选 mask 会多轮确认，并按多轮最坏值与总 outlier 排名。加上 `-FailOnTargetMiss` 后，任一档超过目标阈值会返回退出码 2，可作为本机验收或 CI gate。

`Tune-LatencyAffinity.ps1` 不需要管理员权限。它会把同一个 native ultra 循环测试跑在多个 CPU affinity mask 上，按 `nativeBatchLate` 的 p99.9、max 和 `outliersOverTarget` 排序，并输出 `recommended affinity mask`。默认候选除了完整窗口，也会加入 edge-drop 变体，例如从 `0xF` 继续测试 `0xE` / `0x7`，用来避开窗口边缘更容易被 DPC/ISR 或调度尖峰打扰的逻辑核心。如果某个 mask 的 max 明显低于默认值，可以把它传给 `LatencyProbe --affinity-mask` 或后续播放配置。

建议把 100 轮以内的调参只当 smoke test；真正选择极限档核心掩码时，至少使用 `-TuneIterations 1000 -TunePasses 2`，再用推荐 mask 跑一次 `LatencyProbe --iterations 5000` 确认最大值。短测可能会漏掉 1 秒级或数十秒级才出现一次的调度尖峰。

MacroStudio 的播放控制面板提供“极限核心掩码”字段。把脚本推荐值（例如 `0x1F`）填进去并选择“极限（0.1 ms）”后，MacroStudio 会在后台按该 mask 重新预热 native standby worker；实际播放期间会临时限制进程 affinity，播放结束后恢复原 affinity。

如果仍然出现 0.5ms 以上的偶发尖峰，请用管理员 PowerShell 运行 `Trace-LatencyOutliers.ps1`。脚本会同时生成 LatencyProbe outlier CSV 和 WPR `.etl` 文件；用 Windows Performance Analyzer 打开 `.etl`，对照 CSV 中的批次时间查看 CPU Usage (Precise)、DPC、ISR 和上下文切换，判断尖峰来自应用线程调度、设备驱动中断还是系统后台活动。

### 调研依据

- Microsoft: [`QueryPerformanceCounter`](https://learn.microsoft.com/en-us/windows/win32/api/profileapi/nf-profileapi-queryperformancecounter) and [high-resolution timestamps](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps).
- Microsoft: [scheduling priorities](https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities), process priority classes, and thread priority levels.
- Microsoft: [`SetThreadPriorityBoost`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadpriorityboost) and [`SetProcessPriorityBoost`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocesspriorityboost).
- Microsoft: [Multimedia Class Scheduler Service](https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service) (MMCSS).
- Microsoft: [`CreateWaitableTimerExW`](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw) and [`SetWaitableTimerEx`](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-setwaitabletimerex).
- Microsoft: [CPU Sets](https://learn.microsoft.com/en-us/windows/win32/procthread/cpu-sets). MacroHID maps actual CPU Set IDs before calling `SetThreadSelectedCpuSets`.
- Microsoft: [`timeBeginPeriod`](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod) timer-resolution behavior.
- Microsoft: [`SetProcessInformation`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation) process power throttling controls.
- Chromium: [`base/time/time_win.cc`](https://chromium.googlesource.com/chromium/src/base/+/master/time/time_win.cc) shows a production browser enabling high-resolution Windows timers only when needed.
- Random ASCII: [high-resolution waitable timer discussion](https://groups.google.com/a/chromium.org/g/scheduler-dev/c/0GlSPYreJeY) and sample code compare waitable timers with timer-resolution changes.

## English

### Goal

MacroHID aims to make pure user-mode `SendInput` playback as stable as possible, especially around 1ms waits, loop jitter, condition-trigger latency, and runtime allocations on the playback hot path.

### Precision Modes

MacroHID now provides three precision modes. The 0.5ms / 0.25ms / 0.1ms numbers are LatencyProbe targets and optimization budgets, not hard real-time guarantees in Windows user mode:

- Basic / `Balanced`: 0.5ms target. Keeps high-resolution timer mode, but uses less spin time to favor lower CPU usage.
- High Performance / `ExtremeDuringPlayback`: 0.25ms target and the recommended default. During playback it enables high priority, QPC scheduling, short-window spinning, low-GC execution, and precompiled input batches.
- Extreme / `UltraLowJitter`: 0.1ms target. During playback it prefers the x64 C++ in-process engine in `MacroHid.NativePlayback.dll`; if the DLL is missing or initialization fails, MacroHID automatically falls back to managed ultra. Native auto uses the warmed standby engine, a pre-created native plan, and two-worker delayed rescue to reduce tails when one core is preempted; it falls back to inline native only when standby is unavailable. The native path enters the Windows real-time process class with the highest thread scheduling priority and avoids normal `Sleep(0/1)` in the 1ms/2ms window. MacroStudio performs a fast no-scan standby warm-up after startup so the first trigger does not pay a full-core scan cost; use the affinity tuner or playback-panel mask when you want low-jitter core binding.

The default precision mode is High Performance / `ExtremeDuringPlayback`: MacroHID enters high-precision mode only while a macro is playing, then restores system state when playback ends, stops, or fails. For the lowest max deviation, switch an individual macro to “Extreme” in the playback panel. This mode affects system scheduling, so use it only when you really need stable 1ms/2ms rhythm.

### Windows Scheduling Research Summary

The limiting factor for pure user-mode low jitter is usually whether the playback thread remains scheduled around the deadline, not input encoding itself. The current design follows these system constraints:

- QPC is the shared time base, avoiding `DateTime` and millisecond timer quantization.
- High-resolution waitable timers cover longer waits; near the deadline, playback switches to QPC spin to reduce ordinary sleep tails.
- `timeBeginPeriod` / `NtSetTimerResolution` improve timer resolution but cannot prevent preemption.
- High priority, `REALTIME_PRIORITY_CLASS`, `THREAD_PRIORITY_TIME_CRITICAL`, and MMCSS increase scheduling preference but do not make Windows hard real time.
- Dynamic priority boosts can add extra priority changes to ordinary dynamic-priority threads after I/O, foreground input, and wakeups. During playback MacroHID saves and disables process/thread priority boosting, then restores the previous state on exit to reduce scheduling-state drift in High Performance and helper-thread paths.
- CPU affinity / ideal processor / CPU Sets can reduce migration noise, but pinning to a core affected by DPC/ISR work can make max outliers worse. Extreme therefore uses candidate-core scan before enabling core selection. CPU Sets are applied only after mapping the actual CPU Set ID returned by `GetSystemCpuSetInformation`, avoiding the mistake of treating a logical CPU number as a CPU Set ID.
- Windows 11 power throttling / ignored timer resolution can affect background/minimized timing; playback context tries to disable relevant throttling while playing and restores it afterward.

During playback it enables:

- High process priority and a `TimeCritical` playback thread.
- Process/thread dynamic priority boosts are disabled during playback and restored afterward.
- High-resolution timer mode.
- `QueryPerformanceCounter` as the shared time base.
- Staged waiting: long distance `Sleep(1)`, medium distance `Sleep(0)`, short distance QPC spin.
- Precompiled playback plans and prepared SendInput batches.
- Random waits are resampled each time the step executes while reusing input encodings.
- Dedicated condition-monitor thread with precompiled then-actions.
- Reduced GC interference during playback, restored afterward.
- On Windows 11, MacroHID tries to disable execution-speed throttling/ignored timer resolution during playback and registers MMCSS priority when available.
- In the `UltraLowJitter` native path, compiled `INPUT[]` packets and batch deadlines are copied into a C++ plan, so the playback loop does not touch managed objects; auto mode prefers warmed standby workers, and dense 1ms/2ms batches use two-worker delayed rescue to reduce single-core preemption tails; inline native remains a fallback when standby is unavailable; RAII restores priority, affinity, MMCSS, power throttling, and timer resolution when playback exits.
- MacroStudio warms the native DLL, empty plan, and standby workers in the background after startup. CPU scan is not part of the default startup path; use `Tune-LatencyAffinity.ps1` or `Measure-PrecisionProfiles.ps1 -AutoTuneAffinity` when you want measured core binding.

### What to Expect

- Lower jitter than plain `Thread.Sleep(ms)`.
- 1ms/2ms waits closer to the target on many machines.
- More stable loop rhythm.
- Lower encoding and allocation overhead for large input sequences.
- `LatencyProbe` can report schedule jitter, submit duration, encode cost, condition trigger latency, native selected CPU, CPU migration, `cpuSetApplied`, wait path breakdown, and outlier counts. By default, `outliersOverTarget` follows the profile: Basic 500us, High Performance 250us, Extreme 100us; use `--target-us` to override.

### What Is Not Guaranteed

Windows user mode is not hard real time. MacroHID cannot guarantee hardware-macro or driver-level determinism on every machine, load level, or power plan. It also cannot bypass:

- UAC secure desktop.
- Protected processes.
- Anti-cheat or protected game environments.
- System input isolation policies.
- Input filtering performed by the target application.

### Recommendations

- Use Release builds.
- Close unnecessary high-load background tasks.
- On laptops, plug in power and use a high-performance power plan.
- For elevated targets, run MacroStudio/MacroRunner as Administrator.
- Measure on the actual target machine with `LatencyProbe`.

Example:

```powershell
dotnet run --project src\tools\LatencyProbe\LatencyProbe.csproj --configuration Release -- --iterations 5000 --interval-us 1000
dotnet run --project src\tools\LatencyProbe\LatencyProbe.csproj --configuration Release -- --profile extreme --loop-steps 10 --loop-interval-us 1000 --iterations 5000 --cpu-scan
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile extreme --target-us 100 --loop-steps 10 --loop-interval-us 1000 --iterations 5000 --trace-outliers artifacts\native-extreme-outliers.csv
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile extreme --warm-native --cpu-scan --loop-steps 10 --loop-interval-us 1000 --iterations 5000
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile extreme --warm-native --cpu-scan --precreate-native-plan --affinity-mask 0xF0000000 --loop-steps 10 --loop-interval-us 1000 --iterations 5000
.\scripts\Tune-LatencyAffinity.ps1 -Iterations 1000 -WindowSize 4
.\scripts\Measure-PrecisionProfiles.ps1 -Iterations 1000 -AutoTuneAffinity -TunePasses 2
.\scripts\Measure-PrecisionProfiles.ps1 -Iterations 1000 -AffinityMask 0x1F
.\scripts\Measure-PrecisionProfiles.ps1 -Iterations 5000 -AffinityMask 0x1F -FailOnTargetMiss
.\scripts\Trace-LatencyOutliers.ps1 -Iterations 5000 -LoopSteps 10 -LoopIntervalUs 1000
```

`Measure-PrecisionProfiles.ps1` runs Basic, High Performance, and Extreme back to back, checks step jitter and loop-end drift against 500us, 250us, and 100us thresholds, and writes `artifacts\precision-profiles\precision-*-summary.csv` plus `precision-*-report.md`. Use it as the recommended acceptance entry for deciding whether a machine currently meets the three precision targets. With `-AutoTuneAffinity`, it first calls `Tune-LatencyAffinity.ps1`, selects the recommended mask, and applies it to the Extreme tier. With `-TunePasses 2` or higher, each candidate mask is confirmed across multiple passes and ranked by worst-case tails plus total outliers. With `-FailOnTargetMiss`, any missed tier returns exit code 2, so the command can be used as a local acceptance or CI gate.

`Tune-LatencyAffinity.ps1` does not require administrator privileges. It runs the same native ultra loop test across multiple CPU affinity masks, ranks them by `nativeBatchLate` p99.9, max, and `outliersOverTarget`, then prints the `recommended affinity mask`. Default candidates include both full windows and edge-drop variants, for example testing `0xE` / `0x7` after `0xF`, so the tuner can avoid a noisy logical core at a window edge. If one mask has a much lower max than the default path, pass it to `LatencyProbe --affinity-mask` or a later playback profile.

Treat runs below 100 iterations as smoke tests only. For a real Extreme mask, use at least `-TuneIterations 1000 -TunePasses 2`, then validate the recommended mask with `LatencyProbe --iterations 5000`. Short tests can miss scheduler spikes that appear only once every second or every few dozen seconds.

MacroStudio exposes an “Ultra affinity mask” field in the playback panel. Paste the recommended value, for example `0x1F`, and select “Extreme (0.1 ms)”; MacroStudio will warm native standby workers for that mask in the background, then temporarily restrict process affinity during playback and restore it afterward.

If >0.5ms spikes still appear, run `Trace-LatencyOutliers.ps1` from an elevated PowerShell session. It generates both a LatencyProbe outlier CSV and a WPR `.etl` trace. Open the `.etl` in Windows Performance Analyzer and compare the CSV outlier times with CPU Usage (Precise), DPC, ISR, and context-switch activity to decide whether the spike comes from application scheduling, driver interrupts, or background system activity.

### Research References

- Microsoft: [`QueryPerformanceCounter`](https://learn.microsoft.com/en-us/windows/win32/api/profileapi/nf-profileapi-queryperformancecounter) and [high-resolution timestamps](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps).
- Microsoft: [scheduling priorities](https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities), process priority classes, and thread priority levels.
- Microsoft: [`SetThreadPriorityBoost`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadpriorityboost) and [`SetProcessPriorityBoost`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocesspriorityboost).
- Microsoft: [Multimedia Class Scheduler Service](https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service) (MMCSS).
- Microsoft: [`CreateWaitableTimerExW`](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw) and [`SetWaitableTimerEx`](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-setwaitabletimerex).
- Microsoft: [CPU Sets](https://learn.microsoft.com/en-us/windows/win32/procthread/cpu-sets). MacroHID maps actual CPU Set IDs before calling `SetThreadSelectedCpuSets`.
- Microsoft: [`timeBeginPeriod`](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod) timer-resolution behavior.
- Microsoft: [`SetProcessInformation`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation) process power throttling controls.
- Chromium: [`base/time/time_win.cc`](https://chromium.googlesource.com/chromium/src/base/+/master/time/time_win.cc) shows a production browser enabling high-resolution Windows timers only when needed.
- Random ASCII: [high-resolution waitable timer discussion](https://groups.google.com/a/chromium.org/g/scheduler-dev/c/0GlSPYreJeY) and sample code compare waitable timers with timer-resolution changes.
