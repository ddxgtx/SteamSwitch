using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SteamSwitcher.Core;
using SteamSwitcher.Services;
using SteamSwitcher.ViewModels;

namespace SteamSwitcher.Views
{
    public partial class GameBindingWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly GameAccountBinding _gameBinding;
        private readonly ObservableCollection<GameListViewModel> _displayGames = new();
        private string _searchText = string.Empty;

        public GameBindingWindow(MainViewModel viewModel, GameAccountBinding gameBinding)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _gameBinding = gameBinding;
            DataContext = _viewModel;

            // Drive the list through a single, stable ObservableCollection instead of
            // reassigning ItemsSource on every keystroke. This keeps the SelectedItem
            // binding to SelectedGame intact (the same GameListViewModel instances are
            // reused) and lets the list refresh after a background re-scan.
            GameListBox.ItemsSource = _displayGames;
            ApplyGameListFilter();

            Loaded += async (s, e) =>
            {
                try
                {
                    GameCountLabel.Text = "";
                    SearchBox.Text = PlaceholderText;
                    SearchBox.SetResourceReference(Control.ForegroundProperty, "SearchPlaceholderFgBrush");
                    _isPlaceholderActive = true;

                    await _viewModel.ScanGamesAsync();
                    ApplyGameListFilter();
                    GameCountLabel.Text = $"— {_viewModel.GameList.Count} 款游戏";
                }
                catch (Exception ex)
                {
                    Services.AppLogger.Error("GameBindingWindow initial scan failed.", ex);
                    GameCountLabel.Text = "扫描失败";
                    MessageBox.Show(this, $"扫描游戏失败：{ex.Message}", "游戏绑定",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
        }

        private async void ScanGames_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.ScanGamesAsync();
                ApplyGameListFilter();
                GameCountLabel.Text = $"— {_viewModel.GameList.Count} 款游戏";
            }
            catch (Exception ex)
            {
                Services.AppLogger.Error("GameBindingWindow scan failed.", ex);
                GameCountLabel.Text = "扫描失败";
                MessageBox.Show(this, $"扫描游戏失败：{ex.Message}", "游戏绑定",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private const string PlaceholderText = "搜索游戏名称或 AppID...";
        private bool _isPlaceholderActive = true;

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (_isPlaceholderActive)
            {
                SearchBox.Text = "";
                SearchBox.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
                _isPlaceholderActive = false;
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                SearchBox.Text = PlaceholderText;
                SearchBox.SetResourceReference(Control.ForegroundProperty, "SearchPlaceholderFgBrush");
                _isPlaceholderActive = true;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isPlaceholderActive) return;
            ApplyGameListFilter();
        }

        private void ApplyGameListFilter()
        {
            _searchText = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
            ClearSearchBtn.Visibility = string.IsNullOrEmpty(_searchText) ? Visibility.Collapsed : Visibility.Visible;

            IEnumerable<GameListViewModel> filtered = string.IsNullOrWhiteSpace(_searchText)
                ? _viewModel.GameList
                : _viewModel.GameList
                    .Where(g => g.GameName.ToLowerInvariant().Contains(_searchText) ||
                                g.AppId.ToString().Contains(_searchText))
                    .ToList();

            _displayGames.Clear();
            foreach (var game in filtered)
                _displayGames.Add(game);
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox_GotFocus(sender, e);
            ClearSearchBtn.Visibility = Visibility.Collapsed;
            ApplyGameListFilter();
        }

        private async void SaveBinding_Click(object sender, RoutedEventArgs e)
        {
            try
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
            catch (Exception ex)
            {
                Services.AppLogger.Error("GameBindingWindow save binding failed.", ex);
                MessageBox.Show(this, $"保存绑定失败：{ex.Message}", "游戏绑定",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RemoveBinding_Click(object sender, RoutedEventArgs e)
        {
            try
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
            catch (Exception ex)
            {
                Services.AppLogger.Error("GameBindingWindow remove binding failed.", ex);
                MessageBox.Show(this, $"删除绑定失败：{ex.Message}", "游戏绑定",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ManualAddBinding_Click(object sender, RoutedEventArgs e)
        {
            try
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
            catch (Exception ex)
            {
                Services.AppLogger.Error("GameBindingWindow manual add binding failed.", ex);
                MessageBox.Show(this, $"手动绑定失败：{ex.Message}", "游戏绑定",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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
            // Interactive controls (buttons, text boxes, sliders) mark the mouse
            // event handled and never reach here; the helper double-checks so
            // unhandled presses inside them cannot drag the window.
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
