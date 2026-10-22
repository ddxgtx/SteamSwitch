# 关闭、进程残留与内存检查

本次在实际安装版 `D:\Steam Switch\SteamSwitch.exe` 上检查运行状态，随后通过程序自己的单实例 IPC 请求正常退出，没有强制结束 Steam、游戏或其他桌面程序。源码修复在工作区完成，安装目录未被替换。

## 实机结果

- 原实例 PID **40428**，启动时间 **2026-10-20 23:15:12 +08:00**，参数 `--updated`，版本 3.2.0。其 SHA-256 与上一轮修复版发布文件相同。
- 配置启用了 `MinimizeToTray`、任务栏固定和歌词。没有可见顶层窗口时，程序仍在托盘运行；窗口消失不代表完全退出。
- 23:18:10–23:18:25 的四次采样如下。工作集是当前驻留物理内存；私有内存是该进程的私有提交量，两者不能相加。

| 时间 | 工作集 MB | 私有内存 MB | 句柄 | 线程 |
|---|---:|---:|---:|---:|
| 23:18:10 | 265.80 | 182.36 | 1215 | 49 |
| 23:18:15 | 266.12 | 182.71 | 1215 | 49 |
| 23:18:20 | 266.16 | 184.09 | 1215 | 49 |
| 23:18:25 | 266.17 | 184.81 | 1212 | 48 |

- 23:19:04 通过 `--shutdown-for-update --autorun` 发出正常退出请求。原进程与发送请求的临时进程 PID **42156** 均退出；记录的等待时间 **0.547 秒**。随后复查未发现 SteamSwitch 或退出测试进程残留。
- `TranslucentTB.exe` PID **30972** 从 **2026-10-19 09:22:37** 已开始运行，父进程 10964，来自 Windows Store 包。它早于本次 SteamSwitch 实例启动，是独立程序，已保留。
- 这 15 秒的采样不能用于判断长时间内存泄漏。实际证据支持“托盘常驻”和“完全退出后无残留”；没有证明托盘状态的内存已经最优。

## 本轮修复

1. 主窗口、应用退出兜底、模型和媒体窗口改为逐项清理。一项释放异常不会跳过托盘、其他服务或最终 Shutdown；重复清理安全。
2. 单实例使用实际 Mutex 取锁结果，接受废弃锁；持锁期间只删除自己的 nonce，避免重启交接时删掉新实例的通信文件。测试使用隔离 nonce，避免影响正在运行的程序。
3. 账号服务退出时取消排队任务，允许活动事务收尾后再释放互斥量，避免销毁期间等待悬挂或 Release 异常。
4. 媒体启动按运行代号隔离。Stop/Dispose 后迟到的初始化不能重新启动计时器或挂回事件；并发 Start 共享初始化，空初始化结果允许重试。
5. 封面读取与 HTTP 链使用本次运行的取消令牌，停止后不再提交旧结果；并行来源首个成功后取消其余请求，观察迟到异常。
6. 桌面图标退出恢复不再等待后台回调持有的锁，使用异步窗口恢复并保留恢复标记；外部任务栏刷新加入异步窗口位置标志，降低等待 Explorer 的风险。
7. 关闭按钮根据当前设置提示“收起到托盘”或“退出 Steam Switch”，日志分别记录托盘隐藏、资源清理及应用退出。

Windows API 依据：[ShowWindowAsync](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindowasync)、[SetWindowPos 的 SWP_ASYNCWINDOWPOS](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos#parameters)。没有实际冻结 Explorer；模拟持锁测试验证退出专用路径不等待原锁，不能替代完整的系统故障注入。

## 证据与复跑

最终 `hot` 环境 Release 完整回归 **160/160 通过，0 失败、0 跳过**。UiCapture Release 构建 **0 警告、0 错误**；两种关闭方式各运行两次，4 个隔离 WPF 子进程全部以退出码 0 结束，耗时 0.594–0.612 秒，清理日志均 `Failures=0`，复查无子进程残留。

构建中遇到一次本机 .NET 8.0.424 Roslyn 分析器 `AccessViolationException`；将本次构建的 `DOTNET_PROCESSOR_COUNT` 限为 2 后完整重跑通过，没有禁用分析器或源生成器。该环境问题与被检查程序的退出行为分开记录。

- [实际运行内存采样](G:/SteamSwitch-main/artifacts/exit-audit-20261020/before-exit-memory.json)
- [安装版正常退出记录](G:/SteamSwitch-main/artifacts/exit-audit-20261020/normal-exit-result.json)
- [完整回归 TRX](G:/SteamSwitch-main/artifacts/exit-audit-20261020/tests/exit-lifecycle.trx)
- [WPF 子进程退出记录](G:/SteamSwitch-main/artifacts/exit-audit-20261020/wpf-exit-process-results.json)
- [最终进程快照](G:/SteamSwitch-main/artifacts/exit-audit-20261020/final-process-state.json)
- [本轮修复版安装包](G:/SteamSwitch-main/artifacts/exit-audit-20261020/installer/SteamSwitch-v3.2.0-win-x64-setup.exe)
- [自包含主程序](G:/SteamSwitch-main/artifacts/exit-audit-20261020/publish/SteamSwitch.exe)
- [SHA-256 校验值](G:/SteamSwitch-main/artifacts/exit-audit-20261020/SHA256SUMS.txt)

Windows x64 单文件发布及本机 Inno Setup 编译均已完成。安装包版本仍为 3.2.0，文件大小 55,351,513 字节，SHA-256 为 `EC6DDE46F5E83D93EE6F9A0376FC6E567BE5FBA4C205E3EA5DE99A05F9FFE425`。没有运行安装器，未把源码修复自动覆盖到 `D:\Steam Switch`。

WPF 测试使用隔离配置，不创建真实托盘图标、不运行 Steam、不修改桌面；验证真实 WPF 关闭事件、资源清理、Dispatcher 结束和测试子进程消失。生产安装版的正常退出是前面的独立实机证据。

```powershell
$env:DOTNET_PROCESSOR_COUNT = '2' # 仅当前 PowerShell 会话的构建并发限制
conda run --no-capture-output -n hot dotnet test SteamSwitch.sln -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal
conda run --no-capture-output -n hot dotnet build tools/UiCapture/UiCapture.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal
conda run --no-capture-output -n hot powershell -NoProfile -File artifacts/exit-audit-20261020/verify-exit-smoke.ps1
```

如希望点击右上角关闭按钮后立即退出，可关闭设置中的“关闭窗口时最小化到托盘”。本次保留当前偏好，检查后没有重新启动程序。
