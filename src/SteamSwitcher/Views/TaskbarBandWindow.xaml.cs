using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.ViewModels;

namespace SteamSwitcher.Views
{
    public partial class TaskbarBandWindow : Window
    {
        private readonly AccountManager _accountManager;
        private readonly TaskbarEmbedder _embedder;
        private MainViewModel? _viewModel;
        private List<SteamAccount> _pinnedAccounts = new();
        private List<GameListViewModel> _pinnedGames = new();
        private bool _isPinned;
        private bool _layoutRefreshQueued;
        private int _avatarSize = 40;
        private bool _glassEnabled = true;
        private bool _roundedMode = true;
        private const int ItemGap = 4;
        private const int ShellPadding = 16;
        private const int SeparatorWidth = 15;

        public event EventHandler<SteamAccount>? AccountSwitchRequested;
        public event EventHandler<int>? GameLaunchRequested;

        public TaskbarBandWindow(AccountManager accountManager)
        {
            InitializeComponent();
            _accountManager = accountManager;
            _embedder = new TaskbarEmbedder();
            _embedder.TaskbarCreated += OnTaskbarCreated;

            Loaded += TaskbarBandWindow_Loaded;
            Closing += TaskbarBandWindow_Closing;
        }

        private void TaskbarBandWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateGlassEffect();
        }

        private void TaskbarBandWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => Detach();

