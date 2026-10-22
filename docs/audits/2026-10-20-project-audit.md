**SteamSwitch 当前项目审查与下一步优化方案，2026-10-20。**

审查对象是 `G:\SteamSwitch-main` 当前工作区的 3.2.0 源码，包含审查前已经存在的大量未提交修改。本次只增加这份报告，没有修改产品源码、真实 Steam 配置或用户存档。历史记录只用于定位模块；以下问题均重新核对了当前实现。

当前功能覆盖已经较广，优先工作应集中在启动、设置、更新和恢复的完整流程。现有测试通过，但其中几个会影响实际使用的边界流程没有得到覆盖。

验证情况：在用户指定的 `hot` 环境运行以下命令，Release 构建及 67 项测试全部通过，失败 0、跳过 0。

```powershell
conda run --no-capture-output -n hot dotnet test SteamSwitch.sln -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal
```

首次使用默认编译服务时 Roslyn 报 `System.AccessViolationException`；关闭编译服务并串行编译后通过。这次异常不能直接归因于产品代码。

另外对更新下载、账号库加载、损坏 VDF 和存档服务做了隔离探针。未启动真实 Steam/Explorer 或进行桌面交互验收，因此没有把代码推断的视觉问题当作实机结果，也没有验证本次安装包。

下文 P1 表示建议下一批优先修复，P2 表示随后补齐的功能或可靠性问题，P3 表示较小的交互问题。它们是本次排期等级，不是安全漏洞评级。

