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
        private CancellationTokenSource? _avatarDownloadCts;
        private bool _disposed;

        public List<SteamAccount> Accounts { get; private set; } = new();
        public SteamAccount? CurrentAccount { get; private set; }

        public event EventHandler? AccountsChanged;
        public event EventHandler? CurrentAccountChanged;

        public AccountManager()
        {
            _steamService = new SteamService();
            _vdfParser = new VdfParser();
            _registryHelper = new RegistryHelper();
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public SteamService GetSteamService() => _steamService;

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
                AccountsChanged?.Invoke(this, EventArgs.Empty);
                CurrentAccountChanged?.Invoke(this, EventArgs.Empty);
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

            AccountsChanged?.Invoke(this, EventArgs.Empty);
            CurrentAccountChanged?.Invoke(this, EventArgs.Empty);

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
                    AccountsChanged?.Invoke(this, EventArgs.Empty);
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
            if (_steamService.IsSteamRunning())
            {
                var closed = await _steamService.CloseSteamAsync(forceClose: forceClose);
                if (!closed)
                    return false;
            }

            try
            {
                if (!await UpdateLoginUsersVdfAsync(account.SteamId))
                {
                    AppLogger.Error($"SwitchAccountAsync aborted: failed to update loginusers.vdf for {account.AccountName}.");
                    return false;
                }

                _registryHelper.SetAutoLoginUser(account.AccountName);

                account.MostRecent = true;
                account.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var acc in Accounts)
                {
                    if (acc != account)
                        acc.MostRecent = false;
                }
                CurrentAccount = account;

                AccountsChanged?.Invoke(this, EventArgs.Empty);
                CurrentAccountChanged?.Invoke(this, EventArgs.Empty);
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

        public bool LaunchGame(int appId, bool silent = false)
        {
            return _steamService.StartGame(appId, silent);
        }

        private async Task<bool> UpdateLoginUsersVdfAsync(string activeSteamId)
        {
            var loginUsersPath = _steamService.GetLoginUsersPath();
            if (!File.Exists(loginUsersPath))
            {
                AppLogger.Error($"loginusers.vdf was not found: {loginUsersPath}");
                return false;
            }

            var tempPath = loginUsersPath + ".steamswitch.tmp";

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
                await File.WriteAllTextAsync(tempPath, content);
                File.Move(tempPath, loginUsersPath, overwrite: true);
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
                _avatarDownloadCts?.Cancel();
                _avatarDownloadCts?.Dispose();
                _avatarDownloadCts = null;
                _httpClient?.Dispose();
                _disposed = true;
            }
        }
    }
}
