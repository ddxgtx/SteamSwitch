using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
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
        public string ExpectedSha256 { get; set; } = "";
        public bool IsPrerelease { get; set; }
        public bool CanVerifyIntegrity => ExpectedSha256.Length == 64;
    }

    public class UpdateProgressEventArgs : EventArgs
    {
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public int PercentComplete { get; set; }
    }

    public sealed class UpdateCheckException : Exception
    {
        public DateTimeOffset? RetryAfter { get; }

        public UpdateCheckException(string message, DateTimeOffset? retryAfter = null, Exception? innerException = null)
            : base(message, innerException)
        {
            RetryAfter = retryAfter;
        }
    }

    internal sealed class UpdateRateLimitState
    {
        public DateTimeOffset RetryAfterUtc { get; set; }
    }

    public class UpdateService : IDisposable
    {
        private const string RepoApiUrl = "https://api.github.com/repos/ddxgtx/SteamSwitch/releases/latest";
        private const string RepoPrereleaseApiUrl = "https://api.github.com/repos/ddxgtx/SteamSwitch/releases?per_page=20";
        private const long MaxInstallerSize = 512L * 1024 * 1024;
        public const string ReleasesUrl = "https://github.com/ddxgtx/SteamSwitch/releases";

        private readonly HttpClient _http;
        private readonly string _rateLimitStatePath;
        private CancellationTokenSource? _downloadCts;

        public event EventHandler<UpdateProgressEventArgs>? DownloadProgress;
        public event EventHandler<string>? DownloadCompleted;
        public event EventHandler<string>? DownloadFailed;

        public UpdateService(HttpMessageHandler? handler = null, string? rateLimitStatePath = null)
        {
            _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
            _http.Timeout = TimeSpan.FromSeconds(15);
            _http.DefaultRequestHeaders.Add("User-Agent", "SteamSwitch");
            _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            _rateLimitStatePath = rateLimitStatePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamSwitch", "update-rate-limit.json");
        }

        public async Task<UpdateInfo?> CheckForUpdateAsync(bool includePrereleases = false)
        {
            try
            {
                var deferredUntil = ReadDeferredUntil();
                if (deferredUntil.HasValue && deferredUntil.Value > DateTimeOffset.UtcNow)
                    throw CreateRateLimitException(deferredUntil.Value);

                var apiUrl = includePrereleases ? RepoPrereleaseApiUrl : RepoApiUrl;
                using var response = await _http.GetAsync(apiUrl);
                if (IsRateLimited(response))
                {
                    var retryAfter = GetRetryAfter(response);
                    SaveDeferredUntil(retryAfter);
                    throw CreateRateLimitException(retryAfter);
                }
                response.EnsureSuccessStatusCode();
                ClearDeferredUntil();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var currentVersionText = GetCurrentVersion();
                if (!Version.TryParse(currentVersionText, out var currentVersion))
                    throw new JsonException("当前应用版本号无效。");

                JsonElement release;
                if (includePrereleases)
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        throw new JsonException("GitHub 测试版发布列表格式无效。");

                    release = default;
                    Version? selectedVersion = null;
                    foreach (var candidate in doc.RootElement.EnumerateArray())
                    {
                        if (candidate.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                            continue;

                        var candidateTag = candidate.GetProperty("tag_name").GetString() ?? "";
                        if (!Version.TryParse(candidateTag.TrimStart('v', 'V'), out var candidateVersion) ||
                            candidateVersion <= currentVersion ||
                            (selectedVersion != null && candidateVersion <= selectedVersion))
                            continue;

                        selectedVersion = candidateVersion;
                        release = candidate;
                    }

                    if (selectedVersion == null)
                        return null;
                }
                else
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new JsonException("GitHub 正式版发布信息格式无效。");
                    release = doc.RootElement;
                }

                var tagName = release.GetProperty("tag_name").GetString() ?? "";
                var latestVersion = tagName.TrimStart('v', 'V');
                if (!Version.TryParse(latestVersion, out var latest) || latest <= currentVersion)
                    return null;

                var body = release.GetProperty("body").GetString() ?? "";
                var htmlUrl = release.GetProperty("html_url").GetString() ?? ReleasesUrl;
                var isPrerelease = release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean();
                string downloadUrl = "";
                string fileName = "";
                long fileSize = 0;
                string expectedSha256 = "";

                if (release.TryGetProperty("assets", out var assets))
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
                            expectedSha256 = ReadSha256Digest(asset);
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
                                expectedSha256 = ReadSha256Digest(asset);
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
                    FileSize = fileSize,
                    ExpectedSha256 = expectedSha256,
                    IsPrerelease = isPrerelease
                };
            }
            catch (UpdateCheckException)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                throw new UpdateCheckException("连接 GitHub 检查更新超时，请稍后重试。", innerException: ex);
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateCheckException($"无法连接 GitHub 检查更新：{ex.Message}", innerException: ex);
            }
            catch (JsonException ex)
            {
                throw new UpdateCheckException("GitHub 返回的版本信息无效，请稍后重试。", innerException: ex);
            }
        }

        private static bool IsRateLimited(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return true;
            if (response.StatusCode != HttpStatusCode.Forbidden)
                return false;
            // GitHub uses 403 for both primary and secondary rate limits. This
            // client always sends a valid User-Agent, so backing off is safer
            // than repeatedly retrying an ambiguous 403 response.
            return true;
        }

        private static DateTimeOffset GetRetryAfter(HttpResponseMessage response)
        {
            if (response.Headers.RetryAfter?.Date is { } retryDate)
                return retryDate.ToUniversalTime();
            if (response.Headers.RetryAfter?.Delta is { } retryDelta)
                return DateTimeOffset.UtcNow + retryDelta;
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                long.TryParse(resetValues.FirstOrDefault(), out var resetUnix))
                return DateTimeOffset.FromUnixTimeSeconds(resetUnix);
            return DateTimeOffset.UtcNow.AddMinutes(1);
        }

        private static UpdateCheckException CreateRateLimitException(DateTimeOffset retryAfter)
        {
            var localTime = retryAfter.ToLocalTime();
            return new UpdateCheckException(
                $"GitHub 更新检查已达到请求限制，请在 {localTime:HH:mm} 后重试。",
                retryAfter);
        }

        private DateTimeOffset? ReadDeferredUntil()
        {
            try
            {
                if (!File.Exists(_rateLimitStatePath))
                    return null;
                var state = JsonSerializer.Deserialize<UpdateRateLimitState>(File.ReadAllText(_rateLimitStatePath));
                return state?.RetryAfterUtc;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to read update rate-limit state: {ex.Message}");
                return null;
            }
        }

        private void SaveDeferredUntil(DateTimeOffset retryAfter)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_rateLimitStatePath)!);
                File.WriteAllText(_rateLimitStatePath, JsonSerializer.Serialize(
                    new UpdateRateLimitState { RetryAfterUtc = retryAfter.ToUniversalTime() }));
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to persist update rate-limit state: {ex.Message}");
            }
        }

        private void ClearDeferredUntil()
        {
            try
            {
                if (File.Exists(_rateLimitStatePath))
                    File.Delete(_rateLimitStatePath);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to clear update rate-limit state: {ex.Message}");
            }
        }

        public async Task DownloadUpdateAsync(UpdateInfo update)
        {
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
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
            if (!update.CanVerifyIntegrity)
                throw new InvalidDataException("该 Release 未提供 GitHub SHA-256 摘要，为安全起见请前往 GitHub 手动下载。");
            if (update.FileSize is <= 0 or > MaxInstallerSize)
                throw new InvalidDataException("安装包大小无效或超过 512 MB 安全上限。");

            var downloadUri = new Uri(update.DownloadUrl, UriKind.Absolute);
            EnsureTrustedDownloadUri(downloadUri);

            var downloadDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamSwitch", "updates");
            Directory.CreateDirectory(downloadDir);

            var fileName = string.IsNullOrWhiteSpace(update.FileName)
                ? $"SteamSwitch-v{update.Version}-setup.exe"
                : update.FileName;
            if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Release 资源文件名无效。");
            var filePath = Path.Combine(downloadDir, fileName);
            var temporaryPath = filePath + $".{Guid.NewGuid():N}.download";

            using var response = await _http.GetAsync(downloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            EnsureTrustedDownloadUri(response.RequestMessage?.RequestUri
                ?? throw new InvalidDataException("无法确认安装包最终下载地址。"));

            var totalBytes = response.Content.Headers.ContentLength ?? update.FileSize;
            if (totalBytes is <= 0 or > MaxInstallerSize)
                throw new InvalidDataException("服务器返回的安装包大小无效或超过安全上限。");
            var buffer = new byte[81920];
            var totalRead = 0L;

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            try
            {
                await using var fileStream = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    hash.AppendData(buffer, 0, bytesRead);
                    totalRead += bytesRead;
                    if (totalRead > MaxInstallerSize || totalRead > update.FileSize)
                        throw new InvalidDataException("实际下载大小超过 Release 声明值，已终止更新。");

                    var percent = totalBytes > 0 ? (int)Math.Min(100, totalRead * 100 / totalBytes) : 0;
                    DownloadProgress?.Invoke(this, new UpdateProgressEventArgs
                    {
                        BytesReceived = totalRead,
                        TotalBytes = totalBytes,
                        PercentComplete = percent
                    });
                }

                await fileStream.FlushAsync(cancellationToken);
                fileStream.Flush(flushToDisk: true);

                if (totalRead != update.FileSize)
                    throw new InvalidDataException("安装包实际大小与 GitHub Release 声明不一致。");

                var actualSha256 = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actualSha256, update.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包 SHA-256 校验失败，文件可能已损坏或被替换。");

                File.Move(temporaryPath, filePath, overwrite: true);
                AppLogger.Info($"Update package verified: {fileName}, SHA256={actualSha256}");
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
            return filePath;
        }

        public void CancelDownload()
        {
            _downloadCts?.Cancel();
        }

        public static void LaunchInstaller(string filePath, string expectedSha256)
        {
            try
            {
                if (!File.Exists(filePath) ||
                    !filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    expectedSha256.Length != 64)
                    throw new InvalidDataException("安装包未通过启动前安全检查。");

                using var stream = File.OpenRead(filePath);
                var actualSha256 = Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包在下载后发生变化，已阻止启动。");

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

        private static string ReadSha256Digest(JsonElement asset)
        {
            if (!asset.TryGetProperty("digest", out var digestElement))
                return "";

            var digest = digestElement.GetString() ?? "";
            const string prefix = "sha256:";
            if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return "";

            var value = digest[prefix.Length..].Trim();
            return value.Length == 64 && value.All(Uri.IsHexDigit)
                ? value.ToUpperInvariant()
                : "";
        }

        private static void EnsureTrustedDownloadUri(Uri uri)
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新下载地址必须使用 HTTPS。");

            var trusted = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                          uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                          uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
            if (!trusted)
                throw new InvalidDataException($"已阻止非 GitHub 官方下载地址：{uri.Host}");
        }

        public void Dispose()
        {
            _downloadCts?.Dispose();
            _http.Dispose();
        }
    }
}
