# MacroHID Usage Guide / MacroHID 使用说明

## 中文

### 1. 启动

运行 MacroStudio：

```powershell
.\artifacts\local-run\MacroStudio\MacroStudio.exe
```

如果目标应用是管理员权限窗口，请用管理员身份启动 MacroStudio。应用启动后可以通过顶部“窗口”菜单或左下角工具窗口按钮显示/隐藏：

- 宏数据库
- 序列
- 条件序列
- 动作面板
- 播放控制
- MCRX JSON

### 2. 宏数据库

宏数据库模拟文件管理器：

- 宏类似普通文件，文件夹用于分类。
- 支持新建、复制、粘贴、删除、重命名和拖动整理。
- 导入的宏会进入宏数据库。
- 每个宏可以保存触发键、播放模式、次数和进程筛选。

### 3. 基础序列

基础序列是宏播放后立即执行的主流程。可从动作面板拖入或点击添加：

- 延迟：固定毫秒或随机范围。
- 键盘功能：按下、抬起、文本输入、修饰键。
- 鼠标按键：左键、右键、中键、X1、X2，支持按下/抬起和可选坐标。
- 鼠标移动：相对或绝对坐标。
- 滚轮：垂直和水平滚轮。
- 文本功能：Unicode 文本输入。
- 宏：调用宏数据库中的另一个宏。
- 循环：循环内可继续拖入步骤。

序列支持多选、框选、拖动排序、拖入循环、复制、剪切、粘贴、删除、上移和下移。点击步骤卡片可直接打开可视化属性编辑器。

### 4. 条件序列

每个宏可以同时拥有条件序列。播放开始后：

- 基础序列直接执行。
- 条件序列持续检查条件。
- 条件成立时执行该条件的 then-actions。

条件可以包含：

- 条件名称。
- 条件类型，例如像素颜色。
- 执行步骤范围，列表中显示具体步骤名称。
- 触发后时间窗口，时间从宏触发键按下开始计算。
- 屏幕区域或像素坐标。
- 目标颜色和容差。
- 触发后执行动作序列。

条件中的动作序列与基础序列使用同一套交互：多选、框选、拖动、复制、剪切、粘贴、拖入循环、行内复制/删除和属性编辑。

### 5. 播放控制

每个宏支持三种模式：

- 播放 N 次：触发后播放指定次数，默认 1。
- 按下后循环：按一次开始循环，再按一次停止。
- 按住后循环：按住触发键时循环，松开停止。

触发键支持单键、组合键、Ctrl、Alt、Shift、Win 和鼠标侧键。进程筛选可指定前台窗口进程名，例如 `YuanShen.exe`；只有当前前台进程匹配时该宏才会触发。

精度模式支持基础（0.5ms 目标）、高性能（0.25ms 目标）和极限（0.1ms 目标）。极限会加载 `MacroHid.NativePlayback.dll`，把基础序列热路径放到 x64 C++ in-process 播放循环中执行；密集 1ms/2ms 循环的 auto 模式优先 inline native path，显式 standby 模式保留预热低启动路径。DLL 不可用或当前宏需要 managed 条件激活时会自动回退。目标值是 LatencyProbe 的统计阈值和优化方向，Windows 用户态仍可能出现抢占长尾。

### 6. MCRX JSON

MCRX JSON 面板显示当前宏的原始 JSON。修改 JSON 后，合法内容会同步刷新基础序列和条件序列；非法 JSON 会保留当前编辑状态并显示错误。

### 7. 导入和导出

宏数据库中提供导入/导出：

- 导入：`.mcrx`、MacroConverter XML、Razer Synapse XML、Lua/Logitech Lua、XMouse、QMacro。
- 雷云模块：先加载模块 XML，再导入主宏，可保留嵌套宏调用关系。
- 导出：可将当前宏导出为支持的目标格式。

### 8. 精度探测

本地运行包内的 LatencyProbe 支持 managed/native 对比：

