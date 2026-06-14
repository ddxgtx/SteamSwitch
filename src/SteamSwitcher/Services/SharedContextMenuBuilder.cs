using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
            Action<bool>? toggleTaskbarPinned = null,
            Action? exit = null)
        {
            var palette = MenuPalette.FromApplication();
            var menu = CreateMenuHost(palette);
            var stack = new StackPanel();

            stack.Children.Add(CreateTitle(palette));
            stack.Children.Add(CreateDivider(palette));

            if (showMainWindow != null)
            {
                stack.Children.Add(CreateRow(
                    menu,
                    "显示主窗口",
                    "恢复应用窗口",
                    null,
                    palette,
                    showMainWindow));
            }

            if (launchSteam != null)
            {
                stack.Children.Add(CreateRow(
                    menu,
                    "启动 Steam",
                    "按静默模式设置启动",
                    null,
                    palette,
                    launchSteam));
            }

            stack.Children.Add(CreateDivider(palette));
            stack.Children.Add(CreateSection("窗口", palette));

            if (toggleTaskbarPinned != null)
            {
                stack.Children.Add(CreateRow(
                    menu,
                    "任务栏常驻",
                    "固定账号与游戏入口",
                    isTaskbarPinned ? "开启" : "关闭",
                    palette,
                    () => toggleTaskbarPinned(!isTaskbarPinned),
                    active: isTaskbarPinned));
            }

            if (toggleDesktopFloating != null)
            {
                stack.Children.Add(CreateRow(
                    menu,
                    "桌面悬浮窗",
                    "桌面快捷入口",
                    isDesktopFloatingEnabled ? "开启" : "关闭",
                    palette,
                    () => toggleDesktopFloating(!isDesktopFloatingEnabled),
                    active: isDesktopFloatingEnabled));
            }

            if (toggleDesktopFloatingTopmost != null)
            {
                stack.Children.Add(CreateRow(
                    menu,
                    "悬浮窗置顶",
                    "保持在其他窗口上方",
                    isDesktopFloatingTopmost ? "开启" : "关闭",
                    palette,
                    () => toggleDesktopFloatingTopmost(!isDesktopFloatingTopmost),
                    active: isDesktopFloatingTopmost));
            }

            if (accounts != null && accounts.Count > 0)
            {
                stack.Children.Add(CreateDivider(palette));
                stack.Children.Add(CreateSection("账号", palette));

                foreach (var account in accounts)
                {
                    var isCurrent = account == currentAccount || account.Account.MostRecent;
                    var row = CreateRow(
                        menu,
                        account.DisplayName,
                        account.Username,
                        isCurrent ? "当前" : null,
                        palette,
                        () => accountSelected?.Invoke(account.SteamId),
                        active: isCurrent);

                    row.Tag = account.SteamId;
                    if (!string.IsNullOrWhiteSpace(account.Username))
                        row.ToolTip = account.Username;
                    stack.Children.Add(row);
                }
            }

            if (refresh != null || (showDetach && detach != null))
            {
                stack.Children.Add(CreateDivider(palette));

                if (refresh != null)
                {
                    stack.Children.Add(CreateRow(
                        menu,
                        "刷新",
                        "重新读取固定入口",
                        null,
                        palette,
                        refresh));
                }

                if (showDetach && detach != null)
                {
                    stack.Children.Add(CreateRow(
                        menu,
                        "分离任务栏",
                        "取消任务栏常驻",
                        null,
                        palette,
                        detach));
                }
            }

            stack.Children.Add(CreateDivider(palette));
            stack.Children.Add(CreateRow(
                menu,
                "退出",
                "关闭 Steam Switch",
                null,
                palette,
                () =>
                {
                    if (exit != null)
                        exit();
                    else
                        Application.Current.Shutdown();
                },
                danger: true));

            menu.Items.Add(CreateShell(stack, palette));
            return menu;
        }

        private static ContextMenu CreateMenuHost(MenuPalette palette)
        {
            var menu = new ContextMenu
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HasDropShadow = false,
                SnapsToDevicePixels = true,
                Focusable = false,
                StaysOpen = false,
                Placement = PlacementMode.MousePoint
            };

            var template = new ControlTemplate(typeof(ContextMenu));
            var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            template.VisualTree = presenter;
            menu.Template = template;
            menu.ItemContainerStyle = CreateHostItemStyle();

            return menu;
        }

        private static Style CreateHostItemStyle()
        {
            var style = new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(UIElement.FocusableProperty, false));

            var template = new ControlTemplate(typeof(MenuItem));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            template.VisualTree = content;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));

            return style;
        }

        private static Border CreateShell(UIElement content, MenuPalette palette)
        {
            return new Border
            {
                Width = 304,
                Padding = new Thickness(8),
                Background = palette.Background,
                BorderBrush = palette.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                SnapsToDevicePixels = true,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = palette.IsLight ? 28 : 34,
                    ShadowDepth = 10,
                    Opacity = palette.IsLight ? 0.16 : 0.38
                },
                Child = content
            };
        }

        private static FrameworkElement CreateTitle(MenuPalette palette)
        {
            var grid = new Grid
            {
                Margin = new Thickness(8, 7, 8, 8),
                MinHeight = 38
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            copy.Children.Add(new TextBlock
            {
                Text = "Steam Switch",
                Foreground = palette.Text,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            copy.Children.Add(new TextBlock
            {
                Text = "本地快捷控制",
                Foreground = palette.SecondaryText,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            grid.Children.Add(copy);

            var chip = CreateBadge("工具", palette.SubtleFill, palette.SecondaryText);
            chip.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);

            return grid;
        }

        private static FrameworkElement CreateSection(string text, MenuPalette palette)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = palette.TertiaryText,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 7, 10, 4),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        private static Button CreateRow(
            ContextMenu menu,
            string title,
            string detail,
            string? badge,
            MenuPalette palette,
            Action action,
            bool active = false,
            bool danger = false)
        {
            var button = new Button
            {
                MinHeight = 46,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 1, 0, 1),
                Background = active ? palette.ActiveFill : Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                Style = CreateRowStyle(palette, active),
                Content = CreateRowContent(title, detail, badge, palette, active, danger)
            };

            button.Click += (_, _) =>
            {
                menu.IsOpen = false;
                action();
            };

            return button;
        }

        private static Grid CreateRowContent(
            string title,
            string detail,
            string? badge,
            MenuPalette palette,
            bool active,
            bool danger)
        {
            var grid = new Grid
            {
                Margin = new Thickness(10, 0, 10, 0)
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = active ? new GridLength(5) : new GridLength(0) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (active)
            {
                var mark = new Border
                {
                    Width = 3,
                    Height = 22,
                    CornerRadius = new CornerRadius(2),
                    Background = palette.Accent,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                grid.Children.Add(mark);
            }

            var text = new StackPanel
            {
                Margin = new Thickness(active ? 8 : 0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            text.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = danger ? palette.Danger : palette.Text,
                FontSize = 13,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (!string.IsNullOrWhiteSpace(detail))
            {
                text.Children.Add(new TextBlock
                {
                    Text = detail,
                    Foreground = palette.SecondaryText,
                    FontSize = 10.5,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (!string.IsNullOrWhiteSpace(badge))
            {
                var badgeElement = CreateBadge(
                    badge,
                    active ? palette.ActiveBadgeFill : palette.SubtleFill,
                    active ? palette.Accent : palette.TertiaryText);
                badgeElement.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(badgeElement, 2);
                grid.Children.Add(badgeElement);
            }

            return grid;
        }

        private static Style CreateRowStyle(MenuPalette palette, bool active)
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, palette.Text));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateRowTemplate(palette, active)));
            return style;
        }

        private static ControlTemplate CreateRowTemplate(MenuPalette palette, bool active)
        {
            var template = new ControlTemplate(typeof(Button));

            var root = new FrameworkElementFactory(typeof(Border));
            root.Name = "Root";
            root.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            root.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            root.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            root.AppendChild(content);

            template.VisualTree = root;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, palette.HoverFill, "Root"));
            template.Triggers.Add(hover);

            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, palette.PressedFill, "Root"));
            template.Triggers.Add(pressed);

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));
            template.Triggers.Add(disabled);

            return template;
        }

        private static Border CreateBadge(string text, Brush background, Brush foreground)
        {
            return new Border
            {
                Background = background,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = foreground,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
        }

        private static FrameworkElement CreateDivider(MenuPalette palette)
        {
            return new Border
            {
                Height = 1,
                Margin = new Thickness(10, 6, 10, 6),
                Background = palette.Separator
            };
        }

        private sealed class MenuPalette
        {
            public bool IsLight { get; init; }
            public Brush Background { get; init; } = Brushes.Transparent;
            public Brush Border { get; init; } = Brushes.Transparent;
            public Brush Text { get; init; } = Brushes.White;
            public Brush SecondaryText { get; init; } = Brushes.White;
            public Brush TertiaryText { get; init; } = Brushes.White;
            public Brush Accent { get; init; } = Brushes.DodgerBlue;
            public Brush Danger { get; init; } = Brushes.IndianRed;
            public Brush HoverFill { get; init; } = Brushes.Transparent;
            public Brush PressedFill { get; init; } = Brushes.Transparent;
            public Brush ActiveFill { get; init; } = Brushes.Transparent;
            public Brush ActiveBadgeFill { get; init; } = Brushes.Transparent;
            public Brush SubtleFill { get; init; } = Brushes.Transparent;
            public Brush Separator { get; init; } = Brushes.Transparent;

            public static MenuPalette FromApplication()
            {
                var resources = Application.Current.Resources;
                var windowBrush = GetBrush(resources, "WindowBgBrush", Color.FromRgb(25, 25, 28));
                var isLight = GetLuminance(windowBrush) > 0.55;

                return isLight
                    ? new MenuPalette
                    {
                        IsLight = true,
                        Background = Solid(248, 248, 250, 248),
                        Border = Solid(209, 209, 214, 214),
                        Text = Solid(28, 28, 30),
                        SecondaryText = Solid(99, 99, 102),
                        TertiaryText = Solid(142, 142, 147),
                        Accent = Solid(0, 122, 255),
                        Danger = Solid(255, 59, 48),
                        HoverFill = Solid(232, 232, 237, 232),
                        PressedFill = Solid(218, 218, 224, 238),
                        ActiveFill = Solid(0, 122, 255, 18),
                        ActiveBadgeFill = Solid(0, 122, 255, 26),
                        SubtleFill = Solid(118, 118, 128, 24),
                        Separator = Solid(60, 60, 67, 30)
                    }
                    : new MenuPalette
                    {
                        IsLight = false,
                        Background = Solid(31, 31, 34, 248),
                        Border = Solid(255, 255, 255, 42),
                        Text = Solid(245, 245, 247),
                        SecondaryText = Solid(174, 174, 178),
                        TertiaryText = Solid(132, 132, 138),
                        Accent = Solid(10, 132, 255),
                        Danger = Solid(255, 69, 58),
                        HoverFill = Solid(255, 255, 255, 18),
                        PressedFill = Solid(255, 255, 255, 28),
                        ActiveFill = Solid(10, 132, 255, 28),
                        ActiveBadgeFill = Solid(10, 132, 255, 36),
                        SubtleFill = Solid(255, 255, 255, 14),
                        Separator = Solid(255, 255, 255, 22)
                    };
            }

            private static SolidColorBrush Solid(byte r, byte g, byte b, byte a = 255)
            {
                var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
                brush.Freeze();
                return brush;
            }

            private static double GetLuminance(Brush brush)
            {
                if (brush is not SolidColorBrush solid)
                    return 0;

                var color = solid.Color;
                return (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
            }
        }

        private static Brush GetBrush(ResourceDictionary resources, string key, Color fallback)
        {
            if (resources.Contains(key) && resources[key] is Brush brush)
                return brush;

            return new SolidColorBrush(fallback);
        }
    }
}
