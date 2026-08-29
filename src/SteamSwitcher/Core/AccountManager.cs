using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    public class AccountManager : IDisposable
    {
        private readonly SteamService _steamService;
        private readonly VdfParser _vdfParser;
        private readonly RegistryHelper _registryHelper;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _switchGate = new(1, 1);
        private CancellationTokenSource? _avatarDownloadCts;
        private bool _disposed;

        public List<SteamAccount> Accounts { get; private set; } = new();
        public SteamAccount? CurrentAccount { get; private set; }

        public event EventHandler? AccountsChanged;
        public event EventHandler? CurrentAccountChanged;
        public Func<SteamAccount?, Task>? BeforeAccountSwitchAsync { get; set; }
        public Func<Task<bool>>? ConfirmForceCloseAfterTimeoutAsync { get; set; }

        public AccountManager()
        {
            _steamService = new SteamService();
            _vdfParser = new VdfParser();
            _registryHelper = new RegistryHelper();
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public SteamService GetSteamService() => _steamService;

        /// <summary>
        /// Uses Steam's current login state to refresh the active account without
        /// rebuilding the account list or starting avatar downloads.
        /// </summary>
        public bool RefreshCurrentAccountFromSteamSession()
        {
            if (_disposed || Accounts.Count == 0)
                return false;

            var autoLoginUser = _registryHelper.GetAutoLoginUser();
            string? mostRecentSteamId = null;
            try
            {
                var loginUsersPath = _steamService.GetLoginUsersPath();
                if (File.Exists(loginUsersPath))
                {
                    var data = _vdfParser.Parse(loginUsersPath);
                    if (data.TryGetValue("users", out var usersObj) &&
                        usersObj is Dictionary<string, object> users)
                    {
                        mostRecentSteamId = users.FirstOrDefault(entry =>
                            entry.Value is Dictionary<string, object> userData &&
                            GetValue(userData, "MostRecent") == "1").Key;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to refresh the active Steam account from loginusers.vdf: {ex.Message}");
            }

            var resolved = Accounts.FirstOrDefault(account =>
                !string.IsNullOrWhiteSpace(autoLoginUser) &&
                string.Equals(account.AccountName, autoLoginUser, StringComparison.OrdinalIgnoreCase))
                ?? Accounts.FirstOrDefault(account =>
                    !string.IsNullOrWhiteSpace(mostRecentSteamId) &&
                    string.Equals(account.SteamId, mostRecentSteamId, StringComparison.OrdinalIgnoreCase))
                ?? Accounts.FirstOrDefault(account => account.MostRecent);

            // Steam rewrites loginusers.vdf in place while signing in/out. During that
            // short window both the registry value and MostRecent flag can be absent.
            // Keep the last verified account instead of making the UI flicker to an
            // unauthenticated state; a subsequent poll will resolve the new account.
            if (resolved == null)
            {
                AppLogger.Debug(
                    $"Active Steam account could not be resolved temporarily; retaining " +
                    $"{CurrentAccount?.AccountName ?? "<none>"}.");
                return false;
            }

            if (AreSameAccount(CurrentAccount, resolved))
                return false;

            CurrentAccount = resolved;
            AppLogger.Info($"Refreshed current Steam account: registry={autoLoginUser ?? "<none>"}, mostRecent={mostRecentSteamId ?? "<none>"}, current={resolved?.AccountName ?? "<none>"}");
            RaiseSafely(CurrentAccountChanged, nameof(CurrentAccountChanged));
            return true;
        }

        public async Task<bool> InitializeAsync()
        {
            if (!_steamService.DetectSteamPath())
                return false;

            await LoadAccountsAsync();
            return true;
        }

        public Task LoadAccountsAsync()
        {
            _avatarDownloadCts?.Cancel();
            _avatarDownloadCts?.Dispose();
            _avatarDownloadCts = new CancellationTokenSource();

            Accounts.Clear();
            CurrentAccount = null;

            var loginUsersPath = _steamService.GetLoginUsersPath();
            if (!File.Exists(loginUsersPath))
            {
                RaiseSafely(AccountsChanged, nameof(AccountsChanged));
                RaiseSafely(CurrentAccountChanged, nameof(CurrentAccountChanged));
                return Task.CompletedTask;
            }

            try
            {
                var autoLoginUser = _registryHelper.GetAutoLoginUser();
                var data = _vdfParser.Parse(loginUsersPath);
                if (data.TryGetValue("users", out var usersObj) &&
                    usersObj is Dictionary<string, object> users)
                {
                    foreach (var userEntry in users)
                    {
                        if (userEntry.Value is Dictionary<string, object> userData)
                        {
                            var account = new SteamAccount
                            {
                                SteamId = userEntry.Key,
                                AccountName = GetValue(userData, "AccountName"),
                                PersonaName = GetValue(userData, "PersonaName"),
                                RememberPassword = GetValue(userData, "RememberPassword") == "1",
                                MostRecent = GetValue(userData, "MostRecent") == "1",
                                Timestamp = long.TryParse(GetValue(userData, "Timestamp"), out var ts) ? ts : 0
                            };

                            account.AvatarPath = GetAvatarPath(account.SteamId);

                            Accounts.Add(account);
                        }
                    }
                }

                Accounts = Accounts.OrderByDescending(a => a.MostRecent).ThenByDescending(a => a.Timestamp).ToList();
                CurrentAccount = Accounts.FirstOrDefault(a =>
                        !string.IsNullOrWhiteSpace(autoLoginUser) &&
                        string.Equals(a.AccountName, autoLoginUser, StringComparison.OrdinalIgnoreCase))
                    ?? Accounts.FirstOrDefault(a => a.MostRecent);

                AppLogger.Info(
                    $"Resolved current Steam account: registry={autoLoginUser}, " +
                    $"current={CurrentAccount?.AccountName ?? "<none>"}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading accounts: {ex.Message}");
            }

            RaiseSafely(AccountsChanged, nameof(AccountsChanged));
            RaiseSafely(CurrentAccountChanged, nameof(CurrentAccountChanged));

            var accountSnapshot = Accounts.ToList();
            _ = DownloadMissingAvatarsAsync(accountSnapshot, _avatarDownloadCts.Token);

            return Task.CompletedTask;
        }

        private async Task DownloadMissingAvatarsAsync(
            IReadOnlyCollection<SteamAccount> accounts,
            CancellationToken cancellationToken)
        {
            try
            {
                var avatarUpdated = false;
                foreach (var account in accounts.Where(a => string.IsNullOrEmpty(a.AvatarPath)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var avatarPath = await DownloadAvatarAsync(account.SteamId, cancellationToken);
                        if (!string.IsNullOrEmpty(avatarPath))
                        {
                            account.AvatarPath = avatarPath;
                            avatarUpdated = true;
                            AppLogger.Info($"Downloaded avatar for {account.AccountName}");
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"Failed to download avatar for {account.AccountName}", ex);
                    }
                }

                if (avatarUpdated && !_disposed && !cancellationToken.IsCancellationRequested)
                    RaiseSafely(AccountsChanged, nameof(AccountsChanged));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A refresh or application shutdown superseded this download pass.
            }
            catch (Exception ex)
            {
                AppLogger.Error("Avatar background refresh failed.", ex);
            }
        }

        public async Task<bool> SwitchAccountAsync(SteamAccount account, bool forceClose = false)
        {
            await _switchGate.WaitAsync();
            try
            {
                return await SwitchAccountCoreAsync(account, forceClose);
            }
            finally
            {
                _switchGate.Release();
            }
        }

        private async Task<bool> SwitchAccountCoreAsync(SteamAccount account, bool forceClose)
        {
            if (_steamService.IsSteamRunning())
            {
                var closed = await _steamService.CloseSteamAsync(forceClose: forceClose);
                if (!closed && !forceClose && ConfirmForceCloseAfterTimeoutAsync != null &&
                    await ConfirmForceCloseAfterTimeoutAsync())
                {
                    closed = await _steamService.CloseSteamAsync(forceClose: true);
                }
                if (!closed)
                    return false;
            }

            if (BeforeAccountSwitchAsync != null)
            {
                try
                {
                    await BeforeAccountSwitchAsync(CurrentAccount);
                }
                catch (Exception ex)
                {
                    // A backup problem must not strand the user between accounts.
                    AppLogger.Error("Before-account-switch operation failed; continuing with account switch.", ex);
                }
            }

            try
            {
                if (!await UpdateLoginUsersVdfAsync(account.SteamId))
                {
                    AppLogger.Error($"SwitchAccountAsync aborted: failed to update loginusers.vdf for {account.AccountName}.");
                    return false;
                }

                try
                {
                    _registryHelper.SetAutoLoginUser(account.AccountName);
                }
                catch
                {
                    TryRestoreLoginUsersBackup();
                    throw;
                }

                account.MostRecent = true;
                account.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var acc in Accounts)
                {
                    if (acc != account)
                        acc.MostRecent = false;
                }
                CurrentAccount = account;

                RaiseSafely(AccountsChanged, nameof(AccountsChanged));
                RaiseSafely(CurrentAccountChanged, nameof(CurrentAccountChanged));
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("SwitchAccountAsync failed.", ex);
                return false;
            }
        }

        public bool LaunchSteam(bool silent = false)
        {
            return _steamService.StartSteam(silent: silent);
        }

        public bool LaunchGame(
            int appId,
            bool silent = false,
            string? launchArguments = null,
            bool startInBigPicture = false)
        {
            return _steamService.StartGame(appId, silent, launchArguments, startInBigPicture);
        }

        private void RaiseSafely(EventHandler? handlers, string eventName)
        {
            if (handlers == null) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"AccountManager event subscriber failed: {eventName}.", ex);
                }
            }
        }

        private async Task<bool> UpdateLoginUsersVdfAsync(string activeSteamId)
        {
            var loginUsersPath = _steamService.GetLoginUsersPath();
            if (!File.Exists(loginUsersPath))
            {
                AppLogger.Error($"loginusers.vdf was not found: {loginUsersPath}");
                return false;
            }

            var tempPath = loginUsersPath + $".steamswitch.{Guid.NewGuid():N}.tmp";
            var backupPath = loginUsersPath + ".steamswitch.bak";

            try
            {
                var data = _vdfParser.Parse(loginUsersPath);
                if (!data.TryGetValue("users", out var usersObj) ||
                    usersObj is not Dictionary<string, object> users)
                {
                    AppLogger.Error($"loginusers.vdf does not contain a users section: {loginUsersPath}");
                    return false;
                }

                var foundActiveAccount = false;
                foreach (var userEntry in users)
                {
                    if (userEntry.Value is Dictionary<string, object> userData)
                    {
                        if (string.Equals(userEntry.Key, activeSteamId, StringComparison.OrdinalIgnoreCase))
                        {
                            foundActiveAccount = true;
                            userData["MostRecent"] = "1";
                            userData["RememberPassword"] = "1";
                            userData["Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                        }
                        else
                        {
                            userData["MostRecent"] = "0";
                        }
                    }
                }

                if (!foundActiveAccount)
                {
                    AppLogger.Error($"Steam account {activeSteamId} was not found in loginusers.vdf.");
                    return false;
                }

                var content = _vdfParser.Serialize(data);
                await using (var stream = new FileStream(
                                 tempPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                await using (var writer = new StreamWriter(stream))
                {
                    await writer.WriteAsync(content);
                    await writer.FlushAsync();
                    stream.Flush(flushToDisk: true);
                }

                var validation = _vdfParser.Parse(tempPath);
                if (!validation.TryGetValue("users", out var validationUsers) ||
                    validationUsers is not Dictionary<string, object>)
                    throw new InvalidDataException("生成的 loginusers.vdf 未通过重新解析校验。");

                var transientBackup = $"{backupPath}.{Guid.NewGuid():N}.tmp";
                var preserveTransientBackup = false;
                try
                {
                    File.Replace(tempPath, loginUsersPath, transientBackup, ignoreMetadataErrors: true);
                    try
                    {
                        File.Move(transientBackup, backupPath, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        // Do not commit a switch without a verified pre-switch rollback point.
                        // Leaving an older .bak in place could restore the wrong account if the
                        // following registry update fails.
                        AppLogger.Error("Unable to rotate the pre-switch loginusers.vdf backup; rolling the VDF change back.", ex);
                        try
                        {
                            File.Replace(transientBackup, loginUsersPath, null, ignoreMetadataErrors: true);
                        }
                        catch (Exception rollbackEx)
                        {
                            preserveTransientBackup = true;
                            AppLogger.Error("Failed to roll back loginusers.vdf after backup rotation failed.", rollbackEx);
                            AppLogger.Error($"The pre-switch VDF was preserved for manual recovery at: {transientBackup}");
                        }
                        return false;
                    }
                }
                finally
                {
                    try
                    {
                        if (!preserveTransientBackup && File.Exists(transientBackup))
                            File.Delete(transientBackup);
                    }
                    catch { }
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to update loginusers.vdf: {loginUsersPath}", ex);
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to clean temporary VDF file {tempPath}: {ex.Message}");
                }
            }
        }

        private void TryRestoreLoginUsersBackup()
        {
            var loginUsersPath = _steamService.GetLoginUsersPath();
            var backupPath = loginUsersPath + ".steamswitch.bak";
            if (!File.Exists(loginUsersPath) || !File.Exists(backupPath))
                return;

            var temporaryPath = loginUsersPath + $".steamswitch.rollback.{Guid.NewGuid():N}.tmp";
            try
            {
                File.Copy(backupPath, temporaryPath, overwrite: false);
                File.Replace(temporaryPath, loginUsersPath, null, ignoreMetadataErrors: true);
                AppLogger.Warn("Registry update failed; loginusers.vdf was restored from the pre-switch backup.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to roll back loginusers.vdf after a registry update failure.", ex);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }

        public static bool AreSameAccount(SteamAccount? left, SteamAccount? right)
        {
            if (ReferenceEquals(left, right))
                return left != null;
            if (left == null || right == null)
                return false;

            if (!string.IsNullOrWhiteSpace(left.SteamId) &&
                !string.IsNullOrWhiteSpace(right.SteamId))
            {
                return string.Equals(left.SteamId, right.SteamId, StringComparison.OrdinalIgnoreCase);
            }

            return !string.IsNullOrWhiteSpace(left.AccountName) &&
                   !string.IsNullOrWhiteSpace(right.AccountName) &&
                   string.Equals(left.AccountName, right.AccountName, StringComparison.OrdinalIgnoreCase);
        }

        private string? GetAvatarPath(string steamId)
        {
            var avatarCachePath = _steamService.GetAvatarCachePath();
            if (string.IsNullOrEmpty(avatarCachePath))
                return null;

            var avatarFile = Path.Combine(avatarCachePath, $"{steamId}.png");
            return File.Exists(avatarFile) ? avatarFile : null;
        }

        private async Task<string?> DownloadAvatarAsync(string steamId, CancellationToken cancellationToken)
        {
            string? response = null;

            // 1. 尝试直接请求官方接口
            try
            {
                var url = $"https://steamcommunity.com/profiles/{steamId}/?xml=1";
                response = await _httpClient.GetStringAsync(url, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"直连 steamcommunity.com 获取头像失败: {ex.Message}。将尝试国内镜像反代重试...");
                // 2. 备用降级方案，利用国内可靠的反代镜像重试
                try
                {
                    var fallbackUrl = $"https://shared.steamcommunity.li-sub.cn/profiles/{steamId}/?xml=1";
                    response = await _httpClient.GetStringAsync(fallbackUrl, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception fallbackEx)
                {
                    AppLogger.Warn($"镜像重试失败: {fallbackEx.Message}。如果未开启代理，头像可能无法显示。");
                }
            }

            if (string.IsNullOrEmpty(response))
                return null;

            try
            {
                var match = Regex.Match(response, @"<avatarFull><!\[CDATA\[(.*?)\]\]></avatarFull>");
                if (!match.Success)
                    match = Regex.Match(response, @"<avatarMedium><!\[CDATA\[(.*?)\]\]></avatarMedium>");
                if (!match.Success)
                    match = Regex.Match(response, @"<avatarIcon><!\[CDATA\[(.*?)\]\]></avatarIcon>");

                if (match.Success)
                {
                    var avatarUrl = match.Groups[1].Value;
                    byte[]? avatarBytes = null;

                    // 1. 尝试下载头像图片本身
                    try
                    {
                        avatarBytes = await _httpClient.GetByteArrayAsync(avatarUrl, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"直连下载头像文件失败: {ex.Message}。将尝试通过镜像替换下载...");
                        try
                        {
                            var fallbackAvatarUrl = avatarUrl.Replace("avatars.steamstatic.com", "shared.steamcommunity.li-sub.cn/avatars");
                            avatarBytes = await _httpClient.GetByteArrayAsync(fallbackAvatarUrl, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception fallbackEx)
                        {
                            AppLogger.Error($"镜像下载头像文件亦失败: {fallbackEx.Message}");
                        }
                    }

                    if (avatarBytes != null)
                    {
                        var avatarCachePath = _steamService.GetAvatarCachePath();
                        if (!Directory.Exists(avatarCachePath))
                            Directory.CreateDirectory(avatarCachePath);

                        var avatarFile = Path.Combine(avatarCachePath, $"{steamId}.png");
                        await File.WriteAllBytesAsync(avatarFile, avatarBytes, cancellationToken);
                        return avatarFile;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"解析头像数据时发生异常: {ex.Message}", ex);
            }

            return null;
        }

        public static BitmapImage? LoadImage(string? path, int decodePixelWidth = 0)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                if (decodePixelWidth > 0)
                    image.DecodePixelWidth = decodePixelWidth;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"LoadImage failed for path: {path}", ex);
                return null;
            }
        }

        private static string GetValue(Dictionary<string, object> dict, string key)
        {
            return dict.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                BeforeAccountSwitchAsync = null;
                ConfirmForceCloseAfterTimeoutAsync = null;
                _avatarDownloadCts?.Cancel();
                _avatarDownloadCts?.Dispose();
                _avatarDownloadCts = null;
                _httpClient?.Dispose();
                _disposed = true;
            }
        }
    }
}
