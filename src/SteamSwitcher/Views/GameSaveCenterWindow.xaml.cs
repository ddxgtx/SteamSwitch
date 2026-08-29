using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;
using SteamSwitcher.ViewModels;

namespace SteamSwitcher.Views
{
    public partial class GameSaveCenterWindow : UserControl, IDisposable
    {
        private static readonly AccountFilterOption AllAccounts = new(null, "全部账号");
        private readonly MainViewModel _mainViewModel;
        private readonly ObservableCollection<GameSaveEntry> _entries = new();
        private readonly ObservableCollection<GameSaveBackupInfo> _selectedBackups = new();
        private readonly ICollectionView _entryView;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private GameSaveBackupService? _backupService;
        private bool _isBusy;
        private bool _disposed;
        private int _changeAnalysisVersion;

        public GameSaveCenterWindow(MainViewModel mainViewModel)
        {
            InitializeComponent();
            _mainViewModel = mainViewModel;
            _entryView = CollectionViewSource.GetDefaultView(_entries);
            _entryView.Filter = FilterEntry;
            SaveList.ItemsSource = _entryView;
            BackupHistoryCombo.ItemsSource = _selectedBackups;
            ManualRetentionCombo.ItemsSource = new[] { 3, 5, 10, 20, 50, 100 };
            SafetyRetentionCombo.ItemsSource = new[] { 1, 3, 5, 10, 20, 50 };
            ManualRetentionCombo.SelectedItem = _mainViewModel.GameSaveManualRetention;
            SafetyRetentionCombo.SelectedItem = _mainViewModel.GameSaveSafetyRetention;
            SyncAutoBackupInterval();
            _mainViewModel.GetSettings().CustomGameSaveFolders ??= new List<CustomGameSaveDefinition>();
            AccountFilter.Items.Add(AllAccounts);
            AccountFilter.SelectedIndex = 0;
            Loaded += async (_, _) => await RefreshEntriesAsync();
        }

        public void SetPreviewEntries(IEnumerable<GameSaveEntry> entries)
        {
            _entries.Clear();
            foreach (var entry in entries) _entries.Add(entry);
            RebuildAccountFilter(_entries);
            EmptyHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_entries.Count > 0) SaveList.SelectedIndex = 0;
            if (_entries.Count > 0)
            {
                _selectedBackups.Clear();
                _selectedBackups.Add(new GameSaveBackupInfo
                {
                    FilePath = "preview-backup.zip",
                    CreatedAt = DateTime.Now.AddHours(-2),
                    Size = Math.Max(1, _entries[0].TotalSize),
                    IsDifferential = true
                });
                BackupHistoryCombo.SelectedIndex = 0;
            }
            SetStatus($"已发现 {_entries.Count} 组账号存档 · 预览数据", false);
            SetActionButtonsEnabled(true);
        }

        private async Task RefreshEntriesAsync()
        {
            if (_disposed) return;
            var steamService = _mainViewModel.GetAccountManager().GetSteamService();
            if (string.IsNullOrWhiteSpace(steamService.SteamPath) || !Directory.Exists(steamService.SteamPath))
            {
                SetStatus("未找到 Steam 安装目录，无法扫描 userdata。", false);
                return;
            }

            await RunBusyAsync("正在检查不同账号的存档…", async token =>
            {
                _backupService = CreateBackupService(steamService.SteamPath);
                var accounts = _mainViewModel.Accounts.Select(account => account.Account).ToList();
                var games = await Task.Run(steamService.GetInstalledGames, token);
                var entries = await _backupService.DiscoverAsync(
                    accounts,
                    games,
                    _mainViewModel.GetSettings().CustomGameSaveFolders,
                    _mainViewModel.SmartSaveDiscoveryEnabled,
                    token);

                _entries.Clear();
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.IconPath) && entry.AppId > 0)
                        entry.IconPath = steamService.GetGameIconPath(entry.AppId);
                    _entries.Add(entry);
                }

