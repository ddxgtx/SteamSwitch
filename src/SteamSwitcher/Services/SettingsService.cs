using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using SteamSwitcher.Core;

namespace SteamSwitcher.Services
{
    public class AppSettings
    {
        public bool AutoStartSteam { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public bool StartWithWindows { get; set; }
        public bool TaskbarPinned { get; set; }
        public TaskbarPosition TaskbarPosition { get; set; } = TaskbarPosition.Right;
        public int TaskbarOffsetX { get; set; }
        public int TaskbarOffsetY { get; set; }
        public int TaskbarWindowSize { get; set; } = 2;
        public int TaskbarAvatarSize { get; set; } = 38;
        public bool TaskbarGlassEnabled { get; set; } = true;
        public bool TaskbarRoundedMode { get; set; } = true;
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
        public bool CheckUpdateOnStartup { get; set; } = true;
        public bool AutoInstallUpdates { get; set; }
        public bool ShowNotificationOnSteamClose { get; set; }
        public string Theme { get; set; } = "Dark";
        public List<string> PinnedAccountIds { get; set; } = new();
        public List<int> PinnedGameIds { get; set; } = new();
        public List<string> PinnedPanelItemOrder { get; set; } = new();
    }

    public static class SettingsService
    {
        public static string SettingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamSwitch");

        public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to load settings.", ex);
            }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to save settings.", ex);
            }
        }

        public static void SetStartWithWindows(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null)
                    return;

                const string valueName = "SteamSwitch";
                if (enabled)
                {
                    var exePath = Environment.ProcessPath ??
                                  Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exePath))
                        key.SetValue(valueName, $"\"{exePath}\"", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(valueName, false);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to set start with Windows.", ex);
            }
        }

        public static bool IsStartWithWindowsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("SteamSwitch") is string value &&
                       !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to read start with Windows setting.", ex);
                return false;
            }
        }
    }
}
