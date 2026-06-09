using System;
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
        }

        private async void AddBinding_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(AppIdInput.Text, out int appId) && !string.IsNullOrEmpty(GameNameInput.Text))
            {
                var currentAccount = _viewModel.SelectedAccount?.Account;
                if (currentAccount != null)
                {
                    await _gameBinding.SetBindingAsync(
                        appId,
                        GameNameInput.Text,
                        currentAccount.SteamId,
                        currentAccount.AccountName);

                    AppIdInput.Text = "";
                    GameNameInput.Text = "";

                    MessageBox.Show($"已绑定游戏到 {currentAccount.PersonaName}", 
                        "绑定成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("请先选择一个账号", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                MessageBox.Show("请输入有效的AppID和游戏名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void RemoveBinding_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string appIdStr && int.TryParse(appIdStr, out int appId))
            {
                var result = MessageBox.Show("确定要删除此绑定吗？", "确认", 
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    await _gameBinding.RemoveBindingAsync(appId);
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
    }
}
