using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SteamSwitcher.Core
{
    public enum TaskbarPosition
    {
        Left,
        Center,
        Right,
        Auto // 自动定位到系统托盘左侧
    }

    public class TaskbarEmbedder
    {
        #region Win32 API

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter,
                                                   string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                                 int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpMsg);

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        #endregion

        private static readonly uint WM_TASKBAR_CREATED = RegisterWindowMessage("TaskbarCreated");

        private Window? _embeddedWindow;
        private IntPtr _parentHandle;
        private bool _isEmbedded;
        private int _width;
        private TaskbarPosition _position = TaskbarPosition.Auto;
        private int _offsetX;
        private System.Windows.Threading.DispatcherTimer? _positionTimer;

        public bool IsEmbedded => _isEmbedded;
        public TaskbarPosition Position
        {
            get => _position;
            set
            {
                _position = value;
                if (_isEmbedded) UpdatePosition();
            }
        }
        public int OffsetX
        {
            get => _offsetX;
            set
            {
                _offsetX = value;
                if (_isEmbedded) UpdatePosition();
            }
        }

        public event EventHandler? TaskbarCreated;

        public TaskbarEmbedder()
        {
        }

        public void EmbedWindow(Window wpfWindow, int width)
        {
            _embeddedWindow = wpfWindow;
            _width = width;

            var hwndSource = new WindowInteropHelper(wpfWindow);
            hwndSource.EnsureHandle();

            EmbedToTaskbar();
            StartPositionTimer();
        }

        private void StartPositionTimer()
        {
            _positionTimer = new System.Windows.Threading.DispatcherTimer();
            _positionTimer.Interval = TimeSpan.FromSeconds(2);
            _positionTimer.Tick += (s, e) =>
            {
                if (_isEmbedded)
                {
                    UpdatePosition();
                }
            };
            _positionTimer.Start();
        }

        private void EmbedToTaskbar()
        {
            if (_embeddedWindow == null) return;

            try
            {
                IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
                if (taskbarHandle == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("Cannot find Shell_TrayWnd");
                    return;
                }

                IntPtr hwnd = new WindowInteropHelper(_embeddedWindow).Handle;

                SetParent(hwnd, taskbarHandle);

                int style = GetWindowLong(hwnd, GWL_STYLE);
                style = (style | WS_CHILD) & ~WS_POPUP;
                SetWindowLong(hwnd, GWL_STYLE, style);

                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

                UpdatePosition();

                _embeddedWindow.Show();
                _isEmbedded = true;

                _parentHandle = taskbarHandle;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EmbedToTaskbar failed: {ex.Message}");
            }
        }

        public void RemoveFromTaskbar()
        {
            if (_embeddedWindow == null || !_isEmbedded) return;

            try
            {
                _positionTimer?.Stop();

                IntPtr hwnd = new WindowInteropHelper(_embeddedWindow).Handle;
                SetParent(hwnd, IntPtr.Zero);

                int style = GetWindowLong(hwnd, GWL_STYLE);
                style = (style & ~WS_CHILD) | WS_POPUP;
                SetWindowLong(hwnd, GWL_STYLE, style);

                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                exStyle &= ~(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

                _isEmbedded = false;
                _embeddedWindow.Hide();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RemoveFromTaskbar failed: {ex.Message}");
            }
        }

        public void UpdatePosition()
        {
            if (_embeddedWindow == null || !_isEmbedded) return;

            try
            {
                IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
                if (taskbarHandle == IntPtr.Zero) return;

                IntPtr hwnd = new WindowInteropHelper(_embeddedWindow).Handle;

                RECT taskbarRect;
                GetWindowRect(taskbarHandle, out taskbarRect);

                int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
                int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;

                int x = CalculateX(taskbarWidth);
                int y = 2;
                int height = taskbarHeight - 4;

                SetWindowPos(hwnd, IntPtr.Zero,
                             x, y,
                             _width, height,
                             SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdatePosition failed: {ex.Message}");
            }
        }

        private int CalculateX(int taskbarWidth)
        {
            // 找到系统托盘区域的位置
            int trayX = FindTrayAreaLeft(taskbarWidth);

            switch (_position)
            {
                case TaskbarPosition.Left:
                    return 10 + _offsetX;

                case TaskbarPosition.Center:
                    return (taskbarWidth - _width) / 2 + _offsetX;

                case TaskbarPosition.Right:
                    return taskbarWidth - _width - 10 - _offsetX;

                case TaskbarPosition.Auto:
                    // 自动定位到系统托盘左侧
                    return trayX - _width - 5 + _offsetX;

                default:
                    return trayX - _width - 5 + _offsetX;
            }
        }

        private int FindTrayAreaLeft(int taskbarWidth)
        {
            try
            {
                // 查找系统托盘通知区域
                IntPtr trayWnd = FindWindow("Shell_TrayWnd", null);
                if (trayWnd == IntPtr.Zero) return taskbarWidth - 200;

                // 获取任务栏位置
                RECT taskbarRect;
                GetWindowRect(trayWnd, out taskbarRect);

                // 查找通知区域
                IntPtr trayNotifyWnd = FindWindowEx(trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
                if (trayNotifyWnd == IntPtr.Zero) return taskbarWidth - 200;

                RECT trayRect;
                GetWindowRect(trayNotifyWnd, out trayRect);

                // 计算通知区域左边缘相对于任务栏的位置
                int trayLeftRelative = trayRect.Left - taskbarRect.Left;
                
                return trayLeftRelative;
            }
            catch
            {
                // 出错时返回默认位置
                return taskbarWidth - 200;
            }
        }

        public void HandleWndProc(IntPtr hwnd, int msg)
        {
            if (msg == WM_TASKBAR_CREATED)
            {
                _isEmbedded = false;
                EmbedToTaskbar();
                TaskbarCreated?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            _positionTimer?.Stop();
            _positionTimer = null;
            RemoveFromTaskbar();
        }
    }
}
