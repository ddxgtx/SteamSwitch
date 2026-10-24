using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using SteamSwitcher.Core;
using SteamSwitcher.Models;

namespace SteamSwitcher.Services
{
    public class AppSettings
    {
        public bool AutoStartSteam { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public bool StartWithWindows { get; set; }
        public bool PlaySoundOnSwitch { get; set; } = true;
        public bool ShowSwitchNotification { get; set; } = true;
        public string BackupExportDirectory { get; set; } = "";
        public bool AskBeforeBackupExport { get; set; }
        public bool TaskbarPinned { get; set; }
        public TaskbarPosition TaskbarPosition { get; set; } = TaskbarPosition.Right;
        public int TaskbarMonitorIndex { get; set; }
        public int TaskbarOffsetX { get; set; }
        public int TaskbarOffsetY { get; set; }
        public int TaskbarWindowSize { get; set; } = 2;
        public int TaskbarAvatarSize { get; set; } = 38;
        public bool TaskbarGlassEnabled { get; set; } = true;
        public bool TaskbarRoundedMode { get; set; } = true;
        public DesktopEnhanceSettings DesktopEnhance { get; set; } = new();
        public bool DesktopIconsHiddenByApp { get; set; }
        public bool TaskbarLyricsEnabled { get; set; }
        public bool TaskbarLyricsShowControls { get; set; } = true;
        public bool TaskbarLyricsOnlineEnabled { get; set; } = true;
        public bool TaskbarLyricsArtworkRounded { get; set; } = true;
        public bool TaskbarLyricsShowVisualizer { get; set; } = true;
        public bool TaskbarLyricsTransparentBackground { get; set; } = true;
        public bool TaskbarLyricsAdaptiveWidth { get; set; } = true;
        public int TaskbarLyricsWidth { get; set; } = 320;
        public int TaskbarLyricsFontSize { get; set; } = 14;
        public string TaskbarLyricsFontFamily { get; set; } = "Microsoft YaHei UI";
        public bool TaskbarLyricsTrackInfoOnly { get; set; }
        public bool TaskbarLyricsShowSecondaryLine { get; set; } = true;
        public bool TaskbarLyricsShowProgressBar { get; set; } = true;
        public bool TaskbarLyricsHideWhenNoMedia { get; set; } = true;
        public bool TaskbarLyricsMusicOnly { get; set; } = true;
        public int TaskbarLyricsPollingIntervalMs { get; set; } = 250;
        public int TaskbarLyricsVerticalOffset { get; set; }
        public bool LyricSourceQQEnabled { get; set; } = true;
        public bool LyricSourceNeteaseEnabled { get; set; } = true;
        public bool LyricSourceKugouEnabled { get; set; } = true;
        public bool LyricSourceLrclibEnabled { get; set; } = true;
        public int LyricOffsetMs { get; set; } = 0;
        public bool LyricSplitLongLines { get; set; } = false;
        public int GameSaveManualRetention { get; set; } = 10;
        public int GameSaveSafetyRetention { get; set; } = 5;
        public List<CustomGameSaveDefinition> CustomGameSaveFolders { get; set; } = new();
        public bool GameSaveAutoBackupEnabled { get; set; }
        public bool GameSavePeriodicBackupEnabled { get; set; } = true;
        public int GameSavePeriodicIntervalMinutes { get; set; } = 30;
        public bool SmartSaveDiscoveryEnabled { get; set; } = true;
        public DateTime? GameSaveLastAutoBackup { get; set; }
        // 功能模块总开关：关闭后停止对应功能的全部后台行为，并从导航栏隐藏入口。
        public bool GameSaveCenterEnabled { get; set; } = true;
        public bool DesktopIconHiderEnabled { get; set; } = true;
        public bool TaskbarLyricsModuleEnabled { get; set; } = true;
        public bool DesktopFloatingEnabled { get; set; }
        public bool DesktopFloatingTopmost { get; set; } = true;
        public bool DesktopFloatingLocked { get; set; }
        public int DesktopFloatingOpacity { get; set; } = 80;
        public int DesktopFloatingAvatarSize { get; set; } = 45;
        public bool DesktopFloatingGlassEnabled { get; set; } = true;
        public bool DesktopFloatingRoundedMode { get; set; } = true;
        public string DesktopFloatingGlassColor { get; set; } = "#4A90D9";
        public double? DesktopFloatingLeft { get; set; }
        public double? DesktopFloatingTop { get; set; }
        public string? DesktopFloatingMonitorId { get; set; }
        public double? DesktopFloatingRelativeLeft { get; set; }
        public double? DesktopFloatingRelativeTop { get; set; }
        public bool EnableLibraryInjection { get; set; }
        public bool AutoScanGamesOnStartup { get; set; }
        public bool SilentCloseSteam { get; set; } = true;
        public bool UseBetaUpdateChannel { get; set; }
        public bool CheckUpdateOnStartup { get; set; } = true;
        public bool AutoInstallUpdates { get; set; }
        public bool ShowNotificationOnSteamClose { get; set; }
        public string AccountFilterMode { get; set; } = "All";
        public string AccountSortMode { get; set; } = "Recent";
        public string GameFilterMode { get; set; } = "All";
        public bool AccountGridMode { get; set; }
        public string Theme { get; set; } = "Dark";
        public bool MinimalMode { get; set; } = false;
        public bool DesktopFloatingDockEnabled { get; set; } = true;
        public List<string> PinnedAccountIds { get; set; } = new();
        public List<int> PinnedGameIds { get; set; } = new();
        public List<string> PinnedPanelItemOrder { get; set; } = new();
        public string CustomSteamPath { get; set; } = "";
        public string CustomLoginUsersPath { get; set; } = "";

        // 主窗口布局持久化。null 表示从未保存过（首次运行走 XAML 默认的 CenterScreen）。
        // 几何单位是 DIP；最大化时保存的是"还原后"的几何（RestoreBounds）。
        public double? MainWindowLeft { get; set; }
        public double? MainWindowTop { get; set; }
        public double? MainWindowWidth { get; set; }
        public double? MainWindowHeight { get; set; }
        public bool MainWindowMaximized { get; set; }
    }

