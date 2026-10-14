# FpsHotkeyExt 2.3

独立的 x64 游戏帧率快捷键扩展，支持通过 KY3 的额外 DLL 列表注入。
无需主插件导出 SetFps。实际 KY3 插件只导出 StopPlugin。

## 配置与恢复快捷键

编辑 DLL 同目录的 `fps_hotkeys.json`。已部署的配置位于 `E:\ky3 data\fps_hotkeys.json`。

```json
{
  "restoreHotkey": { "virtualKey": 120, "enabled": true },
  "rules": [
    { "id": 1, "virtualKey": 112, "targetFps": 60, "enabled": true },
    { "id": 2, "virtualKey": 113, "targetFps": 90, "enabled": true },
    { "id": 3, "virtualKey": 114, "targetFps": 120, "enabled": true }
  ]
}
```

- `virtualKey`：按键码，F1～F12 为 112～123；示例中 120 是 F9。
- `targetFps`：目标帧率，0 表示 999；`enabled=false` 禁用对应规则。
- `restoreHotkey`：默认 F9，恢复本次游戏启动时的帧率，并停止扩展持续覆写，将帧率控制交回游戏和启动器。
- 恢复目标优先读取已注入 KY3 插件同目录 `dll_config.ini` 中启用的启动帧率，否则取切换前读到的帧率。无法确定时记录失败，不猜测目标。
- 恢复后仍可再次按切换键。恢复键若与帧率规则冲突，恢复优先；建议使用不同键。
- 修改配置后完全退出游戏，再由 KY3 启动。旧配置省略 `restoreHotkey` 时也默认启用 F9。

## 本次修复

- 完整解析特征码中的空格、`?` 和 `??`，拒绝空签名、重复命中和不可执行地址。
- 扫描主模块所有可读的可执行节，不再只扫描 `.text`；游戏未解包或未就绪时每 5 秒重试。
- 使用 KY3 历史日志的 SetFrameCount/GetFrameCount 特征码；不使用未经核实的 ForceSync 调用。
- 通过 MinHook 拦截 SetFrameCount，将后续 setter 调用的参数统一替换成快捷键目标；同时每 500ms 补写。
- 正确读取 `"enabled": true` 前的空白；按下边沿触发，只有游戏在前台时响应。
- `targetFps=0` 转为 999；负值拒绝，超过 1000 的目标限制为 1000。
- 初始化移到工作线程，避免在 DllMain 的加载器锁内扫描、安装 hook 或等待线程。

## 部署与验证

1. 在 KY3 的额外 DLL 列表中启用 `FpsHotkeyExt.dll`，确认注入到游戏进程。
2. 将 `fps_hotkeys.json` 放在扩展 DLL 同目录。运行时不再依赖启动器的当前工作目录。
3. 完全退出游戏，再由 KY3 重新启动。DLL 安装 hook 后会固定在进程中，重新注入不会更新代码或重载配置。
4. 等待游戏进入可操作界面，测试 F1=60、F2=90、F3=120、F4=144、F5=165、F6=240、F7=999、F8=30（随附配置）。
5. 查看游戏目录 `fps_hotkey_ext.log` 的最新 v2.3 段落。成功时包含 `Ready: setter=...` 和 `VK=... (res=0)`。

源码中的无配置备用规则为 F1=60、F2=120、F3=999、F4=30。
若一直提示 `Setter not ready / signature not unique`，说明当前运行时的特征码或内存节布局仍需核对，扩展不会调用猜测地址。

KY3 的内置帧率功能、垂直同步以及其他插件可能采用不同写入路径。setter hook 能处理通过该函数的覆写；不能保证拦截直接写内存的第三方代码。若成功切换后仍回到启动器的帧率，请在 KY3 界面关闭内置帧率功能进行对照测试，并提供新日志。不要只改 dll_config.ini，启动器会在启动时重新生成设置。

## 编译和测试

需要 CMake、Visual Studio 2022 C++ x64 工具及 Windows SDK。
从本目录运行 `python build_clean.py` 或双击 `build.bat`。
脚本检查编译和测试返回码，成功后更新本目录 DLL；不会自动修改部署目录。

等价命令：

```powershell
cmake -S . -B build-v2.1 -G "Visual Studio 17 2022" -A x64
cmake --build build-v2.1 --config Release
ctest --test-dir build-v2.1 -C Release --output-on-failure
```

测试使用独立的模拟 x64 setter 验证真实 hook、启动器覆写、不限帧和配置解析，不会打开或注入游戏进程。通过测试不等于已在当前游戏版本实测成功。

MinHook 源码使用项目内的 `../UnlockerLite-src/src/MinHook`，许可证见该目录的 LICENSE.txt。旧 BUILD_GUIDE/FINAL_GUIDE 等文档中的单文件 cl 命令已过时，请以本文为准。

## 导出接口

```cpp
bool AddHotkeyRule(int vk, int fps);
bool RemoveHotkeyRule(int id);
int GetActiveFps();
int ForceSetFps(int fps);
int RestoreFps();
```

GetActiveFps 返回当前选定的目标帧率，不是实际渲染帧率计数。未初始化时返回 -1；ForceSetFps 成功返回 0，未初始化返回 -1，负目标返回 -2。

RestoreFps 成功返回 0；未初始化返回 -1；无法确定恢复目标返回 -3。

## Macro 集成

v2.3 增加按游戏 PID 命名的 Local\\FpsHotkeyExt.<pid> 共享内存。MacroStudio 的 KY3 开启/恢复卡片通过确认应答协议控制目标；YSR 协议保持独立。升级 DLL 后必须重启游戏。
