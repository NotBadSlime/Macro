# MCRX Format / MCRX 格式

## 中文

`.mcrx` 是 JSON 文档。当前结构包含宏元数据、播放设置、基础序列和可选条件序列。

```json
{
  "version": 1,
  "id": "a1b2c3d4e5f64789a0b1c2d3e4f50607",
  "name": "baseline",
  "playback": {
    "trigger": "Ctrl+Alt+F8",
    "mode": "fixedCount",
    "count": 1,
    "processFilter": "notepad.exe",
    "precision": "extremeDuringPlayback",
    "affinityMask": "0x1F"
  },
  "steps": [],
  "conditions": []
}
```

`id` 可选，表示宏在库中的稳定标识。导出/导入嵌套“调用宏”时会用它做引用重映射。

### playback

- `trigger`：可选。支持单键、组合键、Ctrl、Shift、Alt、Win、F1–F24、数字小键盘、OEM 符号键和标准鼠标 1–5 键。
- `mode`：`fixedCount`、`toggleLoop`、`holdLoop`。
- `count`：`fixedCount` 的播放次数，最小为 1。
- `processFilter`：可选。前台进程筛选，支持逗号、分号、竖线或换行分隔。
- `precision`：`balanced`、`extremeDuringPlayback` 或 `ultraLowJitter`。UI 中分别显示为基础（0.5ms 目标）、高性能（0.25ms 目标）、极限（0.1ms 目标）。默认是 `extremeDuringPlayback`。
- `affinityMask`：可选。仅建议在 `ultraLowJitter` 极限模式下使用，例如 `0x1F`。它会在播放期间临时限制进程可用 CPU 核心，并配合 native standby 预热，用于避开本机更容易产生调度尖峰的核心组合。可用 `scripts\Tune-LatencyAffinity.ps1` 生成推荐值。

### 基础步骤 steps

键盘：

```json
{ "type": "key.down", "key": "A", "modifiers": ["LeftCtrl"] }
{ "type": "key.up", "key": "A", "modifiers": ["LeftCtrl"] }
{ "type": "key.text", "text": "Hello 世界" }
```

鼠标：

```json
{ "type": "mouse.down", "button": "Left", "mode": "absolute", "x": 100, "y": 200 }
{ "type": "mouse.up", "button": "Left" }
{ "type": "mouse.move", "mode": "relative", "x": 25, "y": -10, "durationMs": 0.5 }
{ "type": "mouse.wheel", "vertical": -1, "horizontal": 0 }
```

OCR 获取文本（识别混合文字，用正则捕获组提取 UID 并写入剪贴板）：

```json
{
  "type": "ocr.extract-text",
  "region": {
    "topLeft": { "x": 100, "y": 100 },
    "topRight": { "x": 900, "y": 100 },
    "bottomRight": { "x": 900, "y": 300 },
    "bottomLeft": { "x": 100, "y": 300 }
  },
  "pattern": "(?i)UID\\s*[:：=]?\\s*([^\\s，。！？,;；]+)",
  "language": "ch",
  "useRegex": true,
  "matchIndex": 1,
  "captureGroup": 1,
  "normalizeWhitespace": true,
  "failIfNotFound": true
}
```

UID 不限于纯数字；示例规则可提取字母、数字、横线、下划线或其他非空白字符。`captureGroup: 1` 只复制第一组括号中的 UID，`0` 复制完整匹配；`pattern` 留空则复制整个 OCR 结果。`normalizeWhitespace` 会在首次匹配失败后，再尝试忽略 OCR 插入的多余空格。编辑器的“测试提取”不会改写剪贴板。

编辑器内置常用规则：全部文字、连续数字 `\d+`、连续字母/数字/横线/下划线 `[A-Za-z0-9_-]+`、UID 后内容、冒号或等号后内容。

“先过滤，再只保留数字”示例：

