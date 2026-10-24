using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SteamSwitcher.Core;

namespace SteamSwitcher.Views
{
    public partial class TaskbarFlyoutWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public event EventHandler? PrevClicked;
        public event EventHandler? PlayPauseClicked;
        public event EventHandler? NextClicked;
        public event EventHandler? FlyoutMouseEnter;
        public event EventHandler? FlyoutMouseLeave;

        private bool _isControlsMode;
        private bool _isVolumeMode;
        private bool _isNotificationMode;

        public TaskbarFlyoutWindow()
        {
            InitializeComponent();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        public void ShowControls(Point centerScreenPoint, double anchorTop, bool isPlaying)
        {
            _isControlsMode = true;
            _isVolumeMode = false;
            _isNotificationMode = false;

            VolumeHost.Visibility = Visibility.Collapsed;
            NotificationHost.Visibility = Visibility.Collapsed;
            ControlsHost.Visibility = Visibility.Visible;
            PlayPauseButton.Content = isPlaying ? "\uE769" : "\uE768";

            var (windowWidth, windowHeight) = MeasureWindowSize(148, 56);
            double effectiveTop = Math.Min(anchorTop, SystemParameters.WorkArea.Bottom);

            var placement = FlyoutPlacement.Clamp(
                new Rect(centerScreenPoint.X - (windowWidth / 2.0), effectiveTop - windowHeight - 4, windowWidth, windowHeight),
                FlyoutPlacement.VirtualScreen());
            Left = placement.Left;
            Top = placement.Top;

            if (Visibility != Visibility.Visible)
            {
                Opacity = 0;
                Visibility = Visibility.Visible;
            }

            BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            });
            ControlsTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            });
        }

        public void ShowVolume(Point centerScreenPoint, double anchorTop, float volume, bool isMuted)
        {
            _isVolumeMode = true;
            _isControlsMode = false;
            _isNotificationMode = false;

            ControlsHost.Visibility = Visibility.Collapsed;
            NotificationHost.Visibility = Visibility.Collapsed;
            VolumeHost.Visibility = Visibility.Visible;

            int percent = (int)Math.Round(volume * 100);
            VolumeProgressBar.Value = percent;
            VolumePercentText.Text = $"{percent}%";
            VolumeIcon.Text = (isMuted || percent == 0)
                ? "\uE74F" // 静音
                : (percent < 33 ? "\uE992" : (percent < 66 ? "\uE994" : "\uE767"));

            var (windowWidth, windowHeight) = MeasureWindowSize(188, 56);
            double effectiveTop = Math.Min(anchorTop, SystemParameters.WorkArea.Bottom);

            var placement = FlyoutPlacement.Clamp(
                new Rect(centerScreenPoint.X - (windowWidth / 2.0), effectiveTop - windowHeight - 4, windowWidth, windowHeight),
                FlyoutPlacement.VirtualScreen());
            Left = placement.Left;
            Top = placement.Top;

            if (Visibility != Visibility.Visible)
            {
                Opacity = 0;
                Visibility = Visibility.Visible;
            }

            BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            });
            VolumeTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            });
        }

        private (double Width, double Height) MeasureWindowSize(double fallbackWidth, double fallbackHeight)
        {
            try
            {
                if (Content is FrameworkElement root)
                {
                    root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var desired = root.DesiredSize;
                    if (desired.Width > 1 && desired.Height > 1)
                    {
                        root.Arrange(new Rect(desired));
                        root.UpdateLayout();
                        return (desired.Width, desired.Height);
                    }
                }

                UpdateLayout();
                double w = ActualWidth > 1 ? ActualWidth : (DesiredSize.Width > 1 ? DesiredSize.Width : fallbackWidth);
                double h = ActualHeight > 1 ? ActualHeight : (DesiredSize.Height > 1 ? DesiredSize.Height : fallbackHeight);
                return (w, h);
            }
            catch
            {
                return (fallbackWidth, fallbackHeight);
            }
        }

        public void ConfigureNotificationContent(string message, string? avatarPath = null, string? customCategory = null)
        {
            _isNotificationMode = true;
            _isControlsMode = false;
            _isVolumeMode = false;

            ControlsHost.Visibility = Visibility.Collapsed;
            VolumeHost.Visibility = Visibility.Collapsed;
            NotificationHost.Visibility = Visibility.Visible;

            string cleanMessage = string.IsNullOrWhiteSpace(message) ? "操作已完成" : message.Trim();
            bool isError = cleanMessage.Contains("失败")
                || cleanMessage.Contains("未能")
                || cleanMessage.Contains("异常")
                || cleanMessage.Contains("请确保")
                || cleanMessage.Contains("错误")
                || cleanMessage.Contains("权限");
            bool isInfo = !isError && (
                cleanMessage.Contains("正在")
                || cleanMessage.Contains("稍候")
                || cleanMessage.Contains("已是当前")
                || cleanMessage.Contains("已存在")
                || cleanMessage.Contains("已打开")
                || cleanMessage.Contains("发现新版本"));

            string category = !string.IsNullOrWhiteSpace(customCategory)
                ? customCategory!
                : (cleanMessage.Contains("账号") || cleanMessage.Contains("切换") || cleanMessage.Contains("当前已是"))
                    ? "账号切换"
                    : (cleanMessage.Contains("游戏") || cleanMessage.Contains("启动"))
                        ? "游戏与启动"
                        : cleanMessage.Contains("Steam")
                            ? "Steam 状态"
                            : "Steam Switch";

            // 使用单层无外圈的纯粹字形，避免在圆形徽章内出现“圆圈套圆圈”的视觉割裂
            string glyph;
            Color accentColor;
            Color borderColor;
            Color badgeBgColor;
            Color badgeBorderColor;
            Color cornerBgColor;
            Color categoryFgColor;

            if (isError)
            {
                glyph = "\uE711";
                accentColor = (Color)ColorConverter.ConvertFromString("#FB7185");
                borderColor = (Color)ColorConverter.ConvertFromString("#66FB7185");
                badgeBgColor = (Color)ColorConverter.ConvertFromString("#28F43F5E");
                badgeBorderColor = (Color)ColorConverter.ConvertFromString("#66FB7185");
                cornerBgColor = (Color)ColorConverter.ConvertFromString("#E11D48");
                categoryFgColor = (Color)ColorConverter.ConvertFromString("#FDA4AF");
            }
            else if (isInfo)
            {
                glyph = cleanMessage.Contains("正在") ? "\uE895" : "\uE77B";
                accentColor = (Color)ColorConverter.ConvertFromString("#5AC8FA");
                borderColor = (Color)ColorConverter.ConvertFromString("#557CE7FF");
                badgeBgColor = (Color)ColorConverter.ConvertFromString("#2438BDF8");
                badgeBorderColor = (Color)ColorConverter.ConvertFromString("#557CE7FF");
                cornerBgColor = (Color)ColorConverter.ConvertFromString("#2563EB");
                categoryFgColor = (Color)ColorConverter.ConvertFromString("#9CC2EC");
            }
            else
            {
                glyph = "\uE73E";
                accentColor = (Color)ColorConverter.ConvertFromString("#38F9D7");
                borderColor = (Color)ColorConverter.ConvertFromString("#5538F9D7");
                badgeBgColor = (Color)ColorConverter.ConvertFromString("#2210B981");
                badgeBorderColor = (Color)ColorConverter.ConvertFromString("#5534D399");
                cornerBgColor = (Color)ColorConverter.ConvertFromString("#10B981");
                categoryFgColor = (Color)ColorConverter.ConvertFromString("#8CE9B8");
            }

            NotificationCategoryText.Text = category;
            NotificationCategoryText.Foreground = new SolidColorBrush(categoryFgColor);
            NotificationMessageText.Text = cleanMessage;
            NotificationStatusDot.Fill = new SolidColorBrush(accentColor);
            NotificationTopGlowStop.Color = accentColor;
            NotificationBorderStop1.Color = borderColor;
            if (NotificationPointerPill != null)
            {
                NotificationPointerPill.Background = new SolidColorBrush(Color.FromArgb(0x99, accentColor.R, accentColor.G, accentColor.B));
            }
            NotificationPointerStroke.Stroke = new SolidColorBrush(Color.FromArgb(0x66, borderColor.R, borderColor.G, borderColor.B));
            NotificationBadgeRing.Background = new SolidColorBrush(badgeBgColor);
            NotificationBadgeRing.BorderBrush = new SolidColorBrush(badgeBorderColor);
            NotificationIconGlyph.Text = glyph;
            NotificationIconGlyph.Foreground = new SolidColorBrush(accentColor);
            NotificationAvatarCornerBadge.Background = new SolidColorBrush(cornerBgColor);
            NotificationAvatarCornerGlyph.Text = glyph;

            bool avatarLoaded = false;
            if (!string.IsNullOrWhiteSpace(avatarPath) && File.Exists(avatarPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 64;
                    bitmap.UriSource = new Uri(avatarPath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    NotificationAvatarBrush.ImageSource = bitmap;
                    avatarLoaded = true;
                }
                catch
                {
                    avatarLoaded = false;
                }
            }

            if (avatarLoaded)
            {
                NotificationAvatarEllipse.Visibility = Visibility.Visible;
                NotificationIconGlyph.Visibility = Visibility.Collapsed;
                NotificationAvatarCornerBadge.Visibility = Visibility.Visible;
            }
            else
            {
                NotificationAvatarEllipse.Visibility = Visibility.Collapsed;
                NotificationIconGlyph.Visibility = Visibility.Visible;
                NotificationAvatarCornerBadge.Visibility = Visibility.Collapsed;
            }
        }

        public void ShowNotificationBubble(Point centerScreenPoint, double anchorTop, string message, string? avatarPath = null, string? customCategory = null)
        {
            ConfigureNotificationContent(message, avatarPath, customCategory);
            var (windowWidth, windowHeight) = MeasureWindowSize(244, 76);

            // 确保锚点至少位于任务栏顶边（WorkArea.Bottom）之上，并预留 10 DIP 额外部署间距
            // （算上窗口外层 12 DIP 阴影留白，气泡底边距离任务栏切换栏约 20~22 DIP，视觉轻盈不贴边）
            double effectiveTop = Math.Min(anchorTop, SystemParameters.WorkArea.Bottom);

            var placement = FlyoutPlacement.Clamp(
                new Rect(centerScreenPoint.X - (windowWidth / 2.0), effectiveTop - windowHeight - 10, windowWidth, windowHeight),
                FlyoutPlacement.VirtualScreen());
            Left = placement.Left;
            Top = placement.Top;

            double bubbleCenterX = placement.Left + (windowWidth / 2.0);
            double maxPointerShift = Math.Max(0, ((NotificationCard.ActualWidth > 0 ? NotificationCard.ActualWidth : windowWidth - 24) / 2.0) - 26);
            NotificationPointerTranslate.X = Math.Clamp(centerScreenPoint.X - bubbleCenterX, -maxPointerShift, maxPointerShift);

            if (Visibility != Visibility.Visible)
            {
                Opacity = 0;
                Visibility = Visibility.Visible;
            }

            BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            NotificationTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(9, 0, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            var scaleEase = new BackEase { Amplitude = 0.20, EasingMode = EasingMode.EaseOut };
            NotificationScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = scaleEase
            });
            NotificationScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = scaleEase
            });
        }

        public void HideFlyout(bool instant = false)
        {
            if (Visibility != Visibility.Visible) return;
            if (instant)
            {
                Visibility = Visibility.Collapsed;
                Opacity = 0;
                return;
            }

            var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseIn }
            };
            anim.Completed += (s, e) =>
            {
                if (Opacity <= 0.05)
                {
                    Visibility = Visibility.Collapsed;
                }
            };
            BeginAnimation(OpacityProperty, anim);
        }

        public bool IsControlsShowing => Visibility == Visibility.Visible && _isControlsMode && Opacity > 0.1;
        public bool IsVolumeShowing => Visibility == Visibility.Visible && _isVolumeMode && Opacity > 0.1;
        public bool IsNotificationShowing => Visibility == Visibility.Visible && _isNotificationMode && Opacity > 0.1;

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            FlyoutMouseEnter?.Invoke(this, EventArgs.Empty);
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            FlyoutMouseLeave?.Invoke(this, EventArgs.Empty);
        }

        private static void AnimateButtonClick(Button? btn)
        {
            if (btn == null) return;
            var scale = new ScaleTransform(0.88, 0.88);
            btn.RenderTransform = scale;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }
            });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }
            });
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            AnimateButtonClick(sender as Button);
            PrevClicked?.Invoke(this, EventArgs.Empty);
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            AnimateButtonClick(sender as Button);
            PlayPauseClicked?.Invoke(this, EventArgs.Empty);
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            AnimateButtonClick(sender as Button);
            NextClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
