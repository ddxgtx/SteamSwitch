using System.Text.Json;
using System.IO.Compression;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class BackupExportTests
{
    [TestMethod]
    public async Task PromptedExport_UsesChosenPathAndSuggestsConfiguredFolder()
    {
        using var temp = new TemporaryDirectory();
        var chosen = temp.Combine("chosen.json");
        var settings = new AppSettings { BackupExportDirectory = temp.Combine("exports"), AskBeforeBackupExport = true };
        var path = await BackupExportService.ExportAsync(settings, BackupExportKind.Settings, "settings.json",
            destination => File.WriteAllTextAsync(destination, "settings"), suggested =>
            {
                Assert.AreEqual(Path.Combine(settings.BackupExportDirectory, "Settings", "settings.json"), suggested);
                Assert.AreEqual(temp.Path, BackupExportService.GetInitialDirectory(suggested));
                return chosen;
            });
        Assert.AreEqual(chosen, path);
        Assert.AreEqual("settings", await File.ReadAllTextAsync(chosen));
        Assert.IsFalse(Directory.Exists(settings.BackupExportDirectory));
    }

    [TestMethod]
    public async Task GameSaveExport_UsesConfiguredFolderAndRemainsImportable()
    {
        using var temp = new TemporaryDirectory();
        var saves = temp.Combine("saves");
        Directory.CreateDirectory(saves);
        await File.WriteAllTextAsync(Path.Combine(saves, "save.dat"), "game progress");
        var entry = new GameSaveEntry
        {
            EntryId = "custom:123456781234123412341234567890ab", CustomId = "123456781234123412341234567890ab",
            IsCustom = true, SteamId = "76561198000000000", AccountName = "test", AppId = 730, GameName = "Test", SavePath = saves
        };
        var service = new GameSaveBackupService(temp.Combine("steam"), temp.Combine("internal-backups"));
        var snapshot = await service.BackupAsync(entry);
        var exported = await BackupExportService.ExportAsync(new AppSettings { BackupExportDirectory = temp.Combine("exports") },
            BackupExportKind.GameSaves, "game.zip", path => service.ExportBackupAsync(entry, snapshot.FilePath, path),
            _ => throw new AssertFailedException("Automatic game export asked for a path."));
        using (var zip = ZipFile.OpenRead(exported!)) Assert.IsTrue(zip.Entries.Any(item => item.FullName.EndsWith("save.dat")));
        var imported = await service.ImportBackupAsync(entry, exported!);
        await service.VerifyBackupAsync(entry, imported);
        Assert.IsTrue(File.Exists(snapshot.FilePath));
    }

    [TestMethod]
    public async Task AutomaticExports_CreateFoldersPreserveHistoryAndNeverAskForAPath()
    {
        using var temp = new TemporaryDirectory();
        var settings = new AppSettings { BackupExportDirectory = temp.Combine("new", "备份") };
        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => BackupExportService.ExportAsync(settings,
            BackupExportKind.Accounts, "accounts.json", path => File.WriteAllTextAsync(path, $"version {index}"),
            _ => throw new AssertFailedException("Automatic export unexpectedly asked for a path."))));
        Assert.AreEqual(8, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(paths.All(path => File.Exists(path) && Path.GetDirectoryName(path) == Path.Combine(settings.BackupExportDirectory, "Accounts")));
        var contents = await Task.WhenAll(paths.Select(path => File.ReadAllTextAsync(path!)));
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 8).Select(index => $"version {index}").ToArray(), contents);
        Assert.AreEqual(8, Directory.GetFiles(settings.BackupExportDirectory, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task CancelledLocationPrompt_WritesNothing()
    {
        using var temp = new TemporaryDirectory();
        var directory = temp.Combine("not-created");
        var result = await BackupExportService.ExportAsync(new AppSettings { BackupExportDirectory = directory, AskBeforeBackupExport = true },
            BackupExportKind.Configuration, "backup.steamswitch.json", _ => throw new AssertFailedException("A cancelled backup wrote data."), _ => null);
        Assert.IsNull(result);
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public async Task FailedAutomaticWrite_RemovesPartialFileAndKeepsPreviousBackup()
    {
        using var temp = new TemporaryDirectory();
        var settings = new AppSettings { BackupExportDirectory = temp.Path };
        var previous = await BackupExportService.ExportAsync(settings, BackupExportKind.Settings, "settings.json",
            path => File.WriteAllTextAsync(path, "good"), _ => null);
        await Assert.ThrowsExactlyAsync<IOException>(() => BackupExportService.ExportAsync(settings, BackupExportKind.Settings, "settings.json",
            async path => { await File.WriteAllTextAsync(path, "partial"); throw new IOException("disk failure"); }, _ => null));
        Assert.AreEqual("good", await File.ReadAllTextAsync(previous!));
        Assert.AreEqual(1, Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task PromptedExport_ValidatesTheRealChosenPathAndPreservesLiveAccountLibrary()
    {
        using var temp = new TemporaryDirectory();
        var libraryPath = temp.Combine("accounts.json");
        var library = new AccountLibraryService(libraryPath);
        await library.SaveAsync();
        var before = await File.ReadAllTextAsync(libraryPath);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => BackupExportService.ExportAsync(
            new AppSettings { BackupExportDirectory = temp.Path, AskBeforeBackupExport = true }, BackupExportKind.Accounts,
            "accounts.json", library.ExportArchiveAsync, _ => libraryPath));
        Assert.AreEqual(before, await File.ReadAllTextAsync(libraryPath));
    }

    [TestMethod]
    public async Task InvalidOrBlockedDirectory_FailsWithoutSilentlySavingElsewhere()
    {
        using var temp = new TemporaryDirectory();
        var called = false;
        var settings = new AppSettings { BackupExportDirectory = "relative-path" };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => BackupExportService.ExportAsync(settings, BackupExportKind.Accounts,
            "accounts.json", _ => { called = true; return Task.CompletedTask; }, _ => null));
        settings.BackupExportDirectory = temp.Combine("occupied");
        await File.WriteAllTextAsync(settings.BackupExportDirectory, "existing file");
        await Assert.ThrowsAsync<IOException>(() => BackupExportService.ExportAsync(settings, BackupExportKind.Accounts,
            "accounts.json", _ => { called = true; return Task.CompletedTask; }, _ => null));
        Assert.IsFalse(called);
        Assert.AreEqual("existing file", await File.ReadAllTextAsync(settings.BackupExportDirectory));
    }

    [TestMethod]
    public async Task ConfigurationExport_RetainsImportableExtensionAndSettingsPolicy()
    {
        using var temp = new TemporaryDirectory();
        var settings = new AppSettings { BackupExportDirectory = temp.Path, AskBeforeBackupExport = false };
        var path = await BackupExportService.ExportAsync(settings, BackupExportKind.Configuration, "backup.steamswitch.json",
            destination => ConfigurationBundleService.ExportAsync(new ConfigurationBundle { Settings = settings }, destination), _ => null);
        Assert.IsTrue(path!.EndsWith(".steamswitch.json", StringComparison.OrdinalIgnoreCase));
        var loaded = await ConfigurationBundleService.ReadAsync(path);
        Assert.AreEqual(temp.Path, loaded.Settings.BackupExportDirectory);
        Assert.IsFalse(loaded.Settings.AskBeforeBackupExport);
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { BackupExportDirectory = temp.Path, AskBeforeBackupExport = true }))!;
        Assert.IsTrue(copy.AskBeforeBackupExport);
        Assert.AreEqual(temp.Path, SettingsSession.Copy(copy).BackupExportDirectory);
    }
}
