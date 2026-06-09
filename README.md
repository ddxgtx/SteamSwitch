<div align="center">

# Steam Switch

**快速切换 Steam 账号的 Windows 桌面工具**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)]()
[![Release](https://img.shields.io/github/v/release/ddxgtx/SteamSwitch)](https://github.com/ddxgtx/SteamSwitch/releases)

[功能特性](#功能特性) • [安装](#安装) • [使用说明](#使用说明)

</div>

---

## 功能特性

### 核心功能
- **一键切换** - 快速切换 Steam 账号
- **账号管理** - 自动读取已登录的 Steam 账号
- **头像显示** - 自动加载 Steam 账号头像
- **系统托盘** - 最小化到系统托盘，右键菜单快速切换

### 任务栏集成
- **任务栏常驻** - 账号头像嵌入 Windows 任务栏
- **自动定位** - 自动检测系统托盘位置
- **选择性固定** - 可选择哪些账号显示在任务栏
- **液态玻璃效果** - 现代化半透明边框
- **圆角模式** - 可切换圆角/方角样式

### Steam 库注入 (新功能)
- **库界面注入** - 在 Steam 游戏详情页添加切换按钮
- **游戏绑定** - 为每个游戏指定默认账号
- **自动切换** - 启动游戏时自动切换到绑定的账号
- **自动记录** - 记录每个游戏最后使用的账号

### 界面设计
- **iOS 风格** - 深色主题，现代化设计
- **自定义窗口** - 无边框窗口，圆角设计
- **深色右键菜单** - 统一的深色主题
- **设置自动保存** - 所有设置自动保存到本地

## 安装

### 方式一：下载安装程序
1. 前往 [Releases](https://github.com/ddxgtx/SteamSwitch/releases) 页面
2. 下载 `SteamSwitch-v1.1.0-win-x64-setup.exe`
3. 运行安装程序

### 方式二：从源码构建
```bash
git clone https://github.com/ddxgtx/SteamSwitch.git
cd SteamSwitch
dotnet restore src/SteamSwitcher/SteamSwitcher.csproj
dotnet build src/SteamSwitcher/SteamSwitcher.csproj -c Release
```

## 系统要求

- Windows 10/11
- .NET 8.0 Desktop Runtime
- Steam 已安装

## 使用说明

### 基本使用

1. **启动程序** - 运行 `SteamSwitch.exe`
2. **查看账号** - 主界面显示所有已登录的 Steam 账号
3. **切换账号** - 选择账号后点击「切换并启动」或「仅切换」
4. **系统托盘** - 关闭窗口会最小化到系统托盘

### 任务栏常驻

1. 点击账号右侧的 📌 按钮选择要固定的账号
2. 勾选底部「任务栏常驻」选项
3. 固定的账号头像会出现在任务栏

### Steam 库注入

1. 点击标题栏 🎮 按钮打开游戏绑定管理
2. 输入游戏 AppID 和名称，选择要绑定的账号
3. 勾选「库界面注入」选项
4. 点击 🎮 按钮启动注入器
5. 在 Steam 游戏详情页会出现「切换账号启动」按钮

### 设置选项

点击标题栏 ⚙ 按钮打开设置：

| 选项 | 说明 |
|------|------|
| 显示位置 | 自动 / 左 / 中 / 右 |
| 位置偏移 | 微调位置（±1000px） |
| 头像大小 | 调整头像尺寸（32-56px） |
| 液态玻璃效果 | 开启/关闭半透明边框 |
| 圆角模式 | 切换圆角/方角样式 |

## 技术栈

- **框架**: .NET 8.0
- **UI**: WPF (Windows Presentation Foundation)
- **架构**: MVVM (Model-View-ViewModel)
- **依赖包**:
  - [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - MVVM 框架
  - [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) - 系统托盘支持

## 许可证

本项目采用 [MIT 许可证](LICENSE) 开源。

---

<div align="center">

**[GitHub](https://github.com/ddxgtx/SteamSwitch)** • **[Releases](https://github.com/ddxgtx/SteamSwitch/releases)**

Powered by **ddxgtx**

</div>
