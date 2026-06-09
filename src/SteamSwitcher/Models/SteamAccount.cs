using System;

namespace SteamSwitcher.Models
{
    public class SteamAccount
    {
        public string SteamId { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string PersonaName { get; set; } = string.Empty;
        public bool RememberPassword { get; set; }
        public bool MostRecent { get; set; }
        public long Timestamp { get; set; }
        public string? AvatarPath { get; set; }

        public DateTime LastLoginTime => DateTimeOffset.FromUnixTimeSeconds(Timestamp).LocalDateTime;

        public override string ToString() => $"{PersonaName} ({AccountName})";
    }
}
