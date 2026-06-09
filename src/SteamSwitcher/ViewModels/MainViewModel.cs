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
        private readonly GameAccountBinding _gameBinding;
        private SteamCEFInjector? _injector;
        private WebSocketServer? _wsServer;

        [ObservableProperty]
        private ObservableCollection<AccountViewModel> _accounts = new();

        [ObservableProperty]
        private ObservableCollection<GameBindingViewModel> _gameBindings = new();

        [ObservableProperty]
        private AccountViewModel? _selectedAccount;

        [ObservableProperty]
        private GameBindingViewModel? _selectedGameBinding;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _statusText = "就绪";

        [ObservableProperty]
        private bool _isSteamRunning;

        [ObservableProperty]
        private bool _isInjectorConnected;

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

        [ObservableProperty]
        private bool _enableLibraryInjection;

        public AccountManager GetAccountManager() => _accountManager;
        public AppSettings GetSettings() => _settings;
        public GameAccountBinding GetGameBinding() => _gameBinding;

        public List<SteamAccount> GetPinnedAccounts()
        {
            return Accounts.Where(a => a.IsPinned).Select(a => a.Account).ToList();
        }

        public MainViewModel()
        {
            _accountManager = new AccountManager();
            _accountManager.AccountsChanged += OnAccountsChanged;

            _gameBinding = new GameAccountBinding();
            _gameBinding.BindingsChanged += OnGameBindingsChanged;

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
            _enableLibraryInjection = _settings.EnableLibraryInjection;

            PropertyChanged += OnPropertyChanged;
        }

        private void OnGameBindingsChanged(object? sender, EventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateGameBindingsList();
            });
        }

        private void UpdateGameBindingsList()
        {
            GameBindings.Clear();
            foreach (var binding in _gameBinding.GetAllBindings().Values)
            {
                GameBindings.Add(new GameBindingViewModel(binding));
            }
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
                case nameof(EnableLibraryInjection):
                    _settings.EnableLibraryInjection = EnableLibraryInjection;
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
            
            // 加载游戏绑定
            await _gameBinding.LoadAsync();
            UpdateGameBindingsList();
            
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
            await SwitchAccountAsync(launchSteam: true);
        }

        [RelayCommand]
        private async Task SwitchOnlyAsync()
        {
            await SwitchAccountAsync(launchSteam: false);
        }

        private async Task SwitchAccountAsync(bool launchSteam)
        {
            if (SelectedAccount == null) return;

            IsLoading = true;
            StatusText = "正在切换账号...";

            try
            {
                var success = await _accountManager.SwitchAccountAsync(SelectedAccount.Account);
                if (success)
                {
                    StatusText = $"已切换到 {SelectedAccount.Account.PersonaName}";
                    UpdateCurrentAccount();

                    if (launchSteam && AutoStartSteam)
                    {
                        StatusText = "正在启动Steam...";
                        _accountManager.LaunchSteam();
                    }
                }
                else
                {
                    StatusText = "切换失败，请确保Steam已关闭";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"切换失败: {ex.Message}";
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

        [RelayCommand]
        private async Task StartInjectorAsync()
        {
            IsLoading = true;
            StatusText = "正在启动注入器...";

            try
            {
                // 先启动WebSocket服务器获取随机端口
                int wsPort = 0;
                try
                {
                    _wsServer = new WebSocketServer(0);
                    _wsServer.MessageReceived += OnWebSocketMessage;
                    await _wsServer.StartAsync();
                    wsPort = _wsServer.ActualPort;
                }
                catch (Exception wsEx)
                {
                    StatusText = $"WebSocket启动失败: {wsEx.Message}";
                    IsLoading = false;
                    return;
                }

                // 创建注入器并注入文件
                _injector = new SteamCEFInjector(_gameBinding, _accountManager);
                _injector.StatusChanged += (s, msg) => 
                {
                    Application.Current.Dispatcher.Invoke(() => StatusText = msg);
                };

                var injected = _injector.InjectCustomFiles(wsPort);
                
                if (injected)
                {
                    IsInjectorConnected = true;
                    
                    // 自动重启Steam库界面
                    StatusText = "正在重启Steam库界面...";
                    await Task.Delay(500);
                    _injector.RestartSteamLibrary();
                    
                    StatusText = $"注入完成！端口: {wsPort}";
                }
                else
                {
                    StatusText = "注入失败，请检查Steam路径";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"注入器启动失败: {ex.Message}";
            }

            IsLoading = false;
        }

        [RelayCommand]
        private void StopInjector()
        {
            _injector?.Dispose();
            _injector = null;
            _wsServer?.Stop();
            _wsServer?.Dispose();
            _wsServer = null;
            IsInjectorConnected = false;
            StatusText = "注入器已停止";
        }

        private async void OnWebSocketMessage(object? sender, (string action, System.Text.Json.JsonElement data) e)
        {
            try
            {
                switch (e.action)
                {
                    case "getAccounts":
                        // 发送账号列表到前端
                        var accList = Accounts.Select(a => new
                        {
                            steamId = a.SteamId,
                            name = a.DisplayName,
                            username = a.Username,
                            isCurrent = a.IsCurrent
                        }).ToList();
                        
                        await _wsServer?.SendToAllAsync("accountsData", new
                        {
                            accounts = accList,
                            current = _accountManager.CurrentAccount?.PersonaName
                        });
                        break;

                    case "switchAccount":
                        if (e.data.TryGetProperty("steamId", out var switchSteamId))
                        {
                            var targetAccount = _accountManager.Accounts.FirstOrDefault(
                                a => a.SteamId == switchSteamId.GetString());
                            
                            if (targetAccount != null)
                            {
                                var success = await _accountManager.SwitchAccountAsync(targetAccount);
                                
                                await _wsServer?.SendToAllAsync("switchResult", new
                                {
                                    success = success,
                                    accountName = targetAccount.PersonaName,
                                    error = success ? "" : "请先关闭Steam"
                                });
                                
                                if (success)
                                {
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        UpdateCurrentAccount();
                                        StatusText = $"已切换到 {targetAccount.PersonaName}";
                                    });
                                }
                            }
                        }
                        break;

                    case "switchAndLaunch":
                        if (e.data.TryGetProperty("appId", out var appIdElement))
                        {
                            var appId = appIdElement.GetInt32();
                            var gameName = e.data.TryGetProperty("gameName", out var nameEl) 
                                ? nameEl.GetString() ?? "" : "";
                            
                            var binding = _gameBinding.GetBinding(appId);
                            if (binding?.AccountSteamId != null)
                            {
                                var account = _accountManager.Accounts.FirstOrDefault(
                                    a => a.SteamId == binding.AccountSteamId);
                                
                                if (account != null)
                                {
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        SelectedAccount = Accounts.FirstOrDefault(
                                            a => a.SteamId == account.SteamId);
                                    });
                                    
                                    await _accountManager.SwitchAccountAsync(account);
                                    _accountManager.LaunchSteam();
                                    
                                    await _gameBinding.RecordPlayAsync(
                                        appId, account.SteamId, account.AccountName);
                                }
                            }
                        }
                        break;

                    case "setBinding":
                        if (e.data.TryGetProperty("appId", out var bindAppId) &&
                            e.data.TryGetProperty("gameName", out var bindGameName) &&
                            e.data.TryGetProperty("steamId", out var bindSteamId) &&
                            e.data.TryGetProperty("accountName", out var bindAccountName))
                        {
                            await _gameBinding.SetBindingAsync(
                                bindAppId.GetInt32(),
                                bindGameName.GetString() ?? "",
                                bindSteamId.GetString() ?? "",
                                bindAccountName.GetString() ?? "");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebSocket message error: {ex.Message}");
            }
        }

        private void UpdateCurrentAccount()
        {
            foreach (var acc in Accounts)
            {
                acc.IsCurrent = acc.Account == _accountManager.CurrentAccount;
            }
        }
    }

    public partial class GameBindingViewModel : ObservableObject
    {
        public GameBinding Binding { get; }

        public int AppId => Binding.AppId;
        public string GameName => Binding.GameName;
        public string? AccountName => Binding.AccountName;
        public bool AutoSwitch => Binding.AutoSwitch;
        public string LastPlayed => Binding.LastPlayed?.ToString("MM-dd HH:mm") ?? "从未";

        public GameBindingViewModel(GameBinding binding)
        {
            Binding = binding;
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
