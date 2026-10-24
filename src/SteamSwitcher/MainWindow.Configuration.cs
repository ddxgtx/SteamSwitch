using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SteamSwitcher.Services;

namespace SteamSwitcher;

public partial class MainWindow
{
    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, SettingsTabs) || SettingsCategoryHint == null) return;
        SettingsCategoryHint.Text = ReferenceEquals(SettingsTabs.SelectedItem, AdvancedSettingsTab)
            ? "高级设置 · Steam 路径、窗口布局、更新与诊断"
            : "常规设置 · 备份位置、启动行为、外观与通知";
    }

    private void ResetAccountLibrarySearch()
    {
        _accountSearchPlaceholder = true;
        AccountSearchBox.Text = AccountSearchHint;
        AccountSearchBox.SetResourceReference(Control.ForegroundProperty, "SearchPlaceholderFgBrush");
    }

    private void ShowRecoveryAccounts_Click(object sender, RoutedEventArgs e)
    {
        ResetAccountLibrarySearch();
        SetAccountGridMode(false);
        ApplyAccountFilterMode("Archived");
        var account = System.Linq.Enumerable.FirstOrDefault(_viewModel.Accounts, item => item.IsArchived);
        _viewModel.SelectedAccount = account;
        if (account != null) AccountListBox.ScrollIntoView(account);
        _viewModel.StatusText = $"已显示 {_viewModel.ArchivedAccountCount} 个仅本地保存的账号；选择账号后可找回，Steam 可能要求重新验证登录。";
    }

    private void ShowAllSavedAccounts_Click(object sender, RoutedEventArgs e)
    {
        ResetAccountLibrarySearch();
        ApplyAccountFilterMode("All");
        _viewModel.StatusText = $"正在显示全部 {_viewModel.Accounts.Count} 个收录账号，可按昵称、登录名或 Steam ID 搜索。";
    }

    private bool _configurationChangeInProgress;

    private Task<string?> ExportBackupAsync(BackupExportKind kind, string fileName, string filter, Func<string, Task> write)
    {
        return _viewModel.ExportBackupAsync(kind, fileName, write, suggested =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "选择备份保存位置", Filter = filter, FileName = Path.GetFileName(suggested),
                InitialDirectory = BackupExportService.GetInitialDirectory(suggested)
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        });
    }

    private void SelectBackupExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择备份导出目录" };
            string current;
            try { current = BackupExportService.GetDirectory(_viewModel.GetSettings()); }
            catch (ArgumentException) { current = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
            if (Directory.Exists(current)) dialog.InitialDirectory = current;
            if (dialog.ShowDialog(this) != true) return;
            _viewModel.BackupExportDirectory = dialog.FolderName;
            if (_viewModel.SaveSettingsNow()) _viewModel.StatusText = $"备份导出目录已保存：{dialog.FolderName}";
        }
        catch (Exception ex) { ShowConfigurationError("设置备份目录", ex); }
    }

    private void OpenBackupExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = BackupExportService.GetDirectory(_viewModel.GetSettings());
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowConfigurationError("打开备份目录", ex); }
    }

    private void ResetBackupExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.BackupExportDirectory = "";
        if (_viewModel.SaveSettingsNow()) _viewModel.StatusText = $"已恢复默认备份导出目录：{_viewModel.BackupExportDirectoryDisplay}";
    }

    private async Task ChangeConfigurationAsync(Func<Task> change)
    {
        if (_configurationChangeInProgress || _viewModel.IsLoading || _viewModel.IsBackupExporting || _gameSaveCenterWindow?.IsBusy == true)
            throw new InvalidOperationException("请等待当前账号、游戏或存档操作完成后再修改配置。");
        _configurationChangeInProgress = true;
        IsEnabled = false;
        try { await change(); }
        finally
        {
            IsEnabled = true;
            _configurationChangeInProgress = false;
        }
    }

    private async void ExportConfigurationBundle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var bundle = _viewModel.CreateConfigurationBundle();
            var path = await ExportBackupAsync(BackupExportKind.Configuration, "SteamSwitch-configuration.steamswitch.json",
                "Steam Switch 应用配置包 (*.steamswitch.json)|*.steamswitch.json", destination => ConfigurationBundleService.ExportAsync(bundle, destination));
            if (path != null) _viewModel.StatusText = $"应用配置包已备份：{path}";
        }
        catch (Exception ex) { ShowConfigurationError("导出应用配置包", ex); }
    }

    private async void ImportConfigurationBundle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Steam Switch 应用配置包 (*.steamswitch.json)|*.steamswitch.json|JSON 文件 (*.json)|*.json"
            };
            if (dialog.ShowDialog(this) != true) return;
            var bundle = await ConfigurationBundleService.ReadAsync(dialog.FileName);
            if (MessageBox.Show(this,
                $"将替换当前设置、{bundle.Accounts.Count} 条账号档案、{bundle.GameBindings.Count} 项游戏绑定和 {bundle.QuickLaunch.Count} 个快速启动项。\n\n应用配置包不包含登录凭据或游戏存档；导入不会启动其中的程序。是否继续？",
                "导入应用配置包", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await ChangeConfigurationAsync(async () =>
            {
                await _viewModel.ImportConfigurationBundleAsync(bundle, ReloadRuntimeSettingsAsync);
            });
            _viewModel.StatusText = "应用配置包已导入并应用";
        }
        catch (Exception ex) { ShowConfigurationError("导入应用配置包", ex); }
    }

    private void AccountArchiveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var archivedCount = _viewModel.ArchivedAccountCount;
        menu.Items.Add(new MenuItem
        {
            Header = $"本地账号库 · {_viewModel.Accounts.Count} 个已保存 · {archivedCount} 个待找回",
            Style = (Style)FindResource("MenuSectionItem")
        });
        menu.Items.Add(new Separator());
        var export = SharedContextMenuBuilder.ActionItem("备份账号库…", "\uE74E");
        export.IsEnabled = _viewModel.CanExportBackup;
        export.Click += ExportAccountArchive_Click;
        menu.Items.Add(export);
        var import = SharedContextMenuBuilder.ActionItem("导入并合并账号库…", "\uE8B5");
        import.Click += ImportAccountArchive_Click;
        menu.Items.Add(import);
        menu.Items.Add(new Separator());
        var forget = SharedContextMenuBuilder.ActionItem("遗忘所选账号…", "\uE74D");
        forget.IsEnabled = _viewModel.SelectedAccount != null;
        forget.ToolTip = _viewModel.SelectedAccount is { } selected ? $"从本应用遗忘 {selected.DisplayName}，操作前会再次确认" : "先在列表选择一个账号";
        forget.SetResourceReference(Control.ForegroundProperty, "DangerFgBrush");
        forget.Click += ForgetSelectedAccount_Click;
        menu.Items.Add(forget);
        var restore = SharedContextMenuBuilder.ActionItem("恢复账号自动收录", "\uE72C");
        foreach (var steamId in _viewModel.GetAccountManager().IgnoredAccountIds)
        {
            var item = new MenuItem { Header = steamId };
            item.Click += async (_, _) =>
            {
                try
                {
                    await ChangeConfigurationAsync(async () =>
                    {
                        await _viewModel.GetAccountManager().RestoreAccountCollectionAsync(steamId);
                    });
                    _viewModel.StatusText = "已恢复收录：Steam 本机记录中存在的账号将重新显示";
                }
                catch (Exception ex) { ShowConfigurationError("恢复账号收录", ex); }
            };
            restore.Items.Add(item);
        }
        restore.IsEnabled = restore.Items.Count > 0;
        restore.ToolTip = restore.IsEnabled ? "选择曾被遗忘的账号，恢复从 Steam 自动收录" : "目前没有停止自动收录的账号";
        menu.Items.Add(restore);
        menu.IsOpen = true;
    }

    private async void ForgetSelectedAccount_Click(object sender, RoutedEventArgs e)
    {
        var account = _viewModel.SelectedAccount;
        if (account == null) return;
        if (MessageBox.Show(this,
            $"从本应用遗忘 {account.DisplayName}（{account.SteamId}）？\n\n会移除本应用中的固定入口和该账号的游戏绑定，并停止自动收录。Steam 登录状态、原始存档和备份不会被删除。",
            "遗忘账号", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await ChangeConfigurationAsync(async () => await _viewModel.ForgetAccountAsync(account.SteamId));
            _viewModel.StatusText = "已从本应用遗忘账号；可在「更多管理」菜单恢复自动收录";
        }
        catch (Exception ex) { ShowConfigurationError("遗忘账号", ex); }
    }

    private async void ExportAccountArchive_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await ExportBackupAsync(BackupExportKind.Accounts, "SteamSwitch-accounts.json",
                "Steam Switch 账号资料 (*.json)|*.json", _viewModel.GetAccountManager().ExportAccountArchiveAsync);
            if (path != null) _viewModel.StatusText = $"账号库已备份（不含登录凭据）：{path}";
        }
        catch (Exception ex) { ShowConfigurationError("导出账号元数据", ex); }
    }

    private async void ImportAccountArchive_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Steam Switch 账号元数据 (*.json)|*.json" };
            if (dialog.ShowDialog(this) != true) return;
            var count = 0;
            await ChangeConfigurationAsync(async () =>
                count = await _viewModel.GetAccountManager().ImportAccountArchiveAsync(dialog.FileName));
            _viewModel.StatusText = $"已合并 {count} 条账号记录；登录状态由 Steam 管理";
        }
        catch (Exception ex) { ShowConfigurationError("导入账号元数据", ex); }
    }

    private void ShowConfigurationError(string action, Exception ex)
    {
        AppLogger.Error(action + " failed.", ex);
        _viewModel.StatusText = $"{action}失败：{ex.Message}";
    }
        private async void ExportSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settings = SettingsSession.Copy(_viewModel.GetSettings());
                var path = await ExportBackupAsync(BackupExportKind.Settings, "SteamSwitch-settings.json",
                    "Steam Switch 设置 (*.json)|*.json", destination => AtomicJsonFile.WriteAsync(destination, settings, new JsonSerializerOptions { WriteIndented = true }));
                if (path != null) _viewModel.StatusText = $"设置已备份：{path}";
            }
            catch (Exception ex)
            {
                AppLogger.Error("Export settings failed.", ex);
                _viewModel.StatusText = $"导出设置失败: {ex.Message}";
            }
        }

        private async void ImportSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Steam Switch 设置 (*.json)|*.json|所有文件 (*.*)|*.*", Title = "导入 Steam Switch 设置" };
                if (dialog.ShowDialog() != true) return;
                var settings = await SettingsImportService.ReadAsync(dialog.FileName);
                if (MessageBox.Show(this, "导入设置会覆盖当前配置，是否继续？", "确认导入", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                await ChangeConfigurationAsync(async () =>
                {
                    await _viewModel.ApplySettingsAsync(settings, applyUi: ReloadRuntimeSettingsAsync);
                });
                _viewModel.StatusText = "设置已导入并重新加载";
            }
            catch (Exception ex)
            {
                AppLogger.Error("Import settings failed.", ex);
                _viewModel.StatusText = $"导入设置失败: {ex.Message}";
            }
        }

        private async void ResetAllSettings_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "确定恢复全部默认设置吗？当前设置会自动备份。", "恢复默认设置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                await ChangeConfigurationAsync(async () =>
                {
                    await _viewModel.ApplySettingsAsync(new AppSettings(), applyUi: ReloadRuntimeSettingsAsync);
                });
                _viewModel.StatusText = "已恢复全部默认设置";
            }
            catch (Exception ex)
            {
                AppLogger.Error("Reset settings failed.", ex);
                _viewModel.StatusText = $"恢复默认设置失败：{ex.Message}";
            }
        }

        private async Task ReloadRuntimeSettingsAsync()
        {
            RestoreAccountViewPreferences();
            _gameFilterMode = _viewModel.GetSettings().GameFilterMode;
            GameFilterCombo.SelectedValue = _gameFilterMode;
            ApplyGameSearchFilter();
            SyncDesktopEnhanceControls();
            SyncGameSaveSettingsUi();
            SettingsFloatingAvatarSlider.Value = _viewModel.DesktopFloatingAvatarSize;
            SettingsFloatingAvatarText.Text = _viewModel.DesktopFloatingAvatarSize.ToString();
            SettingsFloatingOpacitySlider.Value = _viewModel.DesktopFloatingOpacity;
            SettingsFloatingOpacityText.Text = $"{_viewModel.DesktopFloatingOpacity}%";
            ConfigureDesktopEnhance();
            SyncTaskbarPositionRadios();
            DetachDesktopFloating(updateSettings: false);
            DetachFromTaskbar(updateSettings: false);
            if (_viewModel.DesktopFloatingEnabled)
                AttachDesktopFloating();
            else
                DetachDesktopFloating();
            if (_viewModel.IsTaskbarPinned && _taskbarBand == null)
                AttachToTaskbar();
            else if (!_viewModel.IsTaskbarPinned && _taskbarBand != null)
                DetachFromTaskbar();
            ApplyTaskbarLyricsSettings();
            _trayIcon.UpdateMenu(_viewModel.Accounts, _viewModel.SelectedAccount);
            SyncTrayFeatureStates();
            if (_gameSaveCenterWindow != null)
                await _gameSaveCenterWindow.ReloadSettingsAsync();
            if ((!_viewModel.GameSaveCenterEnabled && SaveCenterHost.Visibility == Visibility.Visible) ||
                (!_viewModel.DesktopIconHiderEnabled && DesktopIconsView.Visibility == Visibility.Visible) ||
                (!_viewModel.TaskbarLyricsModuleEnabled &&
                 (TaskbarLyricsView.Visibility == Visibility.Visible || TaskbarLyricsAdvancedView.Visibility == Visibility.Visible)))
            {
                SwitchToAccounts();
            }
        }

}
