using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.ViewModels
{
    public partial class MainViewModel
    {
        private async Task<bool> LaunchGameWithWorkflowAsync(int appId, string gameName, SteamAccount account)
        {
            var binding = _gameBinding.GetBinding(appId);
            gameName = !string.IsNullOrWhiteSpace(gameName)
                ? gameName
                : !string.IsNullOrWhiteSpace(binding?.GameName) ? binding.GameName : $"Steam App {appId}";
            var backupBeforeLaunch = GameSaveCenterEnabled && binding?.BackupBeforeLaunch == true;
            var backupAfterExit = GameSaveCenterEnabled && binding?.BackupAfterExit == true;
            var needsMonitor = backupAfterExit || binding?.CloseSteamAfterExit == true;
            SteamLibraryGame? installedGame = null;
            HashSet<int>? processIdsBeforeLaunch = null;

            if (backupBeforeLaunch)
            {
                StatusText = $"正在备份《{gameName}》启动前存档…";
                var backup = await _gameSaveAutoBackupService.BackupGameAsync(
                    account.SteamId,
                    appId,
                    gameName,
                    _launchWorkflowCts.Token);
                if (!backup.IsComplete)
                {
                    // 「压根没找到存档目录」与「找到但备份失败」必须分开处理：
                    // 前者常见于纯云存档或存档位于未识别的位置，此时若照旧取消启动，
                    // 该游戏只要开着「启动前备份」就永远无法启动，且提示还是自相矛盾的
                    // "失败 0 项，不可用 0 项"。
                    if (backup.HasWork)
                    {
                        StatusText = $"启动前存档备份未完成（失败 {backup.Failed} 项，不可用 {backup.Unavailable} 项），已取消启动《{gameName}》。请检查存档路径";
                        NotificationRequested?.Invoke(this, StatusText);
                        return false;
                    }

                    StatusText = $"未找到《{gameName}》的存档目录（可能是纯云存档），已跳过启动前备份直接启动。";
                    AppLogger.Warn(StatusText);
                }
            }

            if (needsMonitor)
            {
                installedGame = await Task.Run(
                    () => _accountManager.GetSteamService().GetInstalledGames().FirstOrDefault(game => game.AppId == appId),
                    _launchWorkflowCts.Token);
                if (installedGame != null)
                    processIdsBeforeLaunch = GameProcessMonitor.CaptureProcessIds();
            }

            var launched = _accountManager.LaunchGame(
                appId,
                launchArguments: binding?.LaunchArguments,
                startInBigPicture: binding?.StartInBigPicture == true);
            if (!launched)
                return false;

            await RecordStartedGameAsync(appId, account.SteamId, account.AccountName);
            if (needsMonitor && installedGame != null && processIdsBeforeLaunch != null)
                _ = MonitorGameWorkflowAsync(installedGame, account, binding!, processIdsBeforeLaunch, _launchWorkflowCts.Token);
            else if (needsMonitor)
                AppLogger.Warn($"Launch workflow could not monitor appId={appId}: install directory was not found.");
            return true;
        }

        internal async Task RecordStartedGameAsync(int appId, string steamId, string accountName)
        {
            try { await _gameBinding.RecordPlayAsync(appId, steamId, accountName); }
            catch (Exception ex)
            {
                // Launch has already succeeded. A metadata write failure must not
                // misreport the game as failed or skip the exit/backup monitor.
                AppLogger.Error($"Game appId={appId} started, but its play record could not be saved.", ex);
                if (!_disposed) NotificationRequested?.Invoke(this, "游戏已启动，但最近游玩记录未能保存，请检查配置目录权限。");
            }
        }

        private async Task MonitorGameWorkflowAsync(
            SteamLibraryGame game,
            SteamAccount account,
            GameBinding binding,
            ISet<int> processIdsBeforeLaunch,
            CancellationToken cancellationToken)
        {
            try
            {
                var installRoot = Path.Combine(game.LibraryPath, "steamapps", "common", game.InstallDir);
                if (!await GameProcessMonitor.WaitForExitAsync(installRoot, processIdsBeforeLaunch, cancellationToken))
                {
                    DispatchToUi(() =>
                    {
                        var message = $"未能识别《{game.Name}》的运行进程，退出后操作未执行";
                        StatusText = message;
                        NotificationRequested?.Invoke(this, message);
                    });
                    return;
                }
                if (GameSaveCenterEnabled && binding.BackupAfterExit)
                {
                    var backup = await _gameSaveAutoBackupService.BackupGameAsync(
                        account.SteamId,
                        game.AppId,
                        game.Name,
                        cancellationToken);
                    // 与启动前备份一致：没有候选存档时不要报"失败 0 项，不可用 0 项"
                    // 这种自相矛盾的错误，那会让用户以为存档丢了。
                    var message = !backup.IsComplete
                        ? backup.HasWork
                            ? $"《{game.Name}》退出后存档备份未完成（失败 {backup.Failed} 项，不可用 {backup.Unavailable} 项）。请检查存档路径"
                            : $"《{game.Name}》未找到存档目录（可能是纯云存档），已跳过退出后备份"
                        : $"《{game.Name}》退出后存档已备份";
                    DispatchToUi(() =>
                    {
                        StatusText = message;
                        NotificationRequested?.Invoke(this, message);
                    });
                }
                if (binding.CloseSteamAfterExit)
                    await _accountManager.GetSteamService().CloseSteamAsync(SilentCloseSteam);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Game launch workflow monitor failed. appId={game.AppId}", ex);
            }
        }

        [RelayCommand]
        private void OpenDataFolder()
        {
            OpenFolder(SettingsService.SettingsDirectory, "数据目录");
        }

        [RelayCommand]
        private void OpenLogFolder()
        {
            OpenFolder(AppLogger.LogDirectory, "日志目录");
        }

        private void OpenFolder(string path, string name)
        {
            try
            {
                Directory.CreateDirectory(path);
                using var _ = Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
                StatusText = $"已打开{name}";
            }
            catch (Exception ex)
            {
                AppLogger.Error($"OpenFolder failed: {path}", ex);
                StatusText = $"无法打开{name}: {ex.Message}";
            }
        }

        [RelayCommand]
        private void CopyDiagnostics()
        {
            try
            {
                var steamService = _accountManager.GetSteamService();
                var sb = new StringBuilder();
                sb.AppendLine("Steam Switch 诊断信息");
                sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Steam 路径: {steamService.SteamPath ?? "未检测"}");
                sb.AppendLine($"Steam 运行状态: {SteamStateText}");
                sb.AppendLine($"当前账号: {CurrentAccountName}");
                sb.AppendLine($"账号数量: {Accounts.Count}");
                sb.AppendLine($"游戏数量: {GameList.Count}");
                sb.AppendLine($"绑定数量: {GameBindings.Count}");
                sb.AppendLine($"固定入口: {PinnedCountText}");
                sb.AppendLine($"任务栏常驻: {(IsTaskbarPinned ? "开启" : "关闭")}");
                sb.AppendLine($"桌面悬浮窗: {(DesktopFloatingEnabled ? "开启" : "关闭")}");
                sb.AppendLine($"库界面注入: {(EnableLibraryInjection ? "开启" : "关闭")} / {InjectorStateText}");
                sb.AppendLine($"主题: {Theme}");
                sb.AppendLine($"更新保护: {UpdateProtectionText}");
                sb.AppendLine($"本地控制保护: {LocalControlProtectionText}");
                sb.AppendLine($"配置保护: {DataProtectionText}");
                sb.AppendLine($"数据目录: {SettingsService.SettingsDirectory}");
                sb.AppendLine($"日志目录: {AppLogger.LogDirectory}");

                Clipboard.SetText(sb.ToString());
                StatusText = "已复制诊断信息";
            }
            catch (Exception ex)
            {
                AppLogger.Error("CopyDiagnostics failed.", ex);
                StatusText = $"复制诊断信息失败: {ex.Message}";
            }
        }

        private void OnAccountsChanged(object? sender, EventArgs e)
        {
            DispatchToUi(UpdateAccountsList);
        }

        public async Task InitializeAsync(bool checkForUpdates = true, bool startIntegrations = true, bool throwOnFailure = false)
        {
            IsLoading = true;
            StatusText = "正在检测 Steam...";

            try
            {
                _accountManager.GetSteamService().SetCustomPaths(CustomSteamPath, CustomLoginUsersPath);
                var success = await _accountManager.InitializeAsync();
                if (!success)
                {
                    StatusText = _accountManager.AccountSourceWarning ?? "未找到 Steam 安装路径，已加载本地档案";
                }

                StatusText = _accountManager.AccountSourceWarning ?? $"已检测到 {_accountManager.Accounts.Count} 个账号";
                IsSteamRunning = _accountManager.GetSteamService().IsSteamRunning();
                SyncGameSaveAutoBackupService();

                UpdateAccountsList();

                await _gameBinding.LoadAsync();
                try
                {
                    await _quickLaunch.LoadAsync();
                }
                catch (Exception ex)
                {
                    // 加载失败不能中断初始化，但必须让用户知道项目没被读到。
                    StatusText = $"快速启动配置读取失败：{ex.Message}（已保留磁盘文件）";
                    if (throwOnFailure) throw;
                }
                UpdateQuickLaunchList();
                UpdateGameBindingsList();

                if (AutoScanGamesOnStartup)
                    await ScanGamesAsync(manageLoadingState: false);

                if (startIntegrations && EnableLibraryInjection)
                {
                    AppLogger.Info("Library injection is enabled; auto-starting injector after initialization.");
                    _suppressInjectorPrompt = true;
                    _ = StartInjectorAsync();
                }

                if (checkForUpdates && CheckUpdateOnStartup)
                    _ = CheckForUpdatesAsync(isManual: false);
            }
            catch (Exception ex)
            {
                AppLogger.Error("MainViewModel initialization failed.", ex);
                StatusText = $"初始化失败: {ex.Message}";
                if (throwOnFailure) throw;
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task AddQuickLaunchItemAsync(string launchPath)
        {
            if (string.IsNullOrWhiteSpace(launchPath) || (!System.IO.File.Exists(launchPath) && !System.IO.Directory.Exists(launchPath)))
            {
                StatusText = "无效的快速启动路径";
                return;
            }
            var normalizedPath = NormalizeQuickLaunchPath(launchPath);
            var existing = _quickLaunch.GetAll().FirstOrDefault(item =>
                string.Equals(NormalizeQuickLaunchPath(item.ExecutablePath), normalizedPath,
                    StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                StatusText = $"快速启动已存在: {existing.Name}";
                NotificationRequested?.Invoke(this, $"已存在: {existing.Name}");
                return;
            }
            var name = System.IO.Directory.Exists(launchPath)
                ? new System.IO.DirectoryInfo(launchPath).Name
                : System.IO.Path.GetFileNameWithoutExtension(launchPath);
            await _quickLaunch.AddAsync(name, launchPath);
            StatusText = $"已添加快速启动: {name}";
            NotificationRequested?.Invoke(this, $"已添加: {name}");
        }

        private static string NormalizeQuickLaunchPath(string path)
        {
            try
            {
                var fullPath = System.IO.Path.GetFullPath(path.Trim());
                var root = System.IO.Path.GetPathRoot(fullPath);
                return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                    ? fullPath
                    : fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                        System.IO.Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

    }
}
