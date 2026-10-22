using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SteamSwitcher.Core;
using SteamSwitcher.ViewModels;

namespace SteamSwitcher.Services;

/// <summary>One accessible MenuItem tree for the tray and both floating surfaces.</summary>
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
        Action<bool>? toggleTaskbarPinned = null,
        bool isTaskbarLyricsEnabled = false,
        Action<bool>? toggleTaskbarLyrics = null,
        Action? emergencyRestore = null,
        Action? exit = null,
        Action? retryLyrics = null,
        bool canRetryLyrics = false)
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.SetResourceReference(FrameworkElement.StyleProperty, "ModernContextMenu");
        AutomationProperties.SetName(menu, "Steam Switch 快捷菜单");
        menu.Items.Add(Section("Steam Switch", "账号与桌面快捷控制"));
        if (showMainWindow != null) menu.Items.Add(ActionItem("打开主界面", "\uE8A1", showMainWindow));
        if (launchSteam != null) menu.Items.Add(ActionItem("启动 Steam", "\uE768", launchSteam));

        var list = accounts?.OrderByDescending(account => account.IsCurrent)
            .ThenByDescending(account => account.IsPinned).ThenBy(account => account.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (list is { Length: > 0 })
        {
            var accountMenu = ActionItem($"切换账号 · {list.Length}", "\uE77B");
            accountMenu.Tag = "Accounts";
            accountMenu.IsEnabled = accountSelected != null;
            accountMenu.Items.Add(Section("选择账号", "仅本地保存的账号可能需要重新验证登录"));
            var actualCurrent = list.FirstOrDefault(account => account.IsCurrent) ?? (currentAccount?.IsCurrent == true ? currentAccount : null);
            foreach (var account in list)
            {
                var isCurrent = AccountManager.AreSameAccount(account.Account, actualCurrent?.Account);
                var item = ActionItem(account.DisplayName, "\uE77B", () => accountSelected?.Invoke(account.SteamId));
                item.Tag = account.SteamId;
                item.IsChecked = isCurrent;
                item.Header = AccountHeader(account, isCurrent);
                item.ToolTip = $"登录名：{account.Username}\nSteam ID：{account.SteamId}" +
                    (account.IsArchived ? "\n仅本地记录：找回后可能需要密码或验证码" : "");
                AutomationProperties.SetName(item, account.DisplayName + (isCurrent ? "，当前账号" : account.IsArchived ? "，待找回账号" : ""));
                accountMenu.Items.Add(item);
            }
            if (showMainWindow != null)
            {
                accountMenu.Items.Add(new Separator());
                accountMenu.Items.Add(ActionItem("打开主界面管理…", "\uE8A1", showMainWindow));
            }
            menu.Items.Add(accountMenu);
        }

        var windows = ActionItem("窗口与显示", "\uE7F4");
        windows.Tag = "Windows";
        if (toggleTaskbarPinned != null) windows.Items.Add(ToggleItem("任务栏常驻", "\uE840", isTaskbarPinned, toggleTaskbarPinned));
        if (toggleDesktopFloating != null) windows.Items.Add(ToggleItem("桌面悬浮窗", "\uE7B5", isDesktopFloatingEnabled, toggleDesktopFloating));
        if (toggleDesktopFloatingTopmost != null)
        {
            var topmost = ToggleItem("悬浮窗置顶", "\uE18A", isDesktopFloatingTopmost, toggleDesktopFloatingTopmost);
            topmost.IsEnabled = isDesktopFloatingEnabled;
            topmost.ToolTip = isDesktopFloatingEnabled ? "保持在其他窗口上方" : "先开启桌面悬浮窗";
            windows.Items.Add(topmost);
        }
        if (toggleTaskbarLyrics != null)
        {
            if (windows.Items.Count > 0) windows.Items.Add(new Separator());
            windows.Items.Add(ToggleItem("任务栏歌词", "\uE8D6", isTaskbarLyricsEnabled, toggleTaskbarLyrics));
        }
        if (showDetach && detach != null)
        {
            if (windows.Items.Count > 0) windows.Items.Add(new Separator());
            windows.Items.Add(ActionItem("分离任务栏", "\uE898", detach));
        }
        if (windows.Items.Count > 0) menu.Items.Add(windows);

        var tools = ActionItem("维护与恢复", "\uE713");
        tools.Tag = "Tools";
        if (refresh != null) tools.Items.Add(ActionItem("刷新快捷入口", "\uE72C", refresh));
        if (retryLyrics != null)
        {
            var retry = ActionItem("重新获取当前歌词", "\uE8D6", retryLyrics);
            retry.Tag = "RetryLyrics";
            retry.IsEnabled = canRetryLyrics;
            retry.ToolTip = canRetryLyrics ? "重新查询当前歌曲的在线歌词" : "需开启在线任务栏歌词，并有可控制的媒体正在播放";
            tools.Items.Add(retry);
        }
        if (emergencyRestore != null)
            tools.Items.Add(ActionItem("恢复桌面与任务栏", "\uE7BA", emergencyRestore));
        if (tools.Items.Count > 0) menu.Items.Add(tools);
        menu.Items.Add(new Separator());
        var exitItem = ActionItem("退出 Steam Switch", "\uE7E7", exit ?? (() => Application.Current.Shutdown()));
        exitItem.SetResourceReference(Control.ForegroundProperty, "DangerFgBrush");
        exitItem.ToolTip = "关闭程序并恢复由本程序调整的桌面状态";
        menu.Items.Add(exitItem);
        return menu;
    }

    internal static MenuItem ActionItem(string title, string icon, Action? action = null)
    {
        var item = new MenuItem { Header = title, Icon = new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14 } };
        item.SetResourceReference(FrameworkElement.StyleProperty, "ModernMenuItem");
        AutomationProperties.SetName(item, title);
        if (action != null) item.Click += (_, e) => { if (!item.IsEnabled) return; e.Handled = true; action(); };
        return item;
    }

    private static MenuItem ToggleItem(string title, string icon, bool enabled, Action<bool> toggle)
    {
        var item = ActionItem(title, icon);
        item.IsCheckable = true;
        item.IsChecked = enabled;
        item.InputGestureText = enabled ? "已开启" : "未开启";
        item.Click += (_, e) => { if (!item.IsEnabled) return; e.Handled = true; toggle(item.IsChecked); };
        return item;
    }

    private static MenuItem Section(string title, string detail)
    {
        var text = new StackPanel();
        var heading = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        text.Children.Add(heading);
        var hint = new TextBlock { Text = detail, FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 270 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        text.Children.Add(hint);
        var section = new MenuItem { Header = text };
        section.SetResourceReference(FrameworkElement.StyleProperty, "MenuSectionItem");
        return section;
    }

    private static Grid AccountHeader(AccountViewModel account, bool isCurrent)
    {
        var grid = new Grid { MaxWidth = 260 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        copy.Children.Add(new TextBlock { Text = account.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal });
        var login = new TextBlock { Text = account.Username, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        login.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        copy.Children.Add(login);
        grid.Children.Add(copy);
        var badgeText = isCurrent ? "当前" : account.IsArchived ? "待找回" : account.IsPinned ? "常用" : "";
        if (badgeText.Length > 0)
        {
            var label = new TextBlock { Text = badgeText, FontSize = 10, FontWeight = FontWeights.SemiBold };
            label.SetResourceReference(TextBlock.ForegroundProperty, account.IsArchived ? "WarningFgBrush" : "AccentBrush");
            var badge = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 2, 5, 2), VerticalAlignment = VerticalAlignment.Center, Child = label };
            badge.SetResourceReference(Border.BackgroundProperty, account.IsArchived ? "WarningBgBrush" : "AccentBgBrush");
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
        }
        return grid;
    }
}
