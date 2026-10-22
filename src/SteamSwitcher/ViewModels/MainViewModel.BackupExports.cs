using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using SteamSwitcher.Services;

namespace SteamSwitcher.ViewModels;

public partial class MainViewModel
{
    private int _backupExportInFlight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportBackup))]
    private bool _isBackupExporting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentBackupExport))]
    [NotifyPropertyChangedFor(nameof(RecentBackupExportFileName))]
    private string? _recentBackupExportPath;

    [ObservableProperty]
    private string _recentBackupExportSummary = "";

    public bool CanExportBackup => !_disposed && !IsLoading && !IsBackupExporting;
    public bool HasRecentBackupExport => !string.IsNullOrEmpty(RecentBackupExportPath);
    public string RecentBackupExportFileName => Path.GetFileName(RecentBackupExportPath) ?? "";

    internal async Task<string?> ExportBackupAsync(BackupExportKind kind, string fileName,
        Func<string, Task> write, Func<string, string?> choosePath, string? forbiddenDirectory = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanExportBackup || Interlocked.CompareExchange(ref _backupExportInFlight, 1, 0) != 0) return null;
        IsBackupExporting = true;
        StatusText = "正在保存备份，请稍候…";
        try
        {
            var path = await BackupExportService.ExportAsync(SettingsSession.Copy(_settings), kind, fileName, write, choosePath, forbiddenDirectory);
            if (_disposed) return path;
            if (path == null)
            {
                StatusText = "已取消备份导出";
                return null;
            }
            if (!File.Exists(path)) throw new FileNotFoundException("导出文件不存在，请重新备份。", path);
            var label = kind switch
            {
                BackupExportKind.Accounts => "账号库", BackupExportKind.Configuration => "应用配置包",
                BackupExportKind.Settings => "设置", _ => "游戏存档"
            };
            RecentBackupExportSummary = $"本次运行最近导出 · {label} · {DateTime.Now:HH:mm:ss}";
            RecentBackupExportPath = Path.GetFullPath(path);
            StatusText = $"备份已保存：{path}";
            return path;
        }
        finally
        {
            IsBackupExporting = false;
            Interlocked.Exchange(ref _backupExportInFlight, 0);
        }
    }

    internal bool TryGetRecentBackupExport(out string path)
    {
        path = RecentBackupExportPath ?? "";
        if (path.Length > 0 && File.Exists(path)) return true;
        RecentBackupExportPath = null;
        RecentBackupExportSummary = "";
        StatusText = "最近导出的文件已移动、删除或无法访问，可在备份目录中查找或重新备份。";
        return false;
    }
}
