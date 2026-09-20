# 极限核心掩码说明

普通使用请把「极限核心掩码」**留空**。软件会自动选核。只有在极限精度模式下、并且你已经感到节奏偶尔会「顿一下」时，才需要看本文。

## 这是在干什么

电脑里有多颗 **逻辑处理器**（任务管理器 → 性能 → CPU 里看到的那些小格子）。Windows 随时可以把同一个程序的线程从一颗核挪到另一颗核上。挪核本身要花时间，还可能刚好撞上系统中断，于是按键间隔会多出一小截。

**核心掩码**就是告诉 Windows：「播放宏的这几毫秒，只准在我圈出来的这些核上跑。」这在 Windows 里叫 **processor affinity**（处理器亲和性 / 绑核）。

MacroHID 用的是 Windows 提供的线程亲和性接口，不是自己发明的编号规则。掩码是一串二进制开关：第几位为 1，就允许使用第几号逻辑处理器。

## 怎么看自己电脑有几颗核

1. 打开任务管理器（`Ctrl+Shift+Esc`）。
2. 「性能」→「CPU」。
3. 看 **逻辑处理器** 数量。例如写着 16，则编号一般是 0 到 15。

编号从 **0** 开始，和任务管理器里从左到右、从上到下的逻辑处理器顺序一致（同一处理器组内）。带超线程的机器上，相邻两个编号常常是同一颗物理核上的两条逻辑线程，并不是两颗完全独立的核。

## 每个核心对应的代码

填写时用十六进制，前面加 `0x`。也可以写十进制，软件会存成十六进制。

一位对应一颗逻辑处理器，从右边（最低位）开始数：

| 逻辑处理器编号 | 二进制里哪一位 | 只绑这一颗时填写 | 十进制 |
| ---: | --- | --- | ---: |
| 0 | 第 0 位 | `0x1` | 1 |
| 1 | 第 1 位 | `0x2` | 2 |
| 2 | 第 2 位 | `0x4` | 4 |
| 3 | 第 3 位 | `0x8` | 8 |
| 4 | 第 4 位 | `0x10` | 16 |
| 5 | 第 5 位 | `0x20` | 32 |
| 6 | 第 6 位 | `0x40` | 64 |
| 7 | 第 7 位 | `0x80` | 128 |
| 8 | 第 8 位 | `0x100` | 256 |
| 9 | 第 9 位 | `0x200` | 512 |
| 10 | 第 10 位 | `0x400` | 1024 |
| 11 | 第 11 位 | `0x800` | 2048 |
| 12 | 第 12 位 | `0x1000` | 4096 |
| 13 | 第 13 位 | `0x2000` | 8192 |
| 14 | 第 14 位 | `0x4000` | 16384 |
| 15 | 第 15 位 | `0x8000` | 32768 |
| 16 | 第 16 位 | `0x10000` | 65536 |
| 17 | 第 17 位 | `0x20000` | 131072 |
| 18 | 第 18 位 | `0x40000` | 262144 |
| 19 | 第 19 位 | `0x80000` | 524288 |
| 20 | 第 20 位 | `0x100000` | 1048576 |
| 21 | 第 21 位 | `0x200000` | 2097152 |
| 22 | 第 22 位 | `0x400000` | 4194304 |
| 23 | 第 23 位 | `0x800000` | 8388608 |
| 24 | 第 24 位 | `0x1000000` | 16777216 |
| 25 | 第 25 位 | `0x2000000` | 33554432 |
| 26 | 第 26 位 | `0x4000000` | 67108864 |
| 27 | 第 27 位 | `0x8000000` | 134217728 |
| 28 | 第 28 位 | `0x10000000` | 268435456 |
| 29 | 第 29 位 | `0x20000000` | 536870912 |
| 30 | 第 30 位 | `0x40000000` | 1073741824 |
| 31 | 第 31 位 | `0x80000000` | 2147483648 |

超过 32 号的核，继续按「2 的编号次方」加：32 号是 `0x100000000`，33 号是 `0x200000000`，以此类推。MacroHID 按 64 位掩码解析。

