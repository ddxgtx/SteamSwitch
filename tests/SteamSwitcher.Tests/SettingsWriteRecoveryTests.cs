using System.Text.Json;
using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class SettingsWriteRecoveryTests
{
    [TestMethod]
    public void IncompleteStartupRead_ReportsUnsavedAndPreservesReadableOriginal()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.Combine("settings.json");
        var original = JsonSerializer.Serialize(new AppSettings { Theme = "Light", TaskbarLyricsWidth = 640 });
        File.WriteAllText(path, original);
        var session = new SettingsSession(new AppSettings(), settings =>
            SettingsService.SaveToDirectory(settings, temp.Path, primaryReadIncomplete: true));
        session.Current.TaskbarLyricsWidth = 500;
        session.MarkDirty();
        Assert.IsFalse(session.TrySave(), "Skipping a protected write must not be reported as success.");
        Assert.IsTrue(session.IsDirty, "Unsaved edits must remain pending.");
        Assert.AreEqual(original, File.ReadAllText(path));
        Assert.IsFalse(File.Exists(path + ".bak"));
    }

    [TestMethod]
    public void IncompleteStartupRead_DoesNotCreateDefaultsWhenOriginalIsUnavailable()
    {
        using var temp = new TemporaryDirectory();
        Assert.IsFalse(SettingsService.SaveToDirectory(new AppSettings(), temp.Path, primaryReadIncomplete: true));
        Assert.IsFalse(File.Exists(temp.Combine("settings.json")));
    }

    [TestMethod]
    public void CompleteStartupRead_CanSaveAndRotateThePreviousSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.Combine("settings.json");
        File.WriteAllText(path, "{\"Theme\":\"Dark\"}");
        Assert.IsTrue(SettingsService.SaveToDirectory(new AppSettings { Theme = "Light" }, temp.Path));
        Assert.AreEqual("Light", JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!.Theme);
        Assert.AreEqual("{\"Theme\":\"Dark\"}", File.ReadAllText(path + ".bak"));
    }
}