                RebuildAccountFilter(entries);
                EmptyHint.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                SetStatus(entries.Count == 0
                    ? "未在 Steam userdata 中发现存档文件。"
                    : $"已发现 {entries.Count} 组账号存档，涉及 {entries.Select(entry => entry.SteamId).Distinct().Count()} 个账号。", false);
            });
        }

        private void SyncAutoBackupInterval()
        {
            foreach (ComboBoxItem item in AutoBackupIntervalCombo.Items)
            {
                if (item.Tag is string tag && int.TryParse(tag, out var minutes) &&
                    minutes == _mainViewModel.GameSavePeriodicIntervalMinutes)
                {
                    AutoBackupIntervalCombo.SelectedItem = item;
                    return;
                }
            }
            AutoBackupIntervalCombo.SelectedIndex = 1;
        }

        private void AutoBackup_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox)
                _mainViewModel.GameSaveAutoBackupEnabled = checkBox.IsChecked == true;
        }

        private void PeriodicBackup_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox)
                _mainViewModel.GameSavePeriodicBackupEnabled = checkBox.IsChecked == true;
        }

        private void AutoBackupInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AutoBackupIntervalCombo?.SelectedItem is ComboBoxItem { Tag: string tag } &&
                int.TryParse(tag, out var minutes))
                _mainViewModel.GameSavePeriodicIntervalMinutes = minutes;
        }
        private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshEntriesAsync();

        private async void AddCustomFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            var dialog = new CustomSaveFolderDialog(_mainViewModel.Accounts.Select(account => account.Account))
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true || dialog.Definition == null)
                return;

            var settings = _mainViewModel.GetSettings();
            var normalizedPath = Path.GetFullPath(dialog.Definition.FolderPath).TrimEnd(Path.DirectorySeparatorChar);
            var rootPath = Path.GetPathRoot(normalizedPath)?.TrimEnd(Path.DirectorySeparatorChar);
            var backupRoot = _backupService?.BackupRoot.TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(rootPath) ||
                string.Equals(normalizedPath, rootPath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(backupRoot) &&
                 (string.Equals(normalizedPath, backupRoot, StringComparison.OrdinalIgnoreCase) ||
                  normalizedPath.StartsWith(backupRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                  backupRoot.StartsWith(normalizedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            {
                MessageBox.Show(Window.GetWindow(this), "不能选择磁盘根目录，也不能选择与 SteamSwitch 备份目录重叠的目录。", "自定义存档目录", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (settings.CustomGameSaveFolders.Any(item => AreSamePath(item.FolderPath, normalizedPath)))
            {
                MessageBox.Show(Window.GetWindow(this), "该目录已经添加到存档中心。", "自定义存档目录", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            settings.CustomGameSaveFolders.Add(dialog.Definition);
            SettingsService.Save(settings);
            await RefreshEntriesAsync();
            SetStatus($"已添加自定义存档目录：{dialog.Definition.GameName}", false);
        }

        private async void RemoveCustomFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || SaveList.SelectedItem is not GameSaveEntry { IsCustom: true, IsDetected: false } entry)
                return;
            var result = MessageBox.Show(Window.GetWindow(this),
                $"确定从存档中心移除《{entry.GameName}》吗？\n\n存档文件和已有备份不会被删除。",
                "移除自定义存档目录", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            var settings = _mainViewModel.GetSettings();
            settings.CustomGameSaveFolders.RemoveAll(item =>
                string.Equals(item.Id, entry.CustomId, StringComparison.Ordinal));
            SettingsService.Save(settings);
            await RefreshEntriesAsync();
            SetStatus($"已移除自定义存档目录：{entry.GameName}", false);
        }

        private async void BackupSelected_Click(object sender, RoutedEventArgs e)
        {
            if (SaveList.SelectedItem is not GameSaveEntry entry || _backupService == null)
                return;

            await RunBusyAsync($"正在备份 {entry.GameName}…", async token =>
            {
                var result = await _backupService.BackupAsync(entry, cancellationToken: token);
                await RefreshEntryBackupStateAsync(entry);
                await AnalyzeSelectedEntryAsync(entry);
                SetStatus(result.WasSkipped
                    ? $"存档没有变化，已保留现有备份：{entry.AccountDisplayName} / {entry.GameName}"
                    : $"已备份：{entry.AccountDisplayName} / {entry.GameName}", false);
            });
        }

        private async void BackupAll_Click(object sender, RoutedEventArgs e)
        {
            if (_backupService == null || _entries.Count == 0)
            {
                SetStatus("没有可备份的存档。", false);
                return;
            }

            var entries = _entries.Where(entry => entry.IsAvailable).ToList();
            var unavailable = _entries.Count - entries.Count;
            if (entries.Count == 0)
            {
                SetStatus("没有当前可用的存档目录。", false);
                return;
            }
            var failed = new List<string>();
            var skipped = 0;
            await RunBusyAsync($"正在备份全部 {entries.Count} 组存档…", async token =>
            {
                for (var index = 0; index < entries.Count; index++)
                {
                    var entry = entries[index];
                    SetStatus($"正在备份 {index + 1}/{entries.Count}：{entry.GameName}", true);
                    try
                    {
                        var result = await _backupService.BackupAsync(entry, cancellationToken: token);
                        if (result.WasSkipped) skipped++;
                        await RefreshEntryBackupStateAsync(entry);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{entry.AccountDisplayName} / {entry.GameName}");
                        AppLogger.Error($"Backup all failed for SteamId={entry.SteamId}, AppId={entry.AppId}", ex);
                    }
                }

                var unavailableText = unavailable > 0 ? $"，{unavailable} 个目录不可用" : string.Empty;
                SetStatus(failed.Count == 0
                    ? $"全部检查完成：新增 {entries.Count - skipped} 份，跳过 {skipped} 组未变化存档{unavailableText}。"
                    : $"完成 {entries.Count - failed.Count}/{entries.Count} 组；跳过 {skipped} 组未变化，{failed.Count} 组失败{unavailableText}。", false);
            });
        }

        private async void RestoreLatest_Click(object sender, RoutedEventArgs e)
        {
            if (SaveList.SelectedItem is not GameSaveEntry entry || _backupService == null)
                return;

            if (BackupHistoryCombo.SelectedItem is not GameSaveBackupInfo selectedBackup)
            {
                MessageBox.Show(Window.GetWindow(this), "该账号与游戏尚无可恢复备份。", "游戏存档中心", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(Window.GetWindow(this),
                $"将恢复 {entry.AccountDisplayName} 的《{entry.GameName}》。\n" +
                $"备份版本：{selectedBackup.CreatedAt:yyyy-MM-dd HH:mm:ss}（{selectedBackup.KindDisplay}）\n\n" +
                "程序会先关闭 Steam，并自动备份当前存档以便回滚。是否继续？",
                "确认恢复存档", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;

            await RunBusyAsync($"正在准备恢复 {entry.GameName}…", async token =>
            {
                var steamService = _mainViewModel.GetAccountManager().GetSteamService();
                await _backupService.RestoreAsync(entry, selectedBackup.FilePath, token, async _ =>
                {
                    if (!steamService.IsSteamRunning())
                        return;
                    SetStatus("备份校验通过，正在安全关闭 Steam…", true);
                    if (!await steamService.CloseSteamAsync())
                        throw new InvalidOperationException("无法关闭 Steam。请手动退出 Steam 和游戏后重试。");
                });
                await RefreshEntryFileStateAsync(entry);
                SetStatus($"恢复完成：{entry.AccountDisplayName} / {entry.GameName}", false);
                MessageBox.Show(Window.GetWindow(this),
                    "存档已恢复，原存档也已生成保护备份。\n\n重新启动 Steam 时如果出现 Steam Cloud 冲突，请选择刚恢复的本地文件。",
                    "恢复完成", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_backupService == null)
                return;
            Directory.CreateDirectory(_backupService.BackupRoot);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = _backupService.BackupRoot,
                UseShellExecute = true
            });
            process?.Dispose();
        }

        private async void VerifyBackup_Click(object sender, RoutedEventArgs e)
        {
            if (SaveList.SelectedItem is not GameSaveEntry entry ||
                BackupHistoryCombo.SelectedItem is not GameSaveBackupInfo backup ||
                _backupService == null)
                return;

            await RunBusyAsync("正在校验备份清单、文件大小与 SHA-256…", async token =>
            {
                await _backupService.VerifyBackupAsync(entry, backup.FilePath, token);
                SetStatus($"备份校验通过：{backup.DisplayName}", false);
            });
        }

        private async void ExportBackup_Click(object sender, RoutedEventArgs e)
        {
            if (SaveList.SelectedItem is not GameSaveEntry entry ||
                BackupHistoryCombo.SelectedItem is not GameSaveBackupInfo backup ||
                _backupService == null)
                return;

            var dialog = new SaveFileDialog
            {
                Title = "导出存档备份",
                Filter = "SteamSwitch 存档备份 (*.zip)|*.zip",
                FileName = $"{entry.GameName}-{backup.DisplayName}.zip",
                AddExtension = true,
                DefaultExt = ".zip"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            await RunBusyAsync("正在校验并导出备份…", async token =>
            {
                await _backupService.ExportBackupAsync(entry, backup.FilePath, dialog.FileName, token);
                SetStatus($"备份已导出：{dialog.FileName}", false);
            });
        }

        private async void ImportBackup_Click(object sender, RoutedEventArgs e)
        {
            if (SaveList.SelectedItem is not GameSaveEntry entry || _backupService == null)
                return;

            var dialog = new OpenFileDialog
            {
                Title = $"为 {entry.GameName} 导入完整备份",
                Filter = "SteamSwitch 存档备份 (*.zip)|*.zip",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            await RunBusyAsync("正在校验并导入备份…", async token =>
            {
                var imported = await _backupService.ImportBackupAsync(entry, dialog.FileName, token);
                LoadBackupHistory(entry, imported);
                entry.BackupCount = _selectedBackups.Count;
                entry.LastBackupTime = _selectedBackups.FirstOrDefault()?.CreatedAt;
                SetStatus("完整备份已安全导入。", false);
            });
        }

        private void DeleteBackup_Click(object sender, RoutedEventArgs e)
        {
            if (SaveList.SelectedItem is not GameSaveEntry entry ||
                BackupHistoryCombo.SelectedItem is not GameSaveBackupInfo backup ||
                _backupService == null)
                return;

            var result = MessageBox.Show(Window.GetWindow(this),
                $"确定永久删除此备份吗？\n\n{backup.DisplayName}\n{entry.AccountDisplayName} / {entry.GameName}",
                "删除备份", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                _backupService.DeleteBackup(entry, backup.FilePath);
                LoadBackupHistory(entry);
                entry.BackupCount = _selectedBackups.Count;
                entry.LastBackupTime = _selectedBackups.FirstOrDefault()?.CreatedAt;
                UpdateSelectionDetails(entry);
                _entryView.Refresh();
                SetActionButtonsEnabled(true);
                SetStatus("所选备份已删除。", false);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unable to delete game save backup.", ex);
                MessageBox.Show(Window.GetWindow(this), ex.Message, "删除备份失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RetentionCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ManualRetentionCombo.SelectedItem is not int manual || SafetyRetentionCombo.SelectedItem is not int safety)
                return;
            _mainViewModel.GameSaveManualRetention = manual;
            _mainViewModel.GameSaveSafetyRetention = safety;

            var steamPath = _mainViewModel.GetAccountManager().GetSteamService().SteamPath;
            if (!string.IsNullOrWhiteSpace(steamPath) && Directory.Exists(steamPath))
                _backupService = CreateBackupService(steamPath);
            SetStatus($"保留策略已更新：每组手动 {manual} 份、保护 {safety} 份。", false);
        }

        private GameSaveBackupService CreateBackupService(string steamPath) => new(
            steamPath,
            manualRetention: _mainViewModel.GameSaveManualRetention,
            safetyRetention: _mainViewModel.GameSaveSafetyRetention);

        private async Task RefreshEntryBackupStateAsync(GameSaveEntry entry)
        {
            if (_backupService == null) return;
            var backups = await Task.Run(() => _backupService.GetBackups(entry));
            entry.BackupCount = backups.Count;
            entry.LastBackupTime = backups.FirstOrDefault()?.CreatedAt;
            _entryView.Refresh();
            if (ReferenceEquals(SaveList.SelectedItem, entry))
            {
                LoadBackupHistory(entry, selectedPath: backups.FirstOrDefault()?.FilePath);
                UpdateSelectionDetails(entry);
            }
        }

        private async Task RefreshEntryFileStateAsync(GameSaveEntry entry)
        {
            var state = await Task.Run(() =>
            {
                var files = Directory.EnumerateFiles(entry.SavePath, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = false,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    })
                    .Select(path => new FileInfo(path))
                    .ToList();
                return new
                {
                    Count = files.Count,
                    Size = files.Sum(file => file.Length),
                    LastWrite = files.Select(file => file.LastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max()
                };
            });
            entry.FileCount = state.Count;
            entry.TotalSize = state.Size;
            entry.LastWriteTime = state.LastWrite;
            await RefreshEntryBackupStateAsync(entry);
            UpdateSelectionDetails(entry);
        }

        private async Task RunBusyAsync(string initialStatus, Func<CancellationToken, Task> operation)
        {
            if (_disposed || _isBusy) return;
            _isBusy = true;
            BusyProgress.Visibility = Visibility.Visible;
            SetActionButtonsEnabled(false);
            SetStatus(initialStatus, true);
            try
            {
                await operation(_lifetimeCts.Token);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                AppLogger.Error("Game save center operation failed.", ex);
                SetStatus($"操作失败：{ex.Message}", false);
                MessageBox.Show(Window.GetWindow(this), ex.Message, "游戏存档中心", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                if (!_disposed)
                {
                    BusyProgress.Visibility = Visibility.Collapsed;
                    SetActionButtonsEnabled(true);
                }
            }
        }

        private void SetActionButtonsEnabled(bool enabled)
        {
            BackupAllButton.IsEnabled = enabled && _entries.Count > 0;
            BackupSelectedButton.IsEnabled = enabled && SaveList.SelectedItem is GameSaveEntry { IsAvailable: true };
            RestoreButton.IsEnabled = enabled && SaveList.SelectedItem != null && BackupHistoryCombo.SelectedItem != null;
            VerifyBackupButton.IsEnabled = enabled && SaveList.SelectedItem != null && BackupHistoryCombo.SelectedItem != null;
            DeleteBackupButton.IsEnabled = enabled && SaveList.SelectedItem != null && BackupHistoryCombo.SelectedItem != null;
            ExportBackupButton.IsEnabled = enabled && SaveList.SelectedItem != null && BackupHistoryCombo.SelectedItem != null;
            ImportBackupButton.IsEnabled = enabled && SaveList.SelectedItem != null;
            RemoveCustomFolderButton.IsEnabled = enabled && SaveList.SelectedItem is GameSaveEntry { IsCustom: true, IsDetected: false };
        }

        private void SaveList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (SaveList.SelectedItem is GameSaveEntry entry)
            {
                UpdateSelectionDetails(entry);
                LoadBackupHistory(entry);
                _ = AnalyzeSelectedEntryAsync(entry);
            }
            else
                ClearSelectionDetails();
            SetActionButtonsEnabled(!_isBusy);
        }

        private void UpdateSelectionDetails(GameSaveEntry entry)
        {
            SelectedGameText.Text = entry.GameName;
            SelectedAccountText.Text = entry.IsDetected
                ? $"{entry.AccountDisplayName} · 智能识别 · {entry.DetectionRule}"
                : entry.IsCustom
                    ? $"{entry.AccountDisplayName} · 自定义目录"
                    : $"{entry.AccountDisplayName} · {entry.SourceDisplay}";
            FileCountText.Text = $"{entry.FileCount} 个";
            TotalSizeText.Text = entry.SizeDisplay;
            LastWriteText.Text = entry.LastWriteDisplay;
            BackupCountText.Text = entry.BackupDisplay;
            SavePathText.Text = entry.SavePath;
            SelectedGameIcon.Source = LoadGameIcon(entry.IconPath);
            SelectedGameIcon.Visibility = SelectedGameIcon.Source == null ? Visibility.Collapsed : Visibility.Visible;
            SelectedGameFallbackIcon.Visibility = SelectedGameIcon.Source == null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LoadBackupHistory(GameSaveEntry entry, string? selectedPath = null)
        {
            if (_backupService == null) return;
            selectedPath ??= (BackupHistoryCombo.SelectedItem as GameSaveBackupInfo)?.FilePath;
            var backups = _backupService.GetBackups(entry);
            _selectedBackups.Clear();
            foreach (var backup in backups)
                _selectedBackups.Add(backup);
            BackupHistoryCombo.SelectedItem = _selectedBackups.FirstOrDefault(backup =>
                string.Equals(backup.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? _selectedBackups.FirstOrDefault();
        }

        private void BackupHistoryCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            SetActionButtonsEnabled(!_isBusy);
        }

        private async Task AnalyzeSelectedEntryAsync(GameSaveEntry entry)
        {
            if (_disposed || _backupService == null) return;
            if (!entry.IsAvailable)
            {
                ChangeSummaryText.Text = "目录当前不可用；已有备份仍可恢复到原路径";
                return;
            }
            var version = ++_changeAnalysisVersion;
            ChangeSummaryText.Text = "正在比较最新备份…";
            try
            {
                var summary = await _backupService.AnalyzeChangesAsync(entry, _lifetimeCts.Token);
                if (_disposed || version != _changeAnalysisVersion || !ReferenceEquals(SaveList.SelectedItem, entry)) return;
                ChangeSummaryText.Text = summary.DisplayText;
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (!_disposed && version == _changeAnalysisVersion)
                    ChangeSummaryText.Text = $"无法分析变化：{ex.Message}";
            }
        }

        private void ClearSelectionDetails()
        {
            SelectedGameText.Text = "请选择一个存档";
            SelectedAccountText.Text = "—";
            FileCountText.Text = "—";
            TotalSizeText.Text = "—";
            LastWriteText.Text = "—";
            BackupCountText.Text = "—";
            SavePathText.Text = "—";
            ChangeSummaryText.Text = "选择存档后分析变化";
            SelectedGameIcon.Source = null;
            SelectedGameIcon.Visibility = Visibility.Collapsed;
            SelectedGameFallbackIcon.Visibility = Visibility.Visible;
            _changeAnalysisVersion++;
            _selectedBackups.Clear();
        }

        private static BitmapImage? LoadGameIcon(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 96;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }

        private void RebuildAccountFilter(IEnumerable<GameSaveEntry> entries)
        {
            var selectedSteamId = (AccountFilter.SelectedItem as AccountFilterOption)?.SteamId;
            AccountFilter.Items.Clear();
            AccountFilter.Items.Add(AllAccounts);
            foreach (var entry in entries
                         .GroupBy(item => item.SteamId)
                         .Select(group => group.First())
                         .OrderBy(item => item.AccountDisplayName))
            {
                AccountFilter.Items.Add(new AccountFilterOption(
                    entry.SteamId,
                    $"{entry.AccountDisplayName} · {entry.AccountName}"));
            }
            AccountFilter.SelectedItem = AccountFilter.Items
                .OfType<AccountFilterOption>()
                .FirstOrDefault(option => option.SteamId == selectedSteamId) ?? AllAccounts;
        }

        private bool FilterEntry(object value)
        {
            if (value is not GameSaveEntry entry) return false;
            var accountSteamId = (AccountFilter.SelectedItem as AccountFilterOption)?.SteamId;
            if (!string.IsNullOrEmpty(accountSteamId) && entry.SteamId != accountSteamId)
                return false;

            var query = SearchBox.Text?.Trim();
            return string.IsNullOrEmpty(query) ||
                   entry.GameName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                   entry.AccountDisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                   entry.AccountName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                   entry.AppId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private void Filter_Changed(object sender, EventArgs e)
        {
            _entryView.Refresh();
            EmptyHint.Visibility = _entryView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        private static bool AreSamePath(string candidate, string normalizedPath)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate)).TrimEnd(Path.DirectorySeparatorChar),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void SetStatus(string text, bool busy)
        {
            StatusText.Text = text;
            if (busy) BusyProgress.Visibility = Visibility.Visible;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppLogger.LogDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppLogger.LogDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                SetStatus($"无法打开日志目录：{ex.Message}", false);
            }
        }

        private void CopySavePath_Click(object sender, RoutedEventArgs e)
        {
            if (SaveList.SelectedItem is not GameSaveEntry entry || string.IsNullOrWhiteSpace(entry.SavePath)) return;
            try
            {
                Clipboard.SetText(entry.SavePath);
                SetStatus("已复制存档路径。", false);
            }
            catch (Exception ex)
            {
                SetStatus($"无法复制存档路径：{ex.Message}", false);
            }
        }

        private void OpenSaveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (SaveList.SelectedItem is not GameSaveEntry entry || string.IsNullOrWhiteSpace(entry.SavePath)) return;
            try
            {
                if (Directory.Exists(entry.SavePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = entry.SavePath,
                        UseShellExecute = true
                    });
                }
                else
                {
                    SetStatus("存档目录当前不存在。", false);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"无法打开存档目录：{ex.Message}", false);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _changeAnalysisVersion++;
            _lifetimeCts.Cancel();
            GC.SuppressFinalize(this);
        }

        private sealed record AccountFilterOption(string? SteamId, string DisplayName);
    }
}