```json
{
  "type": "ocr.extract-text",
  "region": {
    "topLeft": { "x": 100, "y": 100 },
    "topRight": { "x": 900, "y": 100 },
    "bottomRight": { "x": 900, "y": 300 },
    "bottomLeft": { "x": 100, "y": 300 }
  },
  "pattern": "",
  "filterTerms": "-6\n4=3",
  "keepDigitsOnly": true,
  "language": "ch"
}
```

对 OCR 结果 `-6  4=3  177933444`，程序先按行逐个删除过滤词 `-6`、`4=3`，再移除所有非 `0-9` 字符，剪贴板最终写入 `177933444`。过滤词按普通文字精确匹配，不作为正则表达式执行。

OCR 文字点击（区域内识别文字并点击匹配框中心）：

```json
{
  "type": "ocr.click",
  "region": {
    "topLeft": { "x": 1400, "y": 20 },
    "topRight": { "x": 1900, "y": 20 },
    "bottomRight": { "x": 1900, "y": 220 },
    "bottomLeft": { "x": 1400, "y": 220 }
  },
  "expectedText": "搜索|粘贴",
  "useRegex": true,
  "language": "ch",
  "button": "Left",
  "clickCount": 1,
  "matchIndex": 1,
  "holdMs": 20,
  "intervalMs": 80,
  "offsetX": 0,
  "offsetY": 0
}
```

`matchIndex` 按从上到下、从左到右选择第几个匹配；找不到时跳过点击。建议把 `region` 限定在目标控件附近，避免页面上同名文字导致歧义。

指定窗口到前台：

```json
{
  "type": "window.activate",
  "processName": "YuanShen.exe",
  "windowTitle": "原神|Genshin",
  "useTitleRegex": true,
  "matchIndex": 1,
  "timeoutMs": 3000,
  "restore": true,
  "failIfNotFound": true
}
```

执行时会持续定位并尝试激活目标窗口，确认前台窗口属于目标进程后才继续。`windowTitle` 为空时只按进程匹配；`failIfNotFound` 为 `true` 时，超时会停止本次宏执行。

延迟：

```json
{ "type": "wait", "ms": 10 }
{ "type": "wait", "minMs": 8, "maxMs": 15 }
```

`ms`、`minMs`、`maxMs`、`holdMs`、`durationMs` 支持小数毫秒。随机延迟在每次执行到该步骤时重新采样。

循环和宏调用：

```json
{
  "type": "repeat",
  "count": 3,
  "steps": [
    { "type": "key.down", "key": "B" },
    { "type": "key.up", "key": "B" }
  ]
}
{ "type": "macro.call", "macro": "macro-id-or-name" }
```

媒体键：

```json
{ "type": "consumer.down", "control": "VolumeUp" }
{ "type": "consumer.up", "control": "VolumeUp" }
```

旧版像素步骤仍可解析：

```json
{
  "type": "pixel.when",
  "scope": "screen",
  "x": 100,
  "y": 200,
  "r": 255,
  "g": 0,
  "b": 0,
  "tolerance": 10,
  "then": []
}
```

### 条件序列 conditions

推荐把条件放在顶层 `conditions`。每个条件包含步骤范围、时间窗口、匹配器和触发后动作：

```json
{
  "id": "cond001",
  "name": "Red pixel",
  "startStep": 0,
  "endStep": 3,
  "startPath": "0",
  "endPath": "3",
  "type": "pixel",
  "region": {
    "topLeft": { "x": 100, "y": 200 },
    "topRight": { "x": 101, "y": 200 },
    "bottomRight": { "x": 101, "y": 201 },
    "bottomLeft": { "x": 100, "y": 201 }
  },
  "r": 255,
  "g": 0,
  "b": 0,
  "tolerance": 10,
  "windowStartMs": 3000,
  "windowEndMs": 5000,
  "pollMs": 1,
  "then": [
    { "type": "key.down", "key": "Enter" },
    { "type": "key.up", "key": "Enter" }
  ]
}
```

支持的条件类型：

- `pixel`：颜色匹配。
- `text`：文字识别匹配，使用 `expectedText`、`contains`、`language`；设置 `useRegex: true` 后按正则表达式判断。正则执行带超时保护。

