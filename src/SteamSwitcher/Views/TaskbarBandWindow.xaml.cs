using System;
using System.Collections.Generic;
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
        private MainViewModel? _viewModel;
        private List<SteamAccount> _pinnedAccounts = new();
        private List<GameListViewModel> _pinnedGames = new();
        private List<QuickLaunchItem> _pinnedQuickLaunchItems = new();
        private bool _isPinned;
        private bool _layoutRefreshQueued;
        private EventHandler? _pinnedGamesChangedHandler;
        private EventHandler? _pinnedAccountsChangedHandler;
        private EventHandler? _quickLaunchChangedHandler;
        private PanelDragHelper? _gameDragHelper;
        private PanelDragHelper? _avatarDragHelper;

        private int _glassPadding = 2;
        private int _iconSize = 38;
        private bool _glassEnabled = true;
        private bool _roundedMode = true;
        private int _iconRadius = 27;
        private int _glassRadius = 31;

        public event EventHandler<SteamAccount>? AccountSwitchRequested;
        public event EventHandler<int>? GameLaunchRequested;
        public event EventHandler<string>? QuickLaunchRequested;
        public event EventHandler? ShowMainWindowRequested;
        public event EventHandler? DetachRequested;
        public event EventHandler<bool>? ToggleDesktopFloatingRequested;
        public event EventHandler<bool>? ToggleDesktopFloatingTopmostRequested;
        public event EventHandler<bool>? ToggleTaskbarPinnedRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler<IReadOnlyList<string>>? PanelItemOrderChanged;
        public event EventHandler<IReadOnlyList<string>>? AccountOrderChanged;
        public event EventHandler<int>? GameDeleteRequested;
        public event EventHandler<int>? QuickLaunchDeleteRequested;
        public event EventHandler<int>? AccountDeleteRequested;

        public TaskbarBandWindow(AccountManager accountManager)
        {
            InitializeComponent();
            _accountManager = accountManager;
            _embedder = new TaskbarEmbedder();
            _embedder.TaskbarCreated += (s, e) => Application.Current.Dispatcher.Invoke(Rebuild);
            Loaded += (s, e) => { UpdateGlass(); SetupDragDrop(); };
            Closing += (s, e) => Detach();
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
                launchSteam: () => _accountManager.LaunchSteam(silent: _viewModel?.SilentCloseSteam ?? true),
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
                    b.BorderBrush = isCur ? _currentBorderBrush : _normalBorderBrush;
                }
            }
        }

        // --- Layout ---

        private void Rebuild()
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
            int gc = _pinnedGames.Count + _pinnedQuickLaunchItems.Count;
            int ac = _pinnedAccounts.Count;
            if (gc == 0 && ac == 0) ac = 1;

            int item = _iconSize + 4;
            int total = gc * item;
            if (gc > 0 && ac > 0) total += 10;
            total += ac * item;
            return total + _glassPadding * 2 + 4;
        }

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

            if (_glassEnabled)
            {
                if (isLight)
                {
                    GlassBorder.Background = new LinearGradientBrush(
                        Color.FromArgb(135, 255, 255, 255),
                        Color.FromArgb(75, 235, 245, 255),
                        new Point(0, 0), new Point(1, 1));
                    GlassBorder.BorderBrush = new LinearGradientBrush(
                        Color.FromArgb(160, 255, 255, 255),
                        Color.FromArgb(50, 100, 150, 220),
                        new Point(0, 0), new Point(0, 1));
                }
                else
                {
                    GlassBorder.Background = new LinearGradientBrush(
                        Color.FromArgb(25, 255, 255, 255),
                        Color.FromArgb(12, 0, 0, 0),
                        new Point(0, 0), new Point(1, 1));
                    GlassBorder.BorderBrush = new LinearGradientBrush(
                        Color.FromArgb(45, 255, 255, 255),
                        Color.FromArgb(15, 255, 255, 255),
                        new Point(0, 0), new Point(0, 1));
                }
            }
            else
            {
                if (isLight)
                {
                    GlassBorder.Background = new SolidColorBrush(Color.FromArgb(235, 245, 250, 255));
                    GlassBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(180, 225, 235, 245));
                }
                else
                {
                    GlassBorder.Background = new SolidColorBrush(Color.FromArgb(235, 30, 30, 33));
                    GlassBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
                }
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
                    Child = FallbackIcon(sz, "⚡")
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
            bool isCur = account == _accountManager.CurrentAccount;

            var border = new Border
            {
                Width = sz, Height = sz,
                CornerRadius = new CornerRadius(r),
                BorderBrush = isCur ? _currentBorderBrush : _normalBorderBrush,
                BorderThickness = isCur ? new Thickness(2.5) : new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = account.PersonaName,
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
                if (!isCur)
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

        private static UIElement FallbackIcon(int sz, string icon = "🎮") => new TextBlock()
        {
            Text = icon,
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
