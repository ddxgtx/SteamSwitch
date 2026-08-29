using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

namespace SteamSwitcher.Views
{
    public partial class TaskbarBandWindow : Window
    {
        private readonly AccountManager _accountManager;
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
        private int _lyricsVerticalOffset;
        private int _lyricsFontSize = 14;
        private string _lyricsFontFamily = "Segoe UI Variable Text";
        private bool _adaptiveLayoutDirty = true;
        private string _lastAdaptiveMeasureKey = string.Empty;
        private int _lastMeasuredAdaptiveLyricsWidth = 280;
        private int _adaptiveShrinkCandidateWidth;
        private DateTimeOffset _adaptiveShrinkCandidateSince = DateTimeOffset.MinValue;
        private string _lastPresentationMode = string.Empty;
        private System.Windows.Threading.DispatcherTimer? _visualizerTimer;
        private System.Windows.Threading.DispatcherTimer? _volumeHideTimer;
        private readonly TaskbarFlyoutWindow _volumeFlyout = new();
        private readonly Random _random = new();
        private bool _isPlaying;
        private bool _isLyricsHovered;
        private byte[]? _lastArtwork;
        private string _lastLyricText = string.Empty;
        private string _lastTrackKey = string.Empty;
        private long _lastTrackVersion = -1;
        private DateTimeOffset _trackChangedAt = DateTimeOffset.MinValue;
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
        public event EventHandler<bool>? ToggleTaskbarAppearanceRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler<IReadOnlyList<string>>? PanelItemOrderChanged;
        public event EventHandler<IReadOnlyList<string>>? AccountOrderChanged;
        public event EventHandler<int>? GameDeleteRequested;
        public event EventHandler<int>? QuickLaunchDeleteRequested;
        public event EventHandler<int>? AccountDeleteRequested;

        public TaskbarBandWindow(AccountManager accountManager)
        {
            InitializeComponent();
            AlbumArtwork.SizeChanged += (_, _) => UpdateArtworkClip();
            ArtworkBorder.SizeChanged += (_, _) => UpdateArtworkClip();
            _accountManager = accountManager;
            _embedder = new TaskbarEmbedder();
            _mediaService.StateChanged += MediaService_StateChanged;
            _currentAccountChangedHandler = (s, e) => Dispatcher.BeginInvoke(
                new Action(() => UpdateCurrentAccount(_accountManager.CurrentAccount)));
            _accountManager.CurrentAccountChanged += _currentAccountChangedHandler;
            _embedder.TaskbarCreated += (s, e) => Application.Current.Dispatcher.Invoke(Rebuild);
            Loaded += (s, e) => { UpdateGlass(); SetupDragDrop(); UpdateArtworkClip(); };
            Closing += (s, e) => Detach();
            Closed += (s, e) =>
            {
                if (_currentAccountChangedHandler != null)
                    _accountManager.CurrentAccountChanged -= _currentAccountChangedHandler;
                _mediaService.StateChanged -= MediaService_StateChanged;
                if (_visualizerTimer != null)
                {
                    _visualizerTimer.Stop();
                    _visualizerTimer.Tick -= VisualizerTimer_Tick;
                    _visualizerTimer = null;
                }
                _volumeHideTimer?.Stop();
                _volumeHideTimer = null;
                DetachViewModelEvents();
                try { _volumeFlyout.Close(); } catch { }
                _mediaService.Dispose();
                _embedder.Dispose();
            };
            MouseLeftButtonDown += TaskbarBandWindow_MouseLeftButtonDown;
            MouseRightButtonDown += TaskbarBandWindow_MouseRightButtonDown;
        }

        private void SetupDragDrop()
        {
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
                var index = _pinnedGames.FindIndex(x => x.AppId == game.AppId);
                if (index >= 0)
                    GameDeleteRequested?.Invoke(this, index);
            }
            else if (element.Tag is QuickLaunchItem item)
            {
                var index = _pinnedQuickLaunchItems.FindIndex(x => x.Id == item.Id);
                if (index >= 0)
                    QuickLaunchDeleteRequested?.Invoke(this, index);
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
            ShowContextMenu();
        }

