using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SteamSwitcher.Models;

namespace SteamSwitcher.Views
{
    public partial class CustomSaveFolderDialog : Window
    {
        public CustomGameSaveDefinition? Definition { get; private set; }

        public CustomSaveFolderDialog(IEnumerable<SteamAccount> accounts)
        {
            InitializeComponent();
            AccountCombo.Items.Add(new AccountOption(string.Empty, "本机通用存档（不关联账号）"));
            foreach (var account in accounts.OrderBy(account => account.PersonaName ?? account.AccountName))
            {
                var displayName = string.IsNullOrWhiteSpace(account.PersonaName)
                    ? account.AccountName
                    : $"{account.PersonaName} · {account.AccountName}";
                AccountCombo.Items.Add(new AccountOption(account.SteamId, displayName));
            }
            AccountCombo.SelectedIndex = 0;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择游戏存档目录",
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true)
                return;
            FolderPathBox.Text = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(GameNameBox.Text))
                GameNameBox.Text = new DirectoryInfo(dialog.FolderName).Name;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var gameName = GameNameBox.Text.Trim();
            var folderPath = FolderPathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(gameName))
            {
                MessageBox.Show(this, "请输入游戏名称。", "自定义存档目录", MessageBoxButton.OK, MessageBoxImage.Information);
                GameNameBox.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                MessageBox.Show(this, "请选择存在的存档目录。", "自定义存档目录", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Definition = new CustomGameSaveDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                GameName = gameName,
                FolderPath = Path.GetFullPath(folderPath),
                AccountSteamId = (AccountCombo.SelectedItem as AccountOption)?.SteamId ?? string.Empty
            };
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private sealed record AccountOption(string SteamId, string DisplayName);
    }
}
