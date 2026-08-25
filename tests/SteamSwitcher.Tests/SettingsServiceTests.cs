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
}
