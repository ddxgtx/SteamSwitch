using System.Windows;
using System.Windows.Input;
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
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (LaunchArgumentsBox.Text.Length > 2048)
            {
                MessageBox.Show(this, "启动参数不能超过 2048 个字符。", "游戏启动工作流", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await _viewModel.SaveGameLaunchWorkflowAsync(
                _game.AppId,
                LaunchArgumentsBox.Text,
                BackupBeforeLaunchCheck.IsChecked == true,
                BackupAfterExitCheck.IsChecked == true,
                CloseSteamAfterExitCheck.IsChecked == true,
                BigPictureCheck.IsChecked == true);
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