    public static class SettingsService
    {
        private static readonly object FileLock = new();

        /// <summary>
        /// Set when <see cref="Load"/> had to fall back to in-memory defaults (or the
        /// previous backup) because the primary settings file was only transiently
        /// unreadable. While true, <see cref="Save"/> refuses to overwrite the on-disk
        /// file, so a momentary lock at startup can never wipe the user's real config.
        /// </summary>
        private static bool _lastLoadWasTransientFallback;

        public static string SettingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamSwitch");

        public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
        public static string BackupSettingsPath => SettingsPath + ".bak";

        /// <summary>
        /// settings.json 有两个写入方：正常保存走 <see cref="Save"/>，配置包导入走
        /// AtomicJsonFile.WriteAsync。两者必须共用这一把锁，否则导入与去抖自动保存
        /// 并发时 File.Replace 会失败，或后写的一方把另一方的内容覆盖掉。
        /// </summary>
        internal static void RunUnderSettingsFileLock(Action action)
        {
            lock (FileLock)
            {
                action();
            }
        }

        public static AppSettings Load()
        {
            lock (FileLock)
            {
                _lastLoadWasTransientFallback = false;

                // Retry the primary read a few times: a transient lock (antivirus
                // scan, cloud-sync, or a concurrent write) usually clears within a
                // fraction of a second. Absorbing it here means we almost never fall
                // back to defaults and never risk overwriting the real settings.
                if (TryLoadWithRetry(SettingsPath, out var settings, out var primaryTransient))
                    return Normalize(settings);

                // 主文件只是"暂时读不到"（被并发写入占用、杀软扫描、云同步），
                // 此时绝不能走下面的 .bak 回写分支——那会用陈旧备份覆盖真实配置。
                // 仅在内存里用备份或默认值顶上，等下一次启动再读。
                if (primaryTransient)
                {
                    if (TryLoad(BackupSettingsPath, out settings, out _))
                    {
                        AppLogger.Warn("Primary settings file is temporarily unreadable; using the previous backup in memory only.");
                        _lastLoadWasTransientFallback = true;
                        return Normalize(settings);
                    }

                    AppLogger.Warn("Primary settings file is temporarily unreadable; using in-memory defaults without writing.");
                    _lastLoadWasTransientFallback = true;
                    return new AppSettings();
                }

                if (TryLoad(BackupSettingsPath, out settings, out _))
                {
                    AppLogger.Warn("Primary settings file is unavailable; recovered the previous settings backup.");
                    var recovered = Normalize(settings);
                    string? recoveryPath = null;
                    try
                    {
                        var json = JsonSerializer.Serialize(recovered, new JsonSerializerOptions { WriteIndented = true });
                        recoveryPath = SettingsPath + ".recovered.tmp";
                        Directory.CreateDirectory(SettingsDirectory);
                        File.WriteAllText(recoveryPath, json);
                        File.Move(recoveryPath, SettingsPath, overwrite: true);
                        recoveryPath = null;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"Recovered settings could not repair the primary file: {ex.Message}");
                    }
                    finally
                    {
                        if (recoveryPath != null)
                        {
                            try { File.Delete(recoveryPath); } catch { }
                        }
                    }
                    return recovered;
                }

                // 重置为默认值之前先留一份损坏副本：settings.json 常常只是
                // 某一个字段类型不对就被整体判死，用户需要能找回原文件。
                TryPreserveCorruptSettings();

                var defaults = new AppSettings();
                if (Save(defaults))
                    AppLogger.Warn("Primary and backup settings were unavailable; rebuilt default settings. A copy of the unreadable file was kept as settings.json.corrupt-*.");
                else
                    AppLogger.Warn("Primary and backup settings were unavailable; using in-memory defaults because the file could not be rebuilt.");
                return defaults;
            }
        }

