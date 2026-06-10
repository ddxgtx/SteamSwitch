using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using SteamSwitcher.ViewModels;
using FontStyle = System.Drawing.FontStyle;

namespace SteamSwitcher.Services
{
    public class TrayIconService : IDisposable
    {
        private NotifyIcon? _notifyIcon;
        private Window _ownerWindow;
        private ContextMenuStrip? _contextMenu;

        public event EventHandler<string>? AccountSelected;
        public event EventHandler? LaunchSteamRequested;

        // 深色主题颜色
        private static readonly Color BgColor = Color.FromArgb(28, 28, 30);
        private static readonly Color BgHoverColor = Color.FromArgb(44, 44, 46);
        private static readonly Color TextColor = Color.FromArgb(255, 255, 255);
        private static readonly Color TextSecondaryColor = Color.FromArgb(152, 152, 157);
        private static readonly Color AccentColor = Color.FromArgb(10, 132, 255);
        private static readonly Color SeparatorColor = Color.FromArgb(56, 56, 58);

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
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icon.png");
                if (File.Exists(iconPath))
                {
                    using var bitmap = new Bitmap(iconPath);
                    _notifyIcon.Icon = Icon.FromHandle(bitmap.GetHicon());
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

            _contextMenu = new ContextMenuStrip();
            _contextMenu.Renderer = new DarkRenderer();
            _contextMenu.BackColor = BgColor;
            _contextMenu.ForeColor = TextColor;
            _contextMenu.Padding = new Padding(8, 4, 8, 4);
            _notifyIcon.ContextMenuStrip = _contextMenu;
        }

        private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _ownerWindow.Show();
                _ownerWindow.WindowState = WindowState.Normal;
                _ownerWindow.Activate();
            }
        }

        private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
        {
            _ownerWindow.Show();
            _ownerWindow.WindowState = WindowState.Normal;
            _ownerWindow.Activate();
        }

        public void UpdateMenu(ObservableCollection<AccountViewModel> accounts, AccountViewModel? currentAccount)
        {
            if (_contextMenu == null) return;

            _contextMenu.Items.Clear();

            // 标题
            var titleItem = new ToolStripMenuItem("⚡ Steam Switch");
            titleItem.Enabled = false;
            titleItem.ForeColor = TextSecondaryColor;
            titleItem.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            _contextMenu.Items.Add(titleItem);

            _contextMenu.Items.Add(CreateSeparator());

            // 显示主窗口
            var showItem = new ToolStripMenuItem("📺  显示主窗口");
            showItem.ForeColor = TextColor;
            showItem.Click += (s, e) =>
            {
                _ownerWindow.Show();
                _ownerWindow.WindowState = WindowState.Normal;
                _ownerWindow.Activate();
            };
            _contextMenu.Items.Add(showItem);

            _contextMenu.Items.Add(CreateSeparator());

            // 账号列表
            var accountsHeader = new ToolStripMenuItem("账号");
            accountsHeader.Enabled = false;
            accountsHeader.ForeColor = TextSecondaryColor;
            accountsHeader.Font = new Font("Segoe UI", 8);
            _contextMenu.Items.Add(accountsHeader);

            foreach (var account in accounts)
            {
                var isCurrent = account.Account.MostRecent;
                var prefix = isCurrent ? "● " : "○ ";
                var menuItem = new ToolStripMenuItem($"{prefix}{account.DisplayName}");
                menuItem.Tag = account.SteamId;
                menuItem.ForeColor = isCurrent ? AccentColor : TextColor;
                menuItem.Font = isCurrent 
                    ? new Font("Segoe UI", 9, FontStyle.Bold) 
                    : new Font("Segoe UI", 9);
                
                if (isCurrent)
                {
                    menuItem.Text = $"  ✓ {account.DisplayName}";
                }

                menuItem.Click += (s, e) =>
                {
                    if (s is ToolStripMenuItem item && item.Tag is string steamId)
                    {
                        AccountSelected?.Invoke(this, steamId);
                    }
                };

                _contextMenu.Items.Add(menuItem);
            }

            _contextMenu.Items.Add(CreateSeparator());

            // 启动Steam
            var launchItem = new ToolStripMenuItem("🎮  启动Steam");
            launchItem.ForeColor = TextColor;
            launchItem.Click += (s, e) =>
            {
                LaunchSteamRequested?.Invoke(this, EventArgs.Empty);
            };
            _contextMenu.Items.Add(launchItem);

            _contextMenu.Items.Add(CreateSeparator());

            // 退出
            var exitItem = new ToolStripMenuItem("⏻  退出");
            exitItem.ForeColor = Color.FromArgb(255, 69, 58);
            exitItem.Click += (s, e) =>
            {
                Dispose();
                System.Windows.Application.Current.Shutdown();
            };
            _contextMenu.Items.Add(exitItem);
        }

        private ToolStripSeparator CreateSeparator()
        {
            return new ToolStripSeparator
            {
                BackColor = SeparatorColor,
                ForeColor = SeparatorColor
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

    // 自定义深色渲染器
    public class DarkRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color BgColor = Color.FromArgb(28, 28, 30);
        private static readonly Color BgHoverColor = Color.FromArgb(44, 44, 46);
        private static readonly Color TextColor = Color.FromArgb(255, 255, 255);
        private static readonly Color AccentColor = Color.FromArgb(10, 132, 255);

        public DarkRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            
            if (e.Item.Selected && e.Item.Enabled)
            {
                using var brush = new SolidBrush(BgHoverColor);
                e.Graphics.FillRectangle(brush, rect);
            }
            else if (!e.Item.Enabled)
            {
                using var brush = new SolidBrush(Color.FromArgb(15, 255, 255, 255));
                e.Graphics.FillRectangle(brush, rect);
            }
            else
            {
                using var brush = new SolidBrush(BgColor);
                e.Graphics.FillRectangle(brush, rect);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var rect = new Rectangle(16, e.Item.Height / 2, e.Item.Width - 32, 1);
            using var pen = new Pen(Color.FromArgb(56, 56, 58));
            e.Graphics.DrawLine(pen, rect.X, rect.Y, rect.X + rect.Width, rect.Y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? TextColor : e.Item.ForeColor;
            base.OnRenderItemText(e);
        }
    }

    public class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(28, 28, 30);
        public override Color ImageMarginGradientBegin => Color.FromArgb(28, 28, 30);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(28, 28, 30);
        public override Color ImageMarginGradientEnd => Color.FromArgb(28, 28, 30);
        public override Color MenuBorder => Color.FromArgb(56, 56, 58);
        public override Color MenuItemBorder => Color.FromArgb(56, 56, 58);
        public override Color MenuItemSelected => Color.FromArgb(44, 44, 46);
        public override Color MenuStripGradientBegin => Color.FromArgb(28, 28, 30);
        public override Color MenuStripGradientEnd => Color.FromArgb(28, 28, 30);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(44, 44, 46);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(44, 44, 46);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(44, 44, 46);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(44, 44, 46);
        public override Color SeparatorDark => Color.FromArgb(56, 56, 58);
        public override Color SeparatorLight => Color.FromArgb(56, 56, 58);
    }
}