        private void ShowContextMenu()
        {
            var menu = SharedContextMenuBuilder.Build(
                _viewModel?.Accounts,
                _viewModel?.SelectedAccount,
                showMainWindow: () => ShowMainWindowRequested?.Invoke(this, EventArgs.Empty),
                accountSelected: steamId =>
                {
                    var account = _pinnedAccounts.Find(a => a.SteamId == steamId);
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
                isTaskbarAppearanceEnabled: _viewModel?.TaskbarAppearanceEnabled ?? false,
                toggleTaskbarLyrics: enabled => ToggleTaskbarLyricsRequested?.Invoke(this, enabled),
                toggleTaskbarAppearance: enabled => ToggleTaskbarAppearanceRequested?.Invoke(this, enabled),
                exit: () => ExitRequested?.Invoke(this, EventArgs.Empty)
            );
            menu.IsOpen = true;
        }

        // --- Public API ---

        public bool IsPinned => _isPinned;

        public void SetViewModel(MainViewModel vm)
        {
            DetachViewModelEvents();

            _viewModel = vm;

            // Store event handler references for later unsubscription
            _pinnedGamesChangedHandler = (s, e) => Application.Current.Dispatcher.Invoke(() =>
            {
                _pinnedGames = _viewModel.GetPinnedGames();
                Rebuild();
            });
            _pinnedAccountsChangedHandler = (s, e) => Application.Current.Dispatcher.Invoke(() =>
            {
                _pinnedAccounts = _viewModel.GetPinnedAccounts();
                Rebuild();
            });
            _quickLaunchChangedHandler = (s, e) => Application.Current.Dispatcher.Invoke(() =>
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
            UpdateGlass();
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
            int pollingIntervalMs = 120,
            int verticalOffset = 0,
            int fontSize = 14,
            bool transparentBackground = true,
            string fontFamily = "Segoe UI Variable Text")
        {
            string normalizedFontFamily = string.IsNullOrWhiteSpace(fontFamily)
                ? "Segoe UI Variable Text"
                : fontFamily.Trim();
            int normalizedWidth = Math.Clamp(width, 240, 720);
            int normalizedFontSize = Math.Clamp(fontSize, 12, 18);
            _adaptiveLayoutDirty |= normalizedWidth != _lyricsWidth
                || normalizedFontSize != _lyricsFontSize
                || !string.Equals(normalizedFontFamily, _lyricsFontFamily, StringComparison.Ordinal)
                || adaptiveWidth != _lyricsAdaptiveWidth;

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
            _lyricsVerticalOffset = Math.Clamp(verticalOffset, -12, 12);
            _lyricsFontSize = normalizedFontSize;
            _lyricsFontFamily = normalizedFontFamily;
            if (!_lyricsAdaptiveWidth)
            {
                _currentAdaptiveLyricsWidth = _lyricsWidth;
            }
            _lastAdaptiveMeasureKey = string.Empty;
            _adaptiveShrinkCandidateWidth = 0;
            _adaptiveShrinkCandidateSince = DateTimeOffset.MinValue;
            _mediaService.OnlineLyricsEnabled = onlineLyrics;
            _mediaService.SourceQQEnabled = sourceQQ;
            _mediaService.SourceNeteaseEnabled = sourceNetease;
            _mediaService.SourceKugouEnabled = sourceKugou;
            _mediaService.SourceLrclibEnabled = sourceLrclib;
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
            if (_isPinned) return;
            _embedder.EmbedWindow(this, CalcWidth());
            _isPinned = _embedder.IsEmbedded;
            if (_isPinned) { UpdateGlass(); Rebuild(); }
        }

        public void Detach()
        {
            if (!_isPinned) return;
            _embedder.RemoveFromTaskbar();
            _isPinned = false;
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
            int h = _iconSize + _glassPadding * 2;
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

            LyricsHost.Visibility = _lyricsEnabled ? Visibility.Visible : Visibility.Collapsed;
            LyricsHost.Width = (_lyricsAdaptiveWidth && _currentAdaptiveLyricsWidth > 0) ? _currentAdaptiveLyricsWidth : _lyricsWidth;
            LyricsHost.Height = Math.Max(40, _iconSize + 8);
            LyricsHost.CornerRadius = new CornerRadius(13);
            LyricsHost.Background = _lyricsTransparentBackground
                ? Brushes.Transparent
                : new LinearGradientBrush(
                    Color.FromArgb(0xA9, 0x0A, 0x13, 0x20),
                    Color.FromArgb(0xB2, 0x0F, 0x1B, 0x2B),
                    new Point(0, 0), new Point(1, 1));
            LyricsHost.BorderBrush = _lyricsTransparentBackground
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            LyricsHost.BorderThickness = _lyricsTransparentBackground ? new Thickness(0) : new Thickness(1);
            LyricsHost.RenderTransform = new TranslateTransform(0, _lyricsVerticalOffset);
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
            int h = Math.Max(_iconSize + _glassPadding * 2, _lyricsEnabled ? 42 : 36);

            Width = w;
            Height = h;

            GlassBorder.Background = Brushes.Transparent;
            GlassBorder.BorderThickness = new Thickness(0);
            GlassBorder.CornerRadius = new CornerRadius(0);
            GlassBorder.Padding = new Thickness(0);

            if (GameAvatarContainer != null)
            {
                GameAvatarContainer.CornerRadius = new CornerRadius(_glassRadius);
                GameAvatarContainer.Padding = new Thickness(_glassPadding, Math.Max(2, _glassPadding - 1), _glassPadding, Math.Max(2, _glassPadding - 1));
            }

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
            if (gc > 0 && ac > 0) total += 10;
            total += ac * item;

            if (total > 0)
            {
                total += _glassPadding * 2 + 6;
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

            return total + 6;
        }

        private void MediaService_StateChanged(object? sender, TaskbarMediaSnapshot snapshot)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateMediaUi(snapshot)));
                return;
            }
            UpdateMediaUi(snapshot);
        }

