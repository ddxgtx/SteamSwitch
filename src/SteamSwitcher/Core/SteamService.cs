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

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_HIDE = 0;
        private const int SW_MINIMIZE = 6;
        private const int SW_SHOW = 5;

        public string? SteamPath { get; private set; }
        public string? SteamExePath { get; private set; }

        public string? CustomSteamPath { get; set; }
        public string? CustomLoginUsersPath { get; set; }

        public void SetCustomPaths(string? customSteamPath, string? customLoginUsersPath)
        {
            CustomSteamPath = customSteamPath;
            CustomLoginUsersPath = customLoginUsersPath;
            DetectSteamPath();
        }

        public bool DetectSteamPath()
        {
            if (!string.IsNullOrEmpty(CustomSteamPath) && Directory.Exists(CustomSteamPath))
            {
                SteamPath = CustomSteamPath;
                SteamExePath = Path.Combine(SteamPath, "steam.exe");
                AppLogger.Info($"Detected Steam path from custom path: {SteamPath}");
                return true;
            }

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
            var steamProcs = Process.GetProcessesByName("steam");
            var webProcs = Process.GetProcessesByName("steamwebhelper");
            try
            {
                return steamProcs.Length > 0 || webProcs.Length > 0;
            }
            finally
            {
                foreach (var p in steamProcs) p.Dispose();
                foreach (var p in webProcs) p.Dispose();
            }
        }

        public async Task<bool> CloseSteamAsync(bool silent = false)
        {
            if (!IsSteamRunning())
            {
                AppLogger.Info("CloseSteamAsync skipped: Steam is not running.");
                return true;
            }

            try
            {
                AppLogger.Info($"Closing Steam. Silent={silent}");

                if (silent)
                {
                    // Silent mode: hide windows first, then force kill
                    AppLogger.Info("Silent mode: hiding windows and force killing processes.");

                    // Hide windows FIRST before killing
                    HideSteamWindows();
                    await Task.Delay(50);
                    HideSteamWindows();

                    // Force kill using Process.Kill with WaitForExitAsync
                    var steamProcs = Process.GetProcessesByName("steam");
                    var webProcs = Process.GetProcessesByName("steamwebhelper");
                    var processes = steamProcs.Concat(webProcs).ToList();

                    foreach (var process in processes)
                    {
                        try
                        {
                            AppLogger.Info($"Killing Steam process: {process.Id}");
                            process.Kill();
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error($"Failed to kill Steam process {process.Id}.", ex);
                        }
                    }

                    // Wait for all processes to exit
                    foreach (var process in processes)
                    {
                        try { await process.WaitForExitAsync(); } catch { }
                    }

                    // Dispose all process objects
                    foreach (var process in processes) process.Dispose();

                    // Hide any remaining windows
                    HideSteamWindows();
                    await Task.Delay(100);
                    HideSteamWindows();

                    // Kill any remaining processes
                    var remainingSteam = Process.GetProcessesByName("steam");
                    var remainingWeb = Process.GetProcessesByName("steamwebhelper");
                    var remaining = remainingSteam.Concat(remainingWeb).ToList();
                    foreach (var process in remaining)
                    {
                        try
                        {
                            process.Kill();
                            await process.WaitForExitAsync();
                        }
                        catch { }
                        finally { process.Dispose(); }
                    }

                    // Final hide
                    HideSteamWindows();
                }
                else
                {
                    // Graceful mode: hide, try -shutdown, then force kill if needed
                    HideSteamWindows();
                    await Task.Delay(200);

                    try
                    {
                        if (!string.IsNullOrEmpty(SteamExePath) && File.Exists(SteamExePath))
                        {
                            AppLogger.Info($"Starting shutdown command: {SteamExePath} -shutdown");
                            var shutdownProc = Process.Start(SteamExePath, "-shutdown");
                            shutdownProc?.Dispose();
                            await Task.Delay(3000);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Steam -shutdown failed.", ex);
                    }

                    if (IsSteamRunning())
                    {
                        HideSteamWindows();
                        var steamProcs = Process.GetProcessesByName("steam");
                        var webProcs = Process.GetProcessesByName("steamwebhelper");
                        var processes = steamProcs.Concat(webProcs).ToList();
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
                            finally { process.Dispose(); }
                        }
                        HideSteamWindows();
                        await Task.Delay(2000);
                    }
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

        private void HideSteamWindows()
        {
            var steamProcesses = Process.GetProcessesByName("steam");
            var steamWebHelperProcesses = Process.GetProcessesByName("steamwebhelper");
            try
            {
                var allProcesses = steamProcesses.Concat(steamWebHelperProcesses).ToList();

                if (allProcesses.Count == 0)
                    return;

                var processIds = new HashSet<uint>();
                foreach (var proc in allProcesses)
                {
                    try
                    {
                        processIds.Add((uint)proc.Id);
                    }
                    catch { }
                }

                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hWnd))
                            return true;

                        GetWindowThreadProcessId(hWnd, out uint processId);
                        if (processIds.Contains(processId))
                        {
                            // Minimize first, then hide - more reliable
                            ShowWindow(hWnd, SW_MINIMIZE);
                            ShowWindow(hWnd, SW_HIDE);
                            AppLogger.Info($"Hidden Steam window: hWnd={hWnd}, processId={processId}");
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                AppLogger.Error("HideSteamWindows failed.", ex);
            }
            finally
            {
                foreach (var p in steamProcesses) p.Dispose();
                foreach (var p in steamWebHelperProcesses) p.Dispose();
            }
        }

        public bool StartSteam(string? username = null, bool silent = false, bool enableDebugging = false, int debugPort = 8080)
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
                    argsList.Add($"-devtools-port {debugPort}");
                }

                var args = string.Join(" ", argsList);
                AppLogger.Info($"Starting Steam: {SteamExePath} {args}");
                var proc = Process.Start(SteamExePath, args);
                proc?.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("StartSteam failed.", ex);
                return false;
            }
        }

        public bool StartSteamWithDebugging(string? username = null, int debugPort = 8080)
        {
            return StartSteam(username, enableDebugging: true, debugPort: debugPort);
        }

        public bool StartGame(int appId, bool silent = false)
        {
            if (appId <= 0)
                return false;

            try
            {
                if (!string.IsNullOrEmpty(SteamExePath) && File.Exists(SteamExePath))
                {
                    var args = silent ? $"-silent steam://run/{appId}" : $"steam://run/{appId}";
                    AppLogger.Info($"Starting game via steam.exe. Silent={silent}, appId={appId}");
                    var proc = Process.Start(SteamExePath, args);
                    proc?.Dispose();
                    return true;
                }

                AppLogger.Info($"Starting game via protocol: steam://run/{appId}");
                var fallbackProc = Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://run/{appId}",
                    UseShellExecute = true
                });
                fallbackProc?.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"StartGame failed for appId={appId}.", ex);
                return false;
            }
        }

        public string GetLoginUsersPath()
        {
            if (!string.IsNullOrEmpty(CustomLoginUsersPath) && File.Exists(CustomLoginUsersPath))
            {
                return CustomLoginUsersPath;
            }
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
            var appCacheDir = Path.Combine(libraryCache, appId.ToString());

            // Prefer hash-named icon files (small app icons like 8dbc7195...jpg)
            if (Directory.Exists(appCacheDir))
            {
                var hashFiles = Directory.GetFiles(appCacheDir)
                    .Where(f => IsSupportedImage(f) && Path.GetFileNameWithoutExtension(f).Length >= 20)
                    .OrderBy(f => new FileInfo(f).Length)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(hashFiles))
                    return hashFiles;
            }

            // Fall back to old-style flat files
            var steamGames = Path.Combine(SteamPath, "steam", "games");
            var flatCandidates = new[]
            {
                Path.Combine(libraryCache, $"{appId}_icon.png"),
                Path.Combine(libraryCache, $"{appId}_icon.jpg"),
                Path.Combine(steamGames, $"{appId}.ico"),
                Path.Combine(steamGames, $"{appId}_icon.ico"),
            };

            foreach (var candidate in flatCandidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

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

            // 规范化主路径
            var normalizedMainPath = Path.GetFullPath(SteamPath).TrimEnd(Path.DirectorySeparatorChar);
            folders.Add(normalizedMainPath);

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
                            // 规范化路径并检查是否已存在
                            var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                            if (!folders.Any(f => string.Equals(f, normalizedPath, StringComparison.OrdinalIgnoreCase)))
                                folders.Add(normalizedPath);
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
            // Use the icon from library cache
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
            if (fileName.Contains("_library"))
                return 0;
            if (fileName.Contains("_header"))
                return 1;
            if (fileName.Contains("_capsule"))
                return 2;
            if (fileName.Contains("_logo"))
                return 3;
            if (fileName.Contains("_icon") || fileName.Contains("clienticon"))
                return 4;
            if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
                return 5;
            return 10;
        }
    }
}