        private void OnTaskbarCreated(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() => RefreshAll());
        }

        public void SetViewModel(MainViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel.PinnedGamesChanged += (s, e) => Dispatcher.Invoke(() =>
            {
                _pinnedGames = _viewModel.GetPinnedGames();
                RefreshGameIcons();
                RefreshLayout();
            });
            _viewModel.PinnedAccountsChanged += (s, e) => Dispatcher.Invoke(() =>
            {
                _pinnedAccounts = _viewModel.GetPinnedAccounts();
                RefreshAvatars();
                RefreshLayout();
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

        public void Attach()
        {
            if (_isPinned) return;
            int width = CalculateWidth();
            _embedder.EmbedWindow(this, width);
            _isPinned = _embedder.IsEmbedded;
            if (_isPinned)
            {
                UpdateGlassEffect();
                RefreshAll();
            }
        }

        public void Detach()
        {
            if (!_isPinned) return;
            _embedder.RemoveFromTaskbar();
            _isPinned = false;
        }

        public bool IsPinned => _isPinned;

        public void SetPosition(TaskbarPosition position) => _embedder.Position = position;
        public void SetOffset(int offset) => _embedder.OffsetX = offset;

        public void SetAvatarSize(int size)
        {
            _avatarSize = size;
            RefreshAll();
        }

        public void SetGlassEnabled(bool enabled)
        {
            _glassEnabled = enabled;
            UpdateGlassEffect();
        }

        public void SetRoundedMode(bool rounded)
        {
            _roundedMode = rounded;
            RefreshAll();
        }

        private int CalculateWidth()
        {
            int gameCount = _pinnedGames.Count;
            int accountCount = _pinnedAccounts.Count;
            if (accountCount == 0 && gameCount == 0)
                accountCount = 1;

            int total = gameCount * (_avatarSize + ItemGap);
            if (gameCount > 0 && accountCount > 0)
                total += SeparatorWidth;
            total += accountCount * (_avatarSize + ItemGap);
            return total + ShellPadding;
        }

        private void UpdateGlassEffect()
        {
            if (GlassBorder == null) return;

            if (_glassEnabled)
            {
                GlassBorder.Background = new LinearGradientBrush(
                    Color.FromArgb(114, 255, 255, 255),
                    Color.FromArgb(40, 221, 238, 255),
                    new Point(0, 0), new Point(1, 1));
                GlassBorder.BorderBrush = new LinearGradientBrush(
                    Color.FromArgb(176, 255, 255, 255),
                    Color.FromArgb(58, 89, 171, 255),
                    new Point(0, 0), new Point(0, 1));
            }
            else
            {
                GlassBorder.Background = new SolidColorBrush(Color.FromArgb(224, 244, 250, 255));
                GlassBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(164, 255, 255, 255));
            }
        }

        private static Brush CreateLiquidButtonBrush()
        {
            return new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(58, 255, 255, 255), 0),
                    new(Color.FromArgb(22, 230, 246, 255), 0.52),
                    new(Color.FromArgb(38, 255, 255, 255), 1)
                },
                new Point(0, 0),
                new Point(1, 1));
        }

        private static Brush CreateLiquidHoverBrush()
        {
            return new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(96, 255, 255, 255), 0),
                    new(Color.FromArgb(44, 210, 236, 255), 0.56),
                    new(Color.FromArgb(72, 255, 255, 255), 1)
                },
                new Point(0, 0),
                new Point(1, 1));
        }

        private static Brush CreateLiquidActiveBrush()
        {
            return new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(92, 255, 255, 255), 0),
                    new(Color.FromArgb(46, 92, 176, 255), 0.54),
                    new(Color.FromArgb(66, 255, 255, 255), 1)
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
            GameAvatarSeparator.Visibility = _pinnedGames.Count > 0 && _pinnedAccounts.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            RefreshLayout();
        }

        private void RefreshAvatars()
        {
            AvatarPanel.Children.Clear();
            foreach (var account in _pinnedAccounts)
            {
                AvatarPanel.Children.Add(CreateAvatarButton(account));
            }
            GameAvatarSeparator.Visibility = _pinnedGames.Count > 0 && _pinnedAccounts.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            RefreshLayout();
        }

        private Border CreateGameButton(GameListViewModel game)
        {
            int size = _avatarSize;
            int radius = _roundedMode ? size / 4 : 6;

            var border = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(radius),
                Background = CreateLiquidButtonBrush(),
                BorderBrush = new SolidColorBrush(game.HasBinding
                    ? Color.FromArgb(128, 48, 209, 88)
                    : Color.FromArgb(136, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = $"{game.GameName}\n点击启动 (账号: {game.BindingAccountName ?? "未绑定"})",
                Tag = game,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };

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
                    bitmap.DecodePixelWidth = size;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    var image = new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.UniformToFill
                    };
                    image.Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
                    border.Child = image;
                }
                catch
                {
                    border.Child = CreateGameFallbackIcon(size);
                }
            }
            else
            {
                border.Child = CreateGameFallbackIcon(size);
            }

            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 1)
                {
                    GameLaunchRequested?.Invoke(this, game.AppId);
                }
            };

            border.MouseEnter += (s, e) =>
            {
                border.Background = game.HasBinding
                    ? CreateLiquidActiveBrush()
                    : CreateLiquidHoverBrush();
                border.BorderBrush = new SolidColorBrush(Color.FromArgb(178, 255, 255, 255));
                if (border.RenderTransform is ScaleTransform scale)
                {
                    scale.ScaleX = 1.06;
                    scale.ScaleY = 1.06;
                }
            };

            border.MouseLeave += (s, e) =>
            {
                border.Background = CreateLiquidButtonBrush();
                border.BorderBrush = new SolidColorBrush(game.HasBinding
                    ? Color.FromArgb(128, 48, 209, 88)
                    : Color.FromArgb(136, 255, 255, 255));
                if (border.RenderTransform is ScaleTransform scale)
                {
                    scale.ScaleX = 1;
                    scale.ScaleY = 1;
                }
            };

            border.MouseRightButtonDown += (s, e) =>
            {
                if (_viewModel != null)
                {
                    _viewModel.ToggleGamePin(game.AppId);
                }
            };

            return border;
        }

        private static TextBlock CreateGameFallbackIcon(int size)
        {
            return new TextBlock
            {
                Text = "🎮",
                FontSize = size > 36 ? 16 : 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x2A, 0x32)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private Border CreateAvatarButton(SteamAccount account)
        {
            int size = _avatarSize;
            int radius = _roundedMode ? size / 4 : 6;

            var border = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(radius),
                Background = CreateLiquidButtonBrush(),
                BorderBrush = new SolidColorBrush(Color.FromArgb(136, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = account.PersonaName,
                Tag = account,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };

            var image = new Image
            {
                Stretch = Stretch.UniformToFill
            };

            var avatarPath = account.AvatarPath;
            if (!string.IsNullOrEmpty(avatarPath) && System.IO.File.Exists(avatarPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(avatarPath);
                    bitmap.DecodePixelWidth = size;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image.Source = bitmap;
                }
                catch { }
            }

            image.Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
            border.Child = image;

            bool isCurrent = account == _accountManager.CurrentAccount;
            if (isCurrent)
            {
                border.BorderThickness = new Thickness(2.5);
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            }

            border.MouseLeftButtonDown += (s, e) => AccountSwitchRequested?.Invoke(this, account);

            border.MouseEnter += (s, e) =>
            {
                if (account != _accountManager.CurrentAccount)
                {
                    border.Background = CreateLiquidHoverBrush();
                    border.BorderBrush = new SolidColorBrush(Color.FromArgb(178, 255, 255, 255));
                    if (border.RenderTransform is ScaleTransform scale)
                    {
                        scale.ScaleX = 1.06;
                        scale.ScaleY = 1.06;
                    }
                }
            };

            border.MouseLeave += (s, e) =>
            {
                if (account != _accountManager.CurrentAccount)
                {
                    border.Background = CreateLiquidButtonBrush();
                    border.BorderBrush = new SolidColorBrush(Color.FromArgb(136, 255, 255, 255));
                    if (border.RenderTransform is ScaleTransform scale)
                    {
                        scale.ScaleX = 1;
                        scale.ScaleY = 1;
                    }
                }
            };

            return border;
        }

        public void UpdateCurrentAccount(SteamAccount? current)
        {
            foreach (var child in AvatarPanel.Children)
            {
                if (child is Border border && border.Tag is SteamAccount account)
                {
                    bool isCurrent = account == current;
                    if (isCurrent)
                    {
                        border.BorderThickness = new Thickness(2.5);
                        border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
                    }
                    else
                    {
                        border.BorderThickness = new Thickness(1);
                        border.BorderBrush = new SolidColorBrush(Color.FromArgb(136, 255, 255, 255));
                    }
                }
            }
        }

        private void RefreshLayout()
        {
            int width = CalculateWidth();
            Width = width;
            Height = Math.Max(34, _avatarSize + 6);
            if (_isPinned)
            {
                _embedder.UpdateWidth(width);
                QueuePositionRefresh();
            }
        }

        private void QueuePositionRefresh()
        {
            if (_layoutRefreshQueued)
                return;

            _layoutRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _layoutRefreshQueued = false;
                _embedder.UpdateWidth((int)Math.Ceiling(ActualWidth > 0 ? ActualWidth : Width));
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }
}
