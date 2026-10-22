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
        [RelayCommand]
        private async Task CheckForUpdatesManually()
        {
            await CheckForUpdatesAsync(isManual: true);
        }

        [RelayCommand]
        private void OpenGitHubReleases()
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo
                {
                    FileName = GitHubReleasesUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error("Open GitHub releases failed.", ex);
                StatusText = $"无法打开 GitHub Releases: {ex.Message}";
            }
        }

        private async Task CheckForUpdatesAsync(bool isManual)
        {
            if (_disposed) return;
            var cancellationToken = _lifetimeCts.Token;
            if (Interlocked.Exchange(ref _updateCheckInProgress, 1) != 0)
            {
                if (isManual)
                    StatusText = "更新检查正在进行，请稍候…";
                return;
            }

            try
            {
                if (isManual)
                    StatusText = UseBetaUpdateChannel ? "正在检查 GitHub 测试版更新..." : "正在检查 GitHub 正式版更新...";

                using var updateService = new UpdateService();
                var updateInfo = await updateService.CheckForUpdateAsync(UseBetaUpdateChannel, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (updateInfo != null)
                {
                    StatusText = $"发现新版本 v{updateInfo.Version}";
                    AppLogger.Info($"New version available: v{updateInfo.Version}");

                    if (AutoInstallUpdates)
                    {
                        await DownloadAndInstallUpdateAsync(updateService, updateInfo, cancellationToken);
                        return;
                    }

                    UpdateAvailable?.Invoke(this, updateInfo);
                }
                else if (isManual)
                {
                    StatusText = $"当前已是最新版本 v{UpdateService.GetCurrentVersion()}";
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AppLogger.Info("Update check canceled during shutdown.");
            }
            catch (UpdateCheckException ex)
            {
                if (isManual)
                {
                    AppLogger.Error("Manual update check failed.", ex);
                    StatusText = $"检查更新失败: {ex.Message}";
                }
                else
                {
                    AppLogger.Warn($"Startup update check skipped: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unexpected update check failure.", ex);
                if (isManual)
                    StatusText = $"检查更新失败: {ex.Message}";
            }
            finally
            {
                Volatile.Write(ref _updateCheckInProgress, 0);
            }
        }
        private async Task DownloadAndInstallUpdateAsync(UpdateService updateService, UpdateInfo updateInfo, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(updateInfo.DownloadUrl))
            {
                StatusText = "发现新版本，但没有可用的安装包下载链接";
                UpdateAvailable?.Invoke(this, updateInfo);
                return;
            }

            if (!updateInfo.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "发现新版本，但自动更新需要 setup 安装包";
                UpdateAvailable?.Invoke(this, updateInfo);
                return;
            }

            try
            {
                StatusText = $"正在下载 v{updateInfo.Version} 更新...";
                var filePath = await updateService.DownloadUpdateToFileAsync(updateInfo, cancellationToken);
                StatusText = "更新下载完成，正在启动安装器...";
                if (!UpdateService.LaunchInstaller(filePath, updateInfo.ExpectedSha256, silent: true))
                    StatusText = "无法启动更新安装器，请稍后重试";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AppLogger.Info("Automatic update download canceled during shutdown.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Auto update failed.", ex);
                StatusText = $"自动更新失败: {ex.Message}";
                UpdateAvailable?.Invoke(this, updateInfo);
            }
        }

    }
}
