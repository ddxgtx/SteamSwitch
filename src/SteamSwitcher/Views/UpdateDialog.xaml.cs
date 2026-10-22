using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SteamSwitcher.Core;
using SteamSwitcher.Services;

namespace SteamSwitcher.Views
{
    public partial class UpdateDialog : Window
    {
        private readonly UpdateService _updateService;
        private readonly UpdateInfo _updateInfo;
        private string? _downloadedFilePath;
        private bool _installLaunchStarted;
        private bool _isClosing;

        private void PostToUi(Action action)
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            try { Dispatcher.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        public UpdateDialog(UpdateService updateService, UpdateInfo updateInfo)
        {
            InitializeComponent();
            _updateService = updateService;
            _updateInfo = updateInfo;

            VersionText.Text = $"v{updateInfo.Version}{(updateInfo.IsPrerelease ? " · 测试版" : "")}";
            CurrentVersionText.Text = $"当前版本：v{UpdateService.GetCurrentVersion()}{(UpdateService.IsCurrentVersionPrerelease() ? " · 测试版" : "")}";
            IntegrityText.Text = updateInfo.CanVerifyIntegrity
                ? "GitHub 官方源 · SHA-256 完整性校验可用"
                : "此版本缺少完整性摘要，只能前往 GitHub 手动下载";
            var integrityForeground = updateInfo.CanVerifyIntegrity ? "SuccessFgBrush" : "WarningFgBrush";
            IntegrityBadge.SetResourceReference(Border.BackgroundProperty,
                updateInfo.CanVerifyIntegrity ? "SuccessBgBrush" : "WarningBgBrush");
            IntegrityIcon.SetResourceReference(TextBlock.ForegroundProperty, integrityForeground);
            IntegrityText.SetResourceReference(TextBlock.ForegroundProperty, integrityForeground);
            IntegrityIcon.Text = updateInfo.CanVerifyIntegrity ? "\uE72E" : "\uE7BA";
            ChangelogText.Text = string.IsNullOrWhiteSpace(updateInfo.Changelog)
                ? "暂无更新说明"
                : updateInfo.Changelog;
            if (!updateInfo.CanVerifyIntegrity)
                DownloadButton.Content = "打开 GitHub";

            _updateService.DownloadProgress += OnDownloadProgress;
            _updateService.DownloadCompleted += OnDownloadCompleted;
            _updateService.DownloadFailed += OnDownloadFailed;

            Closing += (s, e) =>
            {
                _isClosing = true;
                _updateService.CancelDownload();
                _updateService.DownloadProgress -= OnDownloadProgress;
                _updateService.DownloadCompleted -= OnDownloadCompleted;
                _updateService.DownloadFailed -= OnDownloadFailed;
            };
        }

        private void OnDownloadProgress(object? sender, UpdateProgressEventArgs e)
        {
            PostToUi(() =>
            {
                DownloadProgressBar.Value = e.PercentComplete;
                DownloadPercentText.Text = $"{e.PercentComplete}%";

                var receivedMB = e.BytesReceived / 1024.0 / 1024.0;
                var totalMB = e.TotalBytes / 1024.0 / 1024.0;
                DownloadStatusText.Text = $"正在下载... {receivedMB:F1} MB / {totalMB:F1} MB";
            });
        }

        private void OnDownloadCompleted(object? sender, string filePath)
        {
            PostToUi(() =>
            {
                _downloadedFilePath = filePath;
                DownloadStatusText.Text = "下载完成";
                DownloadPercentText.Text = "100%";
                DownloadProgressBar.Value = 100;

                CancelButton.Visibility = Visibility.Collapsed;
                IgnoreButton.Visibility = Visibility.Collapsed;
                TryStartSilentInstall();
            });
        }

        private void OnDownloadFailed(object? sender, string error)
        {
            PostToUi(() =>
            {
                DownloadStatusText.Text = error;
                ProgressPanel.Visibility = Visibility.Collapsed;
                DownloadButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Collapsed;
                IgnoreButton.Visibility = Visibility.Visible;
            });
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Ignore_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_updateInfo.CanVerifyIntegrity)
                {
                    if (Uri.TryCreate(_updateInfo.ReleaseUrl, UriKind.Absolute, out var releaseUri) &&
                        releaseUri.Scheme == Uri.UriSchemeHttps &&
                        releaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                    {
                        Process.Start(new ProcessStartInfo(releaseUri.AbsoluteUri) { UseShellExecute = true });
                        Close();
                    }
                    else
                    {
                        MessageBox.Show("更新页面地址无效，已阻止打开。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return;
                }

                if (string.IsNullOrEmpty(_updateInfo.DownloadUrl))
                {
                    MessageBox.Show("没有可用的下载链接，请前往 GitHub 手动下载。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                DownloadButton.Visibility = Visibility.Collapsed;
                IgnoreButton.Visibility = Visibility.Collapsed;
                ProgressPanel.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;

                await _updateService.DownloadUpdateAsync(_updateInfo);
            }
            catch (OperationCanceledException)
            {
                // User cancelled; Cancel_Click already restored the buttons.
            }
            catch (Exception ex)
            {
                AppLogger.Error("Update download failed.", ex);
                if (_isClosing) return;
                DownloadStatusText.Text = $"下载失败：{ex.Message}";
                ProgressPanel.Visibility = Visibility.Collapsed;
                DownloadButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Collapsed;
                IgnoreButton.Visibility = Visibility.Visible;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _updateService.CancelDownload();
            // Delay UI restore so a concurrent cancellation exception in
            // Download_Click can settle before the download button is re-enabled.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosing) return;
                ProgressPanel.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
                DownloadButton.Visibility = Visibility.Visible;
                IgnoreButton.Visibility = Visibility.Visible;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            TryStartSilentInstall();
        }

        private void TryStartSilentInstall()
        {
            if (_installLaunchStarted || string.IsNullOrEmpty(_downloadedFilePath)) return;
            _installLaunchStarted = true;
            DownloadStatusText.Text = "校验完成，正在关闭程序并自动升级…";
            InstallButton.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;

            if (UpdateService.LaunchInstaller(_downloadedFilePath, _updateInfo.ExpectedSha256, silent: true))
                return;

            _installLaunchStarted = false;
            DownloadStatusText.Text = "无法启动安装器，请点击重试";
            ProgressPanel.Visibility = Visibility.Collapsed;
            InstallButton.Visibility = Visibility.Visible;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
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
    }
}
