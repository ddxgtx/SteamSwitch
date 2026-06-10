# 截图说明

本目录包含 Steam Switch 应用程序的截图，用于 README 文档。

## 截图列表

- `main_window.png` - 主界面（暗黑模式）
- `game_list.png` - 游戏列表界面
- `settings.png` - 设置界面
- `taskbar.png` - 任务栏常驻效果
- `floating_window.png` - 桌面悬浮窗效果
- `tray_menu.png` - 系统托盘菜单
- `white_theme.png` - 白色主题界面

## 如何截图

1. 启动 Steam Switch 应用程序
2. 切换到需要截图的界面
3. 使用 Windows 截图工具（Win + Shift + S）或 PowerShell 脚本
4. 保存截图到本目录

## PowerShell 截图脚本

```powershell
# 截取全屏
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$screenshotPath = "screenshot_$timestamp.png"
$bitmap.Save($screenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)

$graphics.Dispose()
$bitmap.Dispose()

Write-Host "截图已保存: $screenshotPath"
```
