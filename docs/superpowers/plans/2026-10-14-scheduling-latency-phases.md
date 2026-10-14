# 调度延迟优化分阶段计划

基线与依据：`docs/research/macro-scheduling-research-2026-10-14.md`（第 8 节优先级表）、`docs/single-macro-tail-and-end-to-end-2026-10-14.md`（实测）。本计划只覆盖缺口，不重复已实现的预编译、QPC 时间轴和共享原生调度器。

范围：阶段 1、2 立即实施；阶段 3 依据阶段 1 的实测决定；阶段 4 只记录，需单独设计。

## 阶段 1：测量先行（纯增量，默认关闭或仅影响基准输出）

| 任务 | 位置 | 验收 |
|---|---|---|
| 1.1 基准补 P99.9/max/超阈比例 | `tests/MacroHid.Scheduling.Benchmarks/Program.cs` | 输出含 n、P99.9、max、>0.1/0.25/1ms 比例（已完成） |
| 1.2 钩子自耗时：回调入口/出口 QPC 写入预分配环形缓冲，可导出 P99 | `src/ui/MacroStudio/GlobalKeyboardHook.cs` | 回调内无分配；关闭时无可测开销；有单元测试覆盖环形缓冲 |
| 1.3 原生逐批追踪：batchId、nominalDue、effectiveDue、submitBegin、submitEnd、sent；新导出读取 | `SharedPlaybackScheduler.inl`、`NativePlayback.h/.cpp` | 默认关闭；开启后可导出逐批 CSV；`MacroHid.NativePlayback.Tests` 通过；关闭时固定核心基准不退化 |

## 阶段 2：热键入口

| 任务 | 位置 | 验收 |
|---|---|---|
| 2.1 前台进程名快照：`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 维护，热键路径只读 | `Services/ForegroundProcessService.cs`、`MainWindow.xaml.cs` | 热键路径无 `Process.GetProcessById`；前台切换后快照在下次热键前已更新；过滤语义不变 |
| 2.2 评估是否把 TriggerPressed 处理移出钩子线程（吞键判断仍在回调内） | `GlobalKeyboardHook.cs` | 依据 1.2 数据决定；钩子自耗时 P99 ≤ 50µs；EndToEndProbe「钩子→托管工作线程」不变差 |

## 阶段 3：调度器唤醒（有风险，逐项 A/B，任一项不达标即撤回）

| 任务 | 验收 |
|---|---|
| 3.1 `Notify()` 定向唤醒，单宏独占时跳过 generation 递增 | 固定核心五轮对照：P99.9 不退化、最大迟到不增、唤醒数与 CPU 下降；无丢唤醒/重复提交 |
| 3.2 自旋窗口增加衰减 | 同上 |

## 阶段 4：仅记录

跨宏同键仲裁（P1c）、条件检测预算与帧年龄（P1d）、堆队列与 VHF 后端（P2/P3）。会改变语义或引入驱动，需单独设计。

## 边界

端到端毫秒级的主要部分（钩子返回后到目标收到消息，约 2.9ms）在调度器之外，本计划不能消除。驱动级注入与硬件中断模拟不在范围内。

## 实施结果（2026-10-14）

| 任务 | 结果 |
|---|---|
| 1.1 | 完成。`Scheduling.Benchmarks` 输出 n、P99.9、max、>0.1/0.25/1ms 比例；n<1000 时 P99.9 等于 max |
| 1.2 | 完成。`HookSelfTimeRecorder`，`MACROHID_HOOK_SELFTIME=1` 开启，钩子释放时写日志 |
| 1.3 | 原生部分完成。`MACROHID_BATCH_TRACE=1` 开启，`MhpGetBatchTrace` 导出。托管互操作与 CSV 导出未接入 |
| 2.1 | 完成。按 (hwnd, pid) 缓存，每次调用仍重读前台。原路径 P50 26µs / P99 48µs，命中约 0.1µs |
| 2.2 | **不实施。** 同步调用 `TriggerPressedAsync` 在调用线程上 P50 27µs / P99 131µs（假输入接收端；首次冷调用 44ms 为 JIT）。移出需要一次线程交接，实测 P50 25µs / P99 74µs，等于把钩子停留时间换成宏启动延迟，P50 无净收益 |
| 3.1 | **撤回。** 定向唤醒（按对方等待目标判断是否唤醒）通过全部原生回归（连续 8 次），但双宏 1ms 对照中 balanced 模式 P99.9 三轮均更差（485/491/486µs 对基线 409/382/383µs）。extreme 模式噪声过大无结论 |
| 3.2 | **未实施。** 自旋窗口只在单次运行内增长且有 800/1000µs 上限；当前机器底噪（P99.9 在 6–739µs 间波动）无法分辨其收益，不对尾部关键循环做无法验证的改动 |

### 若要继续

- 2.2 的数据只覆盖托管同步段。若要优化，应缩短同步段本身（日志字符串、CTS/Task 分配、`RunAsync` 同步前缀），而不是移走它。
- 冷启动首次热键的 44ms JIT 值得单独查：确认应用启动预热是否覆盖了 `MacroPlaybackController.StartRunCore` 路径。
- 3.1/3.2 需要在安静机器（固定核心、关闭后台负载）上做至少五轮 A/B 才能下结论。
