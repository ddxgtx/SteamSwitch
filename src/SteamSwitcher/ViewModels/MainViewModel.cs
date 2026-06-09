using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly AccountManager _accountManager;
        private readonly AppSettings _settings;

        [ObservableProperty]
        private ObservableCollection<AccountViewModel> _accounts = new();

        [ObservableProperty]
        private AccountViewModel? _selectedAccount;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _statusText = "就绪";

        [ObservableProperty]
        private bool _isSteamRunning;

        [ObservableProperty]
        private bool _autoStartSteam;

        [ObservableProperty]
        private bool _minimizeToTray;

        [ObservableProperty]
        private bool _startWithWindows;

        [ObservableProperty]
        private bool _isTaskbarPinned;

        [ObservableProperty]
        private TaskbarPosition _taskbarPosition;

        [ObservableProperty]
        private int _taskbarOffset;

        [ObservableProperty]
        private int _avatarSize;

        [ObservableProperty]
        private bool _glassEnabled;

        [ObservableProperty]
        private bool _roundedMode;

        public AccountManager GetAccountManager() => _accountManager;
        public AppSettings GetSettings() => _settings;

        public List<SteamAccount> GetPinnedAccounts()
        {
            return Accounts.Where(a => a.IsPinned).Select(a => a.Account).ToList();
        }

        public MainViewModel()
        {
            _accountManager = new AccountManager();
            _accountManager.AccountsChanged += OnAccountsChanged;

            _settings = SettingsService.Load();
            _autoStartSteam = _settings.AutoStartSteam;
            _minimizeToTray = _settings.MinimizeToTray;
            _startWithWindows = _settings.StartWithWindows;
            _isTaskbarPinned = _settings.TaskbarPinned;
            _taskbarPosition = _settings.TaskbarPosition;
            _taskbarOffset = _settings.TaskbarOffset;
            _avatarSize = _settings.AvatarSize;
            _glassEnabled = _settings.GlassEnabled;
            _roundedMode = _settings.RoundedMode;

            PropertyChanged += OnPropertyChanged;
        }

        private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(AutoStartSteam):
                    _settings.AutoStartSteam = AutoStartSteam;
                    break;
                case nameof(MinimizeToTray):
                    _settings.MinimizeToTray = MinimizeToTray;
                    break;
                case nameof(StartWithWindows):
                    _settings.StartWithWindows = StartWithWindows;
                    break;
                case nameof(IsTaskbarPinned):
                    _settings.TaskbarPinned = IsTaskbarPinned;
                    break;
                case nameof(TaskbarPosition):
                    _settings.TaskbarPosition = TaskbarPosition;
                    break;
                case nameof(TaskbarOffset):
                    _settings.TaskbarOffset = TaskbarOffset;
                    break;
                case nameof(AvatarSize):
                    _settings.AvatarSize = AvatarSize;
                    break;
                case nameof(GlassEnabled):
                    _settings.GlassEnabled = GlassEnabled;
                    break;
                case nameof(RoundedMode):
                    _settings.RoundedMode = RoundedMode;
                    break;
            }
            SettingsService.Save(_settings);
        }

        private void OnAccountsChanged(object? sender, EventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateAccountsList();
            });
        }

        public async Task InitializeAsync()
        {
            IsLoading = true;
            StatusText = "正在检测Steam...";

            var success = await _accountManager.InitializeAsync();
            if (!success)
            {
                StatusText = "未找到Steam安装路径";
                IsLoading = false;
                return;
            }

            StatusText = $"已检测到 {_accountManager.Accounts.Count} 个账号";
            IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();

            UpdateAccountsList();
            IsLoading = false;
        }

        private void UpdateAccountsList()
        {
            var pinnedIds = _settings.PinnedAccountIds ?? new List<string>();
            
            Accounts.Clear();
            foreach (var account in _accountManager.Accounts)
            {
                var vm = new AccountViewModel(account);
                vm.IsPinned = pinnedIds.Contains(account.SteamId);
                vm.PropertyChanged += OnAccountPropertyChanged;
                Accounts.Add(vm);
            }

            if (_accountManager.CurrentAccount != null)
            {
                SelectedAccount = Accounts.FirstOrDefault(a => a.Account.SteamId == _accountManager.CurrentAccount.SteamId);
            }
        }

        private void OnAccountPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AccountViewModel.IsPinned))
            {
                var pinnedIds = Accounts.Where(a => a.IsPinned).Select(a => a.SteamId).ToList();
                _settings.PinnedAccountIds = pinnedIds;
                SettingsService.Save(_settings);
            }
        }

        [RelayCommand]
        private async Task SwitchAndLaunchAsync()
        {
            if (SelectedAccount == null) return;

            IsLoading = true;
            StatusText = "正在切换账号...";

            var success = await _accountManager.SwitchAccountAsync(SelectedAccount.Account);
            if (success)
            {
                StatusText = $"已切换到 {SelectedAccount.Account.PersonaName}";
                UpdateCurrentAccount();

                if (AutoStartSteam)
                {
                    StatusText = "正在启动Steam...";
                    _accountManager.LaunchSteam();
                }
            }
            else
            {
                StatusText = "切换失败，请确保Steam已关闭";
            }

            IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
            IsLoading = false;
        }

        [RelayCommand]
        private async Task SwitchOnlyAsync()
        {
            if (SelectedAccount == null) return;

            IsLoading = true;
            StatusText = "正在切换账号...";

            var success = await _accountManager.SwitchAccountAsync(SelectedAccount.Account);
            if (success)
            {
                StatusText = $"已切换到 {SelectedAccount.Account.PersonaName}";
                UpdateCurrentAccount();
            }
            else
            {
                StatusText = "切换失败，请确保Steam已关闭";
            }

            IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
            IsLoading = false;
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            IsLoading = true;
            StatusText = "正在刷新账号列表...";

            await _accountManager.LoadAccountsAsync();
            IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();

            UpdateAccountsList();
            StatusText = $"已刷新，共 {_accountManager.Accounts.Count} 个账号";
            IsLoading = false;
        }

        [RelayCommand]
        private void LaunchSteam()
        {
            _accountManager.LaunchSteam();
            IsSteamRunning = true;
            StatusText = "Steam已启动";
        }

        [RelayCommand]
        private void CloseSteam()
        {
            _ = _accountManager.GetSteamService().CloseSteamAsync();
            IsSteamRunning = false;
            StatusText = "正在关闭Steam...";
        }

        private void UpdateCurrentAccount()
        {
            foreach (var acc in Accounts)
            {
                acc.IsCurrent = acc.Account == _accountManager.CurrentAccount;
            }
        }
    }

    public partial class AccountViewModel : ObservableObject
    {
        public SteamAccount Account { get; }

        public string DisplayName => Account.PersonaName;
        public string Username => Account.AccountName;
        public string SteamId => Account.SteamId;

        [ObservableProperty]
        private bool _isCurrent;

        private BitmapImage? _avatar;
        public BitmapImage? Avatar
        {
            get => _avatar;
            set => SetProperty(ref _avatar, value);
        }

        [ObservableProperty]
        private bool _isPinned;

        public AccountViewModel(SteamAccount account)
        {
            Account = account;
            _isCurrent = account.MostRecent;
            LoadAvatar();
        }

        private void LoadAvatar()
        {
            Avatar = AccountManager.LoadImage(Account.AvatarPath);
        }
    }
}
