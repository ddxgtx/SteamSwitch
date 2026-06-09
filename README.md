<div align="center">

# Steam Switch

**快速切换 Steam 账号的 Windows 桌面工具**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)]()

[功能特性](#功能特性) • [安装](#安装) • [使用说明](#使用说明) • [开发](#开发) • [许可证](#许可证)

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
- **选择性固定** - 可选择哪些账号显示在任务栏
- **位置调节** - 支持左/中/右位置，偏移量 ±1000px
- **液态玻璃效果** - 现代化半透明边框效果
- **圆角模式** - 可切换圆角/方角头像样式

### 界面设计
- **iOS 风格** - 深色主题，现代化设计
- **自定义窗口** - 无边框窗口，圆角设计
- **深色右键菜单** - 统一的深色主题
- **设置自动保存** - 所有设置自动保存到本地

## 系统要求

- Windows 10/11
- .NET 8.0 Desktop Runtime
- Steam 已安装

## 安装

### 方式一：下载发布版
1. 前往 [Releases](https://github.com/ddxgtx/SteamSwitch/releases) 页面
2. 下载最新版本的 `SteamSwitch.exe`
3. 运行即可（需要管理员权限）

### 方式二：从源码构建
```bash
# 克隆仓库
git clone https://github.com/ddxgtx/SteamSwitch.git
cd SteamSwitch

# 构建项目
dotnet build src/SteamSwitcher/SteamSwitcher.csproj -c Release

# 发布单文件
dotnet publish src/SteamSwitcher/SteamSwitcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 使用说明

### 基本使用

1. **启动程序** - 运行 `SteamSwitch.exe`，程序会自动请求管理员权限
2. **查看账号** - 主界面显示所有已登录的 Steam 账号
3. **切换账号** - 选择账号后点击「切换并启动」或「仅切换」
4. **系统托盘** - 关闭窗口会最小化到系统托盘

### 任务栏常驻

1. 点击账号右侧的 📌 按钮选择要固定的账号
2. 勾选底部「任务栏常驻」选项
3. 固定的账号头像会出现在任务栏
4. 点击任务栏头像即可快速切换

### 设置选项

点击标题栏 ⚙ 按钮打开设置：

| 选项 | 说明 |
|------|------|
| 切换后自动启动 Steam | 切换账号后自动启动 Steam 客户端 |
| 关闭窗口时最小化到托盘 | 关闭主窗口时最小化而非退出 |
| 开机自动启动 | Windows 启动时自动运行 |
| 显示位置 | 任务栏头像位置（左/中/右） |
| 位置偏移 | 微调位置（±1000px） |
| 头像大小 | 调整头像尺寸（32-56px） |
| 液态玻璃效果 | 开启/关闭半透明边框效果 |
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
│       │   ├── SteamService.cs      # Steam 进程管理
│       │   ├── AccountManager.cs    # 账号管理
│       │   ├── VdfParser.cs         # VDF 文件解析
│       │   ├── RegistryHelper.cs    # 注册表操作
│       │   └── TaskbarEmbedder.cs   # 任务栏嵌入
│       ├── Models/            # 数据模型
│       ├── ViewModels/        # MVVM 视图模型
│       ├── Views/             # UI 控件
│       ├── Services/          # 服务
│       │   ├── TrayIconService.cs   # 系统托盘
│       │   └── SettingsService.cs   # 设置服务
│       └── Resources/         # 资源文件
├── LICENSE                    # MIT 许可证
├── CHANGELOG.md              # 更新日志
└── README.md                 # 项目说明
```

### 构建命令

```bash
# 开发构建
dotnet build

# 发布单文件
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 技术原理

Steam Switch 通过以下方式实现账号切换：

1. **读取 `loginusers.vdf`** - Steam 的账号配置文件
2. **修改 `MostRecent` 标志** - 标记要登录的账号
3. **更新注册表** - 设置 `AutoLoginUser` 值
4. **启动 Steam** - Steam 会自动登录标记的账号

## 常见问题

### Q: 为什么需要管理员权限？
A: 修改 Steam 自动登录设置需要写入 Windows 注册表，这需要管理员权限。

### Q: 切换后需要重新输入密码吗？
A: 不需要，前提是该账号之前已经登录过并勾选了「记住密码」。

### Q: 如何添加新账号？
A: 先手动登录 Steam 并勾选「记住密码」，然后在 Steam Switch 中刷新账号列表。

### Q: 支持多 Steam 路径吗？
A: 目前只支持默认 Steam 安装路径。

## 许可证

本项目采用 [MIT 许可证](LICENSE) 开源。

## 致谢

- [Steam](https://store.steampowered.com/) - Valve 的游戏平台
- [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/) - Windows Presentation Foundation
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - MVVM 框架
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) - 系统托盘支持

---

<div align="center">

**[回到顶部](#steam-switch)**

</div>