公式（不必手算，用计算器的程序员模式即可）：

> 只允许第 *n* 号逻辑处理器 = `2ⁿ`，写成十六进制就是表里那一格。

## 要绑好几颗核时怎么写

把对应代码 **相加**（按位或，不是随便拼字符串）。

例子：

- 只要 0 号：`0x1`
- 0 号和 1 号：`0x1` + `0x2` → `0x3`
- 0 到 3 号四颗：`0x1+0x2+0x4+0x8` → `0xF`
- 0 到 4 号五颗：再加 `0x10` → `0x1F`（界面帮助里的示例）
- 只要 4 号到 7 号：`0x10+0x20+0x40+0x80` → `0xF0`

Windows 自带计算器 → 模式改为「程序员」→ 选十六进制，把上面几个数加起来即可。

不能填 `0`，也不能填机器上不存在的核。绑得太少（只绑一颗很忙的核）有时比不绑更抖，因为系统中断也常打在低编号核上。

## 使用建议

- 日常宏、对间隔不敏感：掩码留空，精度用「高性能」即可。
- 真的要压 1ms 左右的循环：先选「极限」，掩码仍可留空，让程序自己挑。
- 仍不满意：再按本文填掩码。优先选 **连续几颗、避开 0 号** 的组合试，例如 8 核机器可试 `0xF0`（4–7 号）。
- 填完后只在极限模式生效；播放结束会恢复原来的 CPU 使用范围。

## 参考文献

下列材料解释「逻辑处理器、亲和性掩码、调度抢占」，MacroHID 的填写规则与 Windows API 一致。

1. Microsoft Learn. *SetThreadAffinityMask function (winbase.h)*.  
   <https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-setthreadaffinitymask>  
   官方定义：掩码中置 1 的位表示线程允许运行的逻辑处理器；第 0 位对应处理器 0。

2. Microsoft Learn. *GetProcessAffinityMask function (winbase.h)*.  
   <https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getprocessaffinitymask>  
   进程当前被允许使用的处理器集合，也是同一套位掩码。

3. Microsoft Learn. *Multiple Processors*.  
   <https://learn.microsoft.com/windows/win32/procthread/multiple-processors>  
   说明多处理器上的线程调度，以及亲和性如何限制可运行的处理器。

4. Microsoft Learn. *Processor Groups*.  
   <https://learn.microsoft.com/windows/win32/procthread/processor-groups>  
   逻辑处理器很多时，Windows 会分组；常见消费级机器仍落在第 0 组，掩码按组内编号理解即可。

5. Microsoft Learn. *CPU Sets*.  
   <https://learn.microsoft.com/windows/win32/procthread/cpu-sets>  
   比旧式 affinity 更细的选核机制。MacroHID 极限路径会配合系统返回的 CPU Set，避免把「逻辑编号」和「CPU Set ID」搞混。

6. Microsoft Learn. *Acquiring high-resolution time stamps*（QueryPerformanceCounter）.  
   <https://learn.microsoft.com/windows/win32/sysinfo/acquiring-high-resolution-time-stamps>  
   说明用户态高精度计时。绑核解决的是「线程被换核 / 被抢走」带来的抖动，不能让 Windows 变成硬实时系统。

7. Russinovich, M., Solomon, D., Ionescu, A., & Yosifovich, P. *Windows Internals*. Microsoft Press.  
   进程与线程、优先级、亲和性、DPC/中断与调度抢占的系统级叙述。适合把上面几篇 API 文档串起来读。

8. Anderson, T. E., Bershad, B. N., Lazowska, E. D., & Levy, H. M. (1992). Scheduler activations: Effective kernel support for the user-level management of parallelism. *ACM Transactions on Computer Systems, 10*(1), 53–79.  
   <https://doi.org/10.1145/146941.146944>  
   讨论用户态线程与内核调度之间的关系：应用可以把工作钉在某些处理器上，但仍受内核抢占与中断影响。用来理解「绑核能减少迁移，不能保证零抖动」。

若只想记一句官方原意：亲和性掩码是 **bitset**，**bit *n* = 逻辑处理器 *n***。见文献 [1]。
