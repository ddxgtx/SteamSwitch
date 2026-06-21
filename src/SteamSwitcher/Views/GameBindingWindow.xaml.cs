using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SteamSwitcher.Core;
using SteamSwitcher.ViewModels;

namespace SteamSwitcher.Views
{
    public partial class GameBindingWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly GameAccountBinding _gameBinding;

        public GameBindingWindow(MainViewModel viewModel, GameAccountBinding gameBinding)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _gameBinding = gameBinding;
            DataContext = _viewModel;

            Loaded += async (s, e) =>
            {
                GameCountLabel.Text = "";
                SearchBox.Text = PlaceholderText;
                SearchBox.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x63, 0x63, 0x66));
                _isPlaceholderActive = true;

                await _viewModel.ScanGamesAsync();
                GameCountLabel.Text = $"— {_viewModel.GameList.Count} 款游戏";
            };
        }

        private async void ScanGames_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.ScanGamesAsync();
            GameCountLabel.Text = $"— {_viewModel.GameList.Count} 款游戏";
        }

        private const string PlaceholderText = "搜索游戏名称或 AppID...";
        private bool _isPlaceholderActive = true;

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (_isPlaceholderActive)
            {
                SearchBox.Text = "";
                SearchBox.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 255, 255));
                _isPlaceholderActive = false;
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                SearchBox.Text = PlaceholderText;
                SearchBox.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x63, 0x63, 0x66));
                _isPlaceholderActive = true;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isPlaceholderActive) return;

            var searchText = SearchBox.Text?.Trim().ToLower() ?? "";
            ClearSearchBtn.Visibility = string.IsNullOrEmpty(searchText) ? Visibility.Collapsed : Visibility.Visible;

            if (string.IsNullOrEmpty(searchText))
            {
                RefreshGameList(null);
            }
            else
            {
                var filtered = _viewModel.GameList
                    .Where(g => g.GameName.ToLower().Contains(searchText) ||
                                g.AppId.ToString().Contains(searchText))
                    .ToList();

                RefreshGameList(filtered);
            }
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox_GotFocus(sender, e);
            ClearSearchBtn.Visibility = Visibility.Collapsed;
        }

        private void RefreshGameList(System.Collections.Generic.List<GameListViewModel>? filtered)
        {
            var selected = GameListBox.SelectedItem;
            if (filtered == null)
            {
                GameListBox.ItemsSource = _viewModel.GameList;
            }
            else
            {
                GameListBox.ItemsSource = new ObservableCollection<GameListViewModel>(filtered);
            }

            if (selected != null)
            {
                GameListBox.SelectedItem = selected;
            }
        }

        private async void SaveBinding_Click(object sender, RoutedEventArgs e)
        {
            var game = _viewModel.SelectedGame;
            var account = _viewModel.SelectedBindingAccount;

            if (game == null)
            {
                MessageBox.Show("请先在左侧列表中选择一个游戏", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (account == null)
            {
                MessageBox.Show("请先选择一个账号", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await _viewModel.SaveGameBinding(
                game.AppId,
                game.GameName,
                account.SteamId,
                account.DisplayName);
        }

        private async void RemoveBinding_Click(object sender, RoutedEventArgs e)
        {
            var game = _viewModel.SelectedGame;
            if (game == null) return;

            var result = MessageBox.Show(
                $"确定要删除 {game.GameName} 的账号绑定吗？",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await _viewModel.RemoveGameBinding(game.AppId);
            }
        }

        private async void ManualAddBinding_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(ManualAppIdInput.Text?.Trim(), out int appId))
            {
                MessageBox.Show("请输入有效的 AppID", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var gameName = ManualGameNameInput.Text?.Trim();
            if (string.IsNullOrEmpty(gameName))
            {
                MessageBox.Show("请输入游戏名称", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var account = _viewModel.SelectedBindingAccount;
            if (account == null)
            {
                MessageBox.Show("请先选择要绑定的账号", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await _viewModel.SaveGameBinding(appId, gameName, account.SteamId, account.DisplayName);

            ManualAppIdInput.Text = "";
            ManualGameNameInput.Text = "";
            ManualAddPanel.Visibility = Visibility.Collapsed;

            var game = _viewModel.GameList.FirstOrDefault(g => g.AppId == appId);
            if (game != null)
            {
                _viewModel.SelectedGame = game;
            }
        }

        private void ManualAddToggle_Click(object sender, RoutedEventArgs e)
        {
            ManualAddPanel.Visibility = ManualAddPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ToggleGamePin_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GameListViewModel game)
            {
                _viewModel.ToggleGamePin(game.AppId);
                game.IsPinned = _viewModel.IsGamePinned(game.AppId);

                if (game.IsPinned && !game.HasBinding)
                {
                    MessageBox.Show("该游戏尚未绑定账号，请先添加账号绑定后再固定到任务栏。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }
    }
}
