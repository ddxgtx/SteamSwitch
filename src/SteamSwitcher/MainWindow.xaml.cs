using System.Diagnostics;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
        private readonly TrayIconService _trayIcon;
        private readonly TaskbarAppearanceService _taskbarAppearanceService = new();
        private readonly TaskbarOverlayService _taskbarOverlayService = new();
        private readonly TranslucentTbEngine _ttbEngine = new();
        private readonly DesktopIconService _desktopIconService = new();
        private TaskbarBandWindow? _taskbarBand;
        private DesktopFloatingWindow? _desktopFloatingWindow;
        private GameSaveCenterWindow? _gameSaveCenterWindow;
        private DragDropHelper? _quickLaunchDragDrop;
        private bool _gameSearchPlaceholder = true;
        private bool _accountSearchPlaceholder = true;
        private bool _exitRequested;
        private bool _shutdownStarted;
        private bool _isInteractiveSession;
        private bool _isInitialized;
        private Task? _initializationTask;
        private readonly Queue<string[]> _pendingExternalRequests = new();
        private readonly SemaphoreSlim _externalRequestGate = new(1, 1);
        private readonly SemaphoreSlim _accountInteractionGate = new(1, 1);
        private const string GameSearchHint = "搜索游戏、AppID 或启动账号...";
        private const string AccountSearchHint = "搜索账号名称或登录名...";
        private string _accountFilterMode = "All";
        private string _accountSortMode = "Recent";
        private bool _accountGridMode;
        private bool _updatingTaskbarAppearance;
        private Style? _accountListItemStyle;
        private Style? _accountGridItemStyle;

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        private static readonly uint WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaBorderColor = 34;
        private const int DwmWindowCornerPreferenceRound = 2;
        private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

        public MainWindow(bool startHidden = false, string[]? startupArguments = null)
        {
            _isInteractiveSession = !startHidden;
            var initialArguments = startupArguments ?? Array.Empty<string>();
            if (ContainsActionRequest(initialArguments))
                _pendingExternalRequests.Enqueue(initialArguments.ToArray());
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            InitializeComponent();
            _accountListItemStyle = AccountListBox.ItemContainerStyle;
            ApplyAccountSort();
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
            _trayIcon.ToggleTaskbarAppearanceRequested += (s, enabled) => SetTaskbarAppearanceEnabled(enabled);
            _trayIcon.ExitRequested += (s, e) => RequestExit();
            _viewModel.GetAccountManager().CurrentAccountChanged += OnCurrentAccountChanged;
            _viewModel.GetAccountManager().ConfirmForceCloseAfterTimeoutAsync = ConfirmForceCloseAfterTimeoutAsync;

            _viewModel.NotificationRequested += (s, message) =>
            {
                _trayIcon.ShowNotification("Steam Switch", message, 1000);
            };

            _viewModel.UpdateAvailable += (s, updateInfo) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (!_isInteractiveSession)
                    {
                        _trayIcon.ShowNotification("Steam Switch", "发现新版本，可打开主界面检查更新", 2000);
                        return;
                    }

                    using var updateService = new UpdateService();
                    var dialog = new Views.UpdateDialog(updateService, updateInfo);
                    dialog.Owner = this;
                    dialog.ShowDialog();
                });
            };

            Loaded += MainWindow_Loaded;
            SourceInitialized += (_, _) => OnSourceInitialized();
            Closing += Window_Closing;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            StateChanged += Window_StateChanged;
            SizeChanged += MainWindow_SizeChanged;
        }

        private void OnSourceInitialized()
        {
            ApplyNativeWindowAppearance();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var source = HwndSource.FromHwnd(hwnd);
                source?.AddHook(WndProc);
            }
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if ((uint)msg == WmTaskbarCreated)
            {
                _taskbarAppearanceService.Refresh();
                _taskbarBand?.Rebuild();
            }
            return IntPtr.Zero;
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => _taskbarAppearanceService.Refresh()));
        }

        private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => _taskbarAppearanceService.Refresh()));
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
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                // Windows 11 can draw the outer corner itself. Suppressing the DWM border
                // also removes the bright strip that otherwise appears above a custom title bar.
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                {
                    var cornerPreference = DwmWindowCornerPreferenceRound;
                    DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

                    var borderColor = DwmColorNone;
                    DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));

                    // Clear the legacy region so DWM owns anti-aliased corners and maximized state.
                    SetWindowRgn(hwnd, IntPtr.Zero, true);
                    return;
                }

                if (WindowState == WindowState.Maximized)
                {
                    SetWindowRgn(hwnd, IntPtr.Zero, true);
                    return;
                }

                var source = PresentationSource.FromVisual(this);
                double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                int width = (int)(ActualWidth * dpiX);
                int height = (int)(ActualHeight * dpiY);
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

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            EnterInteractiveMode();
            await EnsureInitializedAsync();
        }

        public async void StartInBackground()
        {
            try
            {
                await EnsureInitializedAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Background startup failed.", ex);
                Application.Current.Shutdown(-1);
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
            _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
            SyncTrayFeatureStates();

            GameSearchBox.Text = GameSearchHint;
            GameSearchBox.Foreground = (Brush)Application.Current.Resources["SearchPlaceholderFgBrush"];
            _gameSearchPlaceholder = true;
            AccountSearchBox.Text = AccountSearchHint;
            AccountSearchBox.Foreground = (Brush)Application.Current.Resources["SearchPlaceholderFgBrush"];
            _accountSearchPlaceholder = true;

            SettingsFloatingAvatarSlider.Value = _viewModel.DesktopFloatingAvatarSize;
            SettingsFloatingAvatarText.Text = _viewModel.DesktopFloatingAvatarSize.ToString();
            SettingsFloatingOpacitySlider.Value = _viewModel.DesktopFloatingOpacity;
            SettingsFloatingOpacityText.Text = $"{_viewModel.DesktopFloatingOpacity}%";
            SyncGameSaveSettingsUi();
            ApplyTaskbarAppearance();

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
                Dispatcher.BeginInvoke(new Action(ActivateFromExternalRequest));
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
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(() => HandleExternalRequest(arguments)));
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

            await ProcessExternalRequestSerializedAsync(arguments);
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
            if (gameIndex >= 0 && gameIndex + 1 < arguments.Length &&
                int.TryParse(arguments[gameIndex + 1], out var appId) && appId > 0)
            {
                await _viewModel.LaunchBoundGameAsync(appId);
                return;
            }
            if (switchIndex >= 0 && switchIndex + 1 < arguments.Length)
            {
                var steamId = arguments[switchIndex + 1];
                var launchSteam = arguments.Any(argument =>
                    string.Equals(argument, "--launch-steam", StringComparison.OrdinalIgnoreCase));
                await _viewModel.SwitchAccountBySteamIdAsync(steamId, launchSteam);
                _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
                SyncTrayFeatureStates();
                return;
            }

            ActivateFromExternalRequest();
        }

        private void OnTrayAccountSelected(object? sender, string steamId)
        {
            var account = _viewModel.Accounts.FirstOrDefault(a => a.SteamId == steamId);
            if (account != null)
            {
                _viewModel.SelectedAccount = account;
                _ = _viewModel.AutoStartSteam
                    ? _viewModel.SwitchAndLaunchCommand.ExecuteAsync(null)
                    : _viewModel.SwitchOnlyCommand.ExecuteAsync(null);
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
            Dispatcher.BeginInvoke(new Action(() =>
                _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount)));
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_viewModel.MinimizeToTray && !_exitRequested)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                CleanupForExit();
            }
        }

        private void OnProcessExit(object? sender, EventArgs e) => _desktopIconService.ShowIcons();

        private void RequestExit()
        {
            _exitRequested = true;
            CleanupForExit();
            Close();
            Application.Current.Shutdown();
        }

        private Task<bool> ConfirmForceCloseAfterTimeoutAsync()
        {
            var result = MessageBox.Show(
                this,
                "Steam 在 20 秒内没有正常退出。\n\n可能仍在同步 Steam Cloud。是否立即强制结束 Steam 进程树？",
                "Steam 尚未退出",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            return Task.FromResult(result == MessageBoxResult.Yes);
        }

        private void CleanupForExit()
        {
            if (_shutdownStarted)
                return;

            _shutdownStarted = true;
            _exitRequested = true;

            var taskbarBand = _taskbarBand;
            var desktopFloatingWindow = _desktopFloatingWindow;
            _taskbarBand = null;
            _desktopFloatingWindow = null;

            _viewModel.GetAccountManager().CurrentAccountChanged -= OnCurrentAccountChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            taskbarBand?.Detach();
            taskbarBand?.Close();
            desktopFloatingWindow?.Close();
            _gameSaveCenterWindow?.Dispose();
            _gameSaveCenterWindow = null;
            _desktopIconService.ShowIcons();
            _viewModel.DesktopIconsHiddenByApp = false;
            _desktopIconService.IconVisibilityChanged -= OnDesktopIconVisibilityChanged;
            _desktopIconService.CountdownChanged -= OnDesktopIconCountdownChanged;
            _desktopIconService.Dispose();
            _taskbarAppearanceService.Dispose();
            _taskbarOverlayService.Dispose();
            _ttbEngine.Dispose();
            _trayIcon.Dispose();
            _viewModel.Dispose();
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            ApplyNativeWindowAppearance();

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

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Let WindowChrome handle resize when clicking near edges
            var pos = e.GetPosition(this);
            const int resizeBorder = 6;
            if (pos.X < resizeBorder || pos.X > ActualWidth - resizeBorder ||
                pos.Y < resizeBorder || pos.Y > ActualHeight - resizeBorder)
                return;

            if (e.ClickCount == 2)
            {
                Maximize_Click(sender, e);
            }
            else
            {
                DragMove();
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
            _gameSaveCenterWindow ??= new GameSaveCenterWindow(_viewModel);
            SaveCenterHost.Content = _gameSaveCenterWindow;
            SwitchToSaveCenter();
        }

        private void SideTransparentTaskbar_Click(object sender, RoutedEventArgs e)
        {
            SwitchToTransparentTaskbar();
        }

        private void SideTaskbarLyrics_Click(object sender, RoutedEventArgs e)
        {
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
            TransparentTaskbarView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            PageTitle.Text = "账号管理";
            PageSubtitle.Text = "双击卡片快速切换 Steam 身份，支持置顶常用账号。";
            SetSidebarActive(SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideSaveCenter, SideAccentSave,
                             SideTransparentTaskbar, SideAccentTransparentTaskbar,
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
            TransparentTaskbarView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            PageTitle.Text = "游戏库绑定";
            PageSubtitle.Text = "智能扫描本机 Steam 游戏，为每个游戏绑定专属启动账号。";
            SetSidebarActive(SideGames, SideAccentGame,
                             SideAccounts, SideAccentAccount,
                             SideSaveCenter, SideAccentSave,
                             SideTransparentTaskbar, SideAccentTransparentTaskbar,
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
                    Dispatcher.Invoke(() =>
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
            TransparentTaskbarView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            PageTitle.Text = "游戏存档中心";
            PageSubtitle.Text = "按 Steam 账号与游戏目录智能检查、备份与安全恢复 userdata 存档。";
            SetSidebarActive(SideSaveCenter, SideAccentSave,
                             SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideTransparentTaskbar, SideAccentTransparentTaskbar,
                             SideTaskbarLyrics, SideAccentTaskbarLyrics,
                             SideQuickLaunch, SideAccentQuickLaunch,
                             SideSettings, SideAccentSettings);
        }

        private void SwitchToTransparentTaskbar()
        {
            PageHeader.Visibility = Visibility.Visible;
            AccountView.Visibility = Visibility.Collapsed;
            GameView.Visibility = Visibility.Collapsed;
            QuickLaunchView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            SaveCenterHost.Visibility = Visibility.Collapsed;
            TransparentTaskbarView.Visibility = Visibility.Visible;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            PageTitle.Text = "透明任务栏";
            PageSubtitle.Text = "深度定制 Windows 任务栏视觉材质、动态变色与前台窗口场景保护。";
            SyncTaskbarAppearanceMode();
            SetSidebarActive(SideTransparentTaskbar, SideAccentTransparentTaskbar,
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
            TransparentTaskbarView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Visible;
            PageTitle.Text = "任务栏歌词";
            PageSubtitle.Text = "在任务栏实时显示多平台同步歌词，支持媒体快捷控制与自适应宽度。";
            if (TaskbarLyricsWidthText != null)
                TaskbarLyricsWidthText.Text = $"{_viewModel.TaskbarLyricsWidth} px";
            if (LyricOffsetText != null)
                LyricOffsetText.Text = _viewModel.LyricOffsetMs >= 0 ? $"+{_viewModel.LyricOffsetMs} ms" : $"{_viewModel.LyricOffsetMs} ms";
            SetSidebarActive(SideTaskbarLyrics, SideAccentTaskbarLyrics,
                             SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideSaveCenter, SideAccentSave,
                             SideTransparentTaskbar, SideAccentTransparentTaskbar,
                             SideQuickLaunch, SideAccentQuickLaunch,
                             SideSettings, SideAccentSettings);
        }

        private void SwitchToQuickLaunch()
        {
            PageHeader.Visibility = Visibility.Visible;
            AccountView.Visibility = Visibility.Collapsed;
            GameView.Visibility = Visibility.Collapsed;
            QuickLaunchView.Visibility = Visibility.Visible;
            SettingsView.Visibility = Visibility.Collapsed;
            SaveCenterHost.Visibility = Visibility.Collapsed;
            TransparentTaskbarView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            PageTitle.Text = "快捷启动";
            PageSubtitle.Text = "将常用账号、独立游戏与外部程序组织成桌面快捷面板，支持拖拽排序。";
            SetSidebarActive(SideQuickLaunch, SideAccentQuickLaunch,
                             SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideSaveCenter, SideAccentSave,
                             SideTransparentTaskbar, SideAccentTransparentTaskbar,
                             SideTaskbarLyrics, SideAccentTaskbarLyrics,
                             SideSettings, SideAccentSettings);
            QuickLaunchEmptyHint.Visibility = _viewModel.QuickLaunchList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Initialize drag-drop if not already done
            if (_quickLaunchDragDrop == null)
            {
                _quickLaunchDragDrop = new DragDropHelper(QuickLaunchListBox, QuickLaunchDeleteZone);
                _quickLaunchDragDrop.ItemDeleted += async (s, index) =>
                {
                    await _viewModel.DeleteQuickLaunchItemAsync(index);
                    QuickLaunchEmptyHint.Visibility = _viewModel.QuickLaunchList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                };
                _quickLaunchDragDrop.ItemMoved += async (s, args) =>
                {
                    await _viewModel.ReorderQuickLaunchItemAsync(args.fromIndex, args.toIndex);
                };
                _quickLaunchDragDrop.ItemClicked += (s, index) =>
                {
                    var items = _viewModel.QuickLaunchList;
                    if (index >= 0 && index < items.Count)
                    {
                        _viewModel.LaunchQuickLaunchItem(items[index].Id);
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
            TransparentTaskbarView.Visibility = Visibility.Collapsed;
            TaskbarLyricsView.Visibility = Visibility.Collapsed;
            PageTitle.Text = "偏好设置";
            PageSubtitle.Text = "集中管理开机自启、账号切换行为、任务栏与外观偏好。";
            SetSidebarActive(SideSettings, SideAccentSettings,
                             SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideSaveCenter, SideAccentSave,
                             SideTransparentTaskbar, SideAccentTransparentTaskbar,
                             SideTaskbarLyrics, SideAccentTaskbarLyrics,
                             SideQuickLaunch, SideAccentQuickLaunch);
        }

        private void SetSidebarActive(Button active, Border activeAccent, params object[] inactivePairs)
        {
            var res = Application.Current.Resources;
            active.Background = (Brush)res["SidebarActiveBgBrush"];
            active.Foreground = (Brush)res["SidebarActiveFgBrush"];
            active.FontWeight = FontWeights.SemiBold;
            activeAccent.Background = (Brush)res["SidebarActiveFgBrush"];

            for (var i = 0; i + 1 < inactivePairs.Length; i += 2)
            {
                if (inactivePairs[i] is not Button button || inactivePairs[i + 1] is not Border accent)
                    continue;
                button.Background = Brushes.Transparent;
                button.Foreground = (Brush)res["SidebarInactiveFgBrush"];
                button.FontWeight = FontWeights.Normal;
                accent.Background = Brushes.Transparent;
            }
        }

        private async void GameScan_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.ScanGamesAsync();
            GameScanStatus.Text = _viewModel.GameList.Count > 0
                ? $"共 {_viewModel.GameList.Count} 款游戏"
                : "未找到已安装的游戏";
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

            _viewModel.SelectedAccount = account;
            e.Handled = true;

            if (_viewModel.AutoStartSteam)
                await _viewModel.SwitchAndLaunchCommand.ExecuteAsync(null);
            else
                await _viewModel.SwitchOnlyCommand.ExecuteAsync(null);
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

        private static void OpenAccountToolbarMenu(Button? button)
        {
            if (button?.ContextMenu == null)
                return;

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        private void AccountFilterOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string mode })
                return;

            _accountFilterMode = mode;
            AccountFilterText.Text = mode switch
            {
                "Current" => "当前账号",
                "Pinned" => "已固定账号",
                _ => "全部账号"
            };
            RefreshAccountFilter();
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
            if (useGrid)
            {
                _accountGridItemStyle ??= CreateAccountGridItemStyle();
                AccountListBox.ItemsPanel = (ItemsPanelTemplate)FindResource("AccountGridItemsPanel");
                AccountListBox.ItemContainerStyle = _accountGridItemStyle;
                AccountListBox.MaxHeight = 520;
                Grid.SetColumnSpan(AccountListPanel, 2);
                AccountListPanel.Margin = new Thickness(0);
                AccountDetailPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                AccountListBox.ItemsPanel = (ItemsPanelTemplate)FindResource("AccountListItemsPanel");
                AccountListBox.ItemContainerStyle = _accountListItemStyle;
                AccountListBox.MaxHeight = 270;
                Grid.SetColumnSpan(AccountListPanel, 1);
                AccountListPanel.Margin = new Thickness(0, 0, 24, 0);
                AccountDetailPanel.Visibility = Visibility.Visible;
            }

            SetAccountViewButtonState(AccountListViewButton, !useGrid);
            SetAccountViewButtonState(AccountGridViewButton, useGrid);
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
                _ => true
            };

            var accountSearchText = AccountSearchBox?.Text;
            bool matchesSearch = _accountSearchPlaceholder || string.IsNullOrWhiteSpace(accountSearchText);
            if (!matchesSearch)
            {
                var query = accountSearchText!.Trim();
                matchesSearch = account.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                account.Username.Contains(query, StringComparison.OrdinalIgnoreCase);
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
            if (Resources["FilteredAccounts"] is CollectionViewSource source)
                source.View?.Refresh();
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

            var search = GameSearchBox.Text.Trim();
            if (string.IsNullOrEmpty(search))
            {
                GameListBox.ItemsSource = _viewModel.GameList;
            }
            else
            {
                var filtered = _viewModel.GameList
                    .Where(g => g.GameName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                g.AppId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrWhiteSpace(g.BindingAccountName) &&
                                 g.BindingAccountName.Contains(search, StringComparison.OrdinalIgnoreCase)));
                GameListBox.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<GameListViewModel>(filtered);
            }
        }

        private void GameClearSearch_Click(object sender, RoutedEventArgs e)
        {
            GameSearchBox.Text = "";
            GameSearchBox_GotFocus(sender, e);
            GameClearSearch.Visibility = Visibility.Collapsed;
            GameListBox.ItemsSource = _viewModel.GameList;
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

            await _viewModel.SaveGameBinding(game.AppId, game.GameName, account.SteamId, account.DisplayName);
            GameScanStatus.Text = $"已绑定 {game.GameName} → {account.DisplayName}";
        }

        private async void GameRemoveBinding_Click(object sender, RoutedEventArgs e)
        {
            var game = _viewModel.SelectedGame;
            if (game == null) return;

            var result = MessageBox.Show($"确定要删除 {game.GameName} 的账号绑定吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                await _viewModel.RemoveGameBinding(game.AppId);
                GameScanStatus.Text = $"已删除 {game.GameName} 的绑定";
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
            GamePinToggle.IsChecked = _viewModel.IsGamePinned(game.AppId);

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

                GamePinToggle.IsChecked = game.IsPinned;
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
            _viewModel.TaskbarLyricsEnabled = enabled;
            ApplyTaskbarLyricsSettings();
            SyncTrayFeatureStates();
            _viewModel.StatusText = enabled ? "任务栏歌词已开启" : "任务栏歌词已关闭";
        }

        private void SetTaskbarAppearanceEnabled(bool enabled)
        {
            _viewModel.TaskbarAppearanceEnabled = enabled;
            if (!enabled)
                _taskbarOverlayService.Disable();
            ApplyTaskbarAppearance();
            SyncTrayFeatureStates();
            _viewModel.StatusText = enabled ? "透明任务栏已开启" : "透明任务栏已关闭";
        }

        private void SyncTrayFeatureStates()
        {
            _trayIcon?.UpdateFeatureStates(
                _viewModel.TaskbarLyricsEnabled,
                _viewModel.TaskbarAppearanceEnabled);
        }
        private void TaskbarAppearance_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _viewModel == null || _updatingTaskbarAppearance) return;
            _updatingTaskbarAppearance = true;
            try
            {
                if (sender is CheckBox cb)
                    BindingOperations.GetBindingExpression(cb, CheckBox.IsCheckedProperty)?.UpdateSource();
                if (!_viewModel.TaskbarAppearanceEnabled)
                    _taskbarOverlayService.Disable();
                ApplyTaskbarAppearance();
                SyncTrayFeatureStates();
            }
            finally
            {
                _updatingTaskbarAppearance = false;
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

        private void TaskbarLyricsFontFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _viewModel == null || sender is not ComboBox { SelectedValue: string fontFamily })
                return;

            _viewModel.TaskbarLyricsFontFamily = fontFamily;
            ApplyTaskbarLyricsSettings();
        }

        private void LyricOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _viewModel == null) return;
            var offset = (int)Math.Round(e.NewValue);
            _viewModel.LyricOffsetMs = offset;
            ApplyTaskbarLyricsSettings();
        }

        private void ApplyTaskbarLyricsSettings()
        {
            if (_viewModel.TaskbarLyricsEnabled || _viewModel.IsTaskbarPinned)
            {
                if (_taskbarBand == null)
                {
                    AttachToTaskbar();
                }
                else
                {
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
                        _viewModel.TaskbarLyricsFontFamily);
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
                    _taskbarBand?.SetLyricsSettings(
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
                        _viewModel.TaskbarLyricsFontFamily);
                }
            }
            SyncTrayFeatureStates();
        }

        private void TaskbarAppearanceMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingTaskbarAppearance ||
                sender is not RadioButton { IsChecked: true, Tag: string modeName } ||
                !Enum.TryParse<TaskbarAppearanceMode>(modeName, out var mode))
                return;

            _updatingTaskbarAppearance = true;
            try
            {
                _viewModel.TaskbarAppearanceMode = mode;
                if (IsLoaded) ApplyTaskbarAppearance();
            }
            finally
            {
                _updatingTaskbarAppearance = false;
            }
        }
        private void TaskbarAppearanceOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel == null || _updatingTaskbarAppearance) return;
            _updatingTaskbarAppearance = true;
            try
            {
                var opacity = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
                _viewModel.TaskbarAppearanceOpacity = opacity;
                if (TaskbarAppearanceOpacityText != null) TaskbarAppearanceOpacityText.Text = $"{opacity}%";
                if (IsLoaded) ApplyTaskbarAppearance();
            }
            finally
            {
                _updatingTaskbarAppearance = false;
            }
        }
        private void TaskbarAppearanceColorPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string color } || _viewModel == null) return;
            _viewModel.TaskbarAppearanceColor = color;
            if (TaskbarAppearanceColorBox != null) TaskbarAppearanceColorBox.Text = color;
            ApplyTaskbarAppearance();
        }

        private void TaskbarAppearanceColor_LostFocus(object sender, RoutedEventArgs e)
        {
            if (TaskbarAppearanceColorBox == null || _viewModel == null) return;
            var color = TaskbarAppearanceColorBox.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
            {
                TaskbarAppearanceColorBox.Text = _viewModel.TaskbarAppearanceColor;
                _viewModel.StatusText = "任务栏颜色格式应为 #RRGGBB";
                return;
            }

            _viewModel.TaskbarAppearanceColor = color.ToUpperInvariant();
            TaskbarAppearanceColorBox.Text = _viewModel.TaskbarAppearanceColor;
            ApplyTaskbarAppearance();
        }

        private void TaskbarAppearanceRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_taskbarAppearanceService == null || _viewModel == null) return;
            ApplyTaskbarAppearance();
            _taskbarAppearanceService.Refresh();
            _viewModel.StatusText = "已强制刷新 Windows 任务栏视觉样式";
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
            _viewModel.DesktopImmersiveModeEnabled = true;
            _viewModel.DesktopAutoHideIconsEnabled = true;
            _viewModel.TaskbarAppearanceEnabled = true;
            _viewModel.TaskbarAppearanceMode = TaskbarAppearanceMode.Clear;
            _viewModel.TaskbarAppearanceOpacity = 0;
            if (TaskbarAppearanceOpacitySlider != null) TaskbarAppearanceOpacitySlider.Value = 0;
            SyncTaskbarAppearanceMode();
            ConfigureDesktopEnhance();
            ApplyTaskbarAppearance();
            _viewModel.StatusText = "已进入沉浸桌面模式：透明任务栏与桌面图标自动隐藏已开启";
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

        private void OnDesktopIconVisibilityChanged(object? sender, bool visible) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _viewModel.DesktopIconsVisible = visible;
            _viewModel.DesktopIconsHiddenByApp = !visible;
        }));

        private void OnDesktopIconCountdownChanged(object? sender, int seconds) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _viewModel.DesktopAutoHideCountdown = seconds;
        }));

        private void TaskbarAppearanceStartXamlEnhancer_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;

            _updatingTaskbarAppearance = true;
            try
            {
                _viewModel.TaskbarAppearanceEnabled = true;
                _viewModel.TaskbarAppearanceMode = TaskbarAppearanceMode.Clear;
                _viewModel.TaskbarAppearanceOpacity = 0;
                _viewModel.TaskbarAppearanceColor = "#000000";
                if (TaskbarAppearanceOpacitySlider != null) TaskbarAppearanceOpacitySlider.Value = 0;
                if (TaskbarAppearanceOpacityText != null) TaskbarAppearanceOpacityText.Text = "0%";
                if (TaskbarAppearanceColorBox != null) TaskbarAppearanceColorBox.Text = "#000000";
                SyncTaskbarAppearanceMode();
            }
            finally
            {
                _updatingTaskbarAppearance = false;
            }

            ApplyTaskbarAppearance();
            _taskbarAppearanceService.ForceFullTransparent();
            _viewModel.StatusText = "💎 原生任务栏引擎已应用 100% 全透明";
        }
        private void TaskbarStylePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string presetTag } || _viewModel == null) return;
            var parts = presetTag.Split(':');
            if (parts.Length >= 3 && Enum.TryParse<TaskbarAppearanceMode>(parts[0], out var mode) && int.TryParse(parts[1], out var opacity))
            {
                var color = parts[2];
                _viewModel.TaskbarAppearanceMode = mode;
                _viewModel.TaskbarAppearanceOpacity = opacity;
                _viewModel.TaskbarAppearanceColor = color;
                if (TaskbarAppearanceOpacitySlider != null) TaskbarAppearanceOpacitySlider.Value = opacity;
                if (TaskbarAppearanceOpacityText != null) TaskbarAppearanceOpacityText.Text = $"{opacity}%";
                if (TaskbarAppearanceColorBox != null) TaskbarAppearanceColorBox.Text = color;
                SyncTaskbarAppearanceMode();
                ApplyTaskbarAppearance();
            }
        }

                private void SyncTaskbarAppearanceMode()
        {
            if (TaskbarModeNormal != null) TaskbarModeNormal.IsChecked = _viewModel.TaskbarAppearanceMode == TaskbarAppearanceMode.Normal;
            if (TaskbarModeClear != null) TaskbarModeClear.IsChecked = _viewModel.TaskbarAppearanceMode == TaskbarAppearanceMode.Clear;
            if (TaskbarModeBlur != null) TaskbarModeBlur.IsChecked = _viewModel.TaskbarAppearanceMode == TaskbarAppearanceMode.Blur;
            if (TaskbarModeAcrylic != null) TaskbarModeAcrylic.IsChecked = _viewModel.TaskbarAppearanceMode == TaskbarAppearanceMode.Acrylic;
            if (TaskbarModeOpaque != null) TaskbarModeOpaque.IsChecked = _viewModel.TaskbarAppearanceMode == TaskbarAppearanceMode.Opaque;
        }

        private void ApplyTaskbarAppearance()
        {
            // One owner must control Explorer's composition attributes. Running the native
            // service, TranslucentTB and an overlay concurrently causes continuous repainting,
            // flicker and non-deterministic results after Explorer restarts.
            _ttbEngine.Stop();
            _taskbarOverlayService.Disable();

            _taskbarAppearanceService.Configure(
                _viewModel.TaskbarAppearanceEnabled,
                _viewModel.TaskbarAppearanceMode,
                _viewModel.TaskbarAppearanceColor,
                _viewModel.TaskbarAppearanceOpacity,
                _viewModel.TaskbarDynamicMaximized,
                _viewModel.TaskbarMaximizedMode,
                _viewModel.TaskbarDynamicStartMenu,
                _viewModel.TaskbarStartMenuMode,
                _viewModel.TaskbarDynamicSearch,
                _viewModel.TaskbarSearchMode,
                _viewModel.TaskbarDynamicTaskView,
                _viewModel.TaskbarTaskViewMode);

            if (_viewModel.TaskbarAppearanceEnabled &&
                     _viewModel.TaskbarAppearanceMode == TaskbarAppearanceMode.Blur &&
                     _taskbarAppearanceService.BlurCompatibilityFallback)
                _viewModel.StatusText = "当前 Windows 版本使用亚克力兼容模式替代原生任务栏模糊";
            else if (_viewModel.TaskbarAppearanceEnabled)
                _viewModel.StatusText = $"已应用任务栏外观：{_viewModel.TaskbarAppearanceMode}";
            SyncTrayFeatureStates();
        }

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
                await _viewModel.ScanGamesAsync();
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

        private void SyncGameSaveSettingsUi()
        {
            if (_viewModel == null) return;

            // Sync interval combo
            if (SettingsGameSaveIntervalCombo != null)
            {
                var interval = _viewModel.GameSavePeriodicIntervalMinutes;
                foreach (ComboBoxItem item in SettingsGameSaveIntervalCombo.Items)
                {
                    if (item.Tag is string tag && int.TryParse(tag, out var val) && val == interval)
                    {
                        SettingsGameSaveIntervalCombo.SelectedItem = item;
                        break;
                    }
                }
                if (SettingsGameSaveIntervalCombo.SelectedItem == null && SettingsGameSaveIntervalCombo.Items.Count > 1)
                    SettingsGameSaveIntervalCombo.SelectedIndex = 1; // Default 30 min
            }

            // Sync retention combo
            if (SettingsGameSaveRetentionCombo != null)
            {
                var retention = _viewModel.GameSaveManualRetention;
                foreach (ComboBoxItem item in SettingsGameSaveRetentionCombo.Items)
                {
                    if (item.Tag is string tag && int.TryParse(tag, out var val) && val == retention)
                    {
                        SettingsGameSaveRetentionCombo.SelectedItem = item;
                        break;
                    }
                }
                if (SettingsGameSaveRetentionCombo.SelectedItem == null && SettingsGameSaveRetentionCombo.Items.Count > 1)
                    SettingsGameSaveRetentionCombo.SelectedIndex = 1; // Default 10 copies
            }
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
            if (SettingsGameSaveIntervalCombo?.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag && int.TryParse(tag, out var minutes) &&
                _viewModel != null)
            {
                _viewModel.GameSavePeriodicIntervalMinutes = minutes;
            }
        }

        private void SettingsGameSaveRetention_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
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

        private void SettingsConfirmBeforeLaunch_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && _viewModel != null)
            {
                bool val = cb.IsChecked == true;
                _viewModel.ConfirmBeforeGameLaunch = val;
            }
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
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
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
                    Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
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
            if (_viewModel != null)
            {
                await _viewModel.InitializeAsync();
                if (_viewModel.AutoScanGamesOnStartup)
                {
                    await _viewModel.ScanGamesAsync();
                }
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
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                return;

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
                if (!enabled)
                    DetachFromTaskbar();
            };
            _taskbarBand.ToggleTaskbarLyricsRequested += (s, enabled) => SetTaskbarLyricsEnabled(enabled);
            _taskbarBand.ToggleTaskbarAppearanceRequested += (s, enabled) => SetTaskbarAppearanceEnabled(enabled);
            _taskbarBand.ExitRequested += (s, e) => RequestExit();
            _taskbarBand.QuickLaunchRequested += (s, id) => _viewModel.LaunchQuickLaunchItem(id);
            _taskbarBand.PanelItemOrderChanged += async (s, keys) => await _viewModel.SetPinnedPanelItemOrderAsync(keys);
            _taskbarBand.AccountOrderChanged += (s, ids) => _viewModel.SetPinnedAccountOrder(ids);
            _taskbarBand.GameDeleteRequested += (s, index) => _viewModel.RemovePinnedGame(index);
            _taskbarBand.QuickLaunchDeleteRequested += async (s, index) => await _viewModel.DeletePinnedQuickLaunchItemAsync(index);
            _taskbarBand.AccountDeleteRequested += (s, index) => _viewModel.RemovePinnedAccount(index);
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
                _viewModel.TaskbarLyricsFontFamily);
            
            _taskbarBand.Attach();

            _viewModel.IsTaskbarPinned = _taskbarBand.IsPinned;
        }

        private void DetachFromTaskbar()
        {
            if (_taskbarBand == null) return;

            _taskbarBand.Detach();
            _taskbarBand.Close();
            _taskbarBand = null;

            _viewModel.IsTaskbarPinned = false;
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
            _desktopFloatingWindow.ToggleTaskbarAppearanceRequested += (s, enabled) => SetTaskbarAppearanceEnabled(enabled);
            _desktopFloatingWindow.ExitRequested += (s, e) => RequestExit();
            _desktopFloatingWindow.QuickLaunchRequested += (s, id) => _viewModel.LaunchQuickLaunchItem(id);
            _desktopFloatingWindow.PanelItemOrderChanged += async (s, keys) => await _viewModel.SetPinnedPanelItemOrderAsync(keys);
            _desktopFloatingWindow.AccountOrderChanged += (s, ids) => _viewModel.SetPinnedAccountOrder(ids);
            _desktopFloatingWindow.GameDeleteRequested += (s, index) => _viewModel.RemovePinnedGame(index);
            _desktopFloatingWindow.QuickLaunchDeleteRequested += async (s, index) => await _viewModel.DeletePinnedQuickLaunchItemAsync(index);
            _desktopFloatingWindow.AccountDeleteRequested += (s, index) => _viewModel.RemovePinnedAccount(index);
            _desktopFloatingWindow.PositionChanged += (s, pos) =>
            {
                settings.DesktopFloatingLeft = pos.left;
                settings.DesktopFloatingTop = pos.top;
                SettingsService.Save(settings);
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
            _desktopFloatingWindow.Show();

            _viewModel.DesktopFloatingEnabled = true;
        }

        private void DetachDesktopFloating()
        {
            if (_desktopFloatingWindow == null)
            {
                _viewModel.DesktopFloatingEnabled = false;
                return;
            }

            _desktopFloatingWindow.Close();
            _desktopFloatingWindow = null;
            _viewModel.DesktopFloatingEnabled = false;
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
                _trayIcon.ShowNotification("Steam Switch", "账号操作正在进行，请稍候", 1200);
                return;
            }

            var accountManager = _viewModel.GetAccountManager();
            _viewModel.IsLoading = true;
            _viewModel.StatusText = forceRestart
                ? $"正在强制重启 Steam 并切换到 {account.PersonaName}..."
                : $"正在切换到 {account.PersonaName}...";

            if (forceRestart)
                _trayIcon.ShowNotification("Steam Switch", $"正在强制重启 Steam 并切换到 {account.PersonaName}", 1200);

            try
            {
                if (!forceRestart && AccountManager.AreSameAccount(accountManager.CurrentAccount, account))
                {
                    _viewModel.StatusText = $"{account.PersonaName} 已是当前账号";
                    _trayIcon.ShowNotification("Steam Switch", $"{account.PersonaName} 已是当前账号", 1000);
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
                    _trayIcon.ShowNotification("Steam Switch", failedMessage, 1500);
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

                _trayIcon.ShowNotification("Steam Switch", _viewModel.StatusText, 1500);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Taskbar account switch failed.", ex);
                _viewModel.StatusText = forceRestart ? "强制重启并切换账号失败" : "账号切换失败";
                _trayIcon.ShowNotification("Steam Switch", _viewModel.StatusText, 1500);
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
            await _viewModel.LaunchPinnedGameAsync(appId);
            _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
            SyncTrayFeatureStates();
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
                await _viewModel.AddQuickLaunchItemAsync(dialog.FileName);
                QuickLaunchEmptyHint.Visibility = _viewModel.QuickLaunchList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                await _viewModel.RemoveQuickLaunchItemAsync(id);
                QuickLaunchEmptyHint.Visibility = _viewModel.QuickLaunchList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void QuickLaunchPinToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string id)
            {
                await _viewModel.ToggleQuickLaunchPinAsync(id);
            }
        }


        private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"SteamSwitch Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
                sb.AppendLine($"OS: {Environment.OSVersion}");
                sb.AppendLine($".NET Runtime: {Environment.Version}");
                sb.AppendLine($"Steam Path: {_viewModel?.GetAccountManager()?.GetSteamService()?.SteamPath ?? "N/A"}");
                sb.AppendLine($"Account Count: {_viewModel?.Accounts?.Count ?? 0}");
                sb.AppendLine($"Game Count: {_viewModel?.GameList?.Count ?? 0}");
                sb.AppendLine($"Auto Backup Enabled: {_viewModel?.GameSaveAutoBackupEnabled ?? false}");
                sb.AppendLine($"Periodic Backup Enabled: {_viewModel?.GameSavePeriodicBackupEnabled ?? false} ({_viewModel?.GameSavePeriodicIntervalMinutes ?? 30} min)");
                sb.AppendLine($"Retention Limit: {_viewModel?.GameSaveManualRetention ?? 30}");
                Clipboard.SetText(sb.ToString());
                if (_viewModel != null) _viewModel.StatusText = "已将诊断信息复制到剪贴板";
            }
            catch (Exception ex)
            {
                if (_viewModel != null) _viewModel.StatusText = $"复制诊断失败: {ex.Message}";
            }
        }

        private void ResetWindowLayout_Click(object sender, RoutedEventArgs e)
        {
            Width = 980;
            Height = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
            if (_viewModel != null) _viewModel.StatusText = "已重置主窗口默认尺寸与居中位置";
        }

    }
}
