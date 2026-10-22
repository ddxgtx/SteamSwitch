using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SteamSwitcher.Models;

namespace SteamSwitcher.Core
{
    public static class GameShortcutService
    {
        public static string CreateDesktopShortcut(SteamLibraryGame game, string steamPath)
        {
            return CreateShortcut(
                game,
                steamPath,
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        }

        public static string CreateShortcut(SteamLibraryGame game, string steamPath, string destinationDirectory)
        {
            ArgumentNullException.ThrowIfNull(game);
            if (game.AppId <= 0)
                throw new InvalidDataException("游戏缺少有效的 AppID。");

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new FileNotFoundException("无法确定 Steam Switch 程序路径。", executable);

            Directory.CreateDirectory(destinationDirectory);
            var shortcutName = $"{ShortcutHelper.SanitizeFileName(game.Name, "Steam Game")}.lnk";
            var arguments = $"--launch-game {game.AppId}";
            var shortcutPath = ShortcutHelper.FindExistingShortcut(destinationDirectory, shortcutName, executable, arguments)
                ?? ShortcutHelper.GetUniquePath(destinationDirectory, shortcutName);
            var iconPath = ResolveGameIcon(game, steamPath) ?? executable;

            ShortcutHelper.SaveShortcut(
                shortcutPath,
                executable,
                arguments,
                iconPath,
                $"使用 Steam Switch 切换绑定账号并启动 {game.Name}");
            return shortcutPath;
        }

        private static string? ResolveGameIcon(SteamLibraryGame game, string steamPath)
        {
            var directCandidates = new[]
            {
                game.IconPath ?? string.Empty,
                Path.Combine(steamPath, "steam", "games", $"{game.AppId}.ico"),
                Path.Combine(steamPath, "steam", "games", $"{game.AppId}_icon.ico"),
                Path.Combine(steamPath, "appcache", "librarycache", $"{game.AppId}_icon.jpg"),
                Path.Combine(steamPath, "appcache", "librarycache", $"{game.AppId}_icon.png")
            };

            foreach (var candidate in directCandidates.Where(File.Exists))
            {
                var icon = ShortcutIconService.GetOrCreateIcon(candidate, $"game-{game.AppId}");
                if (!string.IsNullOrWhiteSpace(icon))
                    return icon;
            }

            try
            {
                var appCacheDirectory = Path.Combine(steamPath, "appcache", "librarycache", game.AppId.ToString());
                if (Directory.Exists(appCacheDirectory))
                {
                    var cachedLogo = Directory.EnumerateFiles(appCacheDirectory)
                        .Where(path =>
                        {
                            var extension = Path.GetExtension(path);
                            return extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
                                   extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                                   extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                   extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
                        })
                        .OrderByDescending(path => Path.GetFileName(path).Contains("icon", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(path => new FileInfo(path).Length)
                        .FirstOrDefault();

                    var cachedIcon = ShortcutIconService.GetOrCreateIcon(cachedLogo, $"game-{game.AppId}");
                    if (!string.IsNullOrWhiteSpace(cachedIcon))
                        return cachedIcon;
                }

                var installRoot = Path.Combine(game.LibraryPath, "steamapps", "common", game.InstallDir);
                return Directory.Exists(installRoot)
                    ? Directory.EnumerateFiles(installRoot, "*.exe", SearchOption.TopDirectoryOnly)
                        .Where(path => !Path.GetFileName(path).Contains("unins", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(path => new FileInfo(path).Length)
                        .FirstOrDefault()
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
