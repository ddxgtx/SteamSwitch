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
        public int TaskbarOffset { get; set; }
        public int AvatarSize { get; set; } = 40;
        public bool GlassEnabled { get; set; } = true;
        public bool RoundedMode { get; set; } = true;
        public bool DesktopFloatingEnabled { get; set; }
        public bool DesktopFloatingTopmost { get; set; } = true;
        public bool DesktopFloatingLocked { get; set; }
        public int DesktopFloatingOpacity { get; set; } = 92;
        public double? DesktopFloatingLeft { get; set; }
        public double? DesktopFloatingTop { get; set; }
        public bool EnableLibraryInjection { get; set; }
        public bool AutoScanGamesOnStartup { get; set; }
        public bool ConfirmBeforeGameLaunch { get; set; } = true;
        public List<string> PinnedAccountIds { get; set; } = new();
        public List<int> PinnedGameIds { get; set; } = new();
    }

    public static class SettingsService
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamSwitch",
            "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
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
            catch { }
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
            catch { }
        }
    }
}
