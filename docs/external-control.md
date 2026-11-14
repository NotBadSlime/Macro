# 外部启动与全局命令

`MacroCtl` 通过当前用户、当前登录会话的本地管道控制 Studio。外部启动不需要宏触发键或开启监听，也不会切换当前编辑的宏。执行的是宏库中已保存的内容；默认执行一轮，可用 `--count N` 指定轮数，或用 `--loop` 持续循环，使用 Studio 当前的播放版本、精度及后台运行设置。前台运行仍需满足宏库的进程启用条件，不会主动切换目标窗口。

## 注册与检查

在 Studio 顶部的「窗口 → 全局命令」中点击「注册全局命令」，随后重新打开终端。也可从完整程序目录运行：

```powershell
& ".\MacroCtl\MacroCtl.exe" register --studio "D:\Apps\MacroHID\MacroStudio\MacroStudio.exe"
MacroCtl doctor
```

注册会将启动器放到 `%LOCALAPPDATA%\MacroHID\bin`，把该目录加入当前用户 PATH，并记录 Studio 的实际位置。安装版默认勾选「启用 MacroCtl 全局命令」；便携版按需注册。移动便携目录、切换安装版本后重新注册即可。取消注册或卸载旧版本时，只有关联位置匹配才移除注册，避免影响已切换到的新版本。取消注册保留启动器文件，避免删除正在执行的命令；PATH 和关联信息会移除。

## 命令

```powershell
MacroCtl list
MacroCtl run --id "宏ID" --delay 3s
MacroCtl run --id "宏ID" --delay 3s --count 5 --wait
MacroCtl run --id "宏ID" --loop
MacroCtl run --id "宏ID" --loop --wait
MacroCtl run --library "日常操作" --name "自动填写" --delay 500ms --wait
MacroCtl status
MacroCtl status --run-id "任务ID"
MacroCtl stop --run-id "任务ID"
```

宏右键菜单的「复制外部启动命令」会复制 ID 和示例 `--delay 3s`，可自行修改或去掉延迟。ID 不受宏重命名影响；名称重名时命令会报错，需改用 ID。条件宏不能作为独立序列直接运行。

- 延迟从 Studio 接受请求时开始，支持 `ms`、`s`、`m`，省略单位表示秒，范围为 0～7 天。它不改变宏内部的等待步骤，也不承诺硬实时精度。
- `--count N` 中 N 必须为正整数，表示整份宏总共执行 N 轮；`--loop` 持续执行直到通过 `stop --run-id`、Studio 停止按钮或强制停止键停止。两者不能同时指定，不传时仍执行一轮。延迟只在第一轮前等待一次，不在每轮重复等待。持续循环下 `--wait` 会一直等待，直到停止或执行失败。
- 使用循环参数前，命令会检查 Studio 是否支持该功能。若仍运行旧版 Studio，将明确报错并拒绝启动，需切换到新版；不会忽略循环参数后只执行一轮。
- 接受后立即返回任务 ID，倒计时由 Studio 负责，终端退出不影响已接受的任务。
- `--loop --wait` 会留在控制台等待；按 Ctrl+C 会停止本命令启动的循环任务，等待 Studio 确认任务结束后退出，退出码为 130。延迟倒计时中的循环任务也会被取消；不会停止其他任务。停止过程中再次按 Ctrl+C 不会中断清理。停止请求失败或 10 秒内未能确认结束时，会报错并显示任务 ID 和可重试的 `stop` 命令，不会声称已停止。
- 其他 `--wait` 用法仍按实际结果返回退出码，Ctrl+C 只停止等待；取消任务需用 `stop`。不带 `--wait` 的 `--loop` 接受后会立即返回，需用 `stop` 或 Studio 停止。
- 宏根文档和引用的子宏在接受时取已保存内容的快照。到点录制中、准备中或有其他宏执行时，任务以 `busy` 结束，不排队、不打断其他宏。
- `status` 区分 `waiting`、`starting`、`running`、`stopping`、`completed`、`cancelled`、`busy`、`failed`，等待状态含剩余毫秒。
- 强制停止和退出 Studio 会取消外部任务。任务不跨 Studio 重启恢复；最多保留 64 个活动任务和最近一批结束记录。
- Studio 已在后台或托盘运行时，命令不会弹出主窗口。不运行时默认报错；显式添加 `--launch` 才根据注册路径启动到托盘，必要时正常弹出 Windows 权限提示。只能控制支持此接口的新版本 Studio。

脚本可添加 `--json` 读取结构化结果：

```powershell
MacroCtl run --id "宏ID" --delay 3s --wait --json
```

退出码：0 成功（未指定 `--wait` 时表示接受成功）；2 参数错误；3 Studio 不可用/连接超时；4 宏或任务不存在；5 名称不唯一；6 忙碌；7 执行或停止失败；8 被停止；130 被 Ctrl+C 中断（`--loop --wait` 会先确认任务结束）。

## 传输约定

本地 API 使用版本化的 Windows 命名管道，不监听 TCP/HTTP 端口，只授予当前用户访问权，并拒绝远程管道客户端。客户端通过 `MacroHid.Control` 库发出 `ControlRequest`。每帧为 4 字节小端长度加 UTF-8 JSON，最大 1 MiB；收到完整响应后发送单字节确认 `1`。启动请求必须携带 GUID `requestId`，短期重试同一请求返回同一任务，避免超时重试造成重复执行。普通终端访问管理员 Studio 使用当前用户 ACL 和 medium 完整性标签，不使用会要求双方相同提升级别的 `CurrentUserOnly`。
