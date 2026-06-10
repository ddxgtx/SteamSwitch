using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
        private const int SteamDebugPort = 8080;
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
        private ObservableCollection<GameListViewModel> _gameList = new();

        [ObservableProperty]
        private GameListViewModel? _selectedGame;

        [ObservableProperty]
        private AccountViewModel? _selectedBindingAccount;

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
        private bool _desktopFloatingEnabled;

        [ObservableProperty]
        private bool _desktopFloatingTopmost;

        [ObservableProperty]
        private bool _desktopFloatingLocked;

        [ObservableProperty]
        private int _desktopFloatingOpacity;

        [ObservableProperty]
        private bool _enableLibraryInjection;

        [ObservableProperty]
        private bool _autoScanGamesOnStartup;

        [ObservableProperty]
        private bool _confirmBeforeGameLaunch;

        public AccountManager GetAccountManager() => _accountManager;
        public AppSettings GetSettings() => _settings;
        public GameAccountBinding GetGameBinding() => _gameBinding;

        public event EventHandler? PinnedGamesChanged;
        public event EventHandler? PinnedAccountsChanged;

        public string AccountCountText => Accounts.Count.ToString();
        public string BindingCountText => GameBindings.Count.ToString();
        public string CurrentAccountName =>
            _accountManager.CurrentAccount?.PersonaName ??
            _accountManager.CurrentAccount?.AccountName ??
            "未检测";
        public string SteamStateText => IsSteamRunning ? "运行中" : "未运行";
        public string InjectorStateText => IsInjectorConnected ? "已连接" : "未注入";

        public bool ShowEmptyHint => GameList.Count == 0;

        public string SelectedGameName => SelectedGame?.Name ?? "未选择";
        public string SelectedGameAppId => SelectedGame?.AppId.ToString() ?? "-";

        public bool SelectedGameHasBinding =>
            SelectedGame != null && !string.IsNullOrEmpty(SelectedGame.BindingAccountName);

        public string SelectedGameBindingAccount =>
            SelectedGame != null && !string.IsNullOrEmpty(SelectedGame.BindingAccountName)
                ? SelectedGame.BindingAccountName
                : "";

        public string SelectedGameBindingTime => SelectedGame?.BindingLastPlayed ?? "";

        public List<SteamAccount> GetPinnedAccounts()
        {
            return Accounts.Where(a => a.IsPinned).Select(a => a.Account).ToList();
        }

        public List<GameListViewModel> GetPinnedGames()
        {
            return GameList.Where(g => g.IsPinned).ToList();
        }

        public bool IsGamePinned(int appId)
        {
            return _settings.PinnedGameIds.Contains(appId);
        }

        public void ToggleGamePin(int appId)
        {
            if (_settings.PinnedGameIds.Contains(appId))
                _settings.PinnedGameIds.Remove(appId);
            else
                _settings.PinnedGameIds.Add(appId);

            SettingsService.Save(_settings);

            var game = GameList.FirstOrDefault(g => g.AppId == appId);
            if (game != null)
                game.IsPinned = _settings.PinnedGameIds.Contains(appId);

            PinnedGamesChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task LaunchPinnedGameAsync(int appId)
        {
            var game = GameList.FirstOrDefault(g => g.AppId == appId);
            if (game == null || !game.HasBinding || string.IsNullOrEmpty(game.BindingAccountSteamId))
            {
                StatusText = "该游戏未绑定账号";
                return;
            }

            var account = _accountManager.Accounts.FirstOrDefault(a => a.SteamId == game.BindingAccountSteamId);
            if (account == null)
            {
                StatusText = "绑定的账号不存在";
                return;
            }

            IsLoading = true;
            StatusText = $"正在切换账号并启动 {game.GameName}...";

            try
            {
                var success = await _accountManager.SwitchAccountAsync(account);
                if (!success)
                {
                    StatusText = "切换失败，请确保 Steam 已关闭";
                    return;
                }

                SelectedAccount = Accounts.FirstOrDefault(a => a.SteamId == account.SteamId);
                UpdateCurrentAccount();

                _accountManager.LaunchGame(appId);
                StatusText = $"已启动 {game.GameName}";
            }
            catch (Exception ex)
            {
                AppLogger.Error($"LaunchPinnedGameAsync failed. appId={appId}", ex);
                StatusText = $"启动失败: {ex.Message}";
            }
            finally
            {
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                IsLoading = false;
            }
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
            _desktopFloatingEnabled = _settings.DesktopFloatingEnabled;
            _desktopFloatingTopmost = _settings.DesktopFloatingTopmost;
            _desktopFloatingLocked = _settings.DesktopFloatingLocked;
            _desktopFloatingOpacity = _settings.DesktopFloatingOpacity;
            _enableLibraryInjection = _settings.EnableLibraryInjection;
            _autoScanGamesOnStartup = _settings.AutoScanGamesOnStartup;
            _confirmBeforeGameLaunch = _settings.ConfirmBeforeGameLaunch;
            SettingsService.SetStartWithWindows(_startWithWindows);

            PropertyChanged += OnPropertyChanged;
        }

        private void OnGameBindingsChanged(object? sender, EventArgs e)
        {
            Application.Current.Dispatcher.Invoke(UpdateGameBindingsList);
        }

        private void UpdateGameBindingsList()
        {
            GameBindings.Clear();
            foreach (var binding in _gameBinding.GetAllBindings().Values)
            {
                GameBindings.Add(new GameBindingViewModel(binding));
            }

            OnPropertyChanged(nameof(BindingCountText));
            RefreshGameListBindings();
        }

        public void RefreshGameListBindings()
        {
            var allBindings = _gameBinding.GetAllBindings();
            foreach (var game in GameList)
            {
                if (allBindings.TryGetValue(game.AppId, out var binding))
                {
                    game.BindingAccountName = binding.AccountName;
                    game.BindingAccountSteamId = binding.AccountSteamId;
                    game.BindingLastPlayed = binding.LastPlayed?.ToString("MM-dd HH:mm") ?? "从未";
                    game.HasBinding = true;
                }
                else
                {
                    game.BindingAccountName = null;
                    game.BindingAccountSteamId = null;
                    game.BindingLastPlayed = null;
                    game.HasBinding = false;
                }
            }

            OnPropertyChanged(nameof(SelectedGameHasBinding));
            OnPropertyChanged(nameof(SelectedGameBindingAccount));
            OnPropertyChanged(nameof(SelectedGameBindingTime));
        }

        public async Task ScanGamesAsync()
        {
            IsLoading = true;
            StatusText = "正在扫描本地 Steam 游戏...";

            await Task.Run(() =>
            {
                var steamService = _accountManager.GetSteamService();
                var games = steamService.GetInstalledGames();
                var allBindings = _gameBinding.GetAllBindings();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    GameList.Clear();
                    var pinnedIds = new HashSet<int>(_settings.PinnedGameIds);
                    foreach (var game in games)
                    {
                        var vm = new GameListViewModel(game.AppId, game.Name)
                        {
                            InstallDir = game.InstallDir,
                            IconPath = game.IconPath,
                            SizeDisplay = game.SizeDisplay,
                            IsPinned = pinnedIds.Contains(game.AppId)
                        };

                        if (allBindings.TryGetValue(game.AppId, out var binding))
                        {
                            vm.HasBinding = true;
                            vm.BindingAccountName = binding.AccountName;
                            vm.BindingAccountSteamId = binding.AccountSteamId;
                            vm.BindingLastPlayed = binding.LastPlayed?.ToString("MM-dd HH:mm") ?? "从未";
                        }

                        GameList.Add(vm);
                    }

                    OnPropertyChanged(nameof(ShowEmptyHint));
                });
            });

            if (GameList.Count == 0)
            {
                StatusText = $"未找到已安装的游戏，请在 Steam 中安装游戏后重试";
            }
            else
            {
                StatusText = $"已找到 {GameList.Count} 个已安装的游戏";
            }

            IsLoading = false;
        }

        public async Task SaveGameBinding(int appId, string gameName, string steamId, string accountName)
        {
            await _gameBinding.SetBindingAsync(appId, gameName, steamId, accountName);

            var existing = GameList.FirstOrDefault(g => g.AppId == appId);
            if (existing != null)
            {
                existing.HasBinding = true;
                existing.BindingAccountName = accountName;
                existing.BindingAccountSteamId = steamId;
                existing.BindingLastPlayed = DateTime.Now.ToString("MM-dd HH:mm");
                existing.GameName = gameName;
            }

            OnPropertyChanged(nameof(SelectedGameHasBinding));
            OnPropertyChanged(nameof(SelectedGameBindingAccount));
            OnPropertyChanged(nameof(SelectedGameBindingTime));
            StatusText = $"已绑定 {gameName} → {accountName}";
        }

        public async Task RemoveGameBinding(int appId)
        {
            await _gameBinding.RemoveBindingAsync(appId);

            var existing = GameList.FirstOrDefault(g => g.AppId == appId);
            if (existing != null)
            {
                existing.HasBinding = false;
                existing.BindingAccountName = null;
                existing.BindingAccountSteamId = null;
                existing.BindingLastPlayed = null;
            }

            OnPropertyChanged(nameof(SelectedGameHasBinding));
            OnPropertyChanged(nameof(SelectedGameBindingAccount));
            OnPropertyChanged(nameof(SelectedGameBindingTime));
            StatusText = "已删除绑定";
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
                    SettingsService.SetStartWithWindows(StartWithWindows);
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
                case nameof(DesktopFloatingEnabled):
                    _settings.DesktopFloatingEnabled = DesktopFloatingEnabled;
                    break;
                case nameof(DesktopFloatingTopmost):
                    _settings.DesktopFloatingTopmost = DesktopFloatingTopmost;
                    break;
                case nameof(DesktopFloatingLocked):
                    _settings.DesktopFloatingLocked = DesktopFloatingLocked;
                    break;
                case nameof(DesktopFloatingOpacity):
                    _settings.DesktopFloatingOpacity = Math.Clamp(DesktopFloatingOpacity, 45, 100);
                    break;
                case nameof(EnableLibraryInjection):
                    _settings.EnableLibraryInjection = EnableLibraryInjection;
                    if (EnableLibraryInjection)
                        _ = StartInjectorAsync();
                    else
                        StopInjector();
                    break;
                case nameof(AutoScanGamesOnStartup):
                    _settings.AutoScanGamesOnStartup = AutoScanGamesOnStartup;
                    break;
                case nameof(ConfirmBeforeGameLaunch):
                    _settings.ConfirmBeforeGameLaunch = ConfirmBeforeGameLaunch;
                    break;
            }

            SettingsService.Save(_settings);
        }

        private void OnAccountsChanged(object? sender, EventArgs e)
        {
            Application.Current.Dispatcher.Invoke(UpdateAccountsList);
        }

        public async Task InitializeAsync()
        {
            IsLoading = true;
            StatusText = "正在检测 Steam...";

            var success = await _accountManager.InitializeAsync();
            if (!success)
            {
                StatusText = "未找到 Steam 安装路径";
                IsLoading = false;
                return;
            }

            StatusText = $"已检测到 {_accountManager.Accounts.Count} 个账号";
            IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();

            UpdateAccountsList();

            await _gameBinding.LoadAsync();
            UpdateGameBindingsList();

            if (AutoScanGamesOnStartup)
            {
                await ScanGamesAsync();
            }

            IsLoading = false;

            if (EnableLibraryInjection)
            {
                AppLogger.Info("Library injection is enabled; auto-starting injector after initialization.");
                _ = StartInjectorAsync();
            }
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

            UpdateCurrentAccount();
            OnPropertyChanged(nameof(AccountCountText));
        }

        partial void OnIsSteamRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(SteamStateText));
        }

        partial void OnIsInjectorConnectedChanged(bool value)
        {
            OnPropertyChanged(nameof(InjectorStateText));
        }

        partial void OnSelectedGameChanged(GameListViewModel? value)
        {
            OnPropertyChanged(nameof(SelectedGameName));
            OnPropertyChanged(nameof(SelectedGameAppId));
            OnPropertyChanged(nameof(SelectedGameHasBinding));
            OnPropertyChanged(nameof(SelectedGameBindingAccount));
            OnPropertyChanged(nameof(SelectedGameBindingTime));
        }

        partial void OnGameListChanged(ObservableCollection<GameListViewModel> value)
        {
            OnPropertyChanged(nameof(ShowEmptyHint));
            OnPropertyChanged(nameof(GameList));
        }

        private void OnAccountPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AccountViewModel.IsPinned))
            {
                var pinnedIds = Accounts.Where(a => a.IsPinned).Select(a => a.SteamId).ToList();
                _settings.PinnedAccountIds = pinnedIds;
                SettingsService.Save(_settings);
                PinnedAccountsChanged?.Invoke(this, EventArgs.Empty);
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
                        StatusText = "正在启动 Steam...";
                        _accountManager.LaunchSteam();
                    }
                }
                else
                {
                    StatusText = "切换失败，请确保 Steam 已关闭";
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("SwitchAccountAsync failed.", ex);
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
            StatusText = "Steam 已启动";
        }

        [RelayCommand]
        private void CloseSteam()
        {
            _ = _accountManager.GetSteamService().CloseSteamAsync();
            IsSteamRunning = false;
            StatusText = "正在关闭 Steam...";
        }

        [RelayCommand]
        private async Task StartInjectorAsync()
        {
            if (_injector != null && _injector.IsConnected)
            {
                StatusText = $"注入器已连接，日志: {AppLogger.CurrentLogPath}";
                return;
            }

            IsLoading = true;
            StatusText = "正在启动注入器...";
            AppLogger.Info("StartInjectorAsync requested.");

            try
            {
                _wsServer?.Stop();
                _wsServer?.Dispose();
                _wsServer = new WebSocketServer(8081);
                _wsServer.MessageReceived += OnWebSocketMessage;
                _wsServer.ClientConnected += (s, id) => AppLogger.Info($"Library WebSocket client connected: {id}");
                _wsServer.ClientDisconnected += (s, id) => AppLogger.Info($"Library WebSocket client disconnected: {id}");
                await _wsServer.StartAsync();
                AppLogger.Info("Local WebSocket server started on port 8081.");

                var debugReady = await EnsureSteamDebuggingAsync();
                if (!debugReady)
                {
                    StatusText = $"无法打开 Steam 调试端口，日志: {AppLogger.CurrentLogPath}";
                    return;
                }

                _injector = new SteamCEFInjector(_gameBinding, _accountManager, SteamDebugPort);
                _injector.StatusChanged += (s, msg) =>
                {
                    AppLogger.Info($"Injector status: {msg}");
                    StatusText = msg;
                };

                var connected = await _injector.ConnectAsync();
                IsInjectorConnected = connected;
                StatusText = connected
                    ? $"注入器已启动，日志: {AppLogger.CurrentLogPath}"
                    : $"注入失败，日志: {AppLogger.CurrentLogPath}";
            }
            catch (Exception ex)
            {
                AppLogger.Error("StartInjectorAsync failed.", ex);
                StatusText = $"注入启动失败: {ex.Message}，日志: {AppLogger.CurrentLogPath}";
            }
            finally
            {
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                IsLoading = false;
            }
        }

        private async Task<bool> EnsureSteamDebuggingAsync()
        {
            if (await IsSteamDebugPortOpenAsync())
            {
                AppLogger.Info("Steam debug port is already open.");
                return true;
            }

            var steamService = _accountManager.GetSteamService();
            if (steamService.IsSteamRunning())
            {
                StatusText = "Steam 已运行但未开启调试模式，正在重启 Steam...";
                AppLogger.Info("Steam is running without debug port. Restarting with CEF debugging.");

                var closed = await steamService.CloseSteamAsync();
                if (!closed)
                {
                    AppLogger.Info("Failed to close Steam before starting debug mode.");
                    return false;
                }
            }

            StatusText = "正在以调试模式启动 Steam...";
            var started = steamService.StartSteamWithDebugging();
            if (!started)
            {
                AppLogger.Info("StartSteamWithDebugging returned false.");
                return false;
            }

            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(1000);
                if (await IsSteamDebugPortOpenAsync())
                {
                    AppLogger.Info($"Steam debug port became ready after {i + 1} seconds.");
                    await Task.Delay(1500);
                    return true;
                }
            }

            AppLogger.Info("Timed out waiting for Steam debug port.");
            return false;
        }

        private static async Task<bool> IsSteamDebugPortOpenAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var response = await http.GetAsync($"http://127.0.0.1:{SteamDebugPort}/json/version");
                var ok = response.IsSuccessStatusCode;
                AppLogger.Info($"Debug port check: status={(int)response.StatusCode}, ok={ok}");
                return ok;
            }
            catch (Exception ex)
            {
                AppLogger.Info($"Debug port check failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        [RelayCommand]
        private void StopInjector()
        {
            AppLogger.Info("StopInjector requested.");
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
                AppLogger.Info($"WebSocket action received: {e.action}");

                switch (e.action)
                {
                    case "log":
                        if (e.data.TryGetProperty("message", out var msg))
                        {
                            AppLogger.Info($"Library script: {msg.GetString()}");
                        }
                        break;

                    case "switchAndLaunch":
                        if (e.data.TryGetProperty("appId", out var appIdElement))
                        {
                            var appId = appIdElement.GetInt32();
                            var gameName = e.data.TryGetProperty("gameName", out var nameEl)
                                ? nameEl.GetString() ?? ""
                                : "";
                            var steamId = e.data.TryGetProperty("steamId", out var steamIdEl)
                                ? steamIdEl.GetString()
                                : null;

                            await SwitchAccountAndLaunchGameAsync(appId, gameName, steamId);
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
                            AppLogger.Info($"Binding saved from library: appId={bindAppId.GetInt32()}, account={bindAccountName.GetString()}");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("WebSocket message handling failed.", ex);
                System.Diagnostics.Debug.WriteLine($"WebSocket message error: {ex.Message}");
            }
        }

        private async Task SwitchAccountAndLaunchGameAsync(int appId, string gameName, string? steamId)
        {
            var account = !string.IsNullOrEmpty(steamId)
                ? _accountManager.Accounts.FirstOrDefault(a => a.SteamId == steamId)
                : null;

            var binding = _gameBinding.GetBinding(appId);
            if (account == null && !string.IsNullOrEmpty(binding?.AccountSteamId))
            {
                account = _accountManager.Accounts.FirstOrDefault(a => a.SteamId == binding.AccountSteamId);
            }

            if (account == null)
            {
                AppLogger.Info($"No account binding found for appId={appId}.");
                StatusText = "请先为此游戏选择账号";
                return;
            }

            IsLoading = true;
            StatusText = "正在切换账号并启动游戏...";

            try
            {
                var success = await _accountManager.SwitchAccountAsync(account);
                if (!success)
                {
                    StatusText = "切换失败，请确保 Steam 已关闭";
                    AppLogger.Info($"SwitchAccountAsync returned false for account={account.AccountName}, appId={appId}");
                    return;
                }

                SelectedAccount = Accounts.FirstOrDefault(a => a.SteamId == account.SteamId);
                UpdateCurrentAccount();

                if (!string.IsNullOrWhiteSpace(gameName))
                {
                    await _gameBinding.SetBindingAsync(appId, gameName, account.SteamId, account.AccountName);
                }
                else
                {
                    await _gameBinding.RecordPlayAsync(appId, account.SteamId, account.AccountName);
                }

                var launched = _accountManager.LaunchGame(appId);
                StatusText = launched
                    ? $"已切换到 {account.PersonaName}，正在启动游戏"
                    : $"已切换到 {account.PersonaName}，但启动游戏失败";
                AppLogger.Info($"Switch and launch result: appId={appId}, account={account.AccountName}, launched={launched}");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"SwitchAccountAndLaunchGameAsync failed. appId={appId}", ex);
                StatusText = $"启动失败: {ex.Message}";
            }
            finally
            {
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                IsLoading = false;
            }
        }

        private void UpdateCurrentAccount()
        {
            var currentSteamId = _accountManager.CurrentAccount?.SteamId;
            foreach (var acc in Accounts)
            {
                acc.IsCurrent = !string.IsNullOrEmpty(currentSteamId) && acc.SteamId == currentSteamId;
            }

            OnPropertyChanged(nameof(CurrentAccountName));
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

    public partial class GameListViewModel : ObservableObject
    {
        public int AppId { get; }

        [ObservableProperty]
        private string _gameName = "";

        public string Name => GameName;

        [ObservableProperty]
        private string? _installDir;

        [ObservableProperty]
        private string? _iconPath;

        [ObservableProperty]
        private string _sizeDisplay = "";

        [ObservableProperty]
        private bool _hasBinding;

        [ObservableProperty]
        private string? _bindingAccountName;

        [ObservableProperty]
        private string? _bindingAccountSteamId;

        [ObservableProperty]
        private string? _bindingLastPlayed;

        [ObservableProperty]
        private bool _isPinned;

        public GameListViewModel(int appId, string name)
        {
            AppId = appId;
            GameName = name;
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
