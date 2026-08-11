# 截图说明

本目录包含 Steam Switch 应用程序的截图，用于 README 文档。

## 截图列表

- `screenshot_1.png` - 账号切换页面（暗黑模式）
- `screenshot_2.png` - 游戏列表与账号绑定页面
- `screenshot_3.png` - 快捷启动页面
- `screenshot_4.png` - 设置中心页面
- `注入.png` - Steam 库注入效果

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