```powershell
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend managed --profile extreme --loop-steps 10 --loop-interval-us 1000 --iterations 5000
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile extreme --loop-steps 10 --loop-interval-us 1000 --iterations 5000 --trace-outliers artifacts\native-extreme-outliers.csv
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile extreme --target-us 100 --warm-native --cpu-scan --loop-steps 10 --loop-interval-us 1000 --iterations 5000
```

## English

### 1. Launch

Run MacroStudio:

```powershell
.\artifacts\local-run\MacroStudio\MacroStudio.exe
```

If the target application is elevated, run MacroStudio as Administrator. Use the top Window menu or the lower-left tool-window buttons to show or hide:

- Macro Library
- Sequence
- Condition Sequence
- Action Palette
- Playback Control
- MCRX JSON

### 2. Macro Library

The macro library behaves like a lightweight file manager:

- Macros are file-like items; folders organize them.
- Create, copy, paste, delete, rename, and drag items.
- Imported macros are added to the library.
- Each macro can store its trigger, playback mode, run count, and foreground process filter.

### 3. Base Sequence

The base sequence runs immediately after playback starts. Add steps by dragging or clicking action templates:

- Delay: fixed milliseconds or random range.
- Keyboard: down, up, text input, modifiers.
- Mouse button: left, right, middle, X1, X2, down/up, optional coordinate.
- Mouse move: relative or absolute coordinates.
- Wheel: vertical and horizontal wheel.
- Text: Unicode text input.
- Macro: call another macro from the library.
- Loop: contains nested steps.

The sequence supports multi-select, rectangle select, drag sorting, drop into loops, copy, cut, paste, delete, move up, and move down. Click a step card to edit its properties visually.

### 4. Condition Sequence

Each macro can also have condition directives. After playback starts:

- The base sequence runs directly.
- The condition sequence monitors its conditions.
- When a condition is met, its then-actions are executed.

A condition can include:

- Name.
- Condition type, such as pixel color.
- Step range, displayed by concrete step labels.
- Trigger time window, measured from the macro trigger key press.
- Screen region or pixel coordinate.
- Target color and tolerance.
- Then-actions sequence.

Condition then-actions use the same interaction engine as the base sequence: multi-select, rectangle select, drag, copy, cut, paste, drop into loops, row copy/delete buttons, and property editing.

### 5. Playback Control

Each macro supports three modes:

- Fixed count: play N times, default 1.
- Toggle loop: press once to start looping, press again to stop.
- Hold loop: loop while the trigger is held, stop when released.

Triggers support single keys, chords, Ctrl, Alt, Shift, Win, and mouse side buttons. The process filter can restrict playback to a foreground process such as `YuanShen.exe`; the macro triggers only when the current foreground process matches.

Precision mode supports Basic (0.5ms target), High Performance (0.25ms target), and Extreme (0.1ms target). Extreme loads `MacroHid.NativePlayback.dll`, moving the base-sequence hot path into an x64 C++ in-process playback loop; dense 1ms/2ms loops prefer the inline native path in auto mode, while explicit standby mode keeps the warmed low-startup path. When the DLL is unavailable or the current macro needs managed condition activation, MacroHID falls back automatically. Targets are LatencyProbe thresholds and optimization goals; Windows user mode can still produce preemption tails.

### 6. MCRX JSON

The MCRX JSON panel shows the current macro document. Valid edits update the base and condition sequences live; invalid JSON keeps the current editor state and shows an error.

### 7. Import and Export

Import/export lives in the macro library:

- Import: `.mcrx`, MacroConverter XML, Razer Synapse XML, Lua/Logitech Lua, XMouse, QMacro.
- Razer modules: load module XML files before importing the main macro to preserve nested macro-call relationships where possible.
- Export: save the current macro to supported target formats.

### 8. Precision Probe

The local-run package includes LatencyProbe for managed/native comparison:

```powershell
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend managed --profile extreme --loop-steps 10 --loop-interval-us 1000 --iterations 5000
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile extreme --loop-steps 10 --loop-interval-us 1000 --iterations 5000 --trace-outliers artifacts\native-extreme-outliers.csv
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile extreme --target-us 100 --warm-native --cpu-scan --loop-steps 10 --loop-interval-us 1000 --iterations 5000
```
