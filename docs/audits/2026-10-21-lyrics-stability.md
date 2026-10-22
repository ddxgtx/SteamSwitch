# 歌词显示稳定性修复

## 已确认的闪动路径

| 原因 | 修复后的行为 |
| --- | --- |
| 每次歌词、副行或进度文字变化都会重新测宽，并触发任务栏宽度调整与逐句字号缩放 | 切歌或设置改变时计算布局；完整歌词到达后统一测量一次，逐句播放维持宽度和字号。超过可用空间的文字省略，悬停可看完整内容 |
| 主句更新直接改写整块文字透明度，与鼠标悬停淡出争用 | 换句只替换文字；悬停动画独立控制面板透明度，移除换句时的整块淡入、位移动画 |
| 无翻译时，下一句只在很短窗口出现，随后副行又变为进度文字 | 副行优先显示当前翻译，否则保持歌手、专辑或来源；下一句和进度保留在悬浮提示与进度条中 |
| 切歌后即使已经取得歌词，仍先强制展示 2.2 秒歌名 | 有效歌词立即显示；前奏和没有歌词时显示曲目信息 |
| SMTC 短暂缺失、读失败或歌曲标题短暂清空会清空当前显示 | 有有效历史媒体时保留最多 1.2 秒，超时后正常回到无媒体状态；初次启动没有历史内容时立即显示空状态 |
| 播放器补充/暂缺歌手、专辑字段会被识别为切歌 | 歌曲身份以播放来源、标题和已知歌手为依据，允许可选字段补全；真正来源、标题或已知歌手变化仍切歌 |
| 媒体会话列表重排可能切换不同播放器 | 当前选中的会话仍播放时优先保持，否则选择系统当前播放会话，再回退到其他会话 |
| 时间轴在歌词边界小幅校正会选回上一句或标题 | 对边界附近 150ms 范围内、单步不超过 300ms 的小幅回退保持当前句；正常向后拖动立即重新选句 |
| 同图字节数组变化会重复解码，暂缺图片会清空封面 | 比较封面内容，同图复用已解码图片；同曲目暂缺封面时保留，真正切歌与无媒体时清理 |
| UI 忙碌时逐条显示排队事件，关闭后迟到更新仍可触发控件逻辑 | 合并为最新媒体状态；关闭后丢弃更新，避免重启可视化计时器 |

实现位于 `src/SteamSwitcher/Services/MediaDisplayContinuity.cs`、`TaskbarMediaService.cs` 和 `src/SteamSwitcher/Views/TaskbarBandWindow.xaml.cs`。`TaskbarBandWindow.xaml` 将副行从无限测量的横向 StackPanel 改为有限宽度的 Grid，使长翻译能够正确省略。歌词设置页文案与行为保持一致；现有设置项、歌词源选择和固定宽度模式继续生效。

## 验证

- `hot` 环境中完整 Release 测试：**170/170 通过，0 失败、0 跳过**。新增 10 项回归覆盖边界抖动、真实回退、歌词来源替换、元数据补全和中断超时。
- UiCapture Release 构建：**0 警告、0 错误**。
- 浅色、深色各运行一次实际 WPF 控件稳定性检查，每次包括：80 次长短句、下一句、时间及封面变化；80 个排队事件合并成一次最新文本更新；悬停与换句同时发生；固定宽度；真实切歌；关闭后更新拒绝；长歌词与长翻译的实际布局宽度约束。
- 浅色、深色歌词设置页完成重新渲染与人工图片检查。
- CI 新增 `lyrics-stability-smoke`，检查失败会使构建失败。

证据：

- [完整测试结果](G:/SteamSwitch-main/artifacts/lyrics-stability-20261021/tests/lyrics-stability.trx)
- [深色 WPF 稳定性记录](G:/SteamSwitch-main/artifacts/lyrics-stability-20261021/tests/lyrics-dark.txt)
- [浅色 WPF 稳定性记录](G:/SteamSwitch-main/artifacts/lyrics-stability-20261021/tests/lyrics-light.txt)
- [深色设置页](G:/SteamSwitch-main/artifacts/lyrics-stability-20261021/settings-lyrics-dark.png)
- [浅色设置页](G:/SteamSwitch-main/artifacts/lyrics-stability-20261021/settings-lyrics-light.png)
- [长歌词与长翻译省略](G:/SteamSwitch-main/artifacts/lyrics-stability-20261021/lyrics-dark.png)
- [无翻译时的稳定曲目信息](G:/SteamSwitch-main/artifacts/lyrics-stability-20261021/band-metadata.png)

可复跑命令：

```powershell
$env:DOTNET_PROCESSOR_COUNT = '2'
conda run --no-capture-output -n hot dotnet test SteamSwitch.sln -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal
conda run --no-capture-output -n hot dotnet build tools/UiCapture/UiCapture.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false --verbosity minimal
conda run --no-capture-output -n hot dotnet tools/UiCapture/bin/Release/net8.0-windows10.0.19041.0/UiCapture.dll artifacts/lyrics-check.png lyrics-stability-smoke 1180 800 Dark
```

## 运行验证边界

WPF 检查使用隔离配置与固定输入，运行生产显示代码，但不打开真实窗口、不操作 Steam/Explorer、不播放音乐、不访问歌词服务。当前结论证明已定位代码路径的防抖与布局稳定，不代表已经实测所有播放器的 SMTC、网络检索及真实任务栏嵌入。长歌词保持设置字号，超出任务栏可用宽度时使用省略和完整悬浮提示。

安装包和自包含程序输出到 `artifacts/lyrics-stability-20261021/`，版本保持 3.2.0。本轮不覆盖既有安装目录、不启动安装器。

## 安装包

Windows x64 自包含发布和 Inno Setup 编译成功。安装包大小 **55,347,079 字节**，SHA-256：

```text
E7F21C956AA4096A126A4B70E34E49EFE49A423EAC4CC32F42ABF47B7F8683CB
```

- [歌词稳定性修复版安装包](G:/SteamSwitch-main/artifacts/lyrics-stability-20261021/installer/SteamSwitch-v3.2.0-win-x64-setup.exe)
- [自包含主程序](G:/SteamSwitch-main/artifacts/lyrics-stability-20261021/publish/SteamSwitch.exe)
- [SHA-256 校验文件](G:/SteamSwitch-main/artifacts/lyrics-stability-20261021/SHA256SUMS.txt)
- [最终验证摘要](G:/SteamSwitch-main/artifacts/lyrics-stability-20261021/verification.json)

验证结束后检查到 **0 个 SteamSwitch / UiCapture 残留进程**。安装器未执行，安装和真实播放器联动不在本轮已验证结果中。
