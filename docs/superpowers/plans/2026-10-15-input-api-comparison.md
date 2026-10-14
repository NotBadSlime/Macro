# 输入 API 对照实验 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增独立探针 `InputApiProbe`，在同一台机器上对比触发捕获（低层钩子、Raw Input、RegisterHotKey、轮询）与注入（SendInput 虚拟键码/扫描码/合并、keybd_event）各 API 的延迟，并给出是否值得接入的判定。

**Architecture:** 一个 .NET 8 Windows 控制台工具，含四种子命令：`capture`（单独组）、`capture-paired`（物理按键并列组）、`inject`（注入到独立 Win32 接收进程）、`--receiver`（接收子进程）。探针只写原始 QPC CSV 与 provenance；统计和判定由新的 Python 脚本 `scripts/summarize_input_api.py` 完成，并有 unittest 覆盖。产品代码不改，A0 基线通过链接生产 `GlobalKeyboardHook.cs` 源码复用（与 `EndToEndProbe` 做法相同）。

**Tech Stack:** C# / .NET 8 (net8.0-windows, P/Invoke user32/winmm)，Python 3 stdlib（csv/json/math/unittest）。

设计依据：`docs/superpowers/specs/2026-10-15-input-api-comparison-design.md`。

---

## 文件结构

| 文件 | 职责 |
|---|---|
| `src/tools/InputApiProbe/InputApiProbe.csproj` | 工程；链接 `GlobalKeyboardHook.cs`，引用 `MacroHid.Runtime` |
| `src/tools/InputApiProbe/Native.cs` | 全部 P/Invoke、键盘注入（各变体）、消息循环原语 |
| `src/tools/InputApiProbe/MessageWindow.cs` | 带独立消息线程的窗口（隐藏/可见），处理函数首行取时间戳 |
| `src/tools/InputApiProbe/EmptyHook.cs` | 空 `WH_KEYBOARD_LL` 钩子（实验 B 的「钩子税」因子） |
| `src/tools/InputApiProbe/Capture/ICaptureStrategy.cs` | 捕获方式的统一接口 |
| `src/tools/InputApiProbe/Capture/LowLevelHookCapture.cs` | A0：生产低层钩子 |
| `src/tools/InputApiProbe/Capture/RawInputCapture.cs` | A1：Raw Input |
| `src/tools/InputApiProbe/Capture/RegisterHotKeyCapture.cs` | A2：RegisterHotKey |
| `src/tools/InputApiProbe/Capture/PollingCapture.cs` | A3：GetAsyncKeyState 轮询 |
| `src/tools/InputApiProbe/Capture/CaptureExperiment.cs` | 实验 A 单独组 |
| `src/tools/InputApiProbe/Capture/CapturePairedExperiment.cs` | 实验 A 并列组（物理按键） |
| `src/tools/InputApiProbe/Inject/ReceiverProcess.cs` | 实验 B 的 Win32 接收进程 |
| `src/tools/InputApiProbe/Inject/InjectExperiment.cs` | 实验 B 发送端 |
| `src/tools/InputApiProbe/Provenance.cs` | 写环境与二进制指纹 |
| `src/tools/InputApiProbe/Program.cs` | 子命令分发 |
| `scripts/summarize_input_api.py` | 统计、判定、并列差值 |
| `tests/test_input_api_summary.py` | 汇总脚本的 unittest |
| `docs/input-api-comparison-2026-10-15.md` | 结果文档（Task 8 产出） |

约定：触发键使用 **F24（VK 0x87）**，避免对前台程序产生副作用；物理并列组使用 F8。所有时间戳是 `Stopwatch.GetTimestamp()`（QPC）。构建命令 `dotnet build src/tools/InputApiProbe/InputApiProbe.csproj -c Release`；提交信息末尾都带 `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`。

---

### Task 1: 修订设计文档并建立工程骨架

**Files:**
- Modify: `docs/superpowers/specs/2026-10-15-input-api-comparison-design.md`
- Create: `src/tools/InputApiProbe/InputApiProbe.csproj`
- Create: `src/tools/InputApiProbe/Native.cs`
- Create: `src/tools/InputApiProbe/MessageWindow.cs`
- Create: `src/tools/InputApiProbe/Program.cs`

- [ ] **Step 1: 修订设计文档的三处细节**

在设计文档中做三处精确替换。

替换一（A3 行）：

旧：`| A3 | 专用线程轮询 `GetAsyncKeyState`，分别用 250µs 与 1ms 周期 | 记录 CPU 占用；可能漏掉很短的按键 |`

新：`| A3 | 专用线程轮询 `GetAsyncKeyState`，两个变体：持续自旋（poll-spin，占满一个核）与 1ms 睡眠轮询（poll-1ms，配合 `timeBeginPeriod(1)`） | 记录 CPU 占用；按下时间短于轮询周期会漏掉 |`

替换二（在「方法」小节的列表第一条之前加一行）：

新增一行：`- 触发键使用 F24（VK 0x87）而非 F8，避免对前台程序产生副作用；物理并列组按 `--key` 指定的键（默认 F8）。触发为「按下 → 等待捕获 → 抬起」，不合并，因为轮询方式会漏掉瞬间合并的按下抬起。`

替换三（判定门槛第 3 条之后新增第 4 条）：

新增：`4. 基线自身各轮 P50 的极差不小于 100µs 时，该组判为「无结论」（底噪大于要检测的改善量）；候选出现任何丢失或重复事件，判为「无效」，不进入名单。`

- [ ] **Step 2: 创建工程文件**

`src/tools/InputApiProbe/InputApiProbe.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DefineConstants>$(DefineConstants);MACROHID_HOTKEY_PROBE</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../shared/MacroHid.Runtime/MacroHid.Runtime.csproj" />
    <Compile Include="../../ui/MacroStudio/GlobalKeyboardHook.cs" Link="GlobalKeyboardHook.cs" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: 创建 `Native.cs`**

```csharp
using System.Runtime.InteropServices;

namespace InputApiProbe;

internal enum InjectVariant { Vk, Scan, VkScan, KeybdEvent, MergedPair }

/// <summary>All P/Invoke used by the probe, plus the keyboard injection variants under test.</summary>
internal static class Native
{
    public const ushort VkF24 = 0x87;
    public const ulong Tag = 0x4D484150; // "MHAP", dwExtraInfo on injected input

    private const uint InputKeyboard = 1, KeyUpFlag = 0x2, ScanCodeFlag = 0x8;

    /// <summary>Scan code of F24, resolved once so MapVirtualKey is never inside a timed region. 0 if unmapped.</summary>
    public static readonly ushort ScanF24 = (ushort)MapVirtualKey(VkF24, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Union; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Id;
        public UIntPtr W;
        public IntPtr L;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMessage(out Msg message, IntPtr hwnd, uint minimum, uint maximum);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DispatchMessage(ref Msg message);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, UIntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread, uint message, UIntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint milliseconds);

    private static Input Key(ushort vk, ushort scan, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new() { Keyboard = new() { Vk = vk, Scan = scan, Flags = flags, Extra = new UIntPtr(Tag) } }
    };

    private static void Send(params Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException($"SendInput failed, error={Marshal.GetLastWin32Error()}");
    }

    /// <summary>Key-down of F24 using the given API variant. MergedPair sends down and up in one SendInput call.</summary>
    public static void KeyDown(InjectVariant variant)
    {
        switch (variant)
        {
            case InjectVariant.Vk: Send(Key(VkF24, 0, 0)); break;
            case InjectVariant.Scan: Send(Key(0, ScanF24, ScanCodeFlag)); break;
            case InjectVariant.VkScan: Send(Key(VkF24, ScanF24, 0)); break;
            case InjectVariant.KeybdEvent: keybd_event((byte)VkF24, (byte)ScanF24, 0, new UIntPtr(Tag)); break;
            case InjectVariant.MergedPair: Send(Key(VkF24, 0, 0), Key(VkF24, 0, KeyUpFlag)); break;
        }
    }

