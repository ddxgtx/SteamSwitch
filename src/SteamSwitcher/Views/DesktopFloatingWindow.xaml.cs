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
        private bool _locked;
        private bool _readyToSavePosition;
        private double? _savedLeft;
        private double? _savedTop;
        private int _avatarSize = 42;
        private bool _glassEnabled = true;
        private bool _roundedMode = true;
        private string _glassColor = "#4A90D9";

        private const int WS_EX_TOOLWINDOW = 0x00000080;

        public event EventHandler<SteamAccount>? AccountSwitchRequested;
        public event EventHandler<int>? GameLaunchRequested;
        public event EventHandler<(double left, double top)>? PositionChanged;
        public event EventHandler? ShowMainWindowRequested;
        public event EventHandler<bool>? ToggleDesktopFloatingRequested;
        public event EventHandler<bool>? ToggleDesktopFloatingTopmostRequested;
        public event EventHandler<bool>? ToggleTaskbarPinnedRequested;
        public event EventHandler? ExitRequested;

        public DesktopFloatingWindow(AccountManager accountManager)
        {
            InitializeComponent();
            _accountManager = accountManager;

            Loaded += DesktopFloatingWindow_Loaded;
            LocationChanged += DesktopFloatingWindow_LocationChanged;
            SourceInitialized += (s, e) => HideFromAltTab();
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
            _viewModel = viewModel;
            _viewModel.PinnedGamesChanged += (s, e) => Dispatcher.Invoke(() =>
            {
                _pinnedGames = _viewModel.GetPinnedGames();
                RefreshGameIcons();
            });
            _viewModel.PinnedAccountsChanged += (s, e) => Dispatcher.Invoke(() =>
            {
                _pinnedAccounts = _viewModel.GetPinnedAccounts();
                RefreshAvatars();
            });
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
            return new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(70, 255, 255, 255), 0),
                    new(Color.FromArgb(26, 230, 246, 255), 0.55),
                    new(Color.FromArgb(44, 255, 255, 255), 1)
                },
                new Point(0, 0),
                new Point(1, 1));
        }

        private static Brush CreateLiquidHoverBrush()
        {
            return new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(112, 255, 255, 255), 0),
                    new(Color.FromArgb(52, 206, 234, 255), 0.55),
                    new(Color.FromArgb(82, 255, 255, 255), 1)
                },
                new Point(0, 0),
                new Point(1, 1));
        }

        private void RefreshAll()
        {
            RefreshGameIcons();
            RefreshAvatars();
        }

        private void RefreshGameIcons()
        {
            GamePanel.Children.Clear();
            foreach (var game in _pinnedGames)
            {
                GamePanel.Children.Add(CreateGameButton(game));
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
            GameAvatarSeparator.Visibility = _pinnedGames.Count > 0 && _pinnedAccounts.Count > 0
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
            border.BorderBrush = new SolidColorBrush(game.HasBinding
                ? Color.FromArgb(142, 48, 209, 88)
                : Color.FromArgb(148, 255, 255, 255));

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

            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 1)
                    GameLaunchRequested?.Invoke(this, game.AppId);
                e.Handled = true;
            };
            border.MouseRightButtonDown += (s, e) =>
            {
                ShowContextMenu();
                e.Handled = true;
            };

            AttachHover(border, game.HasBinding);
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

            border.MouseLeftButtonDown += (s, e) =>
            {
                AccountSwitchRequested?.Invoke(this, account);
                e.Handled = true;
            };
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
                    border.BorderBrush = new SolidColorBrush(isCurrent
                        ? Color.FromRgb(0x0A, 0x84, 0xFF)
                        : Color.FromArgb(148, 255, 255, 255));
                }
            }
        }
    }
}