        /// <summary>
        /// 把读不出来的设置文件另存为 settings.json.corrupt-&lt;时间戳&gt;，
        /// 保留最近若干份，避免用户的配置被默认值无声覆盖后无从找回。
        /// </summary>
        private static void TryPreserveCorruptSettings()
        {
            foreach (var source in new[] { SettingsPath, BackupSettingsPath })
            {
                try
                {
                    if (!File.Exists(source)) continue;
                    Directory.CreateDirectory(SettingsDirectory);
                    var target = Path.Combine(
                        SettingsDirectory,
                        $"settings.json.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(source).Replace(".json", string.Empty)}");
                    File.Copy(source, target, overwrite: true);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Could not preserve unreadable settings file {source}: {ex.Message}");
                }
            }

            try
            {
                var directory = new DirectoryInfo(SettingsDirectory);
                if (!directory.Exists) return;
                var stale = directory.GetFiles("settings.json.corrupt-*")
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Skip(5)
                    .ToList();
                foreach (var file in stale)
                {
                    try { file.Delete(); } catch { }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Could not prune old corrupt settings copies: {ex.Message}");
            }
        }

        public static bool Save(AppSettings settings)
        {
            lock (FileLock)
                return SaveToDirectory(settings, SettingsDirectory, _lastLoadWasTransientFallback);
        }

        internal static string? SaveProtectionReason
        {
            get
            {
                lock (FileLock)
                    return _lastLoadWasTransientFallback
                        ? "启动时未能完整读取设置，原文件已保护。请先导出当前修改，再重启程序重新读取原设置。"
                        : null;
            }
        }

        internal static bool SaveToDirectory(AppSettings settings, string directory, bool primaryReadIncomplete = false)
        {
            lock (FileLock)
            {
                var settingsPath = Path.Combine(directory, "settings.json");
                // If the last load fell back to in-memory defaults because the primary
                // file was only transiently unreadable, never overwrite the on-disk file
                // with those defaults. The real settings stay intact; we defer all writes
                // for this session rather than risk clobbering the user's configuration.
                // The trade-off (settings changed during this session won't persist) is
                // far safer than silently wiping real settings the next launch would fix.
                if (primaryReadIncomplete)
                {
                    if (TryLoad(settingsPath, out _, out _))
                        AppLogger.Warn("Skipping settings save: primary file is readable again after a transient failure; preserving on-disk settings.");
                    else
                        AppLogger.Warn("Skipping settings save: primary settings file is still unreadable after a transient failure.");
                    return false;
                }

                string? temporaryPath = null;
                try
                {
                    Directory.CreateDirectory(directory);
                    var normalized = Normalize(settings);
                    var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                    temporaryPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
                    using (var stream = new FileStream(
                               temporaryPath,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None,
                               16 * 1024,
                               FileOptions.WriteThrough))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(json);
                        writer.Flush();
                        stream.Flush(flushToDisk: true);
                    }

                    ReplaceFileWithBackup(temporaryPath, settingsPath, settingsPath + ".bak");
                    temporaryPath = null;
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Failed to save settings.", ex);
                    return false;
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(temporaryPath))
                    {
                        try { File.Delete(temporaryPath); } catch { }
                    }
                }
            }
        }