        private void VisualizerTimer_Tick(object? sender, EventArgs e)
        {
            if (_isPlaying && VisualizerBar1 != null && VisualizerBar2 != null && VisualizerBar3 != null)
            {
                var h1 = 3 + _random.Next(2, 9);
                var h2 = 4 + _random.Next(3, 11);
                var h3 = 2 + _random.Next(2, 8);
                VisualizerBar1.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(h1, TimeSpan.FromMilliseconds(120)));
                VisualizerBar2.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(h2, TimeSpan.FromMilliseconds(120)));
                VisualizerBar3.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(h3, TimeSpan.FromMilliseconds(120)));
            }
        }

        private void StopVisualizer()
        {
            if (VisualizerBar1 != null && VisualizerBar2 != null && VisualizerBar3 != null)
            {
                VisualizerBar1.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(3, TimeSpan.FromMilliseconds(150)));
                VisualizerBar2.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(3, TimeSpan.FromMilliseconds(150)));
                VisualizerBar3.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(3, TimeSpan.FromMilliseconds(150)));
            }
        }

        private void UpdateMediaUi(TaskbarMediaSnapshot snapshot)
        {
            // Session callbacks can arrive from a background thread. Ignore queued
            // snapshots from an older song so their artwork cannot overwrite the new one.
            if (snapshot.TrackVersion < _lastTrackVersion)
                return;

            _lastTrackVersion = snapshot.TrackVersion;
            _lastSnapshot = snapshot;
            if (LyricText == null)
                return;

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
                _lastTrackKey = currentTrackKey;
                _trackChangedAt = DateTimeOffset.Now;
                _lastArtwork = null;
                _lastLyricText = string.Empty;
                AlbumArtwork.Source = null;
                AlbumArtwork.Visibility = Visibility.Collapsed;
                ArtworkFallback.Visibility = Visibility.Visible;
            }

            // 专辑封面展示逻辑 (Lyricify 严谨标准)
            if (snapshot.Artwork is { Length: > 0 })
            {
                if (trackChanged || !ReferenceEquals(_lastArtwork, snapshot.Artwork) || AlbumArtwork.Source == null)
                {
                    _lastArtwork = snapshot.Artwork;
                    var bmp = LoadArtwork(snapshot.Artwork);
                    if (bmp != null)
                    {
                        AlbumArtwork.Source = bmp;
                        AlbumArtwork.Visibility = Visibility.Visible;
                        ArtworkFallback.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        AlbumArtwork.Source = null;
                        AlbumArtwork.Visibility = Visibility.Collapsed;
                        ArtworkFallback.Visibility = Visibility.Visible;
                    }
                }
            }
            else
            {
                _lastArtwork = null;
                AlbumArtwork.Source = null;
                AlbumArtwork.Visibility = Visibility.Collapsed;
                ArtworkFallback.Visibility = Visibility.Visible;
            }

            // ── 稳定显示状态机 ────────────────────────────────────────────────────
            // 1. 无媒体会话             → 占位提示
            // 2. 切歌后的短暂识别阶段   → 歌名 + 歌手/专辑
            // 3. 有同步歌词             → 当前歌词 + 翻译/下一句
            // 4. 前奏、间奏或无歌词     → 歌名 + 稳定的曲目信息
            // 宽度只在切歌或设置变化时计算，避免逐句歌词触发嵌入窗口重排和闪烁。
            bool inTrackChangeBanner = (DateTimeOffset.Now - _trackChangedAt).TotalSeconds < 2.2;

            string primaryText;
            string compositeText;
            string iconGlyph;
            string presentationMode;
            var artistAlbum = string.Join(" · ", new[] { snapshot.Artist, snapshot.Album }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

            if (!snapshot.HasSession)
            {
                presentationMode = "idle";
                primaryText = "等待媒体播放";
                iconGlyph = "\uE8D6";
                compositeText = "打开播放器后将在此显示歌曲与歌词";
            }
            else if (_lyricsTrackInfoOnly && !string.IsNullOrWhiteSpace(snapshot.Title))
            {
                presentationMode = "track";
                primaryText = snapshot.Title;
                iconGlyph = "\uE8D6";
                compositeText = string.IsNullOrWhiteSpace(artistAlbum) ? snapshot.SourceText : artistAlbum;
            }
            else if (inTrackChangeBanner && !string.IsNullOrWhiteSpace(snapshot.Title))
            {
                presentationMode = "track";
                primaryText = snapshot.Title;
                iconGlyph = "\uE8D6";
                compositeText = string.IsNullOrWhiteSpace(artistAlbum) ? snapshot.SourceText : artistAlbum;
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.CurrentLyric))
            {
                presentationMode = "lyric";
                primaryText = snapshot.CurrentLyric;

                if (!string.IsNullOrWhiteSpace(snapshot.CurrentTranslation))
                {
                    iconGlyph = string.Empty;
                    compositeText = snapshot.CurrentTranslation;
                }
                else if (!string.IsNullOrWhiteSpace(snapshot.NextText)
                    && !string.Equals(snapshot.NextText, snapshot.CurrentLyric, StringComparison.Ordinal))
                {
                    iconGlyph = "\uE893";
                    compositeText = snapshot.NextText;
                }
                else
                {
                    iconGlyph = "\uE8D6";
                    compositeText = string.IsNullOrWhiteSpace(artistAlbum)
                        ? (string.IsNullOrWhiteSpace(snapshot.TimeString) ? snapshot.SourceText : snapshot.TimeString)
                        : artistAlbum;
                }
            }
            else
            {
                presentationMode = "track";
                primaryText = string.IsNullOrWhiteSpace(snapshot.Title) ? "正在播放" : snapshot.Title;
                if (!string.IsNullOrWhiteSpace(snapshot.NextText))
                {
                    iconGlyph = "\uE893";
                    compositeText = $"即将播放 · {snapshot.NextText}";
                }
                else
                {
                    iconGlyph = "\uE8D6";
                    compositeText = string.IsNullOrWhiteSpace(artistAlbum)
                        ? (string.IsNullOrWhiteSpace(snapshot.TimeString) ? snapshot.SourceText : snapshot.TimeString)
                        : artistAlbum;
                }
            }

            bool presentationModeChanged = !string.Equals(
                _lastPresentationMode, presentationMode, StringComparison.Ordinal);
            _lastPresentationMode = presentationMode;

            if (_lyricsAdaptiveWidth && _lyricsEnabled && !_isLyricsHovered)
            {
                UpdateAdaptiveLyricsWidth(primaryText, compositeText, trackChanged || _adaptiveLayoutDirty);
                _adaptiveLayoutDirty = false;
            }
            else if (!_lyricsAdaptiveWidth && LyricsHost.Width != _lyricsWidth)
            {
                LyricsHost.Width = _lyricsWidth;
                _currentAdaptiveLyricsWidth = _lyricsWidth;
                ApplyLayout();
            }

            // 逐句歌词只替换文字，不再改变整个面板透明度或尺寸。
            // 仅在切歌或展示模式变化时使用轻微位移动效。
            if (!string.Equals(_lastLyricText, primaryText, StringComparison.Ordinal))
            {
                _lastLyricText = primaryText;
                LyricText.Text = primaryText;
                bool animateModeTransition = trackChanged || presentationModeChanged;

                LyricsTextPanel.BeginAnimation(OpacityProperty, null);
                LyricsTextPanel.Opacity = _isLyricsHovered && _lyricsShowControls && LyricsControls?.IsEnabled == true
                    ? 0.18
                    : 1;

                if (animateModeTransition)
                {
                    LyricsTranslate.BeginAnimation(
                        TranslateTransform.YProperty,
                        new DoubleAnimation(2, 0, TimeSpan.FromMilliseconds(160))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        });
                }
                else
                {
                    LyricsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    LyricsTranslate.Y = 0;
                }
            }

            // 2. 副行图标与文本更新
            if (MediaIcon != null)
            {
                MediaIcon.Text = iconGlyph;
                MediaIcon.Visibility = _lyricsShowSecondaryLine && !string.IsNullOrWhiteSpace(iconGlyph)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            if (MediaText != null)
            {
                MediaText.Text = compositeText;
                MediaText.Visibility = _lyricsShowSecondaryLine ? Visibility.Visible : Visibility.Collapsed;
            }

            FitLyricsTextToAvailableWidth(primaryText, compositeText);

            LyricsHost.ToolTip = string.Join("\n", new[]
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

            LyricsProgress.Value = snapshot.Progress;
            _isPlaying = snapshot.IsPlaying;
            if (PlayPauseButton != null)
            {
                PlayPauseButton.Content = snapshot.IsPlaying ? "\uE769" : "\uE768";
            }
            if (LyricsControls != null)
            {
                LyricsControls.IsEnabled = snapshot.HasSession;
            }
        }

        public void SetPreviewMedia(TaskbarMediaSnapshot snapshot) => UpdateMediaUi(snapshot);

        public void SetPreviewControlsVisible(bool visible)
        {
            if (LyricsControlsGlass != null)
            {
                LyricsControlsGlass.IsHitTestVisible = visible;
                LyricsControlsGlass.Opacity = visible ? 1 : 0;
            }
            if (LyricsTextPanel != null)
                LyricsTextPanel.Opacity = visible ? 0 : 1;
        }

        private void UpdateAdaptiveLyricsWidth(string primaryText, string secondaryText, bool force)
        {
            string measureKey = $"{primaryText}\u001f{secondaryText}\u001f{_lyricsFontSize}\u001f{_lyricsFontFamily}";
            if (force || !string.Equals(measureKey, _lastAdaptiveMeasureKey, StringComparison.Ordinal))
            {
                _lastAdaptiveMeasureKey = measureKey;
                _lastMeasuredAdaptiveLyricsWidth = CalculateAdaptiveLyricsWidth(primaryText, secondaryText);
            }

            int targetWidth = _lastMeasuredAdaptiveLyricsWidth;
            if (force || targetWidth > _currentAdaptiveLyricsWidth + 4)
            {
                ApplyAdaptiveLyricsWidth(targetWidth);
                ResetAdaptiveShrinkCandidate();
                return;
            }

            if (targetWidth < _currentAdaptiveLyricsWidth - 40)
            {
                if (Math.Abs(targetWidth - _adaptiveShrinkCandidateWidth) > 12)
                {
                    _adaptiveShrinkCandidateWidth = targetWidth;
                    _adaptiveShrinkCandidateSince = DateTimeOffset.Now;
                }
                else if ((DateTimeOffset.Now - _adaptiveShrinkCandidateSince).TotalMilliseconds >= 1200)
                {
                    ApplyAdaptiveLyricsWidth(targetWidth);
                    ResetAdaptiveShrinkCandidate();
                }
            }
            else
            {
                ResetAdaptiveShrinkCandidate();
            }
        }

        private int CalculateAdaptiveLyricsWidth(string primaryText, string secondaryText)
        {
            double secondaryFontSize = Math.Max(11.5, _lyricsFontSize - 2.5);
            double primaryWidth = MeasureTextWidth(primaryText, _lyricsFontSize, FontWeights.SemiBold);
            double secondaryWidth = string.IsNullOrWhiteSpace(secondaryText)
                ? 0
                : MeasureTextWidth(secondaryText, secondaryFontSize, FontWeights.Medium) + 16;
            int desiredWidth = (int)Math.Ceiling(Math.Max(primaryWidth, secondaryWidth) + 82);

            int currentLyricsWidth = Math.Max(1, _currentAdaptiveLyricsWidth);
            int nonLyricsWidth = Math.Max(0, CalcWidth() - currentLyricsWidth);
            int taskbarAvailable = _embedder.GetMaximumWidthInDips();
            int maximumLyricsWidth = Math.Clamp(taskbarAvailable - nonLyricsWidth, 240, 720);
            return Math.Clamp(desiredWidth, 240, maximumLyricsWidth);
        }

        private void ApplyAdaptiveLyricsWidth(int width)
        {
            width = Math.Clamp(width, 240, 720);
            if (Math.Abs(width - _currentAdaptiveLyricsWidth) < 4)
                return;

            _currentAdaptiveLyricsWidth = width;
            LyricsHost.Width = width;
            ApplyLayout();
        }

        private void ResetAdaptiveShrinkCandidate()
        {
            _adaptiveShrinkCandidateWidth = 0;
            _adaptiveShrinkCandidateSince = DateTimeOffset.MinValue;
        }

        private void FitLyricsTextToAvailableWidth(string primaryText, string secondaryText)
        {
            double availableWidth = Math.Max(80, LyricsHost.Width - 82);
            double primaryRequired = MeasureTextWidth(primaryText, _lyricsFontSize, FontWeights.SemiBold);
            LyricText.FontSize = primaryRequired > availableWidth
                ? Math.Max(9.5, _lyricsFontSize * availableWidth / primaryRequired)
                : _lyricsFontSize;

            double secondaryBaseSize = Math.Max(11.5, _lyricsFontSize - 2.5);
            double secondaryAvailable = Math.Max(70, availableWidth - 16);
            double secondaryRequired = MeasureTextWidth(secondaryText, secondaryBaseSize, FontWeights.Medium);
            MediaText.FontSize = secondaryRequired > secondaryAvailable
                ? Math.Max(8.5, secondaryBaseSize * secondaryAvailable / secondaryRequired)
                : secondaryBaseSize;
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

        private void LyricsHost_MouseEnter(object sender, MouseEventArgs e)
        {
            _isLyricsHovered = true;
            ArtworkScale?.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.06, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            ArtworkScale?.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.06, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            if (_lyricsShowControls && LyricsControls != null && LyricsControls.IsEnabled)
            {
                if (LyricsControlsGlass != null)
                {
                    LyricsControlsGlass.IsHitTestVisible = true;
                    LyricsControlsGlass.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
                    LyricsControlsTranslate?.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
                }
                LyricsTextPanel?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
            }
        }

        private void LyricsHost_MouseLeave(object sender, MouseEventArgs e)
        {
            _isLyricsHovered = false;
            ArtworkScale?.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            ArtworkScale?.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            if (LyricsControlsGlass != null)
            {
                LyricsControlsGlass.IsHitTestVisible = false;
                LyricsControlsGlass.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
                LyricsControlsTranslate?.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(6, TimeSpan.FromMilliseconds(140)));
            }
            LyricsTextPanel?.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        }

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

        private async void PreviousTrack_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            AnimateButtonClick(sender as Button);
            await _mediaService.SkipPreviousAsync();
        }

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            AnimateButtonClick(sender as Button);
            await _mediaService.TogglePlayPauseAsync();
        }

        private async void NextTrack_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            AnimateButtonClick(sender as Button);
            await _mediaService.SkipNextAsync();
        }

        private void LyricsHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            float step = e.Delta > 0 ? 0.02f : -0.02f;
            float newVol = SystemVolumeService.AdjustVolume(step);
            bool isMuted = SystemVolumeService.IsMuted();
            ShowSeparatedVolumeHud(newVol, isMuted);
        }

        private void ShowSeparatedVolumeHud(float volume, bool isMuted)
        {
            try
            {
                Point topLeft = LyricsHost.PointToScreen(new Point(0, 0));
                Point center = new Point(topLeft.X + (LyricsHost.ActualWidth / 2), topLeft.Y);
                _volumeFlyout.ShowVolume(center, topLeft.Y, volume, isMuted);

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

        private void UpdateGlass()
        {
            if (GlassBorder == null) return;
            
            bool isLight = IsSystemInLightTheme();
            Brush bg;
            Brush border;

            if (_glassEnabled)
            {
                if (isLight)
                {
                    bg = new LinearGradientBrush(
                        Color.FromArgb(135, 255, 255, 255),
                        Color.FromArgb(75, 235, 245, 255),
                        new Point(0, 0), new Point(1, 1));
                    border = new LinearGradientBrush(
                        Color.FromArgb(160, 255, 255, 255),
                        Color.FromArgb(50, 100, 150, 220),
                        new Point(0, 0), new Point(0, 1));
                }
                else
                {
                    bg = new LinearGradientBrush(
                        Color.FromArgb(32, 255, 255, 255),
                        Color.FromArgb(18, 0, 0, 0),
                        new Point(0, 0), new Point(1, 1));
                    border = new LinearGradientBrush(
                        Color.FromArgb(55, 255, 255, 255),
                        Color.FromArgb(20, 255, 255, 255),
                        new Point(0, 0), new Point(0, 1));
                }
            }
            else
            {
                if (isLight)
                {
                    bg = new SolidColorBrush(Color.FromArgb(235, 245, 250, 255));
                    border = new SolidColorBrush(Color.FromArgb(180, 225, 235, 245));
                }
                else
                {
                    bg = new SolidColorBrush(Color.FromArgb(235, 30, 30, 33));
                    border = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
                }
            }

            if (GameAvatarContainer != null)
            {
                GameAvatarContainer.Background = bg;
                GameAvatarContainer.BorderBrush = border;
            }
            else
            {
                GlassBorder.Background = bg;
                GlassBorder.BorderBrush = border;
            }
        }

        // --- Icon Brushes (cached) ---

        private static readonly Brush _btnBrush = CreateFrozenBrush(50, 20, 35);
        private static readonly Brush _hoverBrush = CreateFrozenBrush(90, 45, 70);
        private static readonly Brush _activeBrush = CreateFrozenBrush(80, 50, 60);
        private static readonly Brush _currentBorderBrush = CreateFrozenSolidBrush(0x0A, 0x84, 0xFF, 255);
        private static readonly Brush _normalBorderBrush = CreateFrozenSolidBrush(255, 255, 255, 120);
        private static readonly Brush _hoverBorderBrush = CreateFrozenSolidBrush(255, 255, 255, 190);
        private static readonly Brush _bindingBorderBrush = CreateFrozenSolidBrush(48, 209, 88, 140);

        private static Brush CreateFrozenBrush(byte a1, byte a2, byte a3)
        {
            var brush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(a1, 255, 255, 255), 0),
                    new(Color.FromArgb(a2, 230, 246, 255), 0.5),
                    new(Color.FromArgb(a3, 255, 255, 255), 1)
                }, new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            return brush;
        }

        private static Brush CreateFrozenSolidBrush(byte r, byte g, byte b, byte a)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Brush BtnBrush() => _btnBrush;
        private static Brush HoverBrush() => _hoverBrush;
        private static Brush ActiveBrush() => _activeBrush;

        // --- Create Buttons ---

        private Border MakeGameBtn(GameListViewModel game)
        {
            int sz = _iconSize;

            var border = new Border
            {
                Width = sz, Height = sz,
                CornerRadius = new CornerRadius(8),
                BorderBrush = new SolidColorBrush(game.HasBinding
                    ? Color.FromArgb(140, 48, 209, 88)
                    : Color.FromArgb(120, 255, 255, 255)),
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
                    CornerRadius = new CornerRadius(8),
                    Background = Brushes.Transparent
                };
            }
            else
            {
                border.Background = BtnBrush();
                overlay = new Border
                {
                    CornerRadius = new CornerRadius(8),
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
                    var anim = new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(150))
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

            var border = new Border
            {
                Width = sz, Height = sz,
                CornerRadius = new CornerRadius(8),
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
                    CornerRadius = new CornerRadius(8),
                    Background = Brushes.Transparent
                };
            }
            else
            {
                border.Background = BtnBrush();
                overlay = new Border
                {
                    CornerRadius = new CornerRadius(8),
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
                    var anim = new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(150))
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

            var border = new Border
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
                        var anim = new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(150))
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

        private static UIElement FallbackIcon(int sz, string icon = "\uE7FC") => new TextBlock()
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = sz > 36 ? 16 : 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x2A, 0x32)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static UIElement FallbackText(string text, int sz) => new TextBlock()
        {
            Text = text,
            FontSize = sz > 36 ? 14 : 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0x30, 0x3A)),
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