    /// <summary>Key-up of F24. A no-op for MergedPair because KeyDown already sent the up event.</summary>
    public static void KeyUp(InjectVariant variant)
    {
        switch (variant)
        {
            case InjectVariant.Vk: Send(Key(VkF24, 0, KeyUpFlag)); break;
            case InjectVariant.Scan: Send(Key(0, ScanF24, ScanCodeFlag | KeyUpFlag)); break;
            case InjectVariant.VkScan: Send(Key(VkF24, ScanF24, KeyUpFlag)); break;
            case InjectVariant.KeybdEvent: keybd_event((byte)VkF24, (byte)ScanF24, KeyUpFlag, new UIntPtr(Tag)); break;
            case InjectVariant.MergedPair: break;
        }
    }
}
```

- [ ] **Step 4: 创建 `MessageWindow.cs`**

```csharp
using System.Runtime.InteropServices;

namespace InputApiProbe;

/// <summary>
/// A window with its own message-loop thread. The handler runs on that thread for every message and
/// returns true to swallow it (result 0); returning false falls through to DefWindowProc.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private const uint WmDestroy = 0x2, WmClose = 0x10;
    private static readonly IntPtr HwndMessage = new(-3);

    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public IntPtr Proc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? Menu, Name;
        public IntPtr SmallIcon;
    }

    private readonly Func<uint, IntPtr, IntPtr, bool> handler;
    private readonly Action<IntPtr>? onCreated;
    private readonly bool visible, messageOnly;
    private readonly string title, className = "MacroHID.ApiProbe." + Guid.NewGuid().ToString("N");
    private readonly WindowProc proc;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private Exception? failure;

    public IntPtr Handle { get; private set; }

    /// <param name="onCreated">Runs on the window thread right after creation (RegisterHotKey and raw-input registration must happen there).</param>
    /// <param name="messageOnly">For hidden windows: parent them to HWND_MESSAGE. Set false to get a hidden top-level window.</param>
    public MessageWindow(Func<uint, IntPtr, IntPtr, bool> handler, bool visible, string title,
        Action<IntPtr>? onCreated = null, bool messageOnly = true)
    {
        this.handler = handler; this.visible = visible; this.title = title;
        this.onCreated = onCreated; this.messageOnly = messageOnly;
        proc = Proc;
        thread = new Thread(Run) { IsBackground = true, Name = "ApiProbe-" + title, Priority = ThreadPriority.AboveNormal };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (failure is not null) throw new InvalidOperationException($"{title}: {failure.Message}", failure);
    }

    private IntPtr Proc(IntPtr hwnd, uint message, IntPtr w, IntPtr l)
    {
        if (handler(message, w, l)) return IntPtr.Zero;
        if (message == WmDestroy) PostQuitMessage(0);
        return DefWindowProc(hwnd, message, w, l);
    }

    private void Run()
    {
        IntPtr instance = GetModuleHandle(null);
        var cls = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = Marshal.GetFunctionPointerForDelegate(proc), Instance = instance,
            Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)), Background = new IntPtr(6), Name = className
        };
        try
        {
            if (RegisterClassEx(ref cls) == 0) throw new InvalidOperationException($"RegisterClassEx failed, error={Marshal.GetLastWin32Error()}");
            // 0x10CF0000 = WS_VISIBLE | WS_OVERLAPPEDWINDOW, ex style 8 = WS_EX_TOPMOST (same as the existing receiver).
            Handle = visible
                ? CreateWindowEx(8, className, title, 0x10CF0000, 240, 240, 600, 200, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero)
                : CreateWindowEx(0, className, title, 0, 0, 0, 0, 0, messageOnly ? HwndMessage : IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed, error={Marshal.GetLastWin32Error()}");
            onCreated?.Invoke(Handle);
        }
        catch (Exception ex)
        {
            failure = ex;
            if (Handle != IntPtr.Zero) DestroyWindow(Handle);
            UnregisterClass(className, instance);
            ready.Set();
            return;
        }
        ready.Set();
        while (Native.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); }
        UnregisterClass(className, instance);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) Native.PostMessage(Handle, WmClose, UIntPtr.Zero, IntPtr.Zero);
        thread.Join(2000);
        GC.KeepAlive(proc);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
```

- [ ] **Step 5: 创建最小 `Program.cs`**

```csharp
namespace InputApiProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.Error.WriteLine("Usage: InputApiProbe capture|capture-paired|inject|--receiver <name> [options]");
            return 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
```

- [ ] **Step 6: 构建**

Run: `dotnet build src/tools/InputApiProbe/InputApiProbe.csproj -c Release`
Expected: `0 Error(s)`（依赖兼容性警告可有，与 `EndToEndProbe` 相同类别）。若 `GlobalKeyboardHook.cs` 报找不到类型，说明链接源码需要的类型不在 `MacroHid.Runtime`；对照 `src/tools/EndToEndProbe/EndToEndProbe.csproj` 补齐同样的引用。

- [ ] **Step 7: 提交**

```bash
git add docs/superpowers/specs/2026-10-15-input-api-comparison-design.md src/tools/InputApiProbe
git commit -m "Scaffold InputApiProbe and refine experiment design" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: 汇总脚本（TDD）

**Files:**
- Create: `tests/test_input_api_summary.py`
- Create: `scripts/summarize_input_api.py`

- [ ] **Step 1: 先写失败的测试**

`tests/test_input_api_summary.py`：

