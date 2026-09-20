# MacroHID Usage Guide / MacroHID 使用说明

面向用户的完整中文使用教程见仓库根目录 [README.md](../README.md)。下文保留开发者启动命令与英文对照。

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
- 支持 `Ctrl`/`Shift` 扩展多选、`Ctrl+A` 全选，以及批量复制、删除、锁定/解锁和拖动整理。
- “查看”可切换详细信息、列表、小图标和大图标；“排序方式”可选择自定义顺序、名称或修改日期。
- 双击文件夹进入，使用“向上一级”或 `Backspace` 返回；右键空白处可新建、粘贴、切换视图、排序和刷新。
- 导入的宏会进入宏数据库。
- 每个宏可以保存触发键、播放模式、次数和进程筛选。
- 选中宏后可在序列顶部点击“锁定编辑”；锁定状态会持久保存，仍可运行、监听和导出，但步骤、条件、JSON、名称、播放参数和删除操作均受保护。解除锁定后可继续编辑或删除。

宏数据库快捷键：`F2` 重命名、`Delete` 删除、`Ctrl+A` 全选、`Ctrl+C` 复制、`Ctrl+V` 粘贴、`Ctrl+D` 复制宏、`Ctrl+N` 新建宏、`Ctrl+Shift+N` 新建文件夹、`F5` 刷新。序列和条件列表支持 `Delete`、`Ctrl+A/C/X/V`；序列还支持 `Ctrl+Z/Y`。

### 3. 基础序列

基础序列是宏播放后立即执行的主流程。可从动作面板拖入或点击添加：

也可以点击序列顶部的“录制输入”。点击后先选择录制模式：复刻录入（保留真实间隔）、延迟录入（每个输入之间固定 5ms）或无延迟录入。程序会最小化主窗口，只记录键盘按下/抬起、鼠标左/右/中/X1/X2 按下/抬起，以及垂直/水平滚轮；不录入鼠标移动。按 `Ctrl+Shift+F12` 结束录制；停止组合键本身不会写入宏，主窗口恢复后录制结果会插入当前选中位置。录制期间会暂停全局宏触发监听，结束后自动恢复。锁定的宏不能录制。

- 延迟：固定毫秒或随机范围。
- 键盘功能：按下、抬起、文本输入、修饰键。
- 鼠标按键：左键、右键、中键、X1、X2，支持按下/抬起和可选坐标。
- 鼠标移动：相对或绝对坐标。
- 滚轮：垂直和水平滚轮。
- 指定窗口到前台：从运行窗口列表选择目标，按进程名及可选标题/标题正则定位；自动恢复最小化窗口，并在确认目标进程成为前台后继续。
- OCR 获取文本：框选消息区域，从整段混合文字中按普通文本或正则表达式提取指定匹配/捕获组，并写入剪贴板。UID 不要求是纯数字；可包含字母、横线、下划线或由规则允许的其他字符。可先“测试提取（不写剪贴板）”。
- OCR 文字点击：框选屏幕区域，按普通文字或正则表达式定位文字框中心；支持第 N 个匹配、单/双/三击和坐标偏移，并可先“测试定位（不点击）”。
- 文本功能：Unicode 文本输入。
- 宏：调用宏数据库中的另一个宏。
- 循环：循环内可继续拖入步骤。
- 停止本层宏动作：退出当前宏调用，外层调用继续执行。
- 停止本轮播放：丢弃宏当前一轮的剩余步骤；“按下后循环”和“按住后循环”会自然进入下一轮。
- 停止本次播放：终止触发键启动的整次播放和外层循环，效果与再次按下触发键停止一致。

宏可以调用自身。位于序列末尾的自调用会按可取消尾调用运行，不会持续增加调用栈；再次按触发键可以停止。

“OCR 获取文本”的常用规则可直接从下拉框选择：

