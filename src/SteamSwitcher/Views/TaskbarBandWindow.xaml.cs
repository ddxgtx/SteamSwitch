using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        private MainViewModel? _viewModel;
        private List<SteamAccount> _pinnedAccounts = new();
        private List<GameListViewModel> _pinnedGames = new();
        private bool _isPinned;
        private bool _layoutRefreshQueued;

        private int _glassPadding = 2;
        private int _iconSize = 38;
        private bool _glassEnabled = true;
        private bool _roundedMode = true;
        private int _iconRadius = 27;
        private int _glassRadius = 31;

        public event EventHandler<SteamAccount>? AccountSwitchRequested;
        public event EventHandler<int>? GameLaunchRequested;
        public event EventHandler? ShowMainWindowRequested;
        public event EventHandler? DetachRequested;
        public event EventHandler<bool>? ToggleDesktopFloatingRequested;
        public event EventHandler<bool>? ToggleDesktopFloatingTopmostRequested;
        public event EventHandler<bool>? ToggleTaskbarPinnedRequested;
        public event EventHandler? ExitRequested;

        public TaskbarBandWindow(AccountManager accountManager)
        {
            InitializeComponent();
            _accountManager = accountManager;
            _embedder = new TaskbarEmbedder();
            _embedder.TaskbarCreated += (s, e) => Application.Current.Dispatcher.Invoke(Rebuild);
            Loaded += (s, e) => UpdateGlass();
            Closing += (s, e) => Detach();
            MouseLeftButtonDown += TaskbarBandWindow_MouseLeftButtonDown;
            MouseRightButtonDown += TaskbarBandWindow_MouseRightButtonDown;
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
                launchSteam: () => _accountManager.LaunchSteam(silent: true),
                refresh: () => Rebuild(),
                detach: () => DetachRequested?.Invoke(this, EventArgs.Empty),
                showDetach: true,
                isDesktopFloatingEnabled: _viewModel?.DesktopFloatingEnabled ?? false,
                isDesktopFloatingTopmost: _viewModel?.DesktopFloatingTopmost ?? true,
                isTaskbarPinned: _isPinned,
                toggleDesktopFloating: enabled => ToggleDesktopFloatingRequested?.Invoke(this, enabled),
                toggleDesktopFloatingTopmost: enabled => ToggleDesktopFloatingTopmostRequested?.Invoke(this, enabled),
                toggleTaskbarPinned: enabled => ToggleTaskbarPinnedRequested?.Invoke(this, enabled),
                exit: () => ExitRequested?.Invoke(this, EventArgs.Empty)
            );
            menu.IsOpen = true;
        }

        // --- Public API ---

        public bool IsPinned => _isPinned;

        public void SetViewModel(MainViewModel vm)
        {
            _viewModel = vm;
            _viewModel.PinnedGamesChanged += (s, e) => Application.Current.Dispatcher.Invoke(() =>
            {
                _pinnedGames = _viewModel.GetPinnedGames();
                Rebuild();
            });
            _viewModel.PinnedAccountsChanged += (s, e) => Application.Current.Dispatcher.Invoke(() =>
            {
                _pinnedAccounts = _viewModel.GetPinnedAccounts();
                Rebuild();
            });
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
                    bool isCur = acc == current;
                    b.BorderThickness = isCur ? new Thickness(2.5) : new Thickness(1);
                    b.BorderBrush = isCur
                        ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))
                        : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
                }
            }
        }

        // --- Layout ---

        private void Rebuild()
        {
            int h = _iconSize + _glassPadding * 2;
            _iconRadius = _roundedMode ? (_iconSize + 4) / 2 : 6;
            _glassRadius = h / 2;

            GamePanel.Children.Clear();
            foreach (var g in _pinnedGames)
                GamePanel.Children.Add(MakeGameBtn(g));

            AvatarPanel.Children.Clear();
            foreach (var a in _pinnedAccounts)
                AvatarPanel.Children.Add(MakeAvatarBtn(a));

            GameAvatarSeparator.Visibility =
                _pinnedGames.Count > 0 && _pinnedAccounts.Count > 0
                    ? Visibility.Visible : Visibility.Collapsed;

            ApplyLayout();
        }

        private void ApplyLayout()
        {
            int w = CalcWidth();
            int h = _iconSize + _glassPadding * 2;

            Width = w;
            Height = h;

            GlassBorder.CornerRadius = new CornerRadius(_glassRadius);
            GlassBorder.Padding = new Thickness(_glassPadding);

            if (_isPinned)
            {
                _embedder.UpdateWidth(w);
                QueuePosRefresh();
            }
        }

        private int CalcWidth()
        {
            int gc = _pinnedGames.Count;
            int ac = _pinnedAccounts.Count;
            if (gc == 0 && ac == 0) ac = 1;

            int item = _iconSize + 4;
            int total = gc * item;
            if (gc > 0 && ac > 0) total += 10;
            total += ac * item;
            return total + _glassPadding * 2 + 4;
        }

        // --- Glass ---

        private void UpdateGlass()
        {
            if (GlassBorder == null) return;
            if (_glassEnabled)
            {
                GlassBorder.Background = new LinearGradientBrush(
                    Color.FromArgb(120, 255, 255, 255),
                    Color.FromArgb(50, 225, 240, 255),
                    new Point(0, 0), new Point(1, 1));
                GlassBorder.BorderBrush = new LinearGradientBrush(
                    Color.FromArgb(180, 255, 255, 255),
                    Color.FromArgb(60, 100, 160, 220),
                    new Point(0, 0), new Point(0, 1));
            }
            else
            {
                GlassBorder.Background = new SolidColorBrush(Color.FromArgb(230, 245, 250, 255));
                GlassBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
            }
        }

        // --- Icon Brushes ---

        private static Brush BtnBrush() => new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(50, 255, 255, 255), 0),
                new(Color.FromArgb(20, 230, 246, 255), 0.5),
                new(Color.FromArgb(35, 255, 255, 255), 1)
            }, new Point(0, 0), new Point(1, 1));

        private static Brush HoverBrush() => new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(90, 255, 255, 255), 0),
                new(Color.FromArgb(45, 210, 236, 255), 0.5),
                new(Color.FromArgb(70, 255, 255, 255), 1)
            }, new Point(0, 0), new Point(1, 1));

        private static Brush ActiveBrush() => new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(80, 255, 255, 255), 0),
                new(Color.FromArgb(50, 80, 160, 220), 0.5),
                new(Color.FromArgb(60, 255, 255, 255), 1)
            }, new Point(0, 0), new Point(1, 1));

        // --- Create Buttons ---

        private Border MakeGameBtn(GameListViewModel game)
        {
            int sz = _iconSize;
            int r = _iconRadius;

            var border = new Border
            {
                Width = sz, Height = sz,
                CornerRadius = new CornerRadius(r),
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
            if (imgBrush != null)
            {
                border.Background = imgBrush;
            }
            else
            {
                border.Background = BtnBrush();
                border.Child = FallbackIcon(sz);
            }

            var overlay = new Border
            {
                CornerRadius = new CornerRadius(r),
                Background = Brushes.Transparent
            };
            border.Child = overlay;

            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 1) GameLaunchRequested?.Invoke(this, game.AppId);
            };
            border.MouseEnter += (s, e) =>
            {
                overlay.Background = game.HasBinding ? ActiveBrush() : HoverBrush();
                border.BorderBrush = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255));
                if (border.RenderTransform is ScaleTransform sc) { sc.ScaleX = 1.08; sc.ScaleY = 1.08; }
            };
            border.MouseLeave += (s, e) =>
            {
                overlay.Background = Brushes.Transparent;
                border.BorderBrush = new SolidColorBrush(game.HasBinding
                    ? Color.FromArgb(140, 48, 209, 88)
                    : Color.FromArgb(120, 255, 255, 255));
                if (border.RenderTransform is ScaleTransform sc) { sc.ScaleX = 1; sc.ScaleY = 1; }
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
            bool isCur = account == _accountManager.CurrentAccount;

            var border = new Border
            {
                Width = sz, Height = sz,
                CornerRadius = new CornerRadius(r),
                BorderBrush = isCur
                    ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                BorderThickness = isCur ? new Thickness(2.5) : new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = account.PersonaName,
                Tag = account,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };

            var imgBrush = LoadImageBrush(account.AvatarPath, sz);
            if (imgBrush != null)
            {
                border.Background = imgBrush;
            }
            else
            {
                border.Background = BtnBrush();
                border.Child = FallbackText(Initial(account), sz);
            }

            var overlay = new Border
            {
                CornerRadius = new CornerRadius(r),
                Background = Brushes.Transparent
            };
            border.Child = overlay;

            border.MouseLeftButtonDown += (s, e) =>
            {
                AccountSwitchRequested?.Invoke(this, account);
                e.Handled = true;
            };
            border.MouseRightButtonDown += (s, e) =>
            {
                e.Handled = true;
                ShowContextMenu();
            };
            border.MouseEnter += (s, e) =>
            {
                if (!isCur)
                {
                    overlay.Background = HoverBrush();
                    border.BorderBrush = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255));
                    if (border.RenderTransform is ScaleTransform sc) { sc.ScaleX = 1.08; sc.ScaleY = 1.08; }
                }
            };
            border.MouseLeave += (s, e) =>
            {
                if (!isCur)
                {
                    overlay.Background = Brushes.Transparent;
                    border.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
                    if (border.RenderTransform is ScaleTransform sc) { sc.ScaleX = 1; sc.ScaleY = 1; }
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

        private static UIElement FallbackIcon(int sz) => new TextBlock()
        {
            Text = "🎮",
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

        private void QueuePosRefresh()
        {
            if (_layoutRefreshQueued) return;
            _layoutRefreshQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _layoutRefreshQueued = false;
                _embedder.UpdateWidth((int)Math.Ceiling(ActualWidth > 0 ? ActualWidth : Width));
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }
}