```python
import csv
import importlib.util
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("summary", Path(__file__).parents[1] / "scripts/summarize_input_api.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def group(round_p50, p50, p99=500.0, mx=900.0, lost=0, duplicates=0):
    """Hand-built group: per-round P50s plus pooled stats (round P99/max follow the pooled values)."""
    return {
        "pooled": {"n": 1000, "p50": p50, "p99": p99, "max": mx},
        "rounds": [{"round": i + 1, "p50": v, "p99": p99, "max": mx} for i, v in enumerate(round_p50)],
        "lost": lost, "duplicates": duplicates,
    }


def write_csv(path, header, rows):
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(header)
        writer.writerows(rows)


class PercentileTests(unittest.TestCase):
    def test_nearest_rank(self):
        self.assertEqual(module.stats([4, 1, 3, 2])["p50"], 2)
        self.assertEqual(module.stats(list(range(1, 1001)))["p99"], 990)
        self.assertEqual(module.stats(list(range(1, 1001)))["max"], 1000)

    def test_empty_is_none(self):
        self.assertIsNone(module.stats([]))


class VerdictTests(unittest.TestCase):
    base = group([1000] * 5, 1000)

    def test_clear_win_is_candidate(self):
        self.assertEqual(module.verdict(self.base, group([800] * 5, 800)), "candidate")

    def test_small_gain_is_no_net_benefit(self):
        self.assertEqual(module.verdict(self.base, group([950] * 5, 950)), "no-net-benefit")

    def test_tail_regression_blocks_candidate(self):
        self.assertEqual(module.verdict(self.base, group([800] * 5, 800, p99=600.0)), "no-net-benefit")
        self.assertEqual(module.verdict(self.base, group([800] * 5, 800, mx=1200.0)), "no-net-benefit")

    def test_needs_four_improved_rounds(self):
        self.assertEqual(module.verdict(self.base, group([800, 800, 800, 1100, 1100], 800)), "no-net-benefit")
        self.assertEqual(module.verdict(self.base, group([800, 800, 800, 800, 1100], 800)), "candidate")

    def test_lost_or_duplicate_is_invalid(self):
        self.assertEqual(module.verdict(self.base, group([800] * 5, 800, lost=1)), "invalid")
        self.assertEqual(module.verdict(self.base, group([800] * 5, 800, duplicates=1)), "invalid")

    def test_too_few_rounds_is_inconclusive(self):
        self.assertEqual(module.verdict(group([1000] * 4, 1000), group([800] * 4, 800)), "inconclusive")

    def test_noisy_baseline_is_inconclusive(self):
        noisy = group([900, 1000, 1050, 1000, 1000], 1000)
        self.assertEqual(module.verdict(noisy, group([700] * 5, 700)), "inconclusive")


class CaptureSummaryTests(unittest.TestCase):
    def test_lost_duplicate_and_latency(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "capture-samples.csv"
            header = ["round", "strategy", "sample", "send_qpc", "capture_qpc", "captures", "frequency"]
            # frequency 1_000_000 -> 1 tick = 1 microsecond
            write_csv(path, header, [
                [1, "ll-hook", 0, 1000, 1100, 1, 1000000],
                [1, "ll-hook", 1, 2000, 2300, 1, 1000000],
                [1, "ll-hook", 2, 3000, 0, 0, 1000000],
                [2, "ll-hook", 0, 4000, 4200, 2, 1000000],
                [1, "raw-input", 0, 1000, 1050, 1, 1000000],
            ])
            result = module.summarize_capture(path)
            ll = result["groups"]["ll-hook"]
            self.assertEqual(ll["lost"], 1)
            self.assertEqual(ll["duplicates"], 1)
            self.assertEqual(ll["pooled"]["n"], 3)
            self.assertEqual(ll["pooled"]["p50"], 200)
            self.assertEqual([r["round"] for r in ll["rounds"]], [1, 2])
            self.assertEqual(result["groups"]["raw-input"]["pooled"]["p50"], 50)


class InjectSummaryTests(unittest.TestCase):
    def test_wrong_key_counts_as_lost_and_up_latency_is_reported(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "inject-samples.csv"
            header = ["round", "variant", "hook", "sample", "send_down_qpc", "send_up_qpc",
                      "recv_down_qpc", "recv_up_qpc", "down_count", "up_count", "vk", "frequency"]
            write_csv(path, header, [
                [1, "vk", "none", 0, 1000, 5000, 1400, 5300, 1, 1, 135, 1000000],
                [1, "vk", "none", 1, 6000, 9000, 6500, 9200, 1, 1, 135, 1000000],
                [1, "scan", "none", 0, 1000, 5000, 1200, 5100, 1, 1, 17, 1000000],  # wrong key
            ])
            result = module.summarize_inject(path)
            vk = result["groups"]["vk|none"]
            self.assertEqual(vk["pooled"]["p50"], 400)
            self.assertEqual(result["up_groups"]["vk|none"]["pooled"]["p50"], 200)
            self.assertEqual(result["groups"]["scan|none"]["lost"], 1)


class PairedTests(unittest.TestCase):
    def test_delta_against_baseline_by_nth_capture(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "paired.csv"
            write_csv(path, ["sample", "strategy", "capture_qpc", "frequency"], [
                [0, "ll-hook", 1000, 1000000], [1, "ll-hook", 2000, 1000000],
                [0, "raw-input", 1100, 1000000], [1, "raw-input", 1900, 1000000],
                [0, "poll-1ms", 1500, 1000000],
            ])
            result = module.summarize_paired(path, "ll-hook")
            self.assertEqual(result["raw-input"]["n"], 2)
            self.assertEqual(result["raw-input"]["p50"], -100)
            self.assertEqual(result["raw-input"]["max"], 100)
            self.assertEqual(result["poll-1ms"]["count_mismatch"], True)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: 运行测试确认失败**

Run: `python -m unittest discover -s tests -p "test_input_api_summary.py" -v`
Expected: FAIL，原因是 `scripts/summarize_input_api.py` 不存在（`FileNotFoundError`）。

- [ ] **Step 3: 实现汇总脚本**

`scripts/summarize_input_api.py`：

```python
"""Summarize InputApiProbe CSVs and judge each candidate against its baseline (stdlib only)."""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path

THRESHOLD_US = 100.0
MIN_ROUNDS = 5
MIN_IMPROVED_ROUNDS = 4
F24 = 0x87


def stats(values):
    """Nearest-rank percentiles; None when there are no samples."""
    values = sorted(values)
    if not values:
        return None

    def rank(p):
        return values[math.ceil(p * len(values)) - 1]

    return {"n": len(values), "p50": rank(.5), "p95": rank(.95), "p99": rank(.99),
            "p999": rank(.999), "max": values[-1]}


def build_group(records):
    """records: (round, latency_us, count). count 0 = lost, >1 = duplicate; latency counts when count >= 1."""
    lost = duplicates = 0
    pooled, by_round = [], {}
    for round_number, latency, count in records:
        if count == 0:
            lost += 1
            continue
        if count > 1:
            duplicates += 1
        pooled.append(latency)
        by_round.setdefault(round_number, []).append(latency)
    return {"pooled": stats(pooled),
            "rounds": [{"round": r, **stats(v)} for r, v in sorted(by_round.items())],
            "lost": lost, "duplicates": duplicates}


def verdict(base, cand, threshold_us=THRESHOLD_US):
    """candidate | no-net-benefit | inconclusive | invalid, per the design's decision gate."""
    if cand["lost"] or cand["duplicates"] or cand["pooled"] is None:
        return "invalid"
    if base["pooled"] is None or len(base["rounds"]) < MIN_ROUNDS or len(cand["rounds"]) < MIN_ROUNDS:
        return "inconclusive"
    base_p50 = [r["p50"] for r in base["rounds"]]
    if max(base_p50) - min(base_p50) >= threshold_us:
        return "inconclusive"
    cand_by_round = {r["round"]: r for r in cand["rounds"]}
    improved = sum(1 for r in base["rounds"]
                   if r["round"] in cand_by_round and cand_by_round[r["round"]]["p50"] < r["p50"])
    better = (base["pooled"]["p50"] - cand["pooled"]["p50"] >= threshold_us
              and cand["pooled"]["p99"] <= base["pooled"]["p99"]
              and cand["pooled"]["max"] <= base["pooled"]["max"]
              and improved >= MIN_IMPROVED_ROUNDS)
    return "candidate" if better else "no-net-benefit"


