using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    internal static class GameSavePathDetector
    {
        private const int MaxDetectedFiles = 20_000;
        private const long MaxDetectedSize = 10L * 1024 * 1024 * 1024;

        public static List<GameSaveEntry> Detect(
            IEnumerable<SteamLibraryGame> games,
            ISet<string> existingPaths,
            CancellationToken cancellationToken)
        {
            var result = new List<GameSaveEntry>();
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localLow = Path.Combine(Directory.GetParent(local)?.FullName ?? profile, "LocalLow");
            var companyDirectories = SafeDirectories(localLow).Take(200).ToList();
            foreach (var game in games)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var candidate in BuildCandidates(game, companyDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryAddCandidate(result, existingPaths, game, candidate.Path, candidate.Rule);
                }
            }
            return result;
        }

        private static IEnumerable<(string Path, string Rule)> BuildCandidates(
            SteamLibraryGame game,
            IReadOnlyCollection<string> companyDirectories)
        {
            var names = new[] { game.Name, game.InstallDir }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(SanitizeDirectoryName)
                .Where(name => name.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localLow = Path.Combine(Directory.GetParent(local)?.FullName ?? profile, "LocalLow");

            foreach (var name in names)
            {
                yield return (Path.Combine(documents, "My Games", name), "文档/My Games");
                yield return (Path.Combine(documents, name), "文档目录");
                yield return (Path.Combine(profile, "Saved Games", name), "Saved Games");
                yield return (Path.Combine(local, name, "Saved", "SaveGames"), "Unreal SaveGames");
                yield return (Path.Combine(local, name, "Saved"), "Unreal Saved");
                yield return (Path.Combine(local, name, "SaveGames"), "LocalAppData SaveGames");
                yield return (Path.Combine(local, name, "Saves"), "LocalAppData Saves");
                yield return (Path.Combine(roaming, name, "SaveGames"), "AppData SaveGames");
                yield return (Path.Combine(roaming, name, "Saves"), "AppData Saves");
                yield return (Path.Combine(localLow, name), "Unity LocalLow");

                if (!string.IsNullOrWhiteSpace(game.LibraryPath) && !string.IsNullOrWhiteSpace(game.InstallDir))
                {
                    var installRoot = Path.Combine(game.LibraryPath, "steamapps", "common", game.InstallDir);
                    yield return (Path.Combine(installRoot, "SaveGames"), "游戏目录 SaveGames");
                    yield return (Path.Combine(installRoot, "Saves"), "游戏目录 Saves");
                    yield return (Path.Combine(installRoot, "save"), "游戏目录 save");
                    yield return (Path.Combine(installRoot, "savedata"), "游戏目录 savedata");
                }

                foreach (var companyDirectory in companyDirectories)
                    yield return (Path.Combine(companyDirectory, name), "Unity 公司目录");
            }
        }

        private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private static void TryAddCandidate(
            List<GameSaveEntry> result,
            ISet<string> existingPaths,
            SteamLibraryGame game,
            string candidate,
            string rule)
        {
            try
            {
                if (!Directory.Exists(candidate))
                    return;
                var fullPath = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
                if (!IsAcceptableDetectedPath(fullPath, game))
                {
                    AppLogger.Debug($"Smart save candidate rejected by path containment check: {fullPath}");
                    return;
                }
                if (!existingPaths.Add(fullPath))
                    return;

                var files = Directory.EnumerateFiles(fullPath, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MaxRecursionDepth = 8
                }).Take(MaxDetectedFiles + 1).ToList();
                if (files.Count is 0 or > MaxDetectedFiles)
                    return;

                var sizes = files.Select(path =>
                {
                    try { return new FileInfo(path).Length; }
                    catch { return 0L; }
                }).ToList();
                var totalSize = sizes.Sum();
                if (totalSize > MaxDetectedSize)
                    return;

                var id = CreateStableId(game.AppId, fullPath);
                result.Add(new GameSaveEntry
                {
                    EntryId = $"custom:{id}",
                    IsCustom = true,
                    IsDetected = true,
                    CustomId = id,
                    DetectionRule = rule,
                    IsAvailable = true,
                    SteamId = "detected",
                    UserDataId = id,
                    AccountName = "本机通用存档",
                    AppId = game.AppId,
                    GameName = game.Name,
                    IconPath = game.IconPath,
                    SavePath = fullPath,
                    FileCount = files.Count,
                    TotalSize = totalSize,
                    LastWriteTime = files.Select(path =>
                    {
                        try { return File.GetLastWriteTime(path); }
                        catch { return DateTime.MinValue; }
                    }).DefaultIfEmpty(DateTime.MinValue).Max()
                });
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Smart save candidate skipped: {candidate}. {ex.Message}");
            }
        }

        private static string CreateStableId(int appId, string path)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"{appId}|{path.ToUpperInvariant()}"));
            return new Guid(bytes).ToString("N");
        }

        internal static string? ResolveBackupPath(SteamLibraryGame game, string customId, string? recordedPath)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localLow = Path.Combine(Directory.GetParent(local)?.FullName ?? profile, "LocalLow");
            var companies = SafeDirectories(localLow).Take(200).ToList();
            // A removed Unity company directory no longer appears in enumeration.
            // Only admit exactly one company segment below LocalLow, then run the
            // normal candidate rules and stable-ID check below.
            if (!string.IsNullOrWhiteSpace(recordedPath))
            {
                try
                {
                    var parent = Directory.GetParent(Path.GetFullPath(recordedPath))?.FullName;
                    if (parent != null && string.Equals(Directory.GetParent(parent)?.FullName,
                            Path.GetFullPath(localLow), StringComparison.OrdinalIgnoreCase))
                        companies.Add(parent);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
            }
            foreach (var candidate in BuildCandidates(game, companies.Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            {
                var fullPath = Path.GetFullPath(candidate.Path).TrimEnd(Path.DirectorySeparatorChar);
                if (IsAcceptableDetectedPath(fullPath, game) &&
                    string.Equals(CreateStableId(game.AppId, fullPath), customId, StringComparison.Ordinal))
                    return fullPath;
            }
            return null;
        }

        private static string SanitizeDirectoryName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = string.Concat(value.Trim().Select(character => invalid.Contains(character) ? '_' : character)).Trim();
            // Empty, "." and ".." must never become path segments.
            if (sanitized.Length == 0 || sanitized is "." or "..")
                return string.Empty;
            var stem = sanitized.Split('.')[0];
            if (ReservedDeviceNames.Contains(stem))
                return "_" + sanitized;
            return sanitized;
        }

        private static bool IsAcceptableDetectedPath(string fullPath, SteamLibraryGame game)
        {
            try
            {
                var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                    .TrimEnd(Path.DirectorySeparatorChar);
                var driveRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar);
                // Reject the profile itself, any ancestor of the profile, and disk roots.
                if (string.IsNullOrWhiteSpace(driveRoot) ||
                    string.Equals(fullPath, driveRoot, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fullPath, profile, StringComparison.OrdinalIgnoreCase) ||
                    profile.StartsWith(fullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return false;

                var documents = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
                    .TrimEnd(Path.DirectorySeparatorChar);
                var local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                    .TrimEnd(Path.DirectorySeparatorChar);
                var roaming = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
                    .TrimEnd(Path.DirectorySeparatorChar);
                var localLow = Path.GetFullPath(Path.Combine(Directory.GetParent(local)?.FullName ?? profile, "LocalLow"))
                    .TrimEnd(Path.DirectorySeparatorChar);

                var roots = new List<string> { documents, profile, local, roaming, localLow };
                if (!string.IsNullOrWhiteSpace(game.LibraryPath))
                {
                    try
                    {
                        roots.Add(Path.GetFullPath(game.LibraryPath).TrimEnd(Path.DirectorySeparatorChar));
                    }
                    catch { /* ignore unparsable library paths */ }
                }

                return roots.Any(root =>
                    string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> SafeDirectories(string path)
        {
            try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path) : Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }
    }
}
