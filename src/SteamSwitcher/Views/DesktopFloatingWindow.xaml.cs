using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
    public partial class DesktopFloatingWindow : Window
    {
        private readonly AccountManager _accountManager;
        private MainViewModel? _viewModel;
        private List<SteamAccount> _pinnedAccounts = new();
        private List<GameListViewModel> _pinnedGames = new();
        private List<QuickLaunchItem> _pinnedQuickLaunchItems = new();
        private bool _locked;
        private bool _readyToSavePosition;
        private double? _savedLeft;
        private double? _savedTop;
        private FloatingMonitorPosition? _savedMonitorPosition;
        private bool _restoringPosition;
        private HwndSource? _windowSource;
        private int _avatarSize = 42;
        private bool _glassEnabled = true;
        private bool _roundedMode = true;
        private string _glassColor = "#6AA7FF";
        private EventHandler? _pinnedGamesChangedHandler;
        private EventHandler? _pinnedAccountsChangedHandler;
        private EventHandler? _quickLaunchChangedHandler;
        private EventHandler<bool>? _minimalModeChangedHandler;
        private EventHandler<bool>? _dockEnabledChangedHandler;
        private EventHandler? _currentAccountChangedHandler;
        private PanelDragHelper? _gameDragHelper;
        private PanelDragHelper? _avatarDragHelper;

        private System.Windows.Threading.DispatcherTimer? _autoHideTimer;
        private System.Windows.Threading.DispatcherTimer? _positionSaveTimer;
        private double _normalOpacity = 1.0;
        private bool _isDragging;
        private bool _isCollapsed;

        private enum AnchorEdge
        {
            None,
            Left,
            Right,
            Top
        }

        private const int WS_EX_TOOLWINDOW = 0x00000080;

        public event EventHandler<SteamAccount>? AccountSwitchRequested;
        public event EventHandler<int>? GameLaunchRequested;
        public event EventHandler<string>? QuickLaunchRequested;
        public event EventHandler<(double left, double top)>? PositionChanged;
        public event EventHandler<(string MonitorId, double RelativeLeft, double RelativeTop)>? MonitorPositionChanged;
        public event EventHandler? ShowMainWindowRequested;
        public event EventHandler<bool>? ToggleDesktopFloatingRequested;
        public event EventHandler<bool>? ToggleDesktopFloatingTopmostRequested;
        public event EventHandler<bool>? ToggleTaskbarPinnedRequested;
        public event EventHandler<bool>? ToggleTaskbarLyricsRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler<IReadOnlyList<string>>? PanelItemOrderChanged;
        public event EventHandler<IReadOnlyList<string>>? AccountOrderChanged;
        public event EventHandler<int>? GameDeleteRequested;
        public event EventHandler<int>? QuickLaunchDeleteRequested;
        public event EventHandler<int>? AccountDeleteRequested;

        public DesktopFloatingWindow(AccountManager accountManager)
        {
            InitializeComponent();
            UpdateGlassEffect();
            _accountManager = accountManager;
            _currentAccountChangedHandler = (s, e) => PostToUi(
                () => UpdateCurrentAccount(_accountManager.CurrentAccount));
            _accountManager.CurrentAccountChanged += _currentAccountChangedHandler;

            Loaded += (s, e) => { DesktopFloatingWindow_Loaded(s!, e); SetupDragDrop(); InitializeAutoHide(); };
            LocationChanged += DesktopFloatingWindow_LocationChanged;
            SourceInitialized += (s, e) =>
            {
                HideFromAltTab();
                _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                _windowSource?.AddHook(WindowMessages);
            };
            Closed += (s, e) =>
            {
                var pendingPosition = _positionSaveTimer?.IsEnabled == true;
                ExitCleanup.Run(
                    ("解绑浮窗账号事件", () =>
                    {
                        if (_currentAccountChangedHandler != null)
                            _accountManager.CurrentAccountChanged -= _currentAccountChangedHandler;
                    }),
                    ("解绑浮窗模型事件", DetachViewModelEvents),
                    ("解绑浮窗窗口消息", () =>
                    {
                        var source = _windowSource;
                        _windowSource = null;
                        source?.RemoveHook(WindowMessages);
                    }),
                    ("停止浮窗自动隐藏计时器", () =>
                    {
                        var timer = _autoHideTimer;
                        _autoHideTimer = null;
                        if (timer == null) return;
                        timer.Stop();
                        timer.Tick -= AutoHideTimer_Tick;
                    }),
                    ("解绑浮窗位置事件", () => LocationChanged -= DesktopFloatingWindow_LocationChanged),
                    ("解绑浮窗游戏拖动", () =>
                    {
                        var helper = _gameDragHelper;
                        _gameDragHelper = null;
                        helper?.Detach();
                    }),
                    ("解绑浮窗账号拖动", () =>
                    {
                        var helper = _avatarDragHelper;
                        _avatarDragHelper = null;
                        helper?.Detach();
                    }),
                    ("停止浮窗位置保存计时器", () =>
                    {
                        var timer = _positionSaveTimer;
                        _positionSaveTimer = null;
                        timer?.Stop();
                    }),
                    ("保存浮窗最终位置", () =>
                    {
                        if (pendingPosition && _readyToSavePosition && !_locked && !_isCollapsed) SaveCurrentPosition();
                    }));
            };
        }

        private void InitializeAutoHide()
        {
            _normalOpacity = OpacityPercentToDouble();
            if (_autoHideTimer != null) return;
            _autoHideTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _autoHideTimer.Tick += AutoHideTimer_Tick;
            _autoHideTimer.Start();
        }

        private double OpacityPercentToDouble()
        {
            if (_viewModel != null)
            {
                return Math.Clamp(_viewModel.DesktopFloatingOpacity, 20, 100) / 100.0;
            }
            return 0.8;
        }

        private void SetupDragDrop()
        {
            // Guard against Loaded firing more than once. Double Attach()
            // would stack handlers and fire events twice.
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

        private void HideFromAltTab()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        private const int GWL_EXSTYLE = -20;

        private void DesktopFloatingWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateGlassEffect();
            ApplySavedPosition();
            _readyToSavePosition = true;
        }

        private void PostToUi(Action action)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            try { Dispatcher.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        public void SetViewModel(MainViewModel viewModel)
        {
            DetachViewModelEvents();

            _viewModel = viewModel;

            // Store event handler references for later unsubscription
            _pinnedGamesChangedHandler = (s, e) => PostToUi(() =>
            {
                _pinnedGames = _viewModel.GetPinnedGames();
                RefreshGameIcons();
            });
            _pinnedAccountsChangedHandler = (s, e) => PostToUi(() =>
            {
                _pinnedAccounts = _viewModel.GetPinnedAccounts();
                RefreshAvatars();
            });
            _quickLaunchChangedHandler = (s, e) => PostToUi(() =>
            {
                _pinnedQuickLaunchItems = _viewModel.GetPinnedQuickLaunchItems();
                RefreshGameIcons();
            });

            _minimalModeChangedHandler = (s, val) => PostToUi(() =>
            {
                if (val)
                {
                    BeginAnimation(OpacityProperty, null);
                    Opacity = _normalOpacity;
                }
            });
            _dockEnabledChangedHandler = (s, val) => PostToUi(() =>
            {
                if (!val && _isCollapsed)
                {
                    var edgeLimit = GetAnchorEdge(out double limitVal);
                    ExpandWindow(edgeLimit, limitVal);
                }
            });

            _viewModel.PinnedGamesChanged += _pinnedGamesChangedHandler;
            _viewModel.PinnedAccountsChanged += _pinnedAccountsChangedHandler;
            _viewModel.QuickLaunchChanged += _quickLaunchChangedHandler;
            _viewModel.MinimalModeChanged += _minimalModeChangedHandler;
            _viewModel.DesktopFloatingDockEnabledChanged += _dockEnabledChangedHandler;
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
            if (_minimalModeChangedHandler != null)
                _viewModel.MinimalModeChanged -= _minimalModeChangedHandler;
            if (_dockEnabledChangedHandler != null)
                _viewModel.DesktopFloatingDockEnabledChanged -= _dockEnabledChangedHandler;

            _viewModel = null;
        }

        public void SetPinnedAccounts(List<SteamAccount> accounts)
        {
            _pinnedAccounts = accounts ?? new List<SteamAccount>();
            RefreshAvatars();
        }

        public void SetPinnedGames(List<GameListViewModel> games)
        {
            _pinnedGames = games ?? new List<GameListViewModel>();
            RefreshGameIcons();
        }

        public void SetPinnedQuickLaunchItems(List<QuickLaunchItem> items)
        {
            _pinnedQuickLaunchItems = items ?? new List<QuickLaunchItem>();
            RefreshGameIcons();
        }

        public void SetAvatarSize(int size)
        {
            _avatarSize = Math.Clamp(size, 24, 72);
            RefreshAll();
        }

        public void SetGlassEnabled(bool enabled)
        {
            _glassEnabled = enabled;
            UpdateGlassEffect();
        }

        public void SetGlassColor(string color)
        {
            _glassColor = color;
            UpdateGlassEffect();
        }

        public void SetRoundedMode(bool rounded)
        {
            _roundedMode = rounded;
            RefreshAll();
        }

        public void SetLocked(bool locked)
        {
            _locked = locked;
            HintText.Text = locked ? "已锁定" : "拖动移动";
        }

        public void SetTopmostEnabled(bool enabled)
        {
            Topmost = enabled;
        }

        public void SetOpacityPercent(int percent)
        {
            _normalOpacity = Math.Clamp(percent, 20, 100) / 100.0;
            if (!_isCollapsed)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = _normalOpacity;
            }
        }

        public void SetSavedPosition(double? left, double? top)
        {
            _savedLeft = left;
            _savedTop = top;
            if (IsLoaded)
                ApplySavedPosition();
        }

        public void SetSavedMonitorPosition(string? id, double? relativeLeft, double? relativeTop)
        {
            _savedMonitorPosition = !string.IsNullOrWhiteSpace(id) && relativeLeft is { } x && relativeTop is { } y &&
                double.IsFinite(x) && double.IsFinite(y)
                ? new(id, Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)) : null;
            if (IsLoaded) ApplySavedPosition();
        }

        private IntPtr WindowMessages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WmDisplayChange = 0x007E, WmDpiChanged = 0x02E0, WmSettingChange = 0x001A;
            if (message is WmDisplayChange or WmDpiChanged or WmSettingChange)
            {
                PostToUi(() =>
                {
                    if (!_readyToSavePosition || _isDragging) return;
                    BeginAnimation(LeftProperty, null);
                    BeginAnimation(TopProperty, null);
                    _isCollapsed = false;
                    Opacity = _normalOpacity;
                    ApplySavedPosition();
                    _autoHideTimer?.Start();
                });
            }
            return IntPtr.Zero;
        }

        private void ApplySavedPosition()
        {
            var monitors = FloatingWindowMonitors.Enumerate();
            if (monitors.Count == 0) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            var bounds = FloatingWindowMonitors.GetBounds(hwnd);
            if (bounds == null) return;
            var scale = VisualTreeHelper.GetDpi(this);
            var width = bounds.Value.Width / scale.DpiScaleX;
            var height = bounds.Value.Height / scale.DpiScaleY;
            Rect target;
            if (_savedMonitorPosition != null)
                target = FloatingPlacementGeometry.Restore(_savedMonitorPosition, monitors, width, height);
            else
            {
                var primary = System.Linq.Enumerable.FirstOrDefault(monitors, monitor => monitor.Primary) ?? monitors[0];
                var left = _savedLeft is { } x && double.IsFinite(x) ? x * scale.DpiScaleX
                    : primary.WorkArea.Right - bounds.Value.Width - 72 * primary.ScaleX;
                var top = _savedTop is { } y && double.IsFinite(y) ? y * scale.DpiScaleY
                    : primary.WorkArea.Top + 92 * primary.ScaleY;
                var proposed = new Rect(left, top, bounds.Value.Width, bounds.Value.Height);
                target = FloatingPlacementGeometry.Clamp(proposed, FloatingPlacementGeometry.Nearest(monitors, proposed).WorkArea);
            }
            _restoringPosition = true;
            try { FloatingWindowMonitors.Move(hwnd, target); }
            finally { _restoringPosition = false; }
        }

        private void SaveCurrentPosition()
        {
            var monitors = FloatingWindowMonitors.Enumerate();
            var bounds = FloatingWindowMonitors.GetBounds(new WindowInteropHelper(this).Handle);
            if (bounds is { } rect && monitors.Count > 0)
            {
                _savedMonitorPosition = FloatingPlacementGeometry.Capture(rect, FloatingPlacementGeometry.Nearest(monitors, rect));
                MonitorPositionChanged?.Invoke(this, (_savedMonitorPosition.MonitorId,
                    _savedMonitorPosition.RelativeLeft, _savedMonitorPosition.RelativeTop));
            }
            _savedLeft = Left;
            _savedTop = Top;
            PositionChanged?.Invoke(this, (Left, Top));
        }

        private void DesktopFloatingWindow_LocationChanged(object? sender, EventArgs e)
        {
            if (_readyToSavePosition && !_restoringPosition && !_locked && !_isCollapsed && !_isDragging)
                SchedulePositionSave();
        }

        /// <summary>
        /// LocationChanged fires at very high frequency while the user drags.
        /// Debounce the PositionChanged event (and therefore SettingsService.Save)
        /// so the disk is written at most every 400ms.
        /// </summary>
        private void SchedulePositionSave()
        {
            if (_positionSaveTimer == null)
            {
                _positionSaveTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                _positionSaveTimer.Tick += (s, e) =>
                {
                    _positionSaveTimer?.Stop();
                    if (_readyToSavePosition && !_locked && !_isCollapsed)
                        SaveCurrentPosition();
                };
            }
            _positionSaveTimer.Stop();
            _positionSaveTimer.Start();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            if (_locked || e.ButtonState != MouseButtonState.Pressed)
                return;

            try
            {
                _isDragging = true;
                DragMove();
            }
            catch { }
            finally
            {
                _isDragging = false;
                if (_readyToSavePosition && !_locked && !_isCollapsed)
                    SchedulePositionSave();
            }
        }

        private void Window_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowContextMenu();
        }

        private void ShowContextMenu()
        {
            var menu = BuildContextMenu();
            menu.IsOpen = true;
        }

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
                refresh: null,
                detach: null,
                showDetach: false,
                isDesktopFloatingEnabled: true,
                isDesktopFloatingTopmost: _viewModel?.DesktopFloatingTopmost ?? true,
                isTaskbarPinned: _viewModel?.IsTaskbarPinned ?? false,
                toggleDesktopFloating: enabled => ToggleDesktopFloatingRequested?.Invoke(this, enabled),
                toggleDesktopFloatingTopmost: enabled => ToggleDesktopFloatingTopmostRequested?.Invoke(this, enabled),
                toggleTaskbarPinned: enabled => ToggleTaskbarPinnedRequested?.Invoke(this, enabled),
                isTaskbarLyricsEnabled: _viewModel?.TaskbarLyricsEnabled ?? false,
                toggleTaskbarLyrics: enabled => ToggleTaskbarLyricsRequested?.Invoke(this, enabled),
                exit: () => ExitRequested?.Invoke(this, EventArgs.Empty)
            );
            return menu;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (max < min)
                return min;
            return Math.Min(Math.Max(value, min), max);
        }

        private void UpdateGlassEffect()
        {
            if (RootGlass == null) return;

            RootGlass.SetResourceReference(Border.BackgroundProperty,
                _glassEnabled ? "FloatingSurfaceBrush" : "FloatingSolidBrush");
            RootGlass.SetResourceReference(Border.BorderBrushProperty, "FloatingBorderBrush");
            TintLayer.Background = new SolidColorBrush(ParseColor(_glassColor));
            TintLayer.Visibility = _glassEnabled ? Visibility.Visible : Visibility.Collapsed;
            Resources["FloatingTextPrimaryBrush"] = Application.Current.FindResource(
                _glassEnabled ? "GlassTextPrimaryBrush" : "FloatingTextPrimaryBrush");
            Resources["FloatingTextSecondaryBrush"] = Application.Current.FindResource(
                _glassEnabled ? "GlassTextSecondaryBrush" : "FloatingTextSecondaryBrush");
            RootGlass.Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 2, Opacity = 0.28 };
        }

        private static Color ParseColor(string hex)
        {
            try
            {
                if (hex.StartsWith("#"))
                    hex = hex.Substring(1);
                if (hex.Length == 6)
                {
                    var r = Convert.ToByte(hex.Substring(0, 2), 16);
                    var g = Convert.ToByte(hex.Substring(2, 2), 16);
                    var b = Convert.ToByte(hex.Substring(4, 2), 16);
                    return Color.FromRgb(r, g, b);
                }
            }
            catch { }
            return Color.FromRgb(0x6A, 0xA7, 0xFF);
        }

        private static Brush CreateLiquidButtonBrush()
        {
            return Application.Current?.TryFindResource("FloatingButtonBrush") as Brush ?? _liquidButtonBrush;
        }

        private static Brush CreateLiquidHoverBrush()
        {
            return Application.Current?.TryFindResource("FloatingHoverBrush") as Brush ?? _liquidHoverBrush;
        }

        private static readonly Brush _liquidButtonBrush = CreateFrozenLiquidBrush(70, 26, 44);
        private static readonly Brush _liquidHoverBrush = CreateFrozenLiquidBrush(112, 52, 82);
        private static readonly Brush _currentBorderBrush = CreateFrozenSolidBrush(0x6A, 0xA7, 0xFF, 255);
        private static Brush _normalBorderBrush => Application.Current?.TryFindResource("FloatingBorderBrush") as Brush ?? Brushes.Transparent;
        private static readonly Brush _hoverBorderBrush = CreateFrozenSolidBrush(106, 167, 255, 210);
        private static readonly Brush _bindingBorderBrush = CreateFrozenSolidBrush(101, 214, 166, 190);

        private static Brush CreateFrozenLiquidBrush(byte a1, byte a2, byte a3)
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

        private void RefreshAll()
        {
            RefreshGameIcons();
            RefreshAvatars();
        }

        private void RefreshGameIcons()
        {
            GamePanel.Children.Clear();
            var items = _viewModel?.GetPinnedPanelItems();
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item is GameListViewModel game)
                        GamePanel.Children.Add(CreateGameButton(game));
                    else if (item is QuickLaunchItem quickLaunchItem)
                        GamePanel.Children.Add(CreateQuickLaunchButton(quickLaunchItem));
                }
            }
            else
            {
                foreach (var game in _pinnedGames)
                    GamePanel.Children.Add(CreateGameButton(game));
                foreach (var item in _pinnedQuickLaunchItems)
                    GamePanel.Children.Add(CreateQuickLaunchButton(item));
            }

            UpdateSeparator();
        }

        private void RefreshAvatars()
        {
            AvatarPanel.Children.Clear();
            foreach (var account in _pinnedAccounts)
            {
                AvatarPanel.Children.Add(CreateAvatarButton(account));
            }

            UpdateSeparator();
        }

        private void UpdateSeparator()
        {
            bool hasGames = _pinnedGames.Count > 0 || _pinnedQuickLaunchItems.Count > 0;
            GameAvatarSeparator.Visibility = hasGames && _pinnedAccounts.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private Border CreateGameButton(GameListViewModel game)
        {
            int size = _avatarSize;
            int radius = Math.Max(6, (int)Math.Round(size * 0.26));
            var border = CreateIconShell(size, radius);
            border.ToolTip = $"{game.GameName}\n点击启动 (账号: {game.BindingAccountName ?? "未绑定"})";
            border.Tag = game;
            border.BorderBrush = game.HasBinding ? _bindingBorderBrush : _normalBorderBrush;

            var iconPath = !string.IsNullOrEmpty(game.IconPath)
                ? game.IconPath
                : _accountManager.GetSteamService().GetGameIconPath(game.AppId);
            if (!string.IsNullOrEmpty(iconPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(iconPath);
                    bitmap.DecodePixelWidth = Math.Max(size * 2, 128);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    var imageBrush = new ImageBrush(bitmap)
                    {
                        Stretch = Stretch.UniformToFill
                    };
                    border.Background = imageBrush;
                }
                catch
                {
                    border.Child = CreateFallbackText("G", size);
                }
            }
            else
            {
                border.Child = CreateFallbackText("G", size);
            }

            border.MouseRightButtonDown += (s, e) =>
            {
                ShowContextMenu();
                e.Handled = true;
            };

            AttachHover(border, game.HasBinding);
            return border;
        }

        private Border CreateQuickLaunchButton(QuickLaunchItem item)
        {
            int size = _avatarSize;
            int radius = Math.Max(6, (int)Math.Round(size * 0.26));
            var border = CreateIconShell(size, radius);
            border.ToolTip = $"{item.Name}\n{item.ExecutablePath}";
            border.Tag = item;
            border.BorderBrush = _normalBorderBrush;

            var iconPath = item.IconPath;
            if (!string.IsNullOrEmpty(iconPath) && System.IO.File.Exists(iconPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(iconPath);
                    bitmap.DecodePixelWidth = Math.Max(size * 2, 128);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    var imageBrush = new ImageBrush(bitmap)
                    {
                        Stretch = Stretch.UniformToFill
                    };
                    border.Background = imageBrush;
                }
                catch
                {
                    border.Child = CreateFallbackText("\uE945", size);
                }
            }
            else
            {
                border.Child = CreateFallbackText("\uE945", size);
            }

            border.MouseRightButtonDown += (s, e) =>
            {
                ShowContextMenu();
                e.Handled = true;
            };

            AttachHover(border, false);
            return border;
        }

        private Border CreateAvatarButton(SteamAccount account)
        {
            int size = _avatarSize;
            int radius = _roundedMode ? size / 2 : 8;
            var border = CreateIconShell(size, radius);
            border.ToolTip = account.PersonaName;
            border.Tag = account;

            border.Child = CreateFallbackText(GetAccountInitial(account), size);
            var avatarPath = account.AvatarPath;
            if (!string.IsNullOrEmpty(avatarPath) && System.IO.File.Exists(avatarPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(avatarPath);
                    bitmap.DecodePixelWidth = Math.Max(size * 2, 128);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    var imageBrush = new ImageBrush(bitmap)
                    {
                        Stretch = Stretch.UniformToFill
                    };
                    border.Background = imageBrush;
                    border.Child = null;
                    border.BorderThickness = new Thickness(0);
                }
                catch { }
            }

            if (AccountManager.AreSameAccount(account, _accountManager.CurrentAccount))
            {
                border.BorderThickness = new Thickness(2.5);
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0xA7, 0xFF));
            }

            border.MouseRightButtonDown += (s, e) =>
            {
                ShowContextMenu();
                e.Handled = true;
            };
            AttachHover(
                border,
                false,
                () => AccountManager.AreSameAccount(account, _accountManager.CurrentAccount));
            return border;
        }

        private static Border CreateIconShell(int size, int radius)
        {
            return new RoundedClipBorder
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(radius),
                Background = CreateLiquidButtonBrush(),
                BorderBrush = _normalBorderBrush,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
        }

        private TextBlock CreateFallbackText(string text, int size)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                FontSize = size > 40 ? 15 : 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Application.Current?.TryFindResource("FloatingTextPrimaryBrush") as Brush ?? Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, "FloatingTextPrimaryBrush");
            if (!string.IsNullOrEmpty(text) && text[0] >= '\uE000' && text[0] <= '\uF8FF')
                textBlock.FontFamily = new FontFamily("Segoe MDL2 Assets");
            return textBlock;
        }

        private static string GetAccountInitial(SteamAccount account)
        {
            var name = !string.IsNullOrWhiteSpace(account.PersonaName)
                ? account.PersonaName
                : account.AccountName;
            return string.IsNullOrWhiteSpace(name) ? "S" : name[..1].ToUpperInvariant();
        }

        private static void AttachHover(Border border, bool active, Func<bool>? isCurrentAccount = null)
        {
            Brush normalBorder = border.BorderBrush;
            Thickness normalThickness = border.BorderThickness;
            Brush normalBackground = border.Background;
            bool hasImage = normalBackground is ImageBrush;

            border.MouseEnter += (s, e) =>
            {
                if (isCurrentAccount?.Invoke() != true)
                {
                    if (!hasImage)
                        border.Background = CreateLiquidHoverBrush();
                    border.BorderBrush = new SolidColorBrush(active
                        ? Color.FromArgb(210, 101, 214, 166)
                        : Color.FromArgb(210, 106, 167, 255));
                }
                if (border.RenderTransform is ScaleTransform scale)
                {
                    var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                }
            };

            border.MouseLeave += (s, e) =>
            {
                border.Background = normalBackground;
                if (isCurrentAccount?.Invoke() == true)
                {
                    border.BorderBrush = _currentBorderBrush;
                    border.BorderThickness = new Thickness(2.5);
                }
                else if (isCurrentAccount != null)
                {
                    border.BorderBrush = _normalBorderBrush;
                    border.BorderThickness = new Thickness(1);
                }
                else
                {
                    border.BorderBrush = normalBorder;
                    border.BorderThickness = normalThickness;
                }
                if (border.RenderTransform is ScaleTransform scale)
                {
                    var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                }
            };
        }

        public void UpdateCurrentAccount(SteamAccount? current)
        {
            foreach (var child in AvatarPanel.Children)
            {
                if (child is Border border && border.Tag is SteamAccount account)
                {
                    bool isCurrent = AccountManager.AreSameAccount(account, current);
                    border.BorderThickness = isCurrent ? new Thickness(2.5) : new Thickness(1);
                    border.BorderBrush = isCurrent ? _currentBorderBrush : _normalBorderBrush;
                }
            }
        }

        private AnchorEdge GetAnchorEdge(out double screenLimit)
        {
            screenLimit = 0;
            double left = Left;
            double top = Top;
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;

            var monitors = FloatingWindowMonitors.Enumerate();
            var bounds = FloatingWindowMonitors.GetBounds(new WindowInteropHelper(this).Handle);
            if (bounds == null || monitors.Count == 0) return AnchorEdge.None;
            var monitor = FloatingPlacementGeometry.Nearest(monitors, bounds.Value);
            var scale = VisualTreeHelper.GetDpi(this);
            // Convert local pixel deltas rather than scaling virtual-desktop
            // origins: adjacent monitors may use different DPI values.
            double screenLeft = left + (monitor.WorkArea.Left - bounds.Value.Left) / scale.DpiScaleX;
            double screenTop = top + (monitor.WorkArea.Top - bounds.Value.Top) / scale.DpiScaleY;
            double screenRight = left + (monitor.WorkArea.Right - bounds.Value.Left) / scale.DpiScaleX;

            const double threshold = 15;

            if (left - screenLeft <= threshold)
            {
                screenLimit = screenLeft;
                return AnchorEdge.Left;
            }
            if (screenRight - (left + width) <= threshold)
            {
                screenLimit = screenRight;
                return AnchorEdge.Right;
            }
            if (top - screenTop <= threshold)
            {
                screenLimit = screenTop;
                return AnchorEdge.Top;
            }

            return AnchorEdge.None;
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);

            if (!_isCollapsed || _isDragging || _locked) return;

            bool dockEnabled = _viewModel?.DesktopFloatingDockEnabled ?? true;
            if (!dockEnabled) return;

            var edge = GetAnchorEdge(out double limit);
            if (edge != AnchorEdge.None)
            {
                ExpandWindow(edge, limit);
            }
        }

        private void AutoHideTimer_Tick(object? sender, EventArgs e)
        {
            if (_isDragging || _locked) return;

            bool dockEnabled = _viewModel?.DesktopFloatingDockEnabled ?? true;
            if (!dockEnabled)
            {
                if (_isCollapsed)
                {
                    var edgeLimit = GetAnchorEdge(out double limitVal);
                    ExpandWindow(edgeLimit, limitVal);
                }
                return;
            }

            bool isMouseOver = IsMouseOver;
            var edge = GetAnchorEdge(out double limit);

            if (isMouseOver)
            {
                if (_isCollapsed)
                {
                    ExpandWindow(edge, limit);
                }
            }
            else
            {
                if (!_isCollapsed && edge != AnchorEdge.None)
                {
                    CollapseWindow(edge, limit);
                }
            }
        }

        private void ExpandWindow(AnchorEdge edge, double limit)
        {
            _isCollapsed = false;
            _autoHideTimer?.Stop();

            double targetPos = 0;
            DependencyProperty? dp = null;

            if (edge == AnchorEdge.Left)
            {
                targetPos = limit;
                dp = LeftProperty;
            }
            else if (edge == AnchorEdge.Right)
            {
                targetPos = limit - ActualWidth;
                dp = LeftProperty;
            }
            else if (edge == AnchorEdge.Top)
            {
                targetPos = limit;
                dp = TopProperty;
            }

            if (_viewModel != null && _viewModel.MinimalMode)
            {
                if (dp != null)
                {
                    BeginAnimation(dp, null);
                    if (dp == LeftProperty) Left = targetPos;
                    else Top = targetPos;
                }

                BeginAnimation(OpacityProperty, null);
                Opacity = _normalOpacity;

                _autoHideTimer?.Start();
                return;
            }

            if (dp != null)
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation
                {
                    To = targetPos,
                    Duration = TimeSpan.FromMilliseconds(250),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };

                anim.Completed += (s, ev) =>
                {
                    BeginAnimation(dp, null);
                    if (dp == LeftProperty) Left = targetPos;
                    else Top = targetPos;
                    _autoHideTimer?.Start();
                };

                BeginAnimation(dp, anim);
            }

            var opacityAnim = new DoubleAnimation
            {
                To = _normalOpacity,
                Duration = TimeSpan.FromMilliseconds(200)
            };
            opacityAnim.Completed += (s, ev) =>
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = _normalOpacity;
            };
            BeginAnimation(OpacityProperty, opacityAnim);
        }

        private void CollapseWindow(AnchorEdge edge, double limit)
        {
            _isCollapsed = true;
            _autoHideTimer?.Stop();

            double targetPos = 0;
            DependencyProperty dp = LeftProperty;

            const double visibleThickness = 4;

            if (edge == AnchorEdge.Left)
            {
                targetPos = limit - ActualWidth + visibleThickness;
                dp = LeftProperty;
            }
            else if (edge == AnchorEdge.Right)
            {
                targetPos = limit - visibleThickness;
                dp = LeftProperty;
            }
            else if (edge == AnchorEdge.Top)
            {
                targetPos = limit - ActualHeight + visibleThickness;
                dp = TopProperty;
            }

            if (_viewModel != null && _viewModel.MinimalMode)
            {
                BeginAnimation(dp, null);
                if (dp == LeftProperty) Left = targetPos;
                else Top = targetPos;

                BeginAnimation(OpacityProperty, null);
                Opacity = 0.25;

                _autoHideTimer?.Start();
                return;
            }

            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = targetPos,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            };

            anim.Completed += (s, ev) =>
            {
                BeginAnimation(dp, null);
                if (dp == LeftProperty) Left = targetPos;
                else Top = targetPos;
                _autoHideTimer?.Start();
            };

            BeginAnimation(dp, anim);

            var opacityAnim = new DoubleAnimation
            {
                To = 0.25,
                Duration = TimeSpan.FromMilliseconds(250)
            };
            opacityAnim.Completed += (s, ev) =>
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 0.25;
            };
            BeginAnimation(OpacityProperty, opacityAnim);
        }
    }
}
