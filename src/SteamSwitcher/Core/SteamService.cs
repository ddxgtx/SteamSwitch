using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    public class SteamService
    {
        private readonly RegistryHelper _registryHelper = new();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_MINIMIZE = 6;

        public string? SteamPath { get; private set; }
        public string? SteamExePath { get; private set; }

        public bool DetectSteamPath()
        {
            SteamPath = _registryHelper.GetSteamPath();
            SteamExePath = _registryHelper.GetSteamExePath();

            if (!string.IsNullOrEmpty(SteamPath) && Directory.Exists(SteamPath))
            {
                AppLogger.Info($"Detected Steam path from registry: {SteamPath}");
                return true;
            }

            var commonPaths = new[]
            {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam",
                @"D:\Steam",
                @"D:\Program Files (x86)\Steam",
                @"E:\Steam"
            };

            foreach (var path in commonPaths)
            {
                if (Directory.Exists(path))
                {
                    SteamPath = path;
                    SteamExePath = Path.Combine(path, "steam.exe");
                    AppLogger.Info($"Detected Steam path from fallback path: {SteamPath}");
                    return true;
                }
            }

            AppLogger.Info("Steam path was not detected.");
            return false;
        }

        public bool IsSteamRunning()
        {
            return Process.GetProcessesByName("steam").Length > 0 ||
                   Process.GetProcessesByName("steamwebhelper").Length > 0;
        }

        public async Task<bool> CloseSteamAsync()
        {
            if (!IsSteamRunning())
            {
                AppLogger.Info("CloseSteamAsync skipped: Steam is not running.");
                return true;
            }

            try
            {
                AppLogger.Info("Closing Steam.");

                try
                {
                    if (!string.IsNullOrEmpty(SteamExePath) && File.Exists(SteamExePath))
                    {
                        AppLogger.Info($"Starting shutdown command: {SteamExePath} -shutdown");
                        Process.Start(SteamExePath, "-shutdown");
                        await Task.Delay(3000);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Steam -shutdown failed.", ex);
                }

                if (IsSteamRunning())
                {
                    var processes = Process.GetProcessesByName("steam")
                        .Concat(Process.GetProcessesByName("steamwebhelper"));
                    foreach (var process in processes)
                    {
                        try
                        {
                            AppLogger.Info($"Killing Steam process: {process.Id}");
                            process.Kill();
                            await process.WaitForExitAsync();
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error($"Failed to kill Steam process {process.Id}.", ex);
                        }
                    }
                    await Task.Delay(2000);
                }

                var closed = !IsSteamRunning();
                AppLogger.Info($"CloseSteamAsync result: {closed}");
                return closed;
            }
            catch (Exception ex)
            {
                AppLogger.Error("CloseSteamAsync failed.", ex);
                return false;
            }
        }

        public bool StartSteam(string? username = null, bool silent = false, bool enableDebugging = false)
        {
            if (string.IsNullOrEmpty(SteamExePath) || !File.Exists(SteamExePath))
            {
                AppLogger.Info($"StartSteam skipped: steam.exe not found. SteamExePath={SteamExePath}");
                return false;
            }

            try
            {
                var argsList = new List<string>();

                if (!string.IsNullOrEmpty(username))
                    argsList.Add($"-login {username}");

                if (silent)
                    argsList.Add("-silent");

                if (enableDebugging)
                {
                    argsList.Add("-dev");
                    argsList.Add("-cef-enable-debugging");
                    argsList.Add("-devtools-port 8080");
                }

                var args = string.Join(" ", argsList);
                AppLogger.Info($"Starting Steam: {SteamExePath} {args}");
                Process.Start(SteamExePath, args);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("StartSteam failed.", ex);
                return false;
            }
        }

        public bool StartSteamWithDebugging(string? username = null)
        {
            return StartSteam(username, enableDebugging: true);
        }

        public bool StartGame(int appId)
        {
            if (appId <= 0)
                return false;

            try
            {
                AppLogger.Info($"Starting game via protocol: steam://run/{appId}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://run/{appId}",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception protocolEx)
            {
                AppLogger.Error($"StartGame protocol launch failed for appId={appId}.", protocolEx);

                if (string.IsNullOrEmpty(SteamExePath) || !File.Exists(SteamExePath))
                    return false;

                try
                {
                    AppLogger.Info($"Starting game via steam.exe fallback: {appId}");
                    Process.Start(SteamExePath, $"-silent steam://run/{appId}");
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"StartGame fallback failed for appId={appId}.", ex);
                    return false;
                }
            }
        }

        public string GetLoginUsersPath()
        {
            return Path.Combine(SteamPath ?? "", "config", "loginusers.vdf");
        }

        public string GetAvatarCachePath()
        {
            return Path.Combine(SteamPath ?? "", "config", "avatarcache");
        }

        public string GetConfigPath()
        {
            return Path.Combine(SteamPath ?? "", "config");
        }

        public string? GetGameIconPath(int appId)
        {
            if (string.IsNullOrEmpty(SteamPath))
                return null;

            var libraryCache = Path.Combine(SteamPath, "appcache", "librarycache");
            var steamGames = Path.Combine(SteamPath, "steam", "games");
            var candidates = new[]
            {
                Path.Combine(libraryCache, $"{appId}_icon.jpg"),
                Path.Combine(libraryCache, $"{appId}_icon.png"),
                Path.Combine(libraryCache, $"{appId}_logo.png"),
                Path.Combine(libraryCache, $"{appId}_capsule_184x69.jpg"),
                Path.Combine(libraryCache, $"{appId}_header.jpg"),
                Path.Combine(libraryCache, $"{appId}_library_600x900.jpg"),
                Path.Combine(steamGames, $"{appId}.ico"),
                Path.Combine(steamGames, $"{appId}_icon.ico"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            var wildcardMatch = FindBestGameImage(libraryCache, $"{appId}_*.*")
                ?? FindBestGameImage(Path.Combine(libraryCache, appId.ToString()), "*.*")
                ?? FindBestGameImage(steamGames, $"{appId}*.*");
            if (!string.IsNullOrEmpty(wildcardMatch))
                return wildcardMatch;

            return null;
        }

        public List<SteamLibraryGame> GetInstalledGames()
        {
            var games = new List<SteamLibraryGame>();
            if (string.IsNullOrEmpty(SteamPath))
                return games;

            var libraryPaths = GetLibraryFolders();
            foreach (var libPath in libraryPaths)
            {
                var steamAppsPath = Path.Combine(libPath, "steamapps");
                if (!Directory.Exists(steamAppsPath))
                    continue;

                foreach (var manifestFile in Directory.GetFiles(steamAppsPath, "appmanifest_*.acf"))
                {
                    try
                    {
                        var game = ParseAppManifest(manifestFile, libPath);
                        if (game != null)
                            games.Add(game);
                    }
                    catch
                    {
                    }
                }
            }

            return games.OrderBy(g => g.Name).ToList();
        }

        private List<string> GetLibraryFolders()
        {
            var folders = new List<string>();
            if (string.IsNullOrEmpty(SteamPath))
                return folders;

            var mainLibrary = Path.Combine(SteamPath, "steamapps");
            if (!Directory.Exists(mainLibrary))
                return folders;

            folders.Add(SteamPath);

            var vdfPath = Path.Combine(mainLibrary, "libraryfolders.vdf");
            if (!File.Exists(vdfPath))
                return folders;

            try
            {
                var parser = new VdfParser();
                var vdfData = parser.Parse(vdfPath);

                if (vdfData.TryGetValue("libraryfolders", out var libFolders) &&
                    libFolders is Dictionary<string, object> root)
                {
                    foreach (var kvp in root)
                    {
                        if (kvp.Value is Dictionary<string, object> folderEntry &&
                            folderEntry.TryGetValue("path", out var pathObj) &&
                            pathObj is string path &&
                            Directory.Exists(path))
                        {
                            if (!folders.Contains(path, StringComparer.OrdinalIgnoreCase))
                                folders.Add(path);
                        }
                    }
                }
            }
            catch
            {
            }

            return folders;
        }

        private SteamLibraryGame? ParseAppManifest(string manifestPath, string libraryPath)
        {
            try
            {
                var parser = new VdfParser();
                var data = parser.Parse(manifestPath);

                if (!data.TryGetValue("AppState", out var appStateObj) ||
                    appStateObj is not Dictionary<string, object> appState)
                    return null;

                if (!appState.TryGetValue("appid", out var appIdObj) ||
                    appIdObj is not string appIdStr ||
                    !int.TryParse(appIdStr, out int appId))
                    return null;

                var name = appState.TryGetValue("name", out var nameObj) && nameObj is string n
                    ? n
                    : Path.GetFileNameWithoutExtension(manifestPath).Replace("appmanifest_", "");

                var installDir = appState.TryGetValue("installdir", out var dirObj) && dirObj is string d
                    ? d
                    : "";

                long sizeOnDisk = 0;
                if (appState.TryGetValue("SizeOnDisk", out var sizeObj) && sizeObj is string sizeStr &&
                    long.TryParse(sizeStr, out long parsed))
                {
                    sizeOnDisk = parsed;
                }

                return new SteamLibraryGame
                {
                    AppId = appId,
                    Name = name,
                    InstallDir = installDir,
                    LibraryPath = libraryPath,
                    IconPath = ResolveManifestIconPath(appState, appId),
                    SizeOnDisk = sizeOnDisk
                };
            }
            catch
            {
                return null;
            }
        }

        private string? ResolveManifestIconPath(Dictionary<string, object> appState, int appId)
        {
            if (!string.IsNullOrEmpty(SteamPath))
            {
                var steamGames = Path.Combine(SteamPath, "steam", "games");
                foreach (var key in new[] { "clienticon", "icon" })
                {
                    if (appState.TryGetValue(key, out var value) && value is string hash &&
                        !string.IsNullOrWhiteSpace(hash))
                    {
                        foreach (var extension in new[] { ".ico", ".png", ".jpg", ".jpeg" })
                        {
                            var candidate = Path.Combine(steamGames, hash + extension);
                            if (File.Exists(candidate))
                                return candidate;
                        }
                    }
                }
            }

            return GetGameIconPath(appId);
        }

        private static string? FindBestGameImage(string directory, string pattern)
        {
            if (!Directory.Exists(directory))
                return null;

            try
            {
                return Directory.EnumerateFiles(directory, pattern)
                    .Where(IsSupportedImage)
                    .OrderBy(GetGameImagePriority)
                    .ThenBy(path => path.Length)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSupportedImage(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension is ".jpg" or ".jpeg" or ".png" or ".ico" or ".bmp";
        }

        private static int GetGameImagePriority(string path)
        {
            var fileName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (fileName.Contains("_icon") || fileName.Contains("clienticon"))
                return 0;
            if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (fileName.Contains("_logo"))
                return 2;
            if (fileName.Contains("_capsule"))
                return 3;
            if (fileName.Contains("_header"))
                return 4;
            if (fileName.Contains("_library"))
                return 5;
            return 10;
        }
    }
}
