using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SteamSwitcher.ViewModels;

namespace SteamSwitcher.Services
{
    public static class SharedContextMenuBuilder
    {
        public static ContextMenu Build(
            ObservableCollection<AccountViewModel>? accounts,
            AccountViewModel? currentAccount,
            Action? showMainWindow,
            Action<string>? accountSelected,
            Action? launchSteam,
            Action? refresh,
            Action? detach,
            bool showDetach = false,
            bool isDesktopFloatingEnabled = false,
            bool isDesktopFloatingTopmost = true,
            bool isTaskbarPinned = false,
            Action<bool>? toggleDesktopFloating = null,
            Action<bool>? toggleDesktopFloatingTopmost = null,
            Action<bool>? toggleTaskbarPinned = null)
        {
            var r = Application.Current.Resources;
            var bg = GetBrush(r, "CardBgBrush", Color.FromArgb(230, 28, 28, 30));
            var border = GetBrush(r, "BorderBrush", Color.FromArgb(200, 56, 56, 58));
            var fg = GetBrush(r, "TextPrimaryBrush", Color.FromRgb(255, 255, 255));
            var fgSecondary = GetBrush(r, "TextSecondaryBrush", Color.FromRgb(152, 152, 157));
            var fgTertiary = GetBrush(r, "TextTertiaryBrush", Color.FromRgb(108, 108, 114));
            var accent = GetBrush(r, "AccentBrush", Color.FromRgb(10, 132, 255));
            var hoverBg = GetBrush(r, "HoverBgBrush", Color.FromArgb(26, 255, 255, 255));
            var successFg = new SolidColorBrush(Color.FromRgb(48, 209, 88));

            var menu = new ContextMenu();
            menu.Background = bg;
            menu.BorderBrush = border;
            menu.Foreground = fg;
            menu.BorderThickness = new Thickness(1);
            menu.Padding = new Thickness(4);

            // 标题
            var titleItem = new MenuItem
            {
                Header = "⚡ Steam Switch",
                IsEnabled = false,
                Foreground = fgSecondary,
                FontWeight = FontWeights.SemiBold
            };
            menu.Items.Add(titleItem);
            menu.Items.Add(CreateSeparator(r));

            // 显示主窗口
            if (showMainWindow != null)
            {
                var showItem = new MenuItem { Header = "📺  显示主窗口" };
                showItem.Click += (s, e) => showMainWindow();
                menu.Items.Add(showItem);
            }

            // 启动 Steam
            if (launchSteam != null)
            {
                var launchItem = new MenuItem { Header = "🎮  启动 Steam" };
                launchItem.Click += (s, e) => launchSteam();
                menu.Items.Add(launchItem);
            }

            menu.Items.Add(CreateSeparator(r));

            // 窗口控制
            var windowsHeader = new MenuItem
            {
                Header = "🪟  窗口控制",
                IsEnabled = false,
                Foreground = fgTertiary
            };
            menu.Items.Add(windowsHeader);

            // 任务栏悬浮窗开关
            if (toggleTaskbarPinned != null)
            {
                var taskbarItem = new MenuItem
                {
                    Header = isTaskbarPinned ? "✓  任务栏悬浮窗" : "○  任务栏悬浮窗",
                    Foreground = isTaskbarPinned ? successFg : fg,
                    FontWeight = isTaskbarPinned ? FontWeights.SemiBold : FontWeights.Normal
                };
                taskbarItem.Click += (s, e) => toggleTaskbarPinned(!isTaskbarPinned);
                menu.Items.Add(taskbarItem);
            }

            // 桌面悬浮窗开关
            if (toggleDesktopFloating != null)
            {
                var floatingItem = new MenuItem
                {
                    Header = isDesktopFloatingEnabled ? "✓  桌面悬浮窗" : "○  桌面悬浮窗",
                    Foreground = isDesktopFloatingEnabled ? successFg : fg,
                    FontWeight = isDesktopFloatingEnabled ? FontWeights.SemiBold : FontWeights.Normal
                };
                floatingItem.Click += (s, e) => toggleDesktopFloating(!isDesktopFloatingEnabled);
                menu.Items.Add(floatingItem);
            }

            // 桌面悬浮窗置顶开关
            if (toggleDesktopFloatingTopmost != null)
            {
                var topmostItem = new MenuItem
                {
                    Header = isDesktopFloatingTopmost ? "✓  悬浮窗置顶" : "○  悬浮窗置顶",
                    Foreground = isDesktopFloatingTopmost ? accent : fg,
                    FontWeight = isDesktopFloatingTopmost ? FontWeights.SemiBold : FontWeights.Normal
                };
                topmostItem.Click += (s, e) => toggleDesktopFloatingTopmost(!isDesktopFloatingTopmost);
                menu.Items.Add(topmostItem);
            }

            menu.Items.Add(CreateSeparator(r));

            // 账号列表
            if (accounts != null && accounts.Count > 0)
            {
                var accountsHeader = new MenuItem
                {
                    Header = "👤  账号",
                    IsEnabled = false,
                    Foreground = fgTertiary
                };
                menu.Items.Add(accountsHeader);

                foreach (var account in accounts)
                {
                    var isCurrent = account.Account.MostRecent;
                    var prefix = isCurrent ? "✓  " : "○  ";
                    var item = new MenuItem
                    {
                        Header = $"{prefix}{account.DisplayName}",
                        Foreground = isCurrent ? successFg : fg,
                        FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                        Tag = account.SteamId
                    };
                    if (!string.IsNullOrEmpty(account.Username))
                        item.ToolTip = account.Username;
                    item.Click += (s, e) =>
                    {
                        if (s is MenuItem mi && mi.Tag is string steamId)
                            accountSelected?.Invoke(steamId);
                    };
                    menu.Items.Add(item);
                }

                menu.Items.Add(CreateSeparator(r));
            }

            // 刷新
            if (refresh != null)
            {
                var refreshItem = new MenuItem { Header = "🔄  刷新" };
                refreshItem.Click += (s, e) => refresh();
                menu.Items.Add(refreshItem);
            }

            // 分离
            if (showDetach && detach != null)
            {
                var detachItem = new MenuItem { Header = "📌  分离任务栏" };
                detachItem.Click += (s, e) => detach();
                menu.Items.Add(detachItem);
            }

            menu.Items.Add(CreateSeparator(r));

            // 退出
            var exitItem = new MenuItem
            {
                Header = "⏻  退出",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 69, 58))
            };
            exitItem.Click += (s, e) => Application.Current.Shutdown();
            menu.Items.Add(exitItem);

            return menu;
        }

        private static Separator CreateSeparator(ResourceDictionary r)
        {
            var sep = new Separator();
            var brush = GetBrush(r, "SeparatorBrush", Color.FromArgb(34, 255, 255, 255));
            sep.Background = brush;
            sep.Margin = new Thickness(4, 2, 4, 2);
            return sep;
        }

        private static Brush GetBrush(ResourceDictionary r, string key, Color fallback)
        {
            if (r.Contains(key) && r[key] is Brush brush)
                return brush;
            return new SolidColorBrush(fallback);
        }
    }
}
