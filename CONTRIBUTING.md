# 贡献指南

感谢你对 Steam Switch 项目的关注！我们欢迎各种形式的贡献。

## 如何贡献

### 报告 Bug

1. 前往 [Issues](https://github.com/ddxgtx/SteamSwitch/issues) 页面
2. 点击「New Issue」
3. 选择「Bug Report」模板
4. 填写详细信息，包括：
   - 操作系统版本
   - .NET 版本
   - 复现步骤
   - 期望行为
   - 实际行为
   - 截图（如有）

### 提交功能建议

1. 前往 [Issues](https://github.com/ddxgtx/SteamSwitch/issues) 页面
2. 点击「New Issue」
3. 选择「Feature Request」模板
4. 详细描述你的建议

### 提交代码

1. Fork 本仓库
2. 创建你的特性分支 (`git checkout -b feature/AmazingFeature`)
3. 提交你的更改 (`git commit -m 'Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 打开一个 Pull Request

## 开发环境

### 前置要求

- Visual Studio 2022 或 JetBrains Rider
- .NET 8.0 SDK
- Git

### 设置开发环境

```bash
# 克隆仓库
git clone https://github.com/ddxgtx/SteamSwitch.git
cd SteamSwitch

# 还原依赖
dotnet restore

# 构建项目
dotnet build

# 运行项目
dotnet run --project src/SteamSwitcher/SteamSwitcher.csproj
```

### 代码规范

- 遵循 C# 编码规范
- 使用 MVVM 架构模式
- 为新功能添加适当的注释
- 保持代码简洁易读

### 提交规范

提交信息应遵循以下格式：

```
<类型>(<范围>): <描述>

<详细说明>

<关联 Issue>
```

类型包括：
- `feat`: 新功能
- `fix`: Bug 修复
- `docs`: 文档更新
- `style`: 代码格式调整
- `refactor`: 代码重构
- `test`: 测试相关
- `chore`: 构建/工具相关

示例：
```
feat(taskbar): 添加任务栏头像位置调节功能

- 支持左/中/右位置选择
- 支持偏移量调节（±1000px）
- 设置自动保存

Closes #123
```

## 行为准则

- 尊重他人
- 保持友善
- 接受建设性批评
- 专注于对社区最有利的事情

## 联系方式

如有任何问题，请通过以下方式联系：

- GitHub Issues: [ddxgtx/SteamSwitch](https://github.com/ddxgtx/SteamSwitch/issues)

## 许可证

通过贡献代码，你同意你的贡献将在 [MIT 许可证](LICENSE) 下发布。
