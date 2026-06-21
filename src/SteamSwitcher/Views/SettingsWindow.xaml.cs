using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SteamSwitcher.Core;
using SteamSwitcher.ViewModels;

namespace SteamSwitcher.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public event EventHandler<TaskbarPosition>? PositionChanged;
        public event EventHandler<int>? OffsetChanged;
        public event EventHandler<int>? AvatarSizeChanged;
        public event EventHandler<bool>? GlassChanged;
        public event EventHandler<bool>? RoundedChanged;

        public SettingsWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += SettingsWindow_Loaded;
        }

        private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            switch (_viewModel.TaskbarPosition)
            {
                case TaskbarPosition.Auto:
                    PosAuto.IsChecked = true;
                    break;
                case TaskbarPosition.Left:
                    PosLeft.IsChecked = true;
                    break;
                case TaskbarPosition.Center:
                    PosCenter.IsChecked = true;
                    break;
                case TaskbarPosition.Right:
                    PosRight.IsChecked = true;
                    break;
            }

            OffsetSlider.Value = _viewModel.TaskbarOffsetX;
            AvatarSizeSlider.Value = _viewModel.TaskbarAvatarSize;
        }

        private void Position_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            var position = TaskbarPosition.Auto;
            if (PosAuto.IsChecked == true) position = TaskbarPosition.Auto;
            else if (PosLeft.IsChecked == true) position = TaskbarPosition.Left;
            else if (PosCenter.IsChecked == true) position = TaskbarPosition.Center;
            else if (PosRight.IsChecked == true) position = TaskbarPosition.Right;

            _viewModel.TaskbarPosition = position;
            PositionChanged?.Invoke(this, position);
        }

        private void OffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;

            int offset = (int)e.NewValue;
            OffsetText.Text = offset.ToString();
            _viewModel.TaskbarOffsetX = offset;
            OffsetChanged?.Invoke(this, offset);
        }

        private void AvatarSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;

            int size = (int)e.NewValue;
            AvatarSizeText.Text = size.ToString();
            _viewModel.TaskbarAvatarSize = size;
            AvatarSizeChanged?.Invoke(this, size);
        }

        private void Glass_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _viewModel.TaskbarGlassEnabled = true;
            GlassChanged?.Invoke(this, true);
        }

        private void Glass_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _viewModel.TaskbarGlassEnabled = false;
            GlassChanged?.Invoke(this, false);
        }

        private void Rounded_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _viewModel.TaskbarRoundedMode = true;
            RoundedChanged?.Invoke(this, true);
        }

        private void Rounded_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _viewModel.TaskbarRoundedMode = false;
            RoundedChanged?.Invoke(this, false);
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SelectSteamPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Steam 主程序|steam.exe",
                Title = "请选择 Steam 安装目录下的 steam.exe"
            };
            if (dialog.ShowDialog() == true)
            {
                _viewModel.CustomSteamPath = System.IO.Path.GetDirectoryName(dialog.FileName) ?? "";
            }
        }

        private void SelectLoginUsersPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "配置文件|loginusers.vdf",
                Title = "请选择 loginusers.vdf 配置文件"
            };
            if (dialog.ShowDialog() == true)
            {
                _viewModel.CustomLoginUsersPath = dialog.FileName;
            }
        }

        private void ResetPaths_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.CustomSteamPath = "";
            _viewModel.CustomLoginUsersPath = "";
            MessageBox.Show("已重置为系统自动探测路径，程序将重新扫描。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }
    }
}
