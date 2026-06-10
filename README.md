<div align="center">

# Steam Switch

**Steam 多账号快速切换、游戏账号绑定与桌面快捷启动工具**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)]()
[![Release](https://img.shields.io/github/v/release/ddxgtx/SteamSwitch)](https://github.com/ddxgtx/SteamSwitch/releases)

[功能特性](#功能特性) · [安装](#安装) · [使用说明](#使用说明) · [免责声明](#免责声明)

</div>

---

## 简介

Steam Switch 是一个 Windows 桌面工具，用于管理本机已登录的 Steam 账号，并提供账号快速切换、固定账号/游戏快捷入口、游戏账号绑定、桌面悬浮窗和任务栏常驻等功能。

2.0 版本重点升级了界面体验和快捷入口：主界面、任务栏常驻、桌面悬浮窗均采用更现代的 iOS 风格液态玻璃视觉，同时增加更多功能开关，方便按风险和使用习惯启用或关闭。

## 功能特性

### 账号切换

- 自动读取 Steam 本机已登录账号
- 显示账号头像、昵称和当前账号状态
- 支持「切换并启动 Steam」和「仅切换账号」
- 支持系统托盘菜单快速切换账号
- 支持关闭窗口后最小化到系统托盘

### 游戏绑定与快捷启动

- 扫描本机已安装 Steam 游戏
- 自动读取游戏图标和安装信息
- 为游戏绑定默认启动账号
- 固定常用游戏到快捷入口
- 启动固定游戏前可选择是否二次确认
- 支持手动添加 AppID 与游戏名称

### 任务栏常驻

- 将固定账号和固定游戏嵌入 Windows 任务栏
- 支持自动、左侧、中间、右侧定位
- 右侧/自动模式以最右边框为锚点，新增游戏后向左扩展
- 自动避让 Windows 托盘和隐藏图标按钮
- 支持图标大小、位置偏移、圆角和液态玻璃效果

### 桌面悬浮窗

- 可在桌面显示独立快捷入口
- 支持固定账号切换和固定游戏启动
- 支持拖动位置并自动保存
- 支持始终置顶、锁定位置和透明度调节
- 与任务栏常驻共享固定账号、固定游戏和外观设置

### Steam 库界面注入

- 可在 Steam 库游戏详情页添加账号选择和启动入口
- 支持从 Steam 库页面选择账号并启动游戏
- 支持自动保存游戏与账号绑定关系
- 该功能涉及 Steam CEF 调试端口和页面脚本注入，默认建议关闭，使用前请阅读下方免责声明

### 界面与设置

- iOS 风格液态玻璃界面
- 现代化游戏界面、设置页和任务栏快捷条
- 支持开机自启动、启动时自动扫描游戏、库界面注入等功能开关
- 设置自动保存到本地用户配置目录

## 安装

### 下载发布版

1. 前往 [Releases](https://github.com/ddxgtx/SteamSwitch/releases)
2. 下载最新的 `SteamSwitch-v2.0.0-win-x64.zip` 或安装包
3. 解压或安装后运行 `SteamSwitch.exe`

### 从源码构建

```bash
git clone https://github.com/ddxgtx/SteamSwitch.git
cd SteamSwitch
dotnet restore SteamSwitch.sln
dotnet build SteamSwitch.sln -c Release
```

## 系统要求

- Windows 10/11
- .NET 8.0 Desktop Runtime
- 已安装 Steam 客户端

## 使用说明

### 基本账号切换

1. 启动 `SteamSwitch.exe`
2. 在账号列表中选择目标账号
3. 点击「切换并启动 Steam」或「仅切换账号」
4. 如果启用了「切换后自动启动 Steam」，切换成功后会自动打开 Steam

### 固定账号和游戏

1. 在账号列表中点击固定按钮，选择要固定的账号
2. 在游戏页扫描本机游戏
3. 为常用游戏绑定默认账号
4. 固定常用游戏后，可在任务栏常驻或桌面悬浮窗中快速启动

### 任务栏常驻

1. 至少固定一个账号或游戏
2. 在底部或设置页开启「任务栏常驻」
3. 在设置中调整位置、偏移和图标大小
4. 若使用右侧或自动定位，常驻条会自动避让系统托盘区域

### 桌面悬浮窗

1. 至少固定一个账号或游戏
2. 在设置页开启「桌面悬浮窗」
3. 拖动悬浮窗到合适位置
4. 可开启「锁定位置」避免误拖动
5. 可调整透明度和始终置顶状态

### Steam 库界面注入

1. 在设置页开启「Steam 库界面注入」
2. 程序会以 CEF 调试模式重启 Steam
3. 打开 Steam 游戏详情页后，会尝试显示账号选择和启动入口
4. 使用完成后建议关闭该功能

## 技术栈

- .NET 8.0
- WPF
- CommunityToolkit.Mvvm
- Hardcodet.NotifyIcon.Wpf
- Steam 本地配置读取
- Steam CEF DevTools 调试接口（可选注入功能）

## 配置与数据

程序会读取本机 Steam 配置和本地游戏清单，用于展示账号、头像、游戏与绑定关系。应用自身设置保存于当前 Windows 用户的应用数据目录。

## 免责声明

本项目为第三方开源工具，与 Valve Corporation、Steam 或任何游戏开发商没有从属、授权、背书或合作关系。Steam、Valve 及相关商标归其各自权利人所有。

使用本工具造成的任何账号限制、数据丢失、Steam 客户端异常、游戏启动异常、平台规则风险、VAC/反作弊风险或其他损失，均由使用者自行承担。

尤其需要注意：

- 「Steam 库界面注入」会开启 Steam CEF 调试端口，并向 Steam 客户端页面执行脚本；该行为可能不符合 Steam 用户协议或未来平台策略。
- 本工具不用于作弊、绕过 DRM、篡改游戏进程、修改游戏内存或获取不公平游戏优势。
- 不建议在 VAC、竞技、反作弊敏感游戏运行期间开启库界面注入或 CEF 调试端口。
- 如果你不能接受上述风险，请关闭「Steam 库界面注入」，仅使用账号切换、任务栏常驻和桌面悬浮窗等外部快捷入口功能。

## 许可证

本项目采用 [MIT License](LICENSE) 开源。

---

<div align="center">

**[GitHub](https://github.com/ddxgtx/SteamSwitch)** · **[Releases](https://github.com/ddxgtx/SteamSwitch/releases)**

Powered by **ddxgtx**

</div>