        public static AppSettings Normalize(AppSettings? settings)
        {
            settings ??= new AppSettings();
            settings.DesktopFloatingRelativeLeft = NormalizeRelativePosition(settings.DesktopFloatingRelativeLeft);
            settings.DesktopFloatingRelativeTop = NormalizeRelativePosition(settings.DesktopFloatingRelativeTop);
            settings.CustomGameSaveFolders ??= new List<CustomGameSaveDefinition>();
            settings.PinnedAccountIds ??= new List<string>();
            settings.PinnedGameIds ??= new List<int>();
            settings.PinnedPanelItemOrder ??= new List<string>();
            settings.CustomSteamPath ??= string.Empty;
            settings.CustomLoginUsersPath ??= string.Empty;
            settings.BackupExportDirectory = (settings.BackupExportDirectory ?? string.Empty).Trim();
            settings.DesktopEnhance ??= new DesktopEnhanceSettings();
            // 主窗口布局：NaN/Infinity（settings 被手改或磁盘损坏）一律按"未保存"处理，
            // 恢复时会走默认居中而不是把窗口放到奇怪的位置。
            settings.MainWindowLeft = NormalizeCoordinate(settings.MainWindowLeft);
            settings.MainWindowTop = NormalizeCoordinate(settings.MainWindowTop);
            settings.MainWindowWidth = NormalizeCoordinate(settings.MainWindowWidth);
            settings.MainWindowHeight = NormalizeCoordinate(settings.MainWindowHeight);
            settings.DesktopEnhance.IdleSeconds = Math.Clamp(settings.DesktopEnhance.IdleSeconds, 5, 300);
            settings.DesktopEnhance.AnimationDuration = Math.Clamp(settings.DesktopEnhance.AnimationDuration, 200, 1000);
            settings.TaskbarMonitorIndex = Math.Clamp(settings.TaskbarMonitorIndex, 0, 8);
            settings.TaskbarOffsetX = Math.Clamp(settings.TaskbarOffsetX, -100, 100);
            settings.TaskbarOffsetY = Math.Clamp(settings.TaskbarOffsetY, -100, 100);
            settings.TaskbarWindowSize = Math.Clamp(settings.TaskbarWindowSize, 2, 30);
            settings.TaskbarAvatarSize = Math.Clamp(settings.TaskbarAvatarSize, 16, 96);
            settings.TaskbarLyricsWidth = Math.Clamp(settings.TaskbarLyricsWidth, 240, 720);
            settings.TaskbarLyricsPollingIntervalMs = Math.Clamp(settings.TaskbarLyricsPollingIntervalMs, 60, 1000);
            settings.TaskbarLyricsVerticalOffset = Math.Clamp(settings.TaskbarLyricsVerticalOffset, -12, 12);
            settings.TaskbarLyricsFontSize = Math.Clamp(settings.TaskbarLyricsFontSize, 12, 18);
            settings.TaskbarLyricsFontFamily = string.IsNullOrWhiteSpace(settings.TaskbarLyricsFontFamily)
                ? "Microsoft YaHei UI"
                : settings.TaskbarLyricsFontFamily.Trim();
            settings.LyricOffsetMs = Math.Clamp(settings.LyricOffsetMs, -3000, 3000);
            settings.DesktopFloatingOpacity = Math.Clamp(settings.DesktopFloatingOpacity, 20, 100);
            settings.DesktopFloatingAvatarSize = Math.Clamp(settings.DesktopFloatingAvatarSize, 24, 96);
            settings.DesktopFloatingGlassColor = NormalizeHexColor(settings.DesktopFloatingGlassColor, "#4A90D9");
            settings.GameSaveManualRetention = Math.Clamp(settings.GameSaveManualRetention, 1, 100);
            settings.GameSaveSafetyRetention = Math.Clamp(settings.GameSaveSafetyRetention, 1, 50);
            settings.GameSavePeriodicIntervalMinutes = Math.Clamp(settings.GameSavePeriodicIntervalMinutes, 5, 1440);
            settings.AccountFilterMode = settings.AccountFilterMode is "Current" or "Pinned"
                ? settings.AccountFilterMode
                : "All";
            settings.AccountSortMode = settings.AccountSortMode is "Name" or "Pinned"
                ? settings.AccountSortMode
                : "Recent";
            settings.GameFilterMode = settings.GameFilterMode is "Bound" or "Unbound"
                ? settings.GameFilterMode
                : "All";
            if (settings.AutoInstallUpdates)
                settings.CheckUpdateOnStartup = true;
            settings.Theme = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
                ? "Light"
                : "Dark";
            return settings;
        }

