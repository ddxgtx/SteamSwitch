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

            OffsetSlider.Value = _viewModel.TaskbarOffset;
            AvatarSizeSlider.Value = _viewModel.AvatarSize;
        }

        private void Position_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            var position = TaskbarPosition.Right;
            if (PosLeft.IsChecked == true) position = TaskbarPosition.Left;
            else if (PosCenter.IsChecked == true) position = TaskbarPosition.Center;

            _viewModel.TaskbarPosition = position;
            PositionChanged?.Invoke(this, position);
        }

        private void OffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;

            int offset = (int)e.NewValue;
            OffsetText.Text = offset.ToString();
            _viewModel.TaskbarOffset = offset;
            OffsetChanged?.Invoke(this, offset);
        }

        private void AvatarSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;

            int size = (int)e.NewValue;
            AvatarSizeText.Text = size.ToString();
            _viewModel.AvatarSize = size;
            AvatarSizeChanged?.Invoke(this, size);
        }

        private void Glass_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _viewModel.GlassEnabled = true;
            GlassChanged?.Invoke(this, true);
        }

        private void Glass_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _viewModel.GlassEnabled = false;
            GlassChanged?.Invoke(this, false);
        }

        private void Rounded_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _viewModel.RoundedMode = true;
            RoundedChanged?.Invoke(this, true);
        }

        private void Rounded_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _viewModel.RoundedMode = false;
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

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }
    }
}
