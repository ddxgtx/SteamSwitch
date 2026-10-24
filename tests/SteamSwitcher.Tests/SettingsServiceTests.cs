using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class SettingsServiceTests
{
    [AssemblyInitialize]
    public static void InitializeAssembly(TestContext _)
    {
        var isolatedLogDirectory = Path.Combine(
            Path.GetTempPath(),
            "SteamSwitch.Tests",
            $"logs-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("STEAMSWITCH_LOG_DIRECTORY", isolatedLogDirectory);
    }

    [TestMethod]
    public void Normalize_RepairsMissingDesktopEnhanceSettings()
    {
        var settings = new AppSettings { DesktopEnhance = null! };

        var normalized = SettingsService.Normalize(settings);

        Assert.IsNotNull(normalized.DesktopEnhance);
        Assert.AreEqual(30, normalized.DesktopEnhance.IdleSeconds);
    }

    [TestMethod]
    [DataRow(-10, 5)]
    [DataRow(30, 30)]
    [DataRow(999, 300)]
    public void Normalize_ClampsDesktopHideDelay(int value, int expected)
    {
        var settings = new AppSettings
        {
            DesktopEnhance = new DesktopEnhanceSettings { IdleSeconds = value }
        };

        var normalized = SettingsService.Normalize(settings);

        Assert.AreEqual(expected, normalized.DesktopEnhance.IdleSeconds);
    }

    [TestMethod]
    public void Normalize_ClampsTaskbarLyricsPerformanceAndPositionSettings()
    {
        var settings = new AppSettings
        {
            TaskbarLyricsPollingIntervalMs = 10,
            TaskbarLyricsVerticalOffset = 99
        };

        var normalized = SettingsService.Normalize(settings);

        Assert.AreEqual(60, normalized.TaskbarLyricsPollingIntervalMs);
        Assert.AreEqual(12, normalized.TaskbarLyricsVerticalOffset);
    }

    [TestMethod]
    public void Normalize_ClampsLayoutBackupAndOpacitySettings()
    {
        var normalized = SettingsService.Normalize(new AppSettings
        {
            TaskbarOffsetX = 999,
            TaskbarOffsetY = -999,
            TaskbarWindowSize = 999,
            TaskbarAvatarSize = 1,
            DesktopFloatingOpacity = 999,
            DesktopFloatingAvatarSize = 1,
            GameSaveManualRetention = 0,
            GameSaveSafetyRetention = 999,
            GameSavePeriodicIntervalMinutes = 1
        });

        Assert.AreEqual(100, normalized.TaskbarOffsetX);
        Assert.AreEqual(-100, normalized.TaskbarOffsetY);
        Assert.AreEqual(30, normalized.TaskbarWindowSize);
        Assert.AreEqual(16, normalized.TaskbarAvatarSize);
        Assert.AreEqual(100, normalized.DesktopFloatingOpacity);
        Assert.AreEqual(24, normalized.DesktopFloatingAvatarSize);
        Assert.AreEqual(1, normalized.GameSaveManualRetention);
        Assert.AreEqual(50, normalized.GameSaveSafetyRetention);
        Assert.AreEqual(5, normalized.GameSavePeriodicIntervalMinutes);
    }

    [TestMethod]
    public void Normalize_PreservesMinimumLyricsFontSize()
    {
        var normalized = SettingsService.Normalize(new AppSettings { TaskbarLyricsFontSize = 12 });
        Assert.AreEqual(12, normalized.TaskbarLyricsFontSize);
    }

    [TestMethod]
    public void Normalize_AutoInstallUpdatesRequiresStartupCheck()
    {
        var normalized = SettingsService.Normalize(new AppSettings
        {
            AutoInstallUpdates = true,
            CheckUpdateOnStartup = false
        });

        Assert.IsTrue(normalized.CheckUpdateOnStartup);
    }

    [TestMethod]
    public void Normalize_RepairsInvalidGlassColor()
    {
        var normalized = SettingsService.Normalize(new AppSettings
        {
            DesktopFloatingGlassColor = "#12"
        });

        Assert.AreEqual("#4A90D9", normalized.DesktopFloatingGlassColor);
    }

    [TestMethod]
    public void Normalize_RejectsUnknownGameFilterMode()
    {
        var normalized = SettingsService.Normalize(new AppSettings { GameFilterMode = "Unsupported" });
        Assert.AreEqual("All", normalized.GameFilterMode);
    }

    [TestMethod]
    public void ModuleToggles_DefaultToEnabled()
    {
        var settings = new AppSettings();

        Assert.IsTrue(settings.GameSaveCenterEnabled);
        Assert.IsTrue(settings.DesktopIconHiderEnabled);
        Assert.IsTrue(settings.TaskbarLyricsModuleEnabled);
    }

    [TestMethod]
    public void ModuleToggles_RoundTripThroughJson_AndSurviveNormalize()
    {
        var settings = new AppSettings
        {
            GameSaveCenterEnabled = false,
            DesktopIconHiderEnabled = false,
            TaskbarLyricsModuleEnabled = false
        };

        var reloaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings))!;

        Assert.IsFalse(reloaded.GameSaveCenterEnabled);
        Assert.IsFalse(reloaded.DesktopIconHiderEnabled);
        Assert.IsFalse(reloaded.TaskbarLyricsModuleEnabled);

        var normalized = SettingsService.Normalize(reloaded);
        Assert.IsFalse(normalized.GameSaveCenterEnabled);
        Assert.IsFalse(normalized.DesktopIconHiderEnabled);
        Assert.IsFalse(normalized.TaskbarLyricsModuleEnabled);
    }

    [TestMethod]
    public void ModuleToggles_GateViewModelRuntimeSettings()
    {
        using var temp = new TemporaryDirectory();
        var steam = new SteamSwitcher.Core.SteamService();
        steam.SetCustomPaths(temp.Path, temp.Combine("loginusers.vdf"));
        using var accountManager = new SteamSwitcher.Core.AccountManager(
            steam,
            new SteamSwitcher.Core.AccountLibraryService(temp.Combine("accounts.json")),
            () => null,
            downloadAvatars: false);
        var bindings = new SteamSwitcher.Core.GameAccountBinding(temp.Combine("gamebindings.json"));
        var quickLaunch = new SteamSwitcher.Core.QuickLaunchService(temp.Combine("quicklaunch.json"));
        var settings = new AppSettings
        {
            GameSaveAutoBackupEnabled = true,
            DesktopEnhance = new DesktopEnhanceSettings { EnableAutoHideIcons = true }
        };
        using var vm = new SteamSwitcher.ViewModels.MainViewModel(
            accountManager,
            bindings,
            quickLaunch,
            new SettingsSession(settings, _ => true),
            () => false,
            _ => true,
            temp.Path,
            monitorSystemState: false);

        Assert.IsTrue(vm.CreateDesktopEnhanceSettings().EnableAutoHideIcons);
        Assert.AreNotEqual("模块已关闭", vm.GameSaveAutoBackupStatusText);

        vm.DesktopIconHiderEnabled = false;
        vm.GameSaveCenterEnabled = false;

        Assert.IsFalse(vm.CreateDesktopEnhanceSettings().EnableAutoHideIcons);
        Assert.AreEqual("模块已关闭", vm.GameSaveAutoBackupStatusText);
    }

    [TestMethod]
    public void TaskbarLyricsFontAndOffset_UseClearCjkDefaultAndFullSliderRange()
    {
        var defaults = SettingsService.Normalize(new AppSettings { TaskbarLyricsFontFamily = "   ", LyricOffsetMs = -2800 });
        Assert.AreEqual("Microsoft YaHei UI", defaults.TaskbarLyricsFontFamily);
        Assert.AreEqual(-2800, defaults.LyricOffsetMs);

        var family = SteamSwitcher.Views.TaskbarBandWindow.BuildLyricsFontFamily("LXGW WenKai");
        StringAssert.Contains(family.Source, "LXGW WenKai");
        StringAssert.Contains(family.Source, "Microsoft YaHei UI");
        StringAssert.Contains(family.Source, "Segoe UI Semibold");
    }
}
