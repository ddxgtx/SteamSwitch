using System;
using System.Diagnostics;
using System.Windows;

namespace SteamSwitcher;

public partial class MainWindow
{
    private void RetrySettingsSave_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsLoading || _configurationChangeInProgress) return;
        if (_viewModel.SaveSettingsNow()) _viewModel.StatusText = "设置已保存，当前修改已写入本地。";
    }

    private void RevealRecentBackupExport_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryGetRecentBackupExport(out var path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = true });
        }
        catch (Exception ex) { ShowConfigurationError("定位备份文件", ex); }
    }

    private void CopyRecentBackupExportPath_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryGetRecentBackupExport(out var path)) return;
        try
        {
            Clipboard.SetText(path);
            _viewModel.StatusText = "已复制最近备份的完整路径。";
        }
        catch (Exception ex) { ShowConfigurationError("复制备份路径", ex); }
    }
}
