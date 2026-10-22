**SteamSwitch 全部审查项修复记录，2026-10-20。**

本轮按 [原审查报告](G:/SteamSwitch-main/docs/audits/2026-10-20-project-audit.md) 的 14 项具体问题完成代码修复，并补齐账号遗忘与迁移、应用配置包、保存失败重试和相关职责分离。保留了任务开始前已有的工作区修改，没有提交 Git 或发布远程版本。

| 原编号 | 修复结果 | 主要实现 |
|---|---|---|
| 01 | 已修复启动同步等待造成的 UI 阻塞 | 账号库构造使用同步读取；底层异步文件 I/O 不捕获 UI 上下文。已有小账号库的 UI 上下文回归通过。 |
| 02 | 已修复首次更新下载提交 | 写入、落盘及校验完成后关闭文件流，再提交临时文件；缓存复用、哈希失败和取消均有回归。 |
| 03 | 已修复导入/重置后的旧运行状态与覆盖 | SettingsSession 维护唯一当前快照；统一应用字段、绑定、服务和控件；旧 dirty 不会写回新设置；失败保留重试或回滚。 |
| 04 | 已修复源目录丢失/为空时的恢复入口 | 发现结果合并安全备份索引；userdata 从可信账号/AppID 重建路径，智能识别从已知游戏规则回推。 |
| 05 | 已修复账号库依赖正常 VDF | Steam 或 VDF 不可用时仍加载本地档案，并说明当前来源异常。 |
| 06 | 已修复损坏 VDF 静默接受和写回 | 严格语法与必要账号字段验证；关闭 Steam 前预检；写入前检查账号集合、目标用户名及来源文件变化。 |
| 07 | 已修复库注入旧会话和虚假连接成功 | 新会话清理旧 DOM/连接/监听器/计时器；鉴权 hello 确认成功后才报告连接；同会话刷新数据，断线后持续重新扫描。启动/停止互斥，交互停止采用异步清理。 |
| 08 | 已修复歌词来源和失败缓存 | 缓存包含来源及曲目时长；空结果 30 秒有效期；旧请求可取消，来源变化立即重新查询，右键可手动重试。 |
| 09 | 已修复标题兼容模式的伪同步 | 无 SMTC 时间线时只显示曲目信息，禁用没有实际能力的播放控制。 |
| 10 | 已修复副屏位置恢复与吸边 | 使用所在显示器工作区、DPI 和设备标识，保存 0–1 相对位置；断屏回退到可用显示器。 |
| 11 | 已补自定义旧备份重新关联 | 普通导入仍严格校验身份；迁移单独显示源/目标信息，由用户明确确认，并以 ZIP SHA-256 绑定确认结果。 |
| 12 | 已补差分版本独立导出 | 所选差分版本物化为完整自包含包，保留该版本文件修改及删除；失败不覆盖已有导出。 |
| 13 | 已修复默认导出文件名 | 使用独立合法时间格式，并清洗游戏名非法字符。 |
| 14 | 已修复恢复临时目录交叉清理 | 目录名包含条目哈希与事务 ID，只清理本次事务；保留其他活跃或中断事务的数据。 |

产品完整性方面，账号页新增“账号档案”菜单，支持主动遗忘、恢复自动收录和元数据导入导出。遗忘记录使用墓碑，后续扫描不会重新收录；墓碑、游戏绑定和固定项通过同一配置事务更新，避免部分提交。

设置页分别提供“导出设置”和“导出应用配置包”。应用配置包包括设置、账号元数据、游戏绑定、快速启动，排除 Steam 登录凭据及游戏存档文件。四文件更新和界面应用纳入同一事务；失败回滚，异常中断会在下次正常启动前恢复。导入不会执行快速启动目标，也不会附带启动 Steam 库注入。

设置保存失败会保留 dirty，显示持续提示并自动重试。主窗口与存档中心的下拉框支持非预置间隔和保留 1 份，控件同步不会把旧选项写回。设置、更新、注入和游戏工作流协调代码已分离，设置状态、配置事务、歌词缓存及多屏几何分别有独立服务和可测试边界。

最终验证：

- `hot` 环境 Release 构建及完整测试：**140 通过，0 失败，0 跳过**。原审查基线为 67 项。
- UiCapture Release 构建：**0 警告、0 错误**。
- 真实 WPF 控件的隔离设置应用冒烟通过：验证 live snapshot、绑定刷新、17 分钟非预置间隔及保留 1 份不会被控件回写。
- 生产 CEF 注入脚本在假 DOM 中通过旧令牌替换、鉴权 hello、同会话更新与资源清理回归。
- 账号、设置、存档及配置包入口已离屏渲染并检查。预览工具使用独立启动逻辑和临时配置，不进入正式应用的单实例启动路径。
- Windows x64 自包含单文件发布成功，随后完成本机 Inno Setup 安装包编译。主程序版本与安装包版本均为 3.2.0。
- `git diff --check` 通过；CI 已加入 CEF 和 WPF 两项冒烟检查。

可复跑命令：

```powershell
conda run --no-capture-output -n hot dotnet test SteamSwitch.sln -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal
conda run --no-capture-output -n hot node tests/cef-session-regression.cjs
conda run --no-capture-output -n hot dotnet run --project tools/UiCapture/UiCapture.csproj -c Release -- artifacts/ui-smoke/settings.png settings-apply-smoke
```

构建产物及证据：

- [本地修复版安装包](G:/SteamSwitch-main/artifacts/repair-20261020/installer/SteamSwitch-v3.2.0-win-x64-setup.exe)
- [自包含主程序](G:/SteamSwitch-main/artifacts/repair-20261020/publish/SteamSwitch.exe)
- [SHA-256 校验值](G:/SteamSwitch-main/artifacts/repair-20261020/SHA256SUMS.txt)
- [完整测试 TRX](G:/SteamSwitch-main/artifacts/repair-20261020/tests/full-repair.trx)
- [账号页面渲染](G:/SteamSwitch-main/artifacts/repair-20261020/ui/accounts/accounts.png)
- [应用配置包入口渲染](G:/SteamSwitch-main/artifacts/repair-20261020/ui/configuration/configuration.png)
- [存档中心渲染](G:/SteamSwitch-main/artifacts/repair-20261020/ui/saves/saves.png)

验证边界：没有替换用户正在运行的程序，没有执行安装器，也没有进行真实账号切换、Steam Cloud 同步或真实游戏存档覆盖。真实 Steam 页面、歌词提供方在线状态、Windows 10、实体双屏混合 DPI、Explorer 重启、睡眠唤醒及实际覆盖升级仍需要实机验收。本轮的自动测试和离屏渲染不能替代这些环境验证。

智能识别历史备份只有在能根据已知游戏规则证明目标路径时才自动显示。无法证明来源路径或游戏已不在可信列表时，应手工添加目标目录并走明确确认的迁移流程。中断恢复事务的旧目录会保留，避免自动清理潜在恢复数据。
