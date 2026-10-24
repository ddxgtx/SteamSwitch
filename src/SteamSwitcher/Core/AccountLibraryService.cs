using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    /// <summary>
    /// Saved account entry persisted in the local account library
    /// (%APPDATA%\SteamSwitch\accounts.json). The library keeps accounts that
    /// Steam itself may have pruned from loginusers.vdf so the app can always
    /// restore them when switching.
    /// </summary>
    public class SavedAccountEntry
    {
        public string SteamId { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string PersonaName { get; set; } = string.Empty;
        public long Timestamp { get; set; }
        public long LastSeenUnix { get; set; }
        public string? AvatarPath { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsIgnored { get; set; }
    }

    /// <summary>Portable metadata only. No credentials or machine-local paths.</summary>
    public sealed class AccountMetadataEntry
    {
        public string SteamId { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string PersonaName { get; set; } = string.Empty;
        public long Timestamp { get; set; }
        public long LastSeenUnix { get; set; }
    }

    public sealed class AccountMetadataArchive
    {
        public int FormatVersion { get; set; } = 1;
        public List<AccountMetadataEntry> Accounts { get; set; } = new();
    }

    /// <summary>
    /// Local account library. Steam only keeps a limited number of remembered
    /// accounts in loginusers.vdf and prunes older entries. Every account the
    /// app sees in loginusers.vdf is merged into this library so it survives
    /// Steam's pruning. Accounts that exist only in the library are surfaced
    /// as "archived" accounts and can be restored into loginusers.vdf when
    /// switching to them (Watt Toolkit style multi-account management).
    /// </summary>
    public class AccountLibraryService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _libraryPath;
        private readonly Dictionary<string, SavedAccountEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);
        // 写操作发生在 AccountManager 的闸锁之下（可能是后台线程），
        // 而 UI 线程会直接枚举这些成员，没有锁会抛「集合已修改」。
        private readonly object _sync = new();

        public string LibraryPath => _libraryPath;

        public AccountLibraryService(string? libraryPath = null)
        {
            _libraryPath = libraryPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SteamSwitch",
                "accounts.json");
            Load();
        }

        // 注意：返回的是库里的活对象（调用方会直接改 LastSeenUnix/AvatarPath），
        // 只把枚举本身放进锁里，不做克隆。
        public IReadOnlyList<SavedAccountEntry> Entries
        {
            get { lock (_sync) { return _entries.Values.Where(entry => !entry.IsIgnored).ToList(); } }
        }

        public IReadOnlyList<string> IgnoredAccountIds
        {
            get
            {
                lock (_sync)
                {
                    return _entries.Values
                        .Where(entry => entry.IsIgnored)
                        .Select(entry => entry.SteamId)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToList();
                }
            }
        }

        public bool IsIgnored(string steamId)
        {
            lock (_sync)
            {
                return IsIgnoredCore(steamId);
            }
        }

        // 调用方必须已持有 _sync。
        private bool IsIgnoredCore(string steamId) =>
            _entries.TryGetValue(steamId, out var entry) && entry.IsIgnored;

        public IReadOnlyList<SavedAccountEntry> GetArchiveEntries()
        {
            lock (_sync)
            {
                return _entries.Values.Select(entry => new SavedAccountEntry
                {
                    SteamId = entry.SteamId, IsIgnored = entry.IsIgnored,
                    AccountName = entry.IsIgnored ? string.Empty : entry.AccountName,
                    PersonaName = entry.IsIgnored ? string.Empty : entry.PersonaName,
                    Timestamp = entry.IsIgnored ? 0 : entry.Timestamp,
                    LastSeenUnix = entry.IsIgnored ? 0 : entry.LastSeenUnix
                }).ToList();
            }
        }

        private static SavedAccountEntry Clone(SavedAccountEntry entry) => new()
        {
            SteamId = entry.SteamId, AccountName = entry.AccountName,
            PersonaName = entry.PersonaName, Timestamp = entry.Timestamp,
            LastSeenUnix = entry.LastSeenUnix, AvatarPath = entry.AvatarPath,
            IsIgnored = entry.IsIgnored
        };

        public void Reload()
        {
            lock (_sync)
            {
                _entries.Clear();
                Load();
            }
        }

        private void Load()
        {
            try
            {
                var loaded = AtomicJsonFile.Read<List<SavedAccountEntry>>(_libraryPath, JsonOptions);
                if (loaded == null)
                    return;
                _entries.Clear();
                foreach (var entry in loaded)
                {
                    if (entry == null || !IsValidSteamId(entry.SteamId))
                        continue;
                    if (entry.IsIgnored)
                        // 遗忘是有意的擦除：只保留 SteamId，账号名/头像一律不落库。
                        _entries[entry.SteamId] = new SavedAccountEntry { SteamId = entry.SteamId, IsIgnored = true };
                    else
                    {
                        entry.AccountName ??= string.Empty;
                        entry.PersonaName ??= string.Empty;
                        if (string.IsNullOrWhiteSpace(entry.AccountName) || entry.AccountName.Length > 128 ||
                            entry.AccountName.Any(char.IsControl)) continue;
                        entry.Timestamp = entry.Timestamp is >= 0 and <= 253402300799 ? entry.Timestamp : 0;
                        _entries[entry.SteamId] = entry;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Account library could not be read; starting with an empty library: {ex.Message}");
            }
        }

        /// <summary>
        /// Upserts every account currently present in loginusers.vdf into the
        /// library. Returns true when the library content changed, and reports
        /// how many brand-new accounts were added (for save feedback).
        /// </summary>
        public bool MergeFromVdf(IReadOnlyCollection<SteamAccount> vdfAccounts, out int added)
        {
            added = 0;
            if (vdfAccounts.Count == 0)
                return false;

            lock (_sync)
            {
                return MergeFromVdfCore(vdfAccounts, out added);
            }
        }

        private bool MergeFromVdfCore(IReadOnlyCollection<SteamAccount> vdfAccounts, out int added)
        {
            var changed = false;
            added = 0;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var account in vdfAccounts)
            {
                if (string.IsNullOrWhiteSpace(account.SteamId) || IsIgnored(account.SteamId))
                    continue;

                if (!_entries.TryGetValue(account.SteamId, out var entry))
                {
                    entry = new SavedAccountEntry { SteamId = account.SteamId };
                    _entries[account.SteamId] = entry;
                    changed = true;
                    added++;
                }

                if (!string.IsNullOrWhiteSpace(account.AccountName) &&
                    !string.Equals(entry.AccountName, account.AccountName, StringComparison.Ordinal))
                {
                    entry.AccountName = account.AccountName;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(account.PersonaName) &&
                    !string.Equals(entry.PersonaName, account.PersonaName, StringComparison.Ordinal))
                {
                    entry.PersonaName = account.PersonaName;
                    changed = true;
                }

                if (account.Timestamp > 0 && entry.Timestamp != account.Timestamp)
                {
                    entry.Timestamp = account.Timestamp;
                    changed = true;
                }

                if (!string.IsNullOrEmpty(account.AvatarPath) &&
                    !string.Equals(entry.AvatarPath, account.AvatarPath, StringComparison.OrdinalIgnoreCase))
                {
                    entry.AvatarPath = account.AvatarPath;
                    changed = true;
                }

                if (entry.LastSeenUnix != now)
                {
                    entry.LastSeenUnix = now;
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Returns library accounts that are no longer present in
        /// loginusers.vdf, wrapped as archived SteamAccount instances.
        /// </summary>
        public List<SteamAccount> BuildArchivedAccounts(IReadOnlyCollection<SteamAccount> vdfAccounts)
        {
            var vdfIds = new HashSet<string>(
                vdfAccounts.Select(a => a.SteamId),
                StringComparer.OrdinalIgnoreCase);

            lock (_sync)
            {
            return _entries.Values
                .Where(entry => !entry.IsIgnored && !vdfIds.Contains(entry.SteamId))
                .OrderByDescending(entry => entry.LastSeenUnix)
                .Select(entry => new SteamAccount
                {
                    SteamId = entry.SteamId,
                    AccountName = entry.AccountName,
                    PersonaName = string.IsNullOrWhiteSpace(entry.PersonaName) ? entry.AccountName : entry.PersonaName,
                    Timestamp = entry.Timestamp,
                    AvatarPath = entry.AvatarPath,
                    RememberPassword = false,
                    MostRecent = false,
                    IsArchived = true
                })
                .ToList();
            }
        }

        /// <summary>
        /// Upserts an account into the library after a successful switch so a
        /// restored archived account becomes a first-class library entry.
        /// </summary>
        public void Upsert(SteamAccount account)
        {
            if (string.IsNullOrWhiteSpace(account.SteamId))
                return;

            // SaveAsync/ForgetAsync 都在 _sync 内取快照或改字典，Upsert 也必须在
            // 同一把锁内写入：否则后台线程写的同时 UI 线程枚举会读到撕裂状态，
            // 甚至让 Dictionary 内部结构损坏。
            lock (_sync)
            {
                if (IsIgnoredCore(account.SteamId))
                    return;

                if (!_entries.TryGetValue(account.SteamId, out var entry))
                {
                    entry = new SavedAccountEntry { SteamId = account.SteamId };
                    _entries[account.SteamId] = entry;
                }

                entry.AccountName = account.AccountName;
                entry.PersonaName = account.PersonaName;
                entry.Timestamp = account.Timestamp;
                entry.AvatarPath = account.AvatarPath;
                entry.LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
        }

        public Task SaveAsync()
        {
            List<SavedAccountEntry> snapshot;
            lock (_sync)
            {
                // Serialize a snapshot: later scans must not mutate an in-flight save.
                snapshot = _entries.Values.Select(Clone).ToList();
            }
            return AtomicJsonFile.WriteAsync(_libraryPath, snapshot, JsonOptions);
        }

        /// <summary>Forget metadata and prevent Steam scans from re-adding it.</summary>
        public async Task<bool> ForgetAsync(string steamId)
        {
            if (!IsValidSteamId(steamId)) throw new ArgumentException("Steam ID 无效。", nameof(steamId));

            SavedAccountEntry? previous;
            lock (_sync)
            {
                if (IsIgnoredCore(steamId)) return false;
                _entries.TryGetValue(steamId, out previous);
                // 只留 SteamId：被遗忘的账号元数据必须消失（含账号名与头像）。
                _entries[steamId] = new SavedAccountEntry { SteamId = steamId, IsIgnored = true };
            }

            try { await SaveAsync().ConfigureAwait(false); }
            catch
            {
                lock (_sync)
                {
                    if (previous == null) _entries.Remove(steamId);
                    else _entries[steamId] = previous;
                }
                throw;
            }
            return true;
        }

        /// <summary>Allow future Steam scans/imports to collect this ID again.</summary>
        public async Task<bool> RestoreCollectionAsync(string steamId)
        {
            SavedAccountEntry previous;
            lock (_sync)
            {
                if (!IsIgnoredCore(steamId)) return false;
                previous = _entries[steamId];
                // 删掉忽略条目：被遗忘的账号已无元数据，留一个空壳会在账号库里
                // 显示成没有名字的账号，不如彻底移除、等 Steam 再次出现时重新收录。
                _entries.Remove(steamId);
            }

            try { await SaveAsync().ConfigureAwait(false); }
            catch
            {
                lock (_sync) { _entries[steamId] = previous; }
                throw;
            }
            return true;
        }

        public Task ExportArchiveAsync(string path)
        {
            var exportPath = Path.GetFullPath(path);
            var libraryPath = Path.GetFullPath(_libraryPath);
            if (string.Equals(exportPath, libraryPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(exportPath, libraryPath + ".bak", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择账号库以外的导出文件，避免覆盖本地档案。");
            var archive = new AccountMetadataArchive
            {
                Accounts = Entries.Select(entry => new AccountMetadataEntry
                {
                    SteamId = entry.SteamId, AccountName = entry.AccountName,
                    PersonaName = entry.PersonaName, Timestamp = entry.Timestamp,
                    LastSeenUnix = entry.LastSeenUnix
                }).ToList()
            };
            return AtomicJsonFile.WriteAsync(exportPath, archive, JsonOptions);
        }

        public async Task<int> ImportArchiveAsync(string path)
        {
            const int maxArchiveBytes = 4 * 1024 * 1024;
            if (new FileInfo(path).Length > maxArchiveBytes)
                throw new InvalidDataException("账号档案超过 4 MB 上限。");
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > maxArchiveBytes)
                throw new InvalidDataException("账号档案超过 4 MB 上限。");
            return await ImportJsonAsync(json).ConfigureAwait(false);
        }

        public Task<int> ImportEntriesAsync(IEnumerable<SavedAccountEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);
            var snapshot = entries.Take(10001).ToList();
            if (snapshot.Count > 10000) throw new InvalidDataException("账号数量超过 10000 个上限。");
            // The same parser/validation also strips unexpected fields.
            return ImportJsonAsync(JsonSerializer.Serialize(snapshot, JsonOptions));
        }

        private async Task<int> ImportJsonAsync(string json)
        {
            using var document = JsonDocument.Parse(json);
            List<AccountMetadataEntry> imported;
            var ignoredIds = new List<string>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Older accounts.json arrays remain importable. Explicit projection
                // discards local avatar paths and all unknown credential fields.
                var legacy = JsonSerializer.Deserialize<List<SavedAccountEntry>>(json, JsonOptions)
                    ?? throw new InvalidDataException("账号档案为空。");
                if (legacy.Any(entry => entry == null)) throw new InvalidDataException("账号档案包含空条目。");
                ignoredIds = legacy.Where(entry => entry.IsIgnored).Select(entry => entry.SteamId).ToList();
                imported = legacy.Where(entry => !entry.IsIgnored)
                    .Select(entry => new AccountMetadataEntry
                    {
                        SteamId = entry.SteamId, AccountName = entry.AccountName,
                        PersonaName = entry.PersonaName, Timestamp = entry.Timestamp,
                        LastSeenUnix = entry.LastSeenUnix
                    }).ToList();
            }
            else
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("账号档案必须是元数据对象或旧版账号数组。");
                var properties = document.RootElement.EnumerateObject().ToArray();
                var versions = properties.Where(property =>
                    string.Equals(property.Name, "FormatVersion", StringComparison.OrdinalIgnoreCase)).ToArray();
                var accountLists = properties.Where(property =>
                    string.Equals(property.Name, "Accounts", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (versions.Length != 1 || accountLists.Length != 1 ||
                    versions[0].Value.ValueKind != JsonValueKind.Number ||
                    !versions[0].Value.TryGetInt32(out var version) || version != 1 ||
                    accountLists[0].Value.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("账号元数据缺少有效的 FormatVersion 或 Accounts 字段，未修改本地档案。");
                var archive = JsonSerializer.Deserialize<AccountMetadataArchive>(json, JsonOptions)
                    ?? throw new InvalidDataException("账号档案为空。");
                if (archive.FormatVersion != 1 || archive.Accounts == null)
                    throw new InvalidDataException("不支持此账号档案版本。");
                imported = archive.Accounts;
            }
            if (imported.Count + ignoredIds.Count > 10000) throw new InvalidDataException("账号数量超过 10000 个上限。");
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ignoredIds)
                if (!IsValidSteamId(id) || !unique.Add(id))
                    throw new InvalidDataException("账号档案包含无效或重复的忽略条目。");
            foreach (var entry in imported)
            {
                if (entry == null || !IsValidSteamId(entry.SteamId) ||
                    string.IsNullOrWhiteSpace(entry.AccountName) || entry.AccountName.Length > 128 ||
                    entry.AccountName.Any(char.IsControl) || entry.PersonaName == null ||
                    entry.PersonaName.Length > 256 || entry.PersonaName.Any(char.IsControl) ||
                    entry.Timestamp < 0 || entry.Timestamp > 253402300799 ||
                    entry.LastSeenUnix < 0 || entry.LastSeenUnix > 253402300799 ||
                    !unique.Add(entry.SteamId))
                    throw new InvalidDataException("账号档案包含无效或重复账号，未导入任何内容。");
            }
            var previous = _entries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var id in ignoredIds)
                _entries[id] = new SavedAccountEntry { SteamId = id, IsIgnored = true };
            foreach (var entry in imported.Where(entry => !IsIgnored(entry.SteamId)))
            {
                _entries[entry.SteamId] = new SavedAccountEntry
                {
                    SteamId = entry.SteamId, AccountName = entry.AccountName,
                    PersonaName = entry.PersonaName, Timestamp = entry.Timestamp,
                    LastSeenUnix = entry.LastSeenUnix
                };
                count++;
            }
            try { await SaveAsync().ConfigureAwait(false); }
            catch
            {
                _entries.Clear();
                foreach (var pair in previous) _entries[pair.Key] = pair.Value;
                throw;
            }
            return count;
        }

        internal static bool IsValidSteamId(string? steamId) =>
            !string.IsNullOrWhiteSpace(steamId) && steamId.All(char.IsAsciiDigit) &&
            ulong.TryParse(steamId, out var id) && id > 0;

        /// <summary>
        /// Ensures loginusers.vdf's parsed "users" section contains an entry
        /// for the account, creating a complete entry when Steam pruned it.
        /// Returns true when a new entry was created.
        /// </summary>
        public static bool EnsureUserEntry(Dictionary<string, object> users, SteamAccount account)
        {
            // Demote every other account first so the helper always produces a
            // consistent "this account is the active one" state, regardless of
            // what the caller already did.
            foreach (var other in users)
            {
                if (string.Equals(other.Key, account.SteamId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (other.Value is Dictionary<string, object> otherData)
                    otherData["MostRecent"] = "0";
            }

            if (users.TryGetValue(account.SteamId, out var existing) &&
                existing is Dictionary<string, object> userData)
            {
                userData["MostRecent"] = "1";
                userData["RememberPassword"] = "1";
                userData["Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                if (!userData.TryGetValue("AccountName", out var name) ||
                    string.IsNullOrWhiteSpace(name?.ToString()))
                {
                    userData["AccountName"] = account.AccountName;
                }
                if (!userData.TryGetValue("PersonaName", out var persona) ||
                    string.IsNullOrWhiteSpace(persona?.ToString()))
                {
                    userData["PersonaName"] = account.PersonaName;
                }
                return false;
            }

            users[account.SteamId] = new Dictionary<string, object>
            {
                ["AccountName"] = account.AccountName,
                ["PersonaName"] = account.PersonaName,
                ["RememberPassword"] = "1",
                ["MostRecent"] = "1",
                ["Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
            };
            return true;
        }
    }

}
