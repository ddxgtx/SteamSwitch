# 歌曲控制栏响应与收起动画（2026-10-22）

本轮修复鼠标移出后控制栏滞留，以及快速进出时的动画拖尾，保留此前主界面提示优化。

原因与调整：

- 原 MouseLeave 只启动 90ms 定时复核，复核后才淡出。现在事件中立即检查物理光标，真正移出立即开始收起；鼠标实际仍在歌词区域内时，忽略提示框或布局造成的假离开。
- 悬停期间启用 16ms、Input 优先级的短周期复核，补救没有收到 MouseLeave 的情况，离开后停止。
- 使用歌词控件两角的 PointToScreen 结果形成屏幕矩形，包含 Viewbox 实际缩放与 DPI 转换，不再手动重复乘 DPI。旧逻辑还会在任务栏内容等比缩小时高估命中宽度，造成离开后仍被视为悬停。
- 进入淡入 150ms、位移 160ms，改用较柔和的 SineEase；退出淡出 65ms，文字/位移恢复 100ms。反向动画按剩余距离缩短持续时间，减少短暂停留后的拖尾。保留一次布局、局部缓存和几何冻结。

坐标语义参考 [.NET WPF PointUtil 实现](https://source.dot.net/PresentationCore/src/wpf/src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/PointUtil.cs.html)：RootToClient 已将布局单位转为像素，再经 ClientToScreen 转为屏幕坐标。

验证：

- conda hot 中 Release 构建 0 警告、0 错误，196/196 测试通过。
- `UiCapture <output> lyrics-hover-exit 900 150 Light|Dark`：隔离且不激活的屏外 WPF 窗口，注入光标读取位置，直接调用生产 MouseEnter/MouseLeave 路径。双主题覆盖自然 46 DIP 和压缩为 30 DIP 的视口、负屏幕坐标、真实离开立即取消命中、假离开保持、丢失事件后的复核收起、短暂停留快速退出。
- 本机 DPI=1；四个视口/主题组合中，事件内立即变更收起状态，采样到透明度低于 0.05 的时间约 41–68ms；缺失离开事件时约 55–77ms。结果受动画时钟和调度采样影响，不是实机 Explorer 帧率保证。
- 双主题 `lyrics-hover-profile` 通过：每次仅一次歌词 Measure/Arrange、6 次中途反向、6 次重复事件及窄区域恢复。
- 双主题 `lyrics-stability` 和最小尺寸 `main-guidance` 均通过。

证据：`.codex-qa/hover-responsive-20261022/`。程序输出：`artifacts/hover-responsive-20261022/Release/net8.0-windows10.0.19041.0/SteamSwitch.exe`。

范围：上述测试验证生产事件路径、真实 WPF 坐标变换和动画状态，使用注入光标，未移动用户的真实鼠标；未做真实 Explorer 内的帧率或高 DPI 多显示器验收。启动新程序前需从托盘完全退出旧实例，避免请求交给仍在运行的旧版窗口。
