# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.4.0] - 2026-06-11

### Added
- 新增静默关闭 Steam 功能，切换账号时隐藏所有 Steam 窗口
- 新增「显示与通知」设置分组：静默关闭、启动时检查更新、关闭 Steam 时通知
- 新增窗口边缘拖拽调整大小支持（WindowChrome + Thumb 手柄）
- 新增注入按钮改为方形 Split Button 设计（「切换启动」+ 下拉箭头）
- 新增启动时自动检查 GitHub 新版本
- 新增 CEF 注入 cleanup 机制，停止注入时清除定时器和 WebSocket

### Changed
- 启动游戏时使用 `steam.exe -silent` 静默启动，不显示 Steam 窗口
- 关闭窗口时不再弹出系统通知
- 头像图片使用 64px 解码宽度，减少内存占用
- 头像下载改为后台异步，不阻塞启动
- GameListBox 启用虚拟化和 Recycling 模式
- 游戏搜索使用 OrdinalIgnoreCase 替代 ToLower()
- AppLogger 缓存路径，每 100 次写入才执行清理
- 任务栏定位定时器间隔从 2 秒改为 5 秒
- 缓存所有 Brush 对象为静态冻结字段，消除 GC 压力

### Fixed
- 修复 Process 对象句柄泄漏（IsSteamRunning、CloseSteamAsync、HideSteamWindows）
- 修复 GDI 区域句柄泄漏（SetWindowRgn 失败时释放）
- 修复 AccountViewModel PropertyChanged 事件处理器泄漏
- 修复 DesktopFloatingWindow/TaskbarBandWindow 事件订阅泄漏
- 修复 JsonElement 生命周期竞态条件（传递前 Clone）
- 修复 ContinueWith 未观察异常可能导致崩溃
- 修复 CEF 注入 JavaScript setInterval 永不停止的问题
- 修复 CEF 注入 WebSocket 重连无限循环（添加最大重试次数）
- 修复 HttpClient 在循环中重复创建的问题
- 修复 MainViewModel 未实现 IDisposable 的资源泄漏
- 修复安装程序版本号未更新的问题

## [2.3.0] - 2026-06-10

### Added
- 新增暗黑/白色双主题切换，所有界面元素跟随主题自动变化
- 新增桌面悬浮窗液态玻璃自选颜色功能（蓝/紫/绿/橙/红/粉 6种预设）
- 新增任务栏和悬浮窗右键菜单（显示主窗口、账号列表、启动Steam、退出）
- 新增桌面悬浮窗不出现在 Alt+Tab/任务管理器中
- 新增任务栏和桌面悬浮窗设置完全独立（窗口大小、图标大小、液态玻璃、圆角）
- 新增任务栏 X轴偏移 / Y轴偏移 独立调节
- 新增任务栏窗口大小与图标大小独立调节
- 新增游戏图标优先使用高清封面图（library_600x900）
- 新增日志系统级别过滤（Debug/Info/Warn/Error）和自动轮转
- 新增输入验证（AppID 范围、游戏名长度）
- 新增注入功能风险提示对话框
- 新增现代化圆角窗口设计（Win32 API 级别圆角）

### Changed
- 任务栏和悬浮窗图标使用 ImageBrush 实现圆角，替代旧的 ClipToBounds 方式
- 滑块控件美化：圆形滑块 + 圆角轨道
- 按钮、复选框、单选按钮样式全面美化
- 滚动条样式适配暗黑/白色主题
- 右键菜单使用主题资源，跟随暗黑/白色切换
- 窗口标题栏使用应用图标替代文字图标
- 主题在 OnStartup 中应用，确保启动时正确加载

### Fixed
- 修复 HttpClient 资源泄漏问题
- 修复空 catch 块静默吞掉异常的问题
- 修复 GameAccountBinding 线程安全问题
- 修复设置频繁写盘问题（添加 500ms 防抖）
- 修复 JsonDocument 未释放问题
- 修复 VdfParser 序列化时未转义引号的问题
- 修复任务栏固定游戏后不立即刷新的问题
- 修复桌面悬浮窗和任务栏头像圆角显示问题
- 修复启动时主题不正确的问题
- 修复白色主题下部分元素不可见的问题

## [2.0.0] - 2026-06-10

### Added
- 新增桌面悬浮窗，可拖动、置顶、锁定位置并保存坐标
- 新增悬浮窗透明度调节和功能开关
- 新增固定游戏启动前确认开关
- 新增启动时自动扫描游戏开关
- 新增游戏图标自动读取与固定游戏快捷启动
- 新增 Steam 库界面注入开关和游戏账号绑定入口
- 新增 README 免责声明，明确 Steam 注入、调试端口和反作弊相关风险

### Changed
- 主界面、游戏界面、设置页、任务栏常驻和悬浮窗改为更现代的液态玻璃视觉
- 任务栏常驻改为右边框锚定模型，新增游戏后向左扩展
- 任务栏常驻增强系统托盘避让和高 DPI 定位
- 设置页重新组织常规、游戏、任务栏、悬浮窗和外观分组
- 安装脚本版本更新为 2.0.0，并改为相对路径

### Fixed
- 修复任务栏常驻新增固定游戏后右对齐失效的问题
- 修复任务栏常驻遮挡 Windows 隐藏图标按钮的问题
- 修复固定游戏图标无法读取时的回退显示问题
- 修复任务栏常驻未响应固定账号变化的问题

## [1.1.0] - 2026-06-09

### Added
- 自动定位到系统托盘图标左侧（默认模式）
- 设置中新增"自动"位置选项
- 每2秒动态调整任务栏位置
- 液态玻璃效果开关
- 圆角模式开关
- 头像大小调节（32-56px）
- 位置偏移调节（±1000px）

### Changed
- 默认任务栏位置模式改为"自动"
- 简化切换逻辑，移除无感切换功能
- 优化UI布局

### Fixed
- 修复自动定位计算逻辑
- 修复设置保存问题

## [1.0.0] - 2026-06-09

### Added
- 初始版本发布
- Steam 账号快速切换功能
- 系统托盘支持
- 任务栏常驻功能
- 设置自动保存
- 自定义窗口边框
- 深色主题右键菜单
- 应用图标
- GitHub 链接和 Powered by ddxgtx
