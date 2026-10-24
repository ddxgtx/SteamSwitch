using System.Diagnostics;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;
using SteamSwitcher.ViewModels;
using SteamSwitcher.Views;

namespace SteamSwitcher
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly bool _previewMode;
        private readonly TrayIconService _trayIcon;
        private readonly DesktopIconService _desktopIconService = new();
        private TaskbarBandWindow? _taskbarBand;
        private DesktopFloatingWindow? _desktopFloatingWindow;
        private Views.TaskbarFlyoutWindow? _notificationFlyout;
        private System.Windows.Threading.DispatcherTimer? _notificationFlyoutTimer;
        private string _lastDiagnosticReport = string.Empty;
        private CancellationTokenSource? _lyricsDiagnosticCts;
        private GameSaveCenterWindow? _gameSaveCenterWindow;
        private DragDropHelper? _quickLaunchDragDrop;
        private bool _gameSearchPlaceholder = true;
        private bool _accountSearchPlaceholder = true;
        private string _gameFilterMode = "All";
        private bool _exitRequested;
        private bool _shutdownStarted;
        private bool _isInteractiveSession;
        private bool _isInitialized;
        private Task? _initializationTask;
        private readonly Queue<string[]> _pendingExternalRequests = new();
        private readonly SemaphoreSlim _externalRequestGate = new(1, 1);
        private readonly SemaphoreSlim _accountInteractionGate = new(1, 1);
        private const string GameSearchHint = "搜索游戏、AppID 或启动账号...";
        private const string AccountSearchHint = "搜索昵称、登录名或 SteamID...";
        private string _accountFilterMode = "All";
        private string _accountSortMode = "Recent";
        private bool _accountGridMode;
        private bool _updateDialogOpen;
        private Style? _accountListItemStyle;
        private Style? _accountGridItemStyle;
        private HwndSource? _hwndSource;
        private readonly System.Windows.Threading.DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(2800) };

        private void PostToUi(Action action)
        {
            if (_shutdownStarted || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            try { Dispatcher.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        // 页面切换淡入：窗口加载完成后的导航播放一次轻量渐显，启动过程保持瞬时切换。
        private void PlayViewEnterAnimation(FrameworkElement view)
        {
            if (view is null || !IsLoaded) return;
            try
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var duration = new Duration(TimeSpan.FromMilliseconds(180));
                view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            }
            catch (InvalidOperationException) { }
        }

        // 操作反馈改为窗口内右上角瞬时提示，替代原先底部常驻状态栏。
        private string? _pendingToastText;

        private void OnToastTimerTick(object? sender, EventArgs e) => HideToast();

        private void OnViewModelToastPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_shutdownStarted || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            if (!Dispatcher.CheckAccess())
            {
                PostToUi(() => OnViewModelToastPropertyChanged(sender, e));
                return;
            }

            if (e.PropertyName == nameof(MainViewModel.StatusText))
            {
                if (_viewModel == null) return;
                // 加载遮罩已在显示时，先缓存最终结果，等加载结束再弹提示，避免被遮罩吞掉。
                _pendingToastText = _viewModel.StatusText;
                if (!_viewModel.IsLoading)
                    ShowStatusToast();
            }
            else if (e.PropertyName == nameof(MainViewModel.IsLoading))
            {
                if (_viewModel != null && !_viewModel.IsLoading && !string.IsNullOrEmpty(_pendingToastText))
                {
                    ShowStatusToast();
                    _pendingToastText = null;
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.TaskbarLyricsFontFamily))
            {
                SyncLyricsFontCombos();
            }
            else if (e.PropertyName is nameof(MainViewModel.HasFailedAccountSwitch)
                     or nameof(MainViewModel.HasSettingsSaveError)
                     or nameof(MainViewModel.HasRecentBackupExport)
                     or nameof(MainViewModel.SettingsSaveActionText))
            {
                if (_viewModel != null && !_viewModel.IsLoading &&
                    (_viewModel.HasSettingsSaveError || _viewModel.HasFailedAccountSwitch))
                {
                    ShowStatusToast();
                }
                else if (ToastHost.Visibility == Visibility.Visible)
                {
                    RefreshToastAction();
                }
            }
        }

        private void ShowStatusToast()
        {
            if (_viewModel == null || _viewModel.IsLoading || string.IsNullOrWhiteSpace(_viewModel.StatusText))
                return;
            if (!_viewModel.HasSettingsSaveError &&
                !_viewModel.HasFailedAccountSwitch &&
                !_viewModel.HasRecentBackupExport &&
                (_viewModel.StatusText == "就绪" ||
                 _viewModel.StatusText.EndsWith("· 界面预览", StringComparison.Ordinal)))
            {
                HideToast();
                return;
            }
            ToastText.Text = _viewModel.StatusText;
            ToastHost.Visibility = Visibility.Visible;
            RefreshToastAction();
        }

        private void RefreshToastAction()
        {
            if (_viewModel == null)
            {
                ToastActionButton.Visibility = Visibility.Collapsed;
                _toastTimer.Stop();
                return;
            }
            var isError = _viewModel.HasSettingsSaveError || _viewModel.HasFailedAccountSwitch;
            ToastHost.BorderBrush = (Brush)FindResource(isError ? "DangerFgBrush" : "AccentBorderBrush");
            if (ToastIconBadge != null && ToastIcon != null)
            {
                ToastIconBadge.Background = (Brush)FindResource(isError ? "DangerBgBrush" : "AccentBgBrush");
                ToastIcon.Foreground = (Brush)FindResource(isError ? "DangerFgBrush" : "AccentBrush");
                ToastIcon.Text = isError ? "\uE783" : "\uE946";
            }
            if (_viewModel.HasSettingsSaveError)
            {
                ToastActionButton.Content = _viewModel.SettingsSaveActionText;
                ToastActionButton.Visibility = Visibility.Visible;
            }
            else if (_viewModel.HasFailedAccountSwitch)
            {
                ToastActionButton.Content = "重试切换";
                ToastActionButton.Visibility = Visibility.Visible;
            }
            else if (_viewModel.HasRecentBackupExport)
            {
                ToastActionButton.Content = "定位备份";
                ToastActionButton.Visibility = Visibility.Visible;
            }
            else
            {
                ToastActionButton.Visibility = Visibility.Collapsed;
            }

            _toastTimer.Stop();
            // 持久错误态（设置未保存/账号切换失败）保持提示可见，直到恢复成功或用户手动关闭，避免丢失重试入口。
            if (!isError && ToastHost.Visibility == Visibility.Visible)
            {
                _toastTimer.Interval = ToastActionButton.Visibility == Visibility.Visible
                    ? TimeSpan.FromMilliseconds(6000)
                    : TimeSpan.FromMilliseconds(2800);
                _toastTimer.Start();
            }
        }

        private void HideToast()
        {
            ToastHost.Visibility = Visibility.Collapsed;
            _toastTimer.Stop();
        }

        private void ShowTaskbarNotification(string message, int timeoutMs = 1800, Models.SteamAccount? accountOverride = null)
        {
            if (_previewMode || _shutdownStarted || string.IsNullOrWhiteSpace(message))
                return;

            PostToUi(() =>
            {
                if (_previewMode || _shutdownStarted)
                    return;

                try
                {
                    string? avatarPath = accountOverride?.AvatarPath;
                    if (string.IsNullOrWhiteSpace(avatarPath))
                    {
                        var accountManager = _viewModel.GetAccountManager();
                        var matchedAccount = accountManager.Accounts.FirstOrDefault(a =>
                            !string.IsNullOrWhiteSpace(a.PersonaName)
                            && message.Contains(a.PersonaName, StringComparison.OrdinalIgnoreCase));
                        if (matchedAccount == null && (message.Contains("账号") || message.Contains("切换到") || message.Contains("当前已是")))
                            matchedAccount = accountManager.CurrentAccount;
                        avatarPath = matchedAccount?.AvatarPath;
                    }

                    Point center;
                    double anchorTop;
                    if (_taskbarBand != null && _taskbarBand.TryGetNotificationAnchor(out var bandCenter, out var bandTop))
                    {
                        center = bandCenter;
                        anchorTop = bandTop;
                    }
                    else if (_desktopFloatingWindow != null && _desktopFloatingWindow.IsVisible && _desktopFloatingWindow.ActualWidth > 0)
                    {
                        center = new Point(
                            _desktopFloatingWindow.Left + (_desktopFloatingWindow.ActualWidth / 2.0),
                            _desktopFloatingWindow.Top);
                        anchorTop = _desktopFloatingWindow.Top;
                    }
                    else
                    {
                        var workArea = SystemParameters.WorkArea;
                        center = new Point(workArea.Right - 140, workArea.Bottom - 2);
                        anchorTop = workArea.Bottom - 2;
                    }

                    _notificationFlyout ??= new Views.TaskbarFlyoutWindow();
                    _notificationFlyout.ShowNotificationBubble(center, anchorTop, message, avatarPath);

                    if (_notificationFlyoutTimer == null)
                    {
                        _notificationFlyoutTimer = new System.Windows.Threading.DispatcherTimer();
                        _notificationFlyoutTimer.Tick += (_, _) =>
                        {
                            _notificationFlyoutTimer.Stop();
                            _notificationFlyout?.HideFlyout();
                        };
                    }

                    _notificationFlyoutTimer.Stop();
                    _notificationFlyoutTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1600, 3600));
                    _notificationFlyoutTimer.Start();
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Failed to display taskbar notification bubble.", ex);
                }
            });
        }

        private void ToastActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;
            if (_viewModel.HasSettingsSaveError)
            {
                RetrySettingsSave_Click(sender, e);
            }
            else if (_viewModel.HasFailedAccountSwitch)
            {
                HideToast();
                RetryAccountSwitch_Click(sender, e);
            }
            else if (_viewModel.HasRecentBackupExport)
            {
                RevealRecentBackupExport_Click(sender, e);
            }
        }

        private void ToastCloseButton_Click(object sender, RoutedEventArgs e) => HideToast();

        public MainWindow(bool startHidden = false, string[]? startupArguments = null)
            : this(CompositionRoot.CreateMainViewModel(), startHidden, startupArguments, previewMode: false)
        {
        }

        internal MainWindow(MainViewModel viewModel, bool previewMode)
            : this(viewModel, false, null, previewMode)
        {
        }

        private MainWindow(MainViewModel viewModel, bool startHidden, string[]? startupArguments, bool previewMode)
        {
            _previewMode = previewMode;
            _isInteractiveSession = !startHidden;
            var initialArguments = startupArguments ?? Array.Empty<string>();
            if (ContainsActionRequest(initialArguments))
                _pendingExternalRequests.Enqueue(initialArguments.ToArray());
            _viewModel = viewModel;
            DataContext = _viewModel;

            InitializeComponent();
            SyncLyricsFontCombos();
            _accountListItemStyle = AccountListBox.ItemContainerStyle;
            ApplyAccountSort();
            InitializeAccountLibraryFeedback();
            _viewModel.PropertyChanged += OnViewModelToastPropertyChanged;
            _toastTimer.Tick += OnToastTimerTick;
            Closing += Window_Closing;
            // Must subscribe after InitializeComponent: WindowChromeWorker registers its
            // own SourceInitialized handler while the XAML WindowChrome is applied, and
            // HwndSource invokes hooks in reverse registration order. Subscribing earlier
            // (or via a XAML attribute, which the parser wires during InitializeComponent)
            // puts our WndProc behind WindowChrome's, and chrome's WM_NCHITTEST always
            // claims the message — the maximize button would never report HTMAXBUTTON.
            // The preview path below must not skip it: offscreen previews need the same
            // chrome hit-testing pipeline for smoke assertions.
            SourceInitialized += OnSourceInitialized;
            if (previewMode)
            {
                // Offscreen rendering must not alter Explorer, the tray, or user configuration.
                _trayIcon = new TrayIconService(this, createNativeIcon: false);
                return;
            }
            _desktopIconService.IconVisibilityChanged += OnDesktopIconVisibilityChanged;
            _desktopIconService.CountdownChanged += OnDesktopIconCountdownChanged;
            if (_desktopIconService.RecoverIconsAfterUnexpectedShutdown(_viewModel.DesktopIconsHiddenByApp))
                _viewModel.DesktopIconsHiddenByApp = false;
            SyncDesktopEnhanceControls();
            ConfigureDesktopEnhance();

            _trayIcon = new TrayIconService(this);
            _trayIcon.AccountSelected += OnTrayAccountSelected;
            _trayIcon.LaunchSteamRequested += OnTrayLaunchSteamRequested;
            _trayIcon.ToggleTaskbarLyricsRequested += (s, enabled) => SetTaskbarLyricsEnabled(enabled);
            _trayIcon.EmergencyRestoreRequested += (s, e) => EmergencyRestore_Click(this, new RoutedEventArgs());
            _trayIcon.ExitRequested += (s, e) => RequestExit();
            _trayIcon.NotificationRequested += (s, args) => ShowTaskbarNotification(args.Message, args.Timeout);
            _viewModel.GetAccountManager().CurrentAccountChanged += OnCurrentAccountChanged;
            _viewModel.GetAccountManager().ConfirmForceCloseAfterTimeoutAsync = ConfirmForceCloseAfterTimeoutAsync;

            _viewModel.NotificationRequested += (s, message) =>
            {
                ShowTaskbarNotification(message, 1800);
            };

            _viewModel.UpdateAvailable += (s, updateInfo) =>
            {
                PostToUi(() =>
                {
                    if (!_isInteractiveSession)
                    {
                        ShowTaskbarNotification("发现新版本，可打开主界面检查更新", 2400);
                        return;
                    }

                    // Prevent stacked modal UpdateDialog instances if the event
                    // fires again while a previous dialog is still open.
                    if (_updateDialogOpen)
                    {
                        ShowTaskbarNotification("更新提示已打开", 1500);
                        return;
                    }

                    _updateDialogOpen = true;
                    try
                    {
                        using var updateService = new UpdateService();
                        var dialog = new Views.UpdateDialog(updateService, updateInfo);
                        if (IsVisible)
                            dialog.Owner = this;
                        dialog.ShowDialog();
                    }
                    finally
                    {
                        _updateDialogOpen = false;
                    }
                });
            };

            Loaded += MainWindow_Loaded;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            SizeChanged += MainWindow_SizeChanged;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            ApplyNativeWindowAppearance();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                _hwndSource = HwndSource.FromHwnd(hwnd);
                _hwndSource?.AddHook(WndProc);
            }
            RestoreWindowLayout();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        // Windows 11 只在 WM_NCHITTEST 回报 HTMAXBUTTON 时才会在悬停最大化按钮后弹出
        // 贴靠布局（Snap Layouts）浮层。WindowStyle=None + WindowChrome 的窗口不会产生
        // 这个命中结果，用户于是失去 Win11 最常用的窗口管理入口（Win+Z 和拖拽贴边仍可用，
        // 但远没有悬停按钮直观）。这里把最大化按钮的矩形手工映射为 HTMAXBUTTON。
        // 代价是该按钮不再收到鼠标事件（IsMouseOver/IsPressed 恒 false），视觉状态改由
        // ChromeButtonState 附加属性驱动（见模板 DataTrigger），点击也改为手工处理。
        private const int WmNcHitTest = 0x0084;
        private const int WmNcMouseMove = 0x00A0;
        private const int WmNcLButtonDown = 0x00A1;
        private const int WmNcLButtonUp = 0x00A2;
        private const int WmNcLButtonDblClk = 0x00A3;
        private const int WmNcMouseLeave = 0x02A2;
        private const int HtMaxButton = 9;
        private bool _maxButtonChromePressed;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if ((uint)msg == Native.WindowNative.WmTaskbarCreated)
            {
                // Re-embed the band onto the recreated taskbar. Rebuild alone
                // was insufficient after Explorer restarted because the window
                // was still parented to the dead Shell_TrayWnd.
                _taskbarBand?.HandleTaskbarCreated();
            }

            switch (msg)
            {
                case WmNcHitTest:
                    var chromeHit = HitTestChromeButtons(lParam);
                    if (chromeHit != 0)
                    {
                        // 命中测试在每次鼠标移动时都会发出，顺手维护悬停高亮。
                        SetMaxButtonHot(true);
                        handled = true;
                        return new IntPtr(chromeHit);
                    }
                    SetMaxButtonHot(false);
                    break;

                case WmNcMouseMove:
                    if ((int)wParam != HtMaxButton)
                    {
                        // 指针移出按钮区域（NCHITTEST 的兜底之外再保一道）。
                        SetMaxButtonHot(false);
                        SetMaxButtonPressed(false);
                    }
                    break;

                case WmNcLButtonDown:
                    if ((int)wParam == HtMaxButton)
                    {
                        _maxButtonChromePressed = true;
                        SetMaxButtonPressed(true);
                        // 一并吃掉：DefWindowProc 对 HTMAXBUTTON 的按下会进入
                        // 贴靠布局浮层的跟踪模态循环，而同线程 SendMessage
                        // （自动化测试）永远等不到那条鼠标消息，直接死锁。
                        handled = true;
                    }
                    break;

                case WmNcLButtonUp:
                    if ((int)wParam == HtMaxButton)
                    {
                        SetMaxButtonPressed(false);
                        if (_maxButtonChromePressed)
                        {
                            _maxButtonChromePressed = false;
                            Maximize_Click(BtnWindowMaximize, new RoutedEventArgs());
                        }
                        // 吃掉这条消息：DefWindowProc 会再最大化一次。
                        handled = true;
                    }
                    break;

                case WmNcLButtonDblClk:
                    if ((int)wParam == HtMaxButton)
                        handled = true; // 两次 UP 已各自切换，双击只应生效一次。
                    break;

                case WmNcMouseLeave:
                    // 指针移出窗口后不会再收到 NCHITTEST，不在这里收山，
                    // 高亮和按压状态会一直留在按钮上。
                    SetMaxButtonHot(false);
                    SetMaxButtonPressed(false);
                    break;
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// 最大化按钮矩形内的点映射为 HTMAXBUTTON，返回 0 表示不在任何 chrome 按钮上。
        /// lParam 是物理像素的屏幕坐标（低 16 位 x，高 16 位 y，有符号），而
        /// <see cref="UIElement.PointToScreen"/> 返回 DIP——两边都要归算到物理像素。
        /// </summary>
        private int HitTestChromeButtons(IntPtr lParam)
        {
            if (BtnWindowMaximize == null || !BtnWindowMaximize.IsVisible) return 0;

            try
            {
                long packed = lParam.ToInt64();
                int x = (int)(packed & 0xFFFF);
                int y = (int)((packed >> 16) & 0xFFFF);
                if (x > short.MaxValue) x -= ushort.MaxValue + 1;
                if (y > short.MaxValue) y -= ushort.MaxValue + 1;

                var dpi = VisualTreeHelper.GetDpi(BtnWindowMaximize);
                var origin = BtnWindowMaximize.PointToScreen(new Point(0, 0));
                double left = origin.X * dpi.DpiScaleX;
                double top = origin.Y * dpi.DpiScaleY;
                double width = BtnWindowMaximize.ActualWidth * dpi.DpiScaleX;
                double height = BtnWindowMaximize.ActualHeight * dpi.DpiScaleY;
                if (width <= 0 || height <= 0) return 0;

                return x >= left && x <= left + width && y >= top && y <= top + height
                    ? HtMaxButton
                    : 0;
            }
            catch
            {
                // PointToScreen 在窗口尚未布局时可能抛；退回普通命中测试。
                return 0;
            }
        }

        private ToolTip? _maxButtonToolTip;

        /// <summary>
        /// 悬停高亮 + 提示气泡。命中测试被 Win32 接管后按钮收不到 MouseEnter，
        /// XAML 里的 ToolTip 只会在键盘聚焦时弹出；这里补上鼠标路径，内容与
        /// BtnMaximize 的 DataTrigger 保持同一套"最大化/还原"文案。
        /// </summary>
        private void SetMaxButtonHot(bool hot)
        {
            if (BtnWindowMaximize == null) return;
            ChromeButtonState.SetIsHot(BtnWindowMaximize, hot);

            if (!hot)
            {
                if (_maxButtonToolTip != null) _maxButtonToolTip.IsOpen = false;
                return;
            }

            var text = WindowState == WindowState.Maximized ? "还原" : "最大化";
            _maxButtonToolTip ??= new ToolTip
            {
                PlacementTarget = BtnWindowMaximize,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                HorizontalOffset = 0,
                VerticalOffset = 6,
                Padding = new Thickness(9, 5, 9, 5),
                FontSize = 12,
            };
            _maxButtonToolTip.Content = text;
            _maxButtonToolTip.IsOpen = true;
        }

        private void SetMaxButtonPressed(bool pressed)
        {
            if (BtnWindowMaximize == null) return;
            ChromeButtonState.SetIsPressed(BtnWindowMaximize, pressed);
            if (pressed && _maxButtonToolTip != null)
                _maxButtonToolTip.IsOpen = false; // 按下时收起气泡，避免挡住贴靠布局浮层
        }

        /// <summary>
        /// 恢复上次退出时保存的主窗口布局。没有合法保存值（首次运行、副屏被拔掉、
        /// settings 被手改）时保持 XAML 默认的 CenterScreen + 默认尺寸。
        /// 必须在窗口显示前调用，否则用户会看到窗口先出现在默认位置再跳过去。
        /// </summary>
        private void RestoreWindowLayout()
        {
            if (_previewMode) return;

            try
            {
                var settings = _viewModel.GetSettings();
                MainWindowLayout.Geometry? saved =
                    settings.MainWindowLeft is double left &&
                    settings.MainWindowTop is double top &&
                    settings.MainWindowWidth is double width &&
                    settings.MainWindowHeight is double height
                        ? new MainWindowLayout.Geometry(left, top, width, height)
                        : null;

                var restored = MainWindowLayout.Normalize(
                    saved,
                    new Rect(
                        SystemParameters.VirtualScreenLeft,
                        SystemParameters.VirtualScreenTop,
                        SystemParameters.VirtualScreenWidth,
                        SystemParameters.VirtualScreenHeight),
                    new Size(MinWidth, MinHeight));
                if (restored is null) return;

                // CenterScreen 会在显示时覆盖代码设置的 Left/Top，显式切到 Manual。
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = restored.Value.Left;
                Top = restored.Value.Top;
                Width = restored.Value.Width;
                Height = restored.Value.Height;
                if (settings.MainWindowMaximized)
                    WindowState = WindowState.Maximized;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to restore the main window layout: {ex.Message}");
            }
        }

        /// <summary>
        /// 把主窗口几何写回设置。最大化/最小化时 Left/Top/Width/Height 是状态化后的屏幕值，
        /// 必须取 <see cref="Window.RestoreBounds"/>（"还原后"的几何），否则下次还原会得到
        /// 一个尺寸错误的窗口。
        /// </summary>
        private void SaveWindowLayout()
        {
            if (_previewMode) return;

            try
            {
                var bounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                    return;

                var settings = _viewModel.GetSettings();
                settings.MainWindowLeft = bounds.Left;
                settings.MainWindowTop = bounds.Top;
                settings.MainWindowWidth = bounds.Width;
                settings.MainWindowHeight = bounds.Height;
                settings.MainWindowMaximized = WindowState == WindowState.Maximized;
                _viewModel.SaveSettingsNow();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to persist the main window layout: {ex.Message}");
            }
        }

        /// <summary>
        /// 当前窗口所在显示器的工作区（DIP）。取不到句柄时退回首屏工作区。
        /// 复用车载悬浮窗的显示器枚举（物理像素 + DPI 缩放），换算回 DIP。
        /// </summary>
        private Rect GetCurrentMonitorWorkArea()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var bounds = FloatingWindowMonitors.GetBounds(hwnd);
                    var monitors = FloatingWindowMonitors.Enumerate();
                    if (bounds is Rect windowBounds && monitors.Count > 0)
                    {
                        var monitor = FloatingPlacementGeometry.Nearest(monitors, windowBounds);
                        var scaleX = monitor.ScaleX > 0 ? monitor.ScaleX : 1;
                        var scaleY = monitor.ScaleY > 0 ? monitor.ScaleY : 1;
                        var work = monitor.WorkArea;
                        return new Rect(work.Left / scaleX, work.Top / scaleY, work.Width / scaleX, work.Height / scaleY);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to resolve the current monitor work area: {ex.Message}");
            }
            return SystemParameters.WorkArea;
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            PostToUi(() => _taskbarBand?.Rebuild());
        }

        private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            // 这条消息远比"任务栏变了"宽泛：换壁纸、切主题、改鼠标设置都会触发，
            // 而且一次操作往往连着报好几条。Rebuild 会重建整个乐队（按钮全部重新
            // 创建、悬停状态丢失），所以合并 250ms 内的突发，只认最终状态。
            // SystemEvents 在线程池线程上引发，DispatcherTimer 必须回 UI 线程操作。
            PostToUi(ScheduleBandRebuild);
        }

        private System.Windows.Threading.DispatcherTimer? _bandRebuildTimer;

        private void ScheduleBandRebuild()
        {
            if (_taskbarBand == null) return;

            if (_bandRebuildTimer == null)
            {
                _bandRebuildTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _bandRebuildTimer.Tick += (_, _) =>
                {
                    _bandRebuildTimer.Stop();
                    _taskbarBand?.Rebuild();
                };
            }

            _bandRebuildTimer.Stop();
            _bandRebuildTimer.Start();
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Windows 11 DWM corners are size independent. Only the legacy GDI region
            // needs to be rebuilt continuously while resizing on older systems.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                ApplyNativeWindowAppearance();
            }
        }

        private void ApplyNativeWindowAppearance()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var source = PresentationSource.FromVisual(this);
            double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            Native.WindowNative.ApplyWindowAppearance(
                hwnd,
                WindowState == WindowState.Maximized,
                ActualWidth,
                ActualHeight,
                dpiX,
                dpiY);
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                EnterInteractiveMode();
                await EnsureInitializedAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Interactive startup failed.", ex);
                _viewModel.StatusText = $"初始化失败：{ex.Message}";
            }
        }

        public async void StartInBackground()
        {
            // Keep the public async void signature (callers rely on it) but
            // never let an unobserved exception escape this entry point.
            try
            {
                await EnsureInitializedAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Background startup failed.", ex);
                try { Application.Current.Shutdown(-1); } catch { }
            }
        }

        private Task EnsureInitializedAsync()
        {
            return _initializationTask ??= InitializeCoreAsync();
        }

        private async Task InitializeCoreAsync()
        {
            if (_isInitialized)
                return;

            ApplyNativeWindowAppearance();

            await _viewModel.InitializeAsync();
            _isInitialized = true;
            RestoreAccountViewPreferences();
            _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
            SyncTrayFeatureStates();

            GameSearchBox.Text = GameSearchHint;
            GameSearchBox.Foreground = (Brush)Application.Current.Resources["SearchPlaceholderFgBrush"];
            _gameSearchPlaceholder = true;
            _gameFilterMode = _viewModel.GetSettings().GameFilterMode;
            if (GameFilterCombo != null)
                GameFilterCombo.SelectedValue = _gameFilterMode;
            ApplyGameSearchFilter();
            AccountSearchBox.Text = AccountSearchHint;
            AccountSearchBox.Foreground = (Brush)Application.Current.Resources["SearchPlaceholderFgBrush"];
            _accountSearchPlaceholder = true;

            SettingsFloatingAvatarSlider.Value = _viewModel.DesktopFloatingAvatarSize;
            SettingsFloatingAvatarText.Text = _viewModel.DesktopFloatingAvatarSize.ToString();
            SettingsFloatingOpacitySlider.Value = _viewModel.DesktopFloatingOpacity;
            SettingsFloatingOpacityText.Text = $"{_viewModel.DesktopFloatingOpacity}%";
            SyncGameSaveSettingsUi();

            if ((_viewModel.IsTaskbarPinned || _viewModel.DesktopFloatingEnabled) &&
                _viewModel.GetSettings().PinnedGameIds.Count > 0 &&
                _viewModel.GameList.Count == 0)
            {
                await _viewModel.ScanGamesAsync();
            }

            if (_viewModel.IsTaskbarPinned)
            {
                AttachToTaskbar();
            }

            if (_viewModel.DesktopFloatingEnabled)
            {
                AttachDesktopFloating();
            }

            SwitchToAccounts();
            while (_pendingExternalRequests.Count > 0)
            {
                var arguments = _pendingExternalRequests.Dequeue();
                await ProcessExternalRequestSerializedAsync(arguments);
            }
        }

        private static bool ContainsActionRequest(string[] arguments) =>
            arguments.Any(argument =>
                string.Equals(argument, "--switch-account", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--launch-game", StringComparison.OrdinalIgnoreCase));

        private void EnterInteractiveMode()
        {
            _isInteractiveSession = true;
            App.SuppressFatalDialogs = false;
        }

        public void ActivateFromExternalRequest()
        {
            if (!Dispatcher.CheckAccess())
            {
                PostToUi(ActivateFromExternalRequest);
                return;
            }

            EnterInteractiveMode();
            ShowInTaskbar = true;
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        }

        public async void HandleExternalRequest(string[] arguments)
        {
            // Public async void is intentional (IPC entry point); guard the
            // whole body so an exception can never become unobserved.
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    PostToUi(() => HandleExternalRequest(arguments));
                    return;
                }
                if (arguments.Any(argument => string.Equals(
                        argument, "--shutdown-for-update", StringComparison.OrdinalIgnoreCase)))
                {
                    RequestExit();
                    return;
                }

                if (!_isInitialized)
                {
                    _pendingExternalRequests.Enqueue(arguments.ToArray());
                    return;
                }

                try
                {
                    await ProcessExternalRequestSerializedAsync(arguments);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("External activation request failed.", ex);
                    _viewModel.StatusText = $"外部启动请求失败：{ex.Message}";
                    ActivateFromExternalRequest();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("HandleExternalRequest failed.", ex);
            }
        }

        private async Task ProcessExternalRequestSerializedAsync(string[] arguments)
        {
            await _externalRequestGate.WaitAsync();
            try
            {
                await ProcessExternalRequestCoreAsync(arguments);
            }
            finally
            {
                _externalRequestGate.Release();
            }
        }

        private async Task ProcessExternalRequestCoreAsync(string[] arguments)
        {
            var switchIndex = Array.FindIndex(arguments, argument =>
                string.Equals(argument, "--switch-account", StringComparison.OrdinalIgnoreCase));
            var gameIndex = Array.FindIndex(arguments, argument =>
                string.Equals(argument, "--launch-game", StringComparison.OrdinalIgnoreCase));

            // 切换与游戏启动可以独立发生：同时传入两个参数时，先切换再启动，而不是忽略其中一个。
            if (switchIndex >= 0 && switchIndex + 1 < arguments.Length)
            {
                var steamId = arguments[switchIndex + 1];
                var launchSteam = arguments.Any(argument =>
                    string.Equals(argument, "--launch-steam", StringComparison.OrdinalIgnoreCase));
                // 与外部实例的切换、界面点击的切换共用同一把编排锁，避免并发重启 Steam。
                if (!await _accountInteractionGate.WaitAsync(0))
                {
                    AppLogger.Warn("外部切换请求被忽略：已有账号操作正在进行。");
                }
                else
                {
                    try
                    {
                        await _viewModel.SwitchAccountBySteamIdAsync(steamId, launchSteam);
                        _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
                        SyncTrayFeatureStates();
                    }
                    finally
                    {
                        try { _accountInteractionGate.Release(); } catch (ObjectDisposedException) { }
                    }
                }
            }

            if (gameIndex >= 0 && gameIndex + 1 < arguments.Length &&
                int.TryParse(arguments[gameIndex + 1], out var appId) && appId > 0)
            {
                await _viewModel.LaunchBoundGameAsync(appId);
            }

            if (switchIndex < 0 && gameIndex < 0)
            {
                ActivateFromExternalRequest();
            }
        }

        private void OnTrayAccountSelected(object? sender, string steamId)
        {
            var account = _viewModel.Accounts.FirstOrDefault(a => a.SteamId == steamId);
            if (account != null)
            {
                // 走与卡片/任务栏相同的闸锁入口：裸 fire-and-forget 会和
                // 其它切换路径并发，两个流程同时重启 Steam。
                _ = SwitchToAccountAsync(account);
            }
        }

        private void CreateAccountShortcut_Click(object sender, RoutedEventArgs e)
        {
            var account = _viewModel.SelectedAccount;
            if (account == null)
            {
                MessageBox.Show(this, "请先选择一个 Steam 账号。", "创建快捷方式", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var shortcutPath = AccountShortcutService.CreateDesktopShortcut(account.Account);
                _viewModel.StatusText = $"已创建桌面快捷方式：{Path.GetFileNameWithoutExtension(shortcutPath)}";
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unable to create account desktop shortcut.", ex);
                MessageBox.Show(this, ex.Message, "创建快捷方式失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnTrayLaunchSteamRequested(object? sender, EventArgs e)
        {
            var accountManager = _viewModel.GetAccountManager();
            accountManager.LaunchSteam();
        }

        private void OnCurrentAccountChanged(object? sender, EventArgs e)
        {
            PostToUi(() => _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount));
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_viewModel.MinimizeToTray && !_exitRequested)
            {
                AppLogger.Info("Window close requested; continuing in the tray because MinimizeToTray is enabled.");
                e.Cancel = true;
                // 只走托盘的用户可能从不真正退出，这里也要落盘，否则下次
                // 启动恢复的是很久以前的窗口几何。
                SaveWindowLayout();
                Hide();
            }
            else
            {
                SaveWindowLayout();
                CleanupForExit();
            }
        }

        private void OnProcessExit(object? sender, EventArgs e) => _desktopIconService.DisposeForApplicationExit();

        private void RequestExit()
        {
            AppLogger.Info("Full application exit requested.");
            _exitRequested = true;
            ExitCleanup.Run(
                ("main resources", CleanupForExit),
                ("close main window", Close),
                ("application shutdown", () => Application.Current?.Shutdown()));
        }

        /// <summary>
        /// Ensures programmatic closing follows full cleanup rather than the
        /// ordinary title-bar close behavior that keeps the utility in the tray.
        /// </summary>
        public void PrepareForApplicationShutdown() => _exitRequested = true;

        internal bool IsShutdownStarted => _shutdownStarted;
        internal void CleanupForApplicationExit() => CleanupForExit();

        private async Task<bool> ConfirmForceCloseAfterTimeoutAsync()
        {
            // Always marshal to the UI thread; this callback may run after the
            // Steam close timeout on a worker thread.
            return await Dispatcher.InvokeAsync(() =>
            {
                // Setting Owner on a non-visible window throws InvalidOperationException.
                Window? owner = IsVisible ? this : null;
                var result = MessageBox.Show(
                    owner,
                    "Steam 在 20 秒内没有正常退出。\n\n可能仍在同步 Steam Cloud。是否立即强制结束 Steam 进程树？",
                    "Steam 尚未退出",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                return result == MessageBoxResult.Yes;
            });
        }

        private void CleanupForExit()
        {
            if (_shutdownStarted) return;
            _shutdownStarted = true;
            _exitRequested = true;
            AppLogger.Info("Application resource cleanup started.");

            var hwndSource = _hwndSource;
            _hwndSource = null;
            var lyricsProbe = Interlocked.Exchange(ref _lyricsDiagnosticCts, null);
            var taskbarBand = _taskbarBand;
            var floatingWindow = _desktopFloatingWindow;
            var notificationFlyout = _notificationFlyout;
            var saveCenter = _gameSaveCenterWindow;
            _taskbarBand = null;
            _desktopFloatingWindow = null;
            _notificationFlyout = null;
            _gameSaveCenterWindow = null;

            var failures = ExitCleanup.Run(
                ("window message hook", () => hwndSource?.RemoveHook(WndProc)),
                ("band rebuild debounce", () => _bandRebuildTimer?.Stop()),
                ("toast timer", () => { _toastTimer.Stop(); _toastTimer.Tick -= OnToastTimerTick; }),
                ("notification flyout timer", () => _notificationFlyoutTimer?.Stop()),
                ("toast property subscription", () => _viewModel.PropertyChanged -= OnViewModelToastPropertyChanged),
                ("diagnostic cancellation", () => lyricsProbe?.Cancel()),
                ("diagnostic token", () => lyricsProbe?.Dispose()),
                ("account subscription", () => _viewModel.GetAccountManager().CurrentAccountChanged -= OnCurrentAccountChanged),
                ("account results subscription", DetachAccountLibraryFeedback),
                ("display subscription", () => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged),
                ("preference subscription", () => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged),
                ("process-exit subscription", () => AppDomain.CurrentDomain.ProcessExit -= OnProcessExit),
                ("taskbar detach", () => taskbarBand?.Detach()),
                ("taskbar window", () => taskbarBand?.Close()),
                ("floating window", () => floatingWindow?.Close()),
                ("notification flyout window", () => notificationFlyout?.Close()),
                ("save center subscription", () => { if (saveCenter != null) saveCenter.CloseRequested -= GameSaveCenter_CloseRequested; }),
                ("save center", () => saveCenter?.Dispose()),
                ("desktop visibility subscription", () => _desktopIconService.IconVisibilityChanged -= OnDesktopIconVisibilityChanged),
                ("desktop countdown subscription", () => _desktopIconService.CountdownChanged -= OnDesktopIconCountdownChanged),
                ("desktop icon recovery", () => { if (!_previewMode) _desktopIconService.DisposeForApplicationExit(); }),
                ("desktop recovery setting", () => _viewModel.DesktopIconsHiddenByApp = false),
                ("tray icon", () => _trayIcon.Dispose()),
                ("view model", () => _viewModel.Dispose()));
            // 对称释放两把信号量（它们持有异步等待句柄，应在退出时显式释放，而非依赖终结器）。
            try { _externalRequestGate.Dispose(); } catch { }
            try { _accountInteractionGate.Dispose(); } catch { }
            AppLogger.Info($"Application resource cleanup completed. Failures={failures}");
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            ApplyNativeWindowAppearance();

            // DWM clips a maximized window to the work area. A rounded shell
            // would have its corners sheared off against the window background,
            // leaving visible notches in the four corners. Flatten the shell
            // (and its 1px border) for the maximized state only.
            var maximized = WindowState == WindowState.Maximized;
            RootBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(16);
            RootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);

            if (WindowState == WindowState.Minimized && _viewModel.MinimizeToTray)
            {
                Hide();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void ResizeGrip_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            // Handled by DragDelta
        }

        private void ResizeGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            var newWidth = ActualWidth + e.HorizontalChange;
            var newHeight = ActualHeight + e.VerticalChange;

            if (newWidth >= MinWidth)
                Width = newWidth;
            if (newHeight >= MinHeight)
                Height = newHeight;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void GitHubLink_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/ddxgtx/SteamSwitch",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void TitleBarArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Only the title bar drags the window. The handler used to sit on the
            // Window itself, so a stray click on any unhandled body element
            // (a TextBlock, an image) moved the window and a double-click there
            // toggled maximization. Interactive controls mark the mouse event as
            // handled, so they never reach this handler.
            //
            // Clicks within the outer 6px stay with WindowChrome's resize logic
            // (ResizeBorderThickness), which owns the NCHITTEST result.
            var pos = e.GetPosition(this);
            const int resizeBorder = 6;
            if (pos.X < resizeBorder || pos.X > ActualWidth - resizeBorder ||
                pos.Y < resizeBorder || pos.Y > ActualHeight - resizeBorder)
                return;

            if (e.ClickCount == 2)
            {
                Maximize_Click(sender, e);
                return;
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove aborts when the window changes state mid-drag
                // (e.g. hidden to the tray). Nothing to recover.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Same class of race when the mouse is released outside.
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            SwitchToSettings();
        }

                private void SideAccounts_Click(object sender, RoutedEventArgs e)
        {
            SwitchToAccounts();
        }

        private void SideGames_Click(object sender, RoutedEventArgs e)
        {
            SwitchToGames();
        }

        private void SideSaveCenter_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.GameSaveCenterEnabled)
            {
                _viewModel.StatusText = "游戏存档模块已关闭，可在偏好设置中重新开启";
                return;
            }
            if (_gameSaveCenterWindow == null)
            {
                _gameSaveCenterWindow = new GameSaveCenterWindow(_viewModel);
                _gameSaveCenterWindow.CloseRequested += GameSaveCenter_CloseRequested;
            }
            SaveCenterHost.Content = _gameSaveCenterWindow;
            SwitchToSaveCenter();
        }

        private void GameSaveCenter_CloseRequested(object? sender, EventArgs e)
        {
            SwitchToAccounts();
        }

        private void SideDesktopIcons_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.DesktopIconHiderEnabled)
            {
                _viewModel.StatusText = "桌面图标模块已关闭，可在偏好设置中重新开启";
                return;
            }
            SwitchToDesktopIcons();
        }

        private void SideTaskbarLyrics_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.TaskbarLyricsModuleEnabled)
            {
                _viewModel.StatusText = "任务栏歌词模块已关闭，可在偏好设置中重新开启";
                return;
            }
            SwitchToTaskbarLyrics();
        }

        private void SideQuickLaunch_Click(object sender, RoutedEventArgs e)
        {
            SwitchToQuickLaunch();
        }

        private void SideSettings_Click(object sender, RoutedEventArgs e)
        {
            SwitchToSettings();
        }

        private void SwitchToAccounts()
        {
            PageHeader.Visibility = Visibility.Collapsed;
            AccountView.Visibility = Visibility.Visible;
            GameView.Visibility = Visibility.Collapsed;
            QuickLaunchView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            SaveCenterHost.Visibility = Visibility.Collapsed;
            DesktopIconsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsAdvancedView.Visibility = Visibility.Collapsed;
            PlayViewEnterAnimation(AccountView);
            PageTitle.Text = "多账号库";
            PageSubtitle.Text = "双击卡片快速切换 Steam 身份，支持置顶常用账号。";
            SetSidebarActive(SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideSaveCenter, SideAccentSave,
                             SideDesktopIcons, SideAccentDesktopIcons,
                             SideTaskbarLyrics, SideAccentTaskbarLyrics,
                             SideQuickLaunch, SideAccentQuickLaunch,
                             SideSettings, SideAccentSettings);
        }

        private void SwitchToGames()
        {
            PageHeader.Visibility = Visibility.Visible;
            AccountView.Visibility = Visibility.Collapsed;
            GameView.Visibility = Visibility.Visible;
            QuickLaunchView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            SaveCenterHost.Visibility = Visibility.Collapsed;
            DesktopIconsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsAdvancedView.Visibility = Visibility.Collapsed;
            PlayViewEnterAnimation(GameView);
            PageTitle.Text = "游戏库绑定";
            PageSubtitle.Text = "智能扫描本机 Steam 游戏，为每个游戏绑定专属启动账号。";
            SetSidebarActive(SideGames, SideAccentGame,
                             SideAccounts, SideAccentAccount,
                             SideSaveCenter, SideAccentSave,
                             SideDesktopIcons, SideAccentDesktopIcons,
                             SideTaskbarLyrics, SideAccentTaskbarLyrics,
                             SideQuickLaunch, SideAccentQuickLaunch,
                             SideSettings, SideAccentSettings);

            if (_viewModel.GameList.Count == 0)
            {
                _ = _viewModel.ScanGamesAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        AppLogger.Error("ScanGamesAsync failed", t.Exception);
                        return;
                    }
                    PostToUi(() =>
                    {
                        GameScanStatus.Text = _viewModel.GameList.Count > 0
                            ? $"已安装游戏 — {_viewModel.GameList.Count} 款"
                            : "未找到已安装的游戏";
                    });
                }, TaskContinuationOptions.NotOnCanceled);
            }
            else
            {
                GameScanStatus.Text = $"共 {_viewModel.GameList.Count} 款游戏";
            }
        }

        private void SwitchToSaveCenter()
        {
            PageHeader.Visibility = Visibility.Visible;
            AccountView.Visibility = Visibility.Collapsed;
            GameView.Visibility = Visibility.Collapsed;
            QuickLaunchView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            SaveCenterHost.Visibility = Visibility.Visible;
            DesktopIconsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsAdvancedView.Visibility = Visibility.Collapsed;
            PlayViewEnterAnimation(SaveCenterHost);
            PageTitle.Text = "游戏存档中心";
            PageSubtitle.Text = "按 Steam 账号与游戏目录智能检查、备份与安全恢复 userdata 存档。";
            SetSidebarActive(SideSaveCenter, SideAccentSave,
                             SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideDesktopIcons, SideAccentDesktopIcons,
                             SideTaskbarLyrics, SideAccentTaskbarLyrics,
                             SideQuickLaunch, SideAccentQuickLaunch,
                             SideSettings, SideAccentSettings);
        }

        private void SwitchToDesktopIcons()
        {
            PageHeader.Visibility = Visibility.Visible;
            AccountView.Visibility = Visibility.Collapsed;
            GameView.Visibility = Visibility.Collapsed;
            QuickLaunchView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            SaveCenterHost.Visibility = Visibility.Collapsed;
            DesktopIconsView.Visibility = Visibility.Visible;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsAdvancedView.Visibility = Visibility.Collapsed;
            PlayViewEnterAnimation(DesktopIconsView);
            PageTitle.Text = "桌面图标智能隐藏";
            PageSubtitle.Text = "智能监测鼠标与桌面活动，自动收起桌面图标，还原纯净壁纸视野。";
            SetSidebarActive(SideDesktopIcons, SideAccentDesktopIcons,
                             SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideSaveCenter, SideAccentSave,
                             SideTaskbarLyrics, SideAccentTaskbarLyrics,
                             SideQuickLaunch, SideAccentQuickLaunch,
                             SideSettings, SideAccentSettings);
        }

        private void SwitchToTaskbarLyrics()
        {
            PageHeader.Visibility = Visibility.Visible;
            AccountView.Visibility = Visibility.Collapsed;
            GameView.Visibility = Visibility.Collapsed;
            QuickLaunchView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            SaveCenterHost.Visibility = Visibility.Collapsed;
            DesktopIconsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Visible;
            TaskbarLyricsAdvancedView.Visibility = Visibility.Collapsed;
            PlayViewEnterAnimation(TaskbarLyricsView);
            PageTitle.Text = "任务栏歌词";
            PageSubtitle.Text = "在任务栏实时显示多平台同步歌词，支持媒体快捷控制与自适应宽度。";
            SyncLyricsAdvancedValueLabels();
            SetSidebarActive(SideTaskbarLyrics, SideAccentTaskbarLyrics,
                             SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideSaveCenter, SideAccentSave,
                             SideDesktopIcons, SideAccentDesktopIcons,
                             SideQuickLaunch, SideAccentQuickLaunch,
                             SideSettings, SideAccentSettings);
        }

        private void OpenLyricsAdvanced_Click(object sender, RoutedEventArgs e) => SwitchToTaskbarLyricsAdvanced();

        private void BackFromLyricsAdvanced_Click(object sender, RoutedEventArgs e) => SwitchToTaskbarLyrics();

        /// <summary>歌词板块的二级页面。侧边栏仍高亮「任务栏歌词」，
        /// 让用户清楚自己还在同一个功能板块里。</summary>
        private void SwitchToTaskbarLyricsAdvanced()
        {
            PageHeader.Visibility = Visibility.Visible;
            AccountView.Visibility = Visibility.Collapsed;
            GameView.Visibility = Visibility.Collapsed;
            QuickLaunchView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            SaveCenterHost.Visibility = Visibility.Collapsed;
            DesktopIconsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsAdvancedView.Visibility = Visibility.Visible;
            PlayViewEnterAnimation(TaskbarLyricsAdvancedView);
            PageTitle.Text = "歌词高级设置";
            PageSubtitle.Text = "外观、布局、时间轴校正与性能调优，按需微调歌词岛表现。";
            SyncLyricsAdvancedValueLabels();
            SetSidebarActive(SideTaskbarLyrics, SideAccentTaskbarLyrics,
                             SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideSaveCenter, SideAccentSave,
                             SideDesktopIcons, SideAccentDesktopIcons,
                             SideQuickLaunch, SideAccentQuickLaunch,
                             SideSettings, SideAccentSettings);
        }

        /// <summary>滑块旁的数字标签不走绑定，进入页面时需要按当前值刷新一次。</summary>
        private void SyncLyricsAdvancedValueLabels()
        {
            SyncLyricsFontCombos();
            if (TaskbarLyricsWidthText != null)
                TaskbarLyricsWidthText.Text = $"{_viewModel.TaskbarLyricsWidth} px";
            if (LyricOffsetText != null)
                LyricOffsetText.Text = _viewModel.LyricOffsetMs >= 0
                    ? $"+{_viewModel.LyricOffsetMs} ms"
                    : $"{_viewModel.LyricOffsetMs} ms";
        }

        private void SwitchToQuickLaunch()
        {
            PageHeader.Visibility = Visibility.Visible;
            AccountView.Visibility = Visibility.Collapsed;
            GameView.Visibility = Visibility.Collapsed;
            QuickLaunchView.Visibility = Visibility.Visible;
            SettingsView.Visibility = Visibility.Collapsed;
            SaveCenterHost.Visibility = Visibility.Collapsed;
            DesktopIconsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsAdvancedView.Visibility = Visibility.Collapsed;
            PlayViewEnterAnimation(QuickLaunchView);
            PageTitle.Text = "快捷启动";
            PageSubtitle.Text = "将常用账号、独立游戏与外部程序组织成桌面快捷面板，支持拖拽排序。";
            SetSidebarActive(SideQuickLaunch, SideAccentQuickLaunch,
                             SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideSaveCenter, SideAccentSave,
                             SideDesktopIcons, SideAccentDesktopIcons,
                             SideTaskbarLyrics, SideAccentTaskbarLyrics,
                             SideSettings, SideAccentSettings);
            QuickLaunchEmptyHint.Visibility = _viewModel.QuickLaunchList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Initialize drag-drop if not already done
            if (_quickLaunchDragDrop == null)
            {
                _quickLaunchDragDrop = new DragDropHelper(QuickLaunchListBox, QuickLaunchDeleteZone);
                // async lambda 就是 async void：异常无人接管会直接弹崩溃框。
                // 连续拖删还会让两次删除基于同一份旧索引并发执行。
                _quickLaunchDragDrop.ItemDeleted += async (s, index) =>
                {
                    await RunGuardedAsync("删除快速启动项", async () =>
                    {
                        if (index < 0 || index >= _viewModel.QuickLaunchList.Count) return;
                        await _viewModel.DeleteQuickLaunchItemAsync(index);
                        QuickLaunchEmptyHint.Visibility = _viewModel.QuickLaunchList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    });
                };
                _quickLaunchDragDrop.ItemMoved += async (s, args) =>
                {
                    await RunGuardedAsync("调整快速启动顺序", async () =>
                    {
                        var count = _viewModel.QuickLaunchList.Count;
                        if (args.fromIndex < 0 || args.fromIndex >= count) return;
                        if (args.toIndex < 0 || args.toIndex >= count) return;
                        await _viewModel.ReorderQuickLaunchItemAsync(args.fromIndex, args.toIndex);
                    });
                };
                _quickLaunchDragDrop.ItemClicked += (s, index) =>
                {
                    var items = _viewModel.QuickLaunchList;
                    if (index >= 0 && index < items.Count)
                    {
                        RunGuardedSync("启动项目", () => _viewModel.LaunchQuickLaunchItem(items[index].Id));
                    }
                };
                _quickLaunchDragDrop.Attach();
            }
        }

        private void SwitchToSettings()
        {
            PageHeader.Visibility = Visibility.Visible;
            AccountView.Visibility = Visibility.Collapsed;
            GameView.Visibility = Visibility.Collapsed;
            QuickLaunchView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Visible;
            SaveCenterHost.Visibility = Visibility.Collapsed;
            DesktopIconsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            TaskbarLyricsAdvancedView.Visibility = Visibility.Collapsed;
            PlayViewEnterAnimation(SettingsView);
            PageTitle.Text = "偏好设置";
            PageSubtitle.Text = "常用选项与高级配置分开管理，修改后自动保存。";
            SyncLyricsFontCombos();
            SetSidebarActive(SideSettings, SideAccentSettings,
                             SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideSaveCenter, SideAccentSave,
                             SideDesktopIcons, SideAccentDesktopIcons,
                             SideTaskbarLyrics, SideAccentTaskbarLyrics,
                             SideQuickLaunch, SideAccentQuickLaunch);
        }

        private void SetSidebarActive(Button active, Border activeAccent, params object[] inactivePairs)
        {
            active.SetResourceReference(Control.BackgroundProperty, "SidebarActiveBgBrush");
            active.SetResourceReference(Control.ForegroundProperty, "SidebarActiveFgBrush");
            active.FontWeight = FontWeights.SemiBold;
            activeAccent.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

            for (var i = 0; i + 1 < inactivePairs.Length; i += 2)
            {
                if (inactivePairs[i] is not Button button || inactivePairs[i + 1] is not Border accent)
                    continue;
                button.Background = Brushes.Transparent;
                button.SetResourceReference(Control.ForegroundProperty, "SidebarInactiveFgBrush");
                button.FontWeight = FontWeights.Normal;
                accent.Background = Brushes.Transparent;
            }
        }

        private async void GameScan_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.ScanGamesAsync();
                GameScanStatus.Text = _viewModel.GameList.Count > 0
                    ? $"共 {_viewModel.GameList.Count} 款游戏"
                    : "未找到已安装的游戏";
                ApplyGameSearchFilter();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Game scan command failed.", ex);
                GameScanStatus.Text = $"扫描失败：{ex.Message}";
                _viewModel.StatusText = "游戏扫描失败，请检查 Steam 路径";
            }
        }

        private async void AccountListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left ||
                e.OriginalSource is not DependencyObject source ||
                FindVisualAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) != null)
            {
                return;
            }

            var item = ItemsControl.ContainerFromElement(AccountListBox, source) as ListBoxItem;
            if (item?.DataContext is not AccountViewModel account)
                return;

            e.Handled = true;
            await SwitchToAccountAsync(account);
        }

        /// <summary>
        /// 卡片上的「切换」按钮：让主功能不用依赖双击手势也能被发现。
        /// </summary>
        private async void AccountCardSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not AccountViewModel account)
                return;

            e.Handled = true;
            await SwitchToAccountAsync(account);
        }

        /// <summary>
        /// 双击卡片与卡片按钮共用的切换入口，带重入闩锁。
        /// </summary>
        private async Task SwitchToAccountAsync(AccountViewModel account)
        {
            // Minimum re-entry latch: ignore double-clicks while another switch
            // (list button, taskbar, tray) is already in flight.
            if (_viewModel.IsLoading || !await _accountInteractionGate.WaitAsync(0))
            {
                _viewModel.StatusText = "账号操作正在进行，请稍候";
                return;
            }

            try
            {
                _viewModel.SelectedAccount = account;

                if (_viewModel.AutoStartSteam)
                    await _viewModel.SwitchAndLaunchCommand.ExecuteAsync(null);
                else
                    await _viewModel.SwitchOnlyCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Account switch failed.", ex);
                _viewModel.StatusText = $"账号切换失败：{ex.Message}";
            }
            finally
            {
                try { _accountInteractionGate.Release(); } catch (ObjectDisposedException) { }
            }
        }

        private async void RetryAccountSwitch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.RetryLastAccountSwitchAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Retry account switch failed.", ex);
                _viewModel.StatusText = $"重试失败：{ex.Message}";
            }
        }

        private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
        {
            while (element != null)
            {
                if (element is T match)
                    return match;

                element = VisualTreeHelper.GetParent(element);
            }

            return null;
        }
        private void AccountFilterButton_Click(object sender, RoutedEventArgs e)
        {
            OpenAccountToolbarMenu(sender as Button);
        }

        private void AccountSortButton_Click(object sender, RoutedEventArgs e)
        {
            OpenAccountToolbarMenu(sender as Button);
        }

        private void OpenAccountToolbarMenu(Button? button)
        {
            if (button?.ContextMenu == null)
                return;

            var selectedMode = ReferenceEquals(button, AccountFilterButton) ? _accountFilterMode : _accountSortMode;
            foreach (var item in button.ContextMenu.Items.OfType<MenuItem>())
                item.IsChecked = item.Tag is string mode && mode == selectedMode;

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        private void AccountFilterOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string mode })
                return;

            ApplyAccountFilterMode(mode);
        }

        private void ApplyAccountFilterMode(string mode)
        {
            _accountFilterMode = mode;
            AccountFilterText.Text = mode switch
            {
                "Current" => "当前账号",
                "Pinned" => "已固定账号",
                "Archived" => "待找回账号",
                _ => "全部账号"
            };
            RefreshAccountFilter();
            var settings = _viewModel.GetSettings();
            settings.AccountFilterMode = _accountFilterMode;
            _viewModel.SaveSettingsNow();
        }

        private void AccountSortOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string mode })
                return;

            _accountSortMode = mode;
            AccountSortText.Text = mode switch
            {
                "Name" => "排序：账号名称",
                "Pinned" => "排序：固定优先",
                _ => "排序：最近登录"
            };
            ApplyAccountSort();
            var settings = _viewModel.GetSettings();
            settings.AccountSortMode = _accountSortMode;
            _viewModel.SaveSettingsNow();
        }

        private void ApplyAccountSort()
        {
            if (Resources["FilteredAccounts"] is not CollectionViewSource source || source.View == null)
                return;

            using (source.View.DeferRefresh())
            {
                source.View.SortDescriptions.Clear();
                switch (_accountSortMode)
                {
                    case "Name":
                        source.View.SortDescriptions.Add(new SortDescription(nameof(AccountViewModel.DisplayName), ListSortDirection.Ascending));
                        break;
                    case "Pinned":
                        source.View.SortDescriptions.Add(new SortDescription(nameof(AccountViewModel.IsPinned), ListSortDirection.Descending));
                        source.View.SortDescriptions.Add(new SortDescription(nameof(AccountViewModel.LastLoginTimestamp), ListSortDirection.Descending));
                        break;
                    default:
                        source.View.SortDescriptions.Add(new SortDescription(nameof(AccountViewModel.LastLoginTimestamp), ListSortDirection.Descending));
                        break;
                }

                if (_accountSortMode != "Name")
                    source.View.SortDescriptions.Add(new SortDescription(nameof(AccountViewModel.DisplayName), ListSortDirection.Ascending));
            }
        }

        private void AccountListView_Click(object sender, RoutedEventArgs e)
        {
            SetAccountGridMode(false);
        }

        private void AccountGridView_Click(object sender, RoutedEventArgs e)
        {
            SetAccountGridMode(true);
        }

        private void SetAccountGridMode(bool useGrid)
        {
            if (_accountGridMode == useGrid)
                return;

            _accountGridMode = useGrid;
            var settings = _viewModel.GetSettings();
            settings.AccountGridMode = useGrid;
            _viewModel.SaveSettingsNow();
            if (useGrid)
            {
                _accountGridItemStyle ??= CreateAccountGridItemStyle();
                AccountListBox.ItemsPanel = (ItemsPanelTemplate)FindResource("AccountGridItemsPanel");
                AccountListBox.ItemContainerStyle = _accountGridItemStyle;
                AccountListBox.MaxHeight = double.PositiveInfinity;
                Grid.SetColumnSpan(AccountListPanel, 2);
                AccountListPanel.Margin = new Thickness(0);
                AccountDetailPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                AccountListBox.ItemsPanel = (ItemsPanelTemplate)FindResource("AccountListItemsPanel");
                AccountListBox.ItemContainerStyle = _accountListItemStyle;
                AccountListBox.MaxHeight = double.PositiveInfinity;
                Grid.SetColumnSpan(AccountListPanel, 1);
                AccountListPanel.Margin = new Thickness(0, 0, 24, 0);
                AccountDetailPanel.Visibility = Visibility.Visible;
            }

            SetAccountViewButtonState(AccountListViewButton, !useGrid);
            SetAccountViewButtonState(AccountGridViewButton, useGrid);
        }

        private void RestoreAccountViewPreferences()
        {
            var settings = _viewModel.GetSettings();
            _accountFilterMode = settings.AccountFilterMode;
            _accountSortMode = settings.AccountSortMode;
            AccountFilterText.Text = _accountFilterMode switch
            {
                "Current" => "当前账号",
                "Pinned" => "已固定账号",
                "Archived" => "待找回账号",
                _ => "全部账号"
            };
            AccountSortText.Text = _accountSortMode switch
            {
                "Name" => "排序：账号名称",
                "Pinned" => "排序：固定优先",
                _ => "排序：最近登录"
            };
            RefreshAccountFilter();
            ApplyAccountSort();
            SetAccountGridMode(settings.AccountGridMode);
        }

        private Style CreateAccountGridItemStyle()
        {
            var style = new Style(typeof(ListBoxItem), _accountListItemStyle);
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, double.NaN));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6)));
            return style;
        }

        private static void SetAccountViewButtonState(Button button, bool active)
        {
            if (active)
            {
                button.SetResourceReference(Control.BackgroundProperty, "SelectedBgBrush");
                button.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
            }
            else
            {
                button.Background = Brushes.Transparent;
                button.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
            }
        }
        private void AccountCollectionViewSource_Filter(object sender, FilterEventArgs e)
        {
            if (e.Item is not AccountViewModel account)
            {
                e.Accepted = false;
                return;
            }

            bool matchesScope = _accountFilterMode switch
            {
                "Current" => account.IsCurrent,
                "Pinned" => account.IsPinned,
                "Archived" => account.IsArchived,
                _ => true
            };

            var accountSearchText = AccountSearchBox?.Text;
            bool matchesSearch = _accountSearchPlaceholder || string.IsNullOrWhiteSpace(accountSearchText);
            if (!matchesSearch)
            {
                var query = accountSearchText!.Trim();
                matchesSearch = account.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                account.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                account.Account.SteamId.Contains(query, StringComparison.OrdinalIgnoreCase);
            }

            e.Accepted = matchesScope && matchesSearch;
        }
        private void AccountSearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (!_accountSearchPlaceholder) return;
            _accountSearchPlaceholder = false;
            AccountSearchBox.Text = string.Empty;
            AccountSearchBox.Foreground = (Brush)Application.Current.Resources["SearchTextFgBrush"];
        }

        private void AccountSearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(AccountSearchBox.Text)) return;
            _accountSearchPlaceholder = true;
            AccountSearchBox.Text = AccountSearchHint;
            AccountSearchBox.Foreground = (Brush)Application.Current.Resources["SearchPlaceholderFgBrush"];
            RefreshAccountFilter();
        }

        private void AccountSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshAccountFilter();
        }

        private void RefreshAccountFilter()
        {
            if (AccountListHeading != null)
                AccountListHeading.Text = _accountFilterMode switch
                {
                    "Archived" => "待找回的账号",
                    "Current" => "当前账号",
                    "Pinned" => "已固定账号",
                    _ => "全部已收录账号"
                };
            if (Resources["FilteredAccounts"] is CollectionViewSource source)
                source.View?.Refresh();
            UpdateAccountLibraryFeedback();
        }

        private void GameSearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (_gameSearchPlaceholder)
            {
                GameSearchBox.Text = "";
                GameSearchBox.Foreground = (Brush)Application.Current.Resources["SearchTextFgBrush"];
                _gameSearchPlaceholder = false;
            }
        }

        private void GameSearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(GameSearchBox.Text))
            {
                GameSearchBox.Text = GameSearchHint;
                GameSearchBox.Foreground = (Brush)Application.Current.Resources["SearchPlaceholderFgBrush"];
                _gameSearchPlaceholder = true;
            }
        }

        private void GameSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_gameSearchPlaceholder) return;

            GameClearSearch.Visibility = string.IsNullOrWhiteSpace(GameSearchBox.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            ApplyGameSearchFilter();
        }

        private void ApplyGameSearchFilter()
        {
            if (_viewModel == null || GameListBox == null)
                return;

            var search = _gameSearchPlaceholder ? string.Empty : (GameSearchBox?.Text ?? string.Empty).Trim();
            var mode = _gameFilterMode;

            // 只筛选视图，不重建 ItemsSource：直接赋值会顶掉 XAML 的
            // {Binding GameList}，此后 VM 里的扫描结果再也反映不到列表上。
            var view = CollectionViewSource.GetDefaultView(_viewModel.GameList);
            view.Filter = item => item is GameListViewModel game
                                  && MatchesFilterMode(game, mode)
                                  && MatchesSearch(game, search);
            view.Refresh();

            if (_viewModel.SelectedGame != null && !view.Cast<GameListViewModel>().Contains(_viewModel.SelectedGame))
                _viewModel.SelectedGame = null;
        }

        private static bool MatchesFilterMode(GameListViewModel game, string mode) => mode switch
        {
            "Bound" => game.HasBinding,
            "Unbound" => !game.HasBinding,
            _ => true
        };

        private static bool MatchesSearch(GameListViewModel game, string search)
        {
            if (string.IsNullOrEmpty(search))
                return true;

            return game.GameName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   game.AppId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   (!string.IsNullOrWhiteSpace(game.BindingAccountName) &&
                    game.BindingAccountName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        private void GameFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // XAML 中首项 IsSelected="True" 会在 InitializeComponent 解析期间触发本事件，
            // 此时 GameListBox（位于本行之后）尚未创建。真实筛选由 InitializeCoreAsync
            // 恢复 SelectedValue 后再执行，故此处直接跳过。
            if (GameListBox is null || _viewModel is null)
                return;

            if (GameFilterCombo?.SelectedItem is ComboBoxItem item && item.Tag is string mode)
            {
                _gameFilterMode = mode;
                _viewModel.GetSettings().GameFilterMode = mode;
                _viewModel.SaveSettingsNow();
                ApplyGameSearchFilter();
            }
        }

        private void GameClearSearch_Click(object sender, RoutedEventArgs e)
        {
            GameSearchBox.Text = "";
            GameSearchBox_GotFocus(sender, e);
            GameClearSearch.Visibility = Visibility.Collapsed;
            ApplyGameSearchFilter();
        }

        private async void GameSaveBinding_Click(object sender, RoutedEventArgs e)
        {
            var game = _viewModel.SelectedGame;
            var account = _viewModel.SelectedBindingAccount ?? _viewModel.SelectedAccount;
            if (game == null)
            {
                MessageBox.Show("请先在列表中选择一个游戏", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (account == null)
            {
                MessageBox.Show("请先在右侧账号列表中选择要绑定的账号", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await RunGuardedAsync("保存绑定", async () =>
            {
                await _viewModel.SaveGameBinding(game.AppId, game.GameName, account.SteamId, account.DisplayName);
                GameScanStatus.Text = $"已绑定 {game.GameName} → {account.DisplayName}";
            });
        }

        private async void GameRemoveBinding_Click(object sender, RoutedEventArgs e)
        {
            var game = _viewModel.SelectedGame;
            if (game == null) return;

            var result = MessageBox.Show($"确定要删除 {game.GameName} 的账号绑定吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                await RunGuardedAsync("删除绑定", async () =>
                {
                    await _viewModel.RemoveGameBinding(game.AppId);
                    GameScanStatus.Text = $"已删除 {game.GameName} 的绑定";
                });
            }
        }

        /// <summary>同步版：把异常降级为状态栏提示，不让它冒成崩溃框。</summary>
        private void RunGuardedSync(string operationName, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLogger.Error($"UI operation failed: {operationName}.", ex);
                _viewModel.StatusText = $"{operationName}失败：{ex.Message}";
            }
        }

        /// <summary>
        /// 包住 async void 事件处理器的异步体：任一异常都会冒到
        /// DispatcherUnhandledException 弹出致命错误框，这里统一降级为状态栏提示。
        /// </summary>
        private async Task RunGuardedAsync(string operationName, Func<Task> action, string? successStatus = null)
        {
            try
            {
                await action();
                if (successStatus != null)
                    _viewModel.StatusText = successStatus;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"UI operation failed: {operationName}.", ex);
                _viewModel.StatusText = $"{operationName}失败：{ex.Message}";
            }
        }

        private void GameLaunchWorkflow_Click(object sender, RoutedEventArgs e)
        {
            var game = _viewModel.SelectedGame;
            if (game == null || !_viewModel.SelectedGameHasBinding)
            {
                MessageBox.Show(this, "请先为游戏保存账号绑定。", "游戏启动工作流", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dialog = new GameLaunchWorkflowWindow(_viewModel, game) { Owner = this };
            if (dialog.ShowDialog() == true)
                GameScanStatus.Text = $"已保存 {game.GameName} 的启动工作流";
        }

        private void GameCreateDesktopShortcut_Click(object sender, RoutedEventArgs e)
        {
            var selected = _viewModel.SelectedGame;
            if (selected == null || !selected.HasBinding)
            {
                MessageBox.Show(this, "请先为游戏保存账号绑定。", "创建游戏快捷方式", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var steamService = _viewModel.GetAccountManager().GetSteamService();
            var game = steamService.GetInstalledGames().FirstOrDefault(item => item.AppId == selected.AppId);
            if (game == null)
            {
                MessageBox.Show(this, "未找到该游戏的本地安装信息。", "创建游戏快捷方式", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                var shortcut = GameShortcutService.CreateDesktopShortcut(game, steamService.SteamPath ?? string.Empty);
                GameScanStatus.Text = $"已创建桌面快捷方式：{Path.GetFileName(shortcut)}";
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unable to create game desktop shortcut.", ex);
                MessageBox.Show(this, ex.Message, "创建游戏快捷方式失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GamePinToggle_Click(object sender, RoutedEventArgs e)
        {
            var game = _viewModel.SelectedGame;
            if (game == null) return;

            _viewModel.ToggleGamePin(game.AppId);
            // 用 SetCurrentValue：直接赋值会顶掉 XAML 的 OneWay 绑定，
            // 之后切换选中项时按钮不再跟随 SelectedGame.IsPinned。
            GamePinToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, _viewModel.IsGamePinned(game.AppId));

            if (game.IsPinned && !game.HasBinding)
            {
                MessageBox.Show("该游戏尚未绑定账号，请先添加账号绑定后再固定到任务栏。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void GameItemPinToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameListViewModel game)
            {
                _viewModel.ToggleGamePin(game.AppId);
                game.IsPinned = _viewModel.IsGamePinned(game.AppId);

                if (game.IsPinned && !game.HasBinding)
                {
                    MessageBox.Show("该游戏尚未绑定账号，请先添加账号绑定后再固定到任务栏。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                GamePinToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, game.IsPinned);
            }
        }

        private void ManualAddToggle_Click(object sender, RoutedEventArgs e)
        {
            ManualAddPanel.Visibility = ManualAddPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private async void ManualAddBinding_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(ManualAppId.Text?.Trim(), out int appId) || appId <= 0 || appId > 99999999)
            {
                MessageBox.Show("请输入有效的 AppID (1-99999999)", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var gameName = ManualGameName.Text?.Trim();
            if (string.IsNullOrEmpty(gameName))
            {
                MessageBox.Show("请输入游戏名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (gameName.Length > 200)
            {
                MessageBox.Show("游戏名称过长，请限制在200个字符以内", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var account = _viewModel.SelectedBindingAccount ?? _viewModel.SelectedAccount;
            if (account == null)
            {
                MessageBox.Show("请先在右侧选择要绑定的账号", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await _viewModel.SaveGameBinding(appId, gameName, account.SteamId, account.DisplayName);

            ManualAppId.Text = "";
            ManualGameName.Text = "";
            ManualAddPanel.Visibility = Visibility.Collapsed;
            GameScanStatus.Text = $"已手动绑定 {gameName} → {account.DisplayName}";

            var game = _viewModel.GameList.FirstOrDefault(g => g.AppId == appId);
            if (game != null)
                _viewModel.SelectedGame = game;
        }

        private void SettingsPos_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.IsChecked == true && rb.Tag is string tag)
            {
                _viewModel.TaskbarPosition = tag switch
                {
                    "Auto" => Core.TaskbarPosition.Auto,
                    "Left" => Core.TaskbarPosition.Left,
                    "Center" => Core.TaskbarPosition.Center,
                    "Right" => Core.TaskbarPosition.Right,
                    _ => Core.TaskbarPosition.Auto
                };
                if (_taskbarBand != null && _taskbarBand.IsPinned)
                    _taskbarBand.SetPosition(_viewModel.TaskbarPosition);
            }
        }

        private void SyncTaskbarPositionRadios()
        {
            if (SettingsPosAuto == null) return;
            SettingsPosAuto.IsChecked = _viewModel.TaskbarPosition == Core.TaskbarPosition.Auto;
            SettingsPosLeft.IsChecked = _viewModel.TaskbarPosition == Core.TaskbarPosition.Left;
            SettingsPosCenter.IsChecked = _viewModel.TaskbarPosition == Core.TaskbarPosition.Center;
            SettingsPosRight.IsChecked = _viewModel.TaskbarPosition == Core.TaskbarPosition.Right;
        }

        private void SettingsTaskbarWindowSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel == null) return;
            _viewModel.TaskbarWindowSize = (int)e.NewValue;
            _taskbarBand?.SetWindowSize(_viewModel.TaskbarWindowSize);
        }

        private void TaskbarMonitor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewModel == null || sender is not ComboBox combo || combo.SelectedIndex < 0) return;
            _viewModel.TaskbarMonitorIndex = combo.SelectedIndex;
            _taskbarBand?.SetMonitorIndex(combo.SelectedIndex);
        }

        private void SettingsOffsetX_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel == null) return;
            int val = (int)e.NewValue;
            _viewModel.TaskbarOffsetX = val;
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetOffset(_viewModel.TaskbarOffsetX, _viewModel.TaskbarOffsetY);
        }

        private void SettingsOffsetY_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel == null) return;
            int val = (int)e.NewValue;
            _viewModel.TaskbarOffsetY = val;
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetOffset(_viewModel.TaskbarOffsetX, _viewModel.TaskbarOffsetY);
        }

        private void ResetLayout_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;
            _viewModel.TaskbarPosition = Core.TaskbarPosition.Right;
            _viewModel.TaskbarOffsetX = 0;
            _viewModel.TaskbarOffsetY = 0;
            _viewModel.TaskbarWindowSize = 2;
            _viewModel.TaskbarAvatarSize = 38;
            _viewModel.DesktopFloatingOpacity = 80;
            _viewModel.DesktopFloatingAvatarSize = 45;
            _viewModel.DesktopFloatingGlassColor = "#4A90D9";
            _viewModel.DesktopFloatingTopmost = true;
            _viewModel.DesktopFloatingLocked = false;

            if (SettingsFloatingOpacitySlider != null) SettingsFloatingOpacitySlider.Value = _viewModel.DesktopFloatingOpacity;
            if (SettingsFloatingAvatarSlider != null) SettingsFloatingAvatarSlider.Value = _viewModel.DesktopFloatingAvatarSize;

            _taskbarBand?.SetPosition(_viewModel.TaskbarPosition);
            _taskbarBand?.SetOffset(_viewModel.TaskbarOffsetX, _viewModel.TaskbarOffsetY);
            _taskbarBand?.SetWindowSize(_viewModel.TaskbarWindowSize);
            _taskbarBand?.SetAvatarSize(_viewModel.TaskbarAvatarSize);
            _desktopFloatingWindow?.SetOpacityPercent(_viewModel.DesktopFloatingOpacity);
            _desktopFloatingWindow?.SetAvatarSize(_viewModel.DesktopFloatingAvatarSize);
            _desktopFloatingWindow?.SetGlassColor(_viewModel.DesktopFloatingGlassColor);
            _desktopFloatingWindow?.SetTopmostEnabled(_viewModel.DesktopFloatingTopmost);
            _desktopFloatingWindow?.SetLocked(_viewModel.DesktopFloatingLocked);

            _viewModel.StatusText = "已重置任务栏和悬浮窗布局";
        }

        private void SettingsTaskbarAvatar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel == null) return;
            int val = (int)e.NewValue;
            _viewModel.TaskbarAvatarSize = val;
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetAvatarSize(val);
        }

        private void SettingsFloatingAvatar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel == null) return;
            int val = (int)e.NewValue;
            _viewModel.DesktopFloatingAvatarSize = val;
            SettingsFloatingAvatarText.Text = val.ToString();
            _desktopFloatingWindow?.SetAvatarSize(val);
        }

        private void SettingsTaskbarGlass_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.TaskbarGlassEnabled = val;
                if (_taskbarBand != null && _taskbarBand.IsPinned)
                    _taskbarBand.SetGlassEnabled(val);
            }
        }

        private void SettingsFloatingGlass_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.DesktopFloatingGlassEnabled = val;
                _desktopFloatingWindow?.SetGlassEnabled(val);
            }
        }

        private void GlassColor_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string color)
            {
                _viewModel.DesktopFloatingGlassColor = color;
                _desktopFloatingWindow?.SetGlassColor(color);
            }
        }

        private void SettingsTaskbarRounded_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.TaskbarRoundedMode = val;
                if (_taskbarBand != null && _taskbarBand.IsPinned)
                    _taskbarBand.SetRoundedMode(val);
            }
        }

        private void SetTaskbarLyricsEnabled(bool enabled)
        {
            if (enabled && !_viewModel.TaskbarLyricsModuleEnabled)
            {
                // 模块总开关已关闭：忽略开启请求，保持歌词收起并提示去设置里开模块。
                _viewModel.TaskbarLyricsEnabled = false;
                ApplyTaskbarLyricsSettings();
                SyncTrayFeatureStates();
                _viewModel.StatusText = "任务栏歌词模块已关闭，可在偏好设置中重新开启";
                return;
            }
            _viewModel.TaskbarLyricsEnabled = enabled;
            ApplyTaskbarLyricsSettings();
            SyncTrayFeatureStates();
            _viewModel.StatusText = enabled ? "任务栏歌词已开启" : "任务栏歌词已关闭";
        }

        private bool EffectiveTaskbarLyricsEnabled =>
            _viewModel.TaskbarLyricsModuleEnabled && _viewModel.TaskbarLyricsEnabled;

        private void SyncTrayFeatureStates()
        {
            _trayIcon?.UpdateFeatureStates(EffectiveTaskbarLyricsEnabled);
        }

        private void SettingsGameSaveCenter_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _viewModel == null) return;
            var enabled = _viewModel.GameSaveCenterEnabled;
            _viewModel.StatusText = enabled
                ? "游戏存档模块已开启，自动备份已恢复"
                : "游戏存档模块已关闭，存档监控与自动备份已停止";
            if (!enabled && SaveCenterHost.Visibility == Visibility.Visible)
                SwitchToAccounts();
        }

        private void SettingsDesktopIconHider_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _viewModel == null) return;
            ConfigureDesktopEnhance();
            _viewModel.StatusText = _viewModel.DesktopIconHiderEnabled
                ? "桌面图标模块已开启"
                : "桌面图标模块已关闭，图标已恢复显示";
            if (!_viewModel.DesktopIconHiderEnabled && DesktopIconsView.Visibility == Visibility.Visible)
                SwitchToAccounts();
        }

        private void SettingsTaskbarLyricsModule_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _viewModel == null) return;
            if (!_viewModel.TaskbarLyricsModuleEnabled)
            {
                SetTaskbarLyricsEnabled(false);
                if (TaskbarLyricsView.Visibility == Visibility.Visible ||
                    TaskbarLyricsAdvancedView.Visibility == Visibility.Visible)
                    SwitchToAccounts();
            }
            else
            {
                _viewModel.StatusText = "任务栏歌词模块已开启";
            }
        }

        private void TaskbarLyrics_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _viewModel == null) return;
            ApplyTaskbarLyricsSettings();
            SyncTrayFeatureStates();
        }

        private void TaskbarLyricsWidth_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _viewModel == null) return;
            var width = Math.Clamp((int)Math.Round(e.NewValue), 240, 720);
            _viewModel.TaskbarLyricsWidth = width;
            ApplyTaskbarLyricsSettings();
        }

        private void TaskbarLyricsPolling_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _viewModel == null) return;
            _viewModel.TaskbarLyricsPollingIntervalMs = Math.Clamp((int)Math.Round(e.NewValue), 60, 500);
            ApplyTaskbarLyricsSettings();
        }

        private void TaskbarLyricsVertical_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _viewModel == null) return;
            _viewModel.TaskbarLyricsVerticalOffset = Math.Clamp((int)Math.Round(e.NewValue), -12, 12);
            ApplyTaskbarLyricsSettings();
        }

        private void TaskbarLyricsFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _viewModel == null) return;
            _viewModel.TaskbarLyricsFontSize = Math.Clamp((int)Math.Round(e.NewValue), 12, 18);
            ApplyTaskbarLyricsSettings();
        }

        private bool _synchronizingLyricsFontCombos;

        private void TaskbarLyricsFontFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _synchronizingLyricsFontCombos || _viewModel == null ||
                sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string fontFamily } })
                return;

            _viewModel.TaskbarLyricsFontFamily = fontFamily;
            SyncLyricsFontCombos();
            ApplyTaskbarLyricsSettings();
        }

        private void SyncLyricsFontCombos()
        {
            if (_viewModel == null || _synchronizingLyricsFontCombos) return;
            _synchronizingLyricsFontCombos = true;
            try
            {
                var currentFont = string.IsNullOrWhiteSpace(_viewModel.TaskbarLyricsFontFamily)
                    ? "Microsoft YaHei UI"
                    : _viewModel.TaskbarLyricsFontFamily.Trim();

                foreach (var combo in new[] { TaskbarLyricsPrimaryFontCombo, TaskbarLyricsAdvancedFontCombo, SettingsLyricsFontCombo })
                {
                    if (combo == null) continue;
                    var existing = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                        string.Equals(item.Tag?.ToString(), currentFont, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        existing = new ComboBoxItem
                        {
                            Tag = currentFont,
                            Content = $"{currentFont}（自定义）",
                            FontFamily = TaskbarBandWindow.BuildLyricsFontFamily(currentFont)
                        };
                        combo.Items.Add(existing);
                    }
                    if (!ReferenceEquals(combo.SelectedItem, existing))
                        combo.SelectedItem = existing;
                }
            }
            finally
            {
                _synchronizingLyricsFontCombos = false;
            }
        }

        private void LyricOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _viewModel == null) return;
            var offset = (int)Math.Round(e.NewValue);
            _viewModel.LyricOffsetMs = offset;
            ApplyTaskbarLyricsSettings();
        }

        /// <summary>把歌词设置推送到任务栏歌词岛。参数一律按名绑定，
        /// 避免 20+ 个位置参数在任一端插值时静默串行错位。</summary>
        private void ApplyLyricsSettingsToBand()
        {
            _taskbarBand?.SetLyricsSettings(
                enabled: EffectiveTaskbarLyricsEnabled,
                showControls: _viewModel.TaskbarLyricsShowControls,
                onlineLyrics: _viewModel.TaskbarLyricsOnlineEnabled,
                width: _viewModel.TaskbarLyricsWidth,
                sourceQQ: _viewModel.LyricSourceQQEnabled,
                sourceNetease: _viewModel.LyricSourceNeteaseEnabled,
                sourceKugou: _viewModel.LyricSourceKugouEnabled,
                sourceLrclib: _viewModel.LyricSourceLrclibEnabled,
                offsetMs: _viewModel.LyricOffsetMs,
                compositeMode: true,
                artworkRounded: _viewModel.TaskbarLyricsArtworkRounded,
                showVisualizer: _viewModel.TaskbarLyricsShowVisualizer,
                adaptiveWidth: _viewModel.TaskbarLyricsAdaptiveWidth,
                trackInfoOnly: _viewModel.TaskbarLyricsTrackInfoOnly,
                showSecondaryLine: _viewModel.TaskbarLyricsShowSecondaryLine,
                pollingIntervalMs: _viewModel.TaskbarLyricsPollingIntervalMs,
                verticalOffset: _viewModel.TaskbarLyricsVerticalOffset,
                fontSize: _viewModel.TaskbarLyricsFontSize,
                transparentBackground: _viewModel.TaskbarLyricsTransparentBackground,
                fontFamily: _viewModel.TaskbarLyricsFontFamily,
                splitLongLines: _viewModel.LyricSplitLongLines,
                showProgressBar: _viewModel.TaskbarLyricsShowProgressBar,
                hideWhenNoMedia: _viewModel.TaskbarLyricsHideWhenNoMedia,
                onlyMusicLyrics: _viewModel.TaskbarLyricsMusicOnly);
        }

        private void ApplyTaskbarLyricsSettings()
        {
            SyncLyricsFontCombos();
            if (EffectiveTaskbarLyricsEnabled || _viewModel.IsTaskbarPinned)
            {
                if (_taskbarBand == null)
                {
                    AttachToTaskbar();
                }
                else
                {
                    ApplyLyricsSettingsToBand();
                }
            }
            else
            {
                if (_taskbarBand != null && !_viewModel.IsTaskbarPinned)
                {
                    DetachFromTaskbar();
                }
                else
                {
                    ApplyLyricsSettingsToBand();
                }
            }
            SyncTrayFeatureStates();
        }

        private void DesktopEnhance_Changed(object sender, RoutedEventArgs e) => ConfigureDesktopEnhance();

        private void DesktopHideMode_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { IsChecked: true, Tag: string value } &&
                Enum.TryParse<DesktopIconHideMode>(value, out var mode))
            {
                _viewModel.DesktopIconHideMode = mode;
                ConfigureDesktopEnhance();
            }
        }

        private void DesktopHideNow_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.DesktopIconHiderEnabled)
            {
                _viewModel.StatusText = "桌面图标模块已关闭，可在偏好设置中重新开启";
                return;
            }
            if (!_viewModel.DesktopAutoHideIconsEnabled)
            {
                _viewModel.StatusText = "请先开启桌面图标智能隐藏";
                return;
            }

            _viewModel.StatusText = _desktopIconService.HideIcons()
                ? "桌面图标已隐藏，可按设置的恢复方式重新显示"
                : "未找到可控制的桌面图标窗口；请稍候重试或重启 Explorer";
        }

        private void DesktopShowNow_Click(object sender, RoutedEventArgs e)
        {
            _desktopIconService.ShowIcons();
            _viewModel.StatusText = "桌面图标已恢复显示";
        }

        private void DesktopEnhanceReset_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.DesktopAutoHideIconsEnabled = false;
            _viewModel.DesktopIconHideMode = DesktopIconHideMode.Smart;
            _viewModel.DesktopIconIdleSeconds = 30;
            _viewModel.DesktopRestoreByMouseMove = true;
            _viewModel.DesktopRestoreByTaskbarClick = true;
            _viewModel.DesktopToggleIconsOnClick = false;
            _viewModel.DesktopIconAnimationType = DesktopIconAnimationType.Fade;
            _viewModel.DesktopIconAnimationDuration = 300;
            _viewModel.DesktopImmersiveModeEnabled = false;
            _desktopIconService.ShowIcons();
            ConfigureDesktopEnhance();
            _viewModel.StatusText = "智能桌面增强已恢复默认设置";
        }

        private void DesktopImmersiveMode_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.DesktopIconHiderEnabled)
            {
                _viewModel.StatusText = "桌面图标模块已关闭，可在偏好设置中重新开启";
                return;
            }
            _viewModel.DesktopImmersiveModeEnabled = true;
            _viewModel.DesktopAutoHideIconsEnabled = true;
            ConfigureDesktopEnhance();
            _viewModel.StatusText = "已进入沉浸桌面模式：桌面图标自动隐藏已开启";
        }

        private void DesktopDelayPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string value } || !int.TryParse(value, out var seconds)) return;
            _viewModel.DesktopIconIdleSeconds = Math.Clamp(seconds, 5, 300);
            ConfigureDesktopEnhance();
        }

        private void ConfigureDesktopEnhance() => _desktopIconService.Configure(_viewModel.CreateDesktopEnhanceSettings());

        private void SyncDesktopEnhanceControls()
        {
            if (DesktopModeSmart != null) DesktopModeSmart.IsChecked = _viewModel.DesktopIconHideMode == DesktopIconHideMode.Smart;
            if (DesktopModeTimed != null) DesktopModeTimed.IsChecked = _viewModel.DesktopIconHideMode == DesktopIconHideMode.Timed;
            if (DesktopModeManual != null) DesktopModeManual.IsChecked = _viewModel.DesktopIconHideMode == DesktopIconHideMode.Manual;
        }

        private void OnDesktopIconVisibilityChanged(object? sender, bool visible) => PostToUi(() =>
        {
            _viewModel.DesktopIconsVisible = visible;
            _viewModel.DesktopIconsHiddenByApp = !visible;
        });

        private void OnDesktopIconCountdownChanged(object? sender, int seconds) => PostToUi(() =>
        {
            _viewModel.DesktopAutoHideCountdown = seconds;
        });

        private void SettingsFloatingRounded_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.DesktopFloatingRoundedMode = val;
                _desktopFloatingWindow?.SetRoundedMode(val);
            }
        }

        private void ThemeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _viewModel == null) return;
            if (sender is CheckBox cb)
            {
                _viewModel.IsDarkTheme = cb.IsChecked == true;
            }
        }

        private async void DesktopFloating_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            if (_viewModel.GetSettings().PinnedGameIds.Count > 0 && _viewModel.GameList.Count == 0)
            {
                await RunGuardedAsync("扫描游戏", () => _viewModel.ScanGamesAsync());
            }

            AttachDesktopFloating();
        }

        private void DesktopFloating_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            DetachDesktopFloating();
        }

        private void SettingsFloatingTopmost_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.DesktopFloatingTopmost = val;
                _desktopFloatingWindow?.SetTopmostEnabled(val);
            }
        }

        private void SettingsFloatingLocked_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.DesktopFloatingLocked = val;
                _desktopFloatingWindow?.SetLocked(val);
            }
        }

        private void SettingsFloatingOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel == null) return;
            int val = (int)e.NewValue;
            _viewModel.DesktopFloatingOpacity = val;
            SettingsFloatingOpacityText.Text = $"{val}%";
            _desktopFloatingWindow?.SetOpacityPercent(val);
        }

        private void SettingsStartWithWindows_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.StartWithWindows = val;
            }
        }

        private void SettingsAutoStartSteam_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.AutoStartSteam = val;
            }
        }

        private void SettingsMinimizeToTray_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.MinimizeToTray = val;
            }
        }

        private void SettingsAutoScanGames_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.AutoScanGamesOnStartup = val;
            }
        }

        private bool _synchronizingSaveSettings;

        private void SyncGameSaveSettingsUi()
        {
            if (_viewModel == null) return;
            _synchronizingSaveSettings = true;
            try
            {
                SelectNumericSetting(SettingsGameSaveIntervalCombo, _viewModel.GameSavePeriodicIntervalMinutes, "分钟");
                SelectNumericSetting(SettingsGameSaveRetentionCombo, _viewModel.GameSaveManualRetention, "份");
            }
            finally { _synchronizingSaveSettings = false; }
        }

        private static void SelectNumericSetting(ComboBox combo, int value, string unit)
        {
            if (combo == null) return;
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(candidate =>
                int.TryParse(candidate.Tag?.ToString(), out var current) && current == value);
            if (item == null)
            {
                item = new ComboBoxItem { Tag = value.ToString(), Content = $"{value} {unit}" };
                combo.Items.Add(item);
            }
            combo.SelectedItem = item;
        }

        private void SettingsGameSaveAutoBackup_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
                _viewModel.GameSaveAutoBackupEnabled = cb.IsChecked == true;
        }

        private void SettingsGameSavePeriodic_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
                _viewModel.GameSavePeriodicBackupEnabled = cb.IsChecked == true;
        }

        private void SettingsGameSaveInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_synchronizingSaveSettings) return;
            if (SettingsGameSaveIntervalCombo?.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag && int.TryParse(tag, out var minutes) &&
                _viewModel != null)
            {
                _viewModel.GameSavePeriodicIntervalMinutes = minutes;
            }
        }

        private void SettingsGameSaveRetention_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_synchronizingSaveSettings) return;
            if (SettingsGameSaveRetentionCombo?.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag && int.TryParse(tag, out var count) &&
                _viewModel != null)
            {
                _viewModel.GameSaveManualRetention = count;
            }
        }

        private void SettingsSmartSaveDiscovery_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
                _viewModel.SmartSaveDiscoveryEnabled = cb.IsChecked == true;
        }

        private void SettingsLibraryInjection_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.EnableLibraryInjection = val;
            }
        }

        private void SettingsSilentCloseSteam_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.SilentCloseSteam = val;
            }
        }

        private void SettingsBetaUpdateChannel_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                _viewModel.UseBetaUpdateChannel = cb.IsChecked == true;
            }
        }

        private void SettingsCheckUpdate_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.CheckUpdateOnStartup = val;
            }
        }

        private void SettingsAutoInstallUpdates_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.AutoInstallUpdates = val;
            }
        }

        private void SettingsNotificationOnClose_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.ShowNotificationOnSteamClose = val;
            }
        }

        private void SettingsMinimalMode_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.MinimalMode = val;
            }
        }

        private void SettingsFloatingDock_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.DesktopFloatingDockEnabled = val;
            }
        }

        private void SettingsPlaySound_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                _viewModel.PlaySoundOnSwitch = cb.IsChecked == true;
            }
        }

        private void SettingsShowSwitchNotification_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                _viewModel.ShowSwitchNotification = cb.IsChecked == true;
            }
        }

        private void OpenAppData_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = SettingsService.SettingsDirectory;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using var _p1 = Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                if (_viewModel != null) _viewModel.StatusText = $"打开目录失败: {ex.Message}";
            }
        }

        private void OpenSteamDirectory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = _viewModel?.GetAccountManager()?.GetSteamService()?.SteamPath;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    using var _p2 = Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
                }
                else
                {
                    if (_viewModel != null) _viewModel.StatusText = "未找到 Steam 安装路径";
                }
            }
            catch (Exception ex)
            {
                if (_viewModel != null) _viewModel.StatusText = $"打开 Steam 目录失败: {ex.Message}";
            }
        }

        private async void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cacheDir = Path.Combine(SettingsService.SettingsDirectory, "Cache");
                var artworkDir = Path.Combine(SettingsService.SettingsDirectory, "ArtworkCache");
                var lyricsDir = Path.Combine(SettingsService.SettingsDirectory, "LyricsCache");

                var failures = await Task.Run(() =>
                {
                    var failed = new List<string>();
                    foreach (var dir in new[] { cacheDir, artworkDir, lyricsDir })
                    {
                        if (!Directory.Exists(dir)) continue;
                        try
                        {
                            Directory.Delete(dir, true);
                        }
                        catch (Exception ex)
                        {
                            failed.Add($"{Path.GetFileName(dir)}：{ex.Message}");
                        }
                    }
                    return failed;
                });

                if (_viewModel != null)
                    _viewModel.StatusText = failures.Count == 0
                        ? "已成功清理本地封面与歌词缓存"
                        : $"缓存未完全清理：{string.Join("；", failures)}";
            }
            catch (Exception ex)
            {
                if (_viewModel != null) _viewModel.StatusText = $"清理缓存失败: {ex.Message}";
            }
        }

        private async void RedetectSteam_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.InitializeAsync();
                if (_viewModel.AutoScanGamesOnStartup)
                {
                    await _viewModel.ScanGamesAsync();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Steam re-detection failed.", ex);
                _viewModel.StatusText = $"重新检测 Steam 失败：{ex.Message}";
            }
        }

        private void OnSettingsPositionChanged(object? sender, Core.TaskbarPosition position)
        {
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetPosition(position);
        }

        private void OnSettingsOffsetChanged(object? sender, int offset)
        {
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetOffset(offset, _viewModel.TaskbarOffsetY);
        }

        private void OnSettingsAvatarSizeChanged(object? sender, int size)
        {
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetAvatarSize(size);
            _desktopFloatingWindow?.SetAvatarSize(size);
        }

        private void OnSettingsGlassChanged(object? sender, bool enabled)
        {
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetGlassEnabled(enabled);
            _desktopFloatingWindow?.SetGlassEnabled(enabled);
        }

        private void OnSettingsRoundedChanged(object? sender, bool rounded)
        {
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetRoundedMode(rounded);
            _desktopFloatingWindow?.SetRoundedMode(rounded);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
            SyncTrayFeatureStates();
        }

        private void TaskbarPin_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            AttachToTaskbar();
        }

        private void TaskbarPin_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            DetachFromTaskbar();
        }

        private void AttachToTaskbar()
        {
            if (_taskbarBand != null)
            {
                if (_taskbarBand.IsPinned)
                    return;

                // 已存在但没贴住（例如嵌入失败、或无媒体时自动撤下）：
                // 直接 new 会丢掉旧实例的引用，它的媒体轮询、定位计时器和
                // 事件订阅全都泄漏，而且两个岛会争抢任务栏宿主。
                DetachFromTaskbar(updateSettings: false);
            }

            var pinnedAccounts = _viewModel.GetPinnedAccounts();
            var pinnedGames = _viewModel.GetPinnedGames();
            if (pinnedAccounts.Count == 0 && pinnedGames.Count == 0 && !_viewModel.TaskbarLyricsEnabled)
            {
                _viewModel.StatusText = "请先开启任务栏歌词，或固定至少一个账号/游戏";
                _viewModel.IsTaskbarPinned = false;
                return;
            }

            _taskbarBand = new TaskbarBandWindow(_viewModel.GetAccountManager());
            _taskbarBand.AccountSwitchRequested += OnTaskbarAccountSwitch;
            _taskbarBand.ForceAccountRestartRequested += OnTaskbarForceAccountRestart;
            _taskbarBand.GameLaunchRequested += OnTaskbarGameLaunch;
            _taskbarBand.ShowMainWindowRequested += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };
            _taskbarBand.DetachRequested += (s, e) => DetachFromTaskbar();
            _taskbarBand.ToggleDesktopFloatingRequested += (s, enabled) =>
            {
                if (enabled)
                    AttachDesktopFloating();
                else
                    DetachDesktopFloating();
            };
            _taskbarBand.ToggleDesktopFloatingTopmostRequested += (s, enabled) =>
            {
                _viewModel.DesktopFloatingTopmost = enabled;
                _desktopFloatingWindow?.SetTopmostEnabled(enabled);
            };
            _taskbarBand.ToggleTaskbarPinnedRequested += (s, enabled) =>
            {
                // 之前只实现了解除分支：从菜单勾上「固定到任务栏」没有任何反应。
                if (enabled)
                    AttachToTaskbar();
                else
                    DetachFromTaskbar();
            };
            _taskbarBand.ToggleTaskbarLyricsRequested += (s, enabled) => SetTaskbarLyricsEnabled(enabled);
            _taskbarBand.ExitRequested += (s, e) => RequestExit();
            _taskbarBand.QuickLaunchRequested += (s, id) => RunGuardedSync("启动项目", () => _viewModel.LaunchQuickLaunchItem(id));
            _taskbarBand.PanelItemOrderChanged += async (s, keys) =>
                await RunGuardedAsync("保存面板顺序", () => _viewModel.SetPinnedPanelItemOrderAsync(keys));
            _taskbarBand.AccountOrderChanged += (s, ids) => RunGuardedSync("保存账号顺序", () => _viewModel.SetPinnedAccountOrder(ids));
            _taskbarBand.GameDeleteRequested += (s, index) => RunGuardedSync("移除固定游戏", () => _viewModel.RemovePinnedGame(index));
            _taskbarBand.QuickLaunchDeleteRequested += async (s, id) =>
                await RunGuardedAsync("移除固定项目", () => _viewModel.DeletePinnedQuickLaunchItemAsync(id));
            _taskbarBand.AccountDeleteRequested += (s, index) => RunGuardedSync("移除固定账号", () => _viewModel.RemovePinnedAccount(index));
            _taskbarBand.SetViewModel(_viewModel);
            _taskbarBand.SetPinnedAccounts(pinnedAccounts);
            _taskbarBand.SetPinnedGames(pinnedGames);
            _taskbarBand.SetPinnedQuickLaunchItems(_viewModel.GetPinnedQuickLaunchItems());
            
            _taskbarBand.SetPosition(_viewModel.TaskbarPosition);
            _taskbarBand.SetMonitorIndex(_viewModel.TaskbarMonitorIndex);
            _taskbarBand.SetOffset(_viewModel.TaskbarOffsetX, _viewModel.TaskbarOffsetY);
            _taskbarBand.SetWindowSize(_viewModel.TaskbarWindowSize);
            _taskbarBand.SetAvatarSize(_viewModel.TaskbarAvatarSize);
            _taskbarBand.SetGlassEnabled(_viewModel.TaskbarGlassEnabled);
            _taskbarBand.SetRoundedMode(_viewModel.TaskbarRoundedMode);
            _taskbarBand.SetLyricsSettings(
                _viewModel.TaskbarLyricsEnabled,
                _viewModel.TaskbarLyricsShowControls,
                _viewModel.TaskbarLyricsOnlineEnabled,
                _viewModel.TaskbarLyricsWidth,
                _viewModel.LyricSourceQQEnabled,
                _viewModel.LyricSourceNeteaseEnabled,
                _viewModel.LyricSourceKugouEnabled,
                _viewModel.LyricSourceLrclibEnabled,
                _viewModel.LyricOffsetMs,
                true,
                _viewModel.TaskbarLyricsArtworkRounded,
                _viewModel.TaskbarLyricsShowVisualizer,
                _viewModel.TaskbarLyricsAdaptiveWidth,
                _viewModel.TaskbarLyricsTrackInfoOnly,
                _viewModel.TaskbarLyricsShowSecondaryLine,
                _viewModel.TaskbarLyricsPollingIntervalMs,
                _viewModel.TaskbarLyricsVerticalOffset,
                _viewModel.TaskbarLyricsFontSize,
                _viewModel.TaskbarLyricsTransparentBackground,
                _viewModel.TaskbarLyricsFontFamily,
                _viewModel.LyricSplitLongLines,
                showProgressBar: _viewModel.TaskbarLyricsShowProgressBar,
                hideWhenNoMedia: _viewModel.TaskbarLyricsHideWhenNoMedia,
                onlyMusicLyrics: _viewModel.TaskbarLyricsMusicOnly);
            
            _taskbarBand.Attach();

            _viewModel.IsTaskbarPinned = _taskbarBand.IsPinned;
        }

        private void DetachFromTaskbar(bool updateSettings = true)
        {
            if (_taskbarBand == null) return;

            _taskbarBand.Detach();
            _taskbarBand.Close();
            _taskbarBand = null;

            if (updateSettings) _viewModel.IsTaskbarPinned = false;
        }

        private void AttachDesktopFloating()
        {
            if (_desktopFloatingWindow != null)
                return;

            var pinnedAccounts = _viewModel.GetPinnedAccounts();
            var pinnedGames = _viewModel.GetPinnedGames();
            if (pinnedAccounts.Count == 0 && pinnedGames.Count == 0)
            {
                _viewModel.StatusText = "请先固定至少一个账号或游戏";
                _viewModel.DesktopFloatingEnabled = false;
                return;
            }

            var settings = _viewModel.GetSettings();
            _desktopFloatingWindow = new DesktopFloatingWindow(_viewModel.GetAccountManager());
            _desktopFloatingWindow.AccountSwitchRequested += OnTaskbarAccountSwitch;
            _desktopFloatingWindow.GameLaunchRequested += OnTaskbarGameLaunch;
            _desktopFloatingWindow.ShowMainWindowRequested += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };
            _desktopFloatingWindow.ToggleDesktopFloatingRequested += (s, enabled) =>
            {
                if (!enabled)
                    DetachDesktopFloating();
            };
            _desktopFloatingWindow.ToggleDesktopFloatingTopmostRequested += (s, enabled) =>
            {
                _viewModel.DesktopFloatingTopmost = enabled;
                _desktopFloatingWindow?.SetTopmostEnabled(enabled);
            };
            _desktopFloatingWindow.ToggleTaskbarPinnedRequested += (s, enabled) =>
            {
                if (enabled)
                    AttachToTaskbar();
                else
                    DetachFromTaskbar();
            };
            _desktopFloatingWindow.ToggleTaskbarLyricsRequested += (s, enabled) => SetTaskbarLyricsEnabled(enabled);
            _desktopFloatingWindow.ExitRequested += (s, e) => RequestExit();
            _desktopFloatingWindow.QuickLaunchRequested += (s, id) => _viewModel.LaunchQuickLaunchItem(id);
            _desktopFloatingWindow.PanelItemOrderChanged += async (s, keys) => await _viewModel.SetPinnedPanelItemOrderAsync(keys);
            _desktopFloatingWindow.AccountOrderChanged += (s, ids) => _viewModel.SetPinnedAccountOrder(ids);
            _desktopFloatingWindow.GameDeleteRequested += (s, index) => _viewModel.RemovePinnedGame(index);
            _desktopFloatingWindow.QuickLaunchDeleteRequested += async (s, id) => await _viewModel.DeletePinnedQuickLaunchItemAsync(id);
            _desktopFloatingWindow.AccountDeleteRequested += (s, index) => _viewModel.RemovePinnedAccount(index);
            _desktopFloatingWindow.PositionChanged += (s, pos) =>
            {
                if (_configurationChangeInProgress) return;
                var currentSettings = _viewModel.GetSettings();
                currentSettings.DesktopFloatingLeft = pos.left;
                currentSettings.DesktopFloatingTop = pos.top;
                _viewModel.SaveSettingsNow();
            };
            _desktopFloatingWindow.MonitorPositionChanged += (s, pos) =>
            {
                if (_configurationChangeInProgress) return;
                var currentSettings = _viewModel.GetSettings();
                currentSettings.DesktopFloatingMonitorId = pos.MonitorId;
                currentSettings.DesktopFloatingRelativeLeft = pos.RelativeLeft;
                currentSettings.DesktopFloatingRelativeTop = pos.RelativeTop;
                _viewModel.SaveSettingsNow();
            };
            _desktopFloatingWindow.Closed += (s, e) => _desktopFloatingWindow = null;
            _desktopFloatingWindow.SetViewModel(_viewModel);
            _desktopFloatingWindow.SetPinnedAccounts(pinnedAccounts);
            _desktopFloatingWindow.SetPinnedGames(pinnedGames);
            _desktopFloatingWindow.SetPinnedQuickLaunchItems(_viewModel.GetPinnedQuickLaunchItems());
            _desktopFloatingWindow.SetAvatarSize(_viewModel.DesktopFloatingAvatarSize);
            _desktopFloatingWindow.SetGlassEnabled(_viewModel.DesktopFloatingGlassEnabled);
            _desktopFloatingWindow.SetGlassColor(_viewModel.DesktopFloatingGlassColor);
            _desktopFloatingWindow.SetRoundedMode(_viewModel.DesktopFloatingRoundedMode);
            _desktopFloatingWindow.SetTopmostEnabled(_viewModel.DesktopFloatingTopmost);
            _desktopFloatingWindow.SetLocked(_viewModel.DesktopFloatingLocked);
            _desktopFloatingWindow.SetOpacityPercent(_viewModel.DesktopFloatingOpacity);
            _desktopFloatingWindow.SetSavedPosition(settings.DesktopFloatingLeft, settings.DesktopFloatingTop);
            _desktopFloatingWindow.SetSavedMonitorPosition(settings.DesktopFloatingMonitorId,
                settings.DesktopFloatingRelativeLeft, settings.DesktopFloatingRelativeTop);
            _desktopFloatingWindow.Show();

            _viewModel.DesktopFloatingEnabled = true;
        }

        private void DetachDesktopFloating(bool updateSettings = true)
        {
            if (_desktopFloatingWindow == null)
            {
                if (updateSettings) _viewModel.DesktopFloatingEnabled = false;
                return;
            }

            _desktopFloatingWindow.Close();
            _desktopFloatingWindow = null;
            if (updateSettings) _viewModel.DesktopFloatingEnabled = false;
        }

        private async void OnTaskbarAccountSwitch(object? sender, Models.SteamAccount account)
        {
            await SwitchFromTaskbarAsync(account, forceRestart: false);
        }

        private async void OnTaskbarForceAccountRestart(object? sender, Models.SteamAccount account)
        {
            await SwitchFromTaskbarAsync(account, forceRestart: true);
        }

        private async Task SwitchFromTaskbarAsync(Models.SteamAccount account, bool forceRestart)
        {
            if (_viewModel.IsLoading || !await _accountInteractionGate.WaitAsync(0))
            {
                ShowTaskbarNotification("账号操作正在进行，请稍候", 1600, account);
                return;
            }

            var accountManager = _viewModel.GetAccountManager();
            _viewModel.IsLoading = true;
            _viewModel.StatusText = forceRestart
                ? $"正在强制重启 Steam 并切换到 {account.PersonaName}..."
                : $"正在切换到 {account.PersonaName}...";

            if (forceRestart)
                ShowTaskbarNotification($"正在强制重启 Steam 并切换到 {account.PersonaName}", 1800, account);

            try
            {
                if (!forceRestart && AccountManager.AreSameAccount(accountManager.CurrentAccount, account))
                {
                    _viewModel.StatusText = $"{account.PersonaName} 已是当前账号";
                    ShowTaskbarNotification($"{account.PersonaName} 已是当前账号", 1600, account);
                    return;
                }

                var success = await accountManager.SwitchAccountAsync(
                    account,
                    forceClose: forceRestart || _viewModel.SilentCloseSteam);
                if (!success)
                {
                    var failedMessage = forceRestart
                        ? "强制重启并切换账号失败"
                        : "切换失败，请确保 Steam 已关闭";
                    _viewModel.StatusText = failedMessage;
                    ShowTaskbarNotification(failedMessage, 2000, account);
                    return;
                }

                _viewModel.SelectedAccount = _viewModel.Accounts.FirstOrDefault(
                    item => AccountManager.AreSameAccount(item.Account, account));
                _taskbarBand?.UpdateCurrentAccount(account);
                _desktopFloatingWindow?.UpdateCurrentAccount(account);

                var shouldLaunchSteam = forceRestart || _viewModel.AutoStartSteam;
                if (shouldLaunchSteam)
                {
                    var launched = accountManager.LaunchSteam();
                    _viewModel.StatusText = launched
                        ? forceRestart
                            ? $"已切换到 {account.PersonaName}，Steam 已重启"
                            : $"已切换到 {account.PersonaName}，Steam 已启动"
                        : $"已切换到 {account.PersonaName}，但 Steam 启动失败";
                }
                else
                {
                    _viewModel.StatusText = $"已切换到 {account.PersonaName}";
                }

                ShowTaskbarNotification(_viewModel.StatusText, 1900, account);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Taskbar account switch failed.", ex);
                _viewModel.StatusText = forceRestart ? "强制重启并切换账号失败" : "账号切换失败";
                ShowTaskbarNotification(_viewModel.StatusText, 2000, account);
            }
            finally
            {
                _viewModel.IsSteamRunning = accountManager.GetSteamService().IsSteamRunning();
                _viewModel.IsLoading = false;
                _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
                SyncTrayFeatureStates();
                _accountInteractionGate.Release();
            }
        }

        private async void OnTaskbarGameLaunch(object? sender, int appId)
        {
            try
            {
                await _viewModel.LaunchPinnedGameAsync(appId);
                _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
                SyncTrayFeatureStates();
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Taskbar game launch failed for AppId={appId}.", ex);
                _viewModel.StatusText = "游戏启动失败，请查看日志";
                try { ShowTaskbarNotification("游戏启动失败，请查看日志", 2000); } catch { }
            }
        }

        // Quick Launch event handlers

        private async void QuickLaunchAdd_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "程序或快捷方式 (*.exe;*.lnk;*.url;*.appref-ms)|*.exe;*.lnk;*.url;*.appref-ms|程序 (*.exe)|*.exe|快捷方式 (*.lnk;*.url;*.appref-ms)|*.lnk;*.url;*.appref-ms|所有文件 (*.*)|*.*",
                InitialDirectory = GetQuickLaunchInitialDirectory(),
                Title = "选择要添加到快速启动的程序或快捷方式"
            };
            AddQuickLaunchDesktopPlaces(dialog);

            if (dialog.ShowDialog() == true)
            {
                await RunGuardedAsync("添加快速启动项", async () =>
                {
                    await _viewModel.AddQuickLaunchItemAsync(dialog.FileName);
                    QuickLaunchEmptyHint.Visibility = _viewModel.QuickLaunchList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                });
            }
        }

        private async void SelectCustomSteamPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择 Steam 安装目录" };
            if (dialog.ShowDialog() == true)
            {
                _viewModel.CustomSteamPath = dialog.FolderName;
                await RunGuardedAsync("重新初始化", () => _viewModel.InitializeAsync());
            }
        }

        private async void SelectCustomLoginUsersPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Steam 用户数据 (loginusers.vdf)|loginusers.vdf|所有文件 (*.*)|*.*",
                Title = "选择 loginusers.vdf"
            };
            if (dialog.ShowDialog() == true)
            {
                _viewModel.CustomLoginUsersPath = dialog.FileName;
                await RunGuardedAsync("重新初始化", () => _viewModel.InitializeAsync());
            }
        }

        private async void ResetCustomSteamPaths_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.CustomSteamPath = string.Empty;
            _viewModel.CustomLoginUsersPath = string.Empty;
            await RunGuardedAsync("重新初始化", () => _viewModel.InitializeAsync());
        }

        private async void QuickLaunchAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择要添加到快速启动的文件夹" };
            if (dialog.ShowDialog() == true)
            {
                await RunGuardedAsync("添加快速启动项", async () =>
                {
                    await _viewModel.AddQuickLaunchItemAsync(dialog.FolderName);
                    QuickLaunchEmptyHint.Visibility = _viewModel.QuickLaunchList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                });
            }
        }

        private static string GetQuickLaunchInitialDirectory()
        {
            var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (Directory.Exists(commonDesktop) && ContainsQuickLaunchShortcut(commonDesktop))
                return commonDesktop;

            var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (Directory.Exists(userDesktop))
                return userDesktop;

            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private static bool ContainsQuickLaunchShortcut(string directory)
        {
            try
            {
                return Directory.EnumerateFiles(directory)
                    .Any(path =>
                    {
                        var extension = Path.GetExtension(path);
                        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                               extension.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
                               extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase);
                    });
            }
            catch
            {
                return false;
            }
        }

        private static void AddQuickLaunchDesktopPlaces(Microsoft.Win32.OpenFileDialog dialog)
        {
            var desktopPaths = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };

            foreach (var path in desktopPaths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                dialog.CustomPlaces.Add(new Microsoft.Win32.FileDialogCustomPlace(path));
        }

        private async void QuickLaunchRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string id)
            {
                await RunGuardedAsync("删除快速启动项", async () =>
                {
                    await _viewModel.RemoveQuickLaunchItemAsync(id);
                    QuickLaunchEmptyHint.Visibility = _viewModel.QuickLaunchList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                });
            }
        }

        private async void QuickLaunchPinToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string id)
            {
                try { await RunGuardedAsync("固定快速启动项", () => _viewModel.ToggleQuickLaunchPinAsync(id)); }
                finally
                {
                    // ToggleButton changes its visual value before Click. Restore
                    // the persisted state when the operation failed.
                    if (sender is ToggleButton toggle) toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
                }
            }
        }


        private async void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_lastDiagnosticReport))
                    RunDiagnostics_Click(sender, e);
                await UpdateLyricsConnectivityDiagnosticAsync();
                Clipboard.SetText(_lastDiagnosticReport);
                if (_viewModel != null) _viewModel.StatusText = "已将诊断信息复制到剪贴板";
            }
            catch (Exception ex)
            {
                if (_viewModel != null) _viewModel.StatusText = $"复制诊断失败: {ex.Message}";
            }
        }

        private void ResetWindowLayout_Click(object sender, RoutedEventArgs e)
        {
            // 与 MainWindow.xaml 中的 Height="900" Width="1360" 保持一致。
            // 旧实现写死 980x680，但窗口 MinWidth=1120/MinHeight=760，实际从未生效过。
            const double defaultWidth = 1360;
            const double defaultHeight = 900;

            // 1) 最大化时 WPF 会忽略对 Width/Height 的赋值，必须先还原到正常状态，
            //    否则按钮点了没反应。
            if (WindowState != WindowState.Normal)
                WindowState = WindowState.Normal;
            Width = defaultWidth;
            Height = defaultHeight;

            // 2) 清空持久化布局。否则下次启动会把"刚重置掉"的位置原样恢复回来。
            var settings = _viewModel.GetSettings();
            settings.MainWindowLeft = null;
            settings.MainWindowTop = null;
            settings.MainWindowWidth = null;
            settings.MainWindowHeight = null;
            settings.MainWindowMaximized = false;

            // 3) 在当前窗口所在显示器的工作区内居中。旧实现用 SystemParameters.PrimaryScreen*
            //    计算：窗口在副屏时会被拽到主屏，且 PrimaryScreenWidth 是含任务栏的全屏宽度，
            //    窗口中心可能落到工作区外。运行时改 WindowStartupLocation 也是无效操作，已删除。
            var centered = MainWindowLayout.CenterIn(new Size(defaultWidth, defaultHeight), GetCurrentMonitorWorkArea());
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = centered.Left;
            Top = centered.Top;

            _viewModel.SaveSettingsNow();
            if (_viewModel != null) _viewModel.StatusText = "已重置主窗口默认尺寸与居中位置";
        }

        private void ResetLyricsSettings_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.TaskbarLyricsWidth = 320;
            _viewModel.TaskbarLyricsFontSize = 14;
            _viewModel.TaskbarLyricsFontFamily = "Microsoft YaHei UI";
            _viewModel.TaskbarLyricsAdaptiveWidth = true;
            _viewModel.TaskbarLyricsShowSecondaryLine = true;
            _viewModel.TaskbarLyricsShowProgressBar = true;
            _viewModel.TaskbarLyricsHideWhenNoMedia = true;
            _viewModel.TaskbarLyricsTransparentBackground = true;
            _viewModel.TaskbarLyricsArtworkRounded = true;
            _viewModel.TaskbarLyricsShowVisualizer = true;
            _viewModel.TaskbarLyricsShowControls = true;
            _viewModel.TaskbarLyricsTrackInfoOnly = false;
            _viewModel.LyricSplitLongLines = false;
            _viewModel.LyricOffsetMs = 0;
            _viewModel.TaskbarLyricsVerticalOffset = 0;
            _viewModel.TaskbarLyricsPollingIntervalMs = 250;
            _viewModel.StatusText = "歌词显示设置已恢复默认";
            SetTaskbarLyricsEnabled(_viewModel.TaskbarLyricsEnabled);
        }

        private void RunDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            var steam = _viewModel.GetAccountManager().GetSteamService();
            var steamPath = steam.SteamPath;
            var loginPath = string.IsNullOrWhiteSpace(_viewModel.CustomLoginUsersPath)
                ? Path.Combine(steamPath ?? string.Empty, "config", "loginusers.vdf")
                : _viewModel.CustomLoginUsersPath;
            var steamProcessCount = 0;
            try
            {
                var steamProcesses = Process.GetProcessesByName("steam");
                steamProcessCount = steamProcesses.Length;
                foreach (var process in steamProcesses)
                    process.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to inspect Steam process state: {ex.Message}");
            }
            var checks = new List<string>
            {
                $"[{(Directory.Exists(steamPath) ? "正常" : "错误")}] Steam 安装路径：{steamPath ?? "未检测到"}",
                $"[{(File.Exists(loginPath) ? "正常" : "警告")}] loginusers.vdf：{loginPath}",
                $"[{(steamProcessCount > 0 ? "正常" : "警告")}] Steam 运行状态：{(steamProcessCount > 0 ? $"运行中（{steamProcessCount} 个进程）" : "未运行")}",
                $"[{(_desktopIconService.CanControlDesktopIcons ? "正常" : "错误")}] 桌面图标控制窗口：{(_desktopIconService.CanControlDesktopIcons ? "可用" : "未找到")}",
                $"[{(_desktopIconService.IconsHidden ? "警告" : "正常")}] 桌面图标状态：{(_desktopIconService.IconsHidden ? "当前隐藏" : "显示中")}",
                $"[正常] 歌词服务：{(_viewModel.TaskbarLyricsOnlineEnabled ? "已启用在线检索" : "仅本地模式")}",
                $"[正常] 快捷启动项目：{_viewModel.QuickLaunchList.Count} 个"
            };
            var cache = SettingsService.SettingsDirectory;
            var writable = false;
            try
            {
                Directory.CreateDirectory(cache);
                var p = Path.Combine(cache, $".diagnostic-write-test-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(p, "ok");
                File.Delete(p);
                writable = true;
            }
            catch { }
            checks.Add($"[{(writable ? "正常" : "错误")}] 配置目录写入：{cache}");
            var backupWritable = false;
            try
            {
                var backupDir = GameSaveBackupService.GetDefaultBackupRoot();
                Directory.CreateDirectory(backupDir);
                var p = Path.Combine(backupDir, $".diagnostic-write-test-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(p, "ok");
                File.Delete(p);
                backupWritable = true;
            }
            catch { }
            checks.Add($"[{(backupWritable ? "正常" : "警告")}] 存档备份目录写入：{(backupWritable ? "可写" : "不可写或未创建")}");
            var invalidShortcuts = _viewModel.QuickLaunchList
                .Count(item => !string.IsNullOrWhiteSpace(item.ExecutablePath) &&
                    !File.Exists(item.ExecutablePath) && !Directory.Exists(item.ExecutablePath));
            checks.Add($"[{(invalidShortcuts == 0 ? "正常" : "警告")}] 快捷方式目标：{(invalidShortcuts == 0 ? "全部存在" : $"{invalidShortcuts} 个目标不存在")}");
            var appDirectoryWritable = false;
            try
            {
                var p = Path.Combine(AppContext.BaseDirectory, $".diagnostic-write-test-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(p, "ok");
                File.Delete(p);
                appDirectoryWritable = true;
            }
            catch { }
            checks.Add($"[{(appDirectoryWritable ? "正常" : "警告")}] 程序目录权限：{(appDirectoryWritable ? "可写" : "可能需要提升权限")}");
            var isAdministrator = false;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                isAdministrator = new WindowsPrincipal(identity)
                    .IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to inspect process privilege: {ex.Message}");
            }
            var privilegeStatus = appDirectoryWritable || isAdministrator ? "正常" : "警告";
            var privilegeDescription = isAdministrator
                ? "当前进程属于管理员组"
                : appDirectoryWritable
                    ? "当前进程为普通用户，但程序目录可写"
                    : "当前进程权限受限，程序目录不可写";
            checks.Add($"[{privilegeStatus}] 当前程序权限：{privilegeDescription}");
            try
            {
                var backupDir = GameSaveBackupService.GetDefaultBackupRoot();
                var bytes = Directory.Exists(backupDir)
                    ? Directory.EnumerateFiles(backupDir, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
                    : 0L;
                var usageLevel = bytes >= 5L * 1024 * 1024 * 1024 ? "警告" : "正常";
                checks.Add($"[{usageLevel}] 存档备份占用：{bytes / 1024d / 1024d:0.0} MB");
                var drive = new DriveInfo(Path.GetPathRoot(backupDir) ?? backupDir);
                var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
                checks.Add($"[{(freeGb < 2 ? "警告" : "正常")}] 备份磁盘剩余空间：{freeGb:0.0} GB");
            }
            catch (Exception ex)
            {
                checks.Add($"[警告] 存档备份占用：读取失败（{ex.Message}）");
            }
            checks.Add($"[{(UpdateService.HasPendingUpdateCheckpoint() ? "警告" : "正常")}] 更新状态：{(UpdateService.HasPendingUpdateCheckpoint() ? "检测到未完成的更新启动记录" : "无待处理更新")}");
            var crashLogPath = Path.Combine(SettingsService.SettingsDirectory, "crash.log");
            checks.Add($"[{(File.Exists(crashLogPath) ? "警告" : "正常")}] 上次异常退出记录：{(File.Exists(crashLogPath) ? "发现 crash.log" : "未发现")}");
            _lastDiagnosticReport = "Steam Switch 诊断报告\n" +
                $"版本：{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}\n" +
                $"系统：{Environment.OSVersion}\n" +
                $"运行时：{Environment.Version}\n" +
                DateTime.Now.ToString("时间：yyyy-MM-dd HH:mm:ss") + "\n\n" +
                string.Join(Environment.NewLine, checks);
            DiagnosticSummaryText.Text = _lastDiagnosticReport;
            _viewModel.StatusText = "诊断完成";
            _ = UpdateLyricsConnectivityDiagnosticAsync();
        }

        private async Task UpdateLyricsConnectivityDiagnosticAsync()
        {
            if (!_viewModel.TaskbarLyricsModuleEnabled || !_viewModel.TaskbarLyricsOnlineEnabled)
                return;
            // Copy/export and the diagnostics button can be used in quick
            // succession. Cancel the older probe so a slow response cannot
            // overwrite a newer report with stale connectivity information.
            var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var previousProbe = Interlocked.Exchange(ref _lyricsDiagnosticCts, probeCts);
            previousProbe?.Cancel();
            void CleanupProbe()
            {
                Interlocked.CompareExchange(ref _lyricsDiagnosticCts, null, probeCts);
                probeCts.Dispose();
            }
            string result;
            try
            {
                var reachable = await TaskbarMediaService.CheckLyricsConnectivityAsync(probeCts.Token);
                result = reachable ? "[正常] 歌词网络连通性：在线歌词源可访问" : "[警告] 歌词网络连通性：在线歌词源返回异常";
            }
            catch (OperationCanceledException) when (probeCts.IsCancellationRequested)
            {
                CleanupProbe();
                return;
            }
            catch (Exception ex)
            {
                result = $"[警告] 歌词网络连通性：无法访问（{ex.Message}）";
            }

            if (_shutdownStarted || !_viewModel.TaskbarLyricsModuleEnabled || !_viewModel.TaskbarLyricsOnlineEnabled ||
                !ReferenceEquals(Volatile.Read(ref _lyricsDiagnosticCts), probeCts))
            {
                CleanupProbe();
                return;
            }
            var reportLines = _lastDiagnosticReport
                .Split(new[] { Environment.NewLine }, StringSplitOptions.None)
                .Where(line => !line.Contains("歌词网络连通性：", StringComparison.Ordinal))
                .ToList();
            reportLines.Add(result);
            _lastDiagnosticReport = string.Join(Environment.NewLine, reportLines);
            PostToUi(() => DiagnosticSummaryText.Text = _lastDiagnosticReport);
            CleanupProbe();
        }

        private async void ExportDiagnosticReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_lastDiagnosticReport)) RunDiagnostics_Click(sender, e);
                await UpdateLyricsConnectivityDiagnosticAsync();
                if (string.IsNullOrWhiteSpace(_lastDiagnosticReport)) return;
                var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "文本报告 (*.txt)|*.txt", FileName = $"SteamSwitch-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
                if (dialog.ShowDialog(this) == true)
                {
                    if (ConfigurationBundleService.IsManagedConfigurationFile(SettingsService.SettingsDirectory, dialog.FileName))
                        throw new InvalidOperationException("不能覆盖正在使用的配置文件或其恢复备份，请选择其他导出文件名或目录。");
                    await File.WriteAllTextAsync(dialog.FileName, _lastDiagnosticReport);
                    _viewModel.StatusText = "诊断报告已导出";
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Export diagnostic report failed.", ex);
                _viewModel.StatusText = $"导出诊断报告失败: {ex.Message}";
            }
        }

        private void RepairDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            var repaired = new List<string>();
            try
            {
                _desktopIconService.ShowIcons();
                _viewModel.DesktopIconsHiddenByApp = false;
                repaired.Add("已恢复桌面图标");

                if (!string.IsNullOrWhiteSpace(_viewModel.CustomSteamPath) && !Directory.Exists(_viewModel.CustomSteamPath))
                {
                    _viewModel.CustomSteamPath = string.Empty;
                    repaired.Add("已移除失效的自定义 Steam 路径");
                }
                if (!string.IsNullOrWhiteSpace(_viewModel.CustomLoginUsersPath) && !File.Exists(_viewModel.CustomLoginUsersPath))
                {
                    _viewModel.CustomLoginUsersPath = string.Empty;
                    repaired.Add("已移除失效的 loginusers.vdf 路径");
                }
                if (UpdateService.HasPendingUpdateCheckpoint())
                {
                    UpdateService.CompletePendingUpdateCheckpoint();
                    repaired.Add("已清理未完成的更新记录");
                }

                Directory.CreateDirectory(SettingsService.SettingsDirectory);
                ConfigureDesktopEnhance();
                DiagnosticSummaryText.Text = string.Join(Environment.NewLine, repaired);
                _viewModel.StatusText = repaired.Count == 0 ? "未发现需要修复的问题" : $"已完成 {repaired.Count} 项修复";
            }
            catch (Exception ex)
            {
                AppLogger.Error("Repair diagnostics failed.", ex);
                _viewModel.StatusText = $"一键修复失败: {ex.Message}";
            }
        }

        private void OpenCrashLog_Click(object sender, RoutedEventArgs e)
        {
            var path = Path.Combine(SettingsService.SettingsDirectory, "crash.log");
            if (!File.Exists(path))
            {
                _viewModel.StatusText = "当前没有异常退出记录";
                return;
            }
            using var _p3 = Process.Start(new ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
        }

        private void ClearCrashLog_Click(object sender, RoutedEventArgs e)
        {
            var path = Path.Combine(SettingsService.SettingsDirectory, "crash.log");
            try
            {
                if (File.Exists(path)) File.Delete(path);
                DiagnosticSummaryText.Text = "异常记录已清除";
                _viewModel.StatusText = "异常退出记录已清除";
            }
            catch (Exception ex)
            {
                AppLogger.Error("Clear crash log failed.", ex);
                _viewModel.StatusText = $"清除异常记录失败: {ex.Message}";
            }
        }

        private void EmergencyRestore_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Emergency recovery must stop every feature that can keep
                // altering system UI after a crash, sleep/resume, or Explorer
                // restart. Use the normal setters so the running services and
                // persisted settings are updated together.
                _desktopIconService.ShowIcons();
                _viewModel.DesktopAutoHideIconsEnabled = false;
                _viewModel.DesktopRestoreByMouseMove = false;
                _viewModel.DesktopRestoreByTaskbarClick = false;
                _viewModel.DesktopToggleIconsOnClick = false;
                _viewModel.DesktopIconsHiddenByApp = false;
                SetTaskbarLyricsEnabled(false);
                _viewModel.DesktopFloatingEnabled = false;
                DetachFromTaskbar();
                DetachDesktopFloating();
                // Re-apply the icon policy after clearing all triggers; this
                // also removes the recovery marker if one was left behind.
                ConfigureDesktopEnhance();
                _viewModel.StatusText = "已执行紧急恢复：桌面图标和悬浮窗均已恢复";
            }
            catch (Exception ex)
            {
                AppLogger.Error("Emergency restore failed.", ex);
                _viewModel.StatusText = $"紧急恢复失败: {ex.Message}";
            }
        }

    }
}