- 获取全部文字：提取规则留空。
- 只获取连续数字：`\d+`，例如从 `编号 A-12345-B` 中得到 `12345`。
- 先过滤示例词，再只保留数字：默认过滤词为 `-6` 和 `4=3`。输入 `-6  4=3  177933444` 时，先删除两个过滤词，再删除所有非数字字符，最终得到 `177933444`。过滤词可改为任意内容，每行一个。
- 连续字母、数字、横线或下划线：`[A-Za-z0-9_-]+`。
- 获取 UID 后面的内容：自动处理 `UID:`、`UID：` 或 `UID=`。
- 获取冒号或等号后面的内容：适合“账号：ABC-123”一类消息。

序列支持多选、框选、拖动排序、拖入循环、复制、剪切、粘贴、删除、上移和下移。点击步骤卡片可直接打开可视化属性编辑器。

### 4. 条件序列

每个宏可以同时拥有条件序列。播放开始后：

- 基础序列直接执行。
- 条件序列持续检查条件。
- 条件成立时执行该条件的 then-actions。

条件可以包含：

- 条件名称。
- 条件类型，例如像素颜色或 OCR 文字。
- 执行步骤范围，列表中显示具体步骤名称。
- 触发后时间窗口，时间从宏触发键按下开始计算。
- 屏幕区域或像素坐标。
- 目标颜色和容差。
- OCR 期望文字；可选择包含匹配或带超时保护的正则表达式。
- 触发后执行动作序列。
- 执行方式：“同时执行”让主时间线和条件动作并行；“暂停主时间线”会等待条件动作完成后从原位置继续。

条件中的动作序列与基础序列使用同一套交互：多选、框选、拖动、复制、剪切、粘贴、拖入循环、行内复制/删除和属性编辑。选中条件后，可在“触发后执行”标题右侧点击“录制输入”；停止后，记录到的键鼠动作和延迟只会插入当前条件的触发动作，不会写入基础序列。

跨窗口 OCR 流程建议按“指定 QQ 到前台 → OCR 获取文本（正则提取 UID 到剪贴板）→ 指定原神到前台 → OCR 点击粘贴/搜索”排列。无需双击整条消息或发送 `Ctrl+C`；“指定窗口到前台”动作确认成功后才执行下一步，不依赖 `Alt+Tab` 的窗口顺序。

条件运行边界：

- 每条条件在一次播放迭代中最多触发一次；进入下一次“播放 N 次”或循环播放迭代时，会创建新的条件监听并重新判断。
- 条件动作可以调用其他宏。被调用宏中的“停止本层宏动作”只退出该次嵌套调用，后续条件动作仍会继续。
- “停止本轮播放”会结束宏1当前轮的基础序列，但保留外层循环；下一轮会创建新的条件监听。
- 条件动作直接或通过嵌套宏执行“停止本次播放”时，会终止基础序列和整个外层循环。
- 在条件动作中调用当前宏，只会执行它的基础步骤，不会递归创建该宏的条件监听。若要实现“视觉条件出现 → 完成条件动作 → 停止宏1当前轮 → 下一轮再次判断”，应将宏1设置为“按下后循环”或“按住后循环”，条件选择“暂停主时间线”，并把“停止本轮播放”放在条件动作末尾。再次按触发键或使用“停止本次播放”即可退出整个循环。

### 5. 播放控制

每个宏支持三种模式：

- 播放 N 次：触发后播放指定次数，默认 1。
- 按下后循环：按一次开始循环，再按一次停止。
- 按住后循环：按住触发键时循环，松开停止。

