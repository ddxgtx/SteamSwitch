using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;
using SteamSwitcher.ViewModels;
using SteamSwitcher.Controls;

namespace SteamSwitcher.Views
{
    public partial class TaskbarBandWindow : Window
    {
        private const int AdaptiveMinimumLyricsWidth = 160;
        // 宽度合并窗口：切歌帧 + 歌词抓取完成帧往往只隔几百毫秒，各自 resize
        // 一次就是任务栏上闪两下。等这个窗口内没有新需求再一次性应用。
        private const int AdaptiveWidthDebounceMs = 320;
        private readonly AccountManager _accountManager;
        private readonly Func<Point?> _readCursorPosition;
        private readonly TaskbarEmbedder _embedder;
        private readonly TaskbarMediaService _mediaService = new();
        private MainViewModel? _viewModel;
        private List<SteamAccount> _pinnedAccounts = new();
        private List<GameListViewModel> _pinnedGames = new();
        private List<QuickLaunchItem> _pinnedQuickLaunchItems = new();
        private bool _isPinned;
        private EventHandler? _pinnedGamesChangedHandler;
        private EventHandler? _pinnedAccountsChangedHandler;
        private EventHandler? _quickLaunchChangedHandler;
        private EventHandler? _currentAccountChangedHandler;
        private PanelDragHelper? _gameDragHelper;
        private PanelDragHelper? _avatarDragHelper;

        private int _glassPadding = 2;
        private int _iconSize = 38;
        private bool _glassEnabled = true;
        private bool _roundedMode = true;
        private int _iconRadius = 27;
        private int _glassRadius = 31;
        private int PanelHorizontalPadding => Math.Max(6, _glassPadding + 2);
        private int PanelVerticalPadding => Math.Max(3, _glassPadding);
        private int PanelHeight => _iconSize + PanelVerticalPadding * 2 + 2;
        private bool _lyricsEnabled;
        private bool _lyricsShowControls = true;
        private bool _lyricsArtworkRounded = true;
        private bool _lyricsShowVisualizer = true;
        private bool _lyricsTransparentBackground = true;
        private bool _lyricsAdaptiveWidth = true;
        private int _lyricsWidth = 280;
        private int _currentAdaptiveLyricsWidth = 280;
        private bool _lyricsCompositeMode = true;
        private bool _lyricsTrackInfoOnly;
        private bool _lyricsShowSecondaryLine = true;
        private bool _lyricsShowProgressBar = true;
        // 无媒体播放时是否收起歌词岛。用户要求"没有歌曲播放时不显示任务栏"。
        private bool _lyricsHideWhenNoMedia = true;
        // 区分"用户主动取消了固定"和"因无内容而临时撤下"：只有前者才阻止自动贴回。
        private bool _userPinned = true;
        // ApplyLyricsPresence 可能经 Attach → Rebuild 回到自身，防重入。
        private bool _applyingPresence;
        // 嵌入失败后的重试退避：否则每轮轮询都会再试一次。
        private DateTime _lastEmbedAttempt = DateTime.MinValue;
        private static readonly TimeSpan EmbedRetryBackoff = TimeSpan.FromSeconds(2);
        private int _lyricsVerticalOffset;
        private int _lyricsFontSize = 14;
        private string _lyricsFontFamily = "Segoe UI Variable Text";
        private bool _adaptiveLayoutDirty = true;
        private IReadOnlyList<SyncedLyricLine>? _lastLayoutLyrics;
        private TaskbarMediaSnapshot? _pendingMediaSnapshot;
        private int _mediaUpdateQueued;
        private volatile bool _isClosed;
        private System.Windows.Threading.DispatcherTimer? _visualizerTimer;
        private System.Windows.Threading.DispatcherTimer? _volumeHideTimer;
        private readonly TaskbarFlyoutWindow _volumeFlyout = new();
        private bool _isPlaying;
        // ── 悬停仲裁的三个真源 ──────────────────────────────────────────────
        // _isLyricsHovered        指针此刻是否被认为停留在歌词岛上。
        // _hoverControlsVisible   控制条是否已展开（动画去重用）。
        // _interactionLocked      悬停期间冻结一切会改变布局/几何的轮询更新，
        //                         直到控制条彻底收起后才解冻——布局一变，命中
        //                         测试就重算，鼠标没动也会收到成对的假进出事件，
        //                         控制条就会自己在眼睛底下抖。
        private bool _isLyricsHovered;
        private bool _hoverControlsVisible;
        private bool _interactionLocked;
        private bool _controlsAvailable;
        // 宽度合并窗口：切歌与歌词抓取完成往往只隔几百毫秒，两次 resize 就是两次闪烁。
        private int _pendingAdaptiveWidth;
        private System.Windows.Threading.DispatcherTimer? _widthDebounceTimer;
        // 假 MouseLeave 探针：tooltip 弹出、副行折叠、宽度微调都会让 WPF 丢掉
        // IsMouseOver。鼠标若真的一动没动，就不会再有任何 MouseEnter 来纠正，
        // 因此离开事件只当作"请复核"的信号，真正判决交给光标物理坐标。
        private System.Windows.Threading.DispatcherTimer? _hoverProbeTimer;
        private System.Windows.Threading.DispatcherTimer? _hoverUnlockTimer;
        private byte[]? _lastArtwork;
        private string _lastLyricText = string.Empty;
        private string _lastCompositeText = string.Empty;
        private string _lastTooltipText = string.Empty;
        private bool? _playPauseAccent;
        private string _lastTrackKey = string.Empty;
        private long _lastTrackVersion = -1;
        private TaskbarMediaSnapshot? _lastSnapshot;

        public event EventHandler<SteamAccount>? AccountSwitchRequested;
        public event EventHandler<SteamAccount>? ForceAccountRestartRequested;
        public event EventHandler<int>? GameLaunchRequested;
        public event EventHandler<string>? QuickLaunchRequested;
        public event EventHandler? ShowMainWindowRequested;
        public event EventHandler? DetachRequested;
        public event EventHandler<bool>? ToggleDesktopFloatingRequested;
        public event EventHandler<bool>? ToggleDesktopFloatingTopmostRequested;
        public event EventHandler<bool>? ToggleTaskbarPinnedRequested;
        public event EventHandler<bool>? ToggleTaskbarLyricsRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler<IReadOnlyList<string>>? PanelItemOrderChanged;
        public event EventHandler<IReadOnlyList<string>>? AccountOrderChanged;
        public event EventHandler<int>? GameDeleteRequested;
        public event EventHandler<string>? QuickLaunchDeleteRequested;
        public event EventHandler<int>? AccountDeleteRequested;

        public TaskbarBandWindow(AccountManager accountManager) : this(accountManager, ReadSystemCursorPosition)
        {
        }

        internal TaskbarBandWindow(AccountManager accountManager, Func<Point?> readCursorPosition)
        {
            _readCursorPosition = readCursorPosition ?? throw new ArgumentNullException(nameof(readCursorPosition));
            InitializeComponent();
            AlbumArtwork.SizeChanged += (_, _) => UpdateArtworkClip();
            ArtworkBorder.SizeChanged += (_, _) => UpdateArtworkClip();
            _accountManager = accountManager;
            _embedder = new TaskbarEmbedder();
            _mediaService.StateChanged += MediaService_StateChanged;
            _currentAccountChangedHandler = (s, e) => PostToUi(
                () => UpdateCurrentAccount(_accountManager.CurrentAccount));
            _accountManager.CurrentAccountChanged += _currentAccountChangedHandler;
            _embedder.TaskbarCreated += (s, e) => PostToUi(Rebuild);
            Loaded += (s, e) => { UpdateGlass(); SetupDragDrop(); UpdateArtworkClip(); };
            Closing += (s, e) => ExitCleanup.Run(("移除任务栏嵌入", Detach));
            Closed += (s, e) =>
            {
                _isClosed = true;
                Interlocked.Exchange(ref _pendingMediaSnapshot, null);
                ExitCleanup.Run(
                    ("解绑任务栏账号事件", () =>
                    {
                        if (_currentAccountChangedHandler != null)
                            _accountManager.CurrentAccountChanged -= _currentAccountChangedHandler;
                    }),
                    ("解绑任务栏媒体事件", () => _mediaService.StateChanged -= MediaService_StateChanged),
                    ("停止任务栏可视化计时器", () =>
                    {
                        var timer = _visualizerTimer;
                        _visualizerTimer = null;
                        if (timer == null) return;
                        timer.Stop();
                        timer.Tick -= VisualizerTimer_Tick;
                    }),
                    ("停止任务栏音量计时器", () =>
                    {
                        var timer = _volumeHideTimer;
                        _volumeHideTimer = null;
                        timer?.Stop();
                    }),
                    ("停止任务栏悬停去抖计时器", () =>
                    {
                        var probe = _hoverProbeTimer;
                        _hoverProbeTimer = null;
                        probe?.Stop();
                        var unlock = _hoverUnlockTimer;
                        _hoverUnlockTimer = null;
                        unlock?.Stop();
                    }),
                    ("停止歌词岛宽度合并计时器", () =>
                    {
                        var debounce = _widthDebounceTimer;
                        _widthDebounceTimer = null;
                        debounce?.Stop();
                    }),
                    ("解绑任务栏模型事件", DetachViewModelEvents),
                    ("关闭任务栏音量浮窗", () => _volumeFlyout.Close()),
                    ("解绑任务栏游戏拖动", () =>
                    {
                        var helper = _gameDragHelper;
                        _gameDragHelper = null;
                        helper?.Detach();
                    }),
                    ("解绑任务栏账号拖动", () =>
                    {
                        var helper = _avatarDragHelper;
                        _avatarDragHelper = null;
                        helper?.Detach();
                    }),
                    ("释放任务栏媒体服务", () => _mediaService.Dispose()),
                    ("释放任务栏嵌入器", () => _embedder.Dispose()));
            };
            MouseLeftButtonDown += TaskbarBandWindow_MouseLeftButtonDown;
            MouseRightButtonDown += TaskbarBandWindow_MouseRightButtonDown;
        }

