using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
        public List<string> PinnedAccountIds { get; set; } = new();
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }
    }
}