| 编号 | 优先级 | 当前问题与用户影响 | 证据及验证程度 |
|---|---|---|---|
| 01 | P1 | 账号库构造时同步等待异步读取，存在 UI 启动阻塞风险。已有账号库、读取发生异步挂起且当前有 UI 上下文时，构造函数等待文件读取，文件读取的续体又等待 UI。 | [AccountLibraryService.cs:65](G:/SteamSwitch-main/src/SteamSwitcher/Core/AccountLibraryService.cs:65)、[AtomicJsonFile.cs:28](G:/SteamSwitch-main/src/SteamSwitcher/Services/AtomicJsonFile.cs:28)。当前源码在模拟 UI 上下文中，76 字节有效 JSON 连续 5 次复现；处理排队续体后完成。尚未做真实 WPF 二次启动复现。 |
| 02 | P1 | 首次下载更新校验完成后仍会失败：临时文件保持 `FileShare.None` 打开，随后直接重命名。缓存复用可以通过，掩盖了新下载流程的问题。 | [UpdateService.cs:480](G:/SteamSwitch-main/src/SteamSwitcher/Services/UpdateService.cs:480)、[UpdateService.cs:515](G:/SteamSwitch-main/src/SteamSwitcher/Services/UpdateService.cs:515)。使用当前源文件、内存 HTTP 响应隔离复现文件占用 IOException；仅在临时副本里提前关闭文件流后，同一探针成功。 |
| 03 | P1 | 导入设置和恢复默认设置只写磁盘，没有把新设置应用到当前 ViewModel。界面却提示已重新加载；随后设置变更或待处理保存还可能将旧内存值写回。 | [MainWindow.xaml.cs:3194](G:/SteamSwitch-main/src/SteamSwitcher/MainWindow.xaml.cs:3194)、[MainWindow.xaml.cs:3210](G:/SteamSwitch-main/src/SteamSwitcher/MainWindow.xaml.cs:3210)。调用的 `InitializeAsync()` 只重新初始化业务服务，没有加载设置；设置只在 [MainViewModel.cs:873](G:/SteamSwitch-main/src/SteamSwitcher/ViewModels/MainViewModel.cs:873) 的构造过程中读取。代码路径确认，未操作真实用户设置。 |
| 04 | P1 | Steam userdata 或智能识别存档的源目录丢失/清空后，存档列表条目消失，已有备份不能从存档中心找到恢复入口。自定义目录仍有配置的情况不能一概而论。 | [GameSaveBackupService.cs:131](G:/SteamSwitch-main/src/SteamSwitcher/Core/GameSaveBackupService.cs:131)、[GameSavePathDetector.cs:99](G:/SteamSwitch-main/src/SteamSwitcher/Core/GameSavePathDetector.cs:99)。临时目录复现：删除源目录前 1 个条目，删除后 0 个，备份文件仍有 1 个。 |
| 05 | P2 | 本地账号库尚未真正独立：`loginusers.vdf` 缺失或读取失败时，列表清空，无法显示已经保存的账号档案。 | [AccountManager.cs:178](G:/SteamSwitch-main/src/SteamSwitcher/Core/AccountManager.cs:178)、[AccountManager.cs:182](G:/SteamSwitch-main/src/SteamSwitcher/Core/AccountManager.cs:182)。当前控制流确认，账号库合并在提前返回之后。 |
| 06 | P2 | VDF 解析静默接受未闭合字符串、未闭合对象和多余顶层结束符。切换时重新序列化可能把不完整输入写成结构正常但内容不完整的文件；重新解析输出无法证明原输入完整。 | [VdfParser.cs:87](G:/SteamSwitch-main/src/SteamSwitcher/Core/VdfParser.cs:87)、[VdfParser.cs:182](G:/SteamSwitch-main/src/SteamSwitcher/Core/VdfParser.cs:182)、[AccountManager.cs:512](G:/SteamSwitch-main/src/SteamSwitcher/Core/AccountManager.cs:512)。三类损坏输入均在隔离探针中被接受；未破坏真实 Steam 文件。 |
| 07 | P2 | 停止后重启库注入器，或只重启 SteamSwitch 而保持 Steam 页面，旧脚本继续使用旧端口/令牌及旧账号绑定快照。页面有按钮，但操作可能到不了新服务。 | [MainViewModel.cs:2530](G:/SteamSwitch-main/src/SteamSwitcher/ViewModels/MainViewModel.cs:2530) 每次换令牌；[SteamCEFInjector.cs:346](G:/SteamSwitch-main/src/SteamSwitcher/Core/SteamCEFInjector.cs:346) 对同版本脚本提前返回，不更新配置。控制流确认，真实 Steam 重连待验收。 |
| 08 | P2 | 改歌词源不会让当前查询失效；网络失败也被缓存为空歌词，没有失败有效期。恢复网络或开启另一来源后，同一歌曲仍可能长期无歌词。 | [TaskbarMediaService.cs:311](G:/SteamSwitch-main/src/SteamSwitcher/Services/TaskbarMediaService.cs:311)、[TaskbarMediaService.cs:1228](G:/SteamSwitch-main/src/SteamSwitcher/Services/TaskbarMediaService.cs:1228)、[TaskbarMediaService.cs:1343](G:/SteamSwitch-main/src/SteamSwitcher/Services/TaskbarMediaService.cs:1343)。属性和缓存逻辑确认；未请求线上歌词服务。 |
| 09 | P2 | 无 SMTC 会话、仅靠窗口标题识别歌曲的兼容分支，每次都按零播放时间选歌词，并固定发布“正在播放”。歌词无法随时间推进，暂停状态也不可信。 | [TaskbarMediaService.cs:610](G:/SteamSwitch-main/src/SteamSwitcher/Services/TaskbarMediaService.cs:610)。固定进度和播放状态由源码确认；各播放器是否进入此分支待实机验证。 |
| 10 | P2 | 多屏浮窗保存后重新打开会被限制回主屏；吸边又按整个虚拟桌面边界计算，与位置恢复策略不一致。 | [DesktopFloatingWindow.xaml.cs:402](G:/SteamSwitch-main/src/SteamSwitcher/Views/DesktopFloatingWindow.xaml.cs:402)、[DesktopFloatingWindow.xaml.cs:919](G:/SteamSwitch-main/src/SteamSwitcher/Views/DesktopFloatingWindow.xaml.cs:919)。代码确认，副屏、负坐标和混合 DPI 待实机验证。 |
| 11 | P2 | 自定义存档移除后重新添加相同目录，会获得新随机 ID，无法导入原完整备份；缺少显式重新关联/迁移流程。 | [CustomSaveFolderDialog.xaml.cs:61](G:/SteamSwitch-main/src/SteamSwitcher/Views/CustomSaveFolderDialog.xaml.cs:61)、[GameSaveBackupService.cs:1077](G:/SteamSwitch-main/src/SteamSwitcher/Core/GameSaveBackupService.cs:1077)。隔离复现身份不一致异常。正常恢复的身份校验应继续保留。 |
| 12 | P2 | 默认使用差分备份，但导出按钮对差分版本仍可用，服务随后拒绝导出；没有将所选差分版本生成自包含完整包的入口。 | [GameSaveBackupService.cs:503](G:/SteamSwitch-main/src/SteamSwitcher/Core/GameSaveBackupService.cs:503)、[GameSaveCenterWindow.xaml.cs:529](G:/SteamSwitch-main/src/SteamSwitcher/Views/GameSaveCenterWindow.xaml.cs:529)。代码确认，是流程未闭合。 |
| 13 | P3 | 默认导出文件名直接使用包含 `HH:mm:ss` 的显示文字，带 Windows 非法字符冒号，游戏名也未清洗。 | [GameSaveCenterWindow.xaml.cs:362](G:/SteamSwitch-main/src/SteamSwitcher/Views/GameSaveCenterWindow.xaml.cs:362)、[GameSaveEntry.cs:73](G:/SteamSwitch-main/src/SteamSwitcher/Models/GameSaveEntry.cs:73)。已验证字符串含非法字符；原生保存对话框的最终表现未验证，不能据此宣称必然崩溃。 |
| 14 | P2，服务层条件缺陷 | 恢复临时目录按 AppId 清理，手动自定义存档 AppId 均为 0。相同父目录中不同条目并发恢复时，一个操作会删除另一个操作的 staging。 | [GameSaveBackupService.cs:328](G:/SteamSwitch-main/src/SteamSwitcher/Core/GameSaveBackupService.cs:328)、[GameSaveBackupService.cs:391](G:/SteamSwitch-main/src/SteamSwitcher/Core/GameSaveBackupService.cs:391)。临时目录并发探针确认 staging 被删及 DirectoryNotFoundException。当前单存档中心 `_isBusy` 会阻止通常的单窗口并发，不能描述成正常单次恢复必然丢数据。 |

