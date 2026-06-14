using System;

namespace SteamSwitcher.Models
{
    public class QuickLaunchItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
        public string? Arguments { get; set; }
        public string? IconPath { get; set; }
        public string? WorkingDirectory { get; set; }
        public bool IsPinned { get; set; }
        public int SortOrder { get; set; }
        public DateTime DateAdded { get; set; } = DateTime.Now;
    }
}
