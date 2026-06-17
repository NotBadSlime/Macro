# MCRX Format / MCRX 格式

## 中文

`.mcrx` 是 JSON 文档。当前结构包含宏元数据、播放设置、基础序列和可选条件序列。

```json
{
  "version": 1,
  "name": "baseline",
  "playback": {
    "trigger": "Ctrl+Alt+F8",
    "mode": "fixedCount",
    "count": 1,
    "processFilter": "notepad.exe",
    "precision": "extremeDuringPlayback"
  },
  "steps": [],
  "conditions": []
}
```

### playback

- `trigger`：可选。支持单键、组合键、Ctrl、Shift、Alt、Win 和鼠标侧键。
- `mode`：`fixedCount`、`toggleLoop`、`holdLoop`。
- `count`：`fixedCount` 的播放次数，最小为 1。
- `processFilter`：可选。前台进程筛选，支持逗号、分号、竖线或换行分隔。
- `precision`：`extremeDuringPlayback` 或 `balanced`。默认是 `extremeDuringPlayback`。

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
- `template`：模板匹配，使用 `templateData` base64 和 `threshold`。
- `pixelHash`：像素哈希匹配，使用 `referenceHash` base64 和 `similarity`。
- `text`：文字识别匹配，使用 `expectedText`、`contains` 和 `language`。

## English

`.mcrx` files are JSON documents. The current structure contains metadata, playback settings, the base sequence, and optional condition directives.

```json
{
  "version": 1,
  "name": "baseline",
  "playback": {
    "trigger": "Ctrl+Alt+F8",
    "mode": "fixedCount",
    "count": 1,
    "processFilter": "notepad.exe",
    "precision": "extremeDuringPlayback"
  },
  "steps": [],
  "conditions": []
}
```

### playback

- `trigger`: optional. Supports single keys, chords, Ctrl, Shift, Alt, Win, and mouse side buttons.
- `mode`: `fixedCount`, `toggleLoop`, or `holdLoop`.
- `count`: run count for `fixedCount`, minimum 1.
- `processFilter`: optional foreground process filter, separated by commas, semicolons, pipes, or new lines.
- `precision`: `extremeDuringPlayback` or `balanced`. Default is `extremeDuringPlayback`.

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
- `template`: template match with `templateData` base64 and `threshold`.
- `pixelHash`: pixel hash match with `referenceHash` base64 and `similarity`.
- `text`: OCR/text match with `expectedText`, `contains`, and `language`.
