using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    public sealed class GameSaveBackupService
    {
        private const string ManifestName = "steamswitch-backup.json";
        /// <summary>
        /// Default backup folder under Documents. Exposed as a constant so the
        /// UI never hardcodes the same string again.
        /// </summary>
        public const string DefaultBackupFolderName = "SteamSwitch Backups";
        public static string GetDefaultBackupRoot() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            DefaultBackupFolderName);
        private const int MaxBackupFileCount = 100_000;
        private const long MaxExpandedBackupSize = 100L * 1024 * 1024 * 1024;
        private const int MaxManifestBytes = 8 * 1024 * 1024;
        private const int DifferentialBackupsPerFull = 4;
        private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };
        private static readonly EnumerationOptions SaveEnumerationOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        // Serializes Backup/Restore/Prune/Import/Export per EntryId across all
        // service instances (UI and auto-backup each construct their own).
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> IoGates =
            new(StringComparer.Ordinal);
        private readonly string _steamPath;
        private readonly string _backupRoot;
        private readonly int _manualRetention;
        private readonly int _safetyRetention;

        public GameSaveBackupService(
            string steamPath,
            string? backupRoot = null,
            int manualRetention = 10,
            int safetyRetention = 5)
        {
            _steamPath = Path.GetFullPath(steamPath);
            _backupRoot = Path.GetFullPath(backupRoot ?? GetDefaultBackupRoot());
            _manualRetention = Math.Clamp(manualRetention, 1, 100);
            _safetyRetention = Math.Clamp(safetyRetention, 1, 50);
        }

        public string BackupRoot => _backupRoot;

        public int PruneBackupsForEntry(GameSaveEntry entry)
        {
            var gate = GetIoGate(entry.EntryId);
            gate.Wait();
            try
            {
                var before = GetBackups(entry).Count;
                PruneBackups(entry);
                var after = GetBackups(entry).Count;
                return Math.Max(0, before - after);
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<List<GameSaveEntry>> DiscoverAsync(
            IEnumerable<SteamAccount> accounts,
            IEnumerable<SteamLibraryGame> games,
            CancellationToken cancellationToken = default)
        {
            return DiscoverAsync(accounts, games, Array.Empty<CustomGameSaveDefinition>(), cancellationToken);
        }

        public Task<List<GameSaveEntry>> DiscoverAsync(
            IEnumerable<SteamAccount> accounts,
            IEnumerable<SteamLibraryGame> games,
            IEnumerable<CustomGameSaveDefinition> customFolders,
            CancellationToken cancellationToken = default)
        {
            return DiscoverAsync(accounts, games, customFolders, includeSmartDetected: true, cancellationToken);
        }

        public Task<List<GameSaveEntry>> DiscoverAsync(
            IEnumerable<SteamAccount> accounts,
            IEnumerable<SteamLibraryGame> games,
            IEnumerable<CustomGameSaveDefinition> customFolders,
            bool includeSmartDetected,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(
                () => Discover(accounts, games, customFolders, includeSmartDetected, cancellationToken),
                cancellationToken);
        }

        private List<GameSaveEntry> Discover(
            IEnumerable<SteamAccount> accounts,
            IEnumerable<SteamLibraryGame> games,
            IEnumerable<CustomGameSaveDefinition> customFolders,
            bool includeSmartDetected,
            CancellationToken cancellationToken)
        {
            var result = new List<GameSaveEntry>();
            var gameList = games.GroupBy(game => game.AppId).Select(group => group.First()).ToList();
            var gameNames = gameList.ToDictionary(game => game.AppId, game => game.Name);
            var gameIcons = gameList.ToDictionary(game => game.AppId, game => game.IconPath);
            var accountList = accounts.ToList();
            var userdataRoot = Path.Combine(_steamPath, "userdata");
            if (Directory.Exists(userdataRoot))
            {
                foreach (var account in accountList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var userDataId = ConvertSteamIdToUserDataId(account.SteamId);
                    if (string.IsNullOrEmpty(userDataId))
                        continue;

                    var accountRoot = Path.Combine(userdataRoot, userDataId);
                    if (!Directory.Exists(accountRoot))
                        continue;

                    foreach (var appDirectory in SafeEnumerateDirectories(accountRoot))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!int.TryParse(Path.GetFileName(appDirectory), out var appId) || appId <= 0)
                            continue;

                        var files = SafeEnumerateFiles(appDirectory).ToList();
                        if (files.Count == 0)
                            continue;

                        var entry = new GameSaveEntry
                        {
                            EntryId = $"steam:{account.SteamId}:{appId}",
                            SteamId = account.SteamId,
                            UserDataId = userDataId,
                            AccountName = account.AccountName,
                            PersonaName = account.PersonaName,
                            AppId = appId,
                            GameName = gameNames.TryGetValue(appId, out var name) ? name : $"Steam App {appId}",
                            IconPath = gameIcons.TryGetValue(appId, out var iconPath) ? iconPath : null,
                            SavePath = appDirectory,
                            FileCount = files.Count,
                            TotalSize = files.Sum(GetFileSize),
                            LastWriteTime = files.Select(GetLastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max()
                        };
                        ApplyBackupState(entry);
                        result.Add(entry);
                    }
                }
            }

            foreach (var definition in customFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = CreateCustomEntry(definition, accountList);
                if (entry != null)
                    result.Add(entry);
            }

            if (includeSmartDetected)
            {
                var existingPaths = new HashSet<string>(
                    result.Select(entry => Path.GetFullPath(entry.SavePath).TrimEnd(Path.DirectorySeparatorChar)),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var detected in GameSavePathDetector.Detect(gameList, existingPaths, cancellationToken))
                {
                    ApplyBackupState(detected);
                    result.Add(detected);
                }
            }

            DiscoverBackupOnlyEntries(result, accountList, gameList, includeSmartDetected, cancellationToken);

            return result
                .OrderBy(entry => entry.AccountDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.GameName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private void DiscoverBackupOnlyEntries(
            List<GameSaveEntry> entries,
            IReadOnlyCollection<SteamAccount> accounts,
            IReadOnlyCollection<SteamLibraryGame> games,
            bool includeSmartDetected,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(_backupRoot)) return;
            var existing = entries.Select(entry => entry.EntryId).ToHashSet(StringComparer.Ordinal);
            foreach (var ownerDirectory in SafeEnumerateDirectories(_backupRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsRegularDirectory(ownerDirectory)) continue;
                var owner = Path.GetFileName(ownerDirectory);
                var custom = string.Equals(owner, "custom", StringComparison.Ordinal);
                if (custom && !includeSmartDetected) continue;
                if (!custom && (!ulong.TryParse(owner, out var steamId) || steamId.ToString() != owner)) continue;
                foreach (var backupDirectory in SafeEnumerateDirectories(ownerDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsRegularDirectory(backupDirectory)) continue;
                    var key = Path.GetFileName(backupDirectory);
                    if (custom ? !Guid.TryParseExact(key, "N", out _) : !int.TryParse(key, out var app) || app <= 0 || app.ToString() != key)
                        continue;
                    foreach (var archive in SafeEnumerateTopLevelFiles(backupDirectory)
                                 .Where(path => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
                                 .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal))
                    {
                        try
                        {
                            var manifest = ReadManifest(archive);
                            GameSaveEntry entry;
                            if (custom)
                            {
                                if (!manifest.IsCustom || (!manifest.IsDetected && manifest.SteamId != "detected") ||
                                    manifest.EntryId != $"custom:{key}") continue;
                                var game = games.FirstOrDefault(item => item.AppId == manifest.AppId);
                                if (game == null) continue;
                                var source = GameSavePathDetector.ResolveBackupPath(game, key, manifest.SourceSavePath);
                                if (source == null) continue;
                                entry = new GameSaveEntry
                                {
                                    EntryId = $"custom:{key}", CustomId = key, IsCustom = true, IsDetected = true,
                                    SteamId = "detected", UserDataId = key, AccountName = "本机通用存档",
                                    AppId = game.AppId, GameName = game.Name, IconPath = game.IconPath,
                                    SavePath = source, DetectionRule = "备份索引"
                                };
                            }
                            else
                            {
                                var appId = int.Parse(key);
                                var userDataId = ConvertSteamIdToUserDataId(owner);
                                var account = accounts.FirstOrDefault(item => item.SteamId == owner);
                                var game = games.FirstOrDefault(item => item.AppId == appId);
                                entry = new GameSaveEntry
                                {
                                    EntryId = $"steam:{owner}:{appId}", SteamId = owner, UserDataId = userDataId,
                                    AccountName = account?.AccountName ?? (string.IsNullOrWhiteSpace(manifest.AccountName) ? owner : manifest.AccountName),
                                    PersonaName = account?.PersonaName ?? string.Empty, AppId = appId,
                                    GameName = game?.Name ?? (string.IsNullOrWhiteSpace(manifest.GameName) ? $"Steam App {appId}" : manifest.GameName), IconPath = game?.IconPath,
                                    SavePath = Path.Combine(_steamPath, "userdata", userDataId, appId.ToString())
                                };
                                if (manifest.IsCustom || manifest.AppId != appId || manifest.SteamId != owner) continue;
                            }
                            if (existing.Contains(entry.EntryId)) break;
                            ValidateBackupIdentity(entry, manifest);
                            ValidateEntryPath(entry);
                            var files = Directory.Exists(entry.SavePath) ? SafeEnumerateFiles(entry.SavePath).ToList() : new List<string>();
                            // An empty or vanished source must not become an automatic empty
                            // snapshot that can eventually prune all useful recovery versions.
                            entry.IsAvailable = files.Count > 0;
                            entry.FileCount = files.Count;
                            entry.TotalSize = files.Sum(GetFileSize);
                            entry.LastWriteTime = files.Select(GetLastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max();
                            ApplyBackupState(entry);
                            entries.Add(entry);
                            existing.Add(entry.EntryId);
                            break;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
                        {
                            AppLogger.Warn($"Unable to index game save archive {archive}: {ex.Message}");
                        }
                    }
                }
            }
        }

        private GameSaveEntry? CreateCustomEntry(
            CustomGameSaveDefinition definition,
            IReadOnlyCollection<SteamAccount> accounts)
        {
            if (!Guid.TryParseExact(definition.Id, "N", out _) ||
                string.IsNullOrWhiteSpace(definition.GameName) ||
                string.IsNullOrWhiteSpace(definition.FolderPath))
                return null;

            string fullPath;
            try { fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(definition.FolderPath)); }
            catch { return null; }
            var account = accounts.FirstOrDefault(item =>
                string.Equals(item.SteamId, definition.AccountSteamId, StringComparison.Ordinal));
            var isAvailable = Directory.Exists(fullPath) && IsCustomPathAllowed(fullPath);
            var files = isAvailable ? SafeEnumerateFiles(fullPath).ToList() : new List<string>();
            var entry = new GameSaveEntry
            {
                EntryId = $"custom:{definition.Id}",
                IsCustom = true,
                CustomId = definition.Id,
                IsAvailable = isAvailable,
                SteamId = account?.SteamId ?? "custom",
                UserDataId = definition.Id,
                AccountName = account?.AccountName ?? "本机通用存档",
                PersonaName = account?.PersonaName ?? string.Empty,
                AppId = 0,
                GameName = definition.GameName.Trim(),
                SavePath = fullPath,
                FileCount = files.Count,
                TotalSize = files.Sum(GetFileSize),
                LastWriteTime = files.Select(GetLastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max()
            };
            ApplyBackupState(entry);
            return entry;
        }

        private void ApplyBackupState(GameSaveEntry entry)
        {
            var backups = GetBackups(entry);
            entry.BackupCount = backups.Count;
            entry.LastBackupTime = backups.FirstOrDefault()?.CreatedAt;
        }

        public async Task<GameSaveBackupInfo> BackupAsync(
            GameSaveEntry entry,
            bool isSafetyBackup = false,
            bool requireExclusiveAccess = true,
            bool useDifferential = true,
            CancellationToken cancellationToken = default)
        {
            ValidateEntryPath(entry);
            var gate = GetIoGate(entry.EntryId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await BackupCoreAsync(entry, isSafetyBackup, requireExclusiveAccess, useDifferential, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<GameSaveBackupInfo> BackupCoreAsync(
            GameSaveEntry entry,
            bool isSafetyBackup,
            bool requireExclusiveAccess,
            bool useDifferential,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(entry.SavePath))
                throw new DirectoryNotFoundException($"存档目录不存在：{entry.SavePath}");
            if (requireExclusiveAccess)
                EnsureSaveFilesAreAvailable(entry.SavePath);

            var destinationDirectory = GetBackupDirectory(entry);
            Directory.CreateDirectory(destinationDirectory);
            var timestamp = DateTime.Now;
            var baseBackup = !isSafetyBackup && useDifferential && _manualRetention > 1 ? FindDifferentialBase(entry) : null;
            if (baseBackup != null && !await IsBackupValidAsync(entry, baseBackup.FilePath, cancellationToken))
                baseBackup = null;
            var suffix = isSafetyBackup ? "-before-restore" : baseBackup != null ? "-diff" : string.Empty;
            var destination = GetUniqueBackupPath(destinationDirectory, $"{timestamp:yyyyMMdd-HHmmssfff}{suffix}.zip");
            var temporary = destination + ".tmp";

            EnsureFreeSpaceForBackup(destinationDirectory, entry.SavePath);

            try
            {
                var manifest = await Task.Run(
                    () => CreateArchive(entry, temporary, timestamp, isSafetyBackup, baseBackup, cancellationToken),
                    cancellationToken);
                if (!isSafetyBackup && TryFindEquivalentManualBackup(entry, manifest, out var equivalent) &&
                    await IsBackupValidAsync(entry, equivalent.FilePath, cancellationToken))
                {
                    TryDeleteFile(temporary);
                    equivalent.WasSkipped = true;
                    AppLogger.Info($"Game save backup skipped because content is unchanged: SteamId={entry.SteamId}, AppId={entry.AppId}");
                    return equivalent;
                }
                File.Move(temporary, destination);
                PruneBackups(entry);
                AppLogger.Info($"Game save backup created: {destination}");
                return new GameSaveBackupInfo
                {
                    FilePath = destination,
                    CreatedAt = timestamp,
                    Size = new FileInfo(destination).Length,
                    IsSafetyBackup = isSafetyBackup,
                    IsDifferential = baseBackup != null
                };
            }
            catch
            {
                TryDeleteFile(temporary);
                throw;
            }
        }

        public async Task RestoreLatestAsync(GameSaveEntry entry, CancellationToken cancellationToken = default)
        {
            var latest = GetBackups(entry)
                .FirstOrDefault(backup => !backup.IsSafetyBackup)
                ?? throw new InvalidOperationException("该账号与游戏还没有可恢复的备份。");

            await RestoreAsync(entry, latest.FilePath, cancellationToken);
        }

        public async Task RestoreAsync(
            GameSaveEntry entry,
            string backupPath,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? beforeCommit = null)
        {
            ValidateEntryPath(entry);
            var fullBackupPath = ValidateBackupPath(entry, backupPath);
            var gate = GetIoGate(entry.EntryId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RestoreCoreAsync(entry, fullBackupPath, cancellationToken, beforeCommit).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task RestoreCoreAsync(
            GameSaveEntry entry,
            string fullBackupPath,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? beforeCommit)
        {
            var manifest = ReadManifest(fullBackupPath);
            ValidateBackupIdentity(entry, manifest);

            var parent = Directory.GetParent(entry.SavePath)?.FullName
                ?? throw new InvalidOperationException("无法确定存档目录的父目录。");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.EntryId)))[..16];
            var transaction = Guid.NewGuid().ToString("N");
            var stagingPath = Path.Combine(parent, $".steamswitch-{identity}-restore-{transaction}");
            var oldPath = Path.Combine(parent, $".steamswitch-{identity}-old-{transaction}");

            // 上一次失败的恢复可能留下同前缀的残留目录，体积等同整份存档。
            // 不清理的话失败几次就能撑满磁盘。
            RemoveStaleRestoreDirectories(parent, identity, stagingPath);

            // 恢复需要约 2 份存档的空间：staging 解压一份，旧目录在同卷移动再占一份。
            var restoreBytes = manifest.Files
                .Where(file => file.IsStoredInArchive)
                .Sum(file => Math.Max(0L, file.Size)) * 22 / 10;
            EnsureFreeSpaceForRestore(entry.SavePath, restoreBytes);

            try
            {
                Directory.CreateDirectory(stagingPath);
                await Task.Run(() => ExtractArchiveSafely(fullBackupPath, stagingPath, manifest, cancellationToken), cancellationToken);

                if (beforeCommit != null)
                    await beforeCommit(cancellationToken);
                EnsureSaveFilesAreAvailable(entry.SavePath);

                // Only create the rollback point after the selected archive has passed
                // manifest, path, size and SHA-256 validation.
                if (Directory.Exists(entry.SavePath) && SafeEnumerateFiles(entry.SavePath).Any())
                    await BackupCoreAsync(entry, isSafetyBackup: true, requireExclusiveAccess: true, useDifferential: false, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(entry.SavePath))
                    Directory.Move(entry.SavePath, oldPath);

                Directory.Move(stagingPath, entry.SavePath);
                TryDeleteDirectory(oldPath);
                AppLogger.Info($"Game save restored: {fullBackupPath} -> {entry.SavePath}");
            }
            catch (Exception restoreEx)
            {
                TryDeleteDirectory(stagingPath);
                if (!Directory.Exists(entry.SavePath) && Directory.Exists(oldPath))
                {
                    // 回滚本身也可能失败（杀软/索引器仍占用目录）。此时数据只剩
                    // oldPath 里的那一份：必须原样保留，并把路径写进日志与异常，
                    // 否则用户的存档目录会凭空消失且无从找回。
                    try
                    {
                        Directory.Move(oldPath, entry.SavePath);
                    }
                    catch (Exception rollbackEx)
                    {
                        AppLogger.Error(
                            $"Game save restore failed and the rollback also failed. " +
                            $"The pre-restore data is preserved at {oldPath}; " +
                            $"move it back to {entry.SavePath} manually.", rollbackEx);
                        throw new IOException(
                            $"存档恢复失败，且自动回滚也失败。恢复前的存档仍保留在 {oldPath}，请手动改回 {entry.SavePath}。",
                            restoreEx);
                    }
                }
                throw;
            }
        }

        public List<GameSaveBackupInfo> GetBackups(string steamId, int appId)
        {
            return GetBackups(new GameSaveEntry
            {
                EntryId = $"steam:{steamId}:{appId}",
                SteamId = steamId,
                AppId = appId
            });
        }

        public List<GameSaveBackupInfo> GetBackups(GameSaveEntry entry)
        {
            var directory = GetBackupDirectory(entry);
            if (!Directory.Exists(directory))
                return new List<GameSaveBackupInfo>();

            // Use the guarded enumerator so one inaccessible/corrupt entry in
            // the backup directory cannot take down the entire save center.
            return SafeEnumerateTopLevelFiles(directory)
                .Where(path => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new GameSaveBackupInfo
                    {
                        FilePath = path,
                        CreatedAt = ParseBackupCreatedAt(path, info),
                        Size = info.Length,
                        IsSafetyBackup = Path.GetFileNameWithoutExtension(path).Contains("-before-restore", StringComparison.OrdinalIgnoreCase),
                        IsDifferential = Path.GetFileNameWithoutExtension(path).Contains("-diff", StringComparison.OrdinalIgnoreCase)
                    };
                })
                .OrderByDescending(backup => backup.CreatedAt)
                .ToList();
        }

        private static DateTime ParseBackupCreatedAt(string path, FileInfo info)
        {
            // Prefer the timestamp embedded in the backup file name. CreationTime
            // can be wrong after copies, restores, or volume-level timestamp changes.
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length >= 18 &&
                DateTime.TryParseExact(name[..18], "yyyyMMdd-HHmmssfff",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var precise))
                return precise;
            // 导入的备份命名为 yyyyMMdd-HHmmss-import-<guid>（"yyyyMMdd-HHmmss" 是 15 字符）。
            // 取 14 个字符永远匹配不上，会全部回退到 LastWriteTime，与其余按文件名排序的
            // 备份混用时序，进而影响 FindDifferentialBase 与 PruneBackups 的保留集合。
            if (name.Length >= 15 &&
                DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var coarse))
                return coarse;
            return info.LastWriteTime;
        }

        public Task VerifyBackupAsync(
            GameSaveEntry entry,
            string backupPath,
            CancellationToken cancellationToken = default)
        {
            ValidateEntryPath(entry);
            var fullBackupPath = ValidateBackupPath(entry, backupPath);
            return Task.Run(() =>
            {
                var manifest = ReadManifest(fullBackupPath);
                ValidateBackupIdentity(entry, manifest);
                var temporary = Path.Combine(GetBackupDirectory(entry), $".verify-{Guid.NewGuid():N}");
                try
                {
                    Directory.CreateDirectory(temporary);
                    ExtractArchiveSafely(fullBackupPath, temporary, manifest, cancellationToken);
                }
                finally
                {
                    TryDeleteDirectory(temporary);
                }
            }, cancellationToken);
        }

        public async Task ExportBackupAsync(
            GameSaveEntry entry,
            string backupPath,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            var gate = GetIoGate(entry.EntryId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ValidateEntryPath(entry);
                var source = ValidateBackupPath(entry, backupPath);
                var destination = Path.GetFullPath(destinationPath);
                if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("导出位置不能与原备份相同。");
                var destinationParent = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("导出目录无效。");
                var physicalDestination = Path.Combine(ResolvePhysicalDirectoryPath(destinationParent), Path.GetFileName(destination));
                if (physicalDestination.StartsWith(ResolvePhysicalDirectoryPath(_backupRoot) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("请导出到备份仓库以外的目录，避免覆盖其他恢复版本。");

                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var manifest = ReadManifest(source);
                    ValidateBackupIdentity(entry, manifest);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("导出目录无效。"));
                    var snapshot = Path.Combine(GetBackupDirectory(entry), $".export-{Guid.NewGuid():N}");
                    var temporary = destination + $".{Guid.NewGuid():N}.tmp";
                    try
                    {
                        Directory.CreateDirectory(snapshot);
                        ExtractArchiveSafely(source, snapshot, manifest, cancellationToken);
                        // Export the selected snapshot, including differential changes
                        // and deletions, as a self-contained full archive.
                        ApplyManifestIdentity(manifest, entry);
                        WriteFullSnapshotArchive(snapshot, temporary, manifest, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        File.Move(temporary, destination, overwrite: true);
                    }
                    finally
                    {
                        TryDeleteFile(temporary);
                        TryDeleteDirectory(snapshot);
                    }
                }, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<string> ImportBackupAsync(
            GameSaveEntry entry,
            string sourcePath,
            CancellationToken cancellationToken = default) =>
            ImportBackupCoreAsync(entry, sourcePath, expectedArchiveSha256: null, cancellationToken);

        /// <summary>
        /// Explicit migration only. The caller must show the source/target identity
        /// and obtain confirmation using GetImportPreviewAsync first. Normal imports
        /// and restores retain strict identity checks.
        /// </summary>
        public Task<string> ImportReassociatedCustomBackupAsync(
            GameSaveEntry entry,
            string sourcePath,
            string expectedArchiveSha256,
            CancellationToken cancellationToken = default)
        {
            if (!entry.IsCustom || entry.IsDetected || string.IsNullOrWhiteSpace(expectedArchiveSha256) ||
                expectedArchiveSha256.Length != 64)
                throw new InvalidOperationException("重新关联仅适用于手动添加的自定义目录，并且需要已确认的备份指纹。");
            return ImportBackupCoreAsync(entry, sourcePath, expectedArchiveSha256, cancellationToken);
        }

        public Task<GameSaveImportPreview> GetImportPreviewAsync(
            GameSaveEntry target,
            string sourcePath,
            CancellationToken cancellationToken = default)
        {
            ValidateEntryPath(target);
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(sourcePath);
                using var sourceLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                var manifest = ReadManifest(source);
                if (!string.Equals(manifest.BackupMode, "full", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrWhiteSpace(manifest.BaseBackupFile))
                    throw new InvalidDataException("请选择自包含的完整备份；请先在原存档中心导出所需版本。");
                var matches = true;
                try { ValidateBackupIdentity(target, manifest); }
                catch (InvalidDataException) { matches = false; }
                return new GameSaveImportPreview(manifest.GameName ?? string.Empty, manifest.AccountName ?? string.Empty,
                    manifest.SourceSavePath ?? string.Empty,
                    manifest.EntryId, ComputeSha256(source), matches,
                    target.IsCustom && !target.IsDetected && manifest.IsCustom && manifest.FormatVersion >= 2);
            }, cancellationToken);
        }

        private async Task<string> ImportBackupCoreAsync(
            GameSaveEntry entry,
            string sourcePath,
            string? expectedArchiveSha256,
            CancellationToken cancellationToken)
        {
            ValidateEntryPath(entry);
            var gate = GetIoGate(entry.EntryId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    var source = Path.GetFullPath(sourcePath);
                    if (!File.Exists(source) || !string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("请选择有效的 ZIP 存档备份。");

                    var directory = GetBackupDirectory(entry);
                    Directory.CreateDirectory(directory);
                    var temporary = Path.Combine(directory, $".import-{Guid.NewGuid():N}.zip");
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        File.Copy(source, temporary, overwrite: false);
                        if (expectedArchiveSha256 != null &&
                            !string.Equals(ComputeSha256(temporary), expectedArchiveSha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("备份文件在确认后发生变化，请重新选择并确认。");
                        var manifest = ReadManifest(temporary);
                        var originalEntryId = manifest.EntryId;
                        if (expectedArchiveSha256 == null) ValidateBackupIdentity(entry, manifest);
                        else if (!entry.IsCustom || entry.IsDetected || !manifest.IsCustom || manifest.FormatVersion < 2)
                            throw new InvalidDataException("只能将自定义存档备份显式重新关联到手动添加的目录。");
                        if (!string.Equals(manifest.BackupMode, "full", StringComparison.OrdinalIgnoreCase) ||
                            !string.IsNullOrWhiteSpace(manifest.BaseBackupFile))
                            throw new InvalidDataException("增量备份依赖原始完整备份，不能单独导入。请导出并导入完整备份。");

                        var verifyDirectory = Path.Combine(directory, $".verify-{Guid.NewGuid():N}");
                        try
                        {
                            Directory.CreateDirectory(verifyDirectory);
                            ExtractArchiveSafely(temporary, verifyDirectory, manifest, cancellationToken);
                            if (expectedArchiveSha256 != null)
                            {
                                ApplyManifestIdentity(manifest, entry);
                                manifest.IsSafetyBackup = false;
                                var rewritten = temporary + ".reassociated";
                                try
                                {
                                    WriteFullSnapshotArchive(verifyDirectory, rewritten, manifest, cancellationToken);
                                    File.Move(rewritten, temporary, overwrite: true);
                                }
                                finally { TryDeleteFile(rewritten); }
                            }
                        }
                        finally
                        {
                            TryDeleteDirectory(verifyDirectory);
                        }

                        var safeName = $"{DateTime.Now:yyyyMMdd-HHmmss}-import-{Guid.NewGuid():N}.zip";
                        var destination = Path.Combine(directory, safeName);
                        cancellationToken.ThrowIfCancellationRequested();
                        File.Move(temporary, destination);
                        AppLogger.Info($"Game save backup imported: {destination}");
                        if (expectedArchiveSha256 != null)
                            AppLogger.Info($"Custom game save backup explicitly reassociated: {originalEntryId} -> {entry.EntryId}");
                        return destination;
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private static void WriteFullSnapshotArchive(
            string snapshot,
            string archivePath,
            GameSaveBackupManifest manifest,
            CancellationToken cancellationToken)
        {
            manifest.FormatVersion = 3;
            manifest.BackupMode = "full";
            manifest.BaseBackupFile = string.Empty;
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            var buffer = new byte[128 * 1024];
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                file.StoredInArchive = true;
                var path = Path.GetFullPath(Path.Combine(snapshot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                EnsurePathIsInside(path, snapshot);
                var zipEntry = archive.CreateEntry($"data/{file.Path}", CompressionLevel.Optimal);
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var target = zipEntry.Open();
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, read);
                }
            }
            var manifestEntry = archive.CreateEntry(ManifestName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(manifestEntry.Open());
            writer.Write(JsonSerializer.Serialize(manifest, ManifestJsonOptions));
        }

        private static void ApplyManifestIdentity(GameSaveBackupManifest manifest, GameSaveEntry entry)
        {
            manifest.EntryId = entry.EntryId;
            manifest.IsCustom = entry.IsCustom;
            manifest.SteamId = entry.SteamId;
            manifest.UserDataId = entry.UserDataId;
            manifest.AccountName = entry.AccountName;
            manifest.AppId = entry.AppId;
            manifest.GameName = entry.GameName;
            manifest.SourceSavePath = Path.GetFullPath(entry.SavePath);
            manifest.IsDetected = entry.IsDetected;
        }

        public Task<GameSaveChangeSummary> AnalyzeChangesAsync(
            GameSaveEntry entry,
            CancellationToken cancellationToken = default)
        {
            ValidateEntryPath(entry);
            return Task.Run(() => AnalyzeChanges(entry, cancellationToken), cancellationToken);
        }

        public void DeleteBackup(GameSaveEntry entry, string backupPath)
        {
            ValidateEntryPath(entry);
            var fullBackupPath = ValidateBackupPath(entry, backupPath);
            var gate = GetIoGate(entry.EntryId);
            gate.Wait();
            try
            {
                var fileName = Path.GetFileName(fullBackupPath);
                var dependent = GetBackups(entry).FirstOrDefault(backup =>
                {
                    if (string.Equals(backup.FilePath, fullBackupPath, StringComparison.OrdinalIgnoreCase)) return false;
                    try
                    {
                        var candidate = ReadManifest(backup.FilePath);
                        return string.Equals(candidate.BaseBackupFile, fileName, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                });
                if (dependent != null)
                    throw new InvalidOperationException("该完整备份仍被增量备份引用，请先删除依赖它的增量备份。");
                File.Delete(fullBackupPath);
                AppLogger.Info($"Game save backup deleted: {fullBackupPath}");
            }
            finally
            {
                gate.Release();
            }
        }

        public static string ConvertSteamIdToUserDataId(string steamId)
        {
            if (!ulong.TryParse(steamId, out var steamId64))
                return string.Empty;
            return (steamId64 & uint.MaxValue).ToString();
        }

        private GameSaveBackupManifest CreateArchive(
            GameSaveEntry entry,
            string destination,
            DateTime createdAt,
            bool isSafetyBackup,
            GameSaveBackupInfo? baseBackup,
            CancellationToken cancellationToken)
        {
            var sourceRoot = Path.GetFullPath(entry.SavePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var filePaths = EnumerateSaveFiles(entry.SavePath).ToList();
            if (filePaths.Count > MaxBackupFileCount)
                throw new InvalidDataException($"存档文件超过 {MaxBackupFileCount:N0} 个，已取消备份。");
            if (ExceedsExpandedSizeLimit(filePaths.Select(GetFileSize)))
                throw new InvalidDataException("存档总大小超过 100 GB，已取消备份。");
            GameSaveBackupManifest? baseManifest = null;
            Dictionary<string, GameSaveBackupFile>? baseFiles = null;
            if (baseBackup != null)
            {
                baseManifest = ReadManifest(baseBackup.FilePath);
                ValidateBackupIdentity(entry, baseManifest);
                if (!string.Equals(baseManifest.BackupMode, "full", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("增量备份的基线必须是完整备份。");
                baseFiles = baseManifest.Files.ToDictionary(file => file.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
            }

            var fingerprints = BuildFileFingerprintMap(entry.SavePath, cancellationToken);
            var manifest = new GameSaveBackupManifest
            {
                FormatVersion = 3,
                EntryId = entry.EntryId,
                IsCustom = entry.IsCustom,
                SteamId = entry.SteamId,
                UserDataId = entry.UserDataId,
                AccountName = entry.AccountName,
                AppId = entry.AppId,
                GameName = entry.GameName,
                SourceSavePath = Path.GetFullPath(entry.SavePath),
                IsDetected = entry.IsDetected,
                CreatedAtUtc = createdAt.ToUniversalTime(),
                IsSafetyBackup = isSafetyBackup,
                BackupMode = baseBackup == null ? "full" : "differential",
                BaseBackupFile = baseBackup == null ? string.Empty : Path.GetFileName(baseBackup.FilePath),
                Files = fingerprints.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToList()
            };

            foreach (var file in manifest.Files)
            {
                file.StoredInArchive = baseFiles == null ||
                    !baseFiles.TryGetValue(file.Path, out var previous) ||
                    previous.Size != file.Size ||
                    !string.Equals(previous.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase);
            }

            using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
            var buffer = new byte[128 * 1024];
            long totalExpandedSize = 0;
            foreach (var file in manifest.Files.Where(item => item.IsStoredInArchive))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filePath = Path.Combine(entry.SavePath, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var fullPath = Path.GetFullPath(filePath);
                EnsurePathIsInside(fullPath, sourceRoot);
                var relativePath = file.Path;
                var zipEntry = archive.CreateEntry($"data/{relativePath}", CompressionLevel.Optimal);
                long size = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = zipEntry.Open();
                int bytesRead;
                while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, bytesRead);
                    hash.AppendData(buffer, 0, bytesRead);
                    size += bytesRead;
                    totalExpandedSize += bytesRead;
                    if (totalExpandedSize > MaxExpandedBackupSize)
                        throw new InvalidDataException("存档总大小超过 100 GB，已取消备份。");
                }
                var actualHash = Convert.ToHexString(hash.GetHashAndReset());
                if (size != file.Size || !string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"存档文件在备份期间发生变化：{relativePath}");
            }

            // Differential TOCTOU guard: files marked as unchanged (not stored in
            // this archive) were fingerprinted before packing. Re-read them now and
            // fail the backup if any drifted after that snapshot, otherwise the
            // archive would silently omit the newer content.
            foreach (var file in manifest.Files.Where(item => !item.IsStoredInArchive))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filePath = Path.Combine(entry.SavePath, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var fullPath = Path.GetFullPath(filePath);
                EnsurePathIsInside(fullPath, sourceRoot);
                if (!File.Exists(fullPath) ||
                    new FileInfo(fullPath).Length != file.Size ||
                    !string.Equals(ComputeSha256(fullPath), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"存档文件在备份期间发生变化（未写入增量包）：{file.Path}");
            }

            var manifestEntry = archive.CreateEntry(ManifestName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(manifestEntry.Open());
            writer.Write(JsonSerializer.Serialize(manifest, ManifestJsonOptions));
            return manifest;
        }

        private GameSaveChangeSummary AnalyzeChanges(GameSaveEntry entry, CancellationToken cancellationToken)
        {
            var currentFiles = BuildFileFingerprintMap(entry.SavePath, cancellationToken);
            var latest = GetBackups(entry).FirstOrDefault(backup => !backup.IsSafetyBackup);
            if (latest == null)
                return new GameSaveChangeSummary { Added = currentFiles.Count, HasPreviousBackup = false };

            try
            {
                var manifest = ReadManifest(latest.FilePath);
                ValidateBackupIdentity(entry, manifest);
                var previousFiles = manifest.Files.ToDictionary(file => file.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
                var summary = new GameSaveChangeSummary { HasPreviousBackup = true };
                foreach (var (path, current) in currentFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!previousFiles.TryGetValue(path, out var previous)) summary.Added++;
                    else if (current.Size != previous.Size || !string.Equals(current.Sha256, previous.Sha256, StringComparison.OrdinalIgnoreCase)) summary.Modified++;
                    else summary.Unchanged++;
                }
                summary.Deleted = previousFiles.Keys.Count(path => !currentFiles.ContainsKey(path));
                return summary;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                AppLogger.Warn($"Latest backup could not be used for change analysis: {ex.Message}");
                return new GameSaveChangeSummary { Added = currentFiles.Count, HasPreviousBackup = false };
            }
        }

        private static Dictionary<string, GameSaveBackupFile> BuildFileFingerprintMap(
            string savePath,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, GameSaveBackupFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in EnumerateSaveFiles(savePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(savePath, path).Replace('\\', '/');
                result[relative] = new GameSaveBackupFile
                {
                    Path = relative,
                    Size = new FileInfo(path).Length,
                    Sha256 = ComputeSha256(path)
                };
            }
            return result;
        }

        private async Task<bool> IsBackupValidAsync(GameSaveEntry entry, string path, CancellationToken cancellationToken)
        {
            try
            {
                await VerifyBackupAsync(entry, path, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Warn($"Backup cannot be reused: {path}: {ex.Message}");
                return false;
            }
        }

        private bool TryFindEquivalentManualBackup(
            GameSaveEntry entry,
            GameSaveBackupManifest candidate,
            out GameSaveBackupInfo equivalent)
        {
            equivalent = null!;
            var latest = GetBackups(entry).FirstOrDefault(backup => !backup.IsSafetyBackup);
            if (latest == null) return false;
            try
            {
                var previous = ReadManifest(latest.FilePath);
                ValidateBackupIdentity(entry, previous);
                if (!AreEquivalent(previous.Files, candidate.Files)) return false;
                equivalent = latest;
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to compare latest backup for deduplication: {ex.Message}");
                return false;
            }
        }

        private static bool AreEquivalent(List<GameSaveBackupFile> left, List<GameSaveBackupFile> right)
        {
            if (left.Count != right.Count) return false;
            var rightMap = right.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            return left.All(file => rightMap.TryGetValue(file.Path, out var other) &&
                file.Size == other.Size && string.Equals(file.Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase));
        }

        private GameSaveBackupInfo? FindDifferentialBase(GameSaveEntry entry)
        {
            var manualBackups = GetBackups(entry).Where(backup => !backup.IsSafetyBackup).ToList();
            var full = manualBackups.FirstOrDefault(backup => !backup.IsDifferential);
            if (full == null)
                return null;

            var differentialCount = manualBackups.TakeWhile(backup =>
                !string.Equals(backup.FilePath, full.FilePath, StringComparison.OrdinalIgnoreCase)).Count();
            if (differentialCount >= DifferentialBackupsPerFull)
                return null;

            try
            {
                var manifest = ReadManifest(full.FilePath);
                ValidateBackupIdentity(entry, manifest);
                return string.Equals(manifest.BackupMode, "full", StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to use differential backup base {full.FilePath}: {ex.Message}");
                return null;
            }
        }

        private void ExtractArchiveSafely(
            string archivePath,
            string destination,
            GameSaveBackupManifest manifest,
            CancellationToken cancellationToken)
        {
            if (string.Equals(manifest.BackupMode, "differential", StringComparison.OrdinalIgnoreCase))
            {
                var baseFileName = manifest.BaseBackupFile;
                if (string.IsNullOrWhiteSpace(baseFileName) ||
                    !string.Equals(Path.GetFileName(baseFileName), baseFileName, StringComparison.Ordinal))
                    throw new InvalidDataException("增量备份缺少有效的完整备份基线。");

                var basePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(archivePath)!, baseFileName));
                EnsurePathIsInside(basePath, Path.GetDirectoryName(archivePath)!);
                if (!File.Exists(basePath))
                    throw new FileNotFoundException("增量备份依赖的完整备份不存在。", basePath);
                var baseManifest = ReadManifest(basePath);
                if (!string.Equals(baseManifest.BackupMode, "full", StringComparison.OrdinalIgnoreCase) ||
                    !AreSameBackupIdentity(baseManifest, manifest))
                    throw new InvalidDataException("增量备份与其完整基线身份不一致。");

                ExtractArchiveData(basePath, destination, baseManifest, overwrite: false, cancellationToken);
                RemoveFilesNotInManifest(destination, manifest);
                ExtractArchiveData(archivePath, destination, manifest, overwrite: true, cancellationToken);
                VerifyExtractedSnapshot(destination, manifest, cancellationToken);
                return;
            }

            ExtractArchiveData(archivePath, destination, manifest, overwrite: false, cancellationToken);
            VerifyExtractedSnapshot(destination, manifest, cancellationToken);
        }

        private static void ExtractArchiveData(
            string archivePath,
            string destination,
            GameSaveBackupManifest manifest,
            bool overwrite,
            CancellationToken cancellationToken)
        {
            var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using var archive = ZipFile.OpenRead(archivePath);
            if (manifest.Files.Count > MaxBackupFileCount ||
                ExceedsExpandedSizeLimit(manifest.Files.Select(file => file.Size)))
                throw new InvalidDataException("备份文件数量或展开大小超过安全限制。");

            var storedFiles = manifest.Files.Where(file => file.IsStoredInArchive).ToDictionary(
                file => file.Path.Replace('\\', '/'),
                StringComparer.OrdinalIgnoreCase);
            var dataEntries = archive.Entries
                .Where(entry => entry.FullName.StartsWith("data/", StringComparison.Ordinal) && !string.IsNullOrEmpty(entry.Name))
                .ToList();
            if (dataEntries.Count != storedFiles.Count)
                throw new InvalidDataException("备份文件清单与压缩包内容不一致。");

            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var archiveEntry in dataEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedRelativePath = archiveEntry.FullName["data/".Length..].Replace('\\', '/');
                if (!seenFiles.Add(normalizedRelativePath) ||
                    !storedFiles.TryGetValue(normalizedRelativePath, out var expected) ||
                    archiveEntry.Length != expected.Size)
                    throw new InvalidDataException($"备份文件校验失败：{normalizedRelativePath}");

                var relativePath = normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar);
                var destinationPath = Path.GetFullPath(Path.Combine(destination, relativePath));
                EnsurePathIsInside(destinationPath, destinationRoot);
                // 顺序很重要：EnsureNoReparsePointBelow 遇到"还不存在的路径段"会直接
                // 返回，放在 CreateDirectory 之前等于本次新建的目录链完全没被检查。
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                EnsureNoReparsePointBelow(destinationPath, destinationRoot);
                archiveEntry.ExtractToFile(destinationPath, overwrite);
                if (!string.Equals(ComputeSha256(destinationPath), expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"备份文件哈希校验失败：{normalizedRelativePath}");
            }
            if (seenFiles.Count != storedFiles.Count)
                throw new InvalidDataException("备份缺少清单中记录的文件。");
        }

        /// <summary>
        /// 清理上一次失败恢复留下的残留目录（体积等同整份存档）。删除失败只记日志，
        /// 绝不阻断本次恢复；留到下一次启动/恢复时再试。
        /// </summary>
        private static void RemoveStaleRestoreDirectories(string parent, string identity, string currentStaging)
        {
            try
            {
                if (!Directory.Exists(parent)) return;
                var prefixes = new[]
                {
                    $".steamswitch-{identity}-restore-",
                    $".steamswitch-{identity}-old-"
                };
                foreach (var directory in Directory.GetDirectories(parent))
                {
                    var name = Path.GetFileName(directory);
                    if (!prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (string.Equals(directory, currentStaging, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { Directory.Delete(directory, recursive: true); }
                    catch (Exception ex) { AppLogger.Warn($"Unable to remove stale restore directory {name}: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to scan for stale restore directories: {ex.Message}");
            }
        }

        /// <summary>
        /// 恢复前的空间预检：staging 解压约 1 份，旧目录同卷移动再占 1 份。
        /// 不预检的话大存档会在解压中途或 Directory.Move 处才失败。
        /// </summary>
        private static void EnsureFreeSpaceForRestore(string savePath, long requiredBytes)
        {
            if (requiredBytes <= 0) return;

            var target = Directory.Exists(savePath) ? savePath : Path.GetDirectoryName(savePath);
            if (string.IsNullOrWhiteSpace(target)) return;

            var root = Path.GetPathRoot(Path.GetFullPath(target));
            if (string.IsNullOrEmpty(root)) return;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;

            if (drive.AvailableFreeSpace < requiredBytes)
                throw new IOException(
                    $"磁盘空间不足：恢复需要约 {requiredBytes / 1024 / 1024} MB，{root} 仅剩 {drive.AvailableFreeSpace / 1024 / 1024} MB。");
        }

        /// <summary>
        /// 备份前预估源目录体积并核对可用空间：写满系统盘会影响 Steam、
        /// 资源管理器和页面文件，必须在动手之前拦下。
        /// </summary>
        private static void EnsureFreeSpaceForBackup(string destinationDirectory, string savePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(savePath) || !Directory.Exists(savePath))
                    return;

                var root = Path.GetPathRoot(Path.GetFullPath(destinationDirectory));
                if (string.IsNullOrEmpty(root)) return;

                var drive = new DriveInfo(root);
                if (!drive.IsReady) return;

                var required = EstimateDirectorySize(savePath) * 12 / 10; // 预留 20% 余量
                if (required <= 0) return;

                if (drive.AvailableFreeSpace < required)
                    throw new IOException(
                        $"磁盘空间不足：备份需要约 {required / 1024 / 1024} MB，{root} 仅剩 {drive.AvailableFreeSpace / 1024 / 1024} MB。");
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Could not evaluate free space before backup: {ex.Message}");
            }
        }

        private static long EstimateDirectorySize(string directory)
        {
            long total = 0;
            foreach (var path in EnumerateSaveFiles(directory))
            {
                try { total += new FileInfo(path).Length; }
                catch { }
            }
            return total;
        }

        private static void RemoveFilesNotInManifest(string destination, GameSaveBackupManifest manifest)
        {
            var expected = manifest.Files.Select(file => file.Path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in EnumerateSaveFiles(destination).ToList())
            {
                var relative = Path.GetRelativePath(destination, path).Replace('\\', '/');
                if (!expected.Contains(relative))
                {
                    // 单个只读/被占用的文件不应让整次还原失败：后面还有
                    // VerifyExtractedSnapshot 做最终一致性校验兜底。
                    try { File.Delete(path); }
                    catch (Exception ex) { AppLogger.Warn($"Could not remove stale file during restore: {path} ({ex.Message})"); }
                }
            }
        }

        private static void VerifyExtractedSnapshot(
            string destination,
            GameSaveBackupManifest manifest,
            CancellationToken cancellationToken)
        {
            var actualFiles = EnumerateSaveFiles(destination).ToList();
            if (actualFiles.Count != manifest.Files.Count)
                throw new InvalidDataException("恢复后的文件数量与备份清单不一致。");
            var expected = manifest.Files.ToDictionary(file => file.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
            foreach (var path in actualFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(destination, path).Replace('\\', '/');
                if (!expected.TryGetValue(relative, out var file) ||
                    new FileInfo(path).Length != file.Size ||
                    !string.Equals(ComputeSha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"恢复快照校验失败：{relative}");
            }
        }

        private static GameSaveBackupManifest ReadManifest(string archivePath)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry(ManifestName)
                ?? throw new InvalidDataException("不是有效的 Steam Switch 存档备份：缺少清单。");
            if (entry.Length is <= 0 or > MaxManifestBytes)
                throw new InvalidDataException("备份清单大小无效或超过 8 MB 安全上限。");

            using var input = entry.Open();
            using var manifestBuffer = new MemoryStream((int)entry.Length);
            var buffer = new byte[64 * 1024];
            var totalRead = 0;
            int bytesRead;
            while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                totalRead = checked(totalRead + bytesRead);
                if (totalRead > MaxManifestBytes)
                    throw new InvalidDataException("备份清单展开大小超过 8 MB 安全上限。");
                manifestBuffer.Write(buffer, 0, bytesRead);
            }

            var manifestJson = Encoding.UTF8.GetString(manifestBuffer.GetBuffer(), 0, totalRead);
            var manifest = JsonSerializer.Deserialize<GameSaveBackupManifest>(manifestJson)
                ?? throw new InvalidDataException("备份清单损坏。");
            if (manifest.FormatVersion is not 1 and not 2 and not 3)
                throw new InvalidDataException($"不支持的备份格式版本：{manifest.FormatVersion}");
            if (manifest.FormatVersion >= 2 && string.IsNullOrWhiteSpace(manifest.EntryId))
                throw new InvalidDataException("备份清单缺少存档身份信息。");
            if (manifest.FormatVersion == 3 &&
                !string.Equals(manifest.BackupMode, "full", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(manifest.BackupMode, "differential", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份清单包含无效的备份模式。");
            if (string.Equals(manifest.BackupMode, "differential", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(manifest.BaseBackupFile) ||
                 !string.Equals(Path.GetFileName(manifest.BaseBackupFile), manifest.BaseBackupFile, StringComparison.Ordinal)))
                throw new InvalidDataException("增量备份清单缺少有效的基线文件。");
            if (manifest.Files == null || manifest.Files.Any(file =>
                    file == null || !IsSafeManifestPath(file.Path) || file.Size < 0 || file.Sha256?.Length != 64))
                throw new InvalidDataException("备份清单包含无效的文件记录。");
            if (manifest.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
                throw new InvalidDataException("备份清单包含重复的文件路径。");
            if (manifest.Files.Count > MaxBackupFileCount ||
                ExceedsExpandedSizeLimit(manifest.Files.Select(file => file.Size)))
                throw new InvalidDataException("备份文件数量或展开大小超过安全限制。");
            return manifest;
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static bool IsSafeManifestPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':'))
                return false;
            var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 && segments.All(segment => segment is not "." and not "..");
        }

        private static void EnsureSaveFilesAreAvailable(string savePath)
        {
            if (!Directory.Exists(savePath))
                return;
            foreach (var path in EnumerateSaveFiles(savePath))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                catch (IOException ex)
                {
                    throw new IOException($"存档文件正在被 Steam 或游戏占用，请先退出游戏后重试：{Path.GetFileName(path)}", ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new IOException($"无法独占读取存档文件，请检查权限或退出游戏：{Path.GetFileName(path)}", ex);
                }
            }
        }

        private void ValidateEntryPath(GameSaveEntry entry)
        {
            if (entry.IsCustom)
            {
                if (!Guid.TryParseExact(entry.CustomId, "N", out _) ||
                    !string.Equals(entry.EntryId, $"custom:{entry.CustomId}", StringComparison.Ordinal))
                    throw new InvalidDataException("自定义存档项缺少有效身份信息。");

                var fullPath = Path.GetFullPath(entry.SavePath).TrimEnd(Path.DirectorySeparatorChar);
                var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(root) || string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("不能把磁盘根目录设为存档目录。");

                if (!IsCustomPathAllowed(fullPath))
                    throw new InvalidDataException("自定义存档目录不能与 SteamSwitch 备份目录重叠。");
                return;
            }

            if (entry.AppId <= 0 || string.IsNullOrWhiteSpace(entry.UserDataId) ||
                !ulong.TryParse(entry.SteamId, out _) ||
                entry.UserDataId != ConvertSteamIdToUserDataId(entry.SteamId) ||
                entry.EntryId != $"steam:{entry.SteamId}:{entry.AppId}")
                throw new InvalidDataException("存档项缺少账号或游戏信息。");
            var expected = Path.Combine(_steamPath, "userdata", entry.UserDataId, entry.AppId.ToString());
            if (!string.Equals(
                    Path.GetFullPath(entry.SavePath).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("存档路径不在预期的 Steam userdata 目录中。");
            }
            EnsureNoReparsePointBelow(entry.SavePath, _steamPath);
        }

        private string ValidateBackupPath(GameSaveEntry entry, string backupPath)
        {
            var fullBackupPath = Path.GetFullPath(backupPath);
            EnsurePathIsInside(fullBackupPath, GetBackupDirectory(entry));
            if (!string.Equals(Path.GetExtension(fullBackupPath), ".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份文件必须为 ZIP 格式。");
            if (!File.Exists(fullBackupPath))
                throw new FileNotFoundException("备份文件不存在。", fullBackupPath);
            return fullBackupPath;
        }

        private static void ValidateBackupIdentity(GameSaveEntry entry, GameSaveBackupManifest manifest)
        {
            if (manifest.FormatVersion >= 2)
            {
                if (entry.IsCustom != manifest.IsCustom ||
                    !string.Equals(manifest.EntryId, entry.EntryId, StringComparison.Ordinal))
                    throw new InvalidDataException("备份所属存档与当前选择不一致。");
                return;
            }

            if (manifest.AppId != entry.AppId ||
                !string.Equals(manifest.SteamId, entry.SteamId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("备份所属账号或游戏与当前选择不一致。");
            }
        }

        private static bool AreSameBackupIdentity(GameSaveBackupManifest left, GameSaveBackupManifest right)
        {
            if (left.FormatVersion >= 2 || right.FormatVersion >= 2)
                return left.IsCustom == right.IsCustom &&
                       string.Equals(left.EntryId, right.EntryId, StringComparison.Ordinal);
            return left.AppId == right.AppId && string.Equals(left.SteamId, right.SteamId, StringComparison.Ordinal);
        }

        private void PruneBackups(GameSaveEntry entry)
        {
            var backups = GetBackups(entry);
            var retainedManual = backups.Where(backup => !backup.IsSafetyBackup).Take(_manualRetention).ToList();
            var retainedPaths = retainedManual.Select(backup => backup.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var requiredBasePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var differential in retainedManual.Where(backup => backup.IsDifferential))
            {
                try
                {
                    var manifest = ReadManifest(differential.FilePath);
                    if (!string.IsNullOrWhiteSpace(manifest.BaseBackupFile))
                    {
                        var basePath = Path.Combine(Path.GetDirectoryName(differential.FilePath)!, manifest.BaseBackupFile);
                        retainedPaths.Add(basePath);
                        requiredBasePaths.Add(basePath);
                    }
                }
                catch (Exception ex)
                {
                    // Cannot tell which full baseline this differential depends on.
                    // Abort pruning entirely so a required base is never deleted.
                    AppLogger.Warn($"Unable to read retained differential backup; pruning aborted to protect its base: {ex.Message}");
                    return;
                }
            }
            // Dependencies may exceed the configured snapshot count. Never remove
            // the newest snapshot just to keep its older full baseline.
            while (retainedPaths.Count > Math.Max(_manualRetention, requiredBasePaths.Count + 1))
            {
                var removable = backups.LastOrDefault(backup =>
                    !backup.IsSafetyBackup && retainedPaths.Contains(backup.FilePath) && !requiredBasePaths.Contains(backup.FilePath) &&
                    !string.Equals(backup.FilePath, retainedManual.FirstOrDefault()?.FilePath, StringComparison.OrdinalIgnoreCase));
                if (removable == null) break;
                retainedPaths.Remove(removable.FilePath);
            }
            PruneGroup(backups.Where(backup => !backup.IsSafetyBackup && !retainedPaths.Contains(backup.FilePath)));
            PruneGroup(backups.Where(backup => backup.IsSafetyBackup).Skip(_safetyRetention));
        }

        private static void PruneGroup(IEnumerable<GameSaveBackupInfo> expired)
        {
            foreach (var backup in expired)
            {
                try
                {
                    File.Delete(backup.FilePath);
                    AppLogger.Info($"Expired game save backup deleted: {backup.FilePath}");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Unable to delete expired backup {backup.FilePath}: {ex.Message}");
                }
            }
        }

        private string GetBackupDirectory(GameSaveEntry entry)
        {
            if (entry.IsCustom)
            {
                if (!Guid.TryParseExact(entry.CustomId, "N", out _))
                    throw new InvalidDataException("无效的自定义存档 ID。");
                return Path.Combine(_backupRoot, "custom", entry.CustomId);
            }

            var safeSteamId = string.Concat(entry.SteamId.Where(char.IsDigit));
            if (string.IsNullOrEmpty(safeSteamId) || entry.AppId <= 0)
                throw new InvalidDataException("无效的账号或游戏 ID。");
            return Path.Combine(_backupRoot, safeSteamId, entry.AppId.ToString());
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string path)
        {
            try { return Directory.EnumerateDirectories(path).ToList(); }
            catch (Exception ex) { AppLogger.Warn($"Unable to enumerate save directories: {path}. {ex.Message}"); return Array.Empty<string>(); }
        }

        private static bool IsRegularDirectory(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string path)
        {
            try { return EnumerateSaveFiles(path).ToList(); }
            catch (Exception ex) { AppLogger.Warn($"Unable to enumerate save files: {path}. {ex.Message}"); return Array.Empty<string>(); }
        }

        private static IEnumerable<string> SafeEnumerateTopLevelFiles(string path)
        {
            try { return Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).ToList(); }
            catch (Exception ex) { AppLogger.Warn($"Unable to enumerate backup files: {path}. {ex.Message}"); return Array.Empty<string>(); }
        }

        private static IEnumerable<string> EnumerateSaveFiles(string path) =>
            Directory.EnumerateFiles(path, "*", SaveEnumerationOptions);

        private bool IsCustomPathAllowed(string candidate)
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
                if (Directory.Exists(fullPath) &&
                    (new DirectoryInfo(fullPath).Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;

                var physicalPath = ResolvePhysicalDirectoryPath(fullPath);
                var physicalBackupRoot = ResolvePhysicalDirectoryPath(_backupRoot);
                return !string.Equals(physicalPath, physicalBackupRoot, StringComparison.OrdinalIgnoreCase) &&
                       !physicalPath.StartsWith(physicalBackupRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                       !physicalBackupRoot.StartsWith(physicalPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to validate custom save path {candidate}: {ex.Message}");
                return false;
            }
        }

        private static string ResolvePhysicalDirectoryPath(string path)
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetPathRoot(fullPath)
                ?? throw new InvalidDataException("无法解析存档目录所在磁盘。");
            var current = root;
            foreach (var segment in Path.GetRelativePath(root, fullPath)
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                var info = new DirectoryInfo(current);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0)
                    continue;
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                    current = target.FullName;
            }
            return Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar);
        }

        private static bool ExceedsExpandedSizeLimit(IEnumerable<long> sizes)
        {
            var total = 0L;
            foreach (var size in sizes)
            {
                if (size < 0 || size > MaxExpandedBackupSize - total)
                    return true;
                total += size;
            }
            return false;
        }

        private static long GetFileSize(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        private static DateTime GetLastWriteTime(string path)
        {
            try { return File.GetLastWriteTime(path); }
            catch { return DateTime.MinValue; }
        }

        private static string GetUniqueBackupPath(string directory, string fileName)
        {
            var candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate)) return candidate;
            return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fileName)}-{Guid.NewGuid():N}.zip");
        }

        private static void EnsurePathIsInside(string candidate, string root)
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedCandidate = Path.GetFullPath(candidate);
            if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("检测到不安全的文件路径，操作已取消。");
        }

        private static SemaphoreSlim GetIoGate(string entryId) =>
            IoGates.GetOrAdd(entryId, _ => new SemaphoreSlim(1, 1));

        /// <summary>
        /// Rejects restoring through a symlink or junction. EnsurePathIsInside
        /// only checks the resulting string, so a link anywhere below the
        /// destination root could still redirect the write outside it.
        /// </summary>
        private static void EnsureNoReparsePointBelow(string candidate, string root)
        {
            var normalizedRoot = Path.GetFullPath(root);
            var relative = Path.GetRelativePath(normalizedRoot, Path.GetFullPath(candidate));
            var current = normalizedRoot;

            foreach (var segment in relative.Split(
                         Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment.Length == 0 || segment == ".")
                    continue;

                current = Path.Combine(current, segment);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (FileNotFoundException)
                {
                    // This segment does not exist yet, so neither can any
                    // deeper one; everything below is created freshly.
                    return;
                }
                catch (DirectoryNotFoundException)
                {
                    return;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("检测到符号链接，备份恢复已取消。");
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                // Do not swallow silently: a failed delete of a rollback/staging
                // directory is a leftover the operator may need to clean up.
                AppLogger.Warn($"Unable to delete temporary directory {path}: {ex.Message}");
            }
        }
    }
}
