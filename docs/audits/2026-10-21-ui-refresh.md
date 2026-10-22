# SteamSwitch 全界面美化记录

本轮沿用 Steam 蓝与现有 WPF 架构，统一浅色、深色主题、文字层级、卡片、按钮、输入框和菜单，覆盖主窗口及全部 9 个自有视图文件。保留本轮开始前的工作区修改，没有提交 Git、发布远程版本或自动覆盖安装目录。

## 界面检查与修改

1. **主窗口与导航。** 清理大面积装饰渐变、重复标题和过重边框，统一页面留白、导航分组、工具栏及状态栏。
2. **账号管理。** 提高列表密度，统一搜索与筛选布局；账号状态使用随主题变化的颜色，保持昵称、登录名及归档状态可读；检查列表、网格和空状态。
3. **游戏与快速启动。** 统一列表图标、标题、路径、绑定状态和固定操作的层级；降低删除操作的视觉权重，保留所有原有绑定和事件。
4. **存档中心。** 将当前存档备份、版本恢复和全部备份固定在详情区底部，策略与更多操作独立滚动；改善小字号、列表密度和最小窗口下的布局。
5. **设置、任务栏与歌词。** 合并重复页标题，统一分组、设置行、选项和说明；提供清晰的选中、禁用与焦点视觉。
6. **全部自有弹窗与紧凑组件。** 统一游戏绑定、启动工作流、自定义存档目录、更新和旧设置弹窗；任务栏、桌面浮窗、媒体与音量面板使用深色紧凑配色；托盘及共享右键菜单跟随统一主题。

共享资源集中在 [ThemeManager.cs](G:/SteamSwitch-main/src/SteamSwitcher/Services/ThemeManager.cs) 和 [Styles.xaml](G:/SteamSwitch-main/src/SteamSwitcher/Resources/Styles.xaml)。保留现有资源键与交互入口，避免外观修改引入业务迁移。

## 渲染与检查证据

使用程序真实 WPF XAML、模板及绑定进行离屏渲染。预览使用独立临时配置、假 Steam 路径及固定示例数据；不创建真实托盘图标、不显示原生窗口、不访问网络、不启动 Steam 或修改真实账号与存档。

- [本轮修改前的主页面](G:/SteamSwitch-main/artifacts/ui-refresh-20261020/before)
- [浅深色与窗口尺寸矩阵](G:/SteamSwitch-main/artifacts/ui-refresh-20261020/after/matrix.json)
- [预览图索引](G:/SteamSwitch-main/docs/audits/2026-10-21-ui-preview-gallery.md)

第一轮矩阵覆盖 13 种主页面/状态 × 2 种主题 × 2 种窗口尺寸，以及 18 种弹窗/紧凑组件/控件状态 × 2 种主题，共 88 项。自动渲染成功后仍逐图检查裁切、文字、布局和主动作可达性，并修复发现的问题。

第二轮完整重渲染 **88/88 成功，0 超时**。复验确认账号最小窗口详情可独立滚动、空账号有引导、选中状态正确、自定义存档下拉框高度正常、更新按钮文字完整、音量与歌词进度条正确填充。字体诊断确认使用 Segoe UI Variable Text / Microsoft YaHei UI，没有意外回退到衬线字体。

最终 Release 完整回归 **160/160 通过，0 失败、0 跳过**；UiCapture 构建 **0 警告、0 错误**。隔离 WPF 设置应用、两种关闭方式，以及 CEF 会话清理回归全部通过。CI 已加入两主题的控件状态与对比度检查。

- [完整测试 TRX](G:/SteamSwitch-main/artifacts/ui-refresh-20261020/tests/ui-refresh.trx)
- [设置应用检查](G:/SteamSwitch-main/artifacts/ui-refresh-20261020/tests/settings-apply-smoke.txt)
- [完全退出检查](G:/SteamSwitch-main/artifacts/ui-refresh-20261020/tests/exit-lifecycle-smoke.txt)
- [CEF 回归记录](G:/SteamSwitch-main/artifacts/ui-refresh-20261020/tests/cef-session.txt)

三组主要文字配色按实际主题资源计算对比度：

| 配色 | 浅色 | 深色 |
|---|---:|---:|
| 正文 / 卡片 | 15.575 | 14.179 |
| 辅助文字 / 卡片 | 6.040 | 8.576 |
| 主按钮文字 / 主色 | 5.578 | 6.344 |

这三组均达到 4.5；此结果不代表整套产品已经通过完整无障碍验收。焦点图片展示模板装饰，不能证明真实键盘焦点、Tab 顺序或读屏结果。

![浅色账号页面](G:/SteamSwitch-main/artifacts/ui-refresh-20261020/after/accounts-light-1360x900.png)

![深色账号页面](G:/SteamSwitch-main/artifacts/ui-refresh-20261020/after/accounts-dark-1360x900.png)

## 可复跑命令

```powershell
$env:DOTNET_PROCESSOR_COUNT = '2'
conda run --no-capture-output -n hot dotnet build tools/UiCapture/UiCapture.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal
conda run --no-capture-output -n hot dotnet tools/UiCapture/bin/Release/net8.0-windows10.0.19041.0/UiCapture.dll artifacts/ui-review render-all
conda run --no-capture-output -n hot dotnet test SteamSwitch.sln -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal
```

## 安装包

Windows x64 自包含单文件发布及本机 Inno Setup 安装包编译已完成，版本保持 3.2.0。安装包大小 55,346,554 字节，SHA-256 为 `10054D0FB9B40C39272B4F883D39EC071F1FC98DC1AE0030584ED59502C68CFF`。

- [美化版安装包](G:/SteamSwitch-main/artifacts/ui-refresh-20261020/installer/SteamSwitch-v3.2.0-win-x64-setup.exe)
- [自包含主程序](G:/SteamSwitch-main/artifacts/ui-refresh-20261020/publish/SteamSwitch.exe)
- [SHA-256 校验值](G:/SteamSwitch-main/artifacts/ui-refresh-20261020/SHA256SUMS.txt)
- [最终验证摘要](G:/SteamSwitch-main/artifacts/ui-refresh-20261020/verification.json)

没有运行安装器或覆盖当前安装目录。检查完成后没有遗留 SteamSwitch 或 UiCapture 进程。

离屏检查不能替代真实 Explorer 嵌入、Windows 原生文件选择器与消息框、实体多屏和混合 DPI、屏幕阅读器、实际键盘鼠标操作的验收。这些系统界面的视觉由 Windows 管理，本轮没有替换其行为。
