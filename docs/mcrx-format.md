# MCRX Format

`.mcrx` files are JSON documents:

```json
{
  "version": 1,
  "name": "baseline",
  "steps": []
}
```

## Supported Steps

### Keyboard

```json
{ "type": "key.tap", "key": "A", "modifiers": ["LeftCtrl"], "holdMs": 5 }
{ "type": "key.down", "key": "Enter" }
{ "type": "key.up", "key": "Enter" }
```

`key` names map to HID Usage Page 0x07, including letters, numbers, function keys, navigation keys, keypad keys, international keys, language keys, and modifiers.

### Mouse

```json
{ "type": "mouse.move", "mode": "relative", "x": 25, "y": -10, "durationMs": 0 }
{ "type": "mouse.click", "button": "X1" }
{ "type": "mouse.down", "button": "Left" }
{ "type": "mouse.up", "button": "Left" }
{ "type": "mouse.wheel", "vertical": -1, "horizontal": 0 }
```

Mouse buttons cover `Left`, `Right`, `Middle`, `X1`, `X2`, `Button6`, `Button7`, and `Button8`.

### Consumer Control

```json
{ "type": "consumer.tap", "control": "VolumeUp" }
{ "type": "consumer.down", "control": "PlayPause" }
{ "type": "consumer.up", "control": "PlayPause" }
```

### Timing and Flow

```json
{ "type": "wait", "ms": 12 }
{
  "type": "repeat",
  "count": 2,
  "steps": [
    { "type": "key.tap", "key": "B" }
  ]
}
```

### Pixel Trigger

```json
{
  "type": "pixel.when",
  "scope": "screen",
  "x": 100,
  "y": 200,
  "r": 10,
  "g": 20,
  "b": 30,
  "tolerance": 4,
  "then": [
    { "type": "key.down", "key": "Enter" }
  ]
}
```

`scope` supports `screen` and `window`. First-phase parsing and scheduling are implemented; the DXGI sampler backend is the next native runtime component.
