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
            var shortcutPath = GetUniquePath(destinationDirectory, $"{SanitizeFileName(game.Name)}.lnk");
            var iconPath = ResolveGameIcon(game, steamPath) ?? executable;

            object? shell = null;
            object? shortcut = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("系统未提供 Windows Script Host。");
                shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("无法创建快捷方式服务。");
                dynamic dynamicShell = shell;
                shortcut = dynamicShell.CreateShortcut(shortcutPath);
                dynamic dynamicShortcut = shortcut;
                dynamicShortcut.TargetPath = executable;
                dynamicShortcut.Arguments = $"--launch-game {game.AppId}";
                dynamicShortcut.WorkingDirectory = AppContext.BaseDirectory;
                dynamicShortcut.IconLocation = $"{iconPath},0";
                dynamicShortcut.Description = $"使用 Steam Switch 切换绑定账号并启动 {game.Name}";
                dynamicShortcut.Save();
                return shortcutPath;
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
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
        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var result = string.Concat(value.Trim().Select(character => invalid.Contains(character) ? '_' : character));
            return string.IsNullOrWhiteSpace(result) ? "Steam Game" : result;
        }

        private static string GetUniquePath(string directory, string fileName)
        {
            var candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate)) return candidate;
            var name = Path.GetFileNameWithoutExtension(fileName);
            for (var index = 2; index < 1000; index++)
            {
                candidate = Path.Combine(directory, $"{name} ({index}).lnk");
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}).lnk");
        }
    }
}
