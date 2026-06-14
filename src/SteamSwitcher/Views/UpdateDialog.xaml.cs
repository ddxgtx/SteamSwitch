using System;
using System.Windows;
using System.Windows.Input;
using SteamSwitcher.Services;

namespace SteamSwitcher.Views
{
    public partial class UpdateDialog : Window
    {
        private readonly UpdateService _updateService;
        private readonly UpdateInfo _updateInfo;
        private string? _downloadedFilePath;

        public UpdateDialog(UpdateService updateService, UpdateInfo updateInfo)
        {
            InitializeComponent();
            _updateService = updateService;
            _updateInfo = updateInfo;

            VersionText.Text = $"v{updateInfo.Version}";
            CurrentVersionText.Text = $"当前版本：v{UpdateService.GetCurrentVersion()}";
            ChangelogText.Text = string.IsNullOrWhiteSpace(updateInfo.Changelog)
                ? "暂无更新说明"
                : updateInfo.Changelog;

            _updateService.DownloadProgress += OnDownloadProgress;
            _updateService.DownloadCompleted += OnDownloadCompleted;
            _updateService.DownloadFailed += OnDownloadFailed;

            Closing += (s, e) =>
            {
                _updateService.DownloadProgress -= OnDownloadProgress;
                _updateService.DownloadCompleted -= OnDownloadCompleted;
                _updateService.DownloadFailed -= OnDownloadFailed;
            };
        }

        private void OnDownloadProgress(object? sender, UpdateProgressEventArgs e)
        {
            Dispatcher.Invoke(() =>
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
            Dispatcher.Invoke(() =>
            {
                _downloadedFilePath = filePath;
                DownloadStatusText.Text = "下载完成";
                DownloadPercentText.Text = "100%";
                DownloadProgressBar.Value = 100;

                CancelButton.Visibility = Visibility.Collapsed;
                InstallButton.Visibility = Visibility.Visible;
                IgnoreButton.Visibility = Visibility.Collapsed;
            });
        }

        private void OnDownloadFailed(object? sender, string error)
        {
            Dispatcher.Invoke(() =>
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

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _updateService.CancelDownload();
            ProgressPanel.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Visible;
            IgnoreButton.Visibility = Visibility.Visible;
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_downloadedFilePath))
            {
                UpdateService.LaunchInstaller(_downloadedFilePath);
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }
    }
}
