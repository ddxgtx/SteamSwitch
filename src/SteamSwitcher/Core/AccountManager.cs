using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
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
        private bool _disposed;

        public List<SteamAccount> Accounts { get; private set; } = new();
        public SteamAccount? CurrentAccount { get; private set; }

        public event EventHandler? AccountsChanged;

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

        public async Task LoadAccountsAsync()
        {
            Accounts.Clear();
            CurrentAccount = null;

            var loginUsersPath = _steamService.GetLoginUsersPath();
            if (!File.Exists(loginUsersPath))
                return;

            try
            {
                var data = _vdfParser.Parse(loginUsersPath);
                if (data.TryGetValue("users", out var usersObj) &&
                    usersObj is Dictionary<string, object> users)
                {
                    var autoLoginUser = _registryHelper.GetAutoLoginUser();

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

                            if (account.AccountName == autoLoginUser || account.MostRecent)
                            {
                                CurrentAccount = account;
                            }

                            Accounts.Add(account);
                        }
                    }
                }

                Accounts = Accounts.OrderByDescending(a => a.MostRecent).ThenByDescending(a => a.Timestamp).ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading accounts: {ex.Message}");
            }

            AccountsChanged?.Invoke(this, EventArgs.Empty);

            // Download missing avatars in background
            _ = Task.Run(async () =>
            {
                foreach (var account in Accounts.Where(a => string.IsNullOrEmpty(a.AvatarPath)).ToList())
                {
                    try
                    {
                        var avatarPath = await DownloadAvatarAsync(account.SteamId);
                        if (!string.IsNullOrEmpty(avatarPath))
                        {
                            account.AvatarPath = avatarPath;
                            AppLogger.Info($"Downloaded avatar for {account.AccountName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"Failed to download avatar for {account.AccountName}", ex);
                    }
                }
            });
        }

        public async Task<bool> SwitchAccountAsync(SteamAccount account, bool silentClose = false)
        {
            if (_steamService.IsSteamRunning())
            {
                var closed = await _steamService.CloseSteamAsync(silent: silentClose);
                if (!closed)
                    return false;
            }

            try
            {
                _registryHelper.SetAutoLoginUser(account.AccountName);
                await UpdateLoginUsersVdfAsync(account.SteamId);

                account.MostRecent = true;
                foreach (var acc in Accounts)
                {
                    if (acc != account)
                        acc.MostRecent = false;
                }
                CurrentAccount = account;

                AccountsChanged?.Invoke(this, EventArgs.Empty);
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

        public bool LaunchGame(int appId)
        {
            return _steamService.StartGame(appId);
        }

        private async Task UpdateLoginUsersVdfAsync(string activeSteamId)
        {
            var loginUsersPath = _steamService.GetLoginUsersPath();
            if (!File.Exists(loginUsersPath))
                return;

            try
            {
                var data = _vdfParser.Parse(loginUsersPath);
                if (data.TryGetValue("users", out var usersObj) &&
                    usersObj is Dictionary<string, object> users)
                {
                    foreach (var userEntry in users)
                    {
                        if (userEntry.Value is Dictionary<string, object> userData)
                        {
                            if (userEntry.Key == activeSteamId)
                            {
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
                }

                var content = _vdfParser.Serialize(data);
                await File.WriteAllTextAsync(loginUsersPath, content);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating VDF: {ex.Message}");
            }
        }

        private string? GetAvatarPath(string steamId)
        {
            var avatarCachePath = _steamService.GetAvatarCachePath();
            if (string.IsNullOrEmpty(avatarCachePath))
                return null;

            var avatarFile = Path.Combine(avatarCachePath, $"{steamId}.png");
            return File.Exists(avatarFile) ? avatarFile : null;
        }

        private async Task<string?> DownloadAvatarAsync(string steamId)
        {
            try
            {
                var url = $"https://steamcommunity.com/profiles/{steamId}/?xml=1";
                var response = await _httpClient.GetStringAsync(url);

                var match = Regex.Match(response, @"<avatarFull><!\[CDATA\[(.*?)\]\]></avatarFull>");
                if (!match.Success)
                    match = Regex.Match(response, @"<avatarMedium><!\[CDATA\[(.*?)\]\]></avatarMedium>");
                if (!match.Success)
                    match = Regex.Match(response, @"<avatarIcon><!\[CDATA\[(.*?)\]\]></avatarIcon>");

                if (match.Success)
                {
                    var avatarUrl = match.Groups[1].Value;
                    var avatarBytes = await _httpClient.GetByteArrayAsync(avatarUrl);

                    var avatarCachePath = _steamService.GetAvatarCachePath();
                    if (!Directory.Exists(avatarCachePath))
                        Directory.CreateDirectory(avatarCachePath);

                    var avatarFile = Path.Combine(avatarCachePath, $"{steamId}.png");
                    await File.WriteAllBytesAsync(avatarFile, avatarBytes);
                    return avatarFile;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error downloading avatar: {ex.Message}");
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
                _httpClient?.Dispose();
                _disposed = true;
            }
        }
    }
}
