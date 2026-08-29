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
        private static readonly string[] SteamProcessNames =
        {
            "steam",
            "steamwebhelper",
            "gameoverlayui"
        };

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
            if (string.IsNullOrWhiteSpace(SteamPath))
                DetectSteamPath();
            var processes = GetSteamProcesses();
            try
            {
                return processes.Count > 0;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        public async Task<bool> CloseSteamAsync(bool forceClose = false)
        {
            if (!IsSteamRunning())
            {
                AppLogger.Info("CloseSteamAsync skipped: Steam is not running.");
                return true;
            }

            try
            {
                AppLogger.Info($"Closing Steam. ForceClose={forceClose}");

                if (forceClose)
                {
                    AppLogger.Info("Force-close mode: hiding windows and terminating Steam process trees.");

                    HideSteamWindows();
                    await Task.Delay(50);
                    HideSteamWindows();
                    await ForceKillSteamProcessesAsync();
                    await Task.Delay(100);

                    if (IsSteamRunning())
                        await ForceKillSteamProcessesAsync();

                    HideSteamWindows();
                }
                else
                {
                    // Graceful mode deliberately never escalates to termination.
                    // Steam Cloud and the client may need time to flush data.
                    if (string.IsNullOrEmpty(SteamExePath) || !File.Exists(SteamExePath))
                    {
                        AppLogger.Warn("Graceful Steam shutdown is unavailable because steam.exe was not found.");
                        return false;
                    }

                    AppLogger.Info($"Starting graceful shutdown command: {SteamExePath} -shutdown");
                    var shutdownProc = Process.Start(SteamExePath, "-shutdown");
                    shutdownProc?.Dispose();
                    var exitedGracefully = await WaitForSteamExitAsync(TimeSpan.FromSeconds(20));
                    if (!exitedGracefully)
                        AppLogger.Warn("Steam did not exit within the 20-second graceful shutdown window.");
                    return exitedGracefully;
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

        private async Task<bool> WaitForSteamExitAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsSteamRunning())
                    return true;
                await Task.Delay(500);
            }
            return !IsSteamRunning();
        }

        private void HideSteamWindows()
        {
            var processes = GetSteamProcesses();
            try
            {
                if (processes.Count == 0)
                    return;

                var processIds = new HashSet<uint>();
                foreach (var proc in processes)
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
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        private List<Process> GetSteamProcesses()
        {
            var processes = new List<Process>();
            foreach (var processName in SteamProcessNames)
            {
                try
                {
                    foreach (var process in Process.GetProcessesByName(processName))
                    {
                        if (IsProcessFromSteamInstallation(process))
                            processes.Add(process);
                        else
                            process.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to enumerate {processName} processes: {ex.Message}");
                }
            }
            return processes;
        }

        private bool IsProcessFromSteamInstallation(Process process)
        {
            if (string.IsNullOrWhiteSpace(SteamPath))
                return false;

            try
            {
                var executablePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executablePath)) return false;
                var steamRoot = Path.GetFullPath(SteamPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var fullExecutablePath = Path.GetFullPath(executablePath);
                return fullExecutablePath.StartsWith(steamRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Ignored an unverifiable Steam-named process: {ex.Message}");
                return false;
            }
        }

        private async Task ForceKillSteamProcessesAsync()
        {
            var processes = GetSteamProcesses();
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.HasExited)
                            continue;

                        AppLogger.Info($"Terminating Steam process tree: {process.ProcessName} ({process.Id})");
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"Failed to terminate Steam process {process.Id}.", ex);
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
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
                if (username?.Any(char.IsControl) == true || username?.Length > 128)
                    throw new ArgumentException("Steam 用户名包含无效字符或长度异常。", nameof(username));
                if (enableDebugging && debugPort is <= 0 or > 65535)
                    throw new ArgumentOutOfRangeException(nameof(debugPort));

                var startInfo = new ProcessStartInfo
                {
                    FileName = SteamExePath,
                    UseShellExecute = false
                };
                if (!string.IsNullOrWhiteSpace(username))
                {
                    startInfo.ArgumentList.Add("-login");
                    startInfo.ArgumentList.Add(username.Trim());
                }
                if (silent) startInfo.ArgumentList.Add("-silent");
                if (enableDebugging)
                {
                    startInfo.ArgumentList.Add("-dev");
                    startInfo.ArgumentList.Add("-cef-enable-debugging");
                    startInfo.ArgumentList.Add("-devtools-port");
                    startInfo.ArgumentList.Add(debugPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                AppLogger.Info($"Starting Steam: {SteamExePath}; Silent={silent}; Debugging={enableDebugging}");
                var proc = Process.Start(startInfo);
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

        public bool StartGame(
            int appId,
            bool silent = false,
            string? launchArguments = null,
            bool startInBigPicture = false)
        {
            if (appId <= 0)
                return false;

            try
            {
                if (!string.IsNullOrEmpty(SteamExePath) && File.Exists(SteamExePath))
                {
                    if (launchArguments?.Length > 2048 || launchArguments?.Any(char.IsControl) == true)
                        throw new ArgumentException("启动参数包含无效字符或超过 2048 个字符。", nameof(launchArguments));
                    var launchUri = string.IsNullOrWhiteSpace(launchArguments)
                        ? $"steam://run/{appId}"
                        : $"steam://run/{appId}//{Uri.EscapeDataString(launchArguments.Trim())}/";
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = SteamExePath,
                        UseShellExecute = false
                    };
                    if (silent) startInfo.ArgumentList.Add("-silent");
                    if (startInBigPicture) startInfo.ArgumentList.Add("-bigpicture");
                    startInfo.ArgumentList.Add(launchUri);
                    AppLogger.Info($"Starting game via steam.exe. Silent={silent}, BigPicture={startInBigPicture}, appId={appId}");
                    var proc = Process.Start(startInfo);
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
            if (!string.IsNullOrWhiteSpace(SteamPath))
            {
                var appCacheDir = Path.Combine(SteamPath, "appcache", "librarycache", appId.ToString());
                foreach (var key in new[] { "clienticon", "icon" })
                {
                    if (!appState.TryGetValue(key, out var value) || value is not string hash ||
                        string.IsNullOrWhiteSpace(hash))
                        continue;

                    var directCandidates = new[]
                    {
                        Path.Combine(SteamPath, "steam", "games", $"{hash}.ico"),
                        Path.Combine(appCacheDir, $"{hash}.jpg"),
                        Path.Combine(appCacheDir, $"{hash}.png"),
                        Path.Combine(appCacheDir, $"{hash}.ico")
                    };
                    var direct = directCandidates.FirstOrDefault(File.Exists);
                    if (direct != null)
                        return direct;

                    if (Directory.Exists(appCacheDir))
                    {
                        var matching = Directory.EnumerateFiles(appCacheDir)
                            .FirstOrDefault(path => IsSupportedImage(path) &&
                                Path.GetFileNameWithoutExtension(path)
                                    .Contains(hash, StringComparison.OrdinalIgnoreCase));
                        if (matching != null)
                            return matching;
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
