using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    /// <summary>
    /// Controls TranslucentTB as a high-performance, background taskbar rendering engine
    /// on Windows 11 (including 22H2 / 23H2 / 24H2).
    ///
    /// How it works:
    ///  1. Locates the TranslucentTB MSIX / Standalone installation and its RoamingState\settings.json.
    ///  2. Synchronizes SteamSwitch's taskbar settings (mode, opacity, color, dynamic states)
    ///     directly into TranslucentTB's JSON configuration in real-time.
    ///  3. Launches TranslucentTB silently without showing any UI, wizard, or popup windows.
    ///  4. Continuously suppresses unwanted popups and monitors engine health.
    ///  5. Gracefully stops TranslucentTB when disabled or when SteamSwitch exits.
    /// </summary>
    public sealed class TranslucentTbEngine : IDisposable
    {
        private const string PackagePrefix = "28017CharlesMilette.TranslucentTB";
        private const int SwHide = 0;

        private readonly object _sync = new();
        private Process? _ttbProcess;
        private Timer? _watchdog;
        private Timer? _suppressTimer;
        private bool _disposed;
        private bool _started;
        private bool _ownsProcess;
        private long _lastStartAttemptTick;

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                {
                    if (_started && _ttbProcess != null && !_ttbProcess.HasExited)
                        return true;
                    return IsAnyTranslucentTbProcessRunning();
                }
            }
        }

        /// <summary>
        /// Check if TranslucentTB is installed on the system (MSIX or Standalone).
        /// </summary>
        public static bool IsInstalled()
        {
            try
            {
                var exe = FindExePath();
                return !string.IsNullOrEmpty(exe) && File.Exists(exe);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Synchronize SteamSwitch's settings directly into TranslucentTB's settings.json.
        /// </summary>
        public void SyncConfiguration(
            TaskbarAppearanceMode desktopMode,
            int opacity,
            string hexColor,
            bool dynamicMaximized = false,
            TaskbarAppearanceMode maximizedMode = TaskbarAppearanceMode.Normal,
            bool dynamicStartMenu = false,
            TaskbarAppearanceMode startMenuMode = TaskbarAppearanceMode.Normal,
            bool dynamicSearch = false,
            TaskbarAppearanceMode searchMode = TaskbarAppearanceMode.Normal,
            bool dynamicTaskView = false,
            TaskbarAppearanceMode taskViewMode = TaskbarAppearanceMode.Normal)
        {
            try
            {
                var configPath = FindConfigPath();
                if (string.IsNullOrEmpty(configPath)) return;

                var desktopAccent = ToTtbAccent(desktopMode);
                var desktopColor = FormatTtbColor(hexColor, opacity);

                var config = new TtbSettings
                {
                    DesktopAppearance = new TtbAppearanceItem
                    {
                        Accent = desktopAccent,
                        Color = desktopColor,
                        ShowPeek = false,
                        ShowLine = false,
                        BlurRadius = 9.0
                    },
                    VisibleWindowAppearance = new TtbAppearanceItem
                    {
                        Enabled = false,
                        Accent = desktopAccent,
                        Color = desktopColor,
                        ShowPeek = true,
                        ShowLine = false,
                        BlurRadius = 9.0
                    },
                    MaximizedWindowAppearance = new TtbAppearanceItem
                    {
                        Enabled = dynamicMaximized,
                        Accent = ToTtbAccent(maximizedMode),
                        Color = FormatTtbColor(hexColor, opacity),
                        ShowPeek = true,
                        ShowLine = true,
                        BlurRadius = 9.0
                    },
                    StartOpenedAppearance = new TtbAppearanceItem
                    {
                        Enabled = dynamicStartMenu,
                        Accent = ToTtbAccent(startMenuMode),
                        Color = FormatTtbColor(hexColor, opacity),
                        ShowPeek = true,
                        ShowLine = true,
                        BlurRadius = 9.0
                    },
                    SearchOpenedAppearance = new TtbAppearanceItem
                    {
                        Enabled = dynamicSearch,
                        Accent = ToTtbAccent(searchMode),
                        Color = FormatTtbColor(hexColor, opacity),
                        ShowPeek = true,
                        ShowLine = true,
                        BlurRadius = 9.0
                    },
                    TaskViewOpenedAppearance = new TtbAppearanceItem
                    {
                        Enabled = dynamicTaskView,
                        Accent = ToTtbAccent(taskViewMode),
                        Color = FormatTtbColor(hexColor, opacity),
                        ShowPeek = false,
                        ShowLine = true,
                        BlurRadius = 9.0
                    },
                    BatterySaverAppearance = new TtbAppearanceItem
                    {
                        Enabled = false,
                        Accent = "opaque",
                        Color = "#00000000",
                        ShowPeek = true,
                        ShowLine = false,
                        BlurRadius = 9.0
                    },
                    DisableSaving = false,
                    Verbosity = "warn"
                };

                var dir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });

                File.WriteAllText(configPath, json, Encoding.UTF8);
                AppLogger.Info($"TranslucentTB config synchronized: {configPath}");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to sync TranslucentTB config: {ex.Message}");
            }
        }

        /// <summary>
        /// Start TranslucentTB engine silently in the background.
        /// </summary>
        public bool Start()
        {
            lock (_sync)
            {
                if (_disposed) return false;
                var now = Environment.TickCount64;
                if (_started && (IsRunning || now - _lastStartAttemptTick < 5000)) return true;

                try
                {
                    var existing = Process.GetProcessesByName("TranslucentTB");
                    if (existing.Length > 0)
                    {
                        _ttbProcess?.Dispose();
                        _ttbProcess = existing[0];
                        for (var index = 1; index < existing.Length; index++)
                            existing[index].Dispose();
                        _ownsProcess = false;
                        _started = true;
                        _lastStartAttemptTick = now;
                        EnsureTimers();
                        AppLogger.Info($"TranslucentTB engine attached to existing PID {_ttbProcess.Id}");
                        return true;
                    }

                    var exePath = FindExePath();
                    if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    {
                        AppLogger.Warn("TranslucentTB executable not found on system.");
                        return false;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true
                    };

                    _lastStartAttemptTick = now;
                    _ttbProcess = Process.Start(psi);
                    _ownsProcess = _ttbProcess != null;
                    _started = true;
                    EnsureTimers();
                    AppLogger.Info($"TranslucentTB engine started silently (PID {_ttbProcess?.Id})");
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to start TranslucentTB engine: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Stop TranslucentTB engine and kill background processes.
        /// </summary>
        public void Stop()
        {
            lock (_sync)
            {
                _started = false;
                _lastStartAttemptTick = 0;
                _suppressTimer?.Dispose();
                _suppressTimer = null;
                _watchdog?.Dispose();
                _watchdog = null;

                try
                {
                    if (_ownsProcess && _ttbProcess != null && !_ttbProcess.HasExited)
                    {
                        _ttbProcess.Kill(entireProcessTree: true);
                        _ttbProcess.WaitForExit(1500);
                    }
                }
                catch { }
                finally
                {
                    _ttbProcess?.Dispose();
                    _ttbProcess = null;
                    _ownsProcess = false;
                }
            }
        }

        private void EnsureTimers()
        {
            _suppressTimer ??= new Timer(SuppressTtbWindows, null,
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(200));

            _watchdog ??= new Timer(WatchdogTick, null,
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(4));
        }

        private void WatchdogTick(object? state)
        {
            lock (_sync)
            {
                if (_disposed || !_started) return;
                var running = IsAnyTranslucentTbProcessRunning();
                if (!running)
                {
                    AppLogger.Info("TranslucentTB engine not running — restarting...");
                    try
                    {
                        var exePath = FindExePath();
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            _ttbProcess?.Dispose();
                            _ttbProcess = Process.Start(new ProcessStartInfo
                            {
                                FileName = exePath,
                                UseShellExecute = true,
                                WindowStyle = ProcessWindowStyle.Hidden,
                                CreateNoWindow = true
                            });
                            _ownsProcess = _ttbProcess != null;
                        }
                    }
                    catch { }
                }
            }
        }

        private static bool IsAnyTranslucentTbProcessRunning()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("TranslucentTB");
            }
            catch
            {
                return false;
            }

            try
            {
                return processes.Any(process =>
                {
                    try { return !process.HasExited; }
                    catch { return false; }
                });
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        private void SuppressTtbWindows(object? state)
        {
            try
            {
                var processes = Process.GetProcessesByName("TranslucentTB");
                if (processes.Length == 0) return;

                var pids = processes.Select(p => p.Id).ToHashSet();
                foreach (var proc in processes) proc.Dispose();

                EnumWindows((hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pids.Contains((int)pid) && IsWindowVisible(hwnd))
                    {
                        ShowWindow(hwnd, SwHide);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        public static string? FindExePath()
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // 1. Check WindowsApps Execution Alias Directory
            var winAppsDir = Path.Combine(localApp, "Microsoft", "WindowsApps");
            if (Directory.Exists(winAppsDir))
            {
                // Check subdirectories matching package prefix
                foreach (var dir in Directory.GetDirectories(winAppsDir, PackagePrefix + "*"))
                {
                    var ttbExe = Path.Combine(dir, "ttb.exe");
                    if (File.Exists(ttbExe)) return ttbExe;

                    var mainExe = Path.Combine(dir, "TranslucentTB.exe");
                    if (File.Exists(mainExe)) return mainExe;
                }

                var directTtb = Path.Combine(winAppsDir, "ttb.exe");
                if (File.Exists(directTtb)) return directTtb;

                var directMain = Path.Combine(winAppsDir, "TranslucentTB.exe");
                if (File.Exists(directMain)) return directMain;
            }

            // 2. Check Standalone Install Directory
            var standalone = Path.Combine(localApp, "Programs", "TranslucentTB", "TranslucentTB.exe");
            if (File.Exists(standalone)) return standalone;

            // 3. Check WindowsApps in Program Files
            var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var progWinApps = Path.Combine(progFiles, "WindowsApps");
            if (Directory.Exists(progWinApps))
            {
                foreach (var dir in Directory.GetDirectories(progWinApps, PackagePrefix + "*"))
                {
                    var exe = Path.Combine(dir, "TranslucentTB.exe");
                    if (File.Exists(exe)) return exe;
                }
            }

            return null;
        }

        public static string? FindConfigPath()
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var packagesDir = Path.Combine(localApp, "Packages");
            if (Directory.Exists(packagesDir))
            {
                foreach (var dir in Directory.GetDirectories(packagesDir, PackagePrefix + "*"))
                {
                    return Path.Combine(dir, "RoamingState", "settings.json");
                }
            }
            return null;
        }

        private static string ToTtbAccent(TaskbarAppearanceMode mode) => mode switch
        {
            TaskbarAppearanceMode.Normal => "normal",
            TaskbarAppearanceMode.Opaque => "opaque",
            TaskbarAppearanceMode.Clear => "clear",
            TaskbarAppearanceMode.Blur => "blur",
            TaskbarAppearanceMode.Acrylic => "acrylic",
            _ => "clear"
        };

        private static string FormatTtbColor(string hexColor, int opacity)
        {
            if (string.IsNullOrWhiteSpace(hexColor)) hexColor = "#000000";
            hexColor = hexColor.Trim().TrimStart('#');
            if (hexColor.Length < 6) hexColor = "000000";

            var alpha = Math.Clamp((int)Math.Round(opacity * 255.0 / 100.0), 0, 255);
            return $"#{alpha:X2}{hexColor.Substring(0, 6)}";
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                Stop();
            }
        }

        // ── Settings Schema DTOs ──────────────────────────────────

        private sealed class TtbSettings
        {
            [JsonPropertyName("desktop_appearance")]
            public TtbAppearanceItem? DesktopAppearance { get; set; }

            [JsonPropertyName("visible_window_appearance")]
            public TtbAppearanceItem? VisibleWindowAppearance { get; set; }

            [JsonPropertyName("maximized_window_appearance")]
            public TtbAppearanceItem? MaximizedWindowAppearance { get; set; }

            [JsonPropertyName("start_opened_appearance")]
            public TtbAppearanceItem? StartOpenedAppearance { get; set; }

            [JsonPropertyName("search_opened_appearance")]
            public TtbAppearanceItem? SearchOpenedAppearance { get; set; }

            [JsonPropertyName("task_view_opened_appearance")]
            public TtbAppearanceItem? TaskViewOpenedAppearance { get; set; }

            [JsonPropertyName("battery_saver_appearance")]
            public TtbAppearanceItem? BatterySaverAppearance { get; set; }

            [JsonPropertyName("disable_saving")]
            public bool DisableSaving { get; set; }

            [JsonPropertyName("verbosity")]
            public string Verbosity { get; set; } = "warn";
        }

        private sealed class TtbAppearanceItem
        {
            [JsonPropertyName("enabled")]
            public bool Enabled { get; set; } = true;

            [JsonPropertyName("accent")]
            public string? Accent { get; set; }

            [JsonPropertyName("color")]
            public string? Color { get; set; }

            [JsonPropertyName("show_peek")]
            public bool ShowPeek { get; set; }

            [JsonPropertyName("show_line")]
            public bool ShowLine { get; set; }

            [JsonPropertyName("blur_radius")]
            public double BlurRadius { get; set; } = 9.0;
        }

        // ── P/Invoke ──────────────────────────────────────────────

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    }
}