        private static double? NormalizeCoordinate(double? value) =>
            value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value) ? null : value;

        private static string NormalizeHexColor(string? value, string fallback)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Regex.IsMatch(value.Trim(), "^#[0-9A-Fa-f]{6}$")
                ? value.Trim().ToUpperInvariant()
                : fallback;
        }

        /// <summary>
        /// 判断主设置文件读取失败是否只是"暂时读不到"（被并发写入占用/无权限）。
        /// 这类失败标记 transientFailure，由 Save 逻辑在写入前重读主文件决定是否跳过覆盖，
        /// 避免把内存中的当前设置写回、覆盖掉磁盘上其实完好但此刻被锁的文件。
        /// 刻意不含 FileNotFoundException/DirectoryNotFoundException：主文件缺失是首次运行的
        /// 正常状态，不应被当成"瞬时错误"触发回退保护（否则会与 803ed06 修复的数据丢失防护矛盾）。
        /// 注意：AtomicJsonFile.IsTransientIoError 用途不同（决定读候选文件失败时是否如实抛出），
        /// 两者不要盲目统一。
        /// </summary>
        private static bool IsTransientIoError(Exception ex) =>
            ex is IOException or UnauthorizedAccessException;

        private static bool TryLoad(string path, out AppSettings settings, out bool transientFailure)
        {
            settings = null!;
            transientFailure = false;
            if (!File.Exists(path)) return false;
            try
            {
                var json = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<AppSettings>(json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new JsonStringEnumConverter() }
                    })!;
                return settings != null;
            }
            catch (Exception ex) when (IsTransientIoError(ex))
            {
                transientFailure = true;
                AppLogger.Warn($"Settings file is temporarily unreadable {Path.GetFileName(path)}: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to load settings file {Path.GetFileName(path)}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads the settings file, retrying a few times only on transient IO failures
        /// (a momentary lock from an antivirus scan, cloud-sync, or a concurrent write).
        /// Permanent failures (parse errors, a missing file) return immediately.
        /// </summary>
        private static bool TryLoadWithRetry(string path, out AppSettings settings, out bool transientFailure)
        {
            const int maxAttempts = 3;
            settings = null!;
            transientFailure = false;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (TryLoad(path, out settings, out transientFailure))
                    return true;
                if (!transientFailure)
                    return false;
                if (attempt < maxAttempts - 1)
                    Thread.Sleep(150);
            }
            return false;
        }

        private static void ReplaceFileWithBackup(string temporaryPath, string destinationPath, string backupPath)
        {
            if (!File.Exists(destinationPath))
            {
                File.Move(temporaryPath, destinationPath);
                return;
            }

            var transientBackup = $"{backupPath}.{Guid.NewGuid():N}.tmp";
            File.Replace(temporaryPath, destinationPath, transientBackup, ignoreMetadataErrors: true);
            var rotated = false;
            try
            {
                File.Move(transientBackup, backupPath, overwrite: true);
                rotated = true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Settings were saved, but the previous-version backup could not be rotated; kept transient copy at {transientBackup}: {ex.Message}");
            }
            finally
            {
                if (rotated && File.Exists(transientBackup))
                {
                    try { File.Delete(transientBackup); } catch { }
                }
            }
        }

        private static double? NormalizeRelativePosition(double? value) =>
            value.HasValue && double.IsFinite(value.Value) ? Math.Clamp(value.Value, 0, 1) : null;

        public static bool SetStartWithWindows(bool enabled)
        {
            try
            {
                const string valueName = "SteamSwitch";
                if (enabled)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                    var exePath = GetExecutablePath();
                    if (key == null || string.IsNullOrWhiteSpace(exePath))
                        return false;

                    key.SetValue(valueName, $"\"{exePath}\" --autorun", RegistryValueKind.String);
                }
                else
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    if (key == null)
                        return true;
                    key.DeleteValue(valueName, false);
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to set start with Windows.", ex);
                return false;
            }
        }

        public static bool IsStartWithWindowsEnabled()
        {
            try
            {
                // 只读打开：受限制环境下申请可写句柄会失败并返回 null，
                // 结果把用户已开启的开机自启误判成关闭并静默写回 false。
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
                if (key?.GetValue("SteamSwitch") is not string value ||
                    string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                var exePath = GetExecutablePath();
                if (!string.IsNullOrWhiteSpace(exePath) &&
                    string.Equals(value, $"\"{exePath}\"", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var writableKey = Registry.CurrentUser.OpenSubKey(
                            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                        writableKey?.SetValue(
                            "SteamSwitch",
                            $"\"{exePath}\" --autorun",
                            RegistryValueKind.String);
                    }
                    catch (Exception migrationError)
                    {
                        AppLogger.Warn($"Unable to migrate legacy startup entry: {migrationError.Message}");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to read start with Windows setting.", ex);
                return false;
            }
        }

        private static string? GetExecutablePath()
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
                return Environment.ProcessPath;

            using var process = Process.GetCurrentProcess();
            return process.MainModule?.FileName;
        }
    }
}