兼容说明：旧文件中的 `template` 和 `pixelHash` 仍可被解析，但 MacroStudio 不再提供新建或编辑入口；打开后建议转换为 `pixel` 或 `text` 条件。

## English

`.mcrx` files are JSON documents. The current structure contains metadata, playback settings, the base sequence, and optional condition directives.

```json
{
  "version": 1,
  "id": "a1b2c3d4e5f64789a0b1c2d3e4f50607",
  "name": "baseline",
  "playback": {
    "trigger": "Ctrl+Alt+F8",
    "mode": "fixedCount",
    "count": 1,
    "processFilter": "notepad.exe",
    "precision": "extremeDuringPlayback",
    "affinityMask": "0x1F"
  },
  "steps": [],
  "conditions": []
}
```

`id` is optional and stores the macro’s library identity. Nested `macro.call` references are remapped from this identity during import.

### playback

- `trigger`: optional. Supports single keys, chords, Ctrl, Shift, Alt, Win, F1–F24, numpad keys, OEM symbol keys, and standard mouse buttons 1–5.
- `mode`: `fixedCount`, `toggleLoop`, or `holdLoop`.
- `count`: run count for `fixedCount`, minimum 1.
- `processFilter`: optional foreground process filter, separated by commas, semicolons, pipes, or new lines.
- `precision`: `balanced`, `extremeDuringPlayback`, or `ultraLowJitter`. The UI labels are Basic (0.5ms target), High Performance (0.25ms target), and Extreme (0.1ms target). The default is `extremeDuringPlayback`.
- `affinityMask`: optional and mainly intended for `ultraLowJitter`, for example `0x1F`. During playback it temporarily restricts the process to the selected CPU cores and works with native standby warm-up to avoid local core groups that produce larger scheduler spikes. Use `scripts\Tune-LatencyAffinity.ps1` to find a recommended value.

### Base steps

Keyboard:

```json
{ "type": "key.down", "key": "A", "modifiers": ["LeftCtrl"] }
{ "type": "key.up", "key": "A", "modifiers": ["LeftCtrl"] }
{ "type": "key.text", "text": "Hello world" }
```

Mouse:

```json
{ "type": "mouse.down", "button": "Left", "mode": "absolute", "x": 100, "y": 200 }
{ "type": "mouse.up", "button": "Left" }
{ "type": "mouse.move", "mode": "relative", "x": 25, "y": -10, "durationMs": 0.5 }
{ "type": "mouse.wheel", "vertical": -1, "horizontal": 0 }
```

OCR text extraction (recognize mixed text, extract a UID through a regex capture group, and write it to the clipboard):

```json
{
  "type": "ocr.extract-text",
  "region": {
    "topLeft": { "x": 100, "y": 100 },
    "topRight": { "x": 900, "y": 100 },
    "bottomRight": { "x": 900, "y": 300 },
    "bottomLeft": { "x": 100, "y": 300 }
  },
  "pattern": "(?i)UID\\s*[:：=]?\\s*([^\\s，。！？,;；]+)",
  "language": "ch",
  "useRegex": true,
  "matchIndex": 1,
  "captureGroup": 1,
  "normalizeWhitespace": true,
  "failIfNotFound": true
}
```

The UID does not have to be numeric. The example accepts letters, digits, hyphens, underscores, and other non-whitespace characters. `captureGroup: 1` copies only the first parenthesized group; use `0` for the full match. A blank `pattern` copies all recognized text. `normalizeWhitespace` retries after removing OCR-inserted whitespace. The editor's extraction test does not modify the clipboard.

The editor includes presets for all text, continuous digits (`\d+`), continuous letters/digits/hyphens/underscores (`[A-Za-z0-9_-]+`), content after `UID`, and content after a colon or equals sign.

Filter-first, digits-only example:

```json
{
  "type": "ocr.extract-text",
  "region": {
    "topLeft": { "x": 100, "y": 100 },
    "topRight": { "x": 900, "y": 100 },
    "bottomRight": { "x": 900, "y": 300 },
    "bottomLeft": { "x": 100, "y": 300 }
  },
  "pattern": "",
  "filterTerms": "-6\n4=3",
  "keepDigitsOnly": true,
  "language": "ch"
}
```

