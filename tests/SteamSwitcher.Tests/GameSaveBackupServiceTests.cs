using SteamSwitcher.Core;
using SteamSwitcher.Models;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class GameSaveBackupServiceTests
{
    [TestMethod]
    public async Task FullAndDifferentialBackup_RestoreVerifiedSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var steamRoot = temp.Combine("steam");
        var saveRoot = temp.Combine("saves", "demo");
        var backupRoot = temp.Combine("backups");
        Directory.CreateDirectory(steamRoot);
        Directory.CreateDirectory(saveRoot);
        var entry = CreateCustomEntry(saveRoot);
        var service = new GameSaveBackupService(steamRoot, backupRoot);

        await File.WriteAllTextAsync(System.IO.Path.Combine(saveRoot, "a.txt"), "version-one");
        var full = await service.BackupAsync(entry);
        Assert.IsFalse(full.IsDifferential);

        await File.WriteAllTextAsync(System.IO.Path.Combine(saveRoot, "a.txt"), "version-two");
        await File.WriteAllTextAsync(System.IO.Path.Combine(saveRoot, "b.txt"), "added-file");
        var differential = await service.BackupAsync(entry);
        Assert.IsTrue(differential.IsDifferential);
        await service.VerifyBackupAsync(entry, differential.FilePath);

        await File.WriteAllTextAsync(System.IO.Path.Combine(saveRoot, "a.txt"), "damaged-live-state");
        File.Delete(System.IO.Path.Combine(saveRoot, "b.txt"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(saveRoot, "obsolete.txt"), "remove-me");
        await service.RestoreAsync(entry, differential.FilePath);

        Assert.AreEqual("version-two", await File.ReadAllTextAsync(System.IO.Path.Combine(saveRoot, "a.txt")));
        Assert.AreEqual("added-file", await File.ReadAllTextAsync(System.IO.Path.Combine(saveRoot, "b.txt")));
        Assert.IsFalse(File.Exists(System.IO.Path.Combine(saveRoot, "obsolete.txt")));
    }

    [TestMethod]
    public async Task UnchangedBackup_IsSkipped_AndWrongIdentityIsRejected()
    {
        using var temp = new TemporaryDirectory();
        var steamRoot = temp.Combine("steam");
        var saveRoot = temp.Combine("saves", "demo");
        Directory.CreateDirectory(steamRoot);
        Directory.CreateDirectory(saveRoot);
        var service = new GameSaveBackupService(steamRoot, temp.Combine("backups"));
        var entry = CreateCustomEntry(saveRoot);
        await File.WriteAllTextAsync(System.IO.Path.Combine(saveRoot, "save.dat"), "stable");

        var backup = await service.BackupAsync(entry);
        var duplicate = await service.BackupAsync(entry);
        Assert.IsTrue(duplicate.WasSkipped);

        var wrongEntry = CreateCustomEntry(saveRoot);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            service.RestoreAsync(wrongEntry, backup.FilePath));
    }

    [TestMethod]
    public async Task RetentionLimit_PrunesOldestBackups()
    {
        using var temp = new TemporaryDirectory();
        var steamRoot = temp.Combine("steam");
        var saveRoot = temp.Combine("saves", "demo");
        var backupRoot = temp.Combine("backups");
        Directory.CreateDirectory(steamRoot);
        Directory.CreateDirectory(saveRoot);
        var entry = CreateCustomEntry(saveRoot);
        // Set manualRetention = 3
        var service = new GameSaveBackupService(steamRoot, backupRoot, manualRetention: 3, safetyRetention: 2);

        for (int i = 1; i <= 5; i++)
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(saveRoot, "save.dat"), $"content-{i}");
            await service.BackupAsync(entry);
            await Task.Delay(15);
        }

        var backups = service.GetBackups(entry);
        Assert.IsTrue(backups.Count <= 3, $"Expected at most 3 retained backups, but got {backups.Count}");
    }

    private static GameSaveEntry CreateCustomEntry(string saveRoot)
    {
        var customId = Guid.NewGuid().ToString("N");
        return new GameSaveEntry
        {
            EntryId = $"custom:{customId}",
            CustomId = customId,
            IsCustom = true,
            SteamId = "76561198000000000",
            AccountName = "test",
            AppId = 730,
            GameName = "Test Game",
            SavePath = saveRoot
        };
    }
}
