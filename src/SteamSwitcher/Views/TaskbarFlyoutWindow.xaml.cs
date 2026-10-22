using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

            VolumeHost.Visibility = Visibility.Collapsed;
            ControlsHost.Visibility = Visibility.Visible;
            PlayPauseButton.Content = isPlaying ? "\uE769" : "\uE768";

            UpdateLayout();

            // 将窗口居中放置在歌词框正上方。乐队贴在屏幕边缘时锚点居中会把浮窗送出
            // 屏幕，夹回虚拟屏内（留 8 DIP 呼吸位）。
            double windowWidth = ActualWidth > 0 ? ActualWidth : 140;
            double windowHeight = ActualHeight > 0 ? ActualHeight : 54;

            var placement = FlyoutPlacement.Clamp(
                new Rect(centerScreenPoint.X - (windowWidth / 2), anchorTop - windowHeight + 8, windowWidth, windowHeight),
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
            ControlsTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            });
        }

        public void ShowVolume(Point centerScreenPoint, double anchorTop, float volume, bool isMuted)
        {
            _isVolumeMode = true;
            _isControlsMode = false;

            ControlsHost.Visibility = Visibility.Collapsed;
            VolumeHost.Visibility = Visibility.Visible;

            int percent = (int)Math.Round(volume * 100);
            VolumeProgressBar.Value = percent;
            VolumePercentText.Text = $"{percent}%";
            VolumeIcon.Text = (isMuted || percent == 0)
                ? "\uE74F" // 静音
                : (percent < 33 ? "\uE992" : (percent < 66 ? "\uE994" : "\uE767"));

            UpdateLayout();

            double windowWidth = ActualWidth > 0 ? ActualWidth : 180;
            double windowHeight = ActualHeight > 0 ? ActualHeight : 54;

            var placement = FlyoutPlacement.Clamp(
                new Rect(centerScreenPoint.X - (windowWidth / 2), anchorTop - windowHeight + 8, windowWidth, windowHeight),
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
            VolumeTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
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
