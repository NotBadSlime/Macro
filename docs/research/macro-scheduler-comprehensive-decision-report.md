# 宏软件调度架构综合优化决策报告

**版本**: 1.0 <br>
**日期**: 2026 年 10 月 13 日 <br>
**状态**: 评审完成，待实施 <br>

---

## 目录

1. [执行摘要](#执行摘要)
2. [技术评审概览](#技术评审概览)
3. [调研发现汇总](#调研发现汇总)
4. [优化建议评估](#优化建议评估)
5. [代码改进优先级](#代码改进优先级)
6. [实施路线图](#实施路线图)
7. [附录](#附录)

---

## 执行摘要

本报告整合了以下内容：

1. **深度调研报告**（8 万 + 字）：涵盖主流宏软件架构、GitHub 开源项目、Windows 输入 API 特性
2. **专家评审报告**：评级 A-，提出多项可行性评估和改进建议
3. **综合优化决策**：明确立即实施、中期深化、前瞻探索三个阶段的任务清单

### 核心结论

- **MacroHID 已达到业界领先水平**，优化空间集中在微优化层面
- **推荐立即落地的 3 项高 ROI 优化**：内存池化、CPU 亲和性自动化、GC 压力优化
- **驱动级方案风险过高**，建议转为长期技术储备
- **纯用户态方案的理论下限**约为 50-100μs（理想环境）

---

## 技术评审概览

### 整体评级：A-

**优点**：
- 技术深度充分，对 Windows 调度器和输入 API 栈理解准确
- 现实边界清晰，避免过度承诺
- 分级优化策略合理，便于渐进式落地
- 数据驱动思维，强调基准测试和性能回归预防

**风险点**：
- 驱动级方案风险被低估（合规成本、HVCI 限制、反作弊检测）
- 硬件宏 Offload 可行性分析不足
- 部分优化建议缺乏代码基础支持

---

## 调研发现汇总

### 主流宏软件对比

| 软件 | 架构特点 | 调度精度 | 备注 |
|------|----------|----------|------|
| **AutoHotKey** | 脚本解释型，事件驱动 | 约 1-5ms | 社区活跃但延迟较高 |
| **Razer Synapse** | 云配置 + 驱动层执行 | <1ms | 依赖特定硬件 |
| **Logitech G Hub** | 本地 + 云混合 | <1ms | 配置系统复杂 |
| **MacroHID** | Native 播放 + Managed 调度 | <1ms | **当前实现已接近理论极限** |

### Windows 输入 API 性能对比

| API | 延迟特征 | 适用场景 | 注意事项 |
|-----|----------|----------|----------|
| **SendInput** | ~1-2ms（标准路径） | 通用宏注入 | 受 UIPI 限制 |
| **SetWindowsHookEx** | ~0.5-1ms（低级别钩子） | 全局监听 | 回调中避免 GC 分配 |
| **Raw Input** | ~0.3-0.8ms | 高精度游戏输入 | 实现复杂度较高 |
| **驱动级虚拟 HID** | <0.1ms | 极致延迟需求 | 合规风险极高 |

### 关键开源项目分析

1. **ZMacro**：高性能 GUI 宏工具，C++ 实现，注重低延迟
2. **InputStitch**：可视化宏编排，支持多层路由
3. **Interception**：驱动级输入拦截，适合高级定制场景

---

## 优化建议评估

### 立即实施的优化（高 ROI）⭐⭐⭐⭐⭐

#### 1. 内存池化
**评分**: 4/5

- **实施难度**：低
- **预期收益**：Gen0 分配减少 50%+，GC 频率降低 40-60%
- **实施要点**：
  ```csharp
  // 使用 ConcurrentQueue 实现线程安全的 INPUT[] 池
  private static readonly ConcurrentQueue<INPUT[]> InputBufferPool = new();
  ```

#### 2. CPU 亲和性自动化
**评分**: 4/5

- **实施难度**：低
- **预期收益**：新用户无需手动运行 PowerShell 脚本
- **实施要点**：
  ```csharp
  // 首次启动时自动扫描最优核心组合
  // 缓存结果到 affinity-cache.json
  ```

#### 3. GC 压力优化
**评分**: 4.5/5

- **实施难度**：中
- **预期收益**：突发抖动减少 15-25%
- **影响范围**：所有使用 Native 回退的场景

---

### 建议暂缓的优化 ⏸️

#### 1. 驱动级原型
**评分**: 2/5

- **技术风险**：高
- **合规风险**：极高（EV 证书 $400+/年 + HVCI 限制 + 反作弊检测）
- **建议**：转为长期技术储备，不作为短期目标

#### 2. 中断协同优化
**评分**: 3.5/5

- **技术难点**：Windows 无官方 API 直接查询 IRQ 分布
- **预期收益**：最大抖动降低 10-20%
- **实施成本**：需通过 ETW 或 WMI 间接获取，复杂度高

#### 3. 条件监控分离
**评分**: 3/5

- **技术难点**：增加状态同步复杂度
- **缺失前提**：当前代码库未见 ConditionMonitor 实现基础
- **建议**：先补齐条件评估框架基础设施

---

## 代码改进优先级

基于评审 Agent 的代码审查，以下是最需要优先改进的位置：

### 🥇 优先级 1: `NativePlaybackEngine.cs` - 异常捕获路径的 GC 压力

**位置**：`src/shared/MacroHid.Runtime/NativePlaybackEngine.cs:329-346`

**问题**：
```csharp
catch (DllNotFoundException ex) {
    fallbackReason = $"native DLL unavailable: {ex.Message}"; // 字符串插值产生临时分配
    diagnostics = EmptyDiagnostics("managed", fallbackReason);
    return false;
}
```

**改进方案**：
```csharp
// 使用预定义常量
private static readonly string NativeDllUnavailableMsg = "native DLL unavailable";
// 或在 Plan 创建阶段就预验证，避免运行时异常
```

---

### 🥈 优先级 2: `MacroScheduler.cs` - 动态 List 增长的内存浪费

**位置**：`src/shared/MacroHid.Core/MacroScheduler.cs:32, 38-63`

**问题**：
```csharp
var scheduled = new List<ScheduledMacroStep>(); // 默认容量 4，复杂宏会多次扩容
```

**改进方案**：
```csharp
var estimatedCapacity = CountScheduledSteps(document.Steps);
var scheduled = new List<ScheduledMacroStep>(estimatedCapacity);
```

---

### 🥉 优先级 3: `GlobalKeyboardHook.cs` - 钩子回调中的 Marshal 开销

**位置**：`src/ui/MacroStudio/GlobalKeyboardHook.cs:300, 330`

**问题**：
```csharp
var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam); // 每次都 Marshal
```

**改进方案**：
```csharp
// 使用预先分配的 struct 复用
private static readonly ThreadLocal<KbdLlHookStruct> KeyboardStructCache =
    new(() => new KbdLlHookStruct());

// 或使用 Unsafe 直接内存拷贝
Unsafe.CopyBlock(ref structField, ref lParam.ToPointer(), sizeof(KbdLlHookStruct));
```

---

## 实施路线图

### 第一阶段（1-2 周）：立即可行

**目标**：快速见效，建立信心，完善测试基础设施

| 任务 | 负责人 | 预计工时 | 验收标准 |
|------|--------|----------|----------|
| 1.1 实现 InputBufferPool 内存池化 | TBD | 8h | GC 频率降低 40%+（基准测试） |
| 1.2 MacroScheduler 容量预分配 | TBD | 4h | List 扩容次数归零（profiling） |
| 1.3 AffinityOptimizer 首次启动扫描 | TBD | 12h | 用户无需手动运行 PS 脚本 |
| 1.4 补充基准测试套件 | TBD | 8h | CI/CD集成，自动回归检测 |
| **合计** | - | **32h** | - |

---

### 第二阶段（1-2 月）：中期深化

**目标**：系统性优化，提升长期可维护性

| 任务 | 负责人 | 预计工时 | 验收标准 |
|------|--------|----------|----------|
| 2.1 零分配播放循环 | TBD | 40h | p99.9 抖动降低 20%+ |
| 2.2 ETW 追踪基础设施 | TBD | 16h | 支持自定义 EventSource |
| 2.3 Power Platform 集成 | TBD | 8h | 播放期间禁用睡眠/待机 |
| 2.4 MMCSS 分类优化 | TBD | 8h | 适配 Game 类别的优先级 |
| 2.5 多线程救援机制升级 | TBD | 24h | 量化 p99.9 延迟改善 |
| **合计** | - | **96h** | - |

---

### 第三阶段（3-6 月）：前瞻探索

**目标**：技术储备，验证创新方向

| 任务 | 负责人 | 预计工时 | 验收标准 |
|------|--------|----------|----------|
| 3.1 硬件宏 Offload 兼容性研究 | TBD | 40h | 验证主流外设的 API 开放程度 |
| 3.2 ML 预测调度原型 | TBD | 80h | 积累足够系统负载数据 |
| 3.3 驱动级方案合规评估 | TBD | 24h | 完成法律风险评估 |
| **合计** | - | **144h** | - |

---

## 附录

### A. 遗漏的技术点清单

1. ✅ **MMCSS 注册细节缺失** - 建议补充 `AvSetMmThreadCharacteristics("Game")` 的使用策略
2. ✅ **timer 分辨率副作用** - 需讨论长期高解析度对功耗的影响
3. ✅ **NUMA 感知实际价值** - 对大多数消费级硬件（单 NUMA 节点）无效
4. ✅ **CPU 缓存预热策略** - 建议增加"warm-up run"机制
5. ✅ **Power Platform API** - 播放期间应调用 `SetThreadExecutionState`
6. ✅ **ETW 追踪集成** - 建议集成 `EventSource` 实现自描述追踪

### B. 参考文献

1. Microsoft Learn. *SendInput function (winuser.h)*. https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput
2. Microsoft Learn. *SetWindowsHookEx function (winuser.h)*. https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowshookex
3. Microsoft Learn. *Multiple Processors*. https://learn.microsoft.com/windows/win32/procthread/multiple-processors
4. Russell, M., et al. *Windows Internernals* (7th ed.). Microsoft Press.
5. Draconis: Network-Accelerated Scheduling for Microsecond Latencies. ACM SIGCOMM 2024.

### C. 相关文件清单

- 调研报告：`docs/research/macro-scheduler-deep-dive-report.md`
- 评审报告：`docs/research/macro-scheduler-review-report.md`
- 优化方案：本文档
- 代码位置：`src/shared/MacroHid.*`, `src/ui/MacroStudio/*`

---

## 变更记录

| 版本 | 日期 | 变更内容 | 作者 |
|------|------|----------|------|
| 1.0 | 2026-10-13 | 初稿，整合调研 + 评审结果 | AutoGenerated |

---

**文档结束**
