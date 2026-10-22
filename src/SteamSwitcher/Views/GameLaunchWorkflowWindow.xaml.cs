using System;
using System.Windows;
using System.Windows.Input;
using SteamSwitcher.Core;
using SteamSwitcher.Services;
using SteamSwitcher.ViewModels;

namespace SteamSwitcher.Views
{
    public partial class GameLaunchWorkflowWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly GameListViewModel _game;

        public GameLaunchWorkflowWindow(MainViewModel viewModel, GameListViewModel game)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _game = game;
            var binding = viewModel.GetGameBinding(game.AppId);
            GameSummaryText.Text = $"{game.GameName} · AppID {game.AppId} · {binding?.AccountName ?? "未绑定账号"}";
            LaunchArgumentsBox.Text = binding?.LaunchArguments ?? string.Empty;
            BackupBeforeLaunchCheck.IsChecked = binding?.BackupBeforeLaunch == true;
            BackupAfterExitCheck.IsChecked = binding?.BackupAfterExit == true;
            CloseSteamAfterExitCheck.IsChecked = binding?.CloseSteamAfterExit == true;
            BigPictureCheck.IsChecked = binding?.StartInBigPicture == true;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            WindowDragHelper.TryDrag(this, e);
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (LaunchArgumentsBox.Text.Length > 2048)
            {
                MessageBox.Show(this, "启动参数不能超过 2048 个字符。", "游戏启动工作流", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                await _viewModel.SaveGameLaunchWorkflowAsync(
                    _game.AppId,
                    LaunchArgumentsBox.Text,
                    BackupBeforeLaunchCheck.IsChecked == true,
                    BackupAfterExitCheck.IsChecked == true,
                    CloseSteamAfterExitCheck.IsChecked == true,
                    BigPictureCheck.IsChecked == true);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("GameLaunchWorkflowWindow save failed.", ex);
                // Keep the dialog open so the user can correct input and retry.
                MessageBox.Show(this, $"保存启动工作流失败：{ex.Message}", "游戏启动工作流",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
