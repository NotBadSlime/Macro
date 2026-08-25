# 步骤属性、录制模式与暂停监听

日期：2026-08-26

## Summary

改编辑器属性卡和录制入口，让按下/抬起与延迟分离；录制不再夹带鼠标移动，并提供三种间隔策略。顶栏增加常驻「暂停监听」，捕获按键时自动屏蔽已绑定宏，避免 X1/X2 等通用触发键在设置时误触。

## Goals

- 非延续性动作的属性卡去掉「时间」字段；鼠标移动保留时长用于平滑
- 拖入按下+抬起模板时，中间自动插入 5ms 延迟步骤
- 键盘修饰键改为捕获成独立按键，不再用勾选
- Down/Up 本地化为按下/抬起
- 录制前选择三种模式：复刻、延迟、无延迟；三种都不录入鼠标移动
- 顶栏常驻暂停/恢复监听；捕获播放触发键和动作键位时自动暂停监听
- 不删除任何宏已保存的触发键

## Non-Goals

- 不从 `.mcrx` / 数据模型中删除 `KeyStep.Hold`、`MouseButtonStep.Hold`、`KeyStep.Modifiers`
- 不在三种用户录制模式里提供「顺便录鼠标移动」开关
- 不把暂停监听状态写入设置文件（重启后不保持暂停）
- 不改播放精度、进程过滤、触发键手势格式
- 不把「延迟」动作本身的固定/随机编辑从属性卡拿掉

## Shared constants

| 名称 | 值 | 用途 |
|------|----|------|
| 按下/抬起默认间隔 | 5ms | 动作面板拖入键盘/鼠标按键时，中间插入的 `WaitStep`；延迟录入时动作之间的固定间隔 |
| 播放器最短按下保护 | 现有 `InputPressTiming.ZeroDelayHold`（10ms） | 仅播放编译使用；属性卡不再展示 |

模板生成的按下/抬起步骤 `Hold` 为 `TimeSpan.Zero`。间隔只体现在中间的 `WaitStep` 上。

---

## 1. 属性卡片

### 时间字段

`StepEditorPanel` 的 `TimingEditPanel`（「时间（毫秒）」）仅在下列步骤显示：

- `MouseMoveStep`（平滑移动时长）
- 将来若有同类延续性动作，同样保留

不再显示该字段：

- `KeyStep`
- `MouseButtonStep`
- `ConsumerStep`

`WaitStep` 继续使用独立的延迟编辑区（固定/随机），不是 `TimingEditPanel`。

点「应用」时：隐藏了时间字段的步骤把 `Hold` 写成 `TimeSpan.Zero`。鼠标移动仍读写 `Duration`。

### 键盘修饰键

去掉 Ctrl / Shift / Alt / Win 四个勾选。

「捕获」接受修饰键，写成对应 `HidKey`（如 `LeftControl`），`Modifiers` 为 `None`。

点「应用」且用户未重新捕获键位时：保留该步骤原有 `Modifiers`，避免旧宏里的组合修饰键被无意清掉。播放路径继续识别 `Modifiers`。

### Down / Up 本地化

`ActionKindBox` 的枚举标签走资源：

| 枚举 | zh-CN | en | zh-TW |
|------|-------|----|-------|
| Down | 按下 | Down | 按下 |
| Up | 抬起 | Up | 抬起 |
| Tap（若仍出现在键盘枚举中） | 点按 | Tap | 點按 |

鼠标按键动作同样本地化。序列列表已有「按下/抬起」文案，保持不变。

### 相关文件

- `src/ui/MacroStudio/Controls/StepEditorPanel.xaml`
- `src/ui/MacroStudio/Controls/StepEditorPanel.xaml.cs`
- `src/ui/MacroStudio/Resources/Strings.resx`
- `src/ui/MacroStudio/Resources/Strings.zh-CN.resx`
- `src/ui/MacroStudio/Resources/Strings.zh-TW.resx`

---

## 2. 拖入按下 + 抬起

`MacroActionTemplateFactory.CreateSteps` 对键盘和鼠标按键生成三步：

1. Down，`Hold = 0`
2. `WaitStep(5ms)`
3. Up，`Hold = 0`

同一工厂供以下入口使用，行为一致：

- 主序列动作面板拖入 / 点击
- 条件「然后」动作
- `ThenActionsEditorWindow`

`Pixel` 模板内嵌的键盘按下/抬起同样插入 5ms 延迟。

其他模板（延迟、移动、滚轮、窗口、OCR、文本、循环、停止）不因此插入额外延迟。

---

## 3. 录制模式

### 入口

点击「录制输入」不立即开始。按钮下方弹出三项菜单：

1. 复刻录入
2. 延迟录入
3. 无延迟录入

选中一项后立即按该模式开始录制。点菜单空白处关闭则不开始。正在录制时按钮仍为「停止录制」，行为不变，停止热键仍为 Ctrl+Shift+F12。

条件序列的「录制触发动作」使用同一菜单和同一套选项。

### 三种模式都不录入鼠标移动

键盘、鼠标按键、滚轮照常记录。`RecordMouseMove` 在这三种模式里为 `false`。动作面板仍可手动添加鼠标移动步骤。

### 间隔策略

在 `MacroRecordingOptions` 增加模式，由 `MacroRecordingSession.AddInput` 执行：

| 模式 | 行为 |
|------|------|
| Replica（复刻） | 与现在相同：按真实间隔插入 `WaitStep`（低于现有 `MinimumDelay` 的间隙丢弃） |
| FixedDelay（延迟） | 忽略真实停顿；每两个被接受的输入之间插入恰好 5ms 的 `WaitStep` |
| NoDelay（无延迟） | 不插入任何 `WaitStep` |

