using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
        private readonly SettingsSession _settingsSession;
        private AppSettings _settings => _settingsSession.Current;
        private bool _applyingSettings;
        private readonly GameAccountBinding _gameBinding;
        private readonly QuickLaunchService _quickLaunch;
        private readonly Func<bool> _readStartupState;
        private readonly Func<bool, bool> _writeStartupState;
        private readonly bool _monitorSystemState;
        private readonly string _configurationDirectory;
        private readonly GameSaveAutoBackupService _gameSaveAutoBackupService;
        private readonly CancellationTokenSource _launchWorkflowCts = new();
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private readonly SemaphoreSlim _injectorGate = new(1, 1);
        private CancellationTokenSource? _injectorAttemptCts;
        private bool _suppressInjectorPrompt;
        // Shared client for the CEF debug-port probe. Creating a client per
        // probe would exhaust sockets when the scan runs repeatedly.
        private static readonly HttpClient DebugPortProbeClient = new()
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        private int _updateCheckInProgress;
        private int _gameScanInProgress;
        private SteamCEFInjector? _injector;
        private WebSocketServer? _wsServer;
        private readonly DispatcherTimer _settingsSaveTimer;
        private readonly DispatcherTimer _steamStatusTimer;
        private bool _restoringStartWithWindows;
        private bool _disposed;
        private AccountViewModel? _lastFailedSwitchAccount;
        private bool _lastFailedSwitchLaunchSteam;

        public bool HasFailedAccountSwitch => _lastFailedSwitchAccount != null;

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
        private bool _isGameScanInProgress;

        [ObservableProperty]
        private string _statusText = "就绪";

        [ObservableProperty]
        private string _backupExportDirectory = "";

        [ObservableProperty]
        private bool _askBeforeBackupExport;

        public string BackupExportDirectoryDisplay => string.IsNullOrWhiteSpace(BackupExportDirectory)
            ? BackupExportService.DefaultDirectory : BackupExportDirectory;

        partial void OnBackupExportDirectoryChanged(string value) => OnPropertyChanged(nameof(BackupExportDirectoryDisplay));

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
        private int _taskbarMonitorIndex;

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
        private bool _desktopAutoHideIconsEnabled;

        [ObservableProperty]
        private DesktopIconHideMode _desktopIconHideMode;

        [ObservableProperty]
        private int _desktopIconIdleSeconds = 30;

        [ObservableProperty]
        private bool _desktopRestoreByMouseMove = true;

        [ObservableProperty]
        private bool _desktopRestoreByTaskbarClick = true;

        [ObservableProperty]
        private bool _desktopToggleIconsOnClick;

        [ObservableProperty]
        private DesktopIconAnimationType _desktopIconAnimationType = DesktopIconAnimationType.Fade;

        [ObservableProperty]
        private int _desktopIconAnimationDuration = 300;

        [ObservableProperty]
        private bool _desktopImmersiveModeEnabled;

        [ObservableProperty]
        private bool _desktopIconsVisible = true;

        [ObservableProperty]
        private bool _desktopIconsHiddenByApp;

        [ObservableProperty]
        private int _desktopAutoHideCountdown;

        public string DesktopIconStatusText => DesktopIconsVisible
            ? "桌面图标显示中"
            : DesktopToggleIconsOnClick
                ? "桌面已优化 · 单击桌面恢复"
                : "桌面图标已隐藏";
        public string DesktopClickToggleStatusText => DesktopToggleIconsOnClick ? "已开启" : "未开启";
        public string DesktopAutoHideCountdownText => DesktopAutoHideIconsEnabled && DesktopIconHideMode != DesktopIconHideMode.Manual
            ? $"距离自动隐藏：{DesktopAutoHideCountdown} 秒" : "手动控制桌面图标";

        public DesktopEnhanceSettings CreateDesktopEnhanceSettings() => new()
        {
            EnableAutoHideIcons = DesktopAutoHideIconsEnabled,
            HideMode = DesktopIconHideMode,
            IdleSeconds = DesktopIconIdleSeconds,
            RestoreByMouseMove = DesktopRestoreByMouseMove,
            RestoreByTaskbarClick = DesktopRestoreByTaskbarClick,
            ToggleIconsOnDesktopClick = DesktopToggleIconsOnClick,
            AnimationType = DesktopIconAnimationType,
            AnimationDuration = DesktopIconAnimationDuration,
            EnableImmersiveMode = DesktopImmersiveModeEnabled
        };

        partial void OnDesktopIconsVisibleChanged(bool value) => OnPropertyChanged(nameof(DesktopIconStatusText));
        partial void OnDesktopAutoHideCountdownChanged(int value) => OnPropertyChanged(nameof(DesktopAutoHideCountdownText));
        partial void OnDesktopAutoHideIconsEnabledChanged(bool value) => OnPropertyChanged(nameof(DesktopAutoHideCountdownText));
        partial void OnDesktopIconHideModeChanged(DesktopIconHideMode value) => OnPropertyChanged(nameof(DesktopAutoHideCountdownText));
        partial void OnDesktopToggleIconsOnClickChanged(bool value)
        {
            OnPropertyChanged(nameof(DesktopClickToggleStatusText));
            OnPropertyChanged(nameof(DesktopIconStatusText));
        }

        [ObservableProperty]
        private bool _taskbarLyricsEnabled;

        [ObservableProperty]
        private bool _taskbarLyricsShowControls = true;

        [ObservableProperty]
        private bool _taskbarLyricsOnlineEnabled = true;

        [ObservableProperty]
        private bool _taskbarLyricsArtworkRounded = true;

        [ObservableProperty]
        private bool _taskbarLyricsShowVisualizer = true;

        [ObservableProperty]
        private bool _taskbarLyricsTransparentBackground = true;

        [ObservableProperty]
        private bool _taskbarLyricsAdaptiveWidth = true;

        [ObservableProperty]
        private int _taskbarLyricsWidth = 280;

        [ObservableProperty]
        private bool _taskbarLyricsTrackInfoOnly;

        [ObservableProperty]
        private bool _taskbarLyricsShowSecondaryLine = true;

        /// <summary>是否显示歌词岛底部的播放进度条。</summary>
        [ObservableProperty]
        private bool _taskbarLyricsShowProgressBar = true;

        /// <summary>没有媒体播放时把歌词岛（乃至整个任务栏条）收起来，有歌再贴回。</summary>
        [ObservableProperty]
        private bool _taskbarLyricsHideWhenNoMedia = true;

        [ObservableProperty]
        private int _taskbarLyricsPollingIntervalMs = 120;

        [ObservableProperty]
        private int _taskbarLyricsVerticalOffset;

        [ObservableProperty]
        private bool _lyricSourceQQEnabled = true;

        [ObservableProperty]
        private bool _lyricSourceNeteaseEnabled = true;

        [ObservableProperty]
        private bool _lyricSourceKugouEnabled = true;

        [ObservableProperty]
        private bool _lyricSourceLrclibEnabled = true;

        [ObservableProperty]
        private int _lyricOffsetMs = 0;

        [ObservableProperty]
        private bool _lyricSplitLongLines = false;

        [ObservableProperty]
        private bool _taskbarLyricsMusicOnly = true;

        [ObservableProperty]
        private int _gameSaveManualRetention;

        [ObservableProperty]
        private int _gameSaveSafetyRetention;

        [ObservableProperty]
        private bool _gameSaveAutoBackupEnabled;

        [ObservableProperty]
        private bool _gameSavePeriodicBackupEnabled = true;

        [ObservableProperty]
        private int _gameSavePeriodicIntervalMinutes = 30;

        [ObservableProperty]
        private bool _smartSaveDiscoveryEnabled = true;

        [ObservableProperty]
        private bool _playSoundOnSwitch = true;

        [ObservableProperty]
        private bool _showSwitchNotification = true;

        [ObservableProperty]
        private int _taskbarLyricsFontSize = 14;

        [ObservableProperty]
        private string _taskbarLyricsFontFamily = "Segoe UI Variable Text";

        [ObservableProperty]
        private string _gameSaveAutoBackupStatusText = "已关闭";

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
        private bool _useBetaUpdateChannel;

        [ObservableProperty]
        private bool _checkUpdateOnStartup = true;

        [ObservableProperty]
        private bool _autoInstallUpdates;

        [ObservableProperty]
        private bool _showNotificationOnSteamClose;

        public AccountManager GetAccountManager() => _accountManager;
        public AppSettings GetSettings() => _settings;
        public GameAccountBinding GetGameBinding() => _gameBinding;
        public GameBinding? GetGameBinding(int appId) => _gameBinding.GetBinding(appId);

        public Task SaveGameLaunchWorkflowAsync(
            int appId,
            string launchArguments,
            bool backupBeforeLaunch,
            bool backupAfterExit,
            bool closeSteamAfterExit,
            bool startInBigPicture) =>
            _gameBinding.SetLaunchWorkflowAsync(
                appId,
                launchArguments,
                backupBeforeLaunch,
                backupAfterExit,
                closeSteamAfterExit,
                startInBigPicture);

        public Task LaunchBoundGameAsync(int appId)
        {
            var gameName = _gameBinding.GetBinding(appId)?.GameName ?? string.Empty;
            return SwitchAccountAndLaunchGameAsync(appId, gameName, null);
        }

        public event EventHandler? PinnedGamesChanged;
        public event EventHandler? PinnedAccountsChanged;
        public event EventHandler? QuickLaunchChanged;
        public event EventHandler<string>? NotificationRequested;
        public event EventHandler<UpdateInfo>? UpdateAvailable;
        public event EventHandler<bool>? MinimalModeChanged;
        public event EventHandler<bool>? DesktopFloatingDockEnabledChanged;

        public string AccountCountText => Accounts.Count.ToString();

        /// <summary>
        /// 本地账号库指标：Steam 的 loginusers.vdf 只保留有限账号，被裁掉的账号
        /// 由本地库继续保存（IsArchived）。这两个数字就是"保存多个账号"功能
        /// 在界面上的直接呈现。
        /// </summary>
        public int ArchivedAccountCount => Accounts.Count(a => a.IsArchived);
        public bool HasArchivedAccounts => ArchivedAccountCount > 0;
        public int SteamListedAccountCount => Accounts.Count(account => !account.IsArchived);
        public string AccountLibraryCountsText => $"本地收录 {Accounts.Count} 个  ·  Steam 记录 {SteamListedAccountCount} 个  ·  待找回 {ArchivedAccountCount} 个";
        public string AccountRecoveryButtonText => $"待找回 · {ArchivedAccountCount}";
        public string AccountLibraryBadgeText => HasArchivedAccounts
            ? $"{ArchivedAccountCount} 个仅本地账号 · 可找回"
            : "读取到的账号会自动收录";
        public string AccountLibrarySummaryText => HasArchivedAccounts
            ? "Steam 未列出的历史账号仍保留在本地。点击「待找回」筛选，再选择账号找回。"
            : Accounts.Count == 0 ? "先在 Steam 登录账号，再点击「收录账号」。以后读取到的账号会自动保留在本地。"
            : "读取到的账号资料会保留在本地，Steam 不再列出时，也能在这里找到。";

        public string BindingCountText => GameBindings.Count.ToString();
        public string GameCountText => GameList.Count.ToString();
        public string QuickLaunchCountText => QuickLaunchList.Count.ToString();
        public string QuickLaunchSummaryText => $"{QuickLaunchList.Count} 个快速项正在显示";
        public string CurrentVersionText => $"当前版本：v{UpdateService.GetCurrentVersion()}{(UpdateService.IsCurrentVersionPrerelease() ? " · 测试版" : "")}";
        public string CompactVersionText => $"v{UpdateService.GetCurrentVersion()}{(UpdateService.IsCurrentVersionPrerelease() ? " 测试版" : "")} · 就绪";
        public string TaskbarPinnedStatusText => IsTaskbarPinned ? "任务栏常驻已开启" : "任务栏常驻未开启";
        public string AutoUpdateStatusText => AutoInstallUpdates
            ? "自动更新已开启"
            : CheckUpdateOnStartup ? "更新检查已开启" : "自动更新未开启";
        public string GitHubReleasesUrl => UpdateService.ReleasesUrl;
        public IEnumerable<GameListViewModel> RecentGames => GameList
            .Where(game => game.HasBinding &&
                           !string.IsNullOrWhiteSpace(game.BindingLastPlayed) &&
                           !string.Equals(game.BindingLastPlayed, "从未", StringComparison.Ordinal))
            .OrderByDescending(GetRecentGameSortKey)
            .Take(2);
        public bool HasRecentGames => RecentGames.Any();

        private static DateTime GetRecentGameSortKey(GameListViewModel game)
        {
            var value = game.BindingLastPlayed?.Trim();
            if (string.IsNullOrEmpty(value))
                return DateTime.MinValue;

            var separatorIndex = value.IndexOf(' ');
            if (separatorIndex > 0 && TimeSpan.TryParse(value[(separatorIndex + 1)..], out var time))
            {
                if (value.StartsWith("今天", StringComparison.Ordinal))
                    return DateTime.Today.Add(time);
                if (value.StartsWith("昨天", StringComparison.Ordinal))
                    return DateTime.Today.AddDays(-1).Add(time);
            }

            if (DateTime.TryParseExact(
                    value,
                    "MM-dd HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsed))
            {
                return new DateTime(DateTime.Today.Year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, 0);
            }

            return DateTime.MinValue;
        }

        public string PinnedCountText => (Accounts.Count(a => a.IsPinned) + GameList.Count(g => g.IsPinned)).ToString();
        public string CurrentAccountName =>
            _accountManager.CurrentAccount?.PersonaName ??
            _accountManager.CurrentAccount?.AccountName ??
            "未检测";
        public AccountViewModel? CurrentAccountViewModel => Accounts.FirstOrDefault(a => a.IsCurrent);
        public string SwitchStrategyText => SilentCloseSteam ? "强制结束进程树" : "等待 Steam 正常退出";
        public string LaunchBehaviorText => AutoStartSteam ? "切换后自动启动 Steam" : "仅更新登录账号";
        public string UpdateProtectionText => "GitHub 官方源 · SHA-256 双重校验";
        public string LocalControlProtectionText => "随机会话令牌 · 连接与消息限流";
        public string DataProtectionText => "原子写入 · 自动保留上一版本";
        public string SecuritySummaryText => "3 项核心保护已启用";
        public int BoundGameCount => GameList.Count(g => g.HasBinding);
        public int UnboundGameCount => Math.Max(0, GameList.Count - BoundGameCount);
        public double GameBindingCoverage => GameList.Count == 0 ? 0 : BoundGameCount * 100.0 / GameList.Count;
        public string GameBindingCoverageText => GameList.Count == 0 ? "等待扫描游戏" : $"{BoundGameCount}/{GameList.Count} 款已配置";
        public string InjectorStatusDescription => IsInjectorConnected
            ? "Steam 库入口已连接"
            : EnableLibraryInjection ? "等待 Steam 库连接" : "功能尚未启用";
        public string SteamStateText => IsSteamRunning ? "运行中" : "未运行";
        public string SteamStatusText => IsSteamRunning ? "Steam 运行中" : "Steam 未运行";
        public string InjectorStateText => IsInjectorConnected ? "已连接" : "未注入";

        public bool ShowEmptyHint => GameList.Count == 0;

        public string SelectedGameName => SelectedGame?.Name ?? "未选择";
        public string SelectedGameAppId => SelectedGame?.AppId.ToString() ?? "-";

        public bool SelectedGameHasBinding =>
            SelectedGame != null && !string.IsNullOrEmpty(SelectedGame.BindingAccountName);

        public bool CanSaveGameBinding =>
            SelectedGame != null && (SelectedBindingAccount != null || SelectedAccount != null);

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

            SaveSettingsNow();

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

            SaveSettingsNow();

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

            SaveSettingsNow();
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
            SaveSettingsNow();
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

            SaveSettingsNow();

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

            SaveSettingsNow();
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
            SaveSettingsNow();
            PinnedAccountsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RemovePinnedGame(int index)
        {
            var pinnedIds = _settings.PinnedGameIds;
            if (index >= 0 && index < pinnedIds.Count)
            {
                var appId = pinnedIds[index];
                pinnedIds.RemoveAt(index);
                SaveSettingsNow();

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
                SaveSettingsNow();

                var account = Accounts.FirstOrDefault(a => a.SteamId == steamId);
                if (account != null)
                    account.IsPinned = false;

                OnPropertyChanged(nameof(PinnedCountText));
                PinnedAccountsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public async Task LaunchPinnedGameAsync(int appId)
        {
            if (!await _operationGate.WaitAsync(0))
            {
                StatusText = "已有账号或游戏操作正在进行，请稍候";
                return;
            }

            try
            {
            IsLoading = true;

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
                try
                {
                    StatusText = $"当前已是 {account.PersonaName}，直接启动 {game.GameName}...";
                    NotificationRequested?.Invoke(this, $"当前已是 {account.PersonaName}，直接启动 {game.GameName}");
                    var launched = await LaunchGameWithWorkflowAsync(appId, game.GameName, account);
                    if (launched)
                    {
                        game.BindingLastPlayed = DateTime.Now.ToString("MM-dd HH:mm");
                        StatusText = $"当前已是 {account.PersonaName}，正在启动 {game.GameName}";
                    }
                    else
                    {
                        StatusText = $"启动 {game.GameName} 失败";
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
                }
                return;
            }

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

                var launched = await LaunchGameWithWorkflowAsync(appId, game.GameName, account);
                if (launched)
                {
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
                if (!_disposed)
                {
                    IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                }
            }
            }
            finally
            {
                IsLoading = false;
                _operationGate.Release();
            }
        }

        public MainViewModel() : this(new AccountManager(), new GameAccountBinding(), new QuickLaunchService(),
            new SettingsSession(SettingsService.Load(), SettingsService.Save),
            SettingsService.IsStartWithWindowsEnabled, SettingsService.SetStartWithWindows,
            SettingsService.SettingsDirectory, monitorSystemState: true)
        {
        }

        internal MainViewModel(AccountManager accountManager, GameAccountBinding gameBinding,
            QuickLaunchService quickLaunch, SettingsSession settingsSession,
            Func<bool> readStartupState, Func<bool, bool> writeStartupState,
            string configurationDirectory, bool monitorSystemState = false)
        {
            _readStartupState = readStartupState;
            _writeStartupState = writeStartupState;
            _monitorSystemState = monitorSystemState;
            _configurationDirectory = configurationDirectory;
            _accountManager = accountManager;
            _accountManager.AccountsChanged += OnAccountsChanged;

            _gameBinding = gameBinding;
            _gameBinding.BindingsChanged += OnGameBindingsChanged;

            _quickLaunch = quickLaunch;
            _quickLaunch.ItemsChanged += OnQuickLaunchItemsChanged;

            _settingsSession = settingsSession;
            _settings.CustomGameSaveFolders ??= new List<CustomGameSaveDefinition>();
            _gameSaveAutoBackupService = new GameSaveAutoBackupService(_accountManager, () => _settings);
            _gameSaveAutoBackupService.BackupCompleted += OnGameSaveAutoBackupCompleted;
            _accountManager.BeforeAccountSwitchAsync = _gameSaveAutoBackupService.BackupBeforeAccountSwitchAsync;
            var registryStartWithWindows = _readStartupState();
            if (_settings.StartWithWindows != registryStartWithWindows)
            {
                _settings.StartWithWindows = registryStartWithWindows;
                SaveSettingsNow();
            }

            LoadSettingsFields();
            _settingsSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _settingsSaveTimer.Tick += OnSettingsSaveTimerTick;
            _steamStatusTimer = new DispatcherTimer
            {
                // Steam 常驻托盘时不会触发账号列表刷新；独立轮询保证状态徽章及时同步。
                Interval = TimeSpan.FromSeconds(2)
            };
            _steamStatusTimer.Tick += OnSteamStatusTimerTick;
            IsSteamRunning = _monitorSystemState && _accountManager.GetSteamService().IsSteamRunning();
            if (_monitorSystemState) _steamStatusTimer.Start();

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

        private void OnSteamStatusTimerTick(object? sender, EventArgs e)
        {
            if (_disposed) return;

            var running = _accountManager.GetSteamService().IsSteamRunning();
            if (IsSteamRunning != running)
                IsSteamRunning = running;

            if (_accountManager.RefreshCurrentAccountFromSteamSession())
                UpdateAccountsList();
        }

        private void SyncGameSaveAutoBackupService()
        {
            _gameSaveAutoBackupService.Enabled = _monitorSystemState &&
                (GameSaveAutoBackupEnabled || GameSavePeriodicBackupEnabled);
        }

        private string CreateGameSaveAutoBackupStatusText()
        {
            if (_settings.GameSaveLastAutoBackup.HasValue &&
                (GameSaveAutoBackupEnabled || GameSavePeriodicBackupEnabled))
                return $"最近备份：{_settings.GameSaveLastAutoBackup.Value:MM-dd HH:mm}";

            return (GameSaveAutoBackupEnabled, GameSavePeriodicBackupEnabled) switch
            {
                (true, true) => "正在监控 Steam 退出与定时备份",
                (true, false) => "正在监控 Steam 退出",
                (false, true) => $"定时备份已开启 · 每 {GameSavePeriodicIntervalMinutes} 分钟",
                _ => "已关闭"
            };
        }
        private void OnGameSaveAutoBackupCompleted(object? sender, GameSaveAutoBackupResult result)
        {
            DispatchToUi(() =>
            {
                if (_disposed) return;
                _settings.GameSaveLastAutoBackup = result.CompletedAt;
                ScheduleSettingsSave();
                GameSaveAutoBackupStatusText = result.Failed > 0
                    ? $"最近备份：{result.CompletedAt:MM-dd HH:mm} · {result.Failed} 组失败"
                    : $"最近备份：{result.CompletedAt:MM-dd HH:mm}";
                var unavailableText = result.Unavailable > 0 ? $"，{result.Unavailable} 个目录不可用" : string.Empty;
                var message = $"存档自动备份完成：新增 {result.Created}，未变化 {result.Skipped}{unavailableText}";
                if (result.Failed > 0) message += $"，失败 {result.Failed}";
                StatusText = message;
                if (result.Created > 0 || result.Failed > 0)
                    NotificationRequested?.Invoke(this, message);
            });
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
            NotifyAccountLibraryMetrics();
            OnPropertyChanged(nameof(AccountCountText));
            OnPropertyChanged(nameof(BindingCountText));
            OnPropertyChanged(nameof(GameCountText));
            OnPropertyChanged(nameof(PinnedCountText));
            OnPropertyChanged(nameof(BoundGameCount));
            OnPropertyChanged(nameof(SelectedAccountBoundGameCount));
            OnPropertyChanged(nameof(UnboundGameCount));
            OnPropertyChanged(nameof(GameBindingCoverage));
            OnPropertyChanged(nameof(GameBindingCoverageText));
            OnPropertyChanged(nameof(RecentGames));
            OnPropertyChanged(nameof(HasRecentGames));
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
            OnPropertyChanged(nameof(QuickLaunchSummaryText));
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

        public async Task ScanGamesAsync(bool manageLoadingState = true)
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _gameScanInProgress, 1, 0) != 0)
            {
                StatusText = "游戏扫描正在进行，请稍候...";
                return;
            }
            var cancellationToken = _lifetimeCts.Token;
            IsGameScanInProgress = true;
            if (manageLoadingState)
                IsLoading = true;
            StatusText = "正在扫描本地 Steam 游戏...";

            try
            {
                var scanResult = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var steamService = _accountManager.GetSteamService();
                    return (Games: steamService.GetInstalledGames(), Bindings: _gameBinding.GetAllBindings());
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                GameList.Clear();
                var pinnedIds = new HashSet<int>(_settings.PinnedGameIds);
                foreach (var game in scanResult.Games)
                {
                    var vm = new GameListViewModel(game.AppId, game.Name)
                    {
                        InstallDir = game.InstallDir,
                        IconPath = game.IconPath,
                        SizeDisplay = game.SizeDisplay,
                        IsPinned = pinnedIds.Contains(game.AppId)
                    };

                    if (scanResult.Bindings.TryGetValue(game.AppId, out var binding))
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
                if (_settings.PinnedGameIds.Count > 0)
                    PinnedGamesChanged?.Invoke(this, EventArgs.Empty);
                StatusText = GameList.Count == 0
                    ? "未找到已安装的游戏，请在 Steam 中安装游戏后重试"
                    : $"已找到 {GameList.Count} 个已安装的游戏";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AppLogger.Info("Game scan canceled during shutdown.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unable to scan installed Steam games.", ex);
                if (!_disposed)
                    StatusText = $"扫描游戏失败：{ex.Message}";
            }
            finally
            {
                if (!_disposed)
                {
                    if (manageLoadingState)
                        IsLoading = false;
                    IsGameScanInProgress = false;
                }
                Interlocked.Exchange(ref _gameScanInProgress, 0);
            }
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
            NotifyAccountLibraryMetrics();
            OnPropertyChanged(nameof(AccountCountText));
            OnPropertyChanged(nameof(PinnedCountText));
        }

        private void NotifyAccountLibraryMetrics()
        {
            OnPropertyChanged(nameof(ArchivedAccountCount));
            OnPropertyChanged(nameof(HasArchivedAccounts));
            OnPropertyChanged(nameof(SteamListedAccountCount));
            OnPropertyChanged(nameof(AccountLibraryCountsText));
            OnPropertyChanged(nameof(AccountRecoveryButtonText));
            OnPropertyChanged(nameof(AccountLibraryBadgeText));
            OnPropertyChanged(nameof(AccountLibrarySummaryText));
        }

        partial void OnIsSteamRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(SteamStateText));
            OnPropertyChanged(nameof(SteamStatusText));
            OnPropertyChanged(nameof(AccountActionHint));
        }

        partial void OnIsInjectorConnectedChanged(bool value)
        {
            OnPropertyChanged(nameof(InjectorStateText));
            OnPropertyChanged(nameof(InjectorStatusDescription));
        }

        partial void OnSilentCloseSteamChanged(bool value) => OnPropertyChanged(nameof(SwitchStrategyText));

        partial void OnAutoStartSteamChanged(bool value) => OnPropertyChanged(nameof(LaunchBehaviorText));

        partial void OnEnableLibraryInjectionChanged(bool value) => OnPropertyChanged(nameof(InjectorStatusDescription));

        partial void OnIsTaskbarPinnedChanged(bool value) => OnPropertyChanged(nameof(TaskbarPinnedStatusText));

        partial void OnAutoInstallUpdatesChanged(bool value)
        {
            // Automatic installation has no effect without an update check.
            // Keep the two settings consistent when the user enables it at runtime.
            if (value && !CheckUpdateOnStartup)
                CheckUpdateOnStartup = true;
            OnPropertyChanged(nameof(AutoUpdateStatusText));
        }

        partial void OnCheckUpdateOnStartupChanged(bool value)
        {
            // Do not allow a contradictory state where auto-install is enabled
            // but startup checks are disabled.
            if (!value && AutoInstallUpdates)
            {
                CheckUpdateOnStartup = true;
                return;
            }
            OnPropertyChanged(nameof(AutoUpdateStatusText));
        }

        partial void OnSelectedGameChanged(GameListViewModel? value)
        {
            OnPropertyChanged(nameof(SelectedGameName));
            OnPropertyChanged(nameof(SelectedGameAppId));
            OnPropertyChanged(nameof(SelectedGameHasBinding));
            OnPropertyChanged(nameof(SelectedGameBindingAccount));
            OnPropertyChanged(nameof(SelectedGameBindingTime));
            OnPropertyChanged(nameof(CanSaveGameBinding));
        }

        partial void OnSelectedBindingAccountChanged(AccountViewModel? value) =>
            OnPropertyChanged(nameof(CanSaveGameBinding));

        partial void OnSelectedAccountChanged(AccountViewModel? value)
        {
            OnPropertyChanged(nameof(CanSaveGameBinding));
            NotifyAccountPresentation();
        }

        partial void OnGameListChanged(ObservableCollection<GameListViewModel> value)
        {
            OnPropertyChanged(nameof(ShowEmptyHint));
            OnPropertyChanged(nameof(GameList));
        }

        private void OnAccountPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AccountViewModel.IsCurrent) && ReferenceEquals(sender, SelectedAccount))
                NotifyAccountPresentation();
            if (e.PropertyName == nameof(AccountViewModel.IsPinned) && sender is AccountViewModel account)
            {
                var pinnedIds = _settings.PinnedAccountIds ?? new List<string>();
                if (account.IsPinned)
                {
                    if (!pinnedIds.Contains(account.SteamId))
                        pinnedIds.Add(account.SteamId);
                }
                else
                {
                    pinnedIds.Remove(account.SteamId);
                }
                _settings.PinnedAccountIds = pinnedIds;
                SaveSettingsNow();
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

        [RelayCommand]
        private async Task LaunchRecentGameAsync(GameListViewModel? game)
        {
            if (game == null || !game.HasBinding)
                return;

            await LaunchBoundGameAsync(game.AppId);
        }

        public async Task<bool> SwitchAccountBySteamIdAsync(string steamId, bool launchSteam)
        {
            if (string.IsNullOrWhiteSpace(steamId)) return false;
            var account = Accounts.FirstOrDefault(item => string.Equals(item.SteamId, steamId, StringComparison.Ordinal));
            if (account == null)
            {
                StatusText = $"快捷方式指定的账号不存在：{steamId}";
                return false;
            }

            SelectedAccount = account;
            await SwitchAccountAsync(launchSteam);
            return AccountManager.AreSameAccount(_accountManager.CurrentAccount, account.Account);
        }

        private async Task SwitchAccountAsync(bool launchSteam)
        {
            if (SelectedAccount == null) return;
            if (!await _operationGate.WaitAsync(0))
            {
                StatusText = "账号操作正在进行，请稍候";
                return;
            }

            try
            {
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
                ClearFailedAccountSwitch();
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
                    ClearFailedAccountSwitch();
                    StatusText = $"已切换到 {SelectedAccount.Account.PersonaName}";
                    if (ShowSwitchNotification)
                    {
                        NotificationRequested?.Invoke(this, $"已切换到 {SelectedAccount.Account.PersonaName}");
                    }
                    if (PlaySoundOnSwitch)
                    {
                        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    }
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
                    RememberFailedAccountSwitch(SelectedAccount, launchSteam);
                    StatusText = "切换失败，请确保 Steam 已关闭";
                    if (ShowSwitchNotification)
                    {
                        NotificationRequested?.Invoke(this, "切换失败，请确保 Steam 已关闭");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("SwitchAccountAsync failed.", ex);
                RememberFailedAccountSwitch(SelectedAccount, launchSteam);
                StatusText = $"切换失败: {ex.Message}";
            }

            finally
            {
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                if (MinimalMode)
                    Core.MemoryLimiter.CleanMemory();
            }
            }
            finally
            {
                IsLoading = false;
                _operationGate.Release();
            }
        }

        public async Task RetryLastAccountSwitchAsync()
        {
            var account = _lastFailedSwitchAccount;
            if (account == null || IsLoading)
                return;

            SelectedAccount = account;
            await SwitchAccountAsync(_lastFailedSwitchLaunchSteam);
        }

        private void RememberFailedAccountSwitch(AccountViewModel account, bool launchSteam)
        {
            _lastFailedSwitchAccount = account;
            _lastFailedSwitchLaunchSteam = launchSteam;
            OnPropertyChanged(nameof(HasFailedAccountSwitch));
        }

        private void ClearFailedAccountSwitch()
        {
            if (_lastFailedSwitchAccount == null)
                return;
            _lastFailedSwitchAccount = null;
            OnPropertyChanged(nameof(HasFailedAccountSwitch));
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            IsLoading = true;
            StatusText = "正在刷新账号列表...";

            try
            {
                await _accountManager.LoadAccountsAsync();
                // 进程枚举 + 读 MainModule 是重同步 IO，Steam 进程多时会卡顿 UI。
                IsSteamRunning = await Task.Run(() => _accountManager.GetSteamService().IsSteamRunning());
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
                if (closed && !IsSteamRunning && GetSettings().ShowNotificationOnSteamClose)
                    NotificationRequested?.Invoke(this, "Steam 已关闭");
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

        private async Task SwitchAccountAndLaunchGameAsync(int appId, string gameName, string? steamId)
        {
            if (!await _operationGate.WaitAsync(0))
            {
                StatusText = "已有账号或游戏操作正在进行，请稍候";
                return;
            }

            try
            {
            IsLoading = true;

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
                try
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
                    var launched = await LaunchGameWithWorkflowAsync(appId, gameName, account);
                    StatusText = launched
                        ? $"当前已是 {account.PersonaName}，正在启动游戏"
                        : "启动游戏失败";
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"SwitchAccountAndLaunchGameAsync failed. appId={appId}", ex);
                    StatusText = $"启动失败: {ex.Message}";
                }
                finally
                {
                    IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                }
                return;
            }

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

                var launched = await LaunchGameWithWorkflowAsync(appId, gameName, account);
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
                if (MinimalMode)
                {
                    Core.MemoryLimiter.CleanMemory();
                }
            }
            }
            finally
            {
                IsLoading = false;
                _operationGate.Release();
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
            ExitCleanup.Run(
                ("injection cancellation", () => _injectorAttemptCts?.Cancel()),
                ("view-model cancellation", _lifetimeCts.Cancel),
                ("view-model token", _lifetimeCts.Dispose),
                ("settings timer", () =>
                {
                    _settingsSaveTimer.Stop();
                    _settingsSaveTimer.Tick -= OnSettingsSaveTimerTick;
                }),
                ("Steam status timer", () =>
                {
                    _steamStatusTimer.Stop();
                    _steamStatusTimer.Tick -= OnSteamStatusTimerTick;
                }),
                ("pending settings", () => SavePendingSettings()),
                ("launch cancellation", _launchWorkflowCts.Cancel),
                ("launch token", _launchWorkflowCts.Dispose),
                ("memory timer", () =>
                {
                    if (_minimalModeTimer == null) return;
                    _minimalModeTimer.Stop();
                    _minimalModeTimer.Tick -= OnMinimalModeTimerTick;
                }),
                ("view-model subscriptions", () =>
                {
                    PropertyChanged -= OnPropertyChanged;
                    Accounts.CollectionChanged -= OnDashboardCollectionChanged;
                    GameList.CollectionChanged -= OnDashboardCollectionChanged;
                    GameBindings.CollectionChanged -= OnDashboardCollectionChanged;
                    _accountManager.AccountsChanged -= OnAccountsChanged;
                    _gameBinding.BindingsChanged -= OnGameBindingsChanged;
                    _quickLaunch.ItemsChanged -= OnQuickLaunchItemsChanged;
                    _accountManager.BeforeAccountSwitchAsync = null;
                    _gameSaveAutoBackupService.BackupCompleted -= OnGameSaveAutoBackupCompleted;
                    foreach (var account in Accounts)
                        account.PropertyChanged -= OnAccountPropertyChanged;
                }),
                ("automatic backup", _gameSaveAutoBackupService.Dispose),
                ("injection resources", CleanupInjectorResources),
                ("quick-launch service", _quickLaunch.Dispose),
                ("account service", _accountManager.Dispose));
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

        partial void OnGameNameChanged(string value) => OnPropertyChanged(nameof(Name));

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
        public long LastLoginTimestamp => Account.Timestamp;
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

        /// <summary>
        /// True when Steam has pruned this account from loginusers.vdf and the
        /// app only knows it from the local account library. Switching restores
        /// it into loginusers.vdf first.
        /// </summary>
        public bool IsArchived => Account.IsArchived;

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
