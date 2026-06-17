# Precision Notes / 精度说明

## 中文

### 目标

MacroHID 的目标是在纯用户态 `SendInput` 方案下尽可能稳定地模拟硬件宏节奏，尤其减少 1ms 附近延迟、循环宏抖动、条件触发到动作提交的波动，以及播放热路径中的运行时分配。

### 默认策略

默认精度模式为 `ExtremeDuringPlayback`：只在宏播放期间进入高精度模式，播放结束、停止或异常后恢复系统状态。

播放期间会启用：

- 进程高优先级和播放线程 `TimeCritical`。
- 高分辨率计时器。
- `QueryPerformanceCounter` 统一时间基准。
- 分段等待策略：远距离 `Sleep(1)`，中距离 `Sleep(0)`，短距离 QPC 自旋。
- 预编译播放计划和 prepared SendInput batch。
- 随机延迟在每次执行到该步骤时重新采样，但复用输入编码。
- 条件监控专用线程，触发后的 then-actions 预编译提交。
- 播放期间低 GC 干扰策略，结束后恢复。

### 可以期待什么

- 比普通 `Thread.Sleep(ms)` 更低的调度抖动。
- 1ms/2ms 延迟在许多机器上更接近目标时间。
- 循环宏的节奏更稳定。
- 大量键鼠步骤播放时减少编码和分配开销。
- `LatencyProbe` 可输出 schedule jitter、submit duration、encode cost、condition trigger latency 等统计。

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
```

## English

### Goal

MacroHID aims to make pure user-mode `SendInput` playback as stable as possible, especially around 1ms waits, loop jitter, condition-trigger latency, and runtime allocations on the playback hot path.

### Default Strategy

The default precision mode is `ExtremeDuringPlayback`: MacroHID enters high-precision mode only while a macro is playing, then restores system state when playback ends, stops, or fails.

During playback it enables:

- High process priority and a `TimeCritical` playback thread.
- High-resolution timer mode.
- `QueryPerformanceCounter` as the shared time base.
- Staged waiting: long distance `Sleep(1)`, medium distance `Sleep(0)`, short distance QPC spin.
- Precompiled playback plans and prepared SendInput batches.
- Random waits are resampled each time the step executes while reusing input encodings.
- Dedicated condition-monitor thread with precompiled then-actions.
- Reduced GC interference during playback, restored afterward.

### What to Expect

- Lower jitter than plain `Thread.Sleep(ms)`.
- 1ms/2ms waits closer to the target on many machines.
- More stable loop rhythm.
- Lower encoding and allocation overhead for large input sequences.
- `LatencyProbe` can report schedule jitter, submit duration, encode cost, and condition trigger latency.

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
```
