using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SteamSwitcher.Services
{
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string ReleaseUrl { get; set; } = "";
        public string Changelog { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
    }

    public class UpdateProgressEventArgs : EventArgs
    {
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public int PercentComplete { get; set; }
    }

    public class UpdateService : IDisposable
    {
        private const string RepoApiUrl = "https://api.github.com/repos/ddxgtx/SteamSwitch/releases/latest";
        public const string ReleasesUrl = "https://github.com/ddxgtx/SteamSwitch/releases";

        private readonly HttpClient _http;
        private CancellationTokenSource? _downloadCts;

        public event EventHandler<UpdateProgressEventArgs>? DownloadProgress;
        public event EventHandler<string>? DownloadCompleted;
        public event EventHandler<string>? DownloadFailed;

        public UpdateService()
        {
            _http = new HttpClient();
            _http.DefaultRequestHeaders.Add("User-Agent", "SteamSwitch");
        }

        public async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            try
            {
                HttpResponseMessage response;
                try
                {
                    response = await _http.GetAsync(RepoApiUrl);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"直连 GitHub API 获取更新失败: {ex.Message}。正在尝试国内镜像重试...");
                    var fallbackApiUrl = "https://api.gitmirror.com/repos/ddxgtx/SteamSwitch/releases/latest";
                    response = await _http.GetAsync(fallbackApiUrl);
                }

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tagName = root.GetProperty("tag_name").GetString() ?? "";
                var body = root.GetProperty("body").GetString() ?? "";
                var htmlUrl = root.GetProperty("html_url").GetString() ?? ReleasesUrl;

                var latestVersion = tagName.TrimStart('v', 'V');
                var currentVersion = GetCurrentVersion();

                if (!Version.TryParse(latestVersion, out var latest) ||
                    !Version.TryParse(currentVersion, out var current) ||
                    latest <= current)
                    return null;

                string downloadUrl = "";
                string fileName = "";
                long fileSize = 0;

                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            fileName = name;
                            if (asset.TryGetProperty("size", out var sizeEl))
                                fileSize = sizeEl.GetInt64();
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(downloadUrl))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var name = asset.GetProperty("name").GetString() ?? "";
                            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                                fileName = name;
                                if (asset.TryGetProperty("size", out var sizeEl))
                                    fileSize = sizeEl.GetInt64();
                                break;
                            }
                        }
                    }
                }

                return new UpdateInfo
                {
                    Version = latestVersion,
                    ReleaseUrl = htmlUrl,
                    Changelog = body,
                    DownloadUrl = downloadUrl,
                    FileName = fileName,
                    FileSize = fileSize
                };
            }
            catch (Exception ex)
            {
                AppLogger.Error("Check for update failed.", ex);
                return null;
            }
        }

        public async Task DownloadUpdateAsync(UpdateInfo update)
        {
            _downloadCts = new CancellationTokenSource();

            try
            {
                var filePath = await DownloadUpdateToFileAsync(update, _downloadCts.Token);
                DownloadCompleted?.Invoke(this, filePath);
            }
            catch (OperationCanceledException)
            {
                DownloadFailed?.Invoke(this, "下载已取消");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Download update failed.", ex);
                DownloadFailed?.Invoke(this, $"下载失败: {ex.Message}");
            }
        }

        public async Task<string> DownloadUpdateToFileAsync(UpdateInfo update, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(update.DownloadUrl))
                throw new InvalidOperationException("没有可用的下载链接");

            var downloadDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamSwitch", "updates");
            Directory.CreateDirectory(downloadDir);

            var fileName = string.IsNullOrWhiteSpace(update.FileName)
                ? $"SteamSwitch-v{update.Version}-setup.exe"
                : update.FileName;
            var filePath = Path.Combine(downloadDir, fileName);
            if (File.Exists(filePath))
                File.Delete(filePath);

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(update.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"直连 GitHub 下载安装包失败: {ex.Message}。将尝试使用国内镜像代理下载...");
                var fallbackDownloadUrl = $"https://ghproxy.net/{update.DownloadUrl}";
                try
                {
                    response = await _http.GetAsync(fallbackDownloadUrl,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception fallbackEx)
                {
                    AppLogger.Warn($"国内第一镜像下载失败: {fallbackEx.Message}。尝试第二镜像下载...");
                    var secondFallbackUrl = $"https://mirror.ghproxy.com/{update.DownloadUrl}";
                    response = await _http.GetAsync(secondFallbackUrl,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    response.EnsureSuccessStatusCode();
                }
            }

            var totalBytes = response.Content.Headers.ContentLength ?? update.FileSize;
            var buffer = new byte[81920];
            var totalRead = 0L;

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalRead += bytesRead;

                var percent = totalBytes > 0 ? (int)(totalRead * 100 / totalBytes) : 0;
                DownloadProgress?.Invoke(this, new UpdateProgressEventArgs
                {
                    BytesReceived = totalRead,
                    TotalBytes = totalBytes,
                    PercentComplete = percent
                });
            }

            await fileStream.FlushAsync(cancellationToken);
            return filePath;
        }

        public void CancelDownload()
        {
            _downloadCts?.Cancel();
        }

        public static void LaunchInstaller(string filePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Launch installer failed.", ex);
            }
        }

        public static string GetCurrentVersion()
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        }

        public void Dispose()
        {
            _downloadCts?.Dispose();
            _http.Dispose();
        }
    }
}
