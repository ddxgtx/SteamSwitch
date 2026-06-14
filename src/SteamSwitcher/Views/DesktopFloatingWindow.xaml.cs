using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;
using SteamSwitcher.ViewModels;

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
        private int _avatarSize = 42;
        private bool _glassEnabled = true;
        private bool _roundedMode = true;
        private string _glassColor = "#4A90D9";
        private EventHandler? _pinnedGamesChangedHandler;
        private EventHandler? _pinnedAccountsChangedHandler;
        private EventHandler? _quickLaunchChangedHandler;
        private PanelDragHelper? _gameDragHelper;
        private PanelDragHelper? _avatarDragHelper;

        private const int WS_EX_TOOLWINDOW = 0x00000080;

        public event EventHandler<SteamAccount>? AccountSwitchRequested;
        public event EventHandler<int>? GameLaunchRequested;
        public event EventHandler<string>? QuickLaunchRequested;
        public event EventHandler<(double left, double top)>? PositionChanged;
        public event EventHandler? ShowMainWindowRequested;
        public event EventHandler<bool>? ToggleDesktopFloatingRequested;
        public event EventHandler<bool>? ToggleDesktopFloatingTopmostRequested;
        public event EventHandler<bool>? ToggleTaskbarPinnedRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler<IReadOnlyList<string>>? PanelItemOrderChanged;
        public event EventHandler<IReadOnlyList<string>>? AccountOrderChanged;
        public event EventHandler<int>? GameDeleteRequested;
        public event EventHandler<int>? QuickLaunchDeleteRequested;
        public event EventHandler<int>? AccountDeleteRequested;

        public DesktopFloatingWindow(AccountManager accountManager)
        {
            InitializeComponent();
            _accountManager = accountManager;

            Loaded += (s, e) => { DesktopFloatingWindow_Loaded(s!, e); SetupDragDrop(); };
            LocationChanged += DesktopFloatingWindow_LocationChanged;
            SourceInitialized += (s, e) => HideFromAltTab();
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

        public void SetViewModel(MainViewModel viewModel)
        {
            // Unsubscribe from old events if any
            if (_viewModel != null)
            {
                if (_pinnedGamesChangedHandler != null)
                    _viewModel.PinnedGamesChanged -= _pinnedGamesChangedHandler;
                if (_pinnedAccountsChangedHandler != null)
                    _viewModel.PinnedAccountsChanged -= _pinnedAccountsChangedHandler;
                if (_quickLaunchChangedHandler != null)
                    _viewModel.QuickLaunchChanged -= _quickLaunchChangedHandler;
            }

            _viewModel = viewModel;

            // Store event handler references for later unsubscription
            _pinnedGamesChangedHandler = (s, e) => Dispatcher.Invoke(() =>
            {
                _pinnedGames = _viewModel.GetPinnedGames();
                RefreshGameIcons();
            });
            _pinnedAccountsChangedHandler = (s, e) => Dispatcher.Invoke(() =>
            {
                _pinnedAccounts = _viewModel.GetPinnedAccounts();
                RefreshAvatars();
            });
            _quickLaunchChangedHandler = (s, e) => Dispatcher.Invoke(() =>
            {
                _pinnedQuickLaunchItems = _viewModel.GetPinnedQuickLaunchItems();
                RefreshGameIcons();
            });

            _viewModel.PinnedGamesChanged += _pinnedGamesChangedHandler;
            _viewModel.PinnedAccountsChanged += _pinnedAccountsChangedHandler;
            _viewModel.QuickLaunchChanged += _quickLaunchChangedHandler;
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
            _avatarSize = Math.Clamp(size, 32, 56);
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
            Opacity = Math.Clamp(percent, 45, 100) / 100.0;
        }

        public void SetSavedPosition(double? left, double? top)
        {
            _savedLeft = left;
            _savedTop = top;
            if (IsLoaded)
                ApplySavedPosition();
        }

        private void ApplySavedPosition()
        {
            var workArea = SystemParameters.WorkArea;
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;

            Left = Clamp(_savedLeft ?? workArea.Right - width - 72, workArea.Left + 12, workArea.Right - width - 12);
            Top = Clamp(_savedTop ?? workArea.Top + 92, workArea.Top + 12, workArea.Bottom - height - 12);
        }

        private void DesktopFloatingWindow_LocationChanged(object? sender, EventArgs e)
        {
            if (_readyToSavePosition && !_locked)
                PositionChanged?.Invoke(this, (Left, Top));
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
                DragMove();
            }
            catch { }
        }

        private void Window_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
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
                launchSteam: () => _accountManager.LaunchSteam(silent: true),
                refresh: null,
                detach: null,
                showDetach: false,
                isDesktopFloatingEnabled: true,
                isDesktopFloatingTopmost: _viewModel?.DesktopFloatingTopmost ?? true,
                isTaskbarPinned: _viewModel?.IsTaskbarPinned ?? false,
                toggleDesktopFloating: enabled => ToggleDesktopFloatingRequested?.Invoke(this, enabled),
                toggleDesktopFloatingTopmost: enabled => ToggleDesktopFloatingTopmostRequested?.Invoke(this, enabled),
                toggleTaskbarPinned: enabled => ToggleTaskbarPinnedRequested?.Invoke(this, enabled),
                exit: () => ExitRequested?.Invoke(this, EventArgs.Empty)
            );
            menu.IsOpen = true;
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

            if (_glassEnabled)
            {
                var baseColor = ParseColor(_glassColor);
                var r = baseColor.R;
                var g = baseColor.G;
                var b = baseColor.B;

                RootGlass.Background = new LinearGradientBrush(
                    Color.FromArgb(142, 255, 255, 255),
                    Color.FromArgb(60, r, g, b),
                    new Point(0, 0), new Point(1, 1));
                RootGlass.BorderBrush = new LinearGradientBrush(
                    Color.FromArgb(180, 255, 255, 255),
                    Color.FromArgb(80, r, g, b),
                    new Point(0, 0), new Point(0, 1));
            }
            else
            {
                RootGlass.Background = new SolidColorBrush(Color.FromArgb(230, 244, 250, 255));
                RootGlass.BorderBrush = new SolidColorBrush(Color.FromArgb(176, 255, 255, 255));
            }
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
            return Color.FromRgb(0x4A, 0x90, 0xD9);
        }

        private static Brush CreateLiquidButtonBrush()
        {
            return _liquidButtonBrush;
        }

        private static Brush CreateLiquidHoverBrush()
        {
            return _liquidHoverBrush;
        }

        private static readonly Brush _liquidButtonBrush = CreateFrozenLiquidBrush(70, 26, 44);
        private static readonly Brush _liquidHoverBrush = CreateFrozenLiquidBrush(112, 52, 82);
        private static readonly Brush _currentBorderBrush = CreateFrozenSolidBrush(0x0A, 0x84, 0xFF, 255);
        private static readonly Brush _normalBorderBrush = CreateFrozenSolidBrush(255, 255, 255, 120);
        private static readonly Brush _hoverBorderBrush = CreateFrozenSolidBrush(255, 255, 255, 190);
        private static readonly Brush _bindingBorderBrush = CreateFrozenSolidBrush(48, 209, 88, 140);

        private static Brush CreateFrozenLiquidBrush(byte a1, byte a2, byte a3)
        {
            var brush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(a1, 255, 255, 255), 0),
                    new(Color.FromArgb(a2, 230, 246, 255), 0.55),
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
            int radius = _roundedMode ? size / 4 : 8;
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
            int radius = _roundedMode ? size / 4 : 8;
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
                    border.Child = CreateFallbackText("⚡", size);
                }
            }
            else
            {
                border.Child = CreateFallbackText("⚡", size);
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
            int radius = _roundedMode ? size / 4 : 8;
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

            if (account == _accountManager.CurrentAccount)
            {
                border.BorderThickness = new Thickness(2.5);
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            }

            border.MouseRightButtonDown += (s, e) =>
            {
                ShowContextMenu();
                e.Handled = true;
            };
            AttachHover(border, false);
            return border;
        }

        private static Border CreateIconShell(int size, int radius)
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(radius),
                Background = CreateLiquidButtonBrush(),
                BorderBrush = new SolidColorBrush(Color.FromArgb(148, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
        }

        private static TextBlock CreateFallbackText(string text, int size)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size > 40 ? 15 : 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x28, 0x30, 0x3A)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static string GetAccountInitial(SteamAccount account)
        {
            var name = !string.IsNullOrWhiteSpace(account.PersonaName)
                ? account.PersonaName
                : account.AccountName;
            return string.IsNullOrWhiteSpace(name) ? "S" : name[..1].ToUpperInvariant();
        }

        private static void AttachHover(Border border, bool active)
        {
            Brush normalBorder = border.BorderBrush;
            Thickness normalThickness = border.BorderThickness;
            Brush normalBackground = border.Background;
            bool hasImage = normalBackground is ImageBrush;

            border.MouseEnter += (s, e) =>
            {
                if (!hasImage)
                    border.Background = CreateLiquidHoverBrush();
                border.BorderBrush = new SolidColorBrush(active
                    ? Color.FromArgb(168, 48, 209, 88)
                    : Color.FromArgb(190, 255, 255, 255));
                if (border.RenderTransform is ScaleTransform scale)
                {
                    scale.ScaleX = 1.06;
                    scale.ScaleY = 1.06;
                }
            };

            border.MouseLeave += (s, e) =>
            {
                border.Background = normalBackground;
                border.BorderBrush = normalBorder;
                border.BorderThickness = normalThickness;
                if (border.RenderTransform is ScaleTransform scale)
                {
                    scale.ScaleX = 1;
                    scale.ScaleY = 1;
                }
            };
        }

        public void UpdateCurrentAccount(SteamAccount? current)
        {
            foreach (var child in AvatarPanel.Children)
            {
                if (child is Border border && border.Tag is SteamAccount account)
                {
                    bool isCurrent = account == current;
                    border.BorderThickness = isCurrent ? new Thickness(2.5) : new Thickness(1);
                    border.BorderBrush = isCurrent ? _currentBorderBrush : _normalBorderBrush;
                }
            }
        }
    }
}