For OCR text `-6  4=3  177933444`, literal filters `-6` and `4=3` run first. All remaining non-`0-9` characters are then removed, producing clipboard text `177933444`. Filter terms are literal text, not regular expressions.

OCR text click (recognize text inside a region and click the matched box center):

```json
{
  "type": "ocr.click",
  "region": {
    "topLeft": { "x": 1400, "y": 20 },
    "topRight": { "x": 1900, "y": 20 },
    "bottomRight": { "x": 1900, "y": 220 },
    "bottomLeft": { "x": 1400, "y": 220 }
  },
  "expectedText": "Search|Paste",
  "useRegex": true,
  "language": "en",
  "button": "Left",
  "clickCount": 1,
  "matchIndex": 1,
  "holdMs": 20,
  "intervalMs": 80,
  "offsetX": 0,
  "offsetY": 0
}
```

`matchIndex` selects the Nth match in top-to-bottom, left-to-right order. No click is sent when no match is found. Keep `region` close to the target control to avoid ambiguity from duplicate labels.

Bring a target window to the foreground:

```json
{
  "type": "window.activate",
  "processName": "YuanShen.exe",
  "windowTitle": "Genshin|原神",
  "useTitleRegex": true,
  "matchIndex": 1,
  "timeoutMs": 3000,
  "restore": true,
  "failIfNotFound": true
}
```

The step repeatedly locates and activates the window, then continues only after the foreground window is confirmed to belong to the target process. Leave `windowTitle` empty to match by process only. With `failIfNotFound: true`, timeout stops the current macro playback.

Waits:

```json
{ "type": "wait", "ms": 10 }
{ "type": "wait", "minMs": 8, "maxMs": 15 }
```

`ms`, `minMs`, `maxMs`, `holdMs`, and `durationMs` support fractional milliseconds. Random waits are resampled every time the step executes.

Loops and macro calls:

```json
{
  "type": "repeat",
  "count": 3,
  "steps": [
    { "type": "key.down", "key": "B" },
    { "type": "key.up", "key": "B" }
  ]
}
{ "type": "macro.call", "macro": "macro-id-or-name" }
```

Consumer controls:

```json
{ "type": "consumer.down", "control": "VolumeUp" }
{ "type": "consumer.up", "control": "VolumeUp" }
```

Legacy pixel steps remain parseable:

```json
{
  "type": "pixel.when",
  "scope": "screen",
  "x": 100,
  "y": 200,
  "r": 255,
  "g": 0,
  "b": 0,
  "tolerance": 10,
  "then": []
}
```

### Condition directives

The recommended condition model is the top-level `conditions` array. Each condition stores a step range, time window, matcher, and then-actions:

```json
{
  "id": "cond001",
  "name": "Red pixel",
  "startStep": 0,
  "endStep": 3,
  "startPath": "0",
  "endPath": "3",
  "type": "pixel",
  "region": {
    "topLeft": { "x": 100, "y": 200 },
    "topRight": { "x": 101, "y": 200 },
    "bottomRight": { "x": 101, "y": 201 },
    "bottomLeft": { "x": 100, "y": 201 }
  },
  "r": 255,
  "g": 0,
  "b": 0,
  "tolerance": 10,
  "windowStartMs": 3000,
  "windowEndMs": 5000,
  "pollMs": 1,
  "then": [
    { "type": "key.down", "key": "Enter" },
    { "type": "key.up", "key": "Enter" }
  ]
}
```

Supported condition types:

- `pixel`: color match.
- `text`: OCR/text match with `expectedText`, `contains`, and `language`; set `useRegex: true` for timeout-protected regular-expression matching.

Compatibility note: legacy `template` and `pixelHash` conditions can still be parsed, but MacroStudio no longer exposes UI for creating or editing them. Convert them to `pixel` or `text` conditions when editing.