def read_rows(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    if not rows:
        raise ValueError(f"Empty CSV: {path}")
    return rows


def fingerprint(path):
    return {"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def summarize_capture(path):
    records = {}
    for row in read_rows(path):
        count = int(row["captures"])
        capture = int(row["capture_qpc"])
        if count and capture <= 0:
            count = 0
        latency = (capture - int(row["send_qpc"])) * 1e6 / int(row["frequency"]) if count else 0.0
        records.setdefault(row["strategy"], []).append((int(row["round"]), latency, count))
    return {**fingerprint(path), "groups": {k: build_group(v) for k, v in records.items()}}


def summarize_inject(path):
    down, up = {}, {}
    for row in read_rows(path):
        key = f"{row['variant']}|{row['hook']}"
        freq = int(row["frequency"])
        round_number = int(row["round"])
        count = int(row["down_count"]) if int(row["vk"]) == F24 else 0
        down.setdefault(key, []).append(
            (round_number, (int(row["recv_down_qpc"]) - int(row["send_down_qpc"])) * 1e6 / freq if count else 0.0, count))
        up_count = int(row["up_count"]) if count else 0
        up.setdefault(key, []).append(
            (round_number, (int(row["recv_up_qpc"]) - int(row["send_up_qpc"])) * 1e6 / freq if up_count else 0.0, up_count))
    return {**fingerprint(path),
            "groups": {k: build_group(v) for k, v in down.items()},
            "up_groups": {k: build_group(v) for k, v in up.items()}}


def summarize_paired(path, baseline):
    """Per-strategy delta (strategy - baseline, microseconds) for the nth capture of each strategy."""
    captures = {}
    freq = 1
    for row in read_rows(path):
        freq = int(row["frequency"])
        captures.setdefault(row["strategy"], {})[int(row["sample"])] = int(row["capture_qpc"])
    base = captures[baseline]
    result = {}
    for strategy, values in captures.items():
        if strategy == baseline:
            continue
        deltas = [(values[i] - base[i]) * 1e6 / freq for i in sorted(values) if i in base]
        result[strategy] = {**(stats(deltas) or {"n": 0}), "count_mismatch": len(values) != len(base)}
    return result


def judge(groups, baseline):
    return {key: verdict(groups[baseline], value) for key, value in groups.items() if key != baseline}


def print_table(title, groups, verdicts, baseline):
    print(title)
    print(f"{'candidate':28} {'n':>6} {'lost':>5} {'dup':>4} {'p50':>9} {'p95':>9} {'p99':>9} {'p99.9':>9} {'max':>9}  verdict")
    for key, value in groups.items():
        s = value["pooled"] or {}
        label = "baseline" if key == baseline else verdicts.get(key, "")
        print(f"{key:28} {s.get('n', 0):>6} {value['lost']:>5} {value['duplicates']:>4} "
              f"{s.get('p50', float('nan')):>9.1f} {s.get('p95', float('nan')):>9.1f} {s.get('p99', float('nan')):>9.1f} "
              f"{s.get('p999', float('nan')):>9.1f} {s.get('max', float('nan')):>9.1f}  {label}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("capture", "inject", "paired"):
        p = sub.add_parser(name)
        p.add_argument("csv", type=Path)
        p.add_argument("--baseline", default={"capture": "ll-hook", "inject": "vk|none", "paired": "ll-hook"}[name])
        p.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.command == "paired":
        result = {**fingerprint(args.csv), "baseline": args.baseline, "deltas_us": summarize_paired(args.csv, args.baseline)}
        for key, value in result["deltas_us"].items():
            print(key, value)
    else:
        result = summarize_capture(args.csv) if args.command == "capture" else summarize_inject(args.csv)
        result["baseline"] = args.baseline
        result["verdicts"] = judge(result["groups"], args.baseline)
        print_table(f"{args.command}: time from send to {'capture' if args.command == 'capture' else 'receiver WM_KEYDOWN'} (us)",
                    result["groups"], result["verdicts"], args.baseline)
        if args.command == "inject":
            result["up_verdicts"] = judge(result["up_groups"], args.baseline)
            print_table("inject: key-up send to receiver WM_KEYUP (us)", result["up_groups"], result["up_verdicts"], args.baseline)
    if args.output:
        args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: 运行测试确认通过**

Run: `python -m unittest discover -s tests -p "test_input_api_summary.py" -v`
Expected: 全部用例 `ok`，末尾 `OK`。若 `test_needs_four_improved_rounds` 的第一个断言失败，检查 `verdict` 里 `better` 是否同时要求 `improved >= MIN_IMPROVED_ROUNDS`（该用例候选逐轮 P50 只有三轮低于基线）。

- [ ] **Step 5: 提交**

```bash
git add scripts/summarize_input_api.py tests/test_input_api_summary.py
git commit -m "Add input API summary script with verdict gate and tests" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: 四种捕获方式

**Files:**
- Create: `src/tools/InputApiProbe/Capture/ICaptureStrategy.cs`
- Create: `src/tools/InputApiProbe/Capture/LowLevelHookCapture.cs`
- Create: `src/tools/InputApiProbe/Capture/RawInputCapture.cs`
- Create: `src/tools/InputApiProbe/Capture/RegisterHotKeyCapture.cs`
- Create: `src/tools/InputApiProbe/Capture/PollingCapture.cs`

- [ ] **Step 1: 接口**

`Capture/ICaptureStrategy.cs`：

```csharp
namespace InputApiProbe;

internal interface ICaptureStrategy : IDisposable
{
    string Id { get; }

    /// <summary>
    /// Raised on the strategy's own thread with the QPC taken at the first instruction of its callback
    /// (for polling: right after the call that observed the key down) when the key goes down.
    /// </summary>
    event Action<long>? Captured;

    void Start();
    void Stop();
}
```

- [ ] **Step 2: A0 低层钩子（生产类）**

`Capture/LowLevelHookCapture.cs`：

```csharp
using MacroHid.Core;
using MacroStudio;

namespace InputApiProbe;

/// <summary>A0 baseline: the production GlobalKeyboardHook (low-level keyboard + mouse hook on an AboveNormal dispatcher thread).</summary>
internal sealed class LowLevelHookCapture(ushort vk) : ICaptureStrategy
{
    private GlobalKeyboardHook? hook;

    public string Id => "ll-hook";
    public event Action<long>? Captured;

    public void Start()
    {
        hook = new GlobalKeyboardHook { ProbeInjectedInputTag = new IntPtr((long)Native.Tag) };
        hook.ProbeInputObserved += (key, down, tick) => { if (key == vk && down) Captured?.Invoke(tick); };
        hook.Start(new HotkeyGesture(HidModifier.None, HidKey.F8));
    }

    public void Stop() { hook?.Dispose(); hook = null; }
    public void Dispose() => Stop();
}
```

- [ ] **Step 3: A1 Raw Input**

`Capture/RawInputCapture.cs`：

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InputApiProbe;

/// <summary>A1: Raw Input keyboard with RIDEV_INPUTSINK on a hidden window (cannot swallow keys).</summary>
internal sealed class RawInputCapture(ushort vk) : ICaptureStrategy
{
    private const uint WmInput = 0x00FF, RidInput = 0x10000003, RidevRemove = 0x1, RidevInputSink = 0x100;
    private const int HeaderSize = 24; // RAWINPUTHEADER on x64; RAWKEYBOARD follows

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    private readonly byte[] buffer = new byte[64];
    private MessageWindow? window;

    public string Id => "raw-input";
    public event Action<long>? Captured;

    public void Start()
    {
        window = new MessageWindow(OnMessage, visible: false, "raw-input", onCreated: hwnd =>
        {
            var device = new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevInputSink, Target = hwnd };
            if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
                throw new InvalidOperationException($"RegisterRawInputDevices failed, error={Marshal.GetLastWin32Error()}");
        });
    }

    private bool OnMessage(uint message, IntPtr w, IntPtr l)
    {
        if (message != WmInput) return false;
        long tick = Stopwatch.GetTimestamp();
        uint size = (uint)buffer.Length;
        if (GetRawInputData(l, RidInput, buffer, ref size, (uint)HeaderSize) > 0)
        {
            ushort flags = BitConverter.ToUInt16(buffer, HeaderSize + 2);  // RAWKEYBOARD.Flags, bit 0 = RI_KEY_BREAK
            ushort key = BitConverter.ToUInt16(buffer, HeaderSize + 6);    // RAWKEYBOARD.VKey
            if (key == vk && (flags & 1) == 0) Captured?.Invoke(tick);
        }
        return false; // let DefWindowProc do the required WM_INPUT cleanup
    }

    public void Stop()
    {
        var device = new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevRemove, Target = IntPtr.Zero };
        if (window is not null) RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        window?.Dispose();
        window = null;
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, byte[] data, ref uint size, uint headerSize);
}
```

- [ ] **Step 4: A2 RegisterHotKey**

`Capture/RegisterHotKeyCapture.cs`：

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InputApiProbe;

/// <summary>A2: RegisterHotKey on a hidden window. Down events only; the key is consumed by the system.</summary>
internal sealed class RegisterHotKeyCapture(ushort vk) : ICaptureStrategy
{
    private const uint WmHotKey = 0x0312, ModNoRepeat = 0x4000;
    private MessageWindow? window;

    public string Id => "register-hotkey";
    public event Action<long>? Captured;

    public void Start()
    {
        window = new MessageWindow(OnMessage, visible: false, "register-hotkey", onCreated: hwnd =>
        {
            if (!RegisterHotKey(hwnd, 1, ModNoRepeat, vk))
                throw new InvalidOperationException($"RegisterHotKey failed, error={Marshal.GetLastWin32Error()}");
        });
    }

    private bool OnMessage(uint message, IntPtr w, IntPtr l)
    {
        if (message != WmHotKey) return false;
        Captured?.Invoke(Stopwatch.GetTimestamp());
        return true;
    }

    public void Stop() { window?.Dispose(); window = null; }
    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
}
```

- [ ] **Step 5: A3 轮询**

`Capture/PollingCapture.cs`：

```csharp
using System.Diagnostics;

namespace InputApiProbe;

/// <summary>A3: dedicated thread polling GetAsyncKeyState. periodMs = 0 spins continuously; otherwise sleeps periodMs between polls.</summary>
internal sealed class PollingCapture(ushort vk, int periodMs) : ICaptureStrategy
{
    private Thread? thread;
    private volatile bool stop;

    public string Id => periodMs == 0 ? "poll-spin" : $"poll-{periodMs}ms";
    public event Action<long>? Captured;

    public void Start()
    {
        stop = false;
        thread = new Thread(Loop) { IsBackground = true, Name = "ApiProbe-" + Id, Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    private void Loop()
    {
        bool wasDown = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        while (!stop)
        {
            bool down = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
            long tick = Stopwatch.GetTimestamp();
            if (down && !wasDown) Captured?.Invoke(tick);
            wasDown = down;
            if (periodMs > 0) Thread.Sleep(periodMs);
        }
    }

    public void Stop() { stop = true; thread?.Join(1000); thread = null; }
    public void Dispose() => Stop();
}
```

- [ ] **Step 6: 构建并提交**

Run: `dotnet build src/tools/InputApiProbe/InputApiProbe.csproj -c Release`
Expected: `0 Error(s)`。

```bash
git add src/tools/InputApiProbe
git commit -m "Add four capture strategies to InputApiProbe" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: 实验 A 单独组与 provenance

**Files:**
- Create: `src/tools/InputApiProbe/Provenance.cs`
- Create: `src/tools/InputApiProbe/Capture/CaptureExperiment.cs`
- Modify: `src/tools/InputApiProbe/Program.cs`

- [ ] **Step 1: `Provenance.cs`**

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace InputApiProbe;

internal static class Provenance
{
    public static void Write(string path, string[] args)
    {
        string code = typeof(Provenance).Assembly.Location;
        var data = new Dictionary<string, object?>
        {
            ["date_utc"] = DateTime.UtcNow.ToString("o"),
            ["args"] = args,
            ["os"] = RuntimeInformation.OSDescription,
            ["cpu"] = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            ["logical_processors"] = Environment.ProcessorCount,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["timer_resolution_request_ms"] = 1,
            ["power_scheme"] = Run("powercfg", "/getactivescheme"),
            ["assembly"] = Path.GetFileName(code),
            ["assembly_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(code))),
            ["note"] = "Latency of synthetic input only; see docs/superpowers/specs/2026-10-15-input-api-comparison-design.md for limits."
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            string text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);
            return text;
        }
        catch (Exception ex) { return "unavailable: " + ex.Message; }
    }
}
```

- [ ] **Step 2: `CaptureExperiment.cs`**

```csharp
using System.Diagnostics;
using System.Text;

namespace InputApiProbe;

internal static class CaptureExperiment
{
    private const int Warmup = 20;

    private static readonly (string Id, Func<ICaptureStrategy> Create)[] Candidates =
    {
        ("ll-hook", () => new LowLevelHookCapture(Native.VkF24)),
        ("raw-input", () => new RawInputCapture(Native.VkF24)),
        ("register-hotkey", () => new RegisterHotKeyCapture(Native.VkF24)),
        ("poll-spin", () => new PollingCapture(Native.VkF24, 0)),
        ("poll-1ms", () => new PollingCapture(Native.VkF24, 1)),
    };

    /// <summary>Injects F24 down, waits for the strategy to report it, then F24 up.</summary>
    private sealed class TriggerProbe
    {
        private readonly ManualResetEventSlim signal = new(false);
        private long capturedTick;
        private int count;

        public void OnCaptured(long tick)
        {
            if (Interlocked.Increment(ref count) == 1) { Volatile.Write(ref capturedTick, tick); signal.Set(); }
        }

        public (long Send, long Capture, int Count) Fire()
        {
            signal.Reset(); Volatile.Write(ref capturedTick, 0); Interlocked.Exchange(ref count, 0);
            long send = Stopwatch.GetTimestamp();
            Native.KeyDown(InjectVariant.Vk);
            bool seen = signal.Wait(100);
            long tick = Volatile.Read(ref capturedTick);
            Native.KeyUp(InjectVariant.Vk);
            Thread.Sleep(10); // let late duplicates arrive before reading the count
            return (send, seen ? tick : 0, Volatile.Read(ref count));
        }
    }

    public static int Run(int rounds, int perRound, string output, string[] args)
    {
        Directory.CreateDirectory(output);
        using var samples = new StreamWriter(Path.Combine(output, "capture-samples.csv"), false, new UTF8Encoding(false));
        using var blocks = new StreamWriter(Path.Combine(output, "capture-blocks.csv"), false, new UTF8Encoding(false));
        samples.WriteLine("round,strategy,sample,send_qpc,capture_qpc,captures,frequency");
        blocks.WriteLine("round,strategy,samples,lost,wall_ms,cpu_ms,error");
        Provenance.Write(Path.Combine(output, "capture-provenance.json"), args);
        for (int round = 1; round <= rounds; round++)
            for (int k = 0; k < Candidates.Length; k++)
            {
                var (id, create) = Candidates[(k + round - 1) % Candidates.Length]; // rotate the order every round
                RunBlock(round, id, create, perRound, samples, blocks);
            }
        return 0;
    }

    private static void RunBlock(int round, string id, Func<ICaptureStrategy> create, int perRound,
        StreamWriter samples, StreamWriter blocks)
    {
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        int lost = 0, done = 0;
        string error = "";
        try
        {
            using var strategy = create();
            var probe = new TriggerProbe();
            strategy.Captured += probe.OnCaptured;
            strategy.Start();
            Thread.Sleep(200); // let the strategy's thread and registrations settle
            int seen = 0;
            for (int i = 0; i < Warmup; i++) if (probe.Fire().Count > 0) seen++;
            if (seen == 0) throw new InvalidOperationException("strategy observed none of the warm-up triggers");
            for (int i = 0; i < perRound; i++)
            {
                var (send, capture, count) = probe.Fire();
                if (count == 0) lost++;
                samples.WriteLine($"{round},{id},{i},{send},{capture},{count},{Stopwatch.Frequency}");
                done++;
            }
        }
        catch (Exception ex) { error = ex.Message.Replace(',', ';').Replace('\r', ' ').Replace('\n', ' '); }
        double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        blocks.WriteLine($"{round},{id},{done},{lost},{wall.ElapsedMilliseconds},{cpuMs:F0},{error}");
        samples.Flush(); blocks.Flush();
        Console.WriteLine($"round {round} {id}: n={done} lost={lost} {error}");
    }
}
```

- [ ] **Step 3: 在 `Program.cs` 接入 `capture`**

把 `Program.cs` 整体替换为：

```csharp
namespace InputApiProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string Option(string key, string fallback)
            { int at = Array.IndexOf(args, key); return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback; }

            Native.timeBeginPeriod(1); // stable 10ms pacing; recorded in provenance
            try
            {
                switch (args.FirstOrDefault())
                {
                    case "capture":
                        return CaptureExperiment.Run(int.Parse(Option("--rounds", "5")), int.Parse(Option("--per-round", "2000")),
                            Option("--output", "tmp/input-api"), args);
                    default:
                        Console.Error.WriteLine("Usage: InputApiProbe capture|capture-paired|inject|--receiver <name> [options]");
                        return 2;
                }
            }
            finally { Native.timeEndPeriod(1); }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
```

- [ ] **Step 4: 小规模烟测（同时回答「Raw Input 是否收到合成输入」）**

Run:
```bash
dotnet build src/tools/InputApiProbe/InputApiProbe.csproj -c Release
src/tools/InputApiProbe/bin/Release/net8.0-windows/InputApiProbe.exe capture --rounds 1 --per-round 50 --output tmp/input-api-smoke
```
Expected：每个候选一行 `round 1 <id>: n=50 lost=0`。运行期间不要碰键盘。
- 如果 `raw-input` 一行带 `strategy observed none of the warm-up triggers`：这是真实结论（该方式看不到合成输入），记入结果文档，自动组排除它，不是工程缺陷。先排除是消息窗口类型问题：在 `RawInputCapture.Start()` 里把 `new MessageWindow(...)` 加上参数 `messageOnly: false` 重试一次，再看结果；结论以重试后的为准，两次都写进结果文档。
- `ll-hook` 若丢失，检查是否被其他软件的钩子拦截。

- [ ] **Step 5: 汇总烟测数据**

Run: `python scripts/summarize_input_api.py capture tmp/input-api-smoke/capture-samples.csv`
Expected：打印一张表，`ll-hook` 行为 baseline，其余行 verdict 为 `inconclusive`（只有一轮）。检查 `tmp/input-api-smoke/capture-blocks.csv` 的 `cpu_ms`，`poll-spin` 应明显高于其他。

- [ ] **Step 6: 提交**

```bash
git add src/tools/InputApiProbe
git commit -m "Add capture experiment runner with provenance" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: 实验 A 并列组（物理按键）

**Files:**
- Create: `src/tools/InputApiProbe/Capture/CapturePairedExperiment.cs`
- Modify: `src/tools/InputApiProbe/Program.cs`

- [ ] **Step 1: 实现并列组**

`Capture/CapturePairedExperiment.cs`：

```csharp
using System.Diagnostics;
using System.Text;

namespace InputApiProbe;

/// <summary>All capture strategies armed at once; one physical key press reaches each of them. Records raw capture ticks only.</summary>
internal static class CapturePairedExperiment
{
    public static ushort ParseKey(string name)
    {
        if (name.Length >= 2 && name[0] is 'F' or 'f' && int.TryParse(name.AsSpan(1), out int n) && n is >= 1 and <= 24)
            return (ushort)(0x70 + n - 1);
        throw new ArgumentException($"Unsupported key '{name}', use F1..F24.");
    }

    public static int Run(int samples, ushort vk, string output, string[] args)
    {
        Directory.CreateDirectory(output);
        var candidates = new ICaptureStrategy[]
        {
            new LowLevelHookCapture(vk), new RawInputCapture(vk), new RegisterHotKeyCapture(vk),
            new PollingCapture(vk, 0), new PollingCapture(vk, 1)
        };
        var ticks = new Dictionary<string, List<long>>();
        var armed = new List<ICaptureStrategy>();
        foreach (var strategy in candidates)
        {
            try
            {
                var list = new List<long>();
                strategy.Captured += tick => { lock (list) list.Add(tick); };
                strategy.Start();
                ticks[strategy.Id] = list;
                armed.Add(strategy);
            }
            catch (Exception ex) { Console.Error.WriteLine($"{strategy.Id}: not armed ({ex.Message})"); strategy.Dispose(); }
        }
        try
        {
            Thread.Sleep(300);
            Console.WriteLine($"PHYSICAL_READY: press and release the key {samples} times, about 0.5s apart. Do not touch anything else.");
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < 300)
            {
                int first; lock (ticks["ll-hook"]) first = ticks["ll-hook"].Count;
                if (first >= samples) break;
                Thread.Sleep(50);
            }
            Thread.Sleep(500);
        }
        finally { foreach (var strategy in armed) strategy.Dispose(); }
        using var file = new StreamWriter(Path.Combine(output, "capture-paired.csv"), false, new UTF8Encoding(false));
        file.WriteLine("sample,strategy,capture_qpc,frequency");
        foreach (var (id, list) in ticks)
            lock (list) for (int i = 0; i < list.Count; i++) file.WriteLine($"{i},{id},{list[i]},{Stopwatch.Frequency}");
        Provenance.Write(Path.Combine(output, "capture-paired-provenance.json"), args);
        foreach (var (id, list) in ticks) Console.WriteLine($"{id}: {list.Count} captures");
        return 0;
    }
}
```

注意：`name[0] is 'F' or 'f' && ...` 在 C# 中 `is 'F' or 'f'` 是模式组合，优先级高于 `&&`，写法正确。

- [ ] **Step 2: 在 `Program.cs` 的 switch 中加入分支**

在 `case "capture":` 之前插入：

```csharp
                    case "capture-paired":
                        return CapturePairedExperiment.Run(int.Parse(Option("--physical", "30")),
                            CapturePairedExperiment.ParseKey(Option("--key", "F8")), Option("--output", "tmp/input-api"), args);
```

- [ ] **Step 3: 构建，并用一次短物理测试验证**

Run:
```bash
dotnet build src/tools/InputApiProbe/InputApiProbe.csproj -c Release
src/tools/InputApiProbe/bin/Release/net8.0-windows/InputApiProbe.exe capture-paired --physical 5 --output tmp/input-api-smoke
python scripts/summarize_input_api.py paired tmp/input-api-smoke/capture-paired.csv
```
Expected：提示 `PHYSICAL_READY` 后手动按 F8 五次；末尾每个策略输出 `N captures`（轮询和 `ll-hook` 应为 5，`register-hotkey` 为 5，`raw-input` 为 5 或者 0 取决于设备），汇总打印各策略相对 `ll-hook` 的差值（µs）。

- [ ] **Step 4: 提交**

```bash
git add src/tools/InputApiProbe
git commit -m "Add paired physical-key capture comparison" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: 实验 B 接收进程与空钩子

**Files:**
- Create: `src/tools/InputApiProbe/Inject/ReceiverProcess.cs`
- Create: `src/tools/InputApiProbe/EmptyHook.cs`
- Modify: `src/tools/InputApiProbe/Program.cs`

- [ ] **Step 1: 接收进程**

共享内存布局（发送端与接收端必须一致）：偏移 0 = 接收端就绪标志(int32)；8 = 接收窗口句柄(int64)；16 = 当前样本行号(int32，-1 表示忽略)；20 = 结束标志(int32)；64 起每行 32 字节：+0 按下 QPC(int64)、+8 抬起 QPC(int64)、+16 按下计数(int32)、+20 抬起计数(int32)、+24 收到的 VK(int32)。

`Inject/ReceiverProcess.cs`：

```csharp
using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace InputApiProbe;

/// <summary>Separate-process Win32 window. Records the QPC of the first WM_KEYDOWN / WM_KEYUP for the row the sender selected.</summary>
internal static class ReceiverProcess
{
    public const int Header = 64, Stride = 32;
    public const int Ready = 0, WindowHandle = 8, CurrentRow = 16, Done = 20;
    public const int DownQpc = 0, UpQpc = 8, DownCount = 16, UpCount = 20, DownVk = 24;

    public static long RowOffset(int row) => Header + (long)row * Stride;

    public static void Run(string name)
    {
        using var memory = MemoryMappedFile.OpenExisting(name);
        using var view = memory.CreateViewAccessor();
        using var window = new MessageWindow((message, w, l) =>
        {
            if (message is not (0x100 or 0x101)) return false; // WM_KEYDOWN / WM_KEYUP
            long tick = Stopwatch.GetTimestamp();
            int row = view.ReadInt32(CurrentRow);
            if (row < 0) return false;
            bool down = message == 0x100;
            long at = RowOffset(row);
            long countAt = at + (down ? DownCount : UpCount);
            int previous = view.ReadInt32(countAt);
            view.Write(countAt, previous + 1);
            if (previous == 0)
            {
                view.Write(at + (down ? DownQpc : UpQpc), tick);
                if (down) view.Write(at + DownVk, (int)w.ToInt64());
            }
            return false;
        }, visible: true, "MacroHID input API probe receiver", onCreated: hwnd =>
        {
            view.Write(WindowHandle, hwnd.ToInt64());
            view.Write(Ready, 1);
            Native.SetForegroundWindow(hwnd);
        });
        while (view.ReadInt32(Done) == 0) Thread.Sleep(100);
    }
}
```

- [ ] **Step 2: 空钩子**

`EmptyHook.cs`：

```csharp
using System.Runtime.InteropServices;

namespace InputApiProbe;

/// <summary>A do-nothing WH_KEYBOARD_LL hook on its own message thread: the lower bound of what an active hook adds to the input path.</summary>
internal sealed class EmptyHook : IDisposable
{
    private delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);

    private readonly HookProc proc = (code, w, l) => CallNextHookEx(IntPtr.Zero, code, w, l);
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private uint threadId;
    private bool installed;

    public EmptyHook()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "ApiProbe-empty-hook", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        ready.Wait();
        if (!installed) throw new InvalidOperationException("Could not install the empty low-level hook");
    }

    private void Run()
    {
        threadId = Native.GetCurrentThreadId();
        IntPtr hook = SetWindowsHookEx(13, proc, IntPtr.Zero, 0);
        installed = hook != IntPtr.Zero;
        ready.Set();
        if (!installed) return;
        while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        UnhookWindowsHookEx(hook);
    }

    public void Dispose()
    {
        Native.PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero); // WM_QUIT
        thread.Join(2000);
        GC.KeepAlive(proc);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
}
```

- [ ] **Step 3: 在 `Program.cs` 中接入 `--receiver`**

在 `switch` 的 `default:` 之前插入：

```csharp
                    case "--receiver":
                        ReceiverProcess.Run(args[1]);
                        return 0;
```

- [ ] **Step 4: 构建并提交**

Run: `dotnet build src/tools/InputApiProbe/InputApiProbe.csproj -c Release`
Expected: `0 Error(s)`。

```bash
git add src/tools/InputApiProbe
git commit -m "Add Win32 receiver process and empty hook for injection experiment" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: 实验 B 发送端

**Files:**
- Create: `src/tools/InputApiProbe/Inject/InjectExperiment.cs`
- Modify: `src/tools/InputApiProbe/Program.cs`

- [ ] **Step 1: 发送端**

`Inject/InjectExperiment.cs`：

```csharp
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace InputApiProbe;

internal static class InjectExperiment
{
    private const int Warmup = 20;

    private static readonly (string Id, InjectVariant Variant)[] Variants =
    {
        ("vk", InjectVariant.Vk), ("scan", InjectVariant.Scan), ("vk-scan", InjectVariant.VkScan),
        ("keybd-event", InjectVariant.KeybdEvent), ("merged-pair", InjectVariant.MergedPair),
    };

    public static int Run(int rounds, int perBlock, string output, string[] args)
    {
        Directory.CreateDirectory(output);
        // Native.ScanF24 == 0 means this layout has no scan code for F24; the scan variants cannot be tested.
        var variants = Variants.Where(v => Native.ScanF24 != 0 || v.Variant is InjectVariant.Vk or InjectVariant.MergedPair).ToArray();
        var conditions = variants.SelectMany(v => new[] { (v.Id, v.Variant, Hook: false), (v.Id, v.Variant, Hook: true) }).ToArray();
        long totalRows = (long)rounds * conditions.Length * perBlock;
        string name = "Local\\MacroHID-ApiProbe-" + Guid.NewGuid().ToString("N");
        using var memory = MemoryMappedFile.CreateNew(name, ReceiverProcess.Header + totalRows * ReceiverProcess.Stride);
        using var view = memory.CreateViewAccessor();
        view.Write(ReceiverProcess.CurrentRow, -1);

        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        info.ArgumentList.Add("--receiver"); info.ArgumentList.Add(name);
        using var receiver = Process.Start(info) ?? throw new InvalidOperationException("Cannot start receiver");
        using var csv = new StreamWriter(Path.Combine(output, "inject-samples.csv"), false, new UTF8Encoding(false));
        csv.WriteLine("round,variant,hook,sample,send_down_qpc,send_up_qpc,recv_down_qpc,recv_up_qpc,down_count,up_count,vk,frequency");
        Provenance.Write(Path.Combine(output, "inject-provenance.json"), args);
        try
        {
            var start = Stopwatch.StartNew();
            while (view.ReadInt32(ReceiverProcess.Ready) == 0 && start.Elapsed.TotalSeconds < 30) Thread.Sleep(20);
            IntPtr target = new(view.ReadInt64(ReceiverProcess.WindowHandle));
            if (target == IntPtr.Zero) throw new InvalidOperationException("Receiver did not start");
            Console.WriteLine("WINDOW_READY: click the receiver window to bring it to the foreground, then do not touch anything.");
            while (Native.GetForegroundWindow() != target && start.Elapsed.TotalSeconds < 300)
            { if (receiver.HasExited) throw new InvalidOperationException("Receiver closed"); Thread.Sleep(50); }
            if (Native.GetForegroundWindow() != target) throw new TimeoutException("Receiver never became the foreground window");

            int row = 0;
            for (int round = 1; round <= rounds; round++)
                for (int k = 0; k < conditions.Length; k++)
                {
                    var (id, variant, hook) = conditions[(k + round - 1) % conditions.Length];
                    using var hookScope = hook ? new EmptyHook() : null;
                    Thread.Sleep(100);
                    for (int i = 0; i < Warmup; i++) Sample(view, target, variant, -1);
                    for (int i = 0; i < perBlock; i++)
                    {
                        var s = Sample(view, target, variant, row);
                        long at = ReceiverProcess.RowOffset(row);
                        csv.WriteLine($"{round},{id},{(hook ? "ll-empty" : "none")},{i},{s.SendDown},{s.SendUp}," +
                            $"{view.ReadInt64(at + ReceiverProcess.DownQpc)},{view.ReadInt64(at + ReceiverProcess.UpQpc)}," +
                            $"{view.ReadInt32(at + ReceiverProcess.DownCount)},{view.ReadInt32(at + ReceiverProcess.UpCount)}," +
                            $"{view.ReadInt32(at + ReceiverProcess.DownVk)},{Stopwatch.Frequency}");
                        row++;
                    }
                    csv.Flush();
                    Console.WriteLine($"round {round} {id} hook={(hook ? "ll-empty" : "none")}: done");
                }
            return 0;
        }
        finally
        {
            view.Write(ReceiverProcess.Done, 1);
            if (!receiver.WaitForExit(5000)) receiver.Kill();
        }
    }

    /// <summary>Sends F24 down, waits for the receiver, sends F24 up, waits for the receiver. row -1 = warm-up (receiver ignores it).</summary>
    private static (long SendDown, long SendUp) Sample(MemoryMappedViewAccessor view, IntPtr target, InjectVariant variant, int row)
    {
        if (Native.GetForegroundWindow() != target) throw new InvalidOperationException("Receiver lost foreground focus; stopped before further injection.");
        long at = row >= 0 ? ReceiverProcess.RowOffset(row) : 0;
        view.Write(ReceiverProcess.CurrentRow, row);
        long sendDown = Stopwatch.GetTimestamp();
        Native.KeyDown(variant);
        if (row >= 0) WaitFor(view, at + ReceiverProcess.DownCount); else Thread.Sleep(5);
        long sendUp = variant == InjectVariant.MergedPair ? sendDown : Stopwatch.GetTimestamp();
        Native.KeyUp(variant);
        if (row >= 0) WaitFor(view, at + ReceiverProcess.UpCount); else Thread.Sleep(5);
        Thread.Sleep(10);
        view.Write(ReceiverProcess.CurrentRow, -1);
        return (sendDown, sendUp);
    }

    private static void WaitFor(MemoryMappedViewAccessor view, long countAt)
    {
        var wait = Stopwatch.StartNew();
        while (view.ReadInt32(countAt) == 0 && wait.ElapsedMilliseconds < 100) Thread.Yield();
    }
}
```

- [ ] **Step 2: 在 `Program.cs` 接入 `inject`**

在 `case "--receiver":` 之前插入：

```csharp
                    case "inject":
                        return InjectExperiment.Run(int.Parse(Option("--rounds", "5")), int.Parse(Option("--per-block", "2000")),
                            Option("--output", "tmp/input-api"), args);
```

- [ ] **Step 3: 构建并烟测**

Run:
```bash
dotnet build src/tools/InputApiProbe/InputApiProbe.csproj -c Release
src/tools/InputApiProbe/bin/Release/net8.0-windows/InputApiProbe.exe inject --rounds 1 --per-block 50 --output tmp/input-api-smoke
python scripts/summarize_input_api.py inject tmp/input-api-smoke/inject-samples.csv
```
Expected：出现 `WINDOW_READY` 后点击接收窗口，然后不要碰键鼠；十个条件各输出一行 `done`；汇总表里每个条件 `lost=0 dup=0`，延迟为几百 µs 到几 ms 量级，`verdict` 为 `inconclusive`（只有一轮）。若 `scan` 条件 `lost` 等于样本数，说明扫描码没有被翻译成 VK 0x87，属于有效结论，写入结果文档，不是工程缺陷。

- [ ] **Step 4: 提交**

```bash
git add src/tools/InputApiProbe
git commit -m "Add injection experiment sender" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: 正式测量、结果文档与收尾

**Files:**
- Create: `docs/input-api-comparison-2026-10-15.md`
- Modify: `docs/current-precision-status.md`（顶部指针一行）

本任务需要用户在场：接收窗口需要点击置前，测量期间不能碰键鼠或做其他 CPU 工作。执行前先向用户确认时间窗（总时长约 20–40 分钟）。

- [ ] **Step 1: 记录环境并运行实验 A 单独组**

Run:
```bash
src/tools/InputApiProbe/bin/Release/net8.0-windows/InputApiProbe.exe capture --rounds 5 --per-round 2000 --output tmp/input-api-20261015
python scripts/summarize_input_api.py capture tmp/input-api-20261015/capture-samples.csv --output tmp/input-api-20261015/capture-summary.json
```
Expected：每个候选五轮 `n=2000`；汇总表给出每个候选 verdict。`capture-blocks.csv` 保存 CPU 时间。

- [ ] **Step 2: 运行实验 B**

Run:
```bash
src/tools/InputApiProbe/bin/Release/net8.0-windows/InputApiProbe.exe inject --rounds 5 --per-block 2000 --output tmp/input-api-20261015
python scripts/summarize_input_api.py inject tmp/input-api-20261015/inject-samples.csv --output tmp/input-api-20261015/inject-summary.json
```
Expected：10 个条件 × 5 轮，每块 2,000 样本。以 `vk|none` 为基线，其余条件（含 `vk|ll-empty`，即钩子税）得到 verdict。

- [ ] **Step 3: 并列组物理按键**

Run:
```bash
src/tools/InputApiProbe/bin/Release/net8.0-windows/InputApiProbe.exe capture-paired --physical 30 --output tmp/input-api-20261015
python scripts/summarize_input_api.py paired tmp/input-api-20261015/capture-paired.csv --output tmp/input-api-20261015/paired-summary.json
```
Expected：用户按 30 次 F8；各策略相对 `ll-hook` 的先后差值与 `count_mismatch` 标志。

- [ ] **Step 4: 写结果文档**

`docs/input-api-comparison-2026-10-15.md` 必须包含，且数字全部取自 `tmp/input-api-20261015/*.json` 与 `*-blocks.csv`，不得凭印象填写：

1. 环境与限制：系统、CPU、电源方案（来自 `*-provenance.json`），合成输入与物理键盘路径不同，结果仅用于候选间比较。
2. 实验 A 单独组表：每个候选的 N、丢失、重复、P50、P95、P99、P99.9、最大、五轮 P50 极差、CPU 毫秒/墙钟毫秒、verdict。
3. 实验 B 表：十个条件同样的列，加上抬起事件延迟；单独一行给出「钩子税」= `vk|ll-empty` 相对 `vk|none` 的 P50/P99 差。
4. 并列组表：各策略相对 `ll-hook` 的先后差分位数。
5. 语义兼容表：吞键、区分注入与物理、释放事件、鼠标键、修饰键组合、是否需要消息泵，逐候选填写（依据 Win32 文档与本次观察，观察不到的写「未验证」）。
6. 结论：列出 verdict 为 `candidate` 的方案与它们的语义缺口；`no-net-benefit`、`inconclusive`、`invalid` 的一并保留并说明；明确声明未测游戏内延迟，不承诺任何改善。
7. 后续：若有 `candidate`，下一步是单独设计其接入；驱动级注入另建新仓库的建议保留在这里（见设计文档范围外说明）。

- [ ] **Step 5: 更新状态文档指针**

在 `docs/current-precision-status.md` 第 7 行（以「三档策略、准备缓存」开头的段落）之前新增一段：

```markdown
2026-10-15 输入 API 对照（低层钩子 / Raw Input / RegisterHotKey / 轮询，以及 SendInput 各写法）的结果见 [输入 API 对照](input-api-comparison-2026-10-15.md)。仅比较合成输入的候选方案，不是游戏内延迟结论。

```

- [ ] **Step 6: 全量检查并提交**

Run:
```bash
python -m unittest discover -s tests -p "test_input_api_summary.py"
dotnet build src/tools/InputApiProbe/InputApiProbe.csproj -c Release
git status --short
```
Expected：unittest `OK`；构建 `0 Error(s)`；`git status` 只显示 `docs/` 两个文件与（若有）已忽略的 `tmp/` 之外无多余改动（`tmp/` 已被 `.gitignore` 忽略，原始 CSV 保留在本地，不提交）。

```bash
git add docs/input-api-comparison-2026-10-15.md docs/current-precision-status.md
git commit -m "Record input API comparison results" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 7: 更新记忆并向用户汇报**

更新 `latency-api-comparison-plan` 记忆：写入实验结论与「驱动级注入另建仓库」的时机（是否进入、仓库名待确认）。向用户汇报 verdict 为 `candidate` 的方案和被排除方案的原因。
