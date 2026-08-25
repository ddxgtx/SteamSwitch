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

        private static string SanitizeDirectoryName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(value.Trim().Select(character => invalid.Contains(character) ? '_' : character));
        }

        private static IEnumerable<string> SafeDirectories(string path)
        {
            try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path) : Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }
    }
}
