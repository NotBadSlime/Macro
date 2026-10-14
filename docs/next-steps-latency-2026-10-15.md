# 低延迟后续工作清单

日期：2026-10-15。承接 [输入 API 对照结果](input-api-comparison-2026-10-15.md)。本文只列待办与验收标准，下次会话按需选做；每一项做之前先走 brainstorming，再写计划。

## 已知事实（不要重复做）

- 调度器本身已到微秒级（极限档 P99.9 约 3µs），端到端中位 3.5ms、最大 8.7ms，主要耗时在调度器之外。见 [单宏尾部优化与真实输入接收实测](single-macro-tail-and-end-to-end-2026-10-14.md)。
- 合成输入下，现有低层钩子的捕获 P50 最低（27.7µs），Raw Input、RegisterHotKey、轮询都更慢；SendInput 各写法无差别（P50 约 756–766µs）；空钩子只增加约 20–95µs。**不要再提议换这几个 API 来降延迟。**
- 阶段 2.2、3.1 已撤回，3.2 因底噪未做，见 [调度延迟优化分阶段计划](superpowers/plans/2026-10-14-scheduling-latency-phases.md)。

## 当前代码状态

- 分支 `input-api-comparison`（最新提交 `ac26e8c`），**尚未合并到 main**。内容：`src/tools/InputApiProbe`、`scripts/summarize_input_api.py`、`tests/test_input_api_summary.py`、设计/计划/结果文档。
- 原始数据在本地 `tmp/input-api-20261015/`（已被 gitignore，不随仓库）。
- 重跑方法：
  ```bash
  dotnet build src/tools/InputApiProbe/InputApiProbe.csproj -c Release
  src/tools/InputApiProbe/bin/Release/net8.0-windows/InputApiProbe.exe capture --rounds 5 --per-round 2000 --output tmp/<目录>
  src/tools/InputApiProbe/bin/Release/net8.0-windows/InputApiProbe.exe inject --rounds 5 --per-block 2000 --output tmp/<目录>   # 开始后点击接收窗口
  src/tools/InputApiProbe/bin/Release/net8.0-windows/InputApiProbe.exe capture-paired --physical 30 --key F8 --output tmp/<目录>  # 物理按键
  python scripts/summarize_input_api.py capture|inject|paired <csv> --output <json>
  ```
  测量期间不要碰键鼠，也不要同时构建或跑其他负载。

## 待办一览（建议顺序）

| 序 | 项 | 目的 | 规模 |
|---|---|---|---|
| 1 | 合并分支 | 把探针和文档放进 main | 小 |
| 2 | 修复并列组物理数据 | 弄清 Raw Input 对物理按键为 0 次、其余只有一半的原因 | 小 |
| 3 | ETW 追踪最慢样本 | 定位毫秒级尾部的真实来源 | 中 |
| 4 | 接收端对照（0.75ms 下限） | 判断下限来自消息路由还是接收方式 | 中 |
| 5 | 驱动级注入原型（新仓库） | 判断虚拟 HID 是否有可测量收益 | 大 |

### 1. 合并分支

- 前置：`python -m unittest discover -s tests -p "test_input_api_summary.py"` 通过；`dotnet build src/tools/InputApiProbe/InputApiProbe.csproj -c Release` 0 个错误。
- 做法：用 `superpowers:finishing-a-development-branch` 走合并或 PR；用户决定方式。
- 验收：main 上能构建探针，文档里的链接不失效。

### 2. 修复并列组物理数据

现象：两次物理 F8 并列组中，低层钩子记到 30 次，RegisterHotKey 和两种轮询只有约一半，Raw Input 为 0；同一进程里 Raw Input 对合成输入正常。原因未知。

假设与做法（逐个排除，每次只改一个变量）：

1. **消息窗口类型。** `RawInputCapture` 现在用 message-only 窗口（`messageOnly: true`）。设计计划原本留有备选：改为隐藏的顶层窗口（`messageOnly: false`）。先试这个，它是最便宜的假设。
2. **按键本身。** 换成 F9 或其他不被厂商软件占用的键，重复 30 次。
3. **后台软件。** 机器上运行着 Armoury Crate（`ArmouryCrate*`）和 Razer Synapse（`RazerAppEngine`），都可能处理 F 键或安装自己的钩子。关闭后重测，对照。
4. **诊断输出。** 让 Raw Input 回调额外记录 `RAWINPUTHEADER.hDevice`、`RAWKEYBOARD.MakeCode/VKey/Flags`，以及收到的 `WM_INPUT` 总数（不过滤键），确认是「根本没收到」还是「收到但被过滤掉」。

验收：30 次物理按键下，所有候选的捕获数一致或差异有明确解释；`summarize_input_api.py paired` 给出每个候选相对低层钩子的先后差分位数。若某候选确实收不到物理按键，把这一点写进语义兼容表。

