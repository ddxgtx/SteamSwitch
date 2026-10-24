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
        private static readonly bool UseAvatarMirror = false;

        private readonly SteamService _steamService;
        private readonly VdfParser _vdfParser;
        private readonly RegistryHelper _registryHelper;
        private readonly HttpClient _httpClient;
        private readonly AccountLibraryService _accountLibrary;
        private readonly Func<string?> _autoLoginUserReader;
        private readonly bool _avatarDownloadsEnabled;
        private readonly SemaphoreSlim _switchGate = new(1, 1);
        private readonly CancellationTokenSource _operationLifetime = new();
        private readonly CancellationToken _operationToken;
        private Task? _gateDisposalTask;
        private CancellationTokenSource? _avatarDownloadCts;
        private int _disposeStarted;
        private volatile bool _disposed;

        public List<SteamAccount> Accounts { get; private set; } = new();
        public SteamAccount? CurrentAccount { get; private set; }
        public string? AccountSourceWarning { get; private set; }

        /// <summary>最近一次从 loginusers.vdf 收录到本地账号库的新账号数量（用于保存反馈）。</summary>
        public int LastCollectedNewAccounts { get; private set; }
        public string AccountLibraryPath => _accountLibrary.LibraryPath;
        public IReadOnlyList<string> IgnoredAccountIds => _accountLibrary.IgnoredAccountIds;

        public event EventHandler? AccountsChanged;
        public event EventHandler? CurrentAccountChanged;
        public Func<SteamAccount?, Task>? BeforeAccountSwitchAsync { get; set; }
        public Func<Task<bool>>? ConfirmForceCloseAfterTimeoutAsync { get; set; }

        public AccountManager() : this(new SteamService(), new AccountLibraryService())
        {
        }

        internal AccountManager(SteamService steamService, AccountLibraryService accountLibrary,
            Func<string?>? autoLoginUserReader = null, bool downloadAvatars = true)
        {
            _steamService = steamService;
            _vdfParser = new VdfParser();
            _registryHelper = new RegistryHelper();
            _accountLibrary = accountLibrary;
            _autoLoginUserReader = autoLoginUserReader ?? _registryHelper.GetAutoLoginUser;
            _avatarDownloadsEnabled = downloadAvatars;
            _operationToken = _operationLifetime.Token;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public SteamService GetSteamService() => _steamService;

        /// <summary>
        /// Uses Steam's current login state to refresh the active account without
        /// rebuilding the account list or starting avatar downloads.
        /// </summary>
        public bool RefreshCurrentAccountFromSteamSession()
        {
            if (_disposed)
                return false;

            var lockTaken = false;
            try
            {
                try
                {
                    // Non-blocking: this is called from the UI status timer.
                    // Blocking here would freeze the UI for the entire account switch.
                    if (!_switchGate.Wait(0))
                        return false;
                    lockTaken = true;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                return RefreshCurrentAccountCore();
            }
            finally
            {
                if (lockTaken)
                {
                    try { _switchGate.Release(); } catch (ObjectDisposedException) { }
                }
            }
        }

        private bool RefreshCurrentAccountCore()
        {
            if (Accounts.Count == 0 || AccountSourceWarning != null)
                return false;

            var autoLoginUser = _autoLoginUserReader();
            string? mostRecentSteamId = null;
            try
            {
                var loginUsersPath = _steamService.GetLoginUsersPath();
                if (!string.IsNullOrEmpty(loginUsersPath) && File.Exists(loginUsersPath))
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
            var steamFound = _steamService.DetectSteamPath();
            await LoadAccountsAsync();
            return steamFound || Accounts.Count > 0;
        }

        public async Task LoadAccountsAsync()
        {
            if (_disposed)
                return;

            try
            {
                await EnterOperationAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (OperationCanceledException) when (_operationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                LoadAccountsCore();
            }
            finally
            {
                try { _switchGate.Release(); } catch (ObjectDisposedException) { }
            }
        }

        private void LoadAccountsCore()
        {
            var avatarCts = CancellationTokenSource.CreateLinkedTokenSource(_operationToken);
            var avatarToken = avatarCts.Token;
            var previousAvatars = Interlocked.Exchange(ref _avatarDownloadCts, avatarCts);
            try { previousAvatars?.Cancel(); } catch (ObjectDisposedException) { }
            previousAvatars?.Dispose();

            var vdfAccounts = new List<SteamAccount>();
            var loginUsersPath = _steamService.GetLoginUsersPath();
            var sourceAvailable = false;
            AccountSourceWarning = null;
            try
            {
                var data = ReadValidatedLoginUsers(loginUsersPath);
                var users = (Dictionary<string, object>)data["users"];
                foreach (var userEntry in users)
                {
                    if (userEntry.Value is Dictionary<string, object> userData &&
                        !_accountLibrary.IsIgnored(userEntry.Key))
                    {
                        var account = new SteamAccount
                        {
                            SteamId = userEntry.Key,
                            AccountName = GetValue(userData, "AccountName"),
                            PersonaName = GetValue(userData, "PersonaName"),
                            RememberPassword = GetValue(userData, "RememberPassword") == "1",
                            MostRecent = GetValue(userData, "MostRecent") == "1",
                            Timestamp = long.TryParse(GetValue(userData, "Timestamp"), out var ts) &&
                                ts >= 0 && ts <= 253402300799 ? ts : 0
                        };
                        if (string.IsNullOrWhiteSpace(account.PersonaName)) account.PersonaName = account.AccountName;
                        account.AvatarPath = GetAvatarPath(account.SteamId);
                        vdfAccounts.Add(account);
                    }
                }
                sourceAvailable = true;
            }
            catch (Exception ex)
            {
                AccountSourceWarning = "Steam 账号文件暂不可用，正在显示本地账号档案。请启动 Steam 完成登录后刷新；原文件不会被覆盖。";
                AppLogger.Warn($"Unable to read loginusers.vdf; displaying local account metadata: {ex.Message}");
            }

            var libraryChanged = _accountLibrary.MergeFromVdf(vdfAccounts, out var addedAccounts);
            if (libraryChanged) _ = SaveLibraryAsync();
            LastCollectedNewAccounts = addedAccounts;
            Accounts = vdfAccounts.OrderByDescending(a => a.MostRecent).ThenByDescending(a => a.Timestamp).ToList();
            Accounts.AddRange(_accountLibrary.BuildArchivedAccounts(vdfAccounts));
            var autoLoginUser = sourceAvailable ? _autoLoginUserReader() : null;
            CurrentAccount = sourceAvailable ? Accounts.FirstOrDefault(a =>
                    !a.IsArchived && !string.IsNullOrWhiteSpace(autoLoginUser) &&
                    string.Equals(a.AccountName, autoLoginUser, StringComparison.OrdinalIgnoreCase))
                ?? Accounts.FirstOrDefault(a => !a.IsArchived && a.MostRecent) : null;

            RaiseSafely(AccountsChanged, nameof(AccountsChanged));
            RaiseSafely(CurrentAccountChanged, nameof(CurrentAccountChanged));

            var accountSnapshot = Accounts.ToList();
            if (_disposed)
            {
                if (ReferenceEquals(Interlocked.CompareExchange(ref _avatarDownloadCts, null, avatarCts), avatarCts))
                    avatarCts.Dispose();
            }
            else if (_avatarDownloadsEnabled)
                _ = DownloadMissingAvatarsAsync(accountSnapshot, avatarToken);
        }

        private Dictionary<string, object> ReadValidatedLoginUsers(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("loginusers.vdf 尚未生成，请先启动 Steam 完成登录。", path);
            var data = _vdfParser.Parse(path);
            if (!data.TryGetValue("users", out var usersObj) || usersObj is not Dictionary<string, object> users)
                throw new InvalidDataException("loginusers.vdf 缺少 users 节。");
            foreach (var pair in users)
            {
                if (!AccountLibraryService.IsValidSteamId(pair.Key) ||
                    pair.Value is not Dictionary<string, object> user ||
                    string.IsNullOrWhiteSpace(GetValue(user, "AccountName")) ||
                    GetValue(user, "AccountName").Length > 128 || GetValue(user, "AccountName").Any(char.IsControl))
                    throw new InvalidDataException("loginusers.vdf 包含无效账号条目，已停止修改。");
            }
            return data;
        }

        public IReadOnlyList<SavedAccountEntry> GetAccountArchiveEntries() => _accountLibrary.GetArchiveEntries();

        public async Task ReloadAccountLibraryAsync(bool reloadAccounts = true)
        {
            await EnterOperationAsync();
            try
            {
                // 在后台线程执行同步文件读取，避免调用线程（可能是 UI）被账号库 IO 阻塞。
                await Task.Run(() => _accountLibrary.Reload()).ConfigureAwait(false);
                if (reloadAccounts) LoadAccountsCore();
            }
            finally { try { _switchGate.Release(); } catch (ObjectDisposedException) { } }
        }

        public async Task<bool> ForgetAccountAsync(string steamId)
        {
            await EnterOperationAsync();
            try
            {
                var changed = await _accountLibrary.ForgetAsync(steamId);
                LoadAccountsCore();
                return changed;
            }
            finally { try { _switchGate.Release(); } catch (ObjectDisposedException) { } }
        }

        public async Task<bool> RestoreAccountCollectionAsync(string steamId)
        {
            await EnterOperationAsync();
            try
            {
                var changed = await _accountLibrary.RestoreCollectionAsync(steamId);
                LoadAccountsCore();
                return changed;
            }
            finally { try { _switchGate.Release(); } catch (ObjectDisposedException) { } }
        }

        public async Task ExportAccountArchiveAsync(string path)
        {
            await EnterOperationAsync();
            try { await _accountLibrary.ExportArchiveAsync(path); }
            finally { try { _switchGate.Release(); } catch (ObjectDisposedException) { } }
        }

        public async Task<int> ImportAccountArchiveAsync(string path)
        {
            await EnterOperationAsync();
            try { var count = await _accountLibrary.ImportArchiveAsync(path); LoadAccountsCore(); return count; }
            finally { try { _switchGate.Release(); } catch (ObjectDisposedException) { } }
        }

        public async Task<int> ImportAccountEntriesAsync(IEnumerable<SavedAccountEntry> entries)
        {
            await EnterOperationAsync();
            try { var count = await _accountLibrary.ImportEntriesAsync(entries); LoadAccountsCore(); return count; }
            finally { try { _switchGate.Release(); } catch (ObjectDisposedException) { } }
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
            if (_disposed) return false;
            try { await EnterOperationAsync(); }
            catch (ObjectDisposedException) { return false; }
            catch (OperationCanceledException) when (_operationToken.IsCancellationRequested) { return false; }
            try
            {
                return await SwitchAccountCoreAsync(account, forceClose);
            }
            finally
            {
                try { _switchGate.Release(); } catch (ObjectDisposedException) { }
            }
        }

        private async Task<bool> SwitchAccountCoreAsync(SteamAccount account, bool forceClose)
        {
            if (account == null || !AccountLibraryService.IsValidSteamId(account.SteamId) ||
                string.IsNullOrWhiteSpace(account.AccountName) || account.AccountName.Length > 128 ||
                account.AccountName.Any(char.IsControl) || _accountLibrary.IsIgnored(account.SteamId))
                return false;
            // Refuse a broken/missing source before shutting Steam down. The
            // library contains metadata, never enough information to recreate a
            // client's login state safely from a corrupt file.
            try { ReadValidatedLoginUsers(_steamService.GetLoginUsersPath()); }
            catch (Exception ex)
            {
                AccountSourceWarning = ex.Message;
                AppLogger.Warn($"Account switch stopped before closing Steam: {ex.Message}");
                return false;
            }
            if (_steamService.IsSteamRunning())
            {
                var closed = await _steamService.CloseSteamAsync(forceClose: forceClose);
                if (!closed && !forceClose && ConfirmForceCloseAfterTimeoutAsync != null &&
                    await ConfirmForceCloseAfterTimeoutAsync())
                {
                    closed = await _steamService.CloseSteamAsync(forceClose: true);
                }

                if (closed && _steamService.IsSteamRunning())
                {
                    AppLogger.Warn("Steam close reported success but processes are still present; treating close as failed.");
                    closed = false;
                }

                if (!closed)
                {
                    // 区分"Steam 还在跑但关不掉"与"压根识别不出可关闭的进程"：
                    // 后者用户无从下手，必须给出明确指引，而不是笼统的关闭失败。
                    AccountSourceWarning = _steamService.HasVerifiableSteamProcesses()
                        ? "Steam 仍在运行且未能关闭，账号未切换。请手动退出 Steam 后重试。"
                        : "无法确认 Steam 进程的安装路径，因此未能关闭 Steam，账号未切换。请以管理员身份运行本程序，或手动退出 Steam 后重试。";
                    AppLogger.Error("SwitchAccount aborted: Steam could not be closed; loginusers.vdf and registry were left unchanged.");
                    return false;
                }
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
                if (!await UpdateLoginUsersVdfAsync(account))
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
                // 必须按账号身份比较：重试路径传进来的可能是刷新前的旧实例，
                // 引用相等会漏掉所有账号，导致列表里出现两个「当前账号」。
                foreach (var acc in Accounts)
                {
                    if (!AreSameAccount(acc, account))
                        acc.MostRecent = false;
                }

                // A restored archived account is now a real loginusers.vdf
                // account, so drop the archived marker and refresh the library.
                if (account.IsArchived)
                {
                    account.IsArchived = false;
                    _accountLibrary.Upsert(account);
                    _ = SaveLibraryAsync();
                }

                CurrentAccount = account;
                AccountSourceWarning = null;

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

        private async Task SaveLibraryAsync()
        {
            try
            {
                await _accountLibrary.SaveAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The library is a convenience cache; never fail a load for it.
                AppLogger.Warn($"Account library could not be saved: {ex.Message}");
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

        internal async Task<bool> UpdateLoginUsersVdfAsync(SteamAccount account)
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
                if (!AccountLibraryService.IsValidSteamId(account.SteamId) ||
                    string.IsNullOrWhiteSpace(account.AccountName) || account.AccountName.Length > 128 ||
                    account.AccountName.Any(char.IsControl) || _accountLibrary.IsIgnored(account.SteamId))
                    return false;
                var originalBytes = File.ReadAllBytes(loginUsersPath);
                var data = ReadValidatedLoginUsers(loginUsersPath);
                var users = (Dictionary<string, object>)data["users"];
                var expectedIds = new HashSet<string>(users.Keys, StringComparer.OrdinalIgnoreCase) { account.SteamId };

                var foundActiveAccount = false;
                foreach (var userEntry in users)
                {
                    if (userEntry.Value is Dictionary<string, object> userData)
                    {
                        if (string.Equals(userEntry.Key, account.SteamId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.Equals(GetValue(userData, "AccountName"), account.AccountName, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("目标账号资料已变化，请刷新账号列表后重试。");
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

                // Steam prunes remembered accounts from loginusers.vdf once too
                // many are stored. The local account library keeps those
                // accounts, so restore the entry instead of aborting the
                // switch; Steam will prompt for the password if it no longer
                // remembers it, with the account name already filled in.
                if (!foundActiveAccount)
                {
                    AccountLibraryService.EnsureUserEntry(users, account);
                    AppLogger.Info(
                        $"Restored archived account '{account.AccountName}' into loginusers.vdf " +
                        "from the local account library; Steam may ask for its password once.");
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

                var validation = ReadValidatedLoginUsers(tempPath);
                var validatedUsers = (Dictionary<string, object>)validation["users"];
                if (!expectedIds.SetEquals(validatedUsers.Keys) ||
                    validatedUsers[account.SteamId] is not Dictionary<string, object> validatedTarget ||
                    GetValue(validatedTarget, "MostRecent") != "1" ||
                    validatedUsers.Values.OfType<Dictionary<string, object>>()
                        .Count(user => GetValue(user, "MostRecent") == "1") != 1 ||
                    !string.Equals(content, _vdfParser.Serialize(validation), StringComparison.Ordinal))
                    throw new InvalidDataException("生成的 loginusers.vdf 未通过重新解析校验。");
                if (!originalBytes.AsSpan().SequenceEqual(File.ReadAllBytes(loginUsersPath)))
                    throw new IOException("Steam 账号文件在切换过程中已变化，请刷新后重试。");

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
            var appAvatarFile = Path.Combine(GetAppAvatarCacheDirectory(), $"{steamId}.png");
            if (File.Exists(appAvatarFile))
                return appAvatarFile;

            var legacyCachePath = _steamService.GetAvatarCachePath();
            if (!string.IsNullOrEmpty(legacyCachePath))
            {
                var legacyAvatarFile = Path.Combine(legacyCachePath, $"{steamId}.png");
                if (File.Exists(legacyAvatarFile))
                    return legacyAvatarFile;
            }

            return null;
        }

        private static string GetAppAvatarCacheDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamSwitch",
                "avatarcache");
        }

        private async Task<string?> DownloadAvatarAsync(string steamId, CancellationToken cancellationToken)
        {
            string? response = null;

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
                if (!UseAvatarMirror)
                {
                    AppLogger.Warn($"Failed to fetch avatar profile from steamcommunity.com: {ex.Message}. Avatar mirror is disabled.");
                }
                else
                {
                    AppLogger.Warn($"Failed to fetch avatar profile from steamcommunity.com: {ex.Message}. Trying avatar mirror...");
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
                        AppLogger.Warn($"Avatar mirror retry failed: {fallbackEx.Message}.");
                    }
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
                        if (!UseAvatarMirror)
                        {
                            AppLogger.Warn($"Failed to download avatar file: {ex.Message}. Avatar mirror is disabled.");
                        }
                        else
                        {
                            AppLogger.Warn($"Failed to download avatar file: {ex.Message}. Trying mirror download...");
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
                                AppLogger.Error($"Avatar mirror download failed: {fallbackEx.Message}");
                            }
                        }
                    }

                    if (avatarBytes != null)
                    {
                        var avatarCachePath = GetAppAvatarCacheDirectory();
                        Directory.CreateDirectory(avatarCachePath);

                        var avatarFile = Path.Combine(avatarCachePath, $"{steamId}.png");
                        await File.WriteAllBytesAsync(avatarFile, avatarBytes, cancellationToken);
                        return avatarFile;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to parse avatar data: {ex.Message}", ex);
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

        private async Task EnterOperationAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                await _switchGate.WaitAsync(_operationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // 退出时 CTS 可能被先释放，此时并发进入的操作应安静放弃，
                // 而不是把 ODE 抛到 async void 事件处理器里弹出崩溃框。
                throw;
            }

            if (_disposed)
            {
                try { _switchGate.Release(); } catch (ObjectDisposedException) { }
                throw new ObjectDisposedException(nameof(AccountManager));
            }
        }

        internal Task DisposalCompletion => _gateDisposalTask ?? Task.CompletedTask;

        private async Task DisposeOperationGateWhenIdleAsync()
        {
            // All queued callers were cancelled before this wait. An operation
            // already writing a transaction can finish and release normally.
            await _switchGate.WaitAsync().ConfigureAwait(false);
            _switchGate.Dispose();
            // 在 gate 完全空闲后再释放操作生命周期 CTS：此前是 Dispose() 里同步立即释放，
            // 时序脆弱，可能让仍在访问 _operationToken 的操作观察到 ObjectDisposedException。
            try { _operationLifetime.Dispose(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            _disposed = true;
            _operationLifetime.Cancel();
            BeforeAccountSwitchAsync = null;
            ConfirmForceCloseAfterTimeoutAsync = null;
            var avatars = Interlocked.Exchange(ref _avatarDownloadCts, null);
            try { avatars?.Cancel(); } catch { }
            avatars?.Dispose();
            _httpClient?.Dispose();
            _gateDisposalTask = DisposeOperationGateWhenIdleAsync();
        }
    }
}