默认选项改为 `RecordMouseMove = false`。专门测试移动采样的用例必须显式传入 `RecordMouseMove: true`。

`MacroInputRecorder` 继续把鼠标移动交给 session；session 在 `RecordMouseMove == false` 时直接忽略。不必为三种模式拆第二套 hook。

开始录制仍沿用现有流程：停止播放、卸掉触发监听 hook、最小化窗口。三种模式只影响最终写入序列的步骤，不改变停止热键和录制中的窗口行为。

### 相关文件

- `src/shared/MacroHid.Core/MacroRecordingSession.cs`
- `src/ui/MacroStudio/MacroInputRecorder.cs`
- `src/ui/MacroStudio/MainWindow.xaml.cs`（`StartMacroRecording` 传入对应 options）
- `src/ui/MacroStudio/Controls/SequencePanel.xaml` / `.xaml.cs`
- `src/ui/MacroStudio/Controls/ConditionDirectivePanel.xaml.cs`

---

## 4. 暂停监听

目的：设置过程中不要让已经绑定的宏被 X1/X2 等键触发。触发键配置保留在宏文档和库里，只停止监听。

### 顶栏按钮

放在 `MainWindow` 标题栏状态区左侧（语言切换左侧），任何停靠/浮动布局下都可见。

| 状态 | 按钮文案 | 外观 |
|------|----------|------|
| 未暂停 | 暂停监听 | 普通命令栏按钮 |
| 已暂停 | 恢复监听 | 使用危险/醒目标色，表示触发键当前无效 |

点击「暂停监听」：

1. 记下当前所有正在监听的宏 ID
2. 停止正在播放的宏
3. 停止全部监听控制器并刷新库/播放面板状态
4. 置 `listeningPaused = true`

点击「恢复监听」：

1. 置 `listeningPaused = false`
2. 按记下的 ID 重新开始监听（宏已从库删除则跳过）
3. 若暂停前没有在听的宏，恢复后保持未监听

暂停只对当前进程会话有效，不写入 `%APPDATA%` 设置。

### 暂停期间的其它入口

- 触发键不能启动任何宏
- 点播放面板或宏库的「开始监听」：不启动，状态栏提示先恢复监听
- 「立即运行」仍可手动播放
- 宏库「停止全部监听」仍可用。若当前已暂停：保持暂停（触发键仍不会生效），并清空待恢复名单，因此点「恢复监听」后不会自动重新听那些宏

### 捕获时自动屏蔽

以下操作开始时，若尚未暂停，则执行与「暂停监听」相同的停止逻辑，并标记为自动暂停：

- 播放面板捕获触发键（含键盘和鼠标侧键）
- 步骤属性卡捕获动作键位

捕获结束（提交、取消、只读打断）后：

- 若暂停是用户点顶栏造成的：保持暂停
- 若暂停是这次捕获自动加上的：按记下的名单恢复监听

嵌套规则：用户已暂停时再捕获，捕获结束仍保持用户暂停。

### 所有权

`MainWindow` 持有 `listeningPaused`、自动暂停标记、待恢复宏 ID。`PlaybackPanel` 与 `StepEditorPanel` 发出捕获开始/结束事件，不自己停监听。

### 相关文件

- `src/ui/MacroStudio/MainWindow.xaml`
- `src/ui/MacroStudio/MainWindow.xaml.cs`
- `src/ui/MacroStudio/Controls/PlaybackPanel.xaml.cs`
- `src/ui/MacroStudio/Controls/StepEditorPanel.xaml.cs`

---

## 5. 数据流

```text
动作面板拖入 Keyboard/MouseButton
  -> MacroActionTemplateFactory.CreateSteps
  -> [Down, Wait(5ms), Up]
  -> 插入主序列或条件然后动作

点击录制 -> 模式菜单
  -> MacroRecordingOptions(模式, RecordMouseMove=false)
  -> MacroInputRecorder / MacroRecordingSession
  -> 插入步骤

顶栏暂停 / 捕获开始
  -> MainWindow 停止监听控制器
  -> 触发键仍存在于 PlaybackSettings
  -> 恢复或捕获结束 -> 按名单 Restart listening
```

## 6. 错误处理

- 录制菜单未选模式：不开始录制，无错误提示
- 录制启动失败：沿用现有 `RecordingFailed` 状态文案
- 恢复监听时部分宏找不到或触发键冲突：跳过问题项，能恢复的继续恢复，状态栏报告恢复数量
- 捕获过程中窗口进入只读：结束捕获；若本次为自动暂停则恢复监听

## 7. 测试

Core 测试（`tests/MacroHid.Core.Tests/Program.cs`）：

- 键盘/鼠标按键模板为 Down + 5ms Wait + Up，Hold 为 0
- Pixel 模板内嵌键盘对包含 5ms Wait
- 默认 `MacroRecordingOptions` 忽略 `RecordMouseMove`
- Replica：保留真实间隔，不写入移动步骤
- FixedDelay：无论真实间隔多长，输入之间都是 5ms Wait
- NoDelay：结果中没有 `WaitStep`
- 显式 `RecordMouseMove: true` 时，现有移动采样测试仍然成立

UI 源码约定测试（与现有字符串断言风格一致）：

- 属性卡填充枚举时包含按下/抬起资源键，不再绑定修饰键勾选
- 录制按钮先弹出模式菜单
- 顶栏存在暂停监听按钮；捕获开始/结束会通知 `MainWindow`

## 8. 本地化键（新增）

- `ActionKindDown` / `ActionKindUp` / `ActionKindTap`
- `RecordModeReplica` / `RecordModeFixedDelay` / `RecordModeNoDelay`
- `PauseListening` / `ResumeListening`
- `ListeningPausedHint`（暂停中点开始监听时的提示）
- `ListeningPausedStatus`（顶栏/状态区：已暂停监听）
