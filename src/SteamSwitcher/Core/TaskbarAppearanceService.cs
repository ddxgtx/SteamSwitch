using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    public enum TaskbarAppearanceMode
    {
        Normal,
        Clear,
        Blur,
        Acrylic,
        Opaque
    }

    /// <summary>
    /// Independently applies Windows composition attributes to Explorer taskbars.
    /// Feature inspiration: TranslucentTB (GPL-3.0), https://github.com/TranslucentTB/TranslucentTB
    /// No TranslucentTB source code or binaries are included or linked.
    /// </summary>
    public sealed class TaskbarAppearanceService : IDisposable
    {
        // Win32 constants
        private const int  WcaAccentPolicy          = 19;
        private const int  GwlExStyle               = -20;
        private const long WsExToolWindow           = 0x00000080L;
        private const int  DwmwaCloaked             = 14;
        private const int  DwmwaSystembackdropType  = 38;  // Win11 22H2+ only
        private const uint SwpNoSize                = 0x0001;
        private const uint SwpNoMove                = 0x0002;
        private const uint SwpNoZOrder              = 0x0004;
        private const uint SwpNoActivate            = 0x0010;
        private const uint SwpFrameChanged          = 0x0020;
        private const uint WmThemeChanged           = 0x031A;
        private const uint SmtoAbortIfHung          = 0x0002;
        private const uint MonitorDefaultToNearest  = 0x00000002;
        private const uint RdwInvalidate            = 0x0001;
        private const uint RdwErase                 = 0x0004;
        private const uint RdwAllChildren           = 0x0080;
        private const uint RdwFrame                 = 0x0400;

        // Build thresholds (cached at startup)
        private static readonly bool IsWin11_22H2 = Environment.OSVersion.Version.Build >= 22621;

        private readonly object _sync = new();
        private readonly Dictionary<IntPtr, AppliedAppearance> _applied = new();
        private Timer? _timer;
        private bool _enabled;
        private bool _dynamicMaximized;
        private TaskbarAppearanceMode _desktopMode  = TaskbarAppearanceMode.Clear;
        private TaskbarAppearanceMode _maximizedMode = TaskbarAppearanceMode.Opaque;
        private bool _dynamicStartMenu  = true;
        private TaskbarAppearanceMode _startMenuMode = TaskbarAppearanceMode.Normal;
        private bool _dynamicSearch     = true;
        private TaskbarAppearanceMode _searchMode    = TaskbarAppearanceMode.Normal;
        private bool _dynamicTaskView   = true;
        private TaskbarAppearanceMode _taskViewMode  = TaskbarAppearanceMode.Normal;
        private string _color   = "#000000";
        private int    _opacity = 0;   // Default: 0% = fully transparent
        private int    _timerTick;
        private bool   _disposed;

        public bool BlurCompatibilityFallback => Environment.OSVersion.Version.Build >= 22621;
        public bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;
        /// <summary>
        /// One-click: apply full transparency immediately, no external programs required.
        /// Sets mode=Clear, opacity=0 and uses a fast 300ms refresh cycle.
        /// </summary>
        public void ForceFullTransparent()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _enabled     = true;
                _desktopMode = TaskbarAppearanceMode.Clear;
                _color       = "#000000";
                _opacity     = 0;
                _applied.Clear();
                ApplyCurrentAppearance();
                _timerTick = 0;
                // Fast refresh cycle to fight XAML layer re-paint
                if (_timer == null)
                    _timer = new Timer(OnTimer, null,
                        TimeSpan.FromMilliseconds(300),
                        TimeSpan.FromMilliseconds(300));
            }
        }

        public static bool IsXamlEnhancerInstalled()
        {
            try
            {
                var localPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TranslucentTB", "TranslucentTB.exe");
                if (System.IO.File.Exists(localPath)) return true;

                var packagesDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                if (System.IO.Directory.Exists(packagesDir) && System.IO.Directory.GetDirectories(packagesDir, "*TranslucentTB*").Length > 0)
                    return true;
            }
            catch { }
            return false;
        }

        public static bool StartXamlEnhancer()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "shell:AppsFolder\\28017CharlesMilette.TranslucentTB_v826wp6bftszj!App",
                    UseShellExecute = true
                };
                Process.Start(psi);
                return true;
            }
            catch
            {
                try
                {
                    var localPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TranslucentTB", "TranslucentTB.exe");
                    if (System.IO.File.Exists(localPath))
                    {
                        Process.Start(new ProcessStartInfo { FileName = localPath, UseShellExecute = true });
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        public void Configure(
            bool enabled,
            TaskbarAppearanceMode desktopMode,
            string color,
            int opacity,
            bool dynamicMaximized,
            TaskbarAppearanceMode maximizedMode,
            bool dynamicStartMenu = true,
            TaskbarAppearanceMode startMenuMode = TaskbarAppearanceMode.Normal,
            bool dynamicSearch = true,
            TaskbarAppearanceMode searchMode = TaskbarAppearanceMode.Normal,
            bool dynamicTaskView = true,
            TaskbarAppearanceMode taskViewMode = TaskbarAppearanceMode.Normal)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                _enabled          = enabled;
                _desktopMode      = desktopMode;
                _color            = NormalizeColor(color);
                _opacity          = Math.Clamp(opacity, 0, 100);
                _dynamicMaximized = dynamicMaximized;
                _maximizedMode    = maximizedMode;
                _dynamicStartMenu = dynamicStartMenu;
                _startMenuMode    = startMenuMode;
                _dynamicSearch    = dynamicSearch;
                _searchMode       = searchMode;
                _dynamicTaskView  = dynamicTaskView;
                _taskViewMode     = taskViewMode;

                if (!_enabled)
                {
                    StopTimer();
                    if (_applied.Count > 0) RestoreTaskbars();
                    return;
                }

                _applied.Clear();
                ApplyCurrentAppearance();

                _timerTick = 0;
                // 300ms: fast enough to override XAML layer repaints on Win11
                _timer ??= new Timer(OnTimer, null,
                    TimeSpan.FromMilliseconds(300),
                    TimeSpan.FromMilliseconds(300));
            }
        }

        public void Refresh()
        {
            lock (_sync)
            {
                if (_disposed || !_enabled) return;
                _applied.Clear();
                ApplyCurrentAppearance();
            }
        }

        private void OnTimer(object? state)
        {
            lock (_sync)
            {
                if (_disposed || !_enabled) return;
                _timerTick++;
                ApplyCurrentAppearance();
            }
        }

        private void ApplyCurrentAppearance()
        {
            var taskbars = FindTaskbars();
            var maximizedMonitors = _dynamicMaximized ? FindMaximizedMonitors() : null;
            bool startOpen = _dynamicStartMenu && IsStartMenuOpen();
            bool searchOpen = _dynamicSearch && IsSearchOpen();
            bool taskViewOpen = _dynamicTaskView && IsTaskViewOpen();

            foreach (var handle in taskbars)
            {
                var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
                TaskbarAppearanceMode mode;
                if (startOpen)
                    mode = _startMenuMode;
                else if (searchOpen)
                    mode = _searchMode;
                else if (taskViewOpen)
                    mode = _taskViewMode;
                else if (maximizedMonitors?.Contains(monitor) == true)
                    mode = _maximizedMode;
                else
                    mode = _desktopMode;

                var color = ParseAbgr(_color, _opacity, mode);
                var desired = new AppliedAppearance(mode, color);

                // Dynamic states are polled frequently, but unchanged taskbars do not need a
                // forced DWM frame redraw every 300 ms. Reassert the same state every ~6 seconds
                // to recover from shell repaints while avoiding visible flicker and excess work.
                var periodicReassert = _timerTick == 0 || _timerTick % 20 == 0;
                if (!periodicReassert && _applied.TryGetValue(handle, out var current) && current == desired)
                    continue;

                if (Apply(handle, mode, color))
                    _applied[handle] = desired;
            }

            foreach (var oldHandle in _applied.Keys.Except(taskbars).ToList())
                _applied.Remove(oldHandle);
        }

        private static bool Apply(IntPtr taskbar, TaskbarAppearanceMode mode, uint color)
        {
            if (taskbar == IntPtr.Zero) return false;

            try
            {
                // ── Strategy 1: Reset DWM System Backdrop (Win11 22H2+) ──────────────
                // Setting DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_NONE tells DWM to skip
                // the opaque material layer that Win11 XAML taskbar paints over WCA.
                if (IsWin11_22H2)
                {
                    int backdropVal = mode == TaskbarAppearanceMode.Normal ? 0 : 1; // 0=AUTO, 1=NONE
                    DwmSetWindowAttribute(taskbar, DwmwaSystembackdropType, ref backdropVal, sizeof(int));
                    EnumChildWindows(taskbar, (child, _) =>
                    {
                        DwmSetWindowAttribute(child, DwmwaSystembackdropType, ref backdropVal, sizeof(int));
                        return true;
                    }, IntPtr.Zero);
                }

                // ── Strategy 2: DWM full-frame glass extension ────────────────────────
                // -1 margins instruct DWM to treat the entire client area as glass.
                var margins = new Margins { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                try { DwmExtendFrameIntoClientArea(taskbar, ref margins); } catch { }

                // ── Strategy 3: WCA Accent Policy ────────────────────────────────────
                // For Clear mode at 0% opacity, force GradientColor alpha to 0x00.
                var effectiveMode = mode;
                if (mode == TaskbarAppearanceMode.Blur && IsWin11_22H2)
                    effectiveMode = TaskbarAppearanceMode.Acrylic;

                // Force fully transparent ABGR for Clear mode
                var gradientColor = (mode == TaskbarAppearanceMode.Clear)
                    ? 0x00000000
                    : unchecked((int)color);

                var policy = new AccentPolicy
                {
                    AccentState = effectiveMode switch
                    {
                        TaskbarAppearanceMode.Normal  => AccentState.Disabled,
                        TaskbarAppearanceMode.Opaque  => AccentState.Gradient,
                        TaskbarAppearanceMode.Clear   => AccentState.TransparentGradient,
                        TaskbarAppearanceMode.Blur    => AccentState.BlurBehind,
                        TaskbarAppearanceMode.Acrylic => AccentState.AcrylicBlurBehind,
                        _                             => AccentState.Disabled
                    },
                    AccentFlags   = effectiveMode == TaskbarAppearanceMode.Normal ? 0 : 2,
                    GradientColor = gradientColor,
                    AnimationId   = 0
                };

                var policySize    = Marshal.SizeOf<AccentPolicy>();
                var policyPointer = Marshal.AllocHGlobal(policySize);
                bool applied;
                try
                {
                    Marshal.StructureToPtr(policy, policyPointer, false);
                    var data = new WindowCompositionAttributeData
                    {
                        Attribute  = WcaAccentPolicy,
                        Data       = policyPointer,
                        SizeOfData = policySize
                    };

                    applied = SetWindowCompositionAttribute(taskbar, ref data) != 0;

                    // Apply to all children (XAML Island, TrayNotifyWnd, ReBarWindow32…)
                    EnumChildWindows(taskbar, (child, _) =>
                    {
                        try
                        {
                            SetWindowCompositionAttribute(child, ref data);
                            DwmExtendFrameIntoClientArea(child, ref margins);
                        }
                        catch { }
                        return true;
                    }, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeHGlobal(policyPointer);
                }

                // ── Strategy 4: Force DWM re-composition ─────────────────────────────
                SetWindowPos(taskbar, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
                RedrawWindow(taskbar, IntPtr.Zero, IntPtr.Zero,
                    RdwInvalidate | RdwErase | RdwAllChildren | RdwFrame);

                if (mode == TaskbarAppearanceMode.Normal)
                    SendMessageTimeout(taskbar, WmThemeChanged, IntPtr.Zero, IntPtr.Zero,
                        SmtoAbortIfHung, 250, out _);

                return applied;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to apply taskbar appearance: {ex.Message}");
                return false;
            }
        }

        private void RestoreTaskbars()
        {
            foreach (var handle in FindTaskbars().Concat(_applied.Keys).Distinct())
            {
                if (IsWin11_22H2)
                {
                    int backdropAuto = 0; // DWMSBT_AUTO
                    try
                    {
                        DwmSetWindowAttribute(handle, DwmwaSystembackdropType, ref backdropAuto, sizeof(int));
                        EnumChildWindows(handle, (child, _) =>
                        {
                            DwmSetWindowAttribute(child, DwmwaSystembackdropType, ref backdropAuto, sizeof(int));
                            return true;
                        }, IntPtr.Zero);
                    }
                    catch { }
                }
                Apply(handle, TaskbarAppearanceMode.Normal, 0);
            }
            _applied.Clear();
        }

        private static HashSet<IntPtr> FindTaskbars()
        {
            var handles = new HashSet<IntPtr>();
            var primary = FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero)
                handles.Add(primary);

            EnumWindows((window, _) =>
            {
                var className = GetWindowClass(window);
                if (string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.Ordinal))
                    handles.Add(window);
                return true;
            }, IntPtr.Zero);
            return handles;
        }

        private static HashSet<IntPtr> FindMaximizedMonitors()
        {
            var monitors = new HashSet<IntPtr>();
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window) || IsIconic(window))
                    return true;
                if ((GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExToolWindow) != 0)
                    return true;
                if (DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;

                var className = GetWindowClass(window);
                if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
                    return true;
                if (!IsZoomed(window) && !CoversMonitor(window))
                    return true;
                var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
                if (monitor != IntPtr.Zero)
                    monitors.Add(monitor);
                return true;
            }, IntPtr.Zero);
            return monitors;
        }

        private static bool CoversMonitor(IntPtr window)
        {
            if (!GetWindowRect(window, out var windowRect))
                return false;
            var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
                return false;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
                return false;

            const int tolerance = 2;
            return windowRect.Left <= info.Monitor.Left + tolerance &&
                   windowRect.Top <= info.Monitor.Top + tolerance &&
                   windowRect.Right >= info.Monitor.Right - tolerance &&
                   windowRect.Bottom >= info.Monitor.Bottom - tolerance;
        }

        private static string GetWindowClass(IntPtr handle)
        {
            var builder = new StringBuilder(256);
            return GetClassName(handle, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
        }

        private static bool IsStartMenuOpen()
        {
            try
            {
                var startHwnd = FindWindow("Windows.UI.Core.CoreWindow", "Start");
                if (startHwnd != IntPtr.Zero && IsWindowVisible(startHwnd))
                {
                    if (DwmGetWindowAttribute(startHwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked == 0)
                        return true;
                }

                startHwnd = FindWindow("XamlExplorerHostIslandWindow", "Start");
                if (startHwnd != IntPtr.Zero && IsWindowVisible(startHwnd))
                {
                    if (DwmGetWindowAttribute(startHwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked == 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsSearchOpen()
        {
            try
            {
                var searchHwnd = FindWindow("Windows.UI.Core.CoreWindow", "Search");
                if (searchHwnd != IntPtr.Zero && IsWindowVisible(searchHwnd))
                {
                    if (DwmGetWindowAttribute(searchHwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked == 0)
                        return true;
                }

                searchHwnd = FindWindow("XamlExplorerHostIslandWindow", "Search");
                if (searchHwnd != IntPtr.Zero && IsWindowVisible(searchHwnd))
                {
                    if (DwmGetWindowAttribute(searchHwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked == 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsTaskViewOpen()
        {
            try
            {
                var taskViewHwnd = FindWindow("MultitaskingViewFrame", null);
                if (taskViewHwnd != IntPtr.Zero && IsWindowVisible(taskViewHwnd))
                {
                    if (DwmGetWindowAttribute(taskViewHwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked == 0)
                        return true;
                }
            }
            catch { }
            return false;
        }


        private static string NormalizeColor(string? color)
        {
            if (!string.IsNullOrWhiteSpace(color))
            {
                var value = color.Trim().TrimStart('#');
                if (value.Length == 6 && uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out _))
                    return $"#{value.ToUpperInvariant()}";
            }
            return "#000000";
        }

        private static uint ParseAbgr(string color, int opacity, TaskbarAppearanceMode mode)
        {
            var rgb = Convert.ToUInt32(color.TrimStart('#'), 16);
            var red = (rgb >> 16) & 0xFF;
            var green = (rgb >> 8) & 0xFF;
            var blue = rgb & 0xFF;
            var alpha = mode switch
            {
                TaskbarAppearanceMode.Normal => 0u,
                TaskbarAppearanceMode.Opaque => 255u,
                TaskbarAppearanceMode.Clear => (uint)Math.Clamp(opacity * 255 / 100, 0, 255),
                _ => (uint)Math.Clamp(opacity * 255 / 100, 1, 255)
            };
            return (alpha << 24) | (blue << 16) | (green << 8) | red;
        }

        private void StopTimer()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                StopTimer();
                if (_applied.Count > 0)
                    RestoreTaskbars();
                else
                    _applied.Clear();
                _disposed = true;
            }
        }

        private readonly record struct AppliedAppearance(TaskbarAppearanceMode Mode, uint Color);

        private enum AccentState
        {
            Disabled = 0,
            Gradient = 1,
            TransparentGradient = 2,
            BlurBehind = 3,
            AcrylicBlurBehind = 4
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect WorkArea;
            public uint Flags;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string? windowName);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern IntPtr GetWindowLong32(IntPtr window, int index);

        private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : GetWindowLong32(window, index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr window, ref WindowCompositionAttributeData data);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out IntPtr result);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins pMarInset);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
    }
}
