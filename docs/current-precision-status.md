# MacroHID 当前精度水平

## 结论先说

构建和核心测试都通过；极限档在本机使用 `0xE` affinity 后，这轮 5000 轮长测单步最大偏差 `45us`，整轮最大偏差 `43us`，没有 `>100us` outlier。

当前本机推荐的极限配置仍然是：

```text
affinityMask=0xE
```

## 验证结果

| 项目 | 结果 |
| --- | --- |
| 单元 / 静态 / 回归测试 | `185 test(s) passed` |
| `dotnet build MacroHID.sln --configuration Release` | 成功，`0` 错误，`0` 警告 |
| `Build-LocalRun.ps1 -Configuration Release` | 成功，已更新本机运行目录 |

## 三档精度测试

测试命令使用 `Iterations=1000`、10 步循环、`1ms` 间隔；极限档使用 native standby + affinity `0xE`。

| 档位 | 后端 | 目标 | Step p50 | Step p95 | Step p99 | Step p99.9 | Step max | Loop-end max |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 基础 | managed | `500us` | `3us` | `5us` | `21us` | `87us` | `3299us` | `87us` |
| 高性能 | managed | `250us` | `3us` | `5us` | `7us` | `42us` | `2680us` | `31us` |
| 极限 | native | `100us` | `0us` | `0us` | `0us` | `34us` | `70us` | `36us` |

基础和高性能档的 p99.9 与整轮偏差都不错，但 raw max 仍存在毫秒级 managed 尖峰。这两个档位适合通用桌面自动化和普通宏播放，不适合追求硬件宏级尾部稳定。

极限档才是当前真正压低长尾抖动的方案。它使用 native playback 引擎、standby worker 和 CPU affinity，能显著降低 managed 调度路径上的偶发尖峰。

## 极限档 5000 轮长测

配置：native standby、`affinityMask=0xE`、10 步 * `1ms` * 5000 轮，共 50000 个 batch。

| 指标 | 结果 |
| --- | --- |
| schedule p99.9 | `0us` |
| schedule max | `2us` |
| loop step p99.9 | `3us` |
| loop step max | `45us` |
| loop-end p99.9 | `10us` |
| loop-end max | `43us` |
| `>100us` outlier | `0` |
| native warmup | `388.611ms` |
| native startup | `130us / 144us` |
| engine wake | `544us` |
| CPU Set applied | `2` |
| priority state | realtime / thread priority 生效，worker priority / MMCSS 生效 |

这轮没有生成 outlier CSV，因为没有超过阈值的 outlier。

## 如何理解这些数据

MacroHID 当前的精度水平可以分成两类：

- 基础 / 高性能：平均与 p99 表现很稳，但 managed 后端偶发尖峰仍可能达到毫秒级。适合通用使用。
- 极限：本机 `0xE` affinity 下，5000 轮长测没有 `>100us` outlier，当前最接近硬件宏级尾部稳定。

这些结果是本机环境下的测量值，不是跨机器保证。不同 CPU、系统负载、电源计划、后台服务、DPC/ISR 状态和安全软件都会影响尾部延迟。追求最低长尾时，建议使用极限档并固定已验证的 affinity mask。
