using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class SettingsImportTests
{
    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"foo\":1}")]
    [DataRow("{\"Format\":\"SteamSwitch.Configuration\",\"Settings\":{}}")]
    public async Task UnrelatedOrBundleJson_CannotResetUserSettings(string json)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.Combine("input.json");
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SettingsImportService.ReadAsync(path));
    }

    [TestMethod]
    public async Task ExportedSettings_AreRecognizedAndNormalized()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.Combine("settings.json");
        await File.WriteAllTextAsync(path, "{\"theme\":\"Light\",\"TaskbarLyricsWidth\":9999}");
        var settings = await SettingsImportService.ReadAsync(path);
        Assert.AreEqual("Light", settings.Theme);
        Assert.AreEqual(720, settings.TaskbarLyricsWidth);
    }
}
