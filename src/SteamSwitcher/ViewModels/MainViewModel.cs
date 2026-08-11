using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.ViewModels
{
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private const int SteamDebugPort = 8080;
        private readonly AccountManager _accountManager;
        private readonly AppSettings _settings;
        private readonly GameAccountBinding _gameBinding;
        private readonly QuickLaunchService _quickLaunch;
        private SteamCEFInjector? _injector;
        private WebSocketServer? _wsServer;
        private readonly DispatcherTimer _settingsSaveTimer;
        private bool _settingsDirty;
        private bool _restoringStartWithWindows;
        private bool _disposed;

        [ObservableProperty]
        private ObservableCollection<AccountViewModel> _accounts = new();

        [ObservableProperty]
        private ObservableCollection<GameBindingViewModel> _gameBindings = new();

        [ObservableProperty]
        private ObservableCollection<GameListViewModel> _gameList = new();

        [ObservableProperty]
        private ObservableCollection<QuickLaunchViewModel> _quickLaunchList = new();

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
        private int _taskbarOffsetX;

        [ObservableProperty]
        private int _taskbarOffsetY;

        [ObservableProperty]
        private int _taskbarWindowSize;

        [ObservableProperty]
        private bool _minimalMode;

        [ObservableProperty]
        private bool _desktopFloatingDockEnabled;

        [ObservableProperty]
        private int _taskbarAvatarSize;

        [ObservableProperty]
        private bool _taskbarGlassEnabled;

        [ObservableProperty]
        private bool _taskbarRoundedMode;

        [ObservableProperty]
        private bool _desktopFloatingEnabled;

        [ObservableProperty]
        private bool _desktopFloatingTopmost;

        [ObservableProperty]
        private bool _desktopFloatingLocked;

        [ObservableProperty]
        private int _desktopFloatingOpacity;

        [ObservableProperty]
        private int _desktopFloatingAvatarSize;

        [ObservableProperty]
        private bool _desktopFloatingGlassEnabled;

        [ObservableProperty]
        private bool _desktopFloatingRoundedMode;

        [ObservableProperty]
        private string _desktopFloatingGlassColor = "#4A90D9";

        [ObservableProperty]
        private string _theme = "Dark";

        public bool IsDarkTheme
        {
            get => Theme == "Dark";
            set
            {
                Theme = value ? "Dark" : "Light";
                OnPropertyChanged();
            }
        }

        [ObservableProperty]
        private bool _enableLibraryInjection;

        [ObservableProperty]
        private string _customSteamPath = "";

        [ObservableProperty]
        private string _customLoginUsersPath = "";

        [ObservableProperty]
        private bool _autoScanGamesOnStartup;

        [ObservableProperty]
        private bool _confirmBeforeGameLaunch;

        [ObservableProperty]
        private bool _silentCloseSteam = true;

        [ObservableProperty]
        private bool _checkUpdateOnStartup = true;

        [ObservableProperty]
        private bool _autoInstallUpdates;

        [ObservableProperty]
        private bool _showNotificationOnSteamClose;

        public AccountManager GetAccountManager() => _accountManager;
        public AppSettings GetSettings() => _settings;
        public GameAccountBinding GetGameBinding() => _gameBinding;

        public event EventHandler? PinnedGamesChanged;
        public event EventHandler? PinnedAccountsChanged;
        public event EventHandler? QuickLaunchChanged;
        public event EventHandler<string>? NotificationRequested;
        public event EventHandler<UpdateInfo>? UpdateAvailable;
        public event EventHandler<bool>? MinimalModeChanged;
        public event EventHandler<bool>? DesktopFloatingDockEnabledChanged;

        public string AccountCountText => Accounts.Count.ToString();
        public string BindingCountText => GameBindings.Count.ToString();
        public string GameCountText => GameList.Count.ToString();
        public string QuickLaunchCountText => QuickLaunchList.Count.ToString();
        public string CurrentVersionText => $"当前版本：v{UpdateService.GetCurrentVersion()}";
        public string CompactVersionText => $"v{UpdateService.GetCurrentVersion()} · 就绪";
        public string GitHubReleasesUrl => UpdateService.ReleasesUrl;

        public string PinnedCountText => (Accounts.Count(a => a.IsPinned) + GameList.Count(g => g.IsPinned)).ToString();
        public string CurrentAccountName =>
            _accountManager.CurrentAccount?.PersonaName ??
            _accountManager.CurrentAccount?.AccountName ??
            "未检测";
        public AccountViewModel? CurrentAccountViewModel => Accounts.FirstOrDefault(a => a.IsCurrent);
        public string SwitchStrategyText => SilentCloseSteam ? "强制结束进程树" : "等待 Steam 正常退出";
        public string LaunchBehaviorText => AutoStartSteam ? "切换后自动启动 Steam" : "仅更新登录账号";
        public int BoundGameCount => GameList.Count(g => g.HasBinding);
        public int UnboundGameCount => Math.Max(0, GameList.Count - BoundGameCount);
        public double GameBindingCoverage => GameList.Count == 0 ? 0 : BoundGameCount * 100.0 / GameList.Count;
        public string GameBindingCoverageText => GameList.Count == 0 ? "等待扫描游戏" : $"{BoundGameCount}/{GameList.Count} 款已配置";
        public string InjectorStatusDescription => IsInjectorConnected
            ? "Steam 库入口已连接"
            : EnableLibraryInjection ? "等待 Steam 库连接" : "功能尚未启用";
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
                : "未绑定账号";

        public string SelectedGameBindingTime => SelectedGame?.BindingLastPlayed ?? "暂无启动记录";
        public string SettingsDirectory => SettingsService.SettingsDirectory;
        public string LogDirectory => AppLogger.LogDirectory;
        public string SettingsPath => SettingsService.SettingsPath;
        public string GameBindingsPath => _gameBinding.ConfigPath;
        private const string PanelGamePrefix = "game:";
        private const string PanelQuickLaunchPrefix = "quick:";

        public static string CreatePanelGameKey(int appId) => $"{PanelGamePrefix}{appId}";
        public static string CreatePanelQuickLaunchKey(string id) => $"{PanelQuickLaunchPrefix}{id}";

        public List<SteamAccount> GetPinnedAccounts()
        {
            var accountsById = Accounts
                .Where(a => a.IsPinned)
                .Select(a => a.Account)
                .ToDictionary(a => a.SteamId);

            var ordered = new List<SteamAccount>();
            foreach (var steamId in _settings.PinnedAccountIds)
            {
                if (accountsById.TryGetValue(steamId, out var account))
                    ordered.Add(account);
            }

            return ordered;
        }

        public List<GameListViewModel> GetPinnedGames()
        {
            var gamesById = GameList
                .Where(g => g.IsPinned)
                .ToDictionary(g => g.AppId);

            var ordered = new List<GameListViewModel>();
            foreach (var appId in _settings.PinnedGameIds)
            {
                if (gamesById.TryGetValue(appId, out var game))
                    ordered.Add(game);
            }

            return ordered;
        }

        public List<object> GetPinnedPanelItems()
        {
            var games = GetPinnedGames();
            var quickLaunchItems = GetPinnedQuickLaunchItems();
            var itemsByKey = new Dictionary<string, object>();
            var availableKeys = new List<string>();

            foreach (var game in games)
            {
                var key = CreatePanelGameKey(game.AppId);
                itemsByKey[key] = game;
                availableKeys.Add(key);
            }

            foreach (var item in quickLaunchItems)
            {
                var key = CreatePanelQuickLaunchKey(item.Id);
                itemsByKey[key] = item;
                availableKeys.Add(key);
            }

            return NormalizePanelItemOrder(availableKeys)
                .Where(itemsByKey.ContainsKey)
                .Select(key => itemsByKey[key])
                .ToList();
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

            OnPropertyChanged(nameof(PinnedCountText));
            PinnedGamesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleAccountPin(string steamId)
        {
            if (_settings.PinnedAccountIds.Contains(steamId))
                _settings.PinnedAccountIds.Remove(steamId);
            else
                _settings.PinnedAccountIds.Add(steamId);

            SettingsService.Save(_settings);

            var account = Accounts.FirstOrDefault(a => a.SteamId == steamId);
            if (account != null)
                account.IsPinned = _settings.PinnedAccountIds.Contains(steamId);

            OnPropertyChanged(nameof(PinnedCountText));
            PinnedAccountsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ReorderPinnedGames(int fromIndex, int toIndex)
        {
            var pinnedIds = _settings.PinnedGameIds;
            if (fromIndex < 0 || fromIndex >= pinnedIds.Count || toIndex < 0 || toIndex > pinnedIds.Count)
                return;

            var item = pinnedIds[fromIndex];
            pinnedIds.RemoveAt(fromIndex);
            if (toIndex > fromIndex) toIndex--;
            pinnedIds.Insert(toIndex, item);

            SettingsService.Save(_settings);
            PinnedGamesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetPinnedGameOrder(IEnumerable<int> orderedAppIds)
        {
            var ordered = orderedAppIds
                .Where(id => _settings.PinnedGameIds.Contains(id))
                .Distinct()
                .ToList();
            ordered.AddRange(_settings.PinnedGameIds.Where(id => !ordered.Contains(id)));

            _settings.PinnedGameIds = ordered;
            SettingsService.Save(_settings);
            PinnedGamesChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task SetPinnedPanelItemOrderAsync(IEnumerable<string> orderedKeys)
        {
            var availableKeys = GetCurrentPanelItemKeys();
            var available = availableKeys.ToHashSet();
            var ordered = orderedKeys
                .Where(available.Contains)
                .Distinct()
                .ToList();
            ordered.AddRange(availableKeys.Where(key => !ordered.Contains(key)));

            _settings.PinnedPanelItemOrder = ordered;
            _settings.PinnedGameIds = ordered
                .Select(TryParsePanelGameKey)
                .Where(appId => appId.HasValue)
                .Select(appId => appId!.Value)
                .ToList();

            SettingsService.Save(_settings);

            var quickLaunchIds = ordered
                .Select(TryParsePanelQuickLaunchKey)
                .Where(id => id != null)
                .Select(id => id!)
                .ToList();

            await SetPinnedQuickLaunchOrderAsync(quickLaunchIds);
            PinnedGamesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ReorderPinnedAccounts(int fromIndex, int toIndex)
        {
            var pinnedIds = _settings.PinnedAccountIds;
            if (fromIndex < 0 || fromIndex >= pinnedIds.Count || toIndex < 0 || toIndex > pinnedIds.Count)
                return;

            var item = pinnedIds[fromIndex];
            pinnedIds.RemoveAt(fromIndex);
            if (toIndex > fromIndex) toIndex--;
            pinnedIds.Insert(toIndex, item);

            SettingsService.Save(_settings);
            PinnedAccountsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetPinnedAccountOrder(IEnumerable<string> orderedSteamIds)
        {
            var ordered = orderedSteamIds
                .Where(id => _settings.PinnedAccountIds.Contains(id))
                .Distinct()
                .ToList();
            ordered.AddRange(_settings.PinnedAccountIds.Where(id => !ordered.Contains(id)));

            _settings.PinnedAccountIds = ordered;
            SettingsService.Save(_settings);
            PinnedAccountsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RemovePinnedGame(int index)
        {
            var pinnedIds = _settings.PinnedGameIds;
            if (index >= 0 && index < pinnedIds.Count)
            {
                var appId = pinnedIds[index];
                pinnedIds.RemoveAt(index);
                SettingsService.Save(_settings);

                var game = GameList.FirstOrDefault(g => g.AppId == appId);
                if (game != null)
                    game.IsPinned = false;

                OnPropertyChanged(nameof(PinnedCountText));
                PinnedGamesChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void RemovePinnedAccount(int index)
        {
            var pinnedIds = _settings.PinnedAccountIds;
            if (index >= 0 && index < pinnedIds.Count)
            {
                var steamId = pinnedIds[index];
                pinnedIds.RemoveAt(index);
                SettingsService.Save(_settings);

                var account = Accounts.FirstOrDefault(a => a.SteamId == steamId);
                if (account != null)
                    account.IsPinned = false;

                OnPropertyChanged(nameof(PinnedCountText));
                PinnedAccountsChanged?.Invoke(this, EventArgs.Empty);
            }
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

            var currentSteamId = _accountManager.CurrentAccount?.SteamId;
            var currentAccountName = _accountManager.CurrentAccount?.AccountName;
            var targetSteamId = account.SteamId;
            var targetAccountName = account.AccountName;

            var isCurrentAccount = AccountManager.AreSameAccount(_accountManager.CurrentAccount, account);

            AppLogger.Info($"LaunchPinnedGame: appId={appId}, current=[{currentSteamId}|{currentAccountName}], target=[{targetSteamId}|{targetAccountName}], isCurrent={isCurrentAccount}");

            if (isCurrentAccount)
            {
                StatusText = $"当前已是 {account.PersonaName}，直接启动 {game.GameName}...";
                NotificationRequested?.Invoke(this, $"当前已是 {account.PersonaName}，直接启动 {game.GameName}");
                var launched = _accountManager.LaunchGame(appId);
                if (launched)
                {
                    await _gameBinding.RecordPlayAsync(appId, account.SteamId, account.AccountName);
                    game.BindingLastPlayed = DateTime.Now.ToString("MM-dd HH:mm");
                    StatusText = $"当前已是 {account.PersonaName}，正在启动 {game.GameName}";
                }
                else
                {
                    StatusText = $"启动 {game.GameName} 失败";
                }
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                return;
            }

            IsLoading = true;
            StatusText = $"正在切换账号并启动 {game.GameName}...";

            try
            {
                var success = await _accountManager.SwitchAccountAsync(account, SilentCloseSteam);
                if (!success)
                {
                    StatusText = "切换失败，请确保 Steam 已关闭";
                    NotificationRequested?.Invoke(this, "切换失败，请确保 Steam 已关闭");
                    return;
                }

                SelectedAccount = Accounts.FirstOrDefault(a => a.SteamId == account.SteamId);
                UpdateCurrentAccount();

                var launched = _accountManager.LaunchGame(appId);
                if (launched)
                {
                    await _gameBinding.RecordPlayAsync(appId, account.SteamId, account.AccountName);
                    game.BindingLastPlayed = DateTime.Now.ToString("MM-dd HH:mm");
                    StatusText = $"已切换到 {account.PersonaName}，正在启动 {game.GameName}";
                    NotificationRequested?.Invoke(this, $"已切换到 {account.PersonaName}，正在启动 {game.GameName}");
                }
                else
                {
                    StatusText = $"已切换到 {account.PersonaName}，但启动 {game.GameName} 失败";
                    NotificationRequested?.Invoke(this, $"已切换到 {account.PersonaName}，但启动 {game.GameName} 失败");
                }
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

            _quickLaunch = new QuickLaunchService();
            _quickLaunch.ItemsChanged += OnQuickLaunchItemsChanged;

            _settings = SettingsService.Load();
            var registryStartWithWindows = SettingsService.IsStartWithWindowsEnabled();
            if (_settings.StartWithWindows != registryStartWithWindows)
            {
                _settings.StartWithWindows = registryStartWithWindows;
                SettingsService.Save(_settings);
            }

            _autoStartSteam = _settings.AutoStartSteam;
            _minimizeToTray = _settings.MinimizeToTray;
            _startWithWindows = _settings.StartWithWindows;
            _isTaskbarPinned = _settings.TaskbarPinned;
            _taskbarPosition = _settings.TaskbarPosition;
            _taskbarOffsetX = _settings.TaskbarOffsetX;
            _taskbarOffsetY = _settings.TaskbarOffsetY;
            _taskbarWindowSize = _settings.TaskbarWindowSize;
            _taskbarAvatarSize = _settings.TaskbarAvatarSize;
            _taskbarGlassEnabled = _settings.TaskbarGlassEnabled;
            _taskbarRoundedMode = _settings.TaskbarRoundedMode;
            _desktopFloatingEnabled = _settings.DesktopFloatingEnabled;
            _desktopFloatingTopmost = _settings.DesktopFloatingTopmost;
            _desktopFloatingLocked = _settings.DesktopFloatingLocked;
            _desktopFloatingOpacity = _settings.DesktopFloatingOpacity;
            _desktopFloatingAvatarSize = _settings.DesktopFloatingAvatarSize;
            _desktopFloatingGlassEnabled = _settings.DesktopFloatingGlassEnabled;
            _desktopFloatingRoundedMode = _settings.DesktopFloatingRoundedMode;
            _desktopFloatingGlassColor = _settings.DesktopFloatingGlassColor;
            _theme = _settings.Theme;
            _minimalMode = _settings.MinimalMode;
            _desktopFloatingDockEnabled = _settings.DesktopFloatingDockEnabled;
            _enableLibraryInjection = _settings.EnableLibraryInjection;
            _autoScanGamesOnStartup = _settings.AutoScanGamesOnStartup;
            _confirmBeforeGameLaunch = _settings.ConfirmBeforeGameLaunch;
            _silentCloseSteam = _settings.SilentCloseSteam;
            _checkUpdateOnStartup = _settings.CheckUpdateOnStartup;
            _autoInstallUpdates = _settings.AutoInstallUpdates;
            _showNotificationOnSteamClose = _settings.ShowNotificationOnSteamClose;
            _customSteamPath = _settings.CustomSteamPath ?? "";
            _customLoginUsersPath = _settings.CustomLoginUsersPath ?? "";
            _settingsSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _settingsSaveTimer.Tick += OnSettingsSaveTimerTick;

            PropertyChanged += OnPropertyChanged;
            Accounts.CollectionChanged += OnDashboardCollectionChanged;
            GameList.CollectionChanged += OnDashboardCollectionChanged;
            GameBindings.CollectionChanged += OnDashboardCollectionChanged;
            InitializeMinimalModeTimer();
        }

        private void OnSettingsSaveTimerTick(object? sender, EventArgs e)
        {
            _settingsSaveTimer.Stop();
            SavePendingSettings();
        }

        private void SavePendingSettings()
        {
            if (!_settingsDirty)
                return;

            _settingsDirty = false;
            SettingsService.Save(_settings);
        }

        private void OnDashboardCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            NotifyDashboardMetrics();
            if (ReferenceEquals(sender, Accounts))
            {
                OnPropertyChanged(nameof(CurrentAccountViewModel));
                OnPropertyChanged(nameof(CurrentAccountName));
            }
        }

        private void NotifyDashboardMetrics()
        {
            OnPropertyChanged(nameof(AccountCountText));
            OnPropertyChanged(nameof(BindingCountText));
            OnPropertyChanged(nameof(GameCountText));
            OnPropertyChanged(nameof(PinnedCountText));
            OnPropertyChanged(nameof(BoundGameCount));
            OnPropertyChanged(nameof(UnboundGameCount));
            OnPropertyChanged(nameof(GameBindingCoverage));
            OnPropertyChanged(nameof(GameBindingCoverageText));
        }

        private void OnGameBindingsChanged(object? sender, EventArgs e)
        {
            DispatchToUi(UpdateGameBindingsList);
        }

        private void OnQuickLaunchItemsChanged(object? sender, EventArgs e)
        {
            DispatchToUi(UpdateQuickLaunchList);
        }

        private static void DispatchToUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                _ = dispatcher.BeginInvoke(action);
        }

        private void UpdateQuickLaunchList()
        {
            QuickLaunchList.Clear();
            foreach (var item in _quickLaunch.GetAll())
            {
                QuickLaunchList.Add(new QuickLaunchViewModel
                {
                    Id = item.Id,
                    Name = item.Name,
                    ExecutablePath = item.ExecutablePath,
                    IconPath = item.IconPath,
                    IsPinned = item.IsPinned,
                    SortOrder = item.SortOrder
                });
            }
            OnPropertyChanged(nameof(QuickLaunchCountText));
            QuickLaunchChanged?.Invoke(this, EventArgs.Empty);
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
            NotifyDashboardMetrics();
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
                    OnPropertyChanged(nameof(GameCountText));
                    OnPropertyChanged(nameof(PinnedCountText));
                });
            });

            if (_settings.PinnedGameIds.Count > 0)
                PinnedGamesChanged?.Invoke(this, EventArgs.Empty);

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
                existing.BindingLastPlayed = _gameBinding.GetBinding(appId)?.LastPlayed?.ToString("MM-dd HH:mm") ?? "从未";
                existing.GameName = gameName;
            }
            else
            {
                GameList.Add(new GameListViewModel(appId, gameName)
                {
                    HasBinding = true,
                    BindingAccountName = accountName,
                    BindingAccountSteamId = steamId,
                    BindingLastPlayed = "从未"
                });
                OnPropertyChanged(nameof(ShowEmptyHint));
                OnPropertyChanged(nameof(GameCountText));
            }

            OnPropertyChanged(nameof(SelectedGameHasBinding));
            OnPropertyChanged(nameof(SelectedGameBindingAccount));
            OnPropertyChanged(nameof(SelectedGameBindingTime));
            OnPropertyChanged(nameof(BindingCountText));
            NotifyDashboardMetrics();
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
            OnPropertyChanged(nameof(BindingCountText));
            NotifyDashboardMetrics();
            StatusText = "已删除绑定";
        }

        [RelayCommand]
        private void OpenDataFolder()
        {
            OpenFolder(SettingsService.SettingsDirectory, "数据目录");
        }

        [RelayCommand]
        private void OpenLogFolder()
        {
            OpenFolder(AppLogger.LogDirectory, "日志目录");
        }

        private void OpenFolder(string path, string name)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
                StatusText = $"已打开{name}";
            }
            catch (Exception ex)
            {
                AppLogger.Error($"OpenFolder failed: {path}", ex);
                StatusText = $"无法打开{name}: {ex.Message}";
            }
        }

        [RelayCommand]
        private void CopyDiagnostics()
        {
            try
            {
                var steamService = _accountManager.GetSteamService();
                var sb = new StringBuilder();
                sb.AppendLine("Steam Switch 诊断信息");
                sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Steam 路径: {steamService.SteamPath ?? "未检测"}");
                sb.AppendLine($"Steam 运行状态: {SteamStateText}");
                sb.AppendLine($"当前账号: {CurrentAccountName}");
                sb.AppendLine($"账号数量: {Accounts.Count}");
                sb.AppendLine($"游戏数量: {GameList.Count}");
                sb.AppendLine($"绑定数量: {GameBindings.Count}");
                sb.AppendLine($"固定入口: {PinnedCountText}");
                sb.AppendLine($"任务栏常驻: {(IsTaskbarPinned ? "开启" : "关闭")}");
                sb.AppendLine($"桌面悬浮窗: {(DesktopFloatingEnabled ? "开启" : "关闭")}");
                sb.AppendLine($"库界面注入: {(EnableLibraryInjection ? "开启" : "关闭")} / {InjectorStateText}");
                sb.AppendLine($"主题: {Theme}");
                sb.AppendLine($"数据目录: {SettingsService.SettingsDirectory}");
                sb.AppendLine($"日志目录: {AppLogger.LogDirectory}");

                Clipboard.SetText(sb.ToString());
                StatusText = "已复制诊断信息";
            }
            catch (Exception ex)
            {
                AppLogger.Error("CopyDiagnostics failed.", ex);
                StatusText = $"复制诊断信息失败: {ex.Message}";
            }
        }

        private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var shouldSaveSettings = true;
            switch (e.PropertyName)
            {
                case nameof(AutoStartSteam):
                    _settings.AutoStartSteam = AutoStartSteam;
                    break;
                case nameof(MinimizeToTray):
                    _settings.MinimizeToTray = MinimizeToTray;
                    break;
                case nameof(StartWithWindows):
                    if (_restoringStartWithWindows)
                    {
                        shouldSaveSettings = false;
                        break;
                    }

                    var requestedStartWithWindows = StartWithWindows;
                    var registrationSucceeded = SettingsService.SetStartWithWindows(requestedStartWithWindows);
                    var actualStartWithWindows = SettingsService.IsStartWithWindowsEnabled();
                    _settings.StartWithWindows = actualStartWithWindows;
                    if (!registrationSucceeded || actualStartWithWindows != requestedStartWithWindows)
                    {
                        _restoringStartWithWindows = true;
                        StartWithWindows = actualStartWithWindows;
                        _restoringStartWithWindows = false;
                        StatusText = requestedStartWithWindows
                            ? "开机自启设置失败，请检查当前用户注册表权限"
                            : "关闭开机自启失败，请检查当前用户注册表权限";
                    }
                    break;
                case nameof(IsTaskbarPinned):
                    _settings.TaskbarPinned = IsTaskbarPinned;
                    break;
                case nameof(TaskbarPosition):
                    _settings.TaskbarPosition = TaskbarPosition;
                    break;
                case nameof(TaskbarOffsetX):
                    _settings.TaskbarOffsetX = TaskbarOffsetX;
                    break;
                case nameof(TaskbarOffsetY):
                    _settings.TaskbarOffsetY = TaskbarOffsetY;
                    break;
                case nameof(TaskbarWindowSize):
                    _settings.TaskbarWindowSize = TaskbarWindowSize;
                    break;
                case nameof(TaskbarAvatarSize):
                    _settings.TaskbarAvatarSize = TaskbarAvatarSize;
                    break;
                case nameof(TaskbarGlassEnabled):
                    _settings.TaskbarGlassEnabled = TaskbarGlassEnabled;
                    break;
                case nameof(TaskbarRoundedMode):
                    _settings.TaskbarRoundedMode = TaskbarRoundedMode;
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
                    _settings.DesktopFloatingOpacity = Math.Clamp(DesktopFloatingOpacity, 20, 100);
                    break;
                case nameof(DesktopFloatingAvatarSize):
                    _settings.DesktopFloatingAvatarSize = DesktopFloatingAvatarSize;
                    break;
                case nameof(DesktopFloatingGlassEnabled):
                    _settings.DesktopFloatingGlassEnabled = DesktopFloatingGlassEnabled;
                    break;
                case nameof(DesktopFloatingRoundedMode):
                    _settings.DesktopFloatingRoundedMode = DesktopFloatingRoundedMode;
                    break;
                case nameof(DesktopFloatingGlassColor):
                    _settings.DesktopFloatingGlassColor = DesktopFloatingGlassColor;
                    break;
                case nameof(Theme):
                    _settings.Theme = Theme;
                    ThemeManager.ApplyTheme(Theme);
                    OnPropertyChanged(nameof(IsDarkTheme));
                    break;
                case nameof(EnableLibraryInjection):
                    _settings.EnableLibraryInjection = EnableLibraryInjection;
                    if (EnableLibraryInjection)
                    {
                        _ = StartInjectorAsync().ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                AppLogger.Error("StartInjectorAsync failed", t.Exception);
                            }
                            if (t.IsFaulted || !IsInjectorConnected)
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    EnableLibraryInjection = false;
                                });
                            }
                        }, TaskContinuationOptions.NotOnCanceled);
                    }
                    else
                        StopInjector();
                    break;
                case nameof(AutoScanGamesOnStartup):
                    _settings.AutoScanGamesOnStartup = AutoScanGamesOnStartup;
                    break;
                case nameof(ConfirmBeforeGameLaunch):
                    _settings.ConfirmBeforeGameLaunch = ConfirmBeforeGameLaunch;
                    break;
                case nameof(SilentCloseSteam):
                    _settings.SilentCloseSteam = SilentCloseSteam;
                    break;
                case nameof(CheckUpdateOnStartup):
                    _settings.CheckUpdateOnStartup = CheckUpdateOnStartup;
                    break;
                case nameof(AutoInstallUpdates):
                    _settings.AutoInstallUpdates = AutoInstallUpdates;
                    break;
                case nameof(ShowNotificationOnSteamClose):
                    _settings.ShowNotificationOnSteamClose = ShowNotificationOnSteamClose;
                    break;
                case nameof(CustomSteamPath):
                    _settings.CustomSteamPath = CustomSteamPath;
                    _accountManager.GetSteamService().SetCustomPaths(CustomSteamPath, CustomLoginUsersPath);
                    _ = _accountManager.LoadAccountsAsync();
                    break;
                case nameof(CustomLoginUsersPath):
                    _settings.CustomLoginUsersPath = CustomLoginUsersPath;
                    _accountManager.GetSteamService().SetCustomPaths(CustomSteamPath, CustomLoginUsersPath);
                    _ = _accountManager.LoadAccountsAsync();
                    break;
                case nameof(MinimalMode):
                    _settings.MinimalMode = MinimalMode;
                    InitializeMinimalModeTimer();
                    MinimalModeChanged?.Invoke(this, MinimalMode);
                    break;
                case nameof(DesktopFloatingDockEnabled):
                    _settings.DesktopFloatingDockEnabled = DesktopFloatingDockEnabled;
                    DesktopFloatingDockEnabledChanged?.Invoke(this, DesktopFloatingDockEnabled);
                    break;
                default:
                    shouldSaveSettings = false;
                    break;
            }

            if (!shouldSaveSettings)
                return;

            _settingsDirty = true;
            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Start();
        }

        private void OnAccountsChanged(object? sender, EventArgs e)
        {
            DispatchToUi(UpdateAccountsList);
        }

        public async Task InitializeAsync()
        {
            IsLoading = true;
            StatusText = "正在检测 Steam...";

            try
            {
                _accountManager.GetSteamService().SetCustomPaths(CustomSteamPath, CustomLoginUsersPath);
                var success = await _accountManager.InitializeAsync();
                if (!success)
                {
                    StatusText = "未找到 Steam 安装路径";
                    return;
                }

                StatusText = $"已检测到 {_accountManager.Accounts.Count} 个账号";
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();

                UpdateAccountsList();

                await _gameBinding.LoadAsync();
                await _quickLaunch.LoadAsync();
                UpdateQuickLaunchList();
                UpdateGameBindingsList();

                if (AutoScanGamesOnStartup)
                    await ScanGamesAsync();

                if (EnableLibraryInjection)
                {
                    AppLogger.Info("Library injection is enabled; auto-starting injector after initialization.");
                    _ = StartInjectorAsync();
                }

                if (CheckUpdateOnStartup)
                    _ = CheckForUpdatesAsync(isManual: false);
            }
            catch (Exception ex)
            {
                AppLogger.Error("MainViewModel initialization failed.", ex);
                StatusText = $"初始化失败: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task AddQuickLaunchItemAsync(string launchPath)
        {
            if (string.IsNullOrEmpty(launchPath) || !System.IO.File.Exists(launchPath))
            {
                StatusText = "无效的快速启动路径";
                return;
            }
            var name = System.IO.Path.GetFileNameWithoutExtension(launchPath);
            await _quickLaunch.AddAsync(name, launchPath);
            StatusText = $"已添加快速启动: {name}";
            NotificationRequested?.Invoke(this, $"已添加: {name}");
        }

        public async Task RemoveQuickLaunchItemAsync(string id)
        {
            await _quickLaunch.RemoveAsync(id);
            StatusText = "已移除快速启动项目";
        }

        public async Task ToggleQuickLaunchPinAsync(string id)
        {
            await _quickLaunch.TogglePinAsync(id);
            QuickLaunchChanged?.Invoke(this, EventArgs.Empty);
        }

        public void LaunchQuickLaunchItem(string id)
        {
            var item = _quickLaunch.GetById(id);
            if (item != null)
            {
                var launched = _quickLaunch.Launch(item);
                StatusText = launched ? $"正在启动 {item.Name}" : $"启动 {item.Name} 失败";
            }
        }

        public List<QuickLaunchItem> GetPinnedQuickLaunchItems()
        {
            return _quickLaunch.GetPinned();
        }

        public async Task ReorderQuickLaunchItemAsync(int fromIndex, int toIndex)
        {
            var items = _quickLaunch.GetAll();
            if (fromIndex < 0 || fromIndex >= items.Count || toIndex < 0 || toIndex > items.Count)
                return;

            var item = items[fromIndex];
            items.RemoveAt(fromIndex);
            if (toIndex > fromIndex) toIndex--;
            items.Insert(toIndex, item);

            var orderedIds = items.Select(x => x.Id).ToList();
            await _quickLaunch.ReorderAsync(orderedIds);
        }

        public async Task ReorderPinnedQuickLaunchItemAsync(int fromIndex, int toIndex)
        {
            var allItems = _quickLaunch.GetAll();
            var pinnedItems = allItems.Where(x => x.IsPinned).ToList();
            if (fromIndex < 0 || fromIndex >= pinnedItems.Count || toIndex < 0 || toIndex > pinnedItems.Count)
                return;

            var item = pinnedItems[fromIndex];
            pinnedItems.RemoveAt(fromIndex);
            if (toIndex > fromIndex) toIndex--;
            pinnedItems.Insert(toIndex, item);

            int pinnedIndex = 0;
            var orderedIds = allItems.Select(x =>
                x.IsPinned ? pinnedItems[pinnedIndex++].Id : x.Id).ToList();
            await _quickLaunch.ReorderAsync(orderedIds);
        }

        public async Task SetPinnedQuickLaunchOrderAsync(IEnumerable<string> orderedPinnedIds)
        {
            var allItems = _quickLaunch.GetAll();
            var pinnedIds = allItems.Where(x => x.IsPinned).Select(x => x.Id).ToList();
            var ordered = orderedPinnedIds
                .Where(id => pinnedIds.Contains(id))
                .Distinct()
                .ToList();
            ordered.AddRange(pinnedIds.Where(id => !ordered.Contains(id)));

            int pinnedIndex = 0;
            var orderedIds = allItems.Select(x =>
                x.IsPinned ? ordered[pinnedIndex++] : x.Id).ToList();
            await _quickLaunch.ReorderAsync(orderedIds);
        }

        private List<string> GetCurrentPanelItemKeys()
        {
            var keys = new List<string>();
            keys.AddRange(_settings.PinnedGameIds.Select(CreatePanelGameKey));
            keys.AddRange(GetPinnedQuickLaunchItems().Select(item => CreatePanelQuickLaunchKey(item.Id)));
            return keys;
        }

        private List<string> NormalizePanelItemOrder(List<string> availableKeys)
        {
            var available = availableKeys.ToHashSet();
            var ordered = _settings.PinnedPanelItemOrder
                .Where(available.Contains)
                .Distinct()
                .ToList();
            ordered.AddRange(availableKeys.Where(key => !ordered.Contains(key)));
            return ordered;
        }

        private static int? TryParsePanelGameKey(string key)
        {
            return key.StartsWith(PanelGamePrefix, StringComparison.Ordinal) &&
                   int.TryParse(key[PanelGamePrefix.Length..], out var appId)
                ? appId
                : null;
        }

        private static string? TryParsePanelQuickLaunchKey(string key)
        {
            return key.StartsWith(PanelQuickLaunchPrefix, StringComparison.Ordinal)
                ? key[PanelQuickLaunchPrefix.Length..]
                : null;
        }

        public async Task DeleteQuickLaunchItemAsync(int index)
        {
            var items = _quickLaunch.GetAll();
            if (index >= 0 && index < items.Count)
            {
                await _quickLaunch.RemoveAsync(items[index].Id);
                StatusText = $"已删除: {items[index].Name}";
            }
        }

        public async Task DeletePinnedQuickLaunchItemAsync(int index)
        {
            var items = _quickLaunch.GetPinned();
            if (index >= 0 && index < items.Count)
            {
                await _quickLaunch.RemoveAsync(items[index].Id);
                StatusText = $"已删除: {items[index].Name}";
            }
        }

        [RelayCommand]
        private async Task CheckForUpdatesManually()
        {
            await CheckForUpdatesAsync(isManual: true);
        }

        [RelayCommand]
        private void OpenGitHubReleases()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = GitHubReleasesUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error("Open GitHub releases failed.", ex);
                StatusText = $"无法打开 GitHub Releases: {ex.Message}";
            }
        }

        private async Task CheckForUpdatesAsync(bool isManual)
        {
            try
            {
                if (isManual)
                    StatusText = "正在检查 GitHub 更新...";

                using var updateService = new UpdateService();
                var updateInfo = await updateService.CheckForUpdateAsync();
                if (updateInfo != null)
                {
                    StatusText = $"发现新版本 v{updateInfo.Version}";
                    AppLogger.Info($"New version available: v{updateInfo.Version}");

                    if (AutoInstallUpdates)
                    {
                        await DownloadAndInstallUpdateAsync(updateService, updateInfo);
                        return;
                    }

                    UpdateAvailable?.Invoke(this, updateInfo);
                }
                else if (isManual)
                {
                    StatusText = $"当前已是最新版本 v{UpdateService.GetCurrentVersion()}";
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Check for updates failed.", ex);
                if (isManual)
                    StatusText = $"检查更新失败: {ex.Message}";
            }
        }

        private async Task DownloadAndInstallUpdateAsync(UpdateService updateService, UpdateInfo updateInfo)
        {
            if (string.IsNullOrEmpty(updateInfo.DownloadUrl))
            {
                StatusText = "发现新版本，但没有可用的安装包下载链接";
                UpdateAvailable?.Invoke(this, updateInfo);
                return;
            }

            if (!updateInfo.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "发现新版本，但自动更新需要 setup 安装包";
                UpdateAvailable?.Invoke(this, updateInfo);
                return;
            }

            try
            {
                StatusText = $"正在下载 v{updateInfo.Version} 更新...";
                var filePath = await updateService.DownloadUpdateToFileAsync(updateInfo);
                StatusText = "更新下载完成，正在启动安装器...";
                UpdateService.LaunchInstaller(filePath);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Auto update failed.", ex);
                StatusText = $"自动更新失败: {ex.Message}";
                UpdateAvailable?.Invoke(this, updateInfo);
            }
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                using var updateService = new UpdateService();
                var updateInfo = await updateService.CheckForUpdateAsync();
                if (updateInfo != null)
                {
                    StatusText = $"发现新版本 v{updateInfo.Version}";
                    AppLogger.Info($"New version available: v{updateInfo.Version}");
                    UpdateAvailable?.Invoke(this, updateInfo);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Check for updates failed.", ex);
            }
        }

        private void UpdateAccountsList()
        {
            var pinnedIds = _settings.PinnedAccountIds ?? new List<string>();

            // Unsubscribe from old AccountViewModel events
            foreach (var oldVm in Accounts)
            {
                oldVm.PropertyChanged -= OnAccountPropertyChanged;
            }

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
                SelectedAccount = Accounts.FirstOrDefault(a =>
                    AccountManager.AreSameAccount(a.Account, _accountManager.CurrentAccount));
            }

            UpdateCurrentAccount();
            OnPropertyChanged(nameof(AccountCountText));
            OnPropertyChanged(nameof(PinnedCountText));
        }

        partial void OnIsSteamRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(SteamStateText));
        }

        partial void OnIsInjectorConnectedChanged(bool value)
        {
            OnPropertyChanged(nameof(InjectorStateText));
            OnPropertyChanged(nameof(InjectorStatusDescription));
        }

        partial void OnSilentCloseSteamChanged(bool value) => OnPropertyChanged(nameof(SwitchStrategyText));

        partial void OnAutoStartSteamChanged(bool value) => OnPropertyChanged(nameof(LaunchBehaviorText));

        partial void OnEnableLibraryInjectionChanged(bool value) => OnPropertyChanged(nameof(InjectorStatusDescription));

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
                OnPropertyChanged(nameof(PinnedCountText));
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

            var currentSteamId = _accountManager.CurrentAccount?.SteamId;
            var currentAccountName = _accountManager.CurrentAccount?.AccountName;
            var selectedSteamId = SelectedAccount.Account.SteamId;
            var selectedAccountName = SelectedAccount.Account.AccountName;

            AppLogger.Info($"SwitchAccount: current=[{currentSteamId}|{currentAccountName}], selected=[{selectedSteamId}|{selectedAccountName}]");

            var isCurrentAccount = AccountManager.AreSameAccount(
                _accountManager.CurrentAccount,
                SelectedAccount.Account);

            AppLogger.Info($"SwitchAccount: isCurrent={isCurrentAccount}");

            if (isCurrentAccount)
            {
                StatusText = $"{SelectedAccount.DisplayName} 已是当前账号";
                NotificationRequested?.Invoke(this, $"{SelectedAccount.DisplayName} 已是当前账号");
                if (launchSteam)
                {
                    var launched = _accountManager.LaunchSteam();
                    StatusText = launched
                        ? $"当前已是 {SelectedAccount.DisplayName}，已启动 Steam"
                        : $"当前已是 {SelectedAccount.DisplayName}，但 Steam 启动失败";
                }
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                return;
            }

            IsLoading = true;
            StatusText = "正在切换账号...";

            try
            {
                var success = await _accountManager.SwitchAccountAsync(SelectedAccount.Account, SilentCloseSteam);
                if (success)
                {
                    StatusText = $"已切换到 {SelectedAccount.Account.PersonaName}";
                    NotificationRequested?.Invoke(this, $"已切换到 {SelectedAccount.Account.PersonaName}");
                    UpdateCurrentAccount();

                    if (launchSteam)
                    {
                        var launched = _accountManager.LaunchSteam();
                        StatusText = launched
                            ? $"已切换到 {SelectedAccount.Account.PersonaName}，Steam 已启动"
                            : $"已切换到 {SelectedAccount.Account.PersonaName}，但 Steam 启动失败";
                    }
                }
                else
                {
                    StatusText = "切换失败，请确保 Steam 已关闭";
                    NotificationRequested?.Invoke(this, "切换失败，请确保 Steam 已关闭");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("SwitchAccountAsync failed.", ex);
                StatusText = $"切换失败: {ex.Message}";
            }

            finally
            {
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                IsLoading = false;
                if (MinimalMode)
                    Core.MemoryLimiter.CleanMemory();
            }
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            IsLoading = true;
            StatusText = "正在刷新账号列表...";

            try
            {
                await _accountManager.LoadAccountsAsync();
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                UpdateAccountsList();
                StatusText = $"已刷新，共 {_accountManager.Accounts.Count} 个账号";
            }
            catch (Exception ex)
            {
                AppLogger.Error("Refresh accounts failed.", ex);
                StatusText = $"刷新账号失败: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private void LaunchSteam()
        {
            var launched = _accountManager.LaunchSteam();
            IsSteamRunning = launched || _accountManager.GetSteamService().IsSteamRunning();
            StatusText = launched ? "Steam 已启动" : "Steam 启动失败，请检查安装路径";
        }

        [RelayCommand]
        private async Task CloseSteamAsync()
        {
            StatusText = "正在关闭 Steam...";
            IsLoading = true;

            try
            {
                var closed = await _accountManager.GetSteamService().CloseSteamAsync();
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                StatusText = closed && !IsSteamRunning
                    ? "Steam 已关闭"
                    : "Steam 未能完全退出，可启用强制关闭 Steam 后重试";
            }
            catch (Exception ex)
            {
                AppLogger.Error("CloseSteamAsync command failed.", ex);
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                StatusText = $"关闭 Steam 失败: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private async Task StartInjectorAsync()
        {
            if (_injector != null && _injector.IsConnected)
            {
                StatusText = $"注入器已连接，日志: {AppLogger.CurrentLogPath}";
                return;
            }

            bool confirmed = false;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var warningResult = MessageBox.Show(
                    Application.Current.MainWindow,
                    "开启 Steam 库界面注入将启用 CEF 调试端口并向 Steam 客户端注入脚本。\n\n" +
                    "风险提示：\n" +
                    "1. 可能违反 Steam 服务条款，存在账号封禁风险\n" +
                    "2. 不建议在 VAC、竞技或反作弊敏感游戏中使用\n" +
                    "3. 可能导致 Steam 客户端不稳定\n\n" +
                    "确定要继续吗？",
                    "风险警告",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                confirmed = warningResult == MessageBoxResult.Yes;
            });

            if (!confirmed)
                return;

            IsLoading = true;
            StatusText = "正在启动注入器...";
            AppLogger.Info("StartInjectorAsync requested.");

            try
            {
                CleanupInjectorResources();
                _wsServer = new WebSocketServer(8081);
                _wsServer.MessageReceived += OnWebSocketMessage;
                _wsServer.ClientConnected += (s, id) => AppLogger.Info($"Library WebSocket client connected: {id}");
                _wsServer.ClientDisconnected += (s, id) => AppLogger.Info($"Library WebSocket client disconnected: {id}");
                await _wsServer.StartAsync();
                AppLogger.Info("Local WebSocket server started on port 8081.");

                var debugReady = await EnsureSteamDebuggingAsync();
                if (!debugReady)
                {
                    CleanupInjectorResources();
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
                if (!connected)
                    CleanupInjectorResources();
                StatusText = connected
                    ? $"注入器已启动，日志: {AppLogger.CurrentLogPath}"
                    : $"注入失败，日志: {AppLogger.CurrentLogPath}";
            }
            catch (Exception ex)
            {
                CleanupInjectorResources();
                IsInjectorConnected = false;
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

            var debugPort = FindAvailablePort(SteamDebugPort);
            AppLogger.Info($"Using debug port: {debugPort}");

            StatusText = "正在以调试模式启动 Steam...";
            var started = steamService.StartSteamWithDebugging(debugPort: debugPort);
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

        private static bool IsPortAvailable(int port)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int FindAvailablePort(int startPort)
        {
            for (var port = startPort; port < startPort + 100; port++)
            {
                if (IsPortAvailable(port))
                    return port;
            }
            return startPort;
        }

        [RelayCommand]
        private void StopInjector()
        {
            AppLogger.Info("StopInjector requested.");
            CleanupInjectorResources();
            IsInjectorConnected = false;
            StatusText = "注入器已停止";
        }

        private void CleanupInjectorResources()
        {
            try
            {
                _injector?.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to dispose Steam CEF injector: {ex.Message}");
            }
            _injector = null;

            try
            {
                _wsServer?.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to dispose library WebSocket server: {ex.Message}");
            }
            _wsServer = null;
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

            var currentSteamId = _accountManager.CurrentAccount?.SteamId;
            var currentAccountName = _accountManager.CurrentAccount?.AccountName;
            var targetSteamId = account.SteamId;
            var targetAccountName = account.AccountName;

            var isCurrentAccount = AccountManager.AreSameAccount(_accountManager.CurrentAccount, account);

            AppLogger.Info($"SwitchAndLaunch: appId={appId}, current=[{currentSteamId}|{currentAccountName}], target=[{targetSteamId}|{targetAccountName}], isCurrent={isCurrentAccount}");

            if (isCurrentAccount)
            {
                StatusText = $"当前已是 {account.PersonaName}，直接启动游戏...";
                NotificationRequested?.Invoke(this, $"当前已是 {account.PersonaName}，直接启动游戏");
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
                    ? $"当前已是 {account.PersonaName}，正在启动游戏"
                    : "启动游戏失败";
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                return;
            }

            IsLoading = true;
            StatusText = "正在切换账号并启动游戏...";

            try
            {
                var success = await _accountManager.SwitchAccountAsync(account, SilentCloseSteam);
                if (!success)
                {
                    StatusText = "切换失败，请确保 Steam 已关闭";
                    NotificationRequested?.Invoke(this, "切换失败，请确保 Steam 已关闭");
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
                if (launched)
                {
                    StatusText = $"已切换到 {account.PersonaName}，正在启动游戏";
                    NotificationRequested?.Invoke(this, $"已切换到 {account.PersonaName}，正在启动游戏");
                }
                else
                {
                    StatusText = $"已切换到 {account.PersonaName}，但启动游戏失败";
                    NotificationRequested?.Invoke(this, $"已切换到 {account.PersonaName}，但启动游戏失败");
                }
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
                if (MinimalMode)
                {
                    Core.MemoryLimiter.CleanMemory();
                }
            }
        }

        private void UpdateCurrentAccount()
        {
            foreach (var acc in Accounts)
            {
                acc.IsCurrent = AccountManager.AreSameAccount(acc.Account, _accountManager.CurrentAccount);
            }

            OnPropertyChanged(nameof(CurrentAccountName));
            OnPropertyChanged(nameof(CurrentAccountViewModel));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Tick -= OnSettingsSaveTimerTick;
            SavePendingSettings();

            if (_minimalModeTimer != null)
            {
                _minimalModeTimer.Stop();
                _minimalModeTimer.Tick -= OnMinimalModeTimerTick;
            }

            PropertyChanged -= OnPropertyChanged;
            Accounts.CollectionChanged -= OnDashboardCollectionChanged;
            GameList.CollectionChanged -= OnDashboardCollectionChanged;
            GameBindings.CollectionChanged -= OnDashboardCollectionChanged;
            _accountManager.AccountsChanged -= OnAccountsChanged;
            _gameBinding.BindingsChanged -= OnGameBindingsChanged;
            _quickLaunch.ItemsChanged -= OnQuickLaunchItemsChanged;
            foreach (var account in Accounts)
                account.PropertyChanged -= OnAccountPropertyChanged;

            CleanupInjectorResources();
            _quickLaunch.Dispose();
            _accountManager.Dispose();
            GC.SuppressFinalize(this);
        }

        private DispatcherTimer? _minimalModeTimer;

        private void InitializeMinimalModeTimer()
        {
            if (_minimalModeTimer == null)
            {
                _minimalModeTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(30)
                };
                _minimalModeTimer.Tick += OnMinimalModeTimerTick;
            }

            if (MinimalMode)
            {
                _minimalModeTimer.Start();
                Core.MemoryLimiter.CleanMemory();
            }
            else
            {
                _minimalModeTimer.Stop();
            }
        }

        private void OnMinimalModeTimerTick(object? sender, EventArgs e)
        {
            if (MinimalMode)
                Core.MemoryLimiter.CleanMemory();
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
        public string LastLoginText
        {
            get
            {
                if (Account.Timestamp <= 0) return "登录时间未知";
                var elapsed = DateTime.Now - Account.LastLoginTime;
                if (elapsed.TotalMinutes < 2) return "刚刚登录";
                if (elapsed.TotalHours < 1) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";
                if (elapsed.TotalDays < 1) return $"{Math.Max(1, (int)elapsed.TotalHours)} 小时前";
                if (elapsed.TotalDays < 7) return $"{Math.Max(1, (int)elapsed.TotalDays)} 天前";
                return Account.LastLoginTime.ToString("yyyy-MM-dd");
            }
        }

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
            Avatar = AccountManager.LoadImage(Account.AvatarPath, 64);
        }
    }
}
