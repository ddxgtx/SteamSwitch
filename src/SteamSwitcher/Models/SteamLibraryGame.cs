namespace SteamSwitcher.Models
{
    public class SteamLibraryGame
    {
        public int AppId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string InstallDir { get; set; } = string.Empty;
        public string LibraryPath { get; set; } = string.Empty;
        public string? IconPath { get; set; }
        public long SizeOnDisk { get; set; }

        public string SizeDisplay
        {
            get
            {
                if (SizeOnDisk <= 0) return "-";
                double gb = SizeOnDisk / (1024.0 * 1024.0 * 1024.0);
                return gb >= 1.0 ? $"{gb:F1} GB" : $"{SizeOnDisk / (1024.0 * 1024.0):F0} MB";
            }
        }

        public override string ToString() => $"{Name} ({AppId})";
    }
}
