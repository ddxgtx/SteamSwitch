using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
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
        private const int MaxBackupFileCount = 100_000;
        private const long MaxExpandedBackupSize = 100L * 1024 * 1024 * 1024;
        private const int DifferentialBackupsPerFull = 4;
        private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };
        private static readonly EnumerationOptions SaveEnumerationOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
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
            _backupRoot = Path.GetFullPath(backupRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "SteamSwitch Backups"));
            _manualRetention = Math.Clamp(manualRetention, 1, 100);
            _safetyRetention = Math.Clamp(safetyRetention, 1, 50);
        }

        public string BackupRoot => _backupRoot;

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

            return result
                .OrderBy(entry => entry.AccountDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.GameName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
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
            bool requireExclusiveAccess = false,
            bool useDifferential = true,
            CancellationToken cancellationToken = default)
        {
            ValidateEntryPath(entry);
            if (!Directory.Exists(entry.SavePath))
                throw new DirectoryNotFoundException($"存档目录不存在：{entry.SavePath}");
            if (requireExclusiveAccess)
                EnsureSaveFilesAreAvailable(entry.SavePath);

            var destinationDirectory = GetBackupDirectory(entry);
            Directory.CreateDirectory(destinationDirectory);
            var timestamp = DateTime.Now;
            var baseBackup = !isSafetyBackup && useDifferential ? FindDifferentialBase(entry) : null;
            var suffix = isSafetyBackup ? "-before-restore" : baseBackup != null ? "-diff" : string.Empty;
            var destination = GetUniqueBackupPath(destinationDirectory, $"{timestamp:yyyyMMdd-HHmmssfff}{suffix}.zip");
            var temporary = destination + ".tmp";

            try
            {
                var manifest = await Task.Run(
                    () => CreateArchive(entry, temporary, timestamp, isSafetyBackup, baseBackup, cancellationToken),
                    cancellationToken);
                if (!isSafetyBackup && TryFindEquivalentManualBackup(entry, manifest, out var equivalent))
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

            var manifest = ReadManifest(fullBackupPath);
            ValidateBackupIdentity(entry, manifest);

            var parent = Directory.GetParent(entry.SavePath)?.FullName
                ?? throw new InvalidOperationException("无法确定存档目录的父目录。");
            var stagingPath = Path.Combine(parent, $".{entry.AppId}.steamswitch-restore-{Guid.NewGuid():N}");
            var oldPath = Path.Combine(parent, $".{entry.AppId}.steamswitch-old-{Guid.NewGuid():N}");

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
                    await BackupAsync(entry, isSafetyBackup: true, cancellationToken: cancellationToken);

                if (Directory.Exists(entry.SavePath))
                    Directory.Move(entry.SavePath, oldPath);

                Directory.Move(stagingPath, entry.SavePath);
                TryDeleteDirectory(oldPath);
                AppLogger.Info($"Game save restored: {fullBackupPath} -> {entry.SavePath}");
            }
            catch
            {
                TryDeleteDirectory(stagingPath);
                if (!Directory.Exists(entry.SavePath) && Directory.Exists(oldPath))
                    Directory.Move(oldPath, entry.SavePath);
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

            return Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new GameSaveBackupInfo
                    {
                        FilePath = path,
                        CreatedAt = info.CreationTime,
                        Size = info.Length,
                        IsSafetyBackup = Path.GetFileNameWithoutExtension(path).Contains("-before-restore", StringComparison.OrdinalIgnoreCase),
                        IsDifferential = Path.GetFileNameWithoutExtension(path).Contains("-diff", StringComparison.OrdinalIgnoreCase)
                    };
                })
                .OrderByDescending(backup => backup.CreatedAt)
                .ToList();
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
            if (filePaths.Sum(GetFileSize) > MaxExpandedBackupSize)
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
            if (manifest.Files.Count > MaxBackupFileCount || manifest.Files.Sum(file => file.Size) > MaxExpandedBackupSize)
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
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                archiveEntry.ExtractToFile(destinationPath, overwrite);
                if (!string.Equals(ComputeSha256(destinationPath), expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"备份文件哈希校验失败：{normalizedRelativePath}");
            }
            if (seenFiles.Count != storedFiles.Count)
                throw new InvalidDataException("备份缺少清单中记录的文件。");
        }

        private static void RemoveFilesNotInManifest(string destination, GameSaveBackupManifest manifest)
        {
            var expected = manifest.Files.Select(file => file.Path.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in EnumerateSaveFiles(destination).ToList())
            {
                var relative = Path.GetRelativePath(destination, path).Replace('\\', '/');
                if (!expected.Contains(relative))
                    File.Delete(path);
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
            using var reader = new StreamReader(entry.Open());
            var manifest = JsonSerializer.Deserialize<GameSaveBackupManifest>(reader.ReadToEnd())
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

            if (entry.AppId <= 0 || string.IsNullOrWhiteSpace(entry.UserDataId))
                throw new InvalidDataException("存档项缺少账号或游戏信息。");
            var expected = Path.Combine(_steamPath, "userdata", entry.UserDataId, entry.AppId.ToString());
            if (!string.Equals(
                    Path.GetFullPath(entry.SavePath).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("存档路径不在预期的 Steam userdata 目录中。");
            }
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

        private static void VerifyArchiveContents(
            string archivePath,
            GameSaveBackupManifest manifest,
            CancellationToken cancellationToken)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (manifest.Files.Count > MaxBackupFileCount || manifest.Files.Sum(file => file.Size) > MaxExpandedBackupSize)
                throw new InvalidDataException("备份文件数量或展开大小超过安全限制。");

            var expectedFiles = manifest.Files.ToDictionary(file => file.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
            var dataEntries = archive.Entries
                .Where(entry => entry.FullName.StartsWith("data/", StringComparison.Ordinal) && !string.IsNullOrEmpty(entry.Name))
                .ToList();
            if (dataEntries.Count != expectedFiles.Count)
                throw new InvalidDataException("备份文件清单与压缩包内容不一致。");

            var buffer = new byte[128 * 1024];
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var archiveEntry in dataEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = archiveEntry.FullName["data/".Length..].Replace('\\', '/');
                if (!seenFiles.Add(relativePath) ||
                    !expectedFiles.TryGetValue(relativePath, out var expected) ||
                    archiveEntry.Length != expected.Size)
                    throw new InvalidDataException($"备份文件校验失败：{relativePath}");

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var stream = archiveEntry.Open();
                int bytesRead;
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, bytesRead);
                }
                if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"备份文件哈希校验失败：{relativePath}");
            }
            if (seenFiles.Count != expectedFiles.Count)
                throw new InvalidDataException("备份缺少清单中记录的文件。");
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
                    AppLogger.Warn($"Unable to read retained differential backup: {ex.Message}");
                }
            }
            while (retainedPaths.Count > _manualRetention)
            {
                var removable = backups.LastOrDefault(backup =>
                    !backup.IsSafetyBackup && retainedPaths.Contains(backup.FilePath) && !requiredBasePaths.Contains(backup.FilePath));
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

        private static IEnumerable<string> SafeEnumerateFiles(string path)
        {
            try { return EnumerateSaveFiles(path).ToList(); }
            catch (Exception ex) { AppLogger.Warn($"Unable to enumerate save files: {path}. {ex.Message}"); return Array.Empty<string>(); }
        }

        private static IEnumerable<string> EnumerateSaveFiles(string path) =>
            Directory.EnumerateFiles(path, "*", SaveEnumerationOptions);

        private bool IsCustomPathAllowed(string candidate)
        {
            var fullPath = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            var backupRoot = _backupRoot.TrimEnd(Path.DirectorySeparatorChar);
            return !string.Equals(fullPath, backupRoot, StringComparison.OrdinalIgnoreCase) &&
                   !fullPath.StartsWith(backupRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                   !backupRoot.StartsWith(fullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }
    }
}