触发键支持单键、组合键、Ctrl、Alt、Shift、Win、F1–F24、完整数字小键盘（`Numpad0`–`Numpad9`、`NumpadDivide/Multiply/Minus/Plus/Decimal/Enter`）、主键盘符号键（`Minus/Equal/LeftBracket/RightBracket/Backslash/Semicolon/Quote/Grave/Comma/Period/Slash`）以及标准鼠标 1–5 键（左、右、中、X1、X2）。数字小键盘在 Num Lock 开启或关闭时都按物理小键盘键识别。进程筛选可指定前台窗口进程名，例如 `YuanShen.exe`；只有当前前台进程匹配时该宏才会触发。标题栏的“暂停监听”会停止全部触发键监听，但不会清除已保存的热键。捕获触发键或步骤按键时会自动暂停监听；点击“恢复监听”后，会恢复到暂停前的监听集合。

Windows 标准鼠标消息只定义到 Mouse1–Mouse5。游戏鼠标的第 6 个及以后按钮通常由厂商驱动映射成键盘键；建议在鼠标软件中映射为 F13–F24，再直接捕获为宏触发键。

精度模式支持基础（0.5ms 目标）、高性能（0.25ms 目标）和极限（0.1ms 目标）。极限会加载 `MacroHid.NativePlayback.dll`，把基础序列热路径放到 x64 C++ in-process 播放循环中执行；密集 1ms/2ms 循环的 auto 模式优先 inline native path，显式 standby 模式保留预热低启动路径。条件动作在高性能/极限档会尝试 native-inline；“暂停主时间线”通过独立 native 控制句柄冻结主计划并平移剩余时间戳，不会恢复后集中补发。DLL 不可用或当前宏包含递归/控制流时会自动使用可取消 managed 路径。目标值是 LatencyProbe 的统计阈值和优化方向，Windows 用户态仍可能出现抢占长尾。

### 6. MCRX JSON

MCRX JSON 面板显示当前宏的原始 JSON。修改 JSON 后，合法内容会同步刷新基础序列和条件序列；非法 JSON 会保留当前编辑状态并显示错误。

### 7. 导入和导出

宏数据库中提供导入/导出：

- 导入：`.mcrx`、MacroConverter XML、Razer Synapse XML、Lua/Logitech Lua、XMouse、QMacro、GIMacros JSON；文件选择器支持多选批量导入。对 `.mcrx` 主宏+子宏会一起导入并重映射 `macro.call` 引用。对于雷云 XML，可同时选择主宏和子宏；程序也会搜索同目录文件，自动识别 GUID 引用、先导入子宏，再在主宏中保留嵌套“调用宏”动作。
- 导出：点击导出打开向导选择格式；默认推荐 `MacroHID MCRX`。可多选宏后导出到同一目录（夹外依赖会写入「依赖子宏」）。导出宏文件夹时可选择两个子文件夹或 ZIP，自动打包文件夹内宏与依赖子宏；分享请带上「宏文件夹 + 依赖子宏」。

导入失败时会弹出错误详情，包含文件名、解析失败的行号/列号、具体原因和对应原文；批量导入会继续处理其他文件并汇总所有失败项。

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
- Use `Ctrl`/`Shift` extended selection and `Ctrl+A`, then batch-copy, delete, lock/unlock, or drag selected macros.
- Switch among Details, List, Small icons, and Large icons, and sort by custom order, name, or modified date.
- Double-click a folder to enter it; use Up or `Backspace` to return. The blank-area context menu provides New, Paste, View, Sort, and Refresh.
- Imported macros are added to the library.
- Each macro can store its trigger, playback mode, run count, and foreground process filter.
- Use **Lock editing** in the sequence header to persistently protect a macro. Locked macros can still run, listen, and export, while steps, conditions, JSON, names, playback settings, and deletion remain protected until unlocked.

Library shortcuts: `F2` rename, `Delete` delete, `Ctrl+A` select all, `Ctrl+C` copy, `Ctrl+V` paste, `Ctrl+D` duplicate macro, `Ctrl+N` new macro, `Ctrl+Shift+N` new folder, and `F5` refresh. Sequence and condition lists support `Delete` and `Ctrl+A/C/X/V`; sequences also support `Ctrl+Z/Y`.

### 3. Base Sequence