### 3. ETW 追踪最慢样本

目的：回答「毫秒级尾部来自哪里」。这是 [单宏尾部优化](single-macro-tail-and-end-to-end-2026-10-14.md) 第一项后续，至今未做。

做法：

1. 在 `InputApiProbe inject` 增加可选 `--etw`：为每个样本发出带 `row`、`qpc` 的 EventSource 标记（发送前、接收后），保证默认关闭、关闭时不增加开销。
2. 用 WPR/xperf 录制：线程上下文切换（CSwitch）、ReadyThread、DPC/ISR、页错误、GC/JIT，以及输入栈相关提供者；已有 `scripts/Trace-LatencyOutliers.ps1` 可作起点，先读它。
3. 取最慢的 0.1% 样本，按 `row` 对齐，把发送线程、系统输入线程、接收线程放在同一条时间轴上，逐个归类：被抢占、等待调度、DPC/ISR、GC/JIT、未知。无法解释的保留「未知」，不要一概归咎于某个原因。
4. 记录记录本身的开销：分别跑关闭、仅标记、完整追踪三组，写明追踪是否改变了分布。

验收：产出一张「最慢样本归因表」（各类原因的个数与时间占比）和一份结论文档；不修改产品代码。

### 4. 接收端对照（0.75ms 下限）

事实：无论 `SendInput` 怎么写，到 Normal 优先级的 Win32 `GetMessage` 接收窗口的 P50 都有约 0.75ms。需要区分这是系统输入路由的固有时间，还是接收方式造成的。

在 `ReceiverProcess` 里增加接收模式开关，每次只改一个变量，沿用 `inject` 的五轮方法：

| 模式 | 说明 |
|---|---|
| 基线 | 现状：`GetMessage` 循环，`WM_KEYDOWN`，Normal 优先级 |
| PeekMessage 自旋 | 排除消息循环唤醒延迟 |
| Raw Input 接收 | 接收端注册 Raw Input，记录 `WM_INPUT` 到达时间，比较与 `WM_KEYDOWN` 的先后 |
| 轮询 `GetAsyncKeyState` | 贴近许多游戏的读取方式，用来看「游戏如果轮询，实际能多快看到」 |
| 接收线程提高优先级 / MMCSS | 仅用于判断下限是否随调度而变，不代表真实目标 |

验收：给出每种接收模式的 P50/P99，并判断「下限」是否主要由接收方式决定。结论要写明：不同目标程序读取输入的方式不同，本机结果不能直接外推到某个具体游戏。

### 5. 驱动级注入原型（新仓库）

决定（见项目记忆）：驱动工作放进**新的独立仓库**，不放进本仓库。

- **仓库命名待用户确认。** 用户提议叫 MacroHID，但当前产品、解决方案和命名空间都已叫 MacroHID，容易混淆；建议先确认是否改用区分名（如 `MacroHID-Driver`）。
- 前置判断：第 4 项的结果。若下限主要在接收方式一侧，驱动注入的预期收益就小，应先停下评估是否值得做。
- 原型范围（若继续）：用 VHF 建一个虚拟键盘，能按指定时刻发送 F24 的按下/抬起报告；仅此，不做宏逻辑。
- 对照方法：复用本仓库的 `InputApiProbe inject` 接收端与统计脚本，把「SendInput」与「虚拟 HID」放在同一接收进程、同样的五轮 × 2,000 样本、同样的判定门槛（P50 改善不少于 100µs，P99 与最大值不变差，至少四轮方向一致）下比较。
- 风险需提前写明：驱动签名与测试签名模式、对反作弊软件的影响、安装与卸载、稳定性与蓝屏风险、在用户机器上部署的合规问题。这些都不应被「延迟更低」的结果掩盖。

验收：有数据证明或否定虚拟 HID 相对 SendInput 的收益；若收益低于门槛，记录后终止，不继续投入。

## 不做的事

- 不再比较 Raw Input / RegisterHotKey / 轮询作为触发方式（已有结论）。
- 不做绕过公开接口的直接系统调用，不做 `PostMessage` 类后台输入（语义不同）。
- 不为追求数字而降低判定门槛，不丢弃难看的样本。
- 不把合成输入的数据说成游戏内延迟；游戏帧时间与真实游戏消费需要另行验收（PresentMon）。

## 相关文档

- [输入 API 对照结果](input-api-comparison-2026-10-15.md)、[设计](superpowers/specs/2026-10-15-input-api-comparison-design.md)、[实现计划](superpowers/plans/2026-10-15-input-api-comparison.md)
- [当前精度状态](current-precision-status.md)、[调度研究](research/macro-scheduling-research-2026-10-14.md)
