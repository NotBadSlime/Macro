# MacroHID 1.0.0 正式版

MacroHID `1.0.0` 是当前正式版。这个版本聚焦在 MacroStudio 可用性、宏数据库拖拽体验、安装包图标、三档精度模式和极限档 native playback 稳定性。

## 主要内容

- 宏数据库支持自定义顺序，拖动宏文件可以持久化调整位置。
- 宏数据库拖拽期间支持滚轮滚动，并增加插入位置标识：目标宏上方/下方显示插入线，目标文件夹显示高亮。
- 空白区域释放不会移动宏，边缘自动滚动增加节流，避免拖动时宏突然跑到列表底部。
- 宏数据库在夜间模式下使用动态主题文本颜色，宏名称可读性保持一致。
- 监听控制拆分为宏数据库整体监听与当前宏播放监听，避免整体按钮和单个播放控制互相干扰。
- 条件序列、详情编辑器、JSON 面板、右侧工具窗口和序列窗口尺寸交互包含多项 UI 修正。
- 安装包和应用使用正式 AppIcon。
- 新增当前精度水平文档，记录本机 `affinityMask=0xE` 下极限档 5000 轮长测结果。

## 精度结论

构建和核心测试都通过；极限档在本机使用 `0xE` affinity 后，这轮 5000 轮长测单步最大偏差 `45us`，整轮最大偏差 `43us`，没有 `>100us` outlier。

详见：[当前精度水平](current-precision-status.md)。

## 验证

发布前验证结果：

| 项目 | 结果 |
| --- | --- |
| `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release` | `185 test(s) passed` |
| `dotnet build MacroHID.sln --configuration Release` | 成功，`0` 错误，`0` 警告 |
| `scripts\Build-LocalRun.ps1 -Configuration Release` | 成功，已更新本机运行目录 |

## 版本号

- .NET assembly/package version：`1.0.0`
- Installer `AppVersion`：`1.0.0`
- Git tag：`v1.0.0`
