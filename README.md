<div align="center">

# Steam Switch

**快速切换 Steam 账号的 Windows 桌面工具**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)]()
[![Release](https://img.shields.io/github/v/release/ddxgtx/SteamSwitch)](https://github.com/ddxgtx/SteamSwitch/releases)

[功能特性](#功能特性) • [安装](#安装) • [使用说明](#使用说明) • [开发](#开发)

</div>

---

## 功能特性

### 核心功能
- **一键切换** - 快速切换 Steam 账号，无需手动修改配置
- **账号管理** - 自动读取已登录的 Steam 账号
- **头像显示** - 自动加载 Steam 账号头像
- **系统托盘** - 最小化到系统托盘，右键菜单快速切换

### 任务栏集成
- **任务栏常驻** - 账号头像嵌入 Windows 任务栏
- **自动定位** - 自动检测系统托盘位置，贴在图标左侧
- **选择性固定** - 可选择哪些账号显示在任务栏
- **液态玻璃效果** - 现代化半透明边框效果
- **圆角模式** - 可切换圆角/方角头像样式

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

### 方式二：下载便携版
1. 前往 [Releases](https://github.com/ddxgtx/SteamSwitch/releases) 页面
2. 下载 `SteamSwitch-v1.1.0-portable.zip`
3. 解压后运行（需要 .NET 8.0 Runtime）

### 方式三：从源码构建
```bash
git clone https://github.com/ddxgtx/SteamSwitch.git
cd SteamSwitch
dotnet build src/SteamSwitcher/SteamSwitcher.csproj -c Release
```

## 系统要求

- Windows 10/11
- 安装版/完整版：无需额外依赖
- 便携版：[.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

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
4. 默认自动定位到系统托盘图标左侧

### 设置选项

点击标题栏 ⚙ 按钮打开设置：

| 选项 | 说明 |
|------|------|
| 显示位置 | 自动 / 左 / 中 / 右 |
| 位置偏移 | 微调位置（±1000px） |
| 头像大小 | 调整头像尺寸（32-56px） |
| 液态玻璃效果 | 开启/关闭半透明边框 |
| 圆角模式 | 切换圆角/方角样式 |

## 开发

### 技术栈

- **框架**: .NET 8.0
- **UI**: WPF (Windows Presentation Foundation)
- **架构**: MVVM (Model-View-ViewModel)
- **依赖包**:
  - [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - MVVM 框架
  - [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) - 系统托盘支持

### 项目结构

```
SteamSwitch/
├── src/
│   └── SteamSwitcher/
│       ├── Core/              # 核心逻辑
│       ├── Models/            # 数据模型
│       ├── ViewModels/        # MVVM 视图模型
│       ├── Views/             # UI 控件
│       ├── Services/          # 服务
│       └── Resources/         # 资源文件
├── installer.iss              # Inno Setup 安装脚本
├── LICENSE                    # MIT 许可证
├── CHANGELOG.md               # 更新日志
└── README.md                  # 项目说明
```

## 许可证

本项目采用 [MIT 许可证](LICENSE) 开源。

---

<div align="center">

**[GitHub](https://github.com/ddxgtx/SteamSwitch)** • **[Releases](https://github.com/ddxgtx/SteamSwitch/releases)**

Powered by **ddxgtx**

</div>
