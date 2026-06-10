<div align="center">

# Steam Switch

**快速切换 Steam 账号的 Windows 桌面工具**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)]()
[![Release](https://img.shields.io/github/v/release/ddxgtx/SteamSwitch)](https://github.com/ddxgtx/SteamSwitch/releases)

[功能特性](#功能特性) • [安装](#安装) • [使用说明](#使用说明) • [下载](https://github.com/ddxgtx/SteamSwitch/releases/latest)

</div>

---

## 功能特性

### 🎮 Steam 库注入 (v1.3)
- **库界面注入** - 在游戏详情页的「开始游戏」按钮旁边注入「⚡ 切换账号」按钮
- **账号选择** - 点击按钮展开下拉菜单，选择要使用的账号
- **一键切换** - 选择账号后自动切换并启动游戏
- **账号绑定** - 支持将账号绑定到特定游戏

### ⚡ 核心功能
- **一键切换** - 快速切换 Steam 账号
- **账号管理** - 自动读取已登录的 Steam 账号
- **头像显示** - 自动加载 Steam 账号头像
- **系统托盘** - 最小化到系统托盘，右键菜单快速切换

### 📌 任务栏集成
- **任务栏常驻** - 账号头像嵌入 Windows 任务栏
- **自动定位** - 自动检测系统托盘位置
- **选择性固定** - 可选择哪些账号显示在任务栏
- **液态玻璃效果** - 现代化半透明边框
- **圆角模式** - 可切换圆角/方角样式

### 🎨 界面设计
- **iOS 风格** - 深色主题，现代化设计
- **自定义窗口** - 无边框窗口，圆角设计
- **深色右键菜单** - 统一的深色主题
- **设置自动保存** - 所有设置自动保存到本地
- **默认管理员模式** - 自动请求管理员权限

---

## 截图

```
┌─────────────────────────────────────────────┐
│  ⚡ Steam Switch                ⚙ ↻ ─ □ ✕ │
├─────────────────────────────────────────────┤
│  ┌─────────────────────────────────────────┐│
│  │ [头像] ddxgtx                    [当前] ││
│  │ [头像] scloudy8                   [📌]  ││
│  │ [头像] jrlo2so700                 [📌]  ││
│  └─────────────────────────────────────────┘│
├─────────────────────────────────────────────┤
│  ☐ 启动Steam  ☐ 最小化到托盘               │
│  ☐ 任务栏常驻  ☐ 库界面注入                 │
│  [切换并启动]  [仅切换]                     │
│  Powered by ddxgtx | GitHub                │
└─────────────────────────────────────────────┘
```

---

## 安装

### 方式一：下载安装程序 (推荐)
1. 前往 [Releases](https://github.com/ddxgtx/SteamSwitch/releases/latest) 页面
2. 下载 `SteamSwitch-v1.3.1-win-x64-setup.exe`
3. 运行安装程序（自动请求管理员权限）

### 方式二：下载便携版
1. 前往 [Releases](https://github.com/ddxgtx/SteamSwitch/releases/latest) 页面
2. 下载 `SteamSwitch-v1.3.1-win-x64.zip`
3. 解压后运行（需要 .NET 8.0 Runtime）

### 方式三：从源码构建
```bash
git clone https://github.com/ddxgtx/SteamSwitch.git
cd SteamSwitch
dotnet restore src/SteamSwitcher/SteamSwitcher.csproj
dotnet publish src/SteamSwitcher/SteamSwitcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

---

## 系统要求

- Windows 10/11
- 安装版/完整版：无需额外依赖
- 便携版：[.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

---

## 使用说明

### 基本使用

1. **启动程序** - 运行 `SteamSwitch.exe`（自动请求管理员权限）
2. **查看账号** - 主界面显示所有已登录的 Steam 账号
3. **切换账号** - 选择账号后点击「切换并启动」或「仅切换」
4. **系统托盘** - 关闭窗口会最小化到系统托盘

### Steam 库注入

1. 运行 `SteamSwitch.exe`
2. 点击底部 🎮 按钮启动注入器
3. 打开 Steam 库中任意游戏的详情页
4. 在「开始游戏」旁边点击「⚡ 切换账号」
5. 选择账号即可切换并启动

### 任务栏常驻

1. 点击账号右侧的 📌 按钮选择要固定的账号
2. 勾选底部「任务栏常驻」选项
3. 固定的账号头像会出现在任务栏

### 设置选项

点击标题栏 ⚙ 按钮打开设置：

| 选项 | 说明 |
|------|------|
| 显示位置 | 自动 / 左 / 中 / 右 |
| 位置偏移 | 微调位置（±1000px） |
| 头像大小 | 调整头像尺寸（32-56px） |
| 液态玻璃效果 | 开启/关闭半透明边框 |
| 圆角模式 | 切换圆角/方角样式 |

---

## 技术栈

- **框架**: .NET 8.0
- **UI**: WPF (Windows Presentation Foundation)
- **架构**: MVVM (Model-View-ViewModel)
- **注入**: Chrome DevTools Protocol (CDP)
- **依赖包**:
  - [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - MVVM 框架
  - [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) - 系统托盘支持

---

## 更新日志

### v1.3.1 (2026-06-10)
- 默认管理员模式 - 应用启动时自动请求管理员权限

### v1.3.0 (2026-06-10)
- 库界面游戏详情页注入按钮
- 支持选择账号切换并启动
- 支持绑定账号到游戏

### v1.2.0 (2026-06-09)
- Steam 库注入功能框架
- 游戏账号绑定管理

### v1.1.0 (2026-06-09)
- 自动定位到系统托盘图标左侧
- 液态玻璃效果和圆角模式开关
- 头像大小和位置偏移调节

### v1.0.0 (2026-06-09)
- 初始版本发布
- Steam 账号快速切换
- 系统托盘支持
- 任务栏常驻功能

---

## 许可证

本项目采用 [MIT 许可证](LICENSE) 开源。

---

<div align="center">

**[下载最新版](https://github.com/ddxgtx/SteamSwitch/releases/latest)** • **[GitHub](https://github.com/ddxgtx/SteamSwitch)**

Powered by **ddxgtx**

</div>
