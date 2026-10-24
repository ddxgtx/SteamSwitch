using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    /// <summary>Controls Explorer's desktop icon list without changing Explorer settings.</summary>
    public sealed class DesktopIconService : IDisposable
    {
        private const int SwHide = 0;
        private const int SwShow = 5;
        private readonly object _sync = new();
        private Timer? _timer;
        private DesktopEnhanceSettings _settings = new();
        private bool _iconsHidden;
        private bool _desktopListHiddenByApp;
        private long _timerStartedAt;
        private POINT _hiddenCursor;
        private POINT _lastDesktopActivityCursor;
        private long _lastDesktopActivityAt;
        private bool _hasDesktopActivityCursor;
        private IntPtr _mouseHook;
        private readonly HookProc _mouseProc;
        private readonly Action _requestExitRecovery;
        private volatile bool _disposed;
        private int _applicationExitRequested;
        // 低级鼠标钩子只能在"自带消息循环"的线程上安装。原先挂在 UI 线程，
        // 界面一旦阻塞超过 LowLevelHooksTimeout，Windows 会静默摘钩且不再恢复。
        private Thread? _mouseHookThread;
        private System.Windows.Threading.Dispatcher? _mouseHookDispatcher;
        private readonly ManualResetEventSlim _mouseHookReady = new(false);
        private volatile bool _hookRequested;
        private volatile bool _hookInstalled;
        private int _lastRaisedCountdown = int.MinValue;
        private static string RecoveryMarkerPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamSwitch",
            "desktop-icons-hidden.lock");

        public event EventHandler<bool>? IconVisibilityChanged;
        public event EventHandler<int>? CountdownChanged;

        public bool IconsHidden => _iconsHidden;

        /// <summary>
        /// Raises CountdownChanged only when the value actually changes so
        /// listeners are not flooded every 250ms with an identical countdown.
        /// </summary>
        private void RaiseCountdownChanged(int value)
        {
            if (_lastRaisedCountdown == value) return;
            _lastRaisedCountdown = value;
            CountdownChanged?.Invoke(this, value);
        }

        public bool CanControlDesktopIcons
        {
            get
            {
                lock (_sync)
                    return !_disposed && FindDesktopIconList() != IntPtr.Zero;
            }
        }

        public DesktopIconService() : this(null)
        {
        }

        internal DesktopIconService(Action? requestExitRecovery)
        {
            _mouseProc = MouseHookCallback;
            _requestExitRecovery = requestExitRecovery ?? RequestIconRecoveryForApplicationExit;
        }

        public void Configure(DesktopEnhanceSettings settings)
        {
            lock (_sync)
            {
                if (_disposed) return;
                _settings = settings ?? new DesktopEnhanceSettings();
                _settings.IdleSeconds = Math.Clamp(_settings.IdleSeconds, 5, 300);
                _settings.AnimationDuration = Math.Clamp(_settings.AnimationDuration, 200, 1000);
                _timerStartedAt = Environment.TickCount64;
                _lastDesktopActivityAt = _timerStartedAt;
                _hasDesktopActivityCursor = false;
                _timer ??= new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(_settings.EnableAutoHideIcons || File.Exists(RecoveryMarkerPath)
                    ? 250
                    : Timeout.Infinite, 250);
                UpdateDesktopClickHook();
                if (!_settings.EnableAutoHideIcons && (_iconsHidden || _desktopListHiddenByApp))
                    ShowIconsCore();
                _lastRaisedCountdown = int.MinValue;
                RaiseCountdownChanged(
                    _settings.EnableAutoHideIcons && _settings.HideMode != DesktopIconHideMode.Manual
                        ? _settings.IdleSeconds : 0);
            }
        }

        public bool HideIcons()
        {
            lock (_sync)
            {
                if (_disposed || !_settings.EnableAutoHideIcons) return false;
                var iconList = FindDesktopIconList();
                if (iconList == IntPtr.Zero)
                {
                    AppLogger.Warn("Explorer desktop host was not found. It may still be starting.");
                    return false;
                }
                if (_iconsHidden) return true;
                GetCursorPos(out _hiddenCursor);
                if (!CreateRecoveryMarker()) return false;
                // Hide only Explorer's desktop list view without refreshing open file windows.
                ShowWindow(iconList, SwHide);
                _desktopListHiddenByApp = !IsWindowVisible(iconList);
                _iconsHidden = _desktopListHiddenByApp;
                if (!_iconsHidden) DeleteRecoveryMarker();
                if (_iconsHidden)
                    IconVisibilityChanged?.Invoke(this, false);
                RaiseCountdownChanged(0);
                return _iconsHidden;
            }
        }

        public void ShowIcons()
        {
            lock (_sync) ShowIconsCore();
        }

        public bool RecoverIconsAfterUnexpectedShutdown(bool legacyHiddenByApp = false)
        {
            lock (_sync)
            {
                if (_disposed) return false;
                if (!File.Exists(RecoveryMarkerPath) && !legacyHiddenByApp) return false;
                if (!File.Exists(RecoveryMarkerPath) && legacyHiddenByApp)
                    CreateRecoveryMarker();
                if (!AreDesktopIconsHidden())
                {
                    DeleteRecoveryMarker();
                    return true;
                }
                var iconList = FindDesktopIconList();
                if (iconList == IntPtr.Zero)
                    return false;

                ShowWindow(iconList, SwShow);
                var recovered = IsWindowVisible(iconList);
                if (recovered)
                {
                    _iconsHidden = false;
                    _desktopListHiddenByApp = false;
                    DeleteRecoveryMarker();
                }
                return recovered;
            }
        }

        private void ShowIconsCore()
        {
            var iconList = FindDesktopIconList();
            if ((_iconsHidden || _desktopListHiddenByApp || File.Exists(RecoveryMarkerPath)) && iconList != IntPtr.Zero)
                ShowWindow(iconList, SwShow);
            if (iconList != IntPtr.Zero && IsWindowVisible(iconList) &&
                (_iconsHidden || _desktopListHiddenByApp || File.Exists(RecoveryMarkerPath)))
            {
                _iconsHidden = false;
                _desktopListHiddenByApp = false;
                DeleteRecoveryMarker();
                _timerStartedAt = Environment.TickCount64;
                _lastDesktopActivityAt = _timerStartedAt;
                IconVisibilityChanged?.Invoke(this, true);
                RaiseCountdownChanged(
                    _settings.EnableAutoHideIcons && _settings.HideMode != DesktopIconHideMode.Manual
                        ? _settings.IdleSeconds : 0);
            }
        }

        private void Tick()
        {
            lock (_sync)
            {
                if (_disposed) return;
                if (File.Exists(RecoveryMarkerPath) && !_iconsHidden && !_desktopListHiddenByApp)
                {
                    RecoverIconsAfterUnexpectedShutdown();
                    if (!_settings.EnableAutoHideIcons)
                    {
                        RelaxedTick();
                        return;
                    }
                }
                if (!_settings.EnableAutoHideIcons)
                {
                    RelaxedTick();
                    return;
                }
                if (_iconsHidden)
                {
                    // Explorer can restart and recreate the desktop host. Reacquire it on every
                    // tick and restart the delay when its recreated icon host becomes visible.
                    var iconList = FindDesktopIconList();
                    if (iconList == IntPtr.Zero || IsWindowVisible(iconList))
                    {
                        _iconsHidden = false;
                        _desktopListHiddenByApp = false;
                        _timerStartedAt = Environment.TickCount64;
                        // 不删恢复标记的话，Configure 会以为还有一次未完成的隐藏，
                        // 在自动隐藏已关闭的情况下继续以 250ms 轮询。
                        DeleteRecoveryMarker();
                        IconVisibilityChanged?.Invoke(this, true);
                        return;
                    }
                    CheckRestore();
                    return;
                }
                if (_settings.HideMode == DesktopIconHideMode.Manual)
                {
                    RaiseCountdownChanged(0);
                    RelaxedTick();
                    return;
                }
                var elapsedSeconds = _settings.HideMode == DesktopIconHideMode.Timed
                    ? (int)((Environment.TickCount64 - _timerStartedAt) / 1000)
                    : GetDesktopMouseIdleSeconds();
                var remaining = Math.Max(0, _settings.IdleSeconds - elapsedSeconds);
                RaiseCountdownChanged(remaining);
                if (elapsedSeconds >= _settings.IdleSeconds) HideIcons();
            }
        }

        /// <summary>
        /// When auto-hide is off or manual (no countdown to service), slow the
        /// 250ms poll down to 1s so the timer is not a background storm.
        /// </summary>
        private void RelaxedTick()
        {
            _timer?.Change(1000, 1000);
        }

        // Smart mode deliberately ignores keyboard activity and pointer movement over apps.
        // Only a pointer position change while it is above the desktop restarts the countdown.
        private int GetDesktopMouseIdleSeconds()
        {
            var now = Environment.TickCount64;
            GetCursorPos(out var cursor);
            if (IsDesktopWindow(WindowFromPoint(cursor)))
            {
                if (!_hasDesktopActivityCursor || cursor.X != _lastDesktopActivityCursor.X || cursor.Y != _lastDesktopActivityCursor.Y)
                {
                    _lastDesktopActivityCursor = cursor;
                    _lastDesktopActivityAt = now;
                    _hasDesktopActivityCursor = true;
                }
            }
            return (int)Math.Max(0, (now - _lastDesktopActivityAt) / 1000);
        }

        private void CheckRestore()
        {
            GetCursorPos(out var cursor);
            if (_settings.RestoreByMouseMove && IsDesktopWindow(WindowFromPoint(cursor)) &&
                (cursor.X != _hiddenCursor.X || cursor.Y != _hiddenCursor.Y))
                ShowIconsCore();
        }

        private void UpdateDesktopClickHook()
        {
            var needsHook = _settings.EnableAutoHideIcons &&
                            (_settings.ToggleIconsOnDesktopClick || _settings.RestoreByTaskbarClick);
            _hookRequested = needsHook;
            if (!needsHook && !_hookInstalled && _mouseHookThread == null)
                return;

            // 钩子只在专用线程上装/卸：UI 线程只负责通知。
            EnsureMouseHookThread();
            _mouseHookDispatcher?.BeginInvoke(new Action(SyncMouseHook));
        }

        private void EnsureMouseHookThread()
        {
            if (_mouseHookThread != null) return;
            _mouseHookThread = new Thread(MouseHookThreadProc)
            {
                IsBackground = true,
                Name = "SteamSwitch.DesktopMouseHook"
            };
            _mouseHookThread.SetApartmentState(ApartmentState.STA);
            _mouseHookThread.Start();
            _mouseHookReady.Wait(TimeSpan.FromSeconds(5));
        }

        private void MouseHookThreadProc()
        {
            // Dispatcher.Run 在这个线程上起一个消息循环，满足 WH_MOUSE_LL 的要求。
            // 界面再忙也不会影响它，Windows 不会因为超时把钩子摘掉。
            _mouseHookDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _mouseHookReady.Set();
            System.Windows.Threading.Dispatcher.Run();
        }

        /// <summary>只在钩子线程执行：_mouseHook 的读写权归这个线程。</summary>
        private void SyncMouseHook()
        {
            if (_disposed)
            {
                UninstallMouseHookCore();
                return;
            }

            if (_hookRequested && !_hookInstalled)
            {
                _mouseHook = SetWindowsHookEx(14, _mouseProc, GetModuleHandle(null), 0);
                if (_mouseHook == IntPtr.Zero)
                    AppLogger.Warn($"Unable to install desktop mouse hook. Win32Error={Marshal.GetLastWin32Error()}");
                else
                    _hookInstalled = true;
            }
            else if (!_hookRequested && _hookInstalled)
            {
                UninstallMouseHookCore();
            }
        }

        private void UninstallMouseHookCore()
        {
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            _hookInstalled = false;
        }

        private void StopMouseHookThread()
        {
            _hookRequested = false;
            var dispatcher = _mouseHookDispatcher;
            if (dispatcher == null) return;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            try
            {
                dispatcher.Invoke(
                    new Action(() =>
                    {
                        UninstallMouseHookCore();
                        dispatcher.InvokeShutdown();
                    }),
                    System.Windows.Threading.DispatcherPriority.Send,
                    CancellationToken.None,
                    TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to stop the desktop mouse hook thread cleanly: {ex.Message}");
                var leftover = Interlocked.Exchange(ref _mouseHook, IntPtr.Zero);
                if (leftover != IntPtr.Zero) UnhookWindowsHookEx(leftover);
                _hookInstalled = false;
            }
        }

        private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            const int WmLButtonUp = 0x0202;
            if (code >= 0 && wParam.ToInt32() == WmLButtonUp)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var target = WindowFromPoint(info.pt);
                var isDesktop = IsDesktopWindow(target);
                var isTaskbar = IsTaskbarWindow(target);
                if (!_settings.EnableAutoHideIcons)
                    return CallNextHookEx(_mouseHook, code, wParam, lParam);
                if ((isDesktop && _settings.ToggleIconsOnDesktopClick) ||
                    (isTaskbar && _settings.RestoreByTaskbarClick && _iconsHidden))
                    ThreadPool.QueueUserWorkItem(_ => HandleGlobalClick(isDesktop, isTaskbar));
            }
            return CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        private void HandleGlobalClick(bool isDesktop, bool isTaskbar)
        {
            lock (_sync)
            {
                if (isTaskbar && _settings.RestoreByTaskbarClick && _iconsHidden)
                    ShowIconsCore();
                else if (_settings.EnableAutoHideIcons && isDesktop && _settings.ToggleIconsOnDesktopClick)
                {
                    if (_iconsHidden) ShowIconsCore();
                    else HideIcons();
                }
            }
        }

        private static bool IsDesktopWindow(IntPtr handle)
        {
            var sawDesktopComponent = false;
            for (var current = handle; current != IntPtr.Zero; current = GetParent(current))
            {
                var name = GetClassName(current);
                if (name is "SHELLDLL_DefView" or "SysListView32")
                    sawDesktopComponent = true;
                if (name is "Progman" or "WorkerW")
                    return sawDesktopComponent || current == handle;
            }
            return false;
        }

        private static bool IsTaskbarWindow(IntPtr handle)
        {
            for (var current = handle; current != IntPtr.Zero; current = GetParent(current))
            {
                if (GetClassName(current) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            }
            return false;
        }

        private static IntPtr FindDesktopIconList()
        {
            var progman = FindWindow("Progman", null);
            var list = FindDesktopIconListIn(progman);
            if (list != IntPtr.Zero) return list;
            IntPtr found = IntPtr.Zero;
            EnumWindows((top, _) =>
            {
                found = FindDesktopIconListIn(top);
                return found == IntPtr.Zero;
            }, IntPtr.Zero);
            return found;
        }

        private static bool AreDesktopIconsHidden()
        {
            var list = FindDesktopIconList();
            if (list != IntPtr.Zero) return !IsWindowVisible(list);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", writable: false);
                return Convert.ToInt32(key?.GetValue("HideIcons", 0) ?? 0) != 0;
            }
            catch { return false; }
        }

        private static IntPtr FindDesktopIconListIn(IntPtr root)
        {
            if (root == IntPtr.Zero) return IntPtr.Zero;
            var rootClass = GetClassName(root);
            if (rootClass is not ("Progman" or "WorkerW")) return IntPtr.Zero;

            IntPtr shellView = IntPtr.Zero;
            EnumChildWindows(root, (child, _) =>
            {
                if (GetClassName(child) != "SHELLDLL_DefView") return true;
                shellView = child;
                return false;
            }, IntPtr.Zero);
            if (shellView == IntPtr.Zero) return IntPtr.Zero;

            IntPtr found = IntPtr.Zero;
            EnumChildWindows(shellView, (child, _) =>
            {
                if (GetClassName(child) != "SysListView32") return true;
                found = child;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static string GetClassName(IntPtr handle)
        {
            var buffer = new char[128];
            var length = GetClassName(handle, buffer, buffer.Length);
            return length > 0 ? new string(buffer, 0, length) : string.Empty;
        }

        private static bool CreateRecoveryMarker()
        {
            var markerPath = RecoveryMarkerPath;
            var directory = Path.GetDirectoryName(markerPath)!;
            var temporaryPath = markerPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(Environment.ProcessId);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, markerPath, overwrite: true);
                TrySpawnOutOfProcessGuard();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unable to create the desktop icon recovery marker; icons will remain visible.", ex);
                return false;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }

        private static void TrySpawnOutOfProcessGuard()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) ||
                    !Path.GetFileName(exePath).StartsWith("SteamSwitch", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(exePath))
                {
                    return;
                }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--desktop-icon-guard {Environment.ProcessId}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to start desktop icon guard process: {ex.Message}");
            }
        }

        public static bool TryEmergencyRestoreIconsStatic()
        {
            try
            {
                var iconList = FindDesktopIconList();
                if (iconList != IntPtr.Zero)
                {
                    ShowWindowAsync(iconList, SwShow);
                    ShowWindow(iconList, SwShow);
                }
                if (iconList != IntPtr.Zero && IsWindowVisible(iconList))
                {
                    DeleteRecoveryMarker();
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static void RunOutOfProcessGuard(int parentPid)
        {
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
                while (!parent.WaitForExit(400))
                {
                    if (!File.Exists(RecoveryMarkerPath))
                        return;
                }
            }
            catch
            {
                // Parent process has already exited or cannot be opened.
            }

            if (File.Exists(RecoveryMarkerPath) || AreDesktopIconsHidden())
            {
                TryEmergencyRestoreIconsStatic();
            }
        }

        private static void DeleteRecoveryMarker()
        {
            try { if (File.Exists(RecoveryMarkerPath)) File.Delete(RecoveryMarkerPath); }
            catch (Exception ex) { AppLogger.Warn($"Unable to remove the desktop icon recovery marker: {ex.Message}"); }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
                StopMouseHookThread();
                ShowIconsCore();
            }
        }

        /// <summary>
        /// Shutdown must not wait for a timer callback holding _sync while Explorer
        /// is unresponsive. Stop future work and queue recovery without waiting for
        /// Explorer to process it; normal interactive show/hide semantics are unchanged.
        /// </summary>
        public void DisposeForApplicationExit()
        {
            if (Interlocked.Exchange(ref _applicationExitRequested, 1) != 0) return;
            _disposed = true;
            var timer = Interlocked.Exchange(ref _timer, null);
            _hookRequested = false;
            ExitCleanup.Run(
                ("desktop monitoring timer", () => timer?.Dispose()),
                ("desktop mouse hook", () =>
                {
                    // 退出路径不等待：直接摘钩，再异步关掉消息循环。
                    var mouseHook = Interlocked.Exchange(ref _mouseHook, IntPtr.Zero);
                    if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
                    _hookInstalled = false;
                    var dispatcher = _mouseHookDispatcher;
                    if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                        dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
                }),
                ("asynchronous desktop icon recovery", _requestExitRecovery));
            GC.SuppressFinalize(this);
        }

        private void RequestIconRecoveryForApplicationExit()
        {
            if (!_iconsHidden && !_desktopListHiddenByApp && !File.Exists(RecoveryMarkerPath)) return;
            var iconList = FindDesktopIconList();
            if (iconList != IntPtr.Zero) ShowWindowAsync(iconList, SwShow);
            // A queued request is not proof of visibility. Preserve the marker;
            // normal startup recovery removes it only after confirming visibility.
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetClassName(IntPtr hWnd, [Out] char[] className, int maxCount);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    }
}
