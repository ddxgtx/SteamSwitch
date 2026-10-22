using System;
using System.Collections.Generic;
using System.Linq;

namespace SteamSwitcher.Models
{
    public class GameSaveEntry
    {
        public string EntryId { get; set; } = string.Empty;
        public string SteamId { get; set; } = string.Empty;
        public string UserDataId { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string PersonaName { get; set; } = string.Empty;
        public int AppId { get; set; }
        public string GameName { get; set; } = string.Empty;
        public string? IconPath { get; set; }
        public string SavePath { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        public DateTime LastWriteTime { get; set; }
        public int BackupCount { get; set; }
        public DateTime? LastBackupTime { get; set; }
        public bool IsCustom { get; set; }
        public bool IsDetected { get; set; }
        public string DetectionRule { get; set; } = string.Empty;
        public string CustomId { get; set; } = string.Empty;
        public bool IsAvailable { get; set; } = true;

        public string AccountDisplayName => string.IsNullOrWhiteSpace(PersonaName) ? AccountName : PersonaName;
        public string AppIdDisplay => AppId > 0 ? $" · App {AppId}" : string.Empty;
        public string SourceDisplay => IsDetected
            ? IsAvailable ? "智能识别" : "智能识别目录缺失 · 可恢复"
            : IsCustom
            ? IsAvailable ? "自定义目录" : "自定义目录不可用"
            : IsAvailable ? "Steam userdata" : "目录缺失 · 可恢复";
        public string SizeDisplay => FormatSize(TotalSize);
        public string LastWriteDisplay => LastWriteTime == DateTime.MinValue ? "未知" : LastWriteTime.ToString("yyyy-MM-dd HH:mm");
        public string BackupDisplay => LastBackupTime.HasValue
            ? $"{BackupCount} 份 · 最近 {LastBackupTime.Value:MM-dd HH:mm}"
            : "尚未备份";

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024L * 1024) return $"{bytes / 1024d:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024d:F1} MB";
            return $"{bytes / 1024d / 1024d / 1024d:F2} GB";
        }
    }

    public class CustomGameSaveDefinition
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string GameName { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
        public string AccountSteamId { get; set; } = string.Empty;
    }

    public class GameSaveBackupInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public long Size { get; set; }
        public bool IsSafetyBackup { get; set; }
        public bool WasSkipped { get; set; }
        public bool IsDifferential { get; set; }

        public string KindDisplay => IsSafetyBackup
            ? "恢复前保护"
            : IsDifferential ? "增量备份" : "完整备份";
        public string SizeDisplay => Size < 1024L * 1024
            ? $"{Size / 1024d:F1} KB"
            : $"{Size / 1024d / 1024d:F1} MB";
        public string DisplayName => $"{CreatedAt:yyyy-MM-dd HH:mm:ss} · {KindDisplay} · {SizeDisplay}";

        public string GetExportFileName(string gameName)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var safeName = string.Concat((gameName ?? string.Empty)
                .Select(character => invalid.Contains(character) ? '_' : character)).Trim().TrimEnd('.');
            if (safeName.Length == 0) safeName = "SteamSave";
            if (safeName.Length > 100) safeName = safeName[..100];
            return $"{safeName}-{CreatedAt:yyyyMMdd-HHmmss}.zip";
        }
    }

    public class GameSaveChangeSummary
    {
        public int Added { get; set; }
        public int Modified { get; set; }
        public int Deleted { get; set; }
        public int Unchanged { get; set; }
        public bool HasPreviousBackup { get; set; }
        public bool HasChanges => Added > 0 || Modified > 0 || Deleted > 0;
        public string DisplayText => !HasPreviousBackup
            ? $"首次备份 · {Added} 个文件"
            : HasChanges
                ? $"新增 {Added} · 修改 {Modified} · 删除 {Deleted} · 未变 {Unchanged}"
                : $"没有变化 · {Unchanged} 个文件与最新备份一致";
    }

    public sealed record GameSaveImportPreview(
        string GameName,
        string AccountName,
        string SourceSavePath,
        string EntryId,
        string ArchiveSha256,
        bool MatchesTarget,
        bool CanReassociate);

    internal class GameSaveBackupManifest
    {
        public int FormatVersion { get; set; } = 1;
        public string EntryId { get; set; } = string.Empty;
        public bool IsCustom { get; set; }
        public string SteamId { get; set; } = string.Empty;
        public string UserDataId { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public int AppId { get; set; }
        public string GameName { get; set; } = string.Empty;
        // Optional in format 3 for backwards compatibility. Discovery never trusts
        // this path without checking it against the known game's detection rules.
        public string SourceSavePath { get; set; } = string.Empty;
        public bool IsDetected { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public bool IsSafetyBackup { get; set; }
        public string BackupMode { get; set; } = "full";
        public string BaseBackupFile { get; set; } = string.Empty;
        public List<GameSaveBackupFile> Files { get; set; } = new();
    }

    internal class GameSaveBackupFile
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public bool? StoredInArchive { get; set; }
        public bool IsStoredInArchive => StoredInArchive != false;
    }
}
