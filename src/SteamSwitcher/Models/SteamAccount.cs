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

        /// <summary>
        /// True when the account exists only in the local account library:
        /// Steam has pruned it from loginusers.vdf, so switching to it first
        /// restores the entry (Steam will ask for the password unless it
        /// still remembers it).
        /// </summary>
        public bool IsArchived { get; set; }

        public DateTime LastLoginTime => DateTimeOffset.FromUnixTimeSeconds(Timestamp).LocalDateTime;

        public override string ToString() => $"{PersonaName} ({AccountName})";
    }
}
