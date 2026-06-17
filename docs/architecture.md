# Architecture / 架构说明

## 中文

### 总览

MacroHID 当前架构是纯 .NET + WPF + Win32 `SendInput` 的用户态系统：

```text
MacroStudio / MacroRunner
        |
        v
MacroHid.Core  -- .mcrx model, parser, serializer, compiler
        |
        +--> MacroHid.Converter -- import/export adapters
        |
        v
MacroHid.Runtime -- playback, precision context, conditions, pixels, SendInput
        |
        v
Windows SendInput / visible desktop pixels
```

没有内核驱动、没有后台 Windows service、没有 VHF/KMDF、没有 IOCTL。

### 项目结构

- `src/shared/MacroHid.Core`
  - `.mcrx` 数据模型。
  - 解析和序列化。
  - 步骤规范化，例如把点击拆成按下/抬起。
  - 序列树编辑器，例如多选移动、复制、循环内编辑、按下/抬起关联编辑。
  - 输入动作编译。
  - 条件模型和宏库索引。

- `src/shared/MacroHid.Converter`
  - MacroHID `.mcrx`。
  - MacroConverter XML。
  - Razer Synapse XML 和模块 XML。
  - Lua/Logitech Lua。
  - XMouse。
  - QMacro。

- `src/shared/MacroHid.Runtime`
  - 播放控制器。
  - 预编译播放计划。
  - 高精度 QPC 调度。
  - `PrecisionPlaybackContext`。
  - `SendInputMacroSink` 和 prepared native batches。
  - 像素采样、截图、模板匹配、OCR 接口占位。
  - 条件监控线程。

- `src/ui/MacroStudio`
  - WPF 编辑器。
  - 宏数据库。
  - IDEA 风格工具窗口。
  - 基础序列和条件动作序列共享的 `StepSequencePanel`。
  - 条件序列面板。
  - 动作面板、播放控制、MCRX JSON 面板。
  - 简体中文、繁体中文、英文资源。

- `src/tools/MacroRunner`
  - 命令行 dry-run 和 SendInput 播放。

- `src/tools/LatencyProbe`
  - 用户态调度、编码和提交延迟探测。

- `installer`
  - Inno Setup 安装脚本和语言文件。

### 播放流程

1. MacroStudio 或 MacroRunner 读取 `.mcrx`。
2. `McrxParser` 转换为宏文档模型。
3. `MacroStepNormalizer` 把可简化动作转换为运行时友好的步骤，例如点击拆分为 down/up。
4. `InputActionCompiler` 和运行时生成 `CompiledPlaybackPlan`。
5. 播放开始时进入 `PrecisionPlaybackContext`。
6. `MacroPlaybackExecutor` 按 QPC 时间轴提交 prepared input batches。
7. `SendInputMacroSink` 调用 Win32 `SendInput`。
8. 条件监控线程按时间窗口和像素条件触发 then-actions。
9. 播放结束或取消后释放状态并恢复优先级/计时器/GC 策略。

### 数据流

- 宏数据库保存为 `%APPDATA%\MacroHID\MacroLibrary\*.mcrx`。
- `library.json` 只保存库索引和 UI/播放元数据。
- 独立 `.mcrx` 可以通过打开/保存与其他机器共享。
- JSON 面板编辑合法内容后会重新解析并刷新基础序列和条件序列。

## English

### Overview

MacroHID is currently a pure user-mode .NET + WPF + Win32 `SendInput` system:

```text
MacroStudio / MacroRunner
        |
        v
MacroHid.Core  -- .mcrx model, parser, serializer, compiler
        |
        +--> MacroHid.Converter -- import/export adapters
        |
        v
MacroHid.Runtime -- playback, precision context, conditions, pixels, SendInput
        |
        v
Windows SendInput / visible desktop pixels
```

There is no kernel driver, no background Windows service, no VHF/KMDF layer, and no IOCTL path.

### Project Layout

- `src/shared/MacroHid.Core`
  - `.mcrx` document model.
  - Parser and serializer.
  - Step normalization, such as expanding clicks into down/up.
  - Sequence tree editing, including multi-select move, copy, loop editing, and linked down/up edits.
  - Input action compilation.
  - Condition model and macro library index.

- `src/shared/MacroHid.Converter`
  - MacroHID `.mcrx`.
  - MacroConverter XML.
  - Razer Synapse XML and module XML.
  - Lua/Logitech Lua.
  - XMouse.
  - QMacro.

- `src/shared/MacroHid.Runtime`
  - Playback controller.
  - Precompiled playback plan.
  - High-precision QPC scheduling.
  - `PrecisionPlaybackContext`.
  - `SendInputMacroSink` and prepared native batches.
  - Pixel sampling, capture, template matching, OCR bridge placeholder.
  - Condition monitor thread.

- `src/ui/MacroStudio`
  - WPF editor.
  - Macro library.
  - IDEA-style tool windows.
  - Shared `StepSequencePanel` for base sequences and condition then-actions.
  - Condition sequence panel.
  - Action palette, playback controls, and MCRX JSON panel.
  - Simplified Chinese, Traditional Chinese, and English resources.

- `src/tools/MacroRunner`
  - Command-line dry-run and SendInput playback.

- `src/tools/LatencyProbe`
  - User-mode scheduling, encoding, and submission latency probe.

- `installer`
  - Inno Setup script and language files.

### Playback Flow

1. MacroStudio or MacroRunner reads `.mcrx`.
2. `McrxParser` converts it into the macro document model.
3. `MacroStepNormalizer` converts shorthand actions into runtime-friendly steps, such as click to down/up.
4. `InputActionCompiler` and the runtime build a `CompiledPlaybackPlan`.
5. Playback enters `PrecisionPlaybackContext`.
6. `MacroPlaybackExecutor` submits prepared input batches on the QPC timeline.
7. `SendInputMacroSink` calls Win32 `SendInput`.
8. The condition monitor thread triggers then-actions based on time windows and pixel conditions.
9. Playback completion or cancellation restores priority, timer, and GC state.

### Data Flow

- Macro library entries are stored as `%APPDATA%\MacroHID\MacroLibrary\*.mcrx`.
- `library.json` stores library indexing and UI/playback metadata only.
- Standalone `.mcrx` files can be opened, saved, and shared.
- Valid edits in the JSON panel are reparsed and reflected in the base and condition sequences.
