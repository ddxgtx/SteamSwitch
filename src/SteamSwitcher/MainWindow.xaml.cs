using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
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

        public MainWindow()
        {
            InitializeComponent();
            
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            _trayIcon = new TrayIconService(this);
            _trayIcon.AccountSelected += OnTrayAccountSelected;

            Loaded += MainWindow_Loaded;
            Closing += Window_Closing;
            StateChanged += Window_StateChanged;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await _viewModel.InitializeAsync();
            _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
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

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_viewModel.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                _trayIcon.ShowNotification("Steam Switch", "已最小化到系统托盘");
            }
            else
            {
                _taskbarBand?.Detach();
                _taskbarBand?.Close();
                _trayIcon.Dispose();
                Application.Current.Shutdown();
            }
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
            var settingsWindow = new SettingsWindow(_viewModel);
            settingsWindow.Owner = this;
            settingsWindow.PositionChanged += OnSettingsPositionChanged;
            settingsWindow.OffsetChanged += OnSettingsOffsetChanged;
            settingsWindow.AvatarSizeChanged += OnSettingsAvatarSizeChanged;
            settingsWindow.GlassChanged += OnSettingsGlassChanged;
            settingsWindow.RoundedChanged += OnSettingsRoundedChanged;
            settingsWindow.ShowDialog();
        }

        private void GameBinding_Click(object sender, RoutedEventArgs e)
        {
            var bindingWindow = new GameBindingWindow(_viewModel, _viewModel.GetGameBinding());
            bindingWindow.Owner = this;
            bindingWindow.ShowDialog();
        }

        private void OnSettingsPositionChanged(object? sender, Core.TaskbarPosition position)
        {
            if (_taskbarBand != null && _taskbarBand.IsPinned)
            {
                _taskbarBand.SetPosition(position);
            }
        }

        private void OnSettingsOffsetChanged(object? sender, int offset)
        {
            if (_taskbarBand != null && _taskbarBand.IsPinned)
            {
                _taskbarBand.SetOffset(offset);
            }
        }

        private void OnSettingsAvatarSizeChanged(object? sender, int size)
        {
            if (_taskbarBand != null && _taskbarBand.IsPinned)
            {
                _taskbarBand.SetAvatarSize(size);
            }
        }

        private void OnSettingsGlassChanged(object? sender, bool enabled)
        {
            if (_taskbarBand != null && _taskbarBand.IsPinned)
            {
                _taskbarBand.SetGlassEnabled(enabled);
            }
        }

        private void OnSettingsRoundedChanged(object? sender, bool rounded)
        {
            if (_taskbarBand != null && _taskbarBand.IsPinned)
            {
                _taskbarBand.SetRoundedMode(rounded);
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
        }

        private void TaskbarPin_Checked(object sender, RoutedEventArgs e)
        {
            AttachToTaskbar();
        }

        private void TaskbarPin_Unchecked(object sender, RoutedEventArgs e)
        {
            DetachFromTaskbar();
        }

        private void AttachToTaskbar()
        {
            if (_taskbarBand != null && _taskbarBand.IsPinned)
                return;

            var pinnedAccounts = _viewModel.GetPinnedAccounts();
            if (pinnedAccounts.Count == 0)
            {
                _viewModel.StatusText = "请先固定至少一个账号";
                _viewModel.IsTaskbarPinned = false;
                return;
            }

            _taskbarBand = new TaskbarBandWindow(_viewModel.GetAccountManager());
            _taskbarBand.AccountSwitchRequested += OnTaskbarAccountSwitch;
            _taskbarBand.SetPinnedAccounts(pinnedAccounts);
            
            _taskbarBand.SetPosition(_viewModel.TaskbarPosition);
            _taskbarBand.SetOffset(_viewModel.TaskbarOffset);
            _taskbarBand.SetAvatarSize(_viewModel.AvatarSize);
            _taskbarBand.SetGlassEnabled(_viewModel.GlassEnabled);
            _taskbarBand.SetRoundedMode(_viewModel.RoundedMode);
            
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

        private async void OnTaskbarAccountSwitch(object? sender, Models.SteamAccount account)
        {
            _viewModel.IsLoading = true;
            _viewModel.StatusText = "正在切换账号...";

            var accountManager = _viewModel.GetAccountManager();
            var success = await accountManager.SwitchAccountAsync(account);

            if (success)
            {
                _viewModel.StatusText = $"已切换到 {account.PersonaName}";
                _taskbarBand?.UpdateCurrentAccount(account);

                if (_viewModel.AutoStartSteam)
                {
                    _viewModel.StatusText = "正在启动Steam...";
                    accountManager.LaunchSteam();
                }
            }
            else
            {
                _viewModel.StatusText = "切换失败，请确保Steam已关闭";
            }

            _viewModel.IsSteamRunning = accountManager.GetSteamService().IsSteamRunning();
            _viewModel.IsLoading = false;

            _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
        }
    }
}
