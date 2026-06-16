using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Forms;
using SteamSwitcher.ViewModels;
using FontStyle = System.Drawing.FontStyle;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SteamSwitcher.Services
{
    public class TrayIconService : IDisposable
    {
        private NotifyIcon? _notifyIcon;
        private readonly Window _ownerWindow;
        private ContextMenuStrip? _contextMenu;
        private TrayMenuPalette _palette = TrayMenuPalette.FromApplication();
        private ObservableCollection<AccountViewModel>? _accounts;
        private AccountViewModel? _currentAccount;

        public event EventHandler<string>? AccountSelected;
        public event EventHandler? LaunchSteamRequested;
        public event EventHandler? ExitRequested;

        public TrayIconService(Window ownerWindow)
        {
            _ownerWindow = ownerWindow;
            InitializeTrayIcon();
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new NotifyIcon();

            try
            {
                var iconUri = new Uri("pack://application:,,,/Resources/steam.ico", UriKind.Absolute);
                var resourceInfo = System.Windows.Application.GetResourceStream(iconUri);
                if (resourceInfo != null)
                {
                    using var stream = resourceInfo.Stream;
                    _notifyIcon.Icon = new Icon(stream);
                }
                else
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }

            _notifyIcon.Text = "Steam Switch";
            _notifyIcon.Visible = true;
            _notifyIcon.MouseClick += NotifyIcon_MouseClick;
            _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;

            _contextMenu = new ContextMenuStrip
            {
                Padding = new Padding(8, 7, 8, 7),
                ShowImageMargin = false
            };
            ApplyPalette();
            _notifyIcon.ContextMenuStrip = null;
        }

        private void ApplyPalette()
        {
            if (_contextMenu == null) return;

            _palette = TrayMenuPalette.FromApplication();
            _contextMenu.BackColor = _palette.Background;
            _contextMenu.ForeColor = _palette.Text;
            _contextMenu.Renderer = new AppleMenuRenderer(_palette);
        }

        private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowOwnerWindow();
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowContextMenu();
            }
        }

        private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
        {
            ShowOwnerWindow();
        }

        private void ShowOwnerWindow()
        {
            _ownerWindow.Show();
            _ownerWindow.WindowState = WindowState.Normal;
            _ownerWindow.Activate();
        }

        public void UpdateMenu(ObservableCollection<AccountViewModel> accounts, AccountViewModel? currentAccount)
        {
            _accounts = accounts;
            _currentAccount = currentAccount;

            if (_contextMenu == null) return;

            ApplyPalette();
            _contextMenu.Items.Clear();
            _contextMenu.Items.Add(CreateHeaderItem("Steam Switch", "本地快捷控制"));
            _contextMenu.Items.Add(CreateSeparator());

            var showItem = CreateMenuItem("显示主窗口", "恢复应用窗口");
            showItem.Click += (s, e) => ShowOwnerWindow();
            _contextMenu.Items.Add(showItem);

            var launchItem = CreateMenuItem("启动 Steam", "按静默模式设置启动");
            launchItem.Click += (s, e) => LaunchSteamRequested?.Invoke(this, EventArgs.Empty);
            _contextMenu.Items.Add(launchItem);

            if (accounts.Count > 0)
            {
                _contextMenu.Items.Add(CreateSeparator());
                _contextMenu.Items.Add(CreateSectionItem("账号"));

                foreach (var account in accounts)
                {
                    var isCurrent = account.Account.MostRecent;
                    var item = CreateMenuItem(
                        account.DisplayName,
                        string.IsNullOrWhiteSpace(account.Username) ? "" : account.Username,
                        isCurrent ? _palette.Accent : _palette.Text,
                        isCurrent ? "当前" : "");
                    item.Tag = account.SteamId;
                    item.Click += (s, e) =>
                    {
                        if (s is ToolStripMenuItem menuItem && menuItem.Tag is string steamId)
                            AccountSelected?.Invoke(this, steamId);
                    };
                    _contextMenu.Items.Add(item);
                }
            }

            _contextMenu.Items.Add(CreateSeparator());
            var exitItem = CreateMenuItem("退出", "关闭 Steam Switch", _palette.Danger);
            exitItem.Click += (s, e) => ExitRequested?.Invoke(this, EventArgs.Empty);
            _contextMenu.Items.Add(exitItem);
        }

        private void ShowContextMenu()
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var menu = SharedContextMenuBuilder.Build(
                    _accounts,
                    _currentAccount,
                    showMainWindow: ShowOwnerWindow,
                    accountSelected: steamId => AccountSelected?.Invoke(this, steamId),
                    launchSteam: () => LaunchSteamRequested?.Invoke(this, EventArgs.Empty),
                    refresh: null,
                    detach: null,
                    showDetach: false,
                    exit: () => ExitRequested?.Invoke(this, EventArgs.Empty));

                menu.IsOpen = true;
            });
        }

        private ToolStripMenuItem CreateHeaderItem(string title, string detail)
        {
            return new ToolStripMenuItem($"  {title}\n  {detail}")
            {
                Enabled = false,
                ForeColor = _palette.Text,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                AutoSize = false,
                Height = 44,
                Width = 260,
                Padding = new Padding(0)
            };
        }

        private ToolStripMenuItem CreateSectionItem(string text)
        {
            return new ToolStripMenuItem($"  {text}")
            {
                Enabled = false,
                ForeColor = _palette.SecondaryText,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                AutoSize = false,
                Height = 24,
                Width = 260,
                Padding = new Padding(0)
            };
        }

        private ToolStripMenuItem CreateMenuItem(string title, string detail, Color? color = null, string badge = "")
        {
            var suffix = string.IsNullOrWhiteSpace(badge) ? "" : $"    {badge}";
            var secondLine = string.IsNullOrWhiteSpace(detail) ? "" : $"\n  {detail}";
            return new ToolStripMenuItem($"  {title}{suffix}{secondLine}")
            {
                ForeColor = color ?? _palette.Text,
                Font = new Font("Segoe UI", 9, FontStyle.Regular),
                AutoSize = false,
                Height = string.IsNullOrWhiteSpace(detail) ? 34 : 44,
                Width = 260,
                Padding = new Padding(0)
            };
        }

        private ToolStripSeparator CreateSeparator()
        {
            return new ToolStripSeparator
            {
                BackColor = _palette.Separator,
                ForeColor = _palette.Separator
            };
        }

        public void ShowNotification(string title, string message, int timeout = 3000)
        {
            _notifyIcon?.ShowBalloonTip(timeout, title, message, ToolTipIcon.Info);
        }

        public void Dispose()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            _contextMenu?.Dispose();
        }
    }

    public class AppleMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly TrayMenuPalette _palette;

        public AppleMenuRenderer(TrayMenuPalette palette) : base(new AppleColorTable(palette))
        {
            _palette = palette;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(4, 2, e.Item.Width - 8, e.Item.Height - 4);

            if (e.Item.Selected && e.Item.Enabled)
            {
                using var brush = new SolidBrush(_palette.Hover);
                using var path = RoundedRect(rect, 8);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
                return;
            }

            using var baseBrush = new SolidBrush(_palette.Background);
            e.Graphics.FillRectangle(baseBrush, rect);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(_palette.Separator);
            e.Graphics.DrawLine(pen, 18, y, e.Item.Width - 18, y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.ForeColor;
            base.OnRenderItemText(e);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public class AppleColorTable : ProfessionalColorTable
    {
        private readonly TrayMenuPalette _palette;

        public AppleColorTable(TrayMenuPalette palette)
        {
            _palette = palette;
        }

        public override Color ToolStripDropDownBackground => _palette.Background;
        public override Color ImageMarginGradientBegin => _palette.Background;
        public override Color ImageMarginGradientMiddle => _palette.Background;
        public override Color ImageMarginGradientEnd => _palette.Background;
        public override Color MenuBorder => _palette.Border;
        public override Color MenuItemBorder => _palette.Border;
        public override Color MenuItemSelected => _palette.Hover;
        public override Color MenuStripGradientBegin => _palette.Background;
        public override Color MenuStripGradientEnd => _palette.Background;
        public override Color MenuItemSelectedGradientBegin => _palette.Hover;
        public override Color MenuItemSelectedGradientEnd => _palette.Hover;
        public override Color MenuItemPressedGradientBegin => _palette.Hover;
        public override Color MenuItemPressedGradientEnd => _palette.Hover;
        public override Color SeparatorDark => _palette.Separator;
        public override Color SeparatorLight => _palette.Separator;
    }

    public sealed class TrayMenuPalette
    {
        public Color Background { get; private init; }
        public Color Hover { get; private init; }
        public Color Border { get; private init; }
        public Color Text { get; private init; }
        public Color SecondaryText { get; private init; }
        public Color Accent { get; private init; }
        public Color Danger { get; private init; }
        public Color Separator { get; private init; }

        public static TrayMenuPalette FromApplication()
        {
            var resources = System.Windows.Application.Current.Resources;
            var windowBrush = GetBrush(resources, "WindowBgBrush", MediaColor.FromRgb(25, 25, 28));
            var isLight = GetLuminance(windowBrush) > 0.55;

            return isLight
                ? new TrayMenuPalette
                {
                    Background = Color.FromArgb(248, 248, 250),
                    Hover = Color.FromArgb(232, 232, 237),
                    Border = Color.FromArgb(210, 210, 216),
                    Text = Color.FromArgb(28, 28, 30),
                    SecondaryText = Color.FromArgb(99, 99, 102),
                    Accent = Color.FromArgb(0, 122, 255),
                    Danger = Color.FromArgb(255, 59, 48),
                    Separator = Color.FromArgb(218, 218, 224)
                }
                : new TrayMenuPalette
                {
                    Background = Color.FromArgb(34, 34, 36),
                    Hover = Color.FromArgb(50, 50, 53),
                    Border = Color.FromArgb(68, 68, 72),
                    Text = Color.FromArgb(245, 245, 247),
                    SecondaryText = Color.FromArgb(174, 174, 178),
                    Accent = Color.FromArgb(10, 132, 255),
                    Danger = Color.FromArgb(255, 69, 58),
                    Separator = Color.FromArgb(62, 62, 66)
                };
        }

        private static MediaBrush GetBrush(ResourceDictionary resources, string key, MediaColor fallback)
        {
            if (resources.Contains(key) && resources[key] is MediaBrush brush)
                return brush;
            return new SolidColorBrush(fallback);
        }

        private static double GetLuminance(MediaBrush brush)
        {
            if (brush is not SolidColorBrush solid)
                return 0;

            var c = solid.Color;
            return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        }
    }
}
