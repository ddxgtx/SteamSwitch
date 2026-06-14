using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SteamSwitcher.Core;
using SteamSwitcher.Services;
using SteamSwitcher.ViewModels;
using SteamSwitcher.Views;

namespace SteamSwitcher
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly TrayIconService _trayIcon;
        private TaskbarBandWindow? _taskbarBand;
        private DesktopFloatingWindow? _desktopFloatingWindow;
        private bool _gameSearchPlaceholder = true;
        private bool _exitRequested;
        private bool _shutdownStarted;
        private const string GameSearchHint = "搜索游戏名称或 AppID...";

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        public MainWindow()
        {
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            InitializeComponent();

            _trayIcon = new TrayIconService(this);
            _trayIcon.AccountSelected += OnTrayAccountSelected;
            _trayIcon.LaunchSteamRequested += OnTrayLaunchSteamRequested;
            _trayIcon.ExitRequested += (s, e) => RequestExit();

            _viewModel.NotificationRequested += (s, message) =>
            {
                _trayIcon.ShowNotification("Steam Switch", message, 1000);
            };

            _viewModel.UpdateAvailable += (s, updateInfo) =>
            {
                Dispatcher.Invoke(() =>
                {
                    using var updateService = new UpdateService();
                    var dialog = new Views.UpdateDialog(updateService, updateInfo);
                    dialog.Owner = this;
                    dialog.ShowDialog();
                });
            };

            Loaded += MainWindow_Loaded;
            Closing += Window_Closing;
            StateChanged += Window_StateChanged;
            SizeChanged += MainWindow_SizeChanged;
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyRoundedWindowRegion();
        }

        private void ApplyRoundedWindowRegion()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                var source = PresentationSource.FromVisual(this);
                double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                int width = (int)(ActualWidth * dpiX);
                int height = (int)(ActualHeight * dpiY);
                int radius = (int)(12 * dpiX);

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
            try
            {
                var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icon.png");
                if (System.IO.File.Exists(iconPath))
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath));
                    Icon = bitmap;
                }
            }
            catch { }

            ApplyRoundedWindowRegion();

            await _viewModel.InitializeAsync();
            _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);

            GameSearchBox.Text = GameSearchHint;
            GameSearchBox.Foreground = (Brush)Application.Current.Resources["SearchPlaceholderFgBrush"];
            _gameSearchPlaceholder = true;

            SettingsOffsetXSlider.Value = _viewModel.TaskbarOffsetX;
            SettingsOffsetXText.Text = _viewModel.TaskbarOffsetX.ToString();
            SettingsOffsetYSlider.Value = _viewModel.TaskbarOffsetY;
            SettingsOffsetYText.Text = _viewModel.TaskbarOffsetY.ToString();
            SettingsTaskbarWindowSizeSlider.Value = _viewModel.TaskbarWindowSize;
            SettingsTaskbarWindowSizeText.Text = _viewModel.TaskbarWindowSize.ToString();
            SettingsTaskbarAvatarSlider.Value = _viewModel.TaskbarAvatarSize;
            SettingsTaskbarAvatarText.Text = _viewModel.TaskbarAvatarSize.ToString();
            SettingsFloatingAvatarSlider.Value = _viewModel.DesktopFloatingAvatarSize;
            SettingsFloatingAvatarText.Text = _viewModel.DesktopFloatingAvatarSize.ToString();
            SettingsFloatingOpacitySlider.Value = _viewModel.DesktopFloatingOpacity;
            SettingsFloatingOpacityText.Text = $"{_viewModel.DesktopFloatingOpacity}%";
            SyncTaskbarPositionRadios();

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
        }

        private void OnTrayAccountSelected(object? sender, string steamId)
        {
            var account = _viewModel.Accounts.FirstOrDefault(a => a.SteamId == steamId);
            if (account != null)
            {
                _viewModel.SelectedAccount = account;
                _ = _viewModel.SwitchAndLaunchCommand.ExecuteAsync(null);
            }
        }

        private void OnTrayLaunchSteamRequested(object? sender, EventArgs e)
        {
            var accountManager = _viewModel.GetAccountManager();
            accountManager.LaunchSteam(silent: _viewModel.SilentCloseSteam);
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

        private void RequestExit()
        {
            _exitRequested = true;
            CleanupForExit();
            Close();
            Application.Current.Shutdown();
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

            taskbarBand?.Detach();
            taskbarBand?.Close();
            desktopFloatingWindow?.Close();
            _trayIcon.Dispose();
            _viewModel.Dispose();
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
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

        private void SideSettings_Click(object sender, RoutedEventArgs e)
        {
            SwitchToSettings();
        }

        private void SwitchToAccounts()
        {
            AccountView.Visibility = Visibility.Visible;
            GameView.Visibility = Visibility.Collapsed;
            QuickLaunchView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            SetSidebarActive(SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideQuickLaunch, SideAccentQuickLaunch,
                             SideSettings, SideAccentSettings);
        }

        private void SwitchToGames()
        {
            AccountView.Visibility = Visibility.Collapsed;
            GameView.Visibility = Visibility.Visible;
            QuickLaunchView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            SetSidebarActive(SideGames, SideAccentGame,
                             SideAccounts, SideAccentAccount,
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

        private void SwitchToSettings()
        {
            AccountView.Visibility = Visibility.Collapsed;
            GameView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Visible;
            SetSidebarActive(SideSettings, SideAccentSettings,
                             SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
                             SideQuickLaunch, SideAccentQuickLaunch);
        }

        private void SetSidebarActive(
            Button active, Border activeAccent,
            Button inactive1, Border inactiveAccent1,
            Button inactive2, Border inactiveAccent2,
            Button inactive3, Border inactiveAccent3)
        {
            var res = Application.Current.Resources;
            active.Background = (Brush)res["SidebarActiveBgBrush"];
            active.Foreground = (Brush)res["SidebarActiveFgBrush"];
            active.FontWeight = FontWeights.SemiBold;
            activeAccent.Background = (Brush)res["SidebarActiveFgBrush"];

            inactive1.Background = Brushes.Transparent;
            inactive1.Foreground = (Brush)res["SidebarInactiveFgBrush"];
            inactive1.FontWeight = FontWeights.Normal;
            inactiveAccent1.Background = Brushes.Transparent;

            inactive2.Background = Brushes.Transparent;
            inactive2.Foreground = (Brush)res["SidebarInactiveFgBrush"];
            inactive2.FontWeight = FontWeights.Normal;
            inactiveAccent2.Background = Brushes.Transparent;

            inactive3.Background = Brushes.Transparent;
            inactive3.Foreground = (Brush)res["SidebarInactiveFgBrush"];
            inactive3.FontWeight = FontWeights.Normal;
            inactiveAccent3.Background = Brushes.Transparent;
        }

        private async void GameScan_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.ScanGamesAsync();
            GameScanStatus.Text = _viewModel.GameList.Count > 0
                ? $"共 {_viewModel.GameList.Count} 款游戏"
                : "未找到已安装的游戏";
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
                                g.AppId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase));
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
            switch (_viewModel.TaskbarPosition)
            {
                case Core.TaskbarPosition.Left:
                    SettingsPosLeft.IsChecked = true;
                    break;
                case Core.TaskbarPosition.Center:
                    SettingsPosCenter.IsChecked = true;
                    break;
                case Core.TaskbarPosition.Right:
                    SettingsPosRight.IsChecked = true;
                    break;
                default:
                    SettingsPosAuto.IsChecked = true;
                    break;
            }
        }

        private void SettingsOffsetX_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel == null) return;
            int val = (int)e.NewValue;
            _viewModel.TaskbarOffsetX = val;
            SettingsOffsetXText.Text = val.ToString();
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetOffset(_viewModel.TaskbarOffsetX, _viewModel.TaskbarOffsetY);
        }

        private void SettingsOffsetY_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel == null) return;
            int val = (int)e.NewValue;
            _viewModel.TaskbarOffsetY = val;
            SettingsOffsetYText.Text = val.ToString();
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetOffset(_viewModel.TaskbarOffsetX, _viewModel.TaskbarOffsetY);
        }

        private void SettingsTaskbarWindowSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel == null) return;
            int val = (int)e.NewValue;
            _viewModel.TaskbarWindowSize = val;
            SettingsTaskbarWindowSizeText.Text = val.ToString();
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetWindowSize(val);
        }

        private void ResetLayout_Click(object sender, RoutedEventArgs e)
        {
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

            SyncTaskbarPositionRadios();
            SettingsOffsetXSlider.Value = _viewModel.TaskbarOffsetX;
            SettingsOffsetYSlider.Value = _viewModel.TaskbarOffsetY;
            SettingsTaskbarWindowSizeSlider.Value = _viewModel.TaskbarWindowSize;
            SettingsTaskbarAvatarSlider.Value = _viewModel.TaskbarAvatarSize;
            SettingsFloatingOpacitySlider.Value = _viewModel.DesktopFloatingOpacity;
            SettingsFloatingAvatarSlider.Value = _viewModel.DesktopFloatingAvatarSize;

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
            SettingsTaskbarAvatarText.Text = val.ToString();
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
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetGlassEnabled(_viewModel.TaskbarGlassEnabled);
        }

        private void SettingsFloatingGlass_Changed(object sender, RoutedEventArgs e)
        {
            _desktopFloatingWindow?.SetGlassEnabled(_viewModel.DesktopFloatingGlassEnabled);
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
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                _taskbarBand.SetRoundedMode(_viewModel.TaskbarRoundedMode);
        }

        private void SettingsFloatingRounded_Changed(object sender, RoutedEventArgs e)
        {
            _desktopFloatingWindow?.SetRoundedMode(_viewModel.DesktopFloatingRoundedMode);
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
            _desktopFloatingWindow?.SetTopmostEnabled(_viewModel.DesktopFloatingTopmost);
        }

        private void SettingsFloatingLocked_Changed(object sender, RoutedEventArgs e)
        {
            _desktopFloatingWindow?.SetLocked(_viewModel.DesktopFloatingLocked);
        }

        private void SettingsFloatingOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel == null) return;
            int val = (int)e.NewValue;
            _viewModel.DesktopFloatingOpacity = val;
            SettingsFloatingOpacityText.Text = $"{val}%";
            _desktopFloatingWindow?.SetOpacityPercent(val);
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
            if (pinnedAccounts.Count == 0 && pinnedGames.Count == 0)
            {
                _viewModel.StatusText = "请先固定至少一个账号或游戏";
                _viewModel.IsTaskbarPinned = false;
                return;
            }

            _taskbarBand = new TaskbarBandWindow(_viewModel.GetAccountManager());
            _taskbarBand.AccountSwitchRequested += OnTaskbarAccountSwitch;
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
            _taskbarBand.SetOffset(_viewModel.TaskbarOffsetX, _viewModel.TaskbarOffsetY);
            _taskbarBand.SetWindowSize(_viewModel.TaskbarWindowSize);
            _taskbarBand.SetAvatarSize(_viewModel.TaskbarAvatarSize);
            _taskbarBand.SetGlassEnabled(_viewModel.TaskbarGlassEnabled);
            _taskbarBand.SetRoundedMode(_viewModel.TaskbarRoundedMode);
            
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
            var accountManager = _viewModel.GetAccountManager();
            var currentSteamId = accountManager.CurrentAccount?.SteamId;
            var currentAccountName = accountManager.CurrentAccount?.AccountName;

            var isCurrent = false;
            if (!string.IsNullOrEmpty(currentSteamId) && !string.IsNullOrEmpty(account.SteamId))
                isCurrent = string.Equals(currentSteamId, account.SteamId, StringComparison.OrdinalIgnoreCase);
            if (!isCurrent && !string.IsNullOrEmpty(currentAccountName) && !string.IsNullOrEmpty(account.AccountName))
                isCurrent = string.Equals(currentAccountName, account.AccountName, StringComparison.OrdinalIgnoreCase);

            if (isCurrent)
            {
                _viewModel.StatusText = $"{account.PersonaName} 已是当前账号";
                _trayIcon.ShowNotification("Steam Switch", $"{account.PersonaName} 已是当前账号", 1000);
                _viewModel.IsSteamRunning = accountManager.GetSteamService().IsSteamRunning();
                return;
            }

            _viewModel.IsLoading = true;
            _viewModel.StatusText = "正在切换账号...";

            var success = await accountManager.SwitchAccountAsync(account, _viewModel.SilentCloseSteam);

            if (success)
            {
                _viewModel.StatusText = $"已切换到 {account.PersonaName}";
                _trayIcon.ShowNotification("Steam Switch", $"已切换到 {account.PersonaName}", 1000);
                _taskbarBand?.UpdateCurrentAccount(account);
                _desktopFloatingWindow?.UpdateCurrentAccount(account);

                if (_viewModel.AutoStartSteam)
                {
                    _viewModel.StatusText = "正在启动Steam...";
                    accountManager.LaunchSteam(silent: _viewModel.SilentCloseSteam);
                }
            }
            else
            {
                _viewModel.StatusText = "切换失败，请确保Steam已关闭";
                _trayIcon.ShowNotification("Steam Switch", "切换失败，请确保Steam已关闭", 1000);
            }

            _viewModel.IsSteamRunning = accountManager.GetSteamService().IsSteamRunning();
            _viewModel.IsLoading = false;

            _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
        }

        private async void OnTaskbarGameLaunch(object? sender, int appId)
        {
            await _viewModel.LaunchPinnedGameAsync(appId);
            _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
        }

        // Quick Launch event handlers
        private DragDropHelper? _quickLaunchDragDrop;

        private void SideQuickLaunch_Click(object sender, RoutedEventArgs e)
        {
            SwitchToQuickLaunch();
        }

        private void SwitchToQuickLaunch()
        {
            AccountView.Visibility = Visibility.Collapsed;
            GameView.Visibility = Visibility.Collapsed;
            QuickLaunchView.Visibility = Visibility.Visible;
            SettingsView.Visibility = Visibility.Collapsed;
            SetSidebarActive(SideQuickLaunch, SideAccentQuickLaunch,
                             SideAccounts, SideAccentAccount,
                             SideGames, SideAccentGame,
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

}
}