        private void SetupDragDrop()
        {
            // Guard against Loaded firing more than once (or re-entry after a
            // theme/host rebuild). Double Attach() would stack handlers and
            // fire click/delete/move events twice.
            if (_gameDragHelper != null || _avatarDragHelper != null)
                return;

            _gameDragHelper = new PanelDragHelper(GamePanel);
            _gameDragHelper.ItemClicked += (s, index) =>
            {
                if (TryGetGamePanelElement(index, out var element))
                    LaunchGamePanelElement(element);
            };
            _gameDragHelper.ItemDeleted += (s, index) =>
            {
                if (TryGetGamePanelElement(index, out var element))
                    DeleteGamePanelElement(element);
            };
            _gameDragHelper.ItemMoved += (s, args) => ApplyGamePanelOrder();
            _gameDragHelper.Attach();

            _avatarDragHelper = new PanelDragHelper(AvatarPanel);
            _avatarDragHelper.ItemClicked += (s, index) =>
            {
                if (index < _pinnedAccounts.Count)
                    AccountSwitchRequested?.Invoke(this, _pinnedAccounts[index]);
            };
            _avatarDragHelper.ItemDeleted += (s, index) => AccountDeleteRequested?.Invoke(this, index);
            _avatarDragHelper.ItemMoved += (s, args) => ApplyAvatarPanelOrder();
            _avatarDragHelper.Attach();
        }

        private void ApplyGamePanelOrder()
        {
            var keys = new List<string>();

            foreach (var child in GamePanel.Children)
            {
                if (child is not FrameworkElement element)
                    continue;

                if (element.Tag is GameListViewModel game)
                    keys.Add(MainViewModel.CreatePanelGameKey(game.AppId));
                else if (element.Tag is QuickLaunchItem item)
                    keys.Add(MainViewModel.CreatePanelQuickLaunchKey(item.Id));
            }

            if (keys.Count == _pinnedGames.Count + _pinnedQuickLaunchItems.Count)
                PanelItemOrderChanged?.Invoke(this, keys);
        }

        private bool TryGetGamePanelElement(int index, out FrameworkElement element)
        {
            element = null!;
            if (index < 0 || index >= GamePanel.Children.Count)
                return false;

            if (GamePanel.Children[index] is not FrameworkElement panelElement)
                return false;

            element = panelElement;
            return true;
        }

        private void LaunchGamePanelElement(FrameworkElement element)
        {
            if (element.Tag is GameListViewModel game)
                GameLaunchRequested?.Invoke(this, game.AppId);
            else if (element.Tag is QuickLaunchItem item)
                QuickLaunchRequested?.Invoke(this, item.Id);
        }

        private void DeleteGamePanelElement(FrameworkElement element)
        {
            if (element.Tag is GameListViewModel game)
            {
                // Pass the AppId (stable identity), not a positional index. The panel
                // order is normalized and can differ from the stored PinnedGameIds order,
                // so an index here would remove the wrong game.
                GameDeleteRequested?.Invoke(this, game.AppId);
            }
            else if (element.Tag is QuickLaunchItem item)
            {
                QuickLaunchDeleteRequested?.Invoke(this, item.Id);
            }
        }

        private void ApplyAvatarPanelOrder()
        {
            var accountIds = new List<string>();
            foreach (var child in AvatarPanel.Children)
            {
                if (child is FrameworkElement element && element.Tag is SteamAccount account)
                    accountIds.Add(account.SteamId);
            }

            if (accountIds.Count == _pinnedAccounts.Count)
                AccountOrderChanged?.Invoke(this, accountIds);
        }

        private void UpdateLayoutForDrag(bool isDragging)
        {
            // No longer needed
        }