The base sequence runs immediately after playback starts. Add steps by dragging or clicking action templates:

You can also click **Record input** in the sequence header. Choose a recording mode first: **Replay timing** (real gaps), **Fixed 5 ms delay** (5ms between inputs), or **No delay**. MacroStudio minimizes while it captures keyboard down/up, left/right/middle/X1/X2 mouse down/up, and vertical/horizontal wheels. Mouse movement is not recorded. Press `Ctrl+Shift+F12` to stop; the stop chord is excluded, the previous window state is restored, and the captured steps are inserted at the current selection. Global macro trigger listening is suspended during recording and restored afterward. Locked macros cannot be recorded.

- Delay: fixed milliseconds or random range.
- Keyboard: down, up, text input, modifiers.
- Mouse button: left, right, middle, X1, X2, down/up, optional coordinate.
- Mouse move: relative or absolute coordinates.
- Wheel: vertical and horizontal wheel.
- Bring window to front: choose a running window and match by process plus optional title/title regex; restores minimized windows and waits for foreground confirmation.
- OCR extract text: select a message region, extract a literal/regex match or capture group from mixed recognized text, and write it to the clipboard. UIDs may contain letters, hyphens, underscores, or any characters allowed by the rule. A no-clipboard test is available.
- OCR text click: select a screen region and locate a text-box center using literal or regex matching; supports the Nth match, single/double/triple click, offsets, and a no-click test.
- Text: Unicode text input.
- Macro: call another macro from the library.
- Loop: contains nested steps.
- Stop current macro layer: return from the current macro call and continue its caller.
- Stop current iteration: discard the remaining steps in this iteration; toggle-loop and hold-loop naturally start the next iteration.
- Stop this playback: cancel the trigger-started playback and its outer loop, like pressing the trigger again.

A macro may call itself. A tail self-call stays cancellable without continuously growing the managed call stack.

The OCR extract-text editor includes ready-to-use rules for copying all text, continuous digits (`\d+`), filtering literal terms before keeping digits only, continuous letters/digits/hyphens/underscores, content after `UID`, and content after a colon or equals sign. For example, filtering `-6` and `4=3` from `-6  4=3  177933444`, then removing non-digits, produces `177933444`.

The sequence supports multi-select, rectangle select, drag sorting, drop into loops, copy, cut, paste, delete, move up, and move down. Click a step card to edit its properties visually.

### 4. Condition Sequence

Each macro can also have condition directives. After playback starts:

- The base sequence runs directly.
- The condition sequence monitors its conditions.
- When a condition is met, its then-actions are executed.

A condition can include:

- Name.
- Condition type, such as pixel color or OCR text.
- Step range, displayed by concrete step labels.
- Trigger time window, measured from the macro trigger key press.
- Screen region or pixel coordinate.
- Target color and tolerance.
- OCR expected text with contains matching or timeout-protected regular expressions.
- Then-actions sequence.
- Execution mode: run then-actions in parallel, or pause the main timeline and resume after they finish.

Condition then-actions use the same interaction engine as the base sequence: multi-select, rectangle select, drag, copy, cut, paste, drop into loops, row copy/delete buttons, and property editing. After selecting a condition, click **Record input** beside the then-actions heading; the captured keyboard, mouse, and delay steps are inserted only into that condition, not the base sequence.

For cross-window OCR, use: bring QQ to front → OCR extract text (regex-capture the UID to the clipboard) → bring Genshin to front → OCR-click Paste/Search. No message double-click or `Ctrl+C` is required. Foreground activation is confirmed before the next action and does not depend on `Alt+Tab` order.

Condition runtime boundaries:

