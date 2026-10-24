using System;
using System.Runtime.InteropServices;

namespace SteamSwitcher.Native
{
    /// <summary>
    /// 主窗口的原生 Win32 外观辅助：DWM 圆角/边框、Win10 圆角 Region、TaskbarCreated 消息注册。
    /// 从 MainWindow 下沉而来，集中 P/Invoke 与纯原生调用，便于单独测试且不污染 View 代码。
    /// WndProc 与命中测试仍留在 MainWindow——它们深度依赖窗口自身控件状态，属于 View 的合理职责。
    /// </summary>
    internal static class WindowNative
    {
        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(
            int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        public const int DwmwaWindowCornerPreference = 33;
        public const int DwmwaBorderColor = 34;
        public const int DwmWindowCornerPreferenceRound = 2;
        public const int DwmColorNone = unchecked((int)0xFFFFFFFE);

        public static readonly uint WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

        /// <summary>
        /// 应用原生窗口外观。Win11 由 DWM 负责圆角并隐藏边框；Win10 及以下改用圆角 Region。
        /// 所有参数由调用方（窗口自身）提供，本方法不持有任何窗口状态，可独立测试。
        /// </summary>
        public static void ApplyWindowAppearance(
            IntPtr hwnd, bool isMaximized, double actualWidth, double actualHeight, double dpiX, double dpiY)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                {
                    // Windows 11 自行绘制外圆角；抑制 DWM 边框还能去掉自定义标题栏上方的高亮条。
                    int cornerPreference = DwmWindowCornerPreferenceRound;
                    DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

                    int borderColor = DwmColorNone;
                    DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));

                    // 清除遗留 Region，让 DWM 接管抗锯齿圆角与最大化状态。
                    SetWindowRgn(hwnd, IntPtr.Zero, true);
                    return;
                }

                if (isMaximized)
                {
                    SetWindowRgn(hwnd, IntPtr.Zero, true);
                    return;
                }

                int width = (int)(actualWidth * dpiX);
                int height = (int)(actualHeight * dpiY);
                int radius = (int)(16 * dpiX);

                if (width <= 0 || height <= 0) return;

                var rgn = CreateRoundRectRgn(0, 0, width, height, radius, radius);
                if (SetWindowRgn(hwnd, rgn, true) == 0)
                {
                    DeleteObject(rgn);
                }
            }
            catch { }
        }
    }
}
