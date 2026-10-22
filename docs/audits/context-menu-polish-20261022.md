# 右键菜单统一与操作修复（2026-10-22）

共享菜单原先把按钮堆在一个 MenuItem 容器里，账号越多菜单越长，缺少原生菜单的子菜单和键盘语义；任务栏在外壳之外追加歌词操作，视觉也不一致。

变更：

- 新增 Resources/MenuStyles.xaml，统一 ContextMenu、MenuItem、Separator 的圆角、轻透明表面、描边、阴影、悬停、键盘焦点、禁用态和子菜单箭头。颜色跟随浅深主题。
- SharedContextMenuBuilder 改成真正的 MenuItem 树，顶层保持紧凑，将切换账号、窗口与显示、维护与恢复分组；不设置统一的 MenuItem-only ItemContainerStyle，避免 Separator 样式类型不兼容。
- 菜单和子菜单限制高度，溢出时滚动。账号按当前、固定、名称排列，提供当前/常用/待找回标识、登录名和 Steam ID 提示，长昵称省略。
- 开关使用原生 IsCheckable/IsChecked，显示已开启/未开启；未开启悬浮窗时禁用置顶，无可用媒体时禁用重新获取歌词。退出使用危险操作色。
- 桌面悬浮窗和任务栏菜单以前只在固定账号集合中查找目标，现在优先查完整账号库，修复未固定账号点击无响应。当前标记依据实际 IsCurrent，避免跟随列表选择。
- 任务栏歌词重取并入维护子菜单。主界面更多管理菜单同步图标、分组和危险色；筛选、排序菜单打开时勾选当前选项。

验证：

- conda hot 中 Release 构建成功，0 警告、0 错误；197/197 回归测试通过。中途一次 Roslyn 进程异常后使用关闭分析器方式完成诊断重建，最终标准命令（禁用构建服务器、单进程、UseSharedCompilation=false）再次构建和测试均通过。
- 浅深主题 `context-menu-live` 均通过：实际构建任务栏和桌面菜单，调用原生 MenuItem.OnClick 路径并运行 dispatcher，确认两处未固定账号事件传递、正确当前标记、开关新状态、置顶/歌词禁用状态。
- 额外 100 个历史账号及超长昵称，顶层维持不超过 10 项；子菜单高度受限且能滚动到底部，末尾账号保留找回提示；空账号库不出现空子菜单。
- 菜单实际模板布局包含 Separator 并无类型异常；可操作项保持 Focusable。顶层、账号、窗口菜单完成浅深主题 WPF 渲染并人工查看。
- 两种主题的 account-library-guidance、main-guidance 回归通过。

边界：测试为隔离账号和离屏 WPF 布局/事件路径，没有发送系统键鼠输入，没有执行真实 Steam 登录。未验证真实桌面上跨屏弹出位置、鼠标进入子菜单或系统键盘导航体验。

程序：`G:/SteamSwitch-main/artifacts/context-menu-20261022/Release/net8.0-windows10.0.19041.0/SteamSwitch.exe`

图片：`.codex-qa/context-menu-20261022/`。启动新版前应从托盘完全退出旧实例。
