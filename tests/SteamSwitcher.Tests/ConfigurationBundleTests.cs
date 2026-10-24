using System.Text.Json;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class ConfigurationBundleTests
{
    private static ConfigurationBundle Sample() => new()
    {
        Settings = new AppSettings { Theme = "Light", TaskbarLyricsWidth = 640 },
        Accounts = new() { new SavedAccountEntry { SteamId = "76561198000000001", AccountName = "example", AvatarPath = "private-avatar-path" } },
        GameBindings = new() { [10] = new GameBinding { AppId = 10, AccountSteamId = "76561198000000001", LaunchArguments = "-windowed" } },
        QuickLaunch = new() { new QuickLaunchItem { Id = "test-item", Name = "test", ExecutablePath = "C:\\Tools\\test.exe" } }
    };

    [TestMethod]
    public async Task Bundle_RoundTripsAllFourStores_WithoutMachineLocalAccountData()
    {
        using var temp = new TemporaryDirectory();
        var bundlePath = temp.Combine("export.steamswitch.json");
        await ConfigurationBundleService.ExportAsync(Sample(), bundlePath);
        var json = await File.ReadAllTextAsync(bundlePath);
        Assert.IsFalse(json.Contains("private-avatar-path"));
        var bundle = await ConfigurationBundleService.ReadAsync(bundlePath);
        await new ConfigurationBundleService(temp.Combine("imported")).ImportAsync(bundle);
        var reloaded = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(temp.Combine("imported", "settings.json")))!;
        Assert.AreEqual(640, reloaded.TaskbarLyricsWidth);
        Assert.AreEqual("Light", reloaded.Theme);
        Assert.IsTrue(File.Exists(temp.Combine("imported", "accounts.json")));
        Assert.IsTrue((await File.ReadAllTextAsync(temp.Combine("imported", "gamebindings.json"))).Contains("-windowed"));
        Assert.IsTrue((await File.ReadAllTextAsync(temp.Combine("imported", "quicklaunch.json"))).Contains("test-item"));
        Assert.IsFalse(File.Exists(temp.Combine("imported", "configuration-import-pending.json")));
    }

    [TestMethod]
    public async Task PartialWriteFailure_RollsBackEveryStoreAndItsPreviousBackup()
    {
        using var temp = new TemporaryDirectory();
        await File.WriteAllTextAsync(temp.Combine("settings.json"), "original settings");
        await File.WriteAllTextAsync(temp.Combine("accounts.json"), "original accounts");
        await File.WriteAllTextAsync(temp.Combine("accounts.json.bak"), "older accounts");
        var service = new ConfigurationBundleService(temp.Path, index =>
        {
            if (index == 2) throw new IOException("simulated full disk");
        });
        await Assert.ThrowsExactlyAsync<IOException>(() => service.ImportAsync(Sample()));
        Assert.AreEqual("original settings", await File.ReadAllTextAsync(temp.Combine("settings.json")));
        Assert.AreEqual("original accounts", await File.ReadAllTextAsync(temp.Combine("accounts.json")));
        Assert.AreEqual("older accounts", await File.ReadAllTextAsync(temp.Combine("accounts.json.bak")));
        Assert.IsFalse(File.Exists(temp.Combine("gamebindings.json")));
        Assert.IsFalse(File.Exists(temp.Combine("configuration-import-pending.json")));
    }

    [TestMethod]
    public void InterruptedImport_IsRecoveredBeforeSettingsCanBeLoaded()
    {
        using var temp = new TemporaryDirectory();
        var name = ".configuration-import-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temp.Combine(name));
        File.WriteAllText(temp.Combine(name, "settings.json"), "original");
        File.WriteAllText(temp.Combine("settings.json"), "partially imported");
        File.WriteAllText(temp.Combine("accounts.json"), "new file");
        File.WriteAllText(temp.Combine("configuration-import-pending.json"), JsonSerializer.Serialize(new
        {
            TransactionName = name,
            ExistingFiles = new[] { "settings.json" }
        }));
        ConfigurationBundleService.RecoverInterruptedImport(temp.Path);
        Assert.AreEqual("original", File.ReadAllText(temp.Combine("settings.json")));
        Assert.IsFalse(File.Exists(temp.Combine("accounts.json")));
        Assert.IsFalse(Directory.Exists(temp.Combine(name)));
    }

    [TestMethod]
    public async Task UnrelatedJson_IsRejectedWithoutReplacingConfiguration()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.Combine("unrelated.json");
        await File.WriteAllTextAsync(path, "{}");
        await Assert.ThrowsExactlyAsync<System.IO.InvalidDataException>(() => ConfigurationBundleService.ReadAsync(path));
    }

    [TestMethod]
    public async Task RuntimeFailure_WhenRollbackIsBlocked_PreservesJournalAndOriginalsForRecovery()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.Combine("settings.json");
        await File.WriteAllTextAsync(path, "original settings");
        await File.WriteAllTextAsync(temp.Combine("accounts.json"), "original accounts");
        FileStream? blocker = null;
        try
        {
            Exception? rollbackError = null;
            try
            {
                await new ConfigurationBundleService(temp.Path).ImportAsync(Sample(), applyRuntime: () =>
                {
                    blocker = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw new InvalidOperationException("runtime refresh failed while another reader holds settings");
                });
            }
            catch (Exception ex) { rollbackError = ex; }
            Assert.IsTrue(rollbackError is IOException or UnauthorizedAccessException,
                "The file lock must block rollback to exercise recovery.");
            Assert.IsTrue(File.Exists(temp.Combine("configuration-import-pending.json")),
                "Failed rollback must retain a recovery journal rather than silently abandoning mixed stores.");
            Assert.HasCount(1, Directory.GetDirectories(temp.Path, ".configuration-import-*"),
                "Failed rollback must not delete the only complete copies of the originals.");
        }
        finally { blocker?.Dispose(); }

        ConfigurationBundleService.RecoverInterruptedImport(temp.Path);
        Assert.AreEqual("original settings", await File.ReadAllTextAsync(path));
        Assert.AreEqual("original accounts", await File.ReadAllTextAsync(temp.Combine("accounts.json")));
        Assert.IsFalse(File.Exists(temp.Combine("gamebindings.json")));
        Assert.IsFalse(File.Exists(temp.Combine("quicklaunch.json")));
        Assert.IsFalse(File.Exists(temp.Combine("configuration-import-pending.json")));
    }

    [TestMethod]
    public void InvalidRecoveryJournal_IsQuarantinedWithoutDeletingOriginalSnapshots()
    {
        using var temp = new TemporaryDirectory();
        var staging = temp.Combine(".configuration-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "settings.json"), "only remaining original settings");
        File.WriteAllText(temp.Combine("configuration-import-pending.json"), "invalid journal");
        ConfigurationBundleService.DiscardInterruptedImport(temp.Path);
        Assert.IsFalse(File.Exists(temp.Combine("configuration-import-pending.json")));
        Assert.HasCount(1, Directory.GetFiles(temp.Path, "configuration-import-pending.json.failed-*"));
        Assert.AreEqual("only remaining original settings", File.ReadAllText(Path.Combine(staging, "settings.json")));
    }

    [TestMethod]
    public void NullRecoveryTransaction_IsRejectedWithoutTouchingConfiguration()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(temp.Combine("settings.json"), "current settings");
        File.WriteAllText(temp.Combine("configuration-import-pending.json"), "{\"TransactionName\":null,\"ExistingFiles\":[]}");
        Assert.ThrowsExactly<InvalidDataException>(() => ConfigurationBundleService.RecoverInterruptedImport(temp.Path));
        Assert.AreEqual("current settings", File.ReadAllText(temp.Combine("settings.json")));
    }

    [TestMethod]
    public async Task RuntimeApplyFailure_AlsoRollsBackFiles()
    {
        using var temp = new TemporaryDirectory();
        await File.WriteAllTextAsync(temp.Combine("settings.json"), "original");
        var service = new ConfigurationBundleService(temp.Path);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ImportAsync(Sample(), applyRuntime:
            () => throw new InvalidOperationException("interface refresh failed")));
        Assert.AreEqual("original", await File.ReadAllTextAsync(temp.Combine("settings.json")));
        Assert.IsFalse(File.Exists(temp.Combine("accounts.json")));
    }

    [TestMethod]
    public void EmptyAccountName_IsRejectedBeforeAnyFileIsWritten()
    {
        var sample = Sample();
        sample.Accounts[0].AccountName = "";
        Assert.ThrowsExactly<InvalidDataException>(() => ConfigurationBundleService.Prepare(sample));
    }

    [TestMethod]
    public void IgnoredAccounts_AreExportedAsTombstonesWithoutNames()
    {
        var sample = Sample();
        sample.Accounts[0].IsIgnored = true;
        var clean = ConfigurationBundleService.Prepare(sample);
        Assert.IsTrue(clean.Accounts[0].IsIgnored);
        Assert.AreEqual(string.Empty, clean.Accounts[0].AccountName);
        Assert.IsNull(clean.Accounts[0].AvatarPath);
    }
}