        private void TaskbarBandWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void TaskbarBandWindow_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                ShowContextMenu();
            }
            catch (Exception ex)
            {
                // 右键路径一旦抛异常，WPF 会为每个未处理项弹一个致命框——用户看到的就是
                // "右键弹出大量报错"。这里兜住并落日志，界面保持可用。
                AppLogger.Error("Taskbar band right-click failed.", ex);
            }
        }

        private void ShowContextMenu()
        {
            var menu = BuildContextMenu();
            menu.IsOpen = true;
        }

        /// <summary>
        /// 构建任务栏右键菜单。与打开动作分离，便于无头环境验证菜单本身。
        /// </summary>
        private ContextMenu BuildContextMenu()
        {
            var menu = SharedContextMenuBuilder.Build(
                _viewModel?.Accounts,
                _viewModel?.Accounts.FirstOrDefault(account => account.IsCurrent),
                showMainWindow: () => ShowMainWindowRequested?.Invoke(this, EventArgs.Empty),
                accountSelected: steamId =>
                {
                    var account = _viewModel?.Accounts.FirstOrDefault(a => a.SteamId == steamId)?.Account ?? _pinnedAccounts.Find(a => a.SteamId == steamId);
                    if (account != null)
                        AccountSwitchRequested?.Invoke(this, account);
                },
                launchSteam: () => _accountManager.LaunchSteam(),
                refresh: () => Rebuild(),
                detach: () => DetachRequested?.Invoke(this, EventArgs.Empty),
                showDetach: true,
                isDesktopFloatingEnabled: _viewModel?.DesktopFloatingEnabled ?? false,
                isDesktopFloatingTopmost: _viewModel?.DesktopFloatingTopmost ?? true,
                isTaskbarPinned: _isPinned,
                toggleDesktopFloating: enabled => ToggleDesktopFloatingRequested?.Invoke(this, enabled),
                toggleDesktopFloatingTopmost: enabled => ToggleDesktopFloatingTopmostRequested?.Invoke(this, enabled),
                toggleTaskbarPinned: enabled => ToggleTaskbarPinnedRequested?.Invoke(this, enabled),
                isTaskbarLyricsEnabled: _viewModel?.TaskbarLyricsEnabled ?? false,
                toggleTaskbarLyrics: enabled => ToggleTaskbarLyricsRequested?.Invoke(this, enabled),
                exit: () => ExitRequested?.Invoke(this, EventArgs.Empty),
                retryLyrics: () =>
                {
                    try { _mediaService.RetryLyrics(); }
                    catch (Exception ex) { AppLogger.Error("Retrying lyrics from the taskbar menu failed.", ex); }
                },
                canRetryLyrics: _lyricsEnabled && _mediaService.OnlineLyricsEnabled && _mediaService.Current.HasSession && _mediaService.Current.CanControlPlayback
            );
            return menu;
        }

        // --- Public API ---

        private void PostToUi(Action action)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            try { Dispatcher.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        public bool IsPinned => _isPinned;

        public void SetViewModel(MainViewModel vm)
        {
            DetachViewModelEvents();

            _viewModel = vm;

            // Store event handler references for later unsubscription
            _pinnedGamesChangedHandler = (s, e) => PostToUi(() =>
            {
                _pinnedGames = _viewModel.GetPinnedGames();
                Rebuild();
            });
            _pinnedAccountsChangedHandler = (s, e) => PostToUi(() =>
            {
                _pinnedAccounts = _viewModel.GetPinnedAccounts();
                Rebuild();
            });
            _quickLaunchChangedHandler = (s, e) => PostToUi(() =>
            {
                _pinnedQuickLaunchItems = _viewModel.GetPinnedQuickLaunchItems();
                Rebuild();
            });

            _viewModel.PinnedGamesChanged += _pinnedGamesChangedHandler;
            _viewModel.PinnedAccountsChanged += _pinnedAccountsChangedHandler;
            _viewModel.QuickLaunchChanged += _quickLaunchChangedHandler;
        }

        private void DetachViewModelEvents()
        {
            if (_viewModel == null)
                return;

            if (_pinnedGamesChangedHandler != null)
                _viewModel.PinnedGamesChanged -= _pinnedGamesChangedHandler;
            if (_pinnedAccountsChangedHandler != null)
                _viewModel.PinnedAccountsChanged -= _pinnedAccountsChangedHandler;
            if (_quickLaunchChangedHandler != null)
                _viewModel.QuickLaunchChanged -= _quickLaunchChangedHandler;

            _viewModel = null;
        }

        public void SetPinnedAccounts(List<SteamAccount> accounts)
        {
            _pinnedAccounts = accounts ?? new();
            Rebuild();
        }

        public void SetPinnedGames(List<GameListViewModel> games)
        {
            _pinnedGames = games ?? new();
            Rebuild();
        }

        public void SetPinnedQuickLaunchItems(List<QuickLaunchItem> items)
        {
            _pinnedQuickLaunchItems = items ?? new();
            Rebuild();
        }

        public void SetWindowSize(int size)
        {
            _glassPadding = Math.Clamp(size, 2, 30);
            Rebuild();
        }

        public void SetAvatarSize(int size)
        {
            _iconSize = Math.Clamp(size, 16, 96);
            Rebuild();
        }

        public void SetPosition(TaskbarPosition pos) => _embedder.Position = pos;
        public void SetMonitorIndex(int monitorIndex) => _embedder.SetMonitorIndex(monitorIndex);

        public void SetOffset(int x, int y)
        {
            _embedder.OffsetX = x;
            _embedder.OffsetY = y;
        }

        public void SetGlassEnabled(bool enabled)
        {
            _glassEnabled = enabled;
            Rebuild();
        }

        public void SetRoundedMode(bool rounded)
        {
            _roundedMode = rounded;
            Rebuild();
        }

        public void SetLyricsSettings(
            bool enabled,
            bool showControls,
            bool onlineLyrics,
            int width,
            bool sourceQQ = true,
            bool sourceNetease = true,
            bool sourceKugou = true,
            bool sourceLrclib = true,
            int offsetMs = 0,
            bool compositeMode = true,
            bool artworkRounded = true,
            bool showVisualizer = true,
            bool adaptiveWidth = true,
            bool trackInfoOnly = false,
            bool showSecondaryLine = true,
            int pollingIntervalMs = 250,
            int verticalOffset = 0,
            int fontSize = 14,
            bool transparentBackground = true,
            string fontFamily = "Segoe UI Variable Text",
            bool splitLongLines = false,
            bool showProgressBar = true,
            bool hideWhenNoMedia = true,
            bool onlyMusicLyrics = true)
        {
            string normalizedFontFamily = string.IsNullOrWhiteSpace(fontFamily)
                ? "Segoe UI Variable Text"
                : fontFamily.Trim();
            int normalizedWidth = Math.Clamp(width, 240, 720);
            int normalizedFontSize = Math.Clamp(fontSize, 12, 18);
            _adaptiveLayoutDirty |= normalizedWidth != _lyricsWidth
                || normalizedFontSize != _lyricsFontSize
                || !string.Equals(normalizedFontFamily, _lyricsFontFamily, StringComparison.Ordinal)
                || adaptiveWidth != _lyricsAdaptiveWidth
                || trackInfoOnly != _lyricsTrackInfoOnly
                || showSecondaryLine != _lyricsShowSecondaryLine;

            _lyricsEnabled = enabled;
            _lyricsShowControls = showControls;
            _lyricsWidth = normalizedWidth;
            _lyricsCompositeMode = compositeMode;
            _lyricsArtworkRounded = artworkRounded;
            _lyricsShowVisualizer = showVisualizer;
            _lyricsTransparentBackground = transparentBackground;
            if (VisualizerBadge != null)
                VisualizerBadge.Visibility = showVisualizer ? Visibility.Visible : Visibility.Collapsed;
            if (!showVisualizer)
            {
                _visualizerTimer?.Stop();
                StopVisualizer();
            }
            _lyricsAdaptiveWidth = adaptiveWidth;
            _lyricsTrackInfoOnly = trackInfoOnly;
            _lyricsShowSecondaryLine = showSecondaryLine;
            _lyricsShowProgressBar = showProgressBar;
            _lyricsHideWhenNoMedia = hideWhenNoMedia;
            _lyricsVerticalOffset = Math.Clamp(verticalOffset, -12, 12);
            _lyricsFontSize = normalizedFontSize;
            _lyricsFontFamily = normalizedFontFamily;
            _mediaService.SplitLongLines = splitLongLines;
            _mediaService.OnlyMusicLyrics = onlyMusicLyrics;
            if (!_lyricsAdaptiveWidth)
            {
                _currentAdaptiveLyricsWidth = _lyricsWidth;
            }
            _lastLayoutLyrics = null;
            _mediaService.OnlineLyricsEnabled = onlineLyrics;
            _mediaService.ConfigureLyricsSources(sourceQQ, sourceNetease, sourceKugou, sourceLrclib);
            _mediaService.OffsetMs = offsetMs;
            _mediaService.PollingIntervalMs = pollingIntervalMs;
            if (enabled)
            {
                _ = _mediaService.StartAsync();
            }
            else
            {
                _mediaService.Stop();
                _visualizerTimer?.Stop();
                StopVisualizer();
            }
            Rebuild();
            UpdateMediaUi(_mediaService.Current);
        }

        public void Attach()
        {
            _userPinned = true;
            if (_isPinned) return;
            _embedder.EmbedWindow(this, CalcWidth());
            _isPinned = _embedder.IsEmbedded;
            if (_isPinned) { UpdateGlass(); Rebuild(); }
        }

        /// <summary>
        /// Forwards Explorer's TaskbarCreated broadcast to the embedder so the
        /// band re-parents onto the recreated taskbar. The embedder's
        /// TaskbarCreated event then rebuilds the UI.
        /// </summary>
        public void HandleTaskbarCreated() => _embedder.HandleTaskbarCreated();

        public void Detach()
        {
            _userPinned = false;
            DetachFromTaskbar();
        }

        private void DetachFromTaskbar()
        {
            if (!_isPinned) return;
            _embedder.RemoveFromTaskbar();
            _isPinned = false;
        }

        /// <summary>
        /// 有媒体会话才让歌词岛可见；开了"无歌曲时隐藏"的情况下，连整个任务栏条
        /// 一起撤下来（面板上没有固定游戏/头像时），有歌再贴回去。
        /// </summary>
    private void ApplyLyricsPresence()
    {
        if (_isClosed || _applyingPresence) return;
        _applyingPresence = true;
        try
        {
            bool hasSession = _lastSnapshot is { HasSession: true };
            bool showLyrics = _lyricsEnabled && !(_lyricsHideWhenNoMedia && !hasSession);
            if (LyricsHost != null)
            {
                var target = showLyrics ? Visibility.Visible : Visibility.Collapsed;
                if (LyricsHost.Visibility != target) LyricsHost.Visibility = target;
            }

            if (!_userPinned || SuppressAutoEmbed) return;
            bool hasPanelContent = _pinnedGames.Count > 0
                || _pinnedQuickLaunchItems.Count > 0
                || _pinnedAccounts.Count > 0;
            bool shouldEmbed = showLyrics || hasPanelContent;

            if (shouldEmbed && !_isPinned)
            {
                // 嵌入失败时 _isPinned 仍为 false，而轮询每 250ms 就会走到这里——
                // 没有退避就会反复 EnsureHandle/EmbedToTaskbar/StartPositionTimer，
                // 表现为一堆报错。退避 2 秒再试。
                if (DateTime.UtcNow - _lastEmbedAttempt < EmbedRetryBackoff) return;
                _lastEmbedAttempt = DateTime.UtcNow;
                AppLogger.Warn("No media or panel content; re-attaching the taskbar band.");
                Attach();
            }
            else if (!shouldEmbed && _isPinned)
            {
                AppLogger.Warn("No media session and no pinned content; detaching the taskbar band.");
                DetachFromTaskbar();
            }
        }
        finally
        {
            _applyingPresence = false;
        }
    }

        public void UpdateCurrentAccount(SteamAccount? current)
        {
            foreach (var child in AvatarPanel.Children)
            {
                if (child is Border b && b.Tag is SteamAccount acc)
                {
                    bool isCur = AccountManager.AreSameAccount(acc, current);
                    b.BorderThickness = isCur ? new Thickness(2.5) : new Thickness(1);
                    b.BorderBrush = isCur ? _currentBorderBrush : _normalBorderBrush;
                }
            }
        }

        // --- Layout ---

        public void Rebuild()
        {
            int h = PanelHeight;
            _iconRadius = _roundedMode ? _iconSize / 2 : 8;
            _glassRadius = h / 2;

            GamePanel.Children.Clear();
            var items = _viewModel?.GetPinnedPanelItems();
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item is GameListViewModel game)
                        GamePanel.Children.Add(MakeGameBtn(game));
                    else if (item is QuickLaunchItem quickLaunchItem)
                        GamePanel.Children.Add(MakeQuickLaunchBtn(quickLaunchItem));
                }
            }
            else
            {
                foreach (var g in _pinnedGames)
                    GamePanel.Children.Add(MakeGameBtn(g));
                foreach (var q in _pinnedQuickLaunchItems)
                    GamePanel.Children.Add(MakeQuickLaunchBtn(q));
            }

            AvatarPanel.Children.Clear();
            foreach (var a in _pinnedAccounts)
                AvatarPanel.Children.Add(MakeAvatarBtn(a));

            bool hasGames = _pinnedGames.Count > 0 || _pinnedQuickLaunchItems.Count > 0;
            GameAvatarSeparator.Visibility =
                hasGames && _pinnedAccounts.Count > 0
                    ? Visibility.Visible : Visibility.Collapsed;

            if (GameAvatarContainer != null)
            {
                GameAvatarContainer.Visibility = (hasGames || _pinnedAccounts.Count > 0)
                    ? Visibility.Visible : Visibility.Collapsed;
            }

            // 无媒体会话时（且用户开了"无歌曲时隐藏"）歌词岛也要跟着收起来，
            // 不能只在这里按 _lyricsEnabled 判断。
            bool hasSessionNow = _lastSnapshot is { HasSession: true };
            LyricsHost.Visibility = _lyricsEnabled && !(_lyricsHideWhenNoMedia && !hasSessionNow)
                ? Visibility.Visible
                : Visibility.Collapsed;
            LyricsHost.Width = (_lyricsAdaptiveWidth && _currentAdaptiveLyricsWidth > 0) ? _currentAdaptiveLyricsWidth : _lyricsWidth;
            // Keep the capsule inside the explicitly sized taskbar window, so
            // its lower glass rim is not clipped at the default icon size.
            LyricsHost.Height = Math.Max(42, PanelHeight);
            LyricsHost.CornerRadius = new CornerRadius(LyricsHost.Height / 2);
            LyricsHost.Margin = new Thickness(0, 0, hasGames || _pinnedAccounts.Count > 0 ? 14 : 0, 0);
            if (_lyricsTransparentBackground)
            {
                LyricsHost.Background = Brushes.Transparent;
                LyricsHost.BorderBrush = Brushes.Transparent;
            }
            else
            {
                LyricsHost.SetResourceReference(Border.BackgroundProperty,
                    _glassEnabled ? "FloatingSurfaceBrush" : "FloatingSolidBrush");
                LyricsHost.SetResourceReference(Border.BorderBrushProperty, "FloatingBorderBrush");
            }
            LyricText.SetResourceReference(TextBlock.ForegroundProperty,
                _lyricsTransparentBackground || _glassEnabled ? "GlassTextPrimaryBrush" : "FloatingTextPrimaryBrush");
            MediaText.SetResourceReference(TextBlock.ForegroundProperty,
                _lyricsTransparentBackground || _glassEnabled ? "GlassTextSecondaryBrush" : "FloatingTextSecondaryBrush");
            MediaIcon.SetResourceReference(TextBlock.ForegroundProperty,
                _lyricsTransparentBackground || _glassEnabled ? "GlassTextSecondaryBrush" : "FloatingTextSecondaryBrush");
            // Clear glass needs text shadow over both light and dark wallpapers.
            foreach (var text in new[] { LyricText, MediaText, MediaIcon })
                text.Effect = _lyricsTransparentBackground || _glassEnabled
                    ? new System.Windows.Media.Effects.DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1, Opacity = 0.5 }
                    : null;
            LyricsHost.BorderThickness = _lyricsTransparentBackground ? new Thickness(0) : new Thickness(1);
            LyricsHost.RenderTransform = new TranslateTransform(0, _lyricsVerticalOffset);
            if (LyricsProgress != null)
                LyricsProgress.Visibility = _lyricsShowProgressBar ? Visibility.Visible : Visibility.Collapsed;
            var lyricsFont = new FontFamily(_lyricsFontFamily);
            LyricText.FontFamily = lyricsFont;
            MediaText.FontFamily = lyricsFont;
            LyricText.FontSize = _lyricsFontSize;
            MediaText.FontSize = Math.Max(11.5, _lyricsFontSize - 2.5);
            MediaIcon.FontSize = Math.Max(10, _lyricsFontSize - 4);
            if (ArtworkBorder != null)
            {
                ArtworkBorder.CornerRadius = _lyricsArtworkRounded ? new CornerRadius(8) : new CornerRadius(0);
                UpdateArtworkClip();
            }
            if (!_lyricsShowControls && LyricsControlsGlass != null)
            {
                // _hoverControlsVisible 与实际显示的要是同一个值，否则关闭
                // "悬停控制"后第一次移入会因为状态去重而不做任何事。
                _hoverControlsVisible = false;
                LyricsControlsGlass.Opacity = 0;
                LyricsControlsGlass.IsHitTestVisible = false;
            }

            ApplyLayout();
        }

        private void UpdateArtworkClip()
        {
            if (AlbumArtwork == null)
            {
                return;
            }

            if (!_lyricsArtworkRounded)
            {
                ArtworkBorder.Clip = null;
                AlbumArtwork.Clip = null;
                return;
            }

            double containerWidth = ArtworkBorder.ActualWidth;
            double containerHeight = ArtworkBorder.ActualHeight;
            if (containerWidth > 0 && containerHeight > 0)
            {
                double containerRadius = Math.Min(8d, Math.Min(containerWidth, containerHeight) / 2d);
                ArtworkBorder.Clip = new RectangleGeometry(
                    new Rect(0, 0, containerWidth, containerHeight),
                    containerRadius,
                    containerRadius);
            }

            double width = AlbumArtwork.ActualWidth;
            double height = AlbumArtwork.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            double radius = Math.Min(8d, Math.Min(width, height) / 2d);
            AlbumArtwork.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        }

        private void ApplyLayout()
        {
            int w = CalcWidth();
            int h = Math.Max(PanelHeight, _lyricsEnabled ? 42 : 36);

            Width = w;
            Height = h;
            BandContent.Width = w;
            BandContent.Height = h;

            GlassBorder.Background = Brushes.Transparent;
            GlassBorder.BorderThickness = new Thickness(0);
            GlassBorder.CornerRadius = new CornerRadius(0);
            GlassBorder.Padding = new Thickness(0);

            if (GameAvatarContainer != null)
            {
                GameAvatarContainer.CornerRadius = new CornerRadius(_glassRadius);
                GameAvatarContainer.Padding = new Thickness(PanelHorizontalPadding, PanelVerticalPadding,
                    PanelHorizontalPadding, PanelVerticalPadding);
            }
            UpdateGlass();

            if (_isPinned)
            {
                _embedder.UpdateWidth(w);
            }
        }

        private int CalcWidth()
        {
            int gc = _pinnedGames.Count + _pinnedQuickLaunchItems.Count;
            int ac = _pinnedAccounts.Count;
            int item = _iconSize + 4;
            int total = gc * item;
            if (gc > 0 && ac > 0) total += 9;
            total += ac * item;

            if (total > 0)
            {
                total += PanelHorizontalPadding * 2 + 2;
            }

            if (_lyricsEnabled)
            {
                int curLyricsW = (_lyricsAdaptiveWidth && _currentAdaptiveLyricsWidth > 0)
                    ? _currentAdaptiveLyricsWidth
                    : _lyricsWidth;
                total += curLyricsW + ((gc > 0 || ac > 0) ? 14 : 0);
            }
            else if (gc == 0 && ac == 0)
            {
                total = item;
            }

            return Math.Max(1, total);
        }

        private void MediaService_StateChanged(object? sender, TaskbarMediaSnapshot snapshot)
        {
            if (_isClosed) return;
            Interlocked.Exchange(ref _pendingMediaSnapshot, snapshot);
            if (Interlocked.Exchange(ref _mediaUpdateQueued, 1) != 0) return;
            PostToUi(() =>
            {
                Interlocked.Exchange(ref _mediaUpdateQueued, 0);
                var latest = Interlocked.Exchange(ref _pendingMediaSnapshot, null);
                if (latest != null) UpdateMediaUi(latest);
            });
        }

        private void VisualizerTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isPlaying || VisualizerBar1 == null || VisualizerBar2 == null || VisualizerBar3 == null || VisualizerBar4 == null)
                return;

            // 正弦相位驱动 + 二次缓动，比随机跳变更顺滑、更像真实频谱呼吸。
            _visualizerPhase += Math.PI * 2 * 0.19;
            AnimateVisualizerBar(VisualizerBar1, 3 + 7 * VisualizerWave(_visualizerPhase));
            AnimateVisualizerBar(VisualizerBar2, 3 + 9 * VisualizerWave(_visualizerPhase + 1.05));
            AnimateVisualizerBar(VisualizerBar3, 3 + 7 * VisualizerWave(_visualizerPhase + 2.10));
            AnimateVisualizerBar(VisualizerBar4, 3 + 8 * VisualizerWave(_visualizerPhase + 3.15));
        }

        private double _visualizerPhase;

        private static double VisualizerWave(double phase) => 0.5 - 0.5 * Math.Cos(phase);

        /// <summary>
        /// 条形高度必须从"屏幕上当前的值"起跳。XAML 里的 Height 是基值，直接写
        /// new DoubleAnimation(h, 130) 会让每次 Tick 都从那个固定基值弹向新值——
        /// 130ms 一轮下来，右下角这四根柱子就是肉眼可见的持续抖动。
        /// </summary>
        private static void AnimateVisualizerBar(System.Windows.Controls.Border bar, double height)
        {
            AnimateDouble(bar, FrameworkElement.HeightProperty, height, 130,
                new QuadraticEase { EasingMode = EasingMode.EaseInOut });
        }

        private void StopVisualizer()
        {
            foreach (var bar in new[] { VisualizerBar1, VisualizerBar2, VisualizerBar3, VisualizerBar4 })
            {
                if (bar == null) continue;
                AnimateDouble(bar, FrameworkElement.HeightProperty, 3, 180,
                    new QuadraticEase { EasingMode = EasingMode.EaseOut });
            }
        }

        private void UpdateMediaUi(TaskbarMediaSnapshot snapshot)
        {
            // Session callbacks can arrive from a background thread. Ignore queued
            // snapshots from an older song so their artwork cannot overwrite the new one.
            if (_isClosed || snapshot.TrackVersion < _lastTrackVersion)
                return;

            _lastTrackVersion = snapshot.TrackVersion;
            _lastSnapshot = snapshot;
            // Do not early-return when LyricText is null (it is detached during a
            // Rebuild). The other controls are still valid and must keep updating;
            // only the lyric-specific assignments below are guarded by a null check.
            _isPlaying = snapshot.IsPlaying && snapshot.HasSession;
            if (_isPlaying && _lyricsShowVisualizer)
            {
                if (_visualizerTimer == null)
                {
                    _visualizerTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
                    _visualizerTimer.Tick += VisualizerTimer_Tick;
                }
                if (!_visualizerTimer.IsEnabled)
                    _visualizerTimer.Start();
            }
            else
            {
                _visualizerTimer?.Stop();
                StopVisualizer();
            }

            // 检测切歌，重置封面与歌词状态
            var currentTrackKey = string.IsNullOrWhiteSpace(snapshot.TrackKey)
                ? $"{snapshot.Title}\u001f{snapshot.Artist}\u001f{snapshot.Album}"
                : snapshot.TrackKey;
            bool trackChanged = !string.Equals(_lastTrackKey, currentTrackKey, StringComparison.Ordinal);
            if (trackChanged)
            {
                _adaptiveLayoutDirty = true;
                _lastTrackKey = currentTrackKey;
                _lastLyricText = string.Empty;
                // 刻意不清空封面：把 Source 置 null 会让封面先闪成 fallback 音符
                // 图标、再闪回新封面，这正是切歌时"窗口闪一下"最刺眼的来源。
                // 新封面在同一帧内由下面的逻辑替换；真的没有封面时才回落图标。
            }

            // 专辑封面展示逻辑 (Lyricify 严谨标准)
            if (snapshot.Artwork is { Length: > 0 })
            {
                if (AlbumArtwork.Source == null || _lastArtwork == null ||
                    (!ReferenceEquals(_lastArtwork, snapshot.Artwork) && !_lastArtwork.AsSpan().SequenceEqual(snapshot.Artwork)))
                {
                    var bmp = LoadArtwork(snapshot.Artwork);
                    if (bmp != null)
                    {
                        _lastArtwork = snapshot.Artwork;
                        AlbumArtwork.Source = bmp;
                        AlbumArtwork.Visibility = Visibility.Visible;
                        ArtworkFallback.Visibility = Visibility.Collapsed;
                    }
                }
            }
            else if (trackChanged || !snapshot.HasSession)
            {
                // 没有新封面可用时才回落 fallback：切歌但拿不到封面的情况下，
                // 旧封面继续显示比闪一下图标更稳。
                _lastArtwork = null;
                AlbumArtwork.Source = null;
                AlbumArtwork.Visibility = Visibility.Collapsed;
                ArtworkFallback.Visibility = Visibility.Visible;
            }

            // ── 稳定显示状态机 ────────────────────────────────────────────────────
            // 1. 无媒体会话              → 占位提示
            // 2. 有同步歌词              → 当前歌词 + 翻译/曲目信息
            // 3. 前奏/间奏/尾奏/无歌词   → 歌名 + 歌手与专辑
            // 4. 下一句与时间            → 悬浮提示，副行不再短暂跳变
            // 5. 纯曲目信息模式          → 固定显示歌名
            // 宽度只在切歌、整份歌词到达或设置变化时计算，避免逐句重排。
            string primaryText;
            string compositeText;
            string iconGlyph;
            var artistAlbum = string.Join(" · ", new[] { snapshot.Artist, snapshot.Album }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

            if (!snapshot.HasSession)
            {
                // 没有媒体就不该有文案：占位提示（"等待媒体播放"）在开了"无歌曲时
                // 隐藏"的岛上根本看不到，在关掉它的岛上又只是一句噪音。留空即可。
                primaryText = string.Empty;
                iconGlyph = string.Empty;
                compositeText = string.Empty;
            }
            else if (_lyricsTrackInfoOnly && !string.IsNullOrWhiteSpace(snapshot.Title))
            {
                primaryText = snapshot.Title;
                iconGlyph = "\uE8D6";
                compositeText = artistAlbum;
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.CurrentLyric))
            {
                primaryText = snapshot.CurrentLyric;

                if (!string.IsNullOrWhiteSpace(snapshot.CurrentTranslation))
                {
                    iconGlyph = string.Empty;
                    compositeText = snapshot.CurrentTranslation;
                }
                else
                {
                    iconGlyph = "\uE8D6";
                    compositeText = artistAlbum;
                }
            }
            else
            {
                // 前奏/间奏/尾奏/无歌词统一回落到歌名，不再显示倒计时之类的临时提示。
                primaryText = string.IsNullOrWhiteSpace(snapshot.Title) ? "正在播放" : snapshot.Title;
                iconGlyph = "\uE8D6";
                compositeText = artistAlbum;
            }

            // 悬停（以及收起动画尚未走完）期间一律不碰宽度：改宽度会连带改任务栏
            // 上这个岛的位置，鼠标底下正在指的那块区域会整体位移一次。
            if (_lyricsAdaptiveWidth && _lyricsEnabled && !_interactionLocked)
            {
                // 切歌那一帧只换文字与封面，宽度留到下一轮轮询再算：同一帧里同时
                // 改窗口尺寸和全部内容，观感就是整个岛"闪"一下，而不是平稳换歌。
                if (!trackChanged)
                {
                    UpdateAdaptiveLyricsWidth(snapshot, primaryText, compositeText, _adaptiveLayoutDirty);
                    _adaptiveLayoutDirty = false;
                }
            }
            else if (!_lyricsAdaptiveWidth && LyricsHost.Width != _lyricsWidth)
            {
                LyricsHost.Width = _lyricsWidth;
                _currentAdaptiveLyricsWidth = _lyricsWidth;
                ApplyLayout();
            }

            // Lyricify 式行切换：新一句歌词轻微上滑+淡入。
            // 切歌走的是另一条路：直接定住，不做淡入（否则旧词一闪）。
            if (LyricText != null && !string.Equals(_lastLyricText, primaryText, StringComparison.Ordinal))
            {
                _lastLyricText = primaryText;
                LyricText.Text = primaryText;
                AnimateLyricLineTransition(isTrackChange: trackChanged);
            }

            // 2. 副行图标与文本更新。曲目信息缺失时整行留空，
            // 拿"多源同步歌词"这类音源标签冒充第二行歌词只会误导。
            // 悬停期间副行是隐藏的，此刻再切换它的可见性只会在正被指向的
            // 区域里制造布局变化与命中测试重算——这正是控制条反复进出
            // （表现为闪烁）的诱因。文本照常更新，可见性留到移出后生效。
            var hasSecondaryContent = !string.IsNullOrWhiteSpace(compositeText);
            if (MediaIcon != null && !_interactionLocked)
            {
                MediaIcon.Text = iconGlyph;
                // 不透明度而不是 Visibility：图标本身固定占了 14px，切歌时它出现
                // 或消失都不会让副行文字左右跳一格——那一下跳动就是"闪"。
                MediaIcon.Opacity = _lyricsShowSecondaryLine && hasSecondaryContent && !string.IsNullOrWhiteSpace(iconGlyph)
                    ? 1
                    : 0;
            }
            if (MediaText != null)
            {
                if (!string.Equals(_lastCompositeText, compositeText, StringComparison.Ordinal))
                {
                    _lastCompositeText = compositeText;
                    MediaText.Text = compositeText;
                }
                MediaText.FontSize = Math.Max(11.5, _lyricsFontSize - 2.5);
                if (!_interactionLocked)
                {
                    MediaText.Visibility = _lyricsShowSecondaryLine && hasSecondaryContent
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }

            // Rebuild() owns the configured type size; individual lines use ellipsis
            // and the full tooltip instead of resizing on every poll.
            if (LyricText != null)
                LyricText.FontSize = _lyricsFontSize;

            // Tooltip 只在内容真正变化时赋值，且不在悬停时更新：内容变化会让已
            // 弹出的提示框重排，也可能与鼠标停留区域争夺指向状态。
            if (!_interactionLocked)
            {
                var tooltipText = string.Join("\n", new[]
                {
                    $"🎵 歌曲：{snapshot.Title}",
                    $"👤 歌手：{snapshot.Artist}",
                    string.IsNullOrWhiteSpace(snapshot.Album) ? null : $"💽 专辑：{snapshot.Album}",
                    string.IsNullOrWhiteSpace(snapshot.CurrentLyric) ? null : $"📝 歌词：{snapshot.CurrentLyric}",
                    string.IsNullOrWhiteSpace(snapshot.CurrentTranslation) ? null : $"🌐 翻译：{snapshot.CurrentTranslation}",
                    string.IsNullOrWhiteSpace(snapshot.NextText) ? null : $"⏩ 下一句：{snapshot.NextText}",
                    string.IsNullOrWhiteSpace(snapshot.TimeString) ? null : $"⏱ 进度：{snapshot.TimeString}",
                    $"🏷 音源：{snapshot.SourceText}",
                    "🖱️ 点击：唤醒置顶当前播放器"
                }.Where(s => s != null));
                if (!string.Equals(_lastTooltipText, tooltipText, StringComparison.Ordinal))
                {
                    _lastTooltipText = tooltipText;
                    LyricsHost.ToolTip = tooltipText;
                }
            }

            // 进度条：两次轮询之间用补间动画衔接。切歌时直接归零而不是播一段
            // "从 100% 缩回 0%"的倒退动画——倒退比瞬移更显眼，那一下正是闪烁感
            // 的来源之一。歌词与封面已经在同一帧换掉了，进度条跟上即可。
            // 无头/预览路径下 LyricsProgress 可能为 null（Rebuild():745 就是这么判的）。
            // 不判空会让 NRE 从 BeginInvoke 里逃出，直接崩掉 UI 线程。
            if (LyricsProgress != null)
            {
                if (trackChanged)
                {
                    LyricsProgress.BeginAnimation(ProgressBar.ValueProperty, null);
                    LyricsProgress.Value = snapshot.Progress;
                }
                else
                {
                    // 必须带上当前显示值作为起点，否则每 250ms 重启动画都会让进度条
                    // 从旧的基值猛冲到新值，看上去就是持续抖动。
                    AnimateDouble(LyricsProgress, ProgressBar.ValueProperty, snapshot.Progress, 600);
                }
            }

            if (PlayPauseButton != null)
            {
                var playGlyph = snapshot.IsPlaying ? "\uE769" : "\uE768";
                if (!Equals(PlayPauseButton.Content, playGlyph))
                    PlayPauseButton.Content = playGlyph;
                // 播放中给播放键一抹强调色，状态一眼可辨。只在状态翻转时取，
                // 避免每次轮询都重设资源并让按钮重新布局。
                if (_playPauseAccent != snapshot.IsPlaying)
                {
                    _playPauseAccent = snapshot.IsPlaying;
                    PlayPauseButton.SetResourceReference(Control.ForegroundProperty,
                        snapshot.IsPlaying ? "GlassAccentBrush" : "GlassTextPrimaryBrush");
                }
            }
            if (LyricsControls != null && !_interactionLocked)
            {
                // 只在真正翻转时写入：IsEnabled 会级联到三个按钮（模板里禁用态
                // 降到 0.3 不透明），某些播放器的 SMTC 能力上报不稳定，来回切
                // 会让整条控制栏在鼠标底下闪成半透明。
                bool canControl = snapshot.HasSession && snapshot.CanControlPlayback;
                if (LyricsControls.IsEnabled != canControl)
                    LyricsControls.IsEnabled = canControl;
            }

            // 无媒体时收起歌词岛（乃至整个任务栏条），有媒体再贴回。
            ApplyLyricsPresence();
        }

        public void SetPreviewMedia(TaskbarMediaSnapshot snapshot) => UpdateMediaUi(snapshot);

        /// <summary>
        /// Lyricify 式歌词行切换动画：主副行轻微上滑并淡入。
        /// 悬停显示控制器时文本已隐藏，跳过动画以避免与悬停渐隐互相打断。
        /// </summary>
        private void AnimateLyricLineTransition(bool isTrackChange = false)
        {
            if (_interactionLocked || LyricText == null) return;

            if (isTrackChange)
            {
                // 换歌不是同一首歌里换行：整行替换时再播一遍"上滑淡入"，
                // 看到的不是过渡而是旧词一闪。直接定住，动画只留给逐句过渡。
                LyricText.BeginAnimation(OpacityProperty, null);
                LyricText.Opacity = 1;
                MediaText?.BeginAnimation(OpacityProperty, null);
                if (MediaText != null) MediaText.Opacity = 1;
                if (LyricsTranslate != null)
                {
                    LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    LyricsTranslate.Y = 0;
                }
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(240);
            // From/To 必须显式写出：只给 To 的动画会读 XAML 上的基值当起点，
            // 而基值恒为 0 ——那样这条位移永远不生效，只剩淡入在动。
            AnimateDouble(LyricsTranslate, TranslateTransform.YProperty, 0, 240, ease, fromOverride: 7);
            LyricText.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            MediaText?.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.2, 1, duration) { EasingFunction = ease });
        }

        public void SetPreviewControlsVisible(bool visible)
        {
            // 预览形态由调用方给出，不受当前会话是否可控制影响。
            _controlsAvailable = true;
            _isLyricsHovered = visible;
            ShowLyricsControls(visible, animate: false);
            _interactionLocked = visible;
        }

        private static void ClearOpacityAnimation(UIElement? element)
            => element?.BeginAnimation(OpacityProperty, null);

        private void AnimateArtworkScale(double scale)
        {
            if (ArtworkScale == null) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            AnimateDouble(ArtworkScale, ScaleTransform.ScaleXProperty, scale, 140, ease);
            AnimateDouble(ArtworkScale, ScaleTransform.ScaleYProperty, scale, 140, ease);
        }

        /// <summary>
        /// 关键：只给 To 值 的 DoubleAnimation 会从属性"基值"起步，而不是屏幕上
        /// 当前的显示值。每 250ms 轮询重启动画时，画面就会先跳回旧起点再冲向
        /// 目标——肉眼看到的正是"闪烁"。这里显式把当前显示值作为起点，
        /// 让任何时刻被打断的动画都能平滑接续。
        /// </summary>
        private static void AnimateDouble(DependencyObject target, DependencyProperty property, double to,
            double milliseconds, IEasingFunction? ease = null, double? fromOverride = null)
        {
            // UIElement 并不继承 Animatable，但两者都实现了 IAnimatable,
            // BeginAnimation 由该接口提供。
            if (target is not IAnimatable animatable) return;
            var from = fromOverride ?? (target.GetValue(property) is double current ? current : to);
            if (Math.Abs(from - to) < 0.001)
            {
                // 目标一致就不重启动画，避免无谓的重排与命中测试重算。
                animatable.BeginAnimation(property, null);
                target.SetValue(property, to);
                return;
            }
            animatable.BeginAnimation(property,
                new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease });
        }

        private static void AnimateHoverValue(DependencyObject target, DependencyProperty property, double to,
            double duration, double fullDistance, IEasingFunction ease, double? fromOverride = null)
        {
            var from = fromOverride ?? (target.GetValue(property) is double current ? current : to);
            var fraction = Math.Clamp(Math.Abs(from - to) / Math.Max(0.001, fullDistance), 0, 1);
            AnimateDouble(target, property, to, Math.Max(16, duration * fraction), ease, fromOverride);
        }

        /// <summary>
        /// Lyricify 式悬停交互：歌词**横向让位**给控制条，而不是被整块抹掉。
        /// 歌词始终保持可读，控制条浮在上层从右侧滑入补位。
        /// </summary>
        private void ShowLyricsControls(bool hovered, bool animate = true)
        {
            // 控制条不可用时（无会话 / 播放器不可控制 / 设置里关掉了）就不要让歌词
            // 让位——否则鼠标停在歌词上会凭空抹掉整片文字，却什么也换不来。
            bool visible = hovered && _controlsAvailable;

            // 状态去重：重复的 MouseEnter 不再重启动画，否则正在播放的淡入会被
            // 一次次从头开始，看起来就是按钮在闪。
            if (_hoverControlsVisible == visible && animate) return;
            _hoverControlsVisible = visible;

            var ease = new SineEase { EasingMode = EasingMode.EaseOut };
            // Keep glyphs at their final scale throughout the transition. Scaling
            // transparent text and its effects forces costly raster updates.
            SetScale(LyricsControlsScale, 1);
            SetScale(LyricsTextScale, 1);
            if (!animate)
            {
                ClearOpacityAnimation(LyricsControlsGlass);
                ClearOpacityAnimation(LyricsTextPanel);
                LyricsControlsTranslate?.BeginAnimation(TranslateTransform.XProperty, null);

                if (LyricsControlsGlass != null)
                {
                    LyricsControlsGlass.IsHitTestVisible = visible;
                    LyricsControlsGlass.Opacity = visible ? 1 : 0;
                }
                if (LyricsControlsTranslate != null) LyricsControlsTranslate.X = visible ? 0 : 6;
                ReserveSpaceForLyricsControls(visible, animate: false);
                return;
            }

            if (LyricsControlsGlass != null)
            {
                LyricsControlsGlass.IsHitTestVisible = visible;
                AnimateHoverValue(LyricsControlsGlass, OpacityProperty, visible ? 1 : 0,
                    visible ? 150 : 65, 1, ease);
            }
            if (LyricsControlsTranslate != null)
                AnimateHoverValue(LyricsControlsTranslate, TranslateTransform.XProperty, visible ? 0 : 6,
                    visible ? 160 : 100, 6, ease);

            // 歌词只是向左避让，与控制台动作同步进行。
            ReserveSpaceForLyricsControls(visible, animate: true);
        }

        // 让位后歌词至少还要有这么多可用宽度，否则让位等于把歌词删了。
        private const double ReserveMinimumTextWidth = 64;
        // 空间不够时的退让：歌词不完全消失，只是压暗，控制条浮在其上。
        private const double ReserveDimmedOpacity = 0.3;

        /// <summary>
        /// Lay out the final text width once, then translate from the previous
        /// visual position. Animating Margin would remeasure/ellipsize each frame.
        /// </summary>
        private void ReserveSpaceForLyricsControls(bool reserve, bool animate)
        {
            if (LyricsTextPanel == null || LyricsTranslate == null) return;

            double inset = 0;
            if (reserve && LyricsControlsGlass != null)
            {
                inset = RenderedWidth(LyricsControlsGlass) + 8;
                // 歌词区可用宽度 = 岛宽 - 左右内边距 - 封面列及其间隔
                double available = RenderedWidth(LyricsHost) - 15 - RenderedWidth(ArtworkBorder) - 10;
                if (available - inset < ReserveMinimumTextWidth) inset = 0;
            }

            var ease = new SineEase { EasingMode = EasingMode.EaseOut };
            var to = new Thickness(0, 0, inset, 0);
            // Preserve the current rendered position even when the pointer
            // reverses midway through a transition.
            var fromX = LyricsTranslate.X + inset - LyricsTextPanel.Margin.Right;
            var fullDistance = Math.Max(inset, LyricsTextPanel.Margin.Right);
            LyricsTextPanel.BeginAnimation(FrameworkElement.MarginProperty, null);
            LyricsTextPanel.Margin = to;
            if (animate)
            {
                // Set the inverse offset immediately; a newly attached WPF
                // animation clock may not publish its From value until a tick.
                LyricsTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                LyricsTranslate.X = fromX;
                AnimateHoverValue(LyricsTranslate, TranslateTransform.XProperty, 0, reserve ? 160 : 100,
                    fullDistance, ease, fromOverride: fromX);
                // 只有控制条进来又没能腾让位时才压暗，保证歌词任何时候都可读。
                double opacity = reserve && inset == 0 ? ReserveDimmedOpacity : 1;
                AnimateHoverValue(LyricsTextPanel, OpacityProperty, opacity, reserve ? 150 : 100, 0.7, ease);
                return;
            }

            LyricsTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            LyricsTranslate.X = 0;
            LyricsTextPanel.BeginAnimation(OpacityProperty, null);
            LyricsTextPanel.Opacity = reserve && inset == 0 ? ReserveDimmedOpacity : 1;
        }

        /// <summary>
        /// 元素的实际占用宽度。尚未完成布局（例如还没有被 Arrange 过）时退一步
        /// 取测量宽度，保证让位计算在任何时机都能拿到真实尺寸。
        /// </summary>
        private static double RenderedWidth(FrameworkElement? element)
        {
            if (element == null) return 0;
            if (element.ActualWidth > 0) return element.ActualWidth;
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return element.DesiredSize.Width;
        }

        private static void SetScale(ScaleTransform? transform, double value)
        {
            if (transform == null) return;
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.ScaleX = value;
            transform.ScaleY = value;
        }

        private void UpdateAdaptiveLyricsWidth(TaskbarMediaSnapshot snapshot, string primaryText, string secondaryText, bool force)
        {
            var layout = _lyricsTrackInfoOnly ? null : snapshot.LayoutLyrics;
            // A whole lyric sheet is measured once when it arrives. Individual
            // lines, preview text and the advancing time never resize the band.
            if (!force && (layout == null || layout.Count == 0 || ReferenceEquals(layout, _lastLayoutLyrics))) return;
            _lastLayoutLyrics = layout;
            double secondaryFontSize = Math.Max(11.5, _lyricsFontSize - 2.5);
            double primaryWidth = MeasureTextWidth(primaryText, _lyricsFontSize, FontWeights.SemiBold);
            double secondaryWidth = string.IsNullOrWhiteSpace(secondaryText)
                ? 0
                : MeasureTextWidth(secondaryText, secondaryFontSize, FontWeights.Medium) + 16;
            if (layout != null)
            {
                foreach (var line in layout)
                {
                    primaryWidth = Math.Max(primaryWidth, MeasureTextWidth(line.Text, _lyricsFontSize, FontWeights.SemiBold));
                    secondaryWidth = Math.Max(secondaryWidth,
                        MeasureTextWidth(line.Translation ?? string.Empty, secondaryFontSize, FontWeights.Medium) + 16);
                }
            }
            if (!_lyricsShowSecondaryLine) secondaryWidth = 0;
            int desiredWidth = (int)Math.Ceiling(Math.Max(primaryWidth, secondaryWidth) + 82);

            int currentLyricsWidth = Math.Max(1, _currentAdaptiveLyricsWidth);
            int nonLyricsWidth = Math.Max(0, CalcWidth() - currentLyricsWidth);
            int taskbarAvailable = _embedder.GetMaximumWidthInDips();
            // On small displays the remaining taskbar space can be below the
            // normal 240px minimum. Math.Clamp throws when min > max, so keep
            // the bounds ordered and let the host apply its own clipping.
            int availableLyricsWidth = Math.Max(1, taskbarAvailable - nonLyricsWidth);
            int maximumLyricsWidth = Math.Clamp(availableLyricsWidth, AdaptiveMinimumLyricsWidth, 720);
            ApplyAdaptiveLyricsWidth(Math.Clamp(desiredWidth, AdaptiveMinimumLyricsWidth, maximumLyricsWidth));
        }

        private void ApplyAdaptiveLyricsWidth(int width)
        {
            width = Math.Clamp(width, AdaptiveMinimumLyricsWidth, 720);
            // 阈值放宽到 10px：4px 以内的差异在任务栏上只会表现为持续的微抖动，
            // 而每次写入都会触发一次嵌入层的 SetWindowPos 重定位。
            if (Math.Abs(width - _currentAdaptiveLyricsWidth) < 10)
                return;

            // 切歌与"歌词刚抓取到"通常只隔几百毫秒，两者各自触发一次就会 resize
            // 两次、在任务栏上闪两下。合并进一个稳定窗口，只应用最后一次。
            _pendingAdaptiveWidth = width;
            if (_widthDebounceTimer == null)
            {
                _widthDebounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(AdaptiveWidthDebounceMs)
                };
                _widthDebounceTimer.Tick += (_, _) =>
                {
                    _widthDebounceTimer.Stop();
                    CommitAdaptiveLyricsWidth();
                };
            }

            _widthDebounceTimer.Stop();
            _widthDebounceTimer.Start();
        }

        private void CommitAdaptiveLyricsWidth()
        {
            int width = Math.Clamp(_pendingAdaptiveWidth, AdaptiveMinimumLyricsWidth, 720);
            if (Math.Abs(width - _currentAdaptiveLyricsWidth) < 10)
                return;

            _currentAdaptiveLyricsWidth = width;
            LyricsHost.Width = width;
            ApplyLayout();
        }

        private double MeasureTextWidth(string text, double fontSize, FontWeight fontWeight)
        {
            if (string.IsNullOrEmpty(text) || LyricText == null) return 0;
            try
            {
                var typeface = new Typeface(LyricText.FontFamily, LyricText.FontStyle, fontWeight, LyricText.FontStretch);
                var formattedText = new FormattedText(
                    text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Brushes.White,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                return formattedText.WidthIncludingTrailingWhitespace;
            }
            catch
            {
                return text.Length * (fontSize * 0.95);
            }
        }

        private static BitmapImage? LoadArtwork(byte[]? bytes)
        {
            if (bytes is not { Length: > 0 }) return null;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 96;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }

        // ── 悬停仲裁 ─────────────────────────────────────────────────────────
        // 鼠标事件在这里只被当作"去看一眼"的信号，真正的判决由光标物理坐标复核。
        // 原因：IsMouseOver 会被 tooltip 弹出、透明度/宽度微调、命中测试重算弄丢；
        // 而一旦 WPF 认为指针已经离开，鼠标若此后一动不动，就再也不会有 MouseEnter
        // 来纠偏——控制条于是会在指针还压在歌词上的时候自己收起，再被下一次微不
        // 足道的动作重新唤出。肉眼看到的"抖动"就是这条链。
        private const int HoverProbeIntervalMs = 16;
        private const double HoverHitTolerancePx = 2.0;
        // 控制条彻底淡出后才允许碰布局，覆盖退出动画的最长时长。
        private const int HoverUnlockDelayMs = 160;

        private void LyricsHost_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_isLyricsHovered)
            {
                return;
            }
            SetHoverActive(true);
        }

        private void LyricsHost_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isLyricsHovered) return;
            // A genuine departure starts fading now. A tooltip/layout-generated
            // MouseLeave is ignored while the physical cursor remains inside.
            if (!IsPointerOverLyricsHost()) SetHoverActive(false);
            else ScheduleHoverProbe();
        }

        private void SetHoverActive(bool active)
        {
            StopHoverProbe();
            StopHoverUnlock();
            _isLyricsHovered = active;
            _interactionLocked = true;

            if (active)
            {
                _controlsAvailable = _lyricsShowControls && LyricsControls != null && LyricsControls.IsEnabled;
                AnimateArtworkScale(1.03);
                ShowLyricsControls(true);
                ScheduleHoverProbe();
                return;
            }

            // 冻结要一直延续到收起动画走完：中途若允许宽度/副行可见性重算，
            // 正在播的退出动画会被一次布局改动打断，画面就"跳"一下。
            AnimateArtworkScale(1.0);
            ShowLyricsControls(false);
            ScheduleHoverUnlock();
        }

        private void ScheduleHoverProbe()
        {
            if (_hoverProbeTimer == null)
            {
                _hoverProbeTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input)
                {
                    Interval = TimeSpan.FromMilliseconds(HoverProbeIntervalMs)
                };
                _hoverProbeTimer.Tick += (_, _) =>
                {
                    // 还在歌词岛上就继续守着。注意这里不能就此停掉计时器——
                    // IsMouseOver 一旦丢失就不会再有 MouseLeave 来通知了。
                    if (IsPointerOverLyricsHost()) return;
                    SetHoverActive(false);
                };
            }

            _hoverProbeTimer.Stop();
            _hoverProbeTimer.Start();
        }

        private void StopHoverProbe() => _hoverProbeTimer?.Stop();

        private void ScheduleHoverUnlock()
        {
            if (_hoverUnlockTimer == null)
            {
                _hoverUnlockTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(HoverUnlockDelayMs)
                };
                _hoverUnlockTimer.Tick += (_, _) =>
                {
                    _hoverUnlockTimer.Stop();
                    if (_isLyricsHovered) return;
                    _interactionLocked = false;
                };
            }

            _hoverUnlockTimer.Stop();
            _hoverUnlockTimer.Start();
        }

        private void StopHoverUnlock() => _hoverUnlockTimer?.Stop();

        /// <summary>
        /// 用光标真实坐标而不是 IsMouseOver 判定悬停，屏蔽布局与透明度变化带来的假事件。
        /// PointToScreen already returns physical screen coordinates. Transform
        /// both corners so DPI, negative monitor origins and Viewbox scaling are
        /// included exactly once.
        /// </summary>
        private bool IsPointerOverLyricsHost()
        {
            var bounds = GetLyricsScreenBounds();
            var cursor = _readCursorPosition();
            if (bounds == null || cursor == null) return false;
            return IsPointerInsideBand(cursor.Value.X, bounds.Value.Left, bounds.Value.Width,
                cursor.Value.Y, bounds.Value.Top, bounds.Value.Height);
        }

        internal Rect? GetLyricsScreenBounds()
        {
            if (!IsVisible || !LyricsHost.IsVisible || PresentationSource.FromVisual(LyricsHost) == null) return null;
            try
            {
                var topLeft = LyricsHost.PointToScreen(new Point(0, 0));
                var bottomRight = LyricsHost.PointToScreen(new Point(LyricsHost.ActualWidth, LyricsHost.ActualHeight));
                var bounds = new Rect(topLeft, bottomRight);
                return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
            }
            catch
            {
                return null;
            }
        }

        private static Point? ReadSystemCursorPosition() =>
            GetCursorPos(out var cursor) ? new Point(cursor.X, cursor.Y) : null;

        /// <summary>
        /// 纯几何判定，坐标均为物理像素。边缘留一点容差，避免指针停在边框上时
        /// 亚像素抖动反复触发进出。
        /// </summary>
        internal static bool IsPointerInsideBand(double cursorX, double left, double width,
            double cursorY, double top, double height, double tolerance = HoverHitTolerancePx)
        {
            return cursorX >= left - tolerance && cursorX <= left + width + tolerance
                && cursorY >= top - tolerance && cursorY <= top + height + tolerance;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        private static void AnimateButtonClick(Button? btn)
        {
            if (btn == null) return;
            var scale = new ScaleTransform(0.88, 0.88);
            btn.RenderTransformOrigin = new Point(0.5, 0.5);
            btn.RenderTransform = scale;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }
            });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }
            });
        }

        private bool _mediaBusy;

        private async void PreviousTrack_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_mediaBusy) return;
            _mediaBusy = true;
            try
            {
                AnimateButtonClick(sender as Button);
                await _mediaService.SkipPreviousAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Previous media control failed.", ex);
            }
            finally
            {
                _mediaBusy = false;
            }
        }

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_mediaBusy) return;
            _mediaBusy = true;
            try
            {
                AnimateButtonClick(sender as Button);
                await _mediaService.TogglePlayPauseAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Play/pause media control failed.", ex);
            }
            finally
            {
                _mediaBusy = false;
            }
        }

        private async void NextTrack_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_mediaBusy) return;
            _mediaBusy = true;
            try
            {
                AnimateButtonClick(sender as Button);
                await _mediaService.SkipNextAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Next media control failed.", ex);
            }
            finally
            {
                _mediaBusy = false;
            }
        }

        private void LyricsHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            float step = e.Delta > 0 ? 0.02f : -0.02f;
            float? newVol = SystemVolumeService.AdjustVolume(step);
            bool isMuted = SystemVolumeService.IsMuted();
            ShowSeparatedVolumeHud(newVol ?? SystemVolumeService.GetVolume(), isMuted);
        }

        private void ShowSeparatedVolumeHud(float volume, bool isMuted)
        {
            try
            {
                // 音量条锚在专辑图片的正上方居中：原先按整个歌词岛中心计算，
                // 封面偏右时浮窗会压在歌词上。取不到封面尺寸就退回整岛居中。
                Point topLeft = LyricsHost.PointToScreen(new Point(0, 0));
                Point center = new Point(topLeft.X + (LyricsHost.ActualWidth / 2), topLeft.Y);
                double anchorTop = topLeft.Y;

                if (ArtworkBorder != null && ArtworkBorder.ActualWidth > 0)
                {
                    Point artworkTopLeft = ArtworkBorder.PointToScreen(new Point(0, 0));
                    center = new Point(artworkTopLeft.X + (ArtworkBorder.ActualWidth / 2), artworkTopLeft.Y);
                    anchorTop = artworkTopLeft.Y;
                }

                _volumeFlyout.ShowVolume(center, anchorTop, volume, isMuted);

                if (_volumeHideTimer == null)
                {
                    _volumeHideTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
                    _volumeHideTimer.Tick += (s, e) =>
                    {
                        _volumeHideTimer.Stop();
                        _volumeFlyout.HideFlyout();
                    };
                }
                _volumeHideTimer.Stop();
                _volumeHideTimer.Start();
            }
            catch { }
        }

        private void LyricsHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (LyricsControlsGlass != null && LyricsControlsGlass.IsMouseOver)
                return;
            ActivateActivePlayer();
        }

        public static void ActivateActivePlayer()
        {
            try
            {
                var knownProcs = new[]
                {
                    "cloudmusic", "qqmusic", "kugou", "kwmusic",
                    "spotify", "yesplaymusic", "applemusic", "foobar2000",
                    "aimp", "potplayer64", "potplayermini64"
                };

                foreach (var name in knownProcs)
                {
                    var procs = System.Diagnostics.Process.GetProcessesByName(name);
                    try
                    {
                        foreach (var p in procs)
                        {
                            var hWnd = p.MainWindowHandle;
                            if (hWnd != IntPtr.Zero)
                            {
                                if (IsIconic(hWnd))
                                {
                                    ShowWindow(hWnd, 9 /* SW_RESTORE */);
                                }
                                else
                                {
                                    ShowWindow(hWnd, 5 /* SW_SHOW */);
                                }
                                SetForegroundWindow(hWnd);
                                return;
                            }
                        }
                    }
                    finally
                    {
                        foreach (var process in procs)
                            process.Dispose();
                    }
                }
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        // --- Glass ---

        private static bool IsSystemInLightTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null)
                {
                    var val = key.GetValue("SystemUsesLightTheme");
                    if (val != null)
                        return Convert.ToInt32(val) != 0;
                }
            }
            catch { }
            return false;
        }

        internal void SetPreviewLyricsEnabled(bool enabled)
        {
            _lyricsEnabled = enabled;
            _lyricsShowVisualizer = false;
            // 预览/测试路径绝不能把窗口贴到用户真实任务栏上。
            SuppressAutoEmbed = true;
            Rebuild();
        }

        /// <summary>
        /// 为 true 时 ApplyLyricsPresence 只改歌词岛可见性，不执行嵌入/撤下。
        /// UiCapture 等无头预览路径使用。
        /// </summary>
        internal bool SuppressAutoEmbed { get; set; }

        private void UpdateGlass()
        {
            if (GlassBorder == null) return;
            var surface = GameAvatarContainer ?? GlassBorder;
            surface.SetResourceReference(Border.BackgroundProperty,
                _glassEnabled ? "FloatingSurfaceBrush" : "FloatingSolidBrush");
            surface.SetResourceReference(Border.BorderBrushProperty, "FloatingBorderBrush");
            if (!_lyricsTransparentBackground)
            {
                LyricsHost.SetResourceReference(Border.BackgroundProperty,
                    _glassEnabled ? "FloatingSurfaceBrush" : "FloatingSolidBrush");
            }
        }

        // --- Icon Brushes (cached) ---

        private static readonly Brush _btnBrush = CreateFrozenBrush(50, 20, 35);
        private static readonly Brush _hoverBrush = CreateFrozenBrush(90, 45, 70);
        private static readonly Brush _activeBrush = CreateFrozenBrush(80, 50, 60);
        private static readonly Brush _currentBorderBrush = CreateFrozenSolidBrush(0x6A, 0xA7, 0xFF, 255);
        private static Brush _normalBorderBrush => Application.Current?.TryFindResource("FloatingBorderBrush") as Brush ?? Brushes.Transparent;
        private static readonly Brush _hoverBorderBrush = CreateFrozenSolidBrush(106, 167, 255, 210);
        private static readonly Brush _bindingBorderBrush = CreateFrozenSolidBrush(101, 214, 166, 190);

        private static Brush CreateFrozenBrush(byte a1, byte a2, byte a3)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a1, 106, 167, 255));
            brush.Freeze();
            return brush;
        }

        private static Brush CreateFrozenSolidBrush(byte r, byte g, byte b, byte a)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Brush BtnBrush() => Application.Current?.TryFindResource("FloatingButtonBrush") as Brush ?? _btnBrush;
        private static Brush HoverBrush() => Application.Current?.TryFindResource("FloatingHoverBrush") as Brush ?? _hoverBrush;
        private static Brush ActiveBrush() => _activeBrush;

        // --- Create Buttons ---

        private Border MakeGameBtn(GameListViewModel game)
        {
            int sz = _iconSize;
            int radius = Math.Max(4, (int)Math.Round(sz * 0.26));

            var border = new RoundedClipBorder
            {
                Width = sz, Height = sz,
                CornerRadius = new CornerRadius(radius),
                BorderBrush = new SolidColorBrush(game.HasBinding
                    ? Color.FromArgb(190, 101, 214, 166)
                    : Color.FromArgb(255, 54, 74, 99)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = $"{game.GameName}\n点击启动 ({game.BindingAccountName ?? "未绑定"})",
                Tag = game,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };

            var path = !string.IsNullOrEmpty(game.IconPath)
                ? game.IconPath
                : _accountManager.GetSteamService().GetGameIconPath(game.AppId);

            var imgBrush = LoadImageBrush(path, sz);
            Border overlay;
            if (imgBrush != null)
            {
                border.Background = imgBrush;
                overlay = new Border
                {
                    CornerRadius = new CornerRadius(radius),
                    Background = Brushes.Transparent
                };
            }
            else
            {
                border.Background = BtnBrush();
                overlay = new Border
                {
                    CornerRadius = new CornerRadius(radius),
                    Background = Brushes.Transparent,
                    Child = FallbackIcon(sz)
                };
            }
            border.Child = overlay;

            border.MouseEnter += (s, e) =>
            {
                overlay.Background = game.HasBinding ? ActiveBrush() : HoverBrush();
                border.BorderBrush = _hoverBorderBrush;
                if (border.RenderTransform is ScaleTransform sc)
                {
                    var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    sc.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                    sc.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                }
            };
            border.MouseLeave += (s, e) =>
            {
                overlay.Background = Brushes.Transparent;
                border.BorderBrush = game.HasBinding ? _bindingBorderBrush : _normalBorderBrush;
                if (border.RenderTransform is ScaleTransform sc)
                {
                    var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    sc.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                    sc.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                }
            };
            border.MouseRightButtonDown += (s, e) =>
            {
                e.Handled = true;
                ShowContextMenu();
            };

            return border;
        }

        private Border MakeQuickLaunchBtn(QuickLaunchItem item)
        {
            int sz = _iconSize;
            int radius = Math.Max(4, (int)Math.Round(sz * 0.26));

            var border = new RoundedClipBorder
            {
                Width = sz, Height = sz,
                CornerRadius = new CornerRadius(radius),
                BorderBrush = _normalBorderBrush,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = $"{item.Name}\n{item.ExecutablePath}",
                Tag = item,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };

            var imgBrush = LoadImageBrush(item.IconPath, sz);
            Border overlay;
            if (imgBrush != null)
            {
                border.Background = imgBrush;
                overlay = new Border
                {
                    CornerRadius = new CornerRadius(radius),
                    Background = Brushes.Transparent
                };
            }
            else
            {
                border.Background = BtnBrush();
                overlay = new Border
                {
                    CornerRadius = new CornerRadius(radius),
                    Background = Brushes.Transparent,
                    Child = FallbackIcon(sz, "\uE945")
                };
            }
            border.Child = overlay;

            border.MouseEnter += (s, e) =>
            {
                overlay.Background = HoverBrush();
                border.BorderBrush = _hoverBorderBrush;
                if (border.RenderTransform is ScaleTransform sc)
                {
                    var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    sc.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                    sc.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                }
            };
            border.MouseLeave += (s, e) =>
            {
                overlay.Background = Brushes.Transparent;
                border.BorderBrush = _normalBorderBrush;
                if (border.RenderTransform is ScaleTransform sc)
                {
                    var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    sc.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                    sc.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                }
            };
            border.MouseRightButtonDown += (s, e) =>
            {
                e.Handled = true;
                ShowContextMenu();
            };

            return border;
        }

        private Border MakeAvatarBtn(SteamAccount account)
        {
            int sz = _iconSize;
            int r = _iconRadius;
            bool isCur = AccountManager.AreSameAccount(account, _accountManager.CurrentAccount);

            var border = new RoundedClipBorder
            {
                Width = sz, Height = sz,
                CornerRadius = new CornerRadius(r),
                BorderBrush = isCur ? _currentBorderBrush : _normalBorderBrush,
                BorderThickness = isCur ? new Thickness(2.5) : new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                Focusable = true,
                ToolTip = $"{account.PersonaName}\n左键或 Enter：切换账号\n右键或 Ctrl+Enter：强制重启 Steam 并切换",
                Tag = account,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };

            var imgBrush = LoadImageBrush(account.AvatarPath, sz);
            Border overlay;
            if (imgBrush != null)
            {
                border.Background = imgBrush;
                overlay = new Border
                {
                    CornerRadius = new CornerRadius(r),
                    Background = Brushes.Transparent
                };
            }
            else
            {
                border.Background = BtnBrush();
                overlay = new Border
                {
                    CornerRadius = new CornerRadius(r),
                    Background = Brushes.Transparent,
                    Child = FallbackText(Initial(account), sz)
                };
            }
            border.Child = overlay;
            KeyboardNavigation.SetIsTabStop(border, true);
            System.Windows.Automation.AutomationProperties.SetName(border, $"Steam 账号 {account.PersonaName}");
            System.Windows.Automation.AutomationProperties.SetHelpText(
                border,
                "按 Enter 切换账号；按 Ctrl+Enter 强制重启 Steam 并切换账号");

            border.KeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter && e.Key != Key.Space)
                    return;

                e.Handled = true;
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    ForceAccountRestartRequested?.Invoke(this, account);
                else
                    AccountSwitchRequested?.Invoke(this, account);
            };
            border.GotKeyboardFocus += (s, e) =>
            {
                border.BorderBrush = _hoverBorderBrush;
                border.BorderThickness = new Thickness(2.5);
            };
            border.LostKeyboardFocus += (s, e) =>
            {
                bool isCurrent = AccountManager.AreSameAccount(account, _accountManager.CurrentAccount);
                border.BorderBrush = isCurrent ? _currentBorderBrush : _normalBorderBrush;
                border.BorderThickness = isCurrent ? new Thickness(2.5) : new Thickness(1);
            };

            border.MouseRightButtonDown += (s, e) =>
            {
                e.Handled = true;
                ForceAccountRestartRequested?.Invoke(this, account);
            };
            border.MouseEnter += (s, e) =>
            {
                if (!AccountManager.AreSameAccount(account, _accountManager.CurrentAccount))
                {
                    overlay.Background = HoverBrush();
                    border.BorderBrush = _hoverBorderBrush;
                    if (border.RenderTransform is ScaleTransform sc)
                    {
                        var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        };
                        sc.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                        sc.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                    }
                }
            };
            border.MouseLeave += (s, e) =>
            {
                overlay.Background = Brushes.Transparent;
                bool isCurrent = AccountManager.AreSameAccount(account, _accountManager.CurrentAccount);
                border.BorderBrush = isCurrent ? _currentBorderBrush : _normalBorderBrush;
                border.BorderThickness = isCurrent ? new Thickness(2.5) : new Thickness(1);
                if (border.RenderTransform is ScaleTransform sc)
                {
                    var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    sc.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                    sc.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                }
            };

            return border;
        }

        // --- Helpers ---

        private static ImageBrush? LoadImageBrush(string? path, int size)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = Math.Max(size * 2, 128);
                bmp.EndInit();
                bmp.Freeze();
                return new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            catch { return null; }
        }

        private UIElement FallbackIcon(int sz, string icon = "\uE7FC") => new TextBlock()
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = sz > 36 ? 16 : 14,
            Foreground = Application.Current?.TryFindResource(_glassEnabled ? "GlassTextPrimaryBrush" : "FloatingTextPrimaryBrush") as Brush ?? Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        private UIElement FallbackText(string text, int sz) => new TextBlock()
        {
            Text = text,
            FontSize = sz > 36 ? 14 : 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Application.Current?.TryFindResource(_glassEnabled ? "GlassTextPrimaryBrush" : "FloatingTextPrimaryBrush") as Brush ?? Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static string Initial(SteamAccount a)
        {
            var n = !string.IsNullOrWhiteSpace(a.PersonaName) ? a.PersonaName : a.AccountName;
            return string.IsNullOrWhiteSpace(n) ? "S" : n[..1].ToUpperInvariant();
        }

    }
}
