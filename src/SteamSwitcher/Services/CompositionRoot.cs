using SteamSwitcher.Core;
using SteamSwitcher.ViewModels;

namespace SteamSwitcher.Services
{
    /// <summary>
    /// 应用级服务的组合根：集中创建并装配 MainViewModel 及其可外部注入的依赖，
    /// 消除分散在 MainWindow 与视图模型无参构造里的具体类 new，统一依赖图。
    /// （GameSaveAutoBackupService 因消费 VM 内部 _settings 实时引用，仍由 VM 内部创建。）
    /// </summary>
    public static class CompositionRoot
    {
        public static MainViewModel CreateMainViewModel(bool monitorSystemState = true)
        {
            var accountManager = new AccountManager();
            var gameBinding = new GameAccountBinding();
            var quickLaunch = new QuickLaunchService();
            var settingsSession = new SettingsSession(
                SettingsService.Load(),
                SettingsService.Save,
                () => SettingsService.SaveProtectionReason);

            return new MainViewModel(
                accountManager,
                gameBinding,
                quickLaunch,
                settingsSession,
                SettingsService.IsStartWithWindowsEnabled,
                SettingsService.SetStartWithWindows,
                SettingsService.SettingsDirectory,
                monitorSystemState);
        }
    }
}
