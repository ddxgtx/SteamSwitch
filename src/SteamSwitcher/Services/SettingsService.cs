using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
        public bool TaskbarPinned { get; set; }
        public TaskbarPosition TaskbarPosition { get; set; } = TaskbarPosition.Right;
        public int TaskbarMonitorIndex { get; set; }
        public int TaskbarOffsetX { get; set; }
        public int TaskbarOffsetY { get; set; }
        public int TaskbarWindowSize { get; set; } = 2;
        public int TaskbarAvatarSize { get; set; } = 38;
        public bool TaskbarGlassEnabled { get; set; } = true;
        public bool TaskbarRoundedMode { get; set; } = true;
        public bool TaskbarAppearanceEnabled { get; set; }
        public TaskbarAppearanceMode TaskbarAppearanceMode { get; set; } = TaskbarAppearanceMode.Clear;
        public string TaskbarAppearanceColor { get; set; } = "#000000";
        public int TaskbarAppearanceOpacity { get; set; } = 0;
        public bool TaskbarDynamicMaximized { get; set; } = false;
        public TaskbarAppearanceMode TaskbarMaximizedMode { get; set; } = TaskbarAppearanceMode.Opaque;
        public bool TaskbarDynamicStartMenu { get; set; } = false;
        public TaskbarAppearanceMode TaskbarStartMenuMode { get; set; } = TaskbarAppearanceMode.Normal;
        public bool TaskbarDynamicSearch { get; set; } = false;
        public TaskbarAppearanceMode TaskbarSearchMode { get; set; } = TaskbarAppearanceMode.Normal;
        public bool TaskbarDynamicTaskView { get; set; } = false;
        public TaskbarAppearanceMode TaskbarTaskViewMode { get; set; } = TaskbarAppearanceMode.Normal;
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
        public string TaskbarLyricsFontFamily { get; set; } = "Segoe UI Variable Text";
        public bool TaskbarLyricsTrackInfoOnly { get; set; }
        public bool TaskbarLyricsShowSecondaryLine { get; set; } = true;
        public int TaskbarLyricsPollingIntervalMs { get; set; } = 120;
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
        public bool EnableLibraryInjection { get; set; }
        public bool AutoScanGamesOnStartup { get; set; }
        public bool ConfirmBeforeGameLaunch { get; set; } = true;
        public bool SilentCloseSteam { get; set; } = true;
        public bool UseBetaUpdateChannel { get; set; }
        public bool CheckUpdateOnStartup { get; set; } = true;
        public bool AutoInstallUpdates { get; set; }
        public bool ShowNotificationOnSteamClose { get; set; }
        public string Theme { get; set; } = "Dark";
        public bool MinimalMode { get; set; } = false;
        public bool DesktopFloatingDockEnabled { get; set; } = true;
        public List<string> PinnedAccountIds { get; set; } = new();
        public List<int> PinnedGameIds { get; set; } = new();
        public List<string> PinnedPanelItemOrder { get; set; } = new();
        public string CustomSteamPath { get; set; } = "";
        public string CustomLoginUsersPath { get; set; } = "";
    }

    public static class SettingsService
    {
        private static readonly object FileLock = new();

        public static string SettingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamSwitch");

        public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
        public static string BackupSettingsPath => SettingsPath + ".bak";

        public static AppSettings Load()
        {
            lock (FileLock)
            {
                if (TryLoad(SettingsPath, out var settings))
                    return Normalize(settings);

                if (TryLoad(BackupSettingsPath, out settings))
                {
                    AppLogger.Warn("Primary settings file is unavailable; recovered the previous settings backup.");
                    return Normalize(settings);
                }
            }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            lock (FileLock)
            {
                string? temporaryPath = null;
                try
                {
                    Directory.CreateDirectory(SettingsDirectory);
                    var normalized = Normalize(settings);
                    var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                    temporaryPath = Path.Combine(SettingsDirectory, $"settings.{Guid.NewGuid():N}.tmp");
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

                    ReplaceFileWithBackup(temporaryPath, SettingsPath, BackupSettingsPath);
                    temporaryPath = null;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Failed to save settings.", ex);
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
            settings.CustomGameSaveFolders ??= new List<CustomGameSaveDefinition>();
            settings.PinnedAccountIds ??= new List<string>();
            settings.PinnedGameIds ??= new List<int>();
            settings.PinnedPanelItemOrder ??= new List<string>();
            settings.CustomSteamPath ??= string.Empty;
            settings.CustomLoginUsersPath ??= string.Empty;
            settings.DesktopEnhance ??= new DesktopEnhanceSettings();
            settings.DesktopEnhance.IdleSeconds = Math.Clamp(settings.DesktopEnhance.IdleSeconds, 5, 300);
            settings.DesktopEnhance.AnimationDuration = Math.Clamp(settings.DesktopEnhance.AnimationDuration, 200, 1000);
            settings.TaskbarMonitorIndex = Math.Clamp(settings.TaskbarMonitorIndex, 0, 8);
            settings.TaskbarLyricsWidth = Math.Clamp(settings.TaskbarLyricsWidth, 240, 720);
            settings.TaskbarLyricsPollingIntervalMs = Math.Clamp(settings.TaskbarLyricsPollingIntervalMs, 60, 1000);
            settings.TaskbarLyricsVerticalOffset = Math.Clamp(settings.TaskbarLyricsVerticalOffset, -12, 12);
            settings.TaskbarLyricsFontSize = settings.TaskbarLyricsFontSize <= 12
                ? 14
                : Math.Clamp(settings.TaskbarLyricsFontSize, 12, 18);
            settings.TaskbarLyricsFontFamily = string.IsNullOrWhiteSpace(settings.TaskbarLyricsFontFamily)
                ? "Segoe UI Variable Text"
                : settings.TaskbarLyricsFontFamily.Trim();
            settings.Theme = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
                ? "Light"
                : "Dark";
            return settings;
        }

        private static bool TryLoad(string path, out AppSettings settings)
        {
            settings = null!;
            if (!File.Exists(path)) return false;
            try
            {
                var json = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<AppSettings>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                return settings != null;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to load settings file {Path.GetFileName(path)}: {ex.Message}");
                return false;
            }
        }

        private static void ReplaceFileWithBackup(string temporaryPath, string destinationPath, string backupPath)
        {
            if (!File.Exists(destinationPath))
            {
                File.Move(temporaryPath, destinationPath);
                return;
            }

            var transientBackup = $"{backupPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.Replace(temporaryPath, destinationPath, transientBackup, ignoreMetadataErrors: true);
                try
                {
                    File.Move(transientBackup, backupPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Settings were saved, but the previous-version backup could not be rotated: {ex.Message}");
                }
            }
            finally
            {
                try { if (File.Exists(transientBackup)) File.Delete(transientBackup); } catch { }
            }
        }

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
                        @"Software\Microsoft\Windows\CurrentVersion\Run", true);
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
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key?.GetValue("SteamSwitch") is not string value ||
                    string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                var exePath = GetExecutablePath();
                if (!string.IsNullOrWhiteSpace(exePath) &&
                    string.Equals(value, $"\"{exePath}\"", StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(
                        "SteamSwitch",
                        $"\"{exePath}\" --autorun",
                        RegistryValueKind.String);
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
