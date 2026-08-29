using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOSENDCHANGING = 0x0400;
        private const int EdgeMargin = 8;
        private const int TrayReserveGap = 56;

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
        private int _offsetY;
        private int _monitorIndex;
        private System.Windows.Threading.DispatcherTimer? _positionTimer;
        private IntPtr _lastPositionHwnd;
        private int _lastX = int.MinValue;
        private int _lastY = int.MinValue;
        private int _lastWidth = int.MinValue;
        private int _lastHeight = int.MinValue;

        public bool IsEmbedded => _isEmbedded;
        public TaskbarPosition Position
        {
            get => _position;
            set
            {
                if (_position == value) return;
                _position = value;
                if (_isEmbedded) UpdatePosition();
            }
        }
        public int OffsetX
        {
            get => _offsetX;
            set
            {
                if (_offsetX == value) return;
                _offsetX = value;
                if (_isEmbedded) UpdatePosition();
            }
        }
        public int OffsetY
        {
            get => _offsetY;
            set
            {
                if (_offsetY == value) return;
                _offsetY = value;
                if (_isEmbedded) UpdatePosition();
            }
        }

        public event EventHandler? TaskbarCreated;

        public void SetMonitorIndex(int monitorIndex)
        {
            monitorIndex = Math.Max(0, monitorIndex);
            if (_monitorIndex == monitorIndex) return;
            _monitorIndex = monitorIndex;
            if (_isEmbedded)
            {
                RemoveFromTaskbar();
                EmbedToTaskbar();
                StartPositionTimer();
            }
        }

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

        public void UpdateWidth(int width)
        {
            width = Math.Max(1, width);
            if (_width == width) return;
            _width = width;
            if (_isEmbedded) UpdatePosition();
        }

        public int GetMaximumWidthInDips()
        {
            try
            {
                IntPtr taskbarHandle = FindTargetTaskbar();
                if (taskbarHandle == IntPtr.Zero || !GetWindowRect(taskbarHandle, out var taskbarRect))
                    return 720;

                int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
                int trayX = FindTrayAreaLeft(taskbarHandle, taskbarWidth);
                int maximumPixels = _position is TaskbarPosition.Auto or TaskbarPosition.Right
                    ? CalculateRightEdge(trayX) - EdgeMargin
                    : taskbarWidth - EdgeMargin * 2;
                return Math.Max(240, PixelsToDips(maximumPixels));
            }
            catch
            {
                return 720;
            }
        }
        private void StartPositionTimer()
        {
            _positionTimer = new System.Windows.Threading.DispatcherTimer();
            _positionTimer.Interval = TimeSpan.FromSeconds(5);
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
                IntPtr taskbarHandle = FindTargetTaskbar();
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

                _isEmbedded = true;
                _parentHandle = taskbarHandle;

                ResetPositionCache();
                UpdatePosition(forceFrameRefresh: true);
                _embeddedWindow.Show();
            }
            catch (Exception ex)
            {
                _isEmbedded = false;
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
                ResetPositionCache();
                _embeddedWindow.Hide();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RemoveFromTaskbar failed: {ex.Message}");
            }
        }

        public void UpdatePosition()
        {
            UpdatePosition(forceFrameRefresh: false);
        }

        private void UpdatePosition(bool forceFrameRefresh)
        {
            if (_embeddedWindow == null || !_isEmbedded) return;

            try
            {
                IntPtr taskbarHandle = FindTargetTaskbar();
                if (taskbarHandle == IntPtr.Zero) return;

                IntPtr hwnd = new WindowInteropHelper(_embeddedWindow).Handle;

                RECT taskbarRect;
                GetWindowRect(taskbarHandle, out taskbarRect);

                int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
                int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;

                int trayX = FindTrayAreaLeft(taskbarHandle, taskbarWidth);
                int desiredWidth = DipsToPixels(_width);
                int width = GetEffectiveWidth(taskbarWidth, trayX, desiredWidth);
                int x = CalculateX(taskbarWidth, trayX, width);
                int availableHeight = Math.Max(24, taskbarHeight - 4);
                int desiredHeight = DipsToPixels(_embeddedWindow.ActualHeight > 0
                    ? _embeddedWindow.ActualHeight
                    : _embeddedWindow.Height);
                if (desiredHeight <= 0)
                    desiredHeight = availableHeight;

                int height = Math.Clamp(desiredHeight, 24, availableHeight);
                int y = Math.Max(2, (taskbarHeight - height) / 2) + _offsetY;

                if (!forceFrameRefresh &&
                    hwnd == _lastPositionHwnd &&
                    x == _lastX && y == _lastY && width == _lastWidth && height == _lastHeight)
                {
                    return;
                }

                SetWindowPos(hwnd, IntPtr.Zero,
                             x, y,
                             width, height,
                             SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING |
                             (forceFrameRefresh ? SWP_FRAMECHANGED : 0));
                _lastPositionHwnd = hwnd;
                _lastX = x;
                _lastY = y;
                _lastWidth = width;
                _lastHeight = height;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdatePosition failed: {ex.Message}");
            }
        }

        private void ResetPositionCache()
        {
            _lastPositionHwnd = IntPtr.Zero;
            _lastX = int.MinValue;
            _lastY = int.MinValue;
            _lastWidth = int.MinValue;
            _lastHeight = int.MinValue;
        }

        private int GetEffectiveWidth(int taskbarWidth, int trayX, int desiredWidth)
        {
            if (_position is TaskbarPosition.Auto or TaskbarPosition.Right)
            {
                int safeMaxWidth = CalculateRightEdge(trayX) - EdgeMargin;
                if (safeMaxWidth > 24)
                    return Math.Min(desiredWidth, safeMaxWidth);
            }

            return desiredWidth;
        }

        private int DipsToPixels(double value)
        {
            if (_embeddedWindow == null || double.IsNaN(value) || double.IsInfinity(value))
                return 0;

            var source = PresentationSource.FromVisual(_embeddedWindow);
            double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            return Math.Max(1, (int)Math.Ceiling(value * scale));
        }

        private int PixelsToDips(int value)
        {
            if (_embeddedWindow == null)
                return value;

            var source = PresentationSource.FromVisual(_embeddedWindow);
            double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            return Math.Max(1, (int)Math.Floor(value / Math.Max(0.1, scale)));
        }
        private int CalculateX(int taskbarWidth, int trayX, int width)
        {
            int x;
            int? protectedMaxX = null;

            switch (_position)
            {
                case TaskbarPosition.Left:
                    x = 10 + _offsetX;
                    break;

                case TaskbarPosition.Center:
                    x = (taskbarWidth - width) / 2 + _offsetX;
                    break;

                case TaskbarPosition.Right:
                case TaskbarPosition.Auto:
                    // 右侧和自动模式以常驻条最右侧边框为锚点，正偏移表示继续向左让出空间。
                    int rightEdge = CalculateRightEdge(trayX);
                    x = rightEdge - width;
                    protectedMaxX = x;
                    break;

                default:
                    int defaultRightEdge = CalculateRightEdge(trayX);
                    x = defaultRightEdge - width;
                    protectedMaxX = x;
                    break;
            }

            return ClampX(x, taskbarWidth, width, protectedMaxX);
        }

        private int CalculateRightEdge(int trayX)
        {
            int requestedRightEdge = trayX - TrayReserveGap - Math.Max(0, _offsetX);
            return Math.Max(EdgeMargin + 24, requestedRightEdge);
        }

        private int ClampX(int x, int taskbarWidth, int width, int? protectedMaxX)
        {
            int maxX = Math.Max(EdgeMargin, taskbarWidth - width - EdgeMargin);
            if (protectedMaxX.HasValue)
                maxX = Math.Min(maxX, Math.Max(EdgeMargin, protectedMaxX.Value));

            return Math.Clamp(x, EdgeMargin, maxX);
        }

        private int FindTrayAreaLeft(IntPtr trayWnd, int taskbarWidth)
        {
            try
            {
                if (trayWnd == IntPtr.Zero) return taskbarWidth - 280;

                RECT taskbarRect;
                GetWindowRect(trayWnd, out taskbarRect);

                IntPtr trayNotifyWnd = FindWindowEx(trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
                if (trayNotifyWnd == IntPtr.Zero)
                    trayNotifyWnd = FindChildWindowRecursive(trayWnd, "TrayNotifyWnd");
                if (trayNotifyWnd != IntPtr.Zero)
                {
                    RECT trayRect;
                    GetWindowRect(trayNotifyWnd, out trayRect);
                    int trayLeftRelative = trayRect.Left - taskbarRect.Left;
                    if (trayLeftRelative > 0 && trayLeftRelative < taskbarWidth)
                        return trayLeftRelative;
                }

                IntPtr sysPager = FindWindowEx(trayWnd, IntPtr.Zero, "SysPager", null);
                if (sysPager == IntPtr.Zero)
                    sysPager = FindChildWindowRecursive(trayWnd, "SysPager");
                if (sysPager != IntPtr.Zero)
                {
                    RECT pagerRect;
                    GetWindowRect(sysPager, out pagerRect);
                    int pagerLeft = pagerRect.Left - taskbarRect.Left;
                    if (pagerLeft > 0 && pagerLeft < taskbarWidth)
                        return pagerLeft;
                }

                var taskbarRight = taskbarRect.Right - taskbarRect.Left;
                return taskbarRight > 400 ? taskbarRight - 280 : taskbarRight - 180;
            }
            catch
            {
                return taskbarWidth - 280;
            }
        }

        private IntPtr FindTargetTaskbar()
        {
            var taskbars = new List<(IntPtr Handle, bool Primary, int Left, int Top)>();
            EnumWindows((handle, _) =>
            {
                var className = new StringBuilder(64);
                GetClassName(handle, className, className.Capacity);
                var name = className.ToString();
                if (name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" && GetWindowRect(handle, out var rect))
                    taskbars.Add((handle, name == "Shell_TrayWnd", rect.Left, rect.Top));
                return true;
            }, IntPtr.Zero);

            var ordered = taskbars.OrderByDescending(item => item.Primary)
                .ThenBy(item => item.Left)
                .ThenBy(item => item.Top)
                .ToList();
            if (ordered.Count == 0) return FindWindow("Shell_TrayWnd", null);
            return ordered[Math.Min(_monitorIndex, ordered.Count - 1)].Handle;
        }

        private static IntPtr FindChildWindowRecursive(IntPtr parent, string className)
        {
            IntPtr child = IntPtr.Zero;
            while ((child = FindWindowEx(parent, child, null, null)) != IntPtr.Zero)
            {
                var currentClass = new StringBuilder(256);
                GetClassName(child, currentClass, currentClass.Capacity);
                if (string.Equals(currentClass.ToString(), className, StringComparison.Ordinal))
                    return child;

                var nested = FindChildWindowRecursive(child, className);
                if (nested != IntPtr.Zero)
                    return nested;
            }

            return IntPtr.Zero;
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