- A condition triggers at most once per playback iteration. The next fixed-count or loop iteration creates a new monitor and evaluates it again.
- Then-actions may call other macros. Stop-current-layer inside a called macro returns only from that nested call, and later then-actions continue.
- Stop-current-iteration ends Macro 1's current base iteration but keeps the outer loop alive; the next iteration creates a fresh condition monitor.
- Stop-this-playback, whether direct or inside a called macro, cancels the base sequence and the entire outer loop.
- Calling the current macro from its own then-actions executes its base steps but does not recursively create its condition monitors. For “visual match → finish then-actions → stop Macro 1's current iteration → evaluate again next iteration,” use toggle-loop or hold-loop, select Pause Main Timeline, and put Stop Current Iteration at the end of the then-actions. Press the trigger again or use Stop This Playback to exit the loop.

### 5. Playback Control

Each macro supports three modes:

- Fixed count: play N times, default 1.
- Toggle loop: press once to start looping, press again to stop.
- Hold loop: loop while the trigger is held, stop when released.

Triggers support single keys, chords, Ctrl, Alt, Shift, Win, F1–F24, the full numpad (`Numpad0`–`Numpad9`, divide, multiply, minus, plus, decimal, and enter), main-keyboard OEM symbol keys, and standard mouse buttons 1–5 (left, right, middle, X1, and X2). Physical numpad keys are distinguished even when Num Lock is off. The process filter can restrict playback to a foreground process such as `YuanShen.exe`; the macro triggers only when the current foreground process matches. The title-bar **Pause listening** control stops all trigger listening without clearing saved hotkeys. Capturing a trigger or step key auto-pauses listening; **Resume** restores the previous listening set.

Standard Windows mouse messages expose Mouse1–Mouse5 only. Mouse buttons 6 and above are normally remapped by vendor software; map them to F13–F24 there and capture that key as the trigger.

Precision mode supports Basic (0.5ms target), High Performance (0.25ms target), and Extreme (0.1ms target). Extreme loads `MacroHid.NativePlayback.dll`, moving the base-sequence hot path into an x64 C++ in-process playback loop; dense 1ms/2ms loops prefer the inline native path in auto mode, while explicit standby mode keeps the warmed low-startup path. Condition then-actions attempt native-inline in High Performance and Extreme. Pause mode freezes the native main plan through a per-run control handle and shifts all remaining deadlines, so resume does not burst missed actions. Recursive/control-flow macros use the cancellable managed path when required. Targets are LatencyProbe thresholds and optimization goals; Windows user mode can still produce preemption tails.

### 6. MCRX JSON

The MCRX JSON panel shows the current macro document. Valid edits update the base and condition sequences live; invalid JSON keeps the current editor state and shows an error.

### 7. Import and Export

Import/export lives in the macro library:

- Import: `.mcrx`, MacroConverter XML, Razer Synapse XML, Lua/Logitech Lua, XMouse, QMacro, and GIMacros JSON. The file picker supports multi-select batch import. For `.mcrx` main/submacro sets, MacroStudio imports them together and remaps `macro.call` references. For Razer XML, select the main macro and submacros together—or keep them in the same folder—and MacroStudio automatically resolves GUID references, imports submacros first, and preserves nested macro-call actions in the main macro.
- Export: open the export wizard to choose a format; prefer `MacroHID MCRX`. Multi-select macros to export them into the same folder (out-of-folder dependencies land under Dependencies). Folder export can write two subfolders or a ZIP and automatically packs macros plus dependencies—share the folder together with its Dependencies package.

An import failure opens detailed diagnostics with the file name, failed line/column, reason, and source line. Batch import continues with the remaining files and summarizes every failure.

### 8. Precision Probe

The local-run package includes LatencyProbe for managed/native comparison:

```powershell
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend managed --profile extreme --loop-steps 10 --loop-interval-us 1000 --iterations 5000
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile extreme --loop-steps 10 --loop-interval-us 1000 --iterations 5000 --trace-outliers artifacts\native-extreme-outliers.csv
.\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile extreme --target-us 100 --warm-native --cpu-scan --loop-steps 10 --loop-interval-us 1000 --iterations 5000
```