从产品设计看，还需要补齐三个方面。

1. 账号档案已有“已存档”状态，但缺少从本应用主动遗忘、忽略后续自动收录及档案迁移入口。现有“导出设置”只包含 `AppSettings`，不包含 `accounts.json`、游戏绑定和快速启动项目。应区分“导出设置”和“导出应用配置包”，清楚列明包含项；账号包仅保存元数据，不包含 Steam 密码或登录凭据。
2. 成功提示应对应实际结果。设置保存失败时，[MainViewModel.cs:1071](G:/SteamSwitch-main/src/SteamSwitcher/ViewModels/MainViewModel.cs:1071) 会先清除 dirty 再忽略 `Save()` 返回值，缺少重试和持久的失败提示。注入连接应确认鉴权握手，歌词应区分同步可用与只有曲目信息，任务栏设置也应区分请求已提交和系统实际接受。当前证据只能证明部分错误不可见，不能证明本机任务栏效果已经失效。
3. 主要类职责过重：`MainWindow.xaml.cs` 约 3,267 行，`MainViewModel.cs` 约 3,108 行，混合设置、账号、游戏、备份、更新和注入协调。这已经让“重新初始化业务”被误当成“重新加载设置”。应按流程拆分服务和页面 ViewModel，先修复有明确验收目标的缺陷，再小步提取边界。

建议按以下三批推进，每批形成可验收的独立改动。

| 批次 | 工作内容 | 完成条件 |
|---|---|---|
| 第一批：恢复核心可靠性 | 01 账号库异步初始化；02 关闭下载流后再提交文件；03 统一 `ApplySettingsAsync`；04 将存档发现结果与备份索引合并。顺带修正设置保存失败的 dirty/重试逻辑。 | 有账号库的 WPF 首次/二次启动均能响应；新下载、缓存复用、取消、哈希失败均有明确结果；导入/重置立即生效且重启不回退；源目录不存在或为空仍能找到并恢复备份。 |
| 第二批：补齐数据与功能流程 | 05/06 账号库降级和严格 VDF；07 注入会话重建；08/09 歌词缓存及能力降级；11/12/13 存档重新关联、自包含导出和合法文件名；14 按条目和事务隔离恢复目录。 | VDF 损坏不会覆盖原文件；Steam 不重启也能停止后重连；网络恢复/切源可重新获取歌词；移除再添加目录后有显式恢复方式；任意可恢复版本均能导出自包含包；两项并发恢复互不干扰。 |
| 第三批：整理架构与发布验收 | 账号/设置/游戏工作流/媒体/更新职责拆分；补应用配置包和主动遗忘；10 多屏位置；完善桌面兼容与发布检查。 | 大类拆分保持原有测试；Windows 10/11、Explorer 重启、睡眠唤醒、任务栏自动隐藏、双屏与混合 DPI 有实机记录；自包含 publish、安装、覆盖升级、托盘退出和自动重启完成闭环验证。 |

本轮不建议先扩展新功能或整体重写界面。下一次实现建议从 01、02、03、04 开始，它们分别影响能否启动、能否更新、配置是否真的生效，以及丢失存档后能否恢复。

现有 67 项测试验证了许多解析、持久化和备份规则；它们不等同于完整产品流程验证。例如当前更新测试覆盖缓存复用，却没有覆盖一次成功的新下载提交；普通单元测试也没有暴露 UI 同步上下文中的构造阻塞。新增测试应围绕这些实际失败场景，桌面和安装验收另行记录。

隔离证据保留在以下临时位置，可能被系统清理，产品源码是问题定位的长期依据：

- 更新下载探针：`C:\Users\ddx\AppData\Local\Temp\SteamSwitch-Audit-20261020-3316f33051ae41ccb0eb864d012abc39`。首次链接原源码输出 `FIRST_DOWNLOAD_FAILED: IOException`；当前目录保留提前关闭流的实验副本，输出 `FIRST_DOWNLOAD_SUCCEEDED: True`，并不表示仓库已修复。
- UI 上下文探针：`C:\Users\ddx\AppData\Local\Temp\steamswitch-deadlock-audit-8a4561742d884cd9afe1318b8eceb1c3\Run.ps1`。
- VDF 探针：`C:\Users\ddx\AppData\Local\Temp\steamswitch-accounts-audit-8887657dbd8c4512a104310f4fb5b878\Run.ps1`。
- 存档探针及输出：`C:\Users\ddx\AppData\Local\Temp\SteamSwitchAudit-2db0b19998994ebfb9f150a6a153cee1\Program.cs`、`audit-results.log`。
