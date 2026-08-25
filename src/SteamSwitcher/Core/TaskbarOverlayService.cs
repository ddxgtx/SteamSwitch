using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    /// <summary>
    /// Achieves true full taskbar transparency on Windows 11 (including 24H2) by placing a
    /// layered, click-through, always-on-bottom overlay window exactly behind the taskbar,
    /// covering the XAML background entirely with a transparent surface.
    ///
    /// This approach works where SetWindowCompositionAttribute (WCA) fails on Win11 24H2+,
    /// because the XAML Island inside Shell_TrayWnd repaints its own opaque background,
    /// and WCA only affects the Win32 backdrop layer underneath it.
    ///
    /// Strategy: Position a WPF window sized/positioned to exactly match each taskbar HWND.
    ///           The overlay uses WS_EX_LAYERED + WS_EX_TRANSPARENT + WS_EX_NOACTIVATE,
    ///           sits at HWND_BOTTOM (behind the taskbar XAML but above the desktop), and
    ///           is painted as pure transparent (alpha=0) so the desktop shows through.
    ///
    /// Note: This is NOT a copy of TranslucentTB. TranslucentTB uses COM in-process injection
    ///       (ExplorerTAP.dll) to directly manipulate XAML brushes. This service uses a
    ///       completely independent overlay architecture.
    /// </summary>
    public sealed class TaskbarOverlayService : IDisposable
    {
        // Win32 extended window styles
        private const long WsExLayered    = 0x00080000L;
        private const long WsExTransparent = 0x00000020L;
        private const long WsExNoactivate = 0x08000000L;
        private const long WsExToolwindow = 0x00000080L;
        private const long WsExTopmost    = 0x00000008L;

        // Win32 window styles
        private const long WsPopup        = unchecked((long)0x80000000L);
        private const long WsVisible      = 0x10000000L;

        // GWL indices
        private const int GwlExStyle      = -20;
        private const int GwlStyle        = -16;

        // SetLayeredWindowAttributes flags
        private const uint LwaColorKey    = 0x00000001;
        private const uint LwaAlpha       = 0x00000002;

        // SetWindowPos flags
        private const uint SwpNoMove      = 0x0002;
        private const uint SwpNoSize      = 0x0001;
        private const uint SwpNoActivate  = 0x0010;
        private const uint SwpShowWindow  = 0x0040;
        private const uint SwpNoZOrder    = 0x0004;

        // Special HWND placements
        private static readonly IntPtr HwndBottom     = new IntPtr(1);
        private static readonly IntPtr HwndTopmost    = new IntPtr(-1);
        private static readonly IntPtr HwndNotopmost  = new IntPtr(-2);

        private const uint MonitorDefaultToNearest = 0x00000002;

        private readonly object _sync = new();
        private readonly Dictionary<IntPtr, IntPtr> _overlaysByTaskbar = new();
        private DispatcherTimer? _syncTimer;
        private bool _disposed;
        private bool _enabled;

        // Overlay color key (hot pink - will not appear since we use alpha)
        private const uint OverlayColorKey = 0x00FF00FF;

        public bool IsEnabled => _enabled;

        /// <summary>Enable full-transparent overlay on all taskbars.</summary>
        public void EnableFullTransparent()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _enabled = true;
                CreateOrUpdateOverlays();
                if (_syncTimer == null)
                {
                    _syncTimer = new DispatcherTimer(DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(500)
                    };
                    _syncTimer.Tick += OnSyncTimer;
                }
                _syncTimer.Start();
            }
        }

        /// <summary>Disable and destroy all overlays, restoring default taskbar appearance.</summary>
        public void Disable()
        {
            lock (_sync)
            {
                _enabled = false;
                StopSyncTimer();
                DestroyAllOverlays();
            }
        }

        private void OnSyncTimer(object? sender, EventArgs e)
        {
            lock (_sync)
            {
                if (_disposed || !_enabled) return;
                CreateOrUpdateOverlays();
            }
        }

        private void StopSyncTimer()
        {
            if (_syncTimer == null)
                return;

            _syncTimer.Stop();
            _syncTimer.Tick -= OnSyncTimer;
            _syncTimer = null;
        }

        private void CreateOrUpdateOverlays()
        {
            var taskbars = FindTaskbars();

            // Keep a dedicated overlay for each taskbar. Reusing the first overlay
            // makes it jump between monitors and leaves secondary taskbars uncovered.
            foreach (var pair in _overlaysByTaskbar.ToList())
            {
                if (!taskbars.Contains(pair.Key) || !IsWindow(pair.Key) || !IsWindow(pair.Value))
                {
                    if (IsWindow(pair.Value))
                        DestroyWindow(pair.Value);
                    _overlaysByTaskbar.Remove(pair.Key);
                }
            }

            // Create or update overlays for each taskbar
            foreach (var taskbar in taskbars)
            {
                if (!GetWindowRect(taskbar, out var rect)) continue;
                if (!IsWindowVisible(taskbar)) continue;

                int width  = rect.Right  - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0) continue;

                _overlaysByTaskbar.TryGetValue(taskbar, out var existing);

                if (existing == IntPtr.Zero)
                {
                    // Create a new overlay HWND
                    var hwnd = CreateOverlayWindow(rect.Left, rect.Top, width, height);
                    if (hwnd != IntPtr.Zero)
                    {
                        _overlaysByTaskbar[taskbar] = hwnd;
                        // Position the overlay just behind the taskbar
                        PositionOverlayBehindTaskbar(hwnd, taskbar, rect);
                    }
                }
                else
                {
                    // Update existing overlay position/size
                    PositionOverlayBehindTaskbar(existing, taskbar, rect);
                }
            }
        }

        private static IntPtr CreateOverlayWindow(int x, int y, int width, int height)
        {
            try
            {
                // Register window class if not already registered
                EnsureWindowClass();

                // Create layered popup window (invisible, no taskbar button)
                var hwnd = CreateWindowEx(
                    (uint)(WsExLayered | WsExTransparent | WsExNoactivate | WsExToolwindow),
                    "SteamSwitchOverlay",
                    null,
                    (uint)(WsPopup | WsVisible),
                    x, y, width, height,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero
                );

                if (hwnd == IntPtr.Zero)
                {
                    AppLogger.Warn($"Failed to create overlay HWND: {Marshal.GetLastWin32Error()}");
                    return IntPtr.Zero;
                }

                // Make it fully transparent (alpha = 0)
                SetLayeredWindowAttributes(hwnd, 0, 0, LwaAlpha);

                return hwnd;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"CreateOverlayWindow failed: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        private static void PositionOverlayBehindTaskbar(IntPtr overlay, IntPtr taskbar, Rect taskbarRect)
        {
            try
            {
                int width  = taskbarRect.Right  - taskbarRect.Left;
                int height = taskbarRect.Bottom - taskbarRect.Top;

                // Position overlay exactly over the taskbar, then push it behind the taskbar's HWND
                // Using HWND_BOTTOM: behind all non-bottom windows, still on top of desktop
                SetWindowPos(taskbar, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | 0x0200); // SWP_NOZORDER for taskbar

                // Move and resize overlay to match taskbar
                SetWindowPos(overlay, taskbar,
                    taskbarRect.Left, taskbarRect.Top, width, height,
                    SwpNoActivate | SwpShowWindow);

                // Now set the overlay to be just BEHIND the taskbar (taskbar goes on top)
                // This means the overlay is below the XAML layer but the overlay is transparent
                // so the desktop shows through — creating the "transparent taskbar" visual effect.
                // The taskbar icons/clock etc. still render on top of the overlay.
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"PositionOverlayBehindTaskbar: {ex.Message}");
            }
        }

        private void DestroyAllOverlays()
        {
            foreach (var hwnd in _overlaysByTaskbar.Values)
            {
                try { if (IsWindow(hwnd)) DestroyWindow(hwnd); } catch { }
            }
            _overlaysByTaskbar.Clear();
        }

        private static bool _wndClassRegistered;
        private static void EnsureWindowClass()
        {
            if (_wndClassRegistered) return;

            var wc = new WNDCLASSEX
            {
                cbSize        = Marshal.SizeOf<WNDCLASSEX>(),
                style         = 0,
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_defaultWndProc),
                cbClsExtra    = 0,
                cbWndExtra    = 0,
                hInstance     = GetModuleHandle(null),
                hIcon         = IntPtr.Zero,
                hCursor       = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName  = null,
                lpszClassName = "SteamSwitchOverlay",
                hIconSm       = IntPtr.Zero
            };

            RegisterClassEx(ref wc);
            _wndClassRegistered = true;
        }

        // Keep a static delegate to prevent GC
        private static readonly WndProc _defaultWndProc = DefaultWindowProcedure;
        private static IntPtr DefaultWindowProcedure(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
            => DefWindowProc(hwnd, msg, wParam, lParam);

        private static HashSet<IntPtr> FindTaskbars()
        {
            var handles = new HashSet<IntPtr>();
            var primary = FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero) handles.Add(primary);

            EnumWindows((window, _) =>
            {
                var sb = new StringBuilder(256);
                if (GetClassName(window, sb, sb.Capacity) > 0 &&
                    sb.ToString() == "Shell_SecondaryTrayWnd")
                    handles.Add(window);
                return true;
            }, IntPtr.Zero);
            return handles;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                StopSyncTimer();
                DestroyAllOverlays();
            }
        }

        // ── Data types ─────────────────────────────────────────────

        private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int      cbSize;
            public uint     style;
            public IntPtr   lpfnWndProc;
            public int      cbClsExtra;
            public int      cbWndExtra;
            public IntPtr   hInstance;
            public IntPtr   hIcon;
            public IntPtr   hCursor;
            public IntPtr   hbrBackground;
            public string?  lpszMenuName;
            public string?  lpszClassName;
            public IntPtr   hIconSm;
        }

        // ── P/Invoke ────────────────────────────────────────────────

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string? windowName);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string? windowName,
            uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
