using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Reflection;
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

    internal sealed class SemanticReleaseVersion : IComparable<SemanticReleaseVersion>
    {
        private readonly int[] _core;
        private readonly string[] _prerelease;

        private SemanticReleaseVersion(int[] core, string[] prerelease)
        {
            _core = core;
            _prerelease = prerelease;
        }

        public static bool TryParse(string? value, out SemanticReleaseVersion version)
        {
            version = null!;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.Trim().TrimStart('v', 'V');
            var buildIndex = normalized.IndexOf('+');
            if (buildIndex >= 0)
                normalized = normalized[..buildIndex];
            var prereleaseIndex = normalized.IndexOf('-');
            var prerelease = prereleaseIndex >= 0
                ? normalized[(prereleaseIndex + 1)..]
                    .Split('.', StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            var coreText = prereleaseIndex >= 0 ? normalized[..prereleaseIndex] : normalized;
            var components = coreText.Split('.');
            if (components.Length is < 2 or > 4 ||
                components.Any(component => !int.TryParse(component, out var number) || number < 0) ||
                (prereleaseIndex >= 0 && prerelease.Length == 0))
                return false;

            var core = new int[4];
            for (var index = 0; index < components.Length; index++)
                core[index] = int.Parse(components[index]);
            version = new SemanticReleaseVersion(core, prerelease);
            return true;
        }

        public int CompareTo(SemanticReleaseVersion? other)
        {
            if (other == null) return 1;
            for (var index = 0; index < _core.Length; index++)
            {
                var comparison = _core[index].CompareTo(other._core[index]);
                if (comparison != 0) return comparison;
            }
            if (_prerelease.Length == 0)
                return other._prerelease.Length == 0 ? 0 : 1;
            if (other._prerelease.Length == 0)
                return -1;
            for (var index = 0; index < Math.Min(_prerelease.Length, other._prerelease.Length); index++)
            {
                var leftNumeric = int.TryParse(_prerelease[index], out var leftNumber);
                var rightNumeric = int.TryParse(other._prerelease[index], out var rightNumber);
                int comparison;
                if (leftNumeric && rightNumeric) comparison = leftNumber.CompareTo(rightNumber);
                else if (leftNumeric) comparison = -1;
                else if (rightNumeric) comparison = 1;
                else comparison = string.Compare(_prerelease[index], other._prerelease[index], StringComparison.OrdinalIgnoreCase);
                if (comparison != 0) return comparison;
            }
            return _prerelease.Length.CompareTo(other._prerelease.Length);
        }
    }

    public class UpdateService : IDisposable
    {
        private const string RepoApiUrl = "https://api.github.com/repos/ddxgtx/SteamSwitch/releases/latest";
        private const string RepoPrereleaseApiUrl = "https://api.github.com/repos/ddxgtx/SteamSwitch/releases?per_page=20";
        private const long MaxInstallerSize = 512L * 1024 * 1024;
        public const string ReleasesUrl = "https://github.com/ddxgtx/SteamSwitch/releases";
        private static string PendingUpdatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamSwitch", "update-pending.json");

        private readonly HttpClient _http;
        private readonly string _rateLimitStatePath;
        private readonly string _downloadDirectory;
        private readonly object _downloadSync = new();
        private CancellationTokenSource? _downloadCts;
        private volatile bool _disposed;

        public event EventHandler<UpdateProgressEventArgs>? DownloadProgress;
        public event EventHandler<string>? DownloadCompleted;
        public event EventHandler<string>? DownloadFailed;

        public UpdateService(HttpMessageHandler? handler = null, string? rateLimitStatePath = null, string? downloadDirectory = null)
        {
            _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
            // Infinite so large installer downloads are not cancelled mid-body.
            // Callers apply explicit deadlines via linked CTS (see CheckForUpdateAsync).
            _http.Timeout = Timeout.InfiniteTimeSpan;
            _http.DefaultRequestHeaders.Add("User-Agent", "SteamSwitch");
            _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            _rateLimitStatePath = rateLimitStatePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamSwitch", "update-rate-limit.json");
            _downloadDirectory = downloadDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamSwitch", "updates");
        }

        public async Task<UpdateInfo?> CheckForUpdateAsync(bool includePrereleases = false, CancellationToken cancellationToken = default)
        {
            // Explicit 15s deadline for the metadata request; the shared client
            // itself has an infinite timeout so downloads are not affected.
            using var checkTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            checkTimeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            var checkToken = checkTimeoutCts.Token;

            try
            {
                var deferredUntil = ReadDeferredUntil();
                if (deferredUntil.HasValue && deferredUntil.Value > DateTimeOffset.UtcNow)
                    throw CreateRateLimitException(deferredUntil.Value);

                var apiUrl = includePrereleases ? RepoPrereleaseApiUrl : RepoApiUrl;
                using var response = await _http.GetAsync(apiUrl, checkToken);
                if (IsRateLimited(response))
                {
                    var retryAfter = GetRetryAfter(response);
                    SaveDeferredUntil(retryAfter);
                    throw CreateRateLimitException(retryAfter);
                }
                response.EnsureSuccessStatusCode();
                ClearDeferredUntil();

                var json = await response.Content.ReadAsStringAsync(checkToken);
                using var doc = JsonDocument.Parse(json);
                var currentVersionText = GetCurrentVersion();
                if (!SemanticReleaseVersion.TryParse(currentVersionText, out var currentVersion))
                    throw new JsonException("当前应用版本号无效。");

                JsonElement release;
                if (includePrereleases)
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        throw new JsonException("GitHub 测试版发布列表格式无效。");

                    release = default;
                    SemanticReleaseVersion? selectedVersion = null;
                    foreach (var candidate in doc.RootElement.EnumerateArray())
                    {
                        if (candidate.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                            continue;

                        var candidateTag = candidate.GetProperty("tag_name").GetString() ?? "";
                        if (!SemanticReleaseVersion.TryParse(candidateTag, out var candidateVersion) ||
                            candidateVersion.CompareTo(currentVersion) <= 0 ||
                            (selectedVersion != null && candidateVersion.CompareTo(selectedVersion) <= 0))
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
                if (!SemanticReleaseVersion.TryParse(latestVersion, out var latest) ||
                    latest.CompareTo(currentVersion) <= 0)
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

                    // Portable ZIP archives are useful for manual downloads but
                    // cannot be passed to LaunchInstaller (which intentionally
                    // accepts only a verified setup executable). Do not expose
                    // a misleading "download then install" path when a Release
                    // has no compatible setup package.
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

        private static readonly TimeSpan MaxRetryAfterDelay = TimeSpan.FromHours(1);

        private static bool IsRateLimited(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return true;
            if (response.StatusCode != HttpStatusCode.Forbidden)
                return false;

            // Prefer the explicit remaining-quota header: a 403 with remaining > 0
            // is usually a permission/SSO issue, not a rate limit.
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) &&
                long.TryParse(remainingValues.FirstOrDefault(), out var remaining) &&
                remaining > 0)
                return false;

            // GitHub uses 403 for both primary and secondary rate limits. When the
            // remaining-quota header is absent or zero, backing off is safer than
            // repeatedly retrying an ambiguous 403 response.
            return true;
        }

        private static DateTimeOffset GetRetryAfter(HttpResponseMessage response)
        {
            var now = DateTimeOffset.UtcNow;
            DateTimeOffset candidate;

            if (response.Headers.RetryAfter?.Date is { } retryDate)
                candidate = retryDate.ToUniversalTime();
            else if (response.Headers.RetryAfter?.Delta is { } retryDelta)
                candidate = now + retryDelta;
            else if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                long.TryParse(resetValues.FirstOrDefault(), out var resetUnix))
                candidate = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
            else
                candidate = now.AddMinutes(1);

            // Clamp so a hostile/absurd Retry-After cannot disable update checks
            // for an unbounded period.
            if (candidate <= now)
                candidate = now.AddMinutes(1);
            if (candidate - now > MaxRetryAfterDelay)
                candidate = now + MaxRetryAfterDelay;
            return candidate;
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
            CancellationTokenSource operationCts;
            lock (_downloadSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _downloadCts?.Cancel();
                operationCts = new CancellationTokenSource();
                _downloadCts = operationCts;
            }

            try
            {
                var filePath = await DownloadUpdateToFileAsync(update, operationCts.Token);
                if (IsCurrentDownload(operationCts))
                    DownloadCompleted?.Invoke(this, filePath);
            }
            catch (OperationCanceledException)
            {
                if (IsCurrentDownload(operationCts))
                    DownloadFailed?.Invoke(this, "下载已取消");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Download update failed.", ex);
                if (IsCurrentDownload(operationCts))
                    DownloadFailed?.Invoke(this, $"下载失败: {ex.Message}");
            }
            finally
            {
                lock (_downloadSync)
                {
                    if (ReferenceEquals(_downloadCts, operationCts))
                        _downloadCts = null;
                }
                operationCts.Dispose();
            }
        }

        public async Task<string> DownloadUpdateToFileAsync(UpdateInfo update, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.IsNullOrEmpty(update.DownloadUrl))
                throw new InvalidOperationException("没有可用的下载链接");
            if (!update.CanVerifyIntegrity)
                throw new InvalidDataException("该 Release 未提供 GitHub SHA-256 摘要，为安全起见请前往 GitHub 手动下载。");
            if (update.FileSize is <= 0 or > MaxInstallerSize)
                throw new InvalidDataException("安装包大小无效或超过 512 MB 安全上限。");

            var downloadUri = new Uri(update.DownloadUrl, UriKind.Absolute);
            EnsureTrustedDownloadUri(downloadUri);

            var downloadDir = _downloadDirectory;
            Directory.CreateDirectory(downloadDir);

            var fileName = string.IsNullOrWhiteSpace(update.FileName)
                ? $"SteamSwitch-v{update.Version}-setup.exe"
                : update.FileName;
            if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Release 资源文件名无效。");
            var filePath = Path.Combine(downloadDir, fileName);
            var temporaryPath = filePath + $".{Guid.NewGuid():N}.download";

            if (await IsVerifiedCachedInstallerAsync(filePath, update.FileSize, update.ExpectedSha256, cancellationToken))
            {
                DownloadProgress?.Invoke(this, new UpdateProgressEventArgs
                {
                    BytesReceived = update.FileSize,
                    TotalBytes = update.FileSize,
                    PercentComplete = 100
                });
                AppLogger.Info($"Reusing verified update package: {fileName}");
                return filePath;
            }

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
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                string actualSha256;
                await using (var fileStream = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    hash.AppendData(buffer, 0, bytesRead);
                    totalRead += bytesRead;
                    if (totalRead > MaxInstallerSize || totalRead > update.FileSize)
                        throw new InvalidDataException("实际下载大小超过 Release 声明值，已终止更新。");

                    var percent = totalBytes > 0 ? (int)Math.Min(100, totalRead * 100 / totalBytes) : 0;
                    if (!_disposed)
                    {
                        DownloadProgress?.Invoke(this, new UpdateProgressEventArgs
                        {
                            BytesReceived = totalRead,
                            TotalBytes = totalBytes,
                            PercentComplete = percent
                        });
                    }
                }

                await fileStream.FlushAsync(cancellationToken);
                fileStream.Flush(flushToDisk: true);

                if (totalRead != update.FileSize)
                    throw new InvalidDataException("安装包实际大小与 GitHub Release 声明不一致。");

                actualSha256 = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actualSha256, update.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包 SHA-256 校验失败，文件可能已损坏或被替换。");
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, filePath, overwrite: true);
                AppLogger.Info($"Update package verified: {fileName}, SHA256={actualSha256}");
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
            return filePath;
        }

        private static async Task<bool> IsVerifiedCachedInstallerAsync(
            string filePath,
            long expectedSize,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists || info.Length != expectedSize)
                    return false;

                await using var stream = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken);
                return string.Equals(Convert.ToHexString(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to reuse cached update package: {ex.Message}");
                return false;
            }
        }

        public void CancelDownload()
        {
            lock (_downloadSync)
                _downloadCts?.Cancel();
            // 取消后立刻清理，避免残留的 .download 一直占着磁盘。
            try { CleanupOldUpdatePackages(); }
            catch (Exception ex) { AppLogger.Warn($"Unable to clean update packages after cancel: {ex.Message}"); }
        }

        private bool IsCurrentDownload(CancellationTokenSource operationCts)
        {
            lock (_downloadSync)
                return !_disposed && ReferenceEquals(_downloadCts, operationCts);
        }

        public static bool LaunchInstaller(string filePath, string expectedSha256, bool silent = true)
        {
            try
            {
                if (!File.Exists(filePath) ||
                    !filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    expectedSha256.Length != 64)
                    throw new InvalidDataException("安装包未通过启动前安全检查。");

                // Keep the FileStream open across Process.Start so the hashed bytes
                // cannot be swapped out between verification and launch (TOCTOU).
                using (var stream = File.OpenRead(filePath))
                {
                    var actualSha256 = Convert.ToHexString(SHA256.HashData(stream));
                    if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("安装包在下载后发生变化，已阻止启动。");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    };
                    if (silent)
                    {
                        startInfo.ArgumentList.Add("/VERYSILENT");
                        startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
                        startInfo.ArgumentList.Add("/NORESTART");
                        startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");
                        startInfo.ArgumentList.Add("/SP-");
                    }

                    if (Process.Start(startInfo) == null)
                        throw new InvalidOperationException("无法启动更新安装器。");
                }

                // Persist the checkpoint only after the installer process was
                // actually created. A failed Process.Start must not leave a
                // false-positive "unfinished update" warning behind.
                WritePendingUpdateCheckpoint(filePath, expectedSha256);

                AppLogger.Info($"Update installer started. Silent={silent}");
                if (Application.Current?.MainWindow is SteamSwitcher.MainWindow mainWindow)
                    mainWindow.PrepareForApplicationShutdown();
                Application.Current?.Shutdown();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Launch installer failed.", ex);
                return false;
            }
        }

        public static bool HasPendingUpdateCheckpoint() => File.Exists(PendingUpdatePath);

        public static void CompletePendingUpdateCheckpoint()
        {
            try
            {
                if (File.Exists(PendingUpdatePath))
                    File.Delete(PendingUpdatePath);
                CleanupOldUpdatePackages();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to clear update checkpoint: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理陈旧安装包与中断的下载残留。取消下载、安装未完成或进程被杀时
        /// 都必须有机会执行到，否则 %LOCALAPPDATA%\SteamSwitch\updates 里的
        /// *.exe 与 *.download 会无限堆积（单个最大 512MB）。
        /// </summary>
        public static void CleanupOldUpdatePackages()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamSwitch", "updates");
            if (!Directory.Exists(directory))
                return;

            var cutoff = DateTime.UtcNow.AddDays(-30);
            var installers = Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => info.LastWriteTimeUtc < cutoff)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Skip(3) // Keep a few recent packages as a manual recovery point.
                .ToList();
            foreach (var installer in installers)
            {
                try { installer.Delete(); }
                catch (Exception ex) { AppLogger.Warn($"Unable to remove stale update package {installer.Name}: {ex.Message}"); }
            }

            // 中断的下载残留：下载路径的 finally 会删 .download，但进程被杀、
            // 断电或磁盘满时执行不到，只能在这里兜底。
            var partialCutoff = DateTime.UtcNow.AddDays(-1);
            var partials = Directory.EnumerateFiles(directory, "*.download", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => info.LastWriteTimeUtc < partialCutoff)
                .ToList();
            foreach (var partial in partials)
            {
                try { partial.Delete(); }
                catch (Exception ex) { AppLogger.Warn($"Unable to remove partial update download {partial.Name}: {ex.Message}"); }
            }
        }

        private static void WritePendingUpdateCheckpoint(string installerPath, string expectedSha256)
        {
            try
            {
                var directory = Path.GetDirectoryName(PendingUpdatePath)!;
                Directory.CreateDirectory(directory);
                var temporaryPath = PendingUpdatePath + $".{Guid.NewGuid():N}.tmp";
                var payload = new
                {
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CurrentVersion = GetCurrentVersion(),
                    InstallerPath = installerPath,
                    ExpectedSha256 = expectedSha256
                };
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload));
                File.Move(temporaryPath, PendingUpdatePath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to write update checkpoint: {ex.Message}");
            }
        }

        public static string GetCurrentVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = !string.IsNullOrWhiteSpace(informational)
                ? informational
                : assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var metadataIndex = version.IndexOf('+');
            return metadataIndex >= 0 ? version[..metadataIndex] : version;
        }

        public static bool IsCurrentVersionPrerelease() =>
            GetCurrentVersion().Contains('-', StringComparison.Ordinal);

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
            lock (_downloadSync)
            {
                if (_disposed) return;
                _disposed = true;
                _downloadCts?.Cancel();
                _downloadCts = null;
            }
            DownloadProgress = null;
            DownloadCompleted = null;
            DownloadFailed = null;
            _http.Dispose();
        }
    }
}
