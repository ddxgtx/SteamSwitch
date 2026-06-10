<div align="center">

<img src="icon.png" width="100" height="100" alt="Steam Switch">

# Steam Switch

**Windows 上的 Steam 多账号切换、游戏账号绑定与快捷启动工具**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4.svg)]()
[![Release](https://img.shields.io/github/v/release/ddxgtx/SteamSwitch)](https://github.com/ddxgtx/SteamSwitch/releases)

[功能](#功能) · [截图](#截图) · [安装](#安装) · [使用](#使用) · [配置与日志](#配置与日志) · [风险提示](#风险提示)

</div>

---

## 简介

Steam Switch 会读取本机 Steam 已登录账号，并提供快速切换、游戏账号绑定、任务栏常驻、桌面悬浮窗、托盘菜单和可选的 Steam 库界面注入能力。

它适合经常在多个 Steam 账号之间切换，或希望把常用账号/游戏做成桌面与任务栏快捷入口的用户。

## 功能

### 账号切换

- 自动读取 Steam 本机登录账号、昵称、头像和当前账号状态
- 支持「切换并启动 Steam」与「仅切换账号」
- 支持系统托盘菜单快速切换账号、启动 Steam、恢复主窗口
- 支持关闭窗口后最小化到托盘
- 支持开机自动启动

### 游戏绑定与启动

- 扫描本机已安装 Steam 游戏，支持多个 Steam 库目录
- 为游戏绑定默认启动账号
- 固定常用游戏到任务栏常驻或桌面悬浮窗
- 支持手动添加 AppID 和游戏名称
- 手动添加绑定后会立即出现在游戏列表中
- 启动固定游戏后会记录最近使用时间
- 可开启启动前二次确认，避免误切账号或误启动游戏

### 任务栏常驻

- 将固定账号和固定游戏嵌入 Windows 任务栏
- 支持自动、左侧、中间、右侧定位
- 支持 X/Y 偏移、玻璃边距、图标大小、圆角模式和液态玻璃效果
- 右键菜单可恢复主窗口、切换账号、启动 Steam、刷新或分离任务栏

### 桌面悬浮窗

- 在桌面显示可拖动的账号和游戏快捷入口
- 支持始终置顶、锁定位置、透明度调节和位置记忆
- 支持独立图标大小、圆角模式、液态玻璃效果和 6 种玻璃颜色
- 不出现在 Alt+Tab 中，适合长期常驻

### 设置、主题与排障

- 支持暗黑/白色主题切换
- 设置自动保存，并带 500ms 防抖，减少频繁写盘
- 设置页提供「数据目录」「日志目录」「重置布局」快捷操作
- 日志按日期写入并自动轮转，方便定位启动、切换和注入问题

### Steam 库界面注入

- 在 Steam 库游戏详情页的「开始游戏」按钮旁注入「切换启动」按钮
- 点击「切换启动」可直接使用已绑定账号启动游戏
- 点击旁边的下拉箭头可打开账号选择菜单，切换绑定账号
- 自动保存游戏与账号绑定关系，下次启动自动应用
- 支持一键绑定/解绑，绑定后按钮变为绿色标识
- 默认关闭，启用前请先阅读下方风险提示

## 截图

<div align="center">

### 主界面
![主界面](docs/screenshots/screenshot_1.png)

### 游戏绑定
![游戏绑定](docs/screenshots/screenshot_2.png)

### 设置与快捷入口
![设置与快捷入口](docs/screenshots/screenshot_3.png)

### 系统托盘
![系统托盘](docs/screenshots/screenshot_4.png)

### Steam 库界面注入
![Steam 库界面注入](docs/screenshots/注入.png)

</div>

## 安装

### 下载发布版

1. 打开 [Releases](https://github.com/ddxgtx/SteamSwitch/releases)
2. 下载 `SteamSwitch-v2.2.0-win-x64-setup.exe` 安装包，或下载便携压缩包
3. 安装或解压后运行 `SteamSwitch.exe`

### 从源码构建

```powershell
git clone https://github.com/ddxgtx/SteamSwitch.git
cd SteamSwitch
dotnet restore SteamSwitch.sln
dotnet build SteamSwitch.sln -c Release
```

也可以直接运行：

```powershell
.\build.bat
```

## 系统要求

- Windows 10 / Windows 11
- .NET 8.0 Desktop Runtime 或 .NET 8.0 SDK
- 已安装 Steam 客户端

## 使用

### 切换账号

1. 启动 Steam Switch
2. 在账号列表中选择目标账号
3. 点击「切换并启动 Steam」或「仅切换账号」
4. 如果 Steam 正在运行，请按提示关闭后再切换

### 绑定游戏账号

1. 打开「游戏」页并扫描本机游戏
2. 选择一个游戏
3. 在右侧账号列表中选择默认账号
4. 点击绑定按钮
5. 需要长期快捷启动时，将该游戏固定到快捷入口

### 手动添加游戏

1. 在「游戏」页展开手动添加
2. 输入 Steam AppID 和游戏名称
3. 选择要绑定的账号
4. 保存后该游戏会立即加入列表，并可继续固定或启动

### 使用任务栏常驻

1. 至少固定一个账号或游戏
2. 在设置页开启「任务栏常驻」
3. 按需调整位置、偏移、玻璃边距和图标大小
4. 右键常驻条可打开快捷菜单

### 使用桌面悬浮窗

1. 至少固定一个账号或游戏
2. 在设置页开启「桌面悬浮窗」
3. 拖动到合适位置
4. 可开启锁定位置、始终置顶，或调整透明度和玻璃颜色

## 配置与日志

程序数据默认保存在当前 Windows 用户的应用数据目录：

```text
%APPDATA%\SteamSwitch
```

常见文件：

- `settings.json`：应用设置、主题、任务栏和悬浮窗配置
- `gamebindings.json`：游戏与账号绑定关系
- `logs\steamswitch-YYYYMMDD.log`：运行日志

在设置页的「维护与排障」中可以直接打开数据目录和日志目录，也可以一键重置任务栏/悬浮窗布局。

## 技术栈

- .NET 8.0
- WPF
- CommunityToolkit.Mvvm
- Hardcodet.NotifyIcon.Wpf
- Steam 本地配置与 VDF 解析
- Win32 API 任务栏嵌入
- Steam CEF DevTools 调试接口（可选）

## 项目结构

```text
SteamSwitch/
├── SteamSwitch.sln
├── build.bat
├── installer.iss
├── docs/
│   └── screenshots/
└── src/
    └── SteamSwitcher/
        ├── Core/
        ├── Models/
        ├── Services/
        ├── ViewModels/
        ├── Views/
        ├── Resources/
        ├── App.xaml
        └── MainWindow.xaml
```

## 风险提示

Steam Switch 是第三方开源工具，与 Valve Corporation、Steam 或任何游戏开发商没有从属、授权、背书或合作关系。Steam、Valve 及相关商标归其各自权利人所有。

使用本工具造成的账号限制、数据丢失、Steam 客户端异常、游戏启动异常、平台规则风险、VAC/反作弊风险或其他损失，均由使用者自行承担。

特别注意：

- 「Steam 库界面注入」会开启 Steam CEF 调试端口，并向 Steam 客户端页面执行脚本。
- 该行为可能不符合 Steam 用户协议或未来平台策略。
- 本工具不用于作弊、绕过 DRM、篡改游戏进程、修改游戏内存或获取不公平游戏优势。
- 不建议在 VAC、竞技或反作弊敏感游戏运行期间开启库界面注入或 CEF 调试端口。
- 如果不能接受上述风险，请关闭「Steam 库界面注入」，仅使用账号切换、任务栏常驻和桌面悬浮窗等外部快捷入口功能。

## 贡献

欢迎提交 Issue 和 Pull Request。

```powershell
git checkout -b feature/your-feature
dotnet build SteamSwitch.sln -c Release
```

提交前请至少确认项目可以成功构建。

## 许可证

本项目基于 [MIT License](LICENSE) 开源。

## 致谢

- [Steam](https://store.steampowered.com/)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon)

<div align="center">

**[GitHub](https://github.com/ddxgtx/SteamSwitch)** · **[Releases](https://github.com/ddxgtx/SteamSwitch/releases)** · **[Issues](https://github.com/ddxgtx/SteamSwitch/issues)**

Powered by **ddxgtx**

</div>
