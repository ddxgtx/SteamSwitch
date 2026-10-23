using System;
using System.IO;
using System.Threading.Tasks;

namespace SteamSwitcher.Services;

internal enum BackupExportKind { Accounts, Configuration, Settings, GameSaves }

/// <summary>Shared destination policy for user-facing backup/export actions.</summary>
internal static class BackupExportService
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamSwitch", "Backups");

    public static string GetDirectory(AppSettings settings)
    {
        var directory = string.IsNullOrWhiteSpace(settings.BackupExportDirectory)
            ? DefaultDirectory : Environment.ExpandEnvironmentVariables(settings.BackupExportDirectory.Trim());
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("备份导出目录必须是完整路径，请在常规设置中重新选择。");
        return Path.GetFullPath(directory);
    }

    /// <summary>拒绝把导出文件写进指定目录（例如存档备份仓库）。</summary>
    private static void EnsureOutsideDirectory(string path, string? forbiddenDirectory)
    {
        if (string.IsNullOrWhiteSpace(forbiddenDirectory)) return;

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(forbiddenDirectory).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能导出到 SteamSwitch 的存档备份目录，请在备份目录之外选择位置。");
    }

    public static string GetInitialDirectory(string suggestedPath)
    {
        var directory = Path.GetDirectoryName(suggestedPath);
        while (directory != null && !Directory.Exists(directory)) directory = Path.GetDirectoryName(directory);
        return directory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public static async Task<string?> ExportAsync(AppSettings settings, BackupExportKind kind, string fileName,
        Func<string, Task> write, Func<string, string?> choosePath, string? forbiddenDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("备份文件名无效。", nameof(fileName));
        var folder = Path.Combine(GetDirectory(settings), kind.ToString());
        if (settings.AskBeforeBackupExport)
        {
            var chosen = choosePath(Path.Combine(folder, fileName));
            if (chosen == null) return null;
            // 两条路径都要做目标校验：否则"每次询问保存位置"会把导出文件直接放进
            // 备份仓库，之后它会被当成一个可恢复版本列出，还可能被清理规则删掉。
            EnsureOutsideDirectory(chosen, forbiddenDirectory);
            // Keep each exporter's validation against its real target (for example,
            // exporting an account archive must never replace the live library).
            await write(chosen);
            return chosen;
        }

        Directory.CreateDirectory(folder);
        // A unique name and a final no-overwrite rename preserve earlier backups,
        // including multiple exports in the same second or concurrent operations.
        var extension = fileName.EndsWith(".steamswitch.json", StringComparison.OrdinalIgnoreCase)
            ? ".steamswitch.json" : Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        var target = Path.Combine(folder, $"{stem}-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}{extension}");
        // 默认（不询问）分支同样必须校验目标：当用户把备份导出目录配成存档仓库根目录时，
        // 不校验会让导出文件落进仓库，被当成可恢复版本列出或被清理规则删掉。
        EnsureOutsideDirectory(target, forbiddenDirectory);
        var staging = target + ".pending";
        try
        {
            await write(staging);
            File.Move(staging, target, overwrite: false);
            return target;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
            if (File.Exists(staging + ".bak")) File.Delete(staging + ".bak");
        }
    }
}
