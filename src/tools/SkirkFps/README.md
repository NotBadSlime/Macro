# YSRExtend / YSR 解帧器扩展

独立于 MacroStudio 的 YSR 小工具。运行 `YSRExtend.exe`，无需安装 .NET 或 Python，图片和运行库内置在单个 EXE 中。沿用原配置位置和单实例标识，与旧版保持兼容。

1. 先通过兼容的 YSR 解帧器正常启动游戏。
2. 默认 F1 → 117 FPS，F2 → 217 FPS，F3 → 384 FPS。
3. 点击快捷键按钮后按下单键或 Ctrl / Alt / Shift / Win 组合键；Esc 取消。修改目标帧率，或点击“添加规则”建立自己的列表（最多 64 条）。新规则默认关闭，配置后勾选启用。
4. 点击“开始监听”。“应用”也可直接设置该行帧率。
5. “恢复原帧率”或可自定义的恢复快捷键（默认 Ctrl + Shift + F12）可还原首次设置前的数值。停止监听、正常关闭也会恢复。

支持快捷键冲突检查。监听使用 Windows 全局快捷键注册，启用的快捷键会被本工具接管，而不是继续发给游戏。修改规则时会暂停监听，需重新开启。

当前显示的是 YSR 的 FPS 上限，不是游戏实时渲染 FPS。与 `fps_toggle.py` 使用同一个共享内存协议：`6F83E7C5-A59D-B27D-3BBE-7D67BA1B4D5B`，偏移 8 的 int32；设置期间每 50ms 保持目标。程序不负责启动或注入解帧器。不要同时运行原切帧脚本或 MacroStudio 的 YSR 卡片，否则会互相覆盖。权限被拒绝时，使用与解帧器相同的权限运行本工具。强制杀进程/断电无法保证恢复。

配置自动保存于 `%APPDATA%\SkirkFps\settings.json`。程序移动后配置仍保留。重新启动默认不监听，避免意外接管按键。

配置读取支持 `// 单行注释`、`/* 多行注释 */` 和末尾逗号。注释不参与加载，字符串内部的 `//` 不受影响。通过界面自动保存时会重新生成 JSON，因此手写注释不会保留。

构建：`powershell -NoProfile -File scripts/Build-SkirkFps.ps1`。

图标及背景使用内置图像生成工具制作，原文件位于 `Assets/skirk-icon.png` 和 `Assets/skirk-background.png`。生成提示词：

- 图标：Skirk / 丝柯克 anime head-and-shoulders portrait, silver-white hair, violet eyes, indigo-purple palette, crisp silhouette readable at 32 pixels, square, no text or watermark.
- 背景：Skirk anime illustration, wide landscape, character on right third, quiet dark-indigo negative space on left for UI, soft violet-blue rim lighting, no words or interface elements.
