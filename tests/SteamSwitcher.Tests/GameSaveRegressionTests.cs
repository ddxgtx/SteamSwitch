using System.IO.Compression;
using System.Reflection;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class GameSaveRegressionTests
{
    private static GameSaveEntry CreateEntry(string path) => new()
    {
        EntryId = "custom:123456781234123412341234567890ab",
        CustomId = "123456781234123412341234567890ab",
        IsCustom = true,
        SteamId = "76561198000000000",
        AccountName = "test",
        AppId = 730,
        GameName = "Test",
        SavePath = path
    };

    [TestMethod]
    public async Task RetentionOne_PreservesLatestRestorableSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var saves = temp.Combine("saves");
        Directory.CreateDirectory(saves);
        var entry = CreateEntry(saves);
        var service = new GameSaveBackupService(temp.Combine("steam"), temp.Combine("backups"), manualRetention: 1);
        var saveFile = Path.Combine(saves, "save.dat");
        await File.WriteAllTextAsync(saveFile, "one");
        await service.BackupAsync(entry);
        await Task.Delay(20);
        await File.WriteAllTextAsync(saveFile, "two");
        var latest = await service.BackupAsync(entry);
        Assert.HasCount(1, service.GetBackups(entry));
        await service.VerifyBackupAsync(entry, latest.FilePath);
        await File.WriteAllTextAsync(saveFile, "live");
        await service.RestoreLatestAsync(entry);
        Assert.AreEqual("two", await File.ReadAllTextAsync(saveFile));
    }

    [TestMethod]
    public async Task LowerRetentionToOne_KeepsLatestDifferentialAndItsBase()
    {
        using var temp = new TemporaryDirectory();
        var saves = temp.Combine("saves");
        Directory.CreateDirectory(saves);
        var entry = CreateEntry(saves);
        var service = new GameSaveBackupService(temp.Combine("steam"), temp.Combine("backups"));
        await File.WriteAllTextAsync(Path.Combine(saves, "save.dat"), "one");
        await service.BackupAsync(entry);
        await Task.Delay(20);
        await File.WriteAllTextAsync(Path.Combine(saves, "save.dat"), "two");
        var latest = await service.BackupAsync(entry);
        var reduced = new GameSaveBackupService(temp.Combine("steam"), temp.Combine("backups"), manualRetention: 1);
        reduced.PruneBackupsForEntry(entry);
        Assert.AreEqual(latest.FilePath, reduced.GetBackups(entry).First().FilePath);
        await reduced.VerifyBackupAsync(entry, latest.FilePath);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CorruptBackup_IsNotReused(bool differential)
    {
        using var temp = new TemporaryDirectory();
        var saves = temp.Combine("saves");
        Directory.CreateDirectory(saves);
        var entry = CreateEntry(saves);
        var service = new GameSaveBackupService(temp.Combine("steam"), temp.Combine("backups"));
        await File.WriteAllTextAsync(Path.Combine(saves, "save.dat"), "one");
        var backup = await service.BackupAsync(entry);
        if (differential)
        {
            await Task.Delay(20);
            await File.WriteAllTextAsync(Path.Combine(saves, "save.dat"), "two");
            backup = await service.BackupAsync(entry);
        }
        using (var zip = ZipFile.Open(backup.FilePath, ZipArchiveMode.Update))
        {
            zip.GetEntry("data/save.dat")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("data/save.dat").Open());
            writer.Write("bad");
        }
        var next = await service.BackupAsync(entry);
        Assert.IsFalse(next.WasSkipped);
        await service.VerifyBackupAsync(entry, next.FilePath);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WorkflowWithoutAvailableSave_DoesNotReportSuccess(bool missingCustomFolder)
    {
        using var temp = new TemporaryDirectory();
        using var accounts = new AccountManager();
        accounts.GetSteamService().SetCustomPaths(temp.Path, null);
        var settings = new AppSettings { SmartSaveDiscoveryEnabled = false };
        if (missingCustomFolder)
            settings.CustomGameSaveFolders.Add(new CustomGameSaveDefinition
            {
                GameName = "Test", FolderPath = temp.Combine("missing")
            });
        using var service = new GameSaveAutoBackupService(accounts, () => settings);
        var result = await service.BackupGameAsync("76561198000000000", 730, "Test");
        Assert.IsFalse(result.IsComplete);
        Assert.IsFalse(result.HasUsableBackup);
        Assert.AreEqual(missingCustomFolder ? 1 : 0, result.Unavailable);
    }

    [TestMethod]
    public async Task BeforeAccountSwitch_WaitsForBusyBackupGate()
    {
        using var temp = new TemporaryDirectory();
        using var accounts = new AccountManager();
        accounts.GetSteamService().SetCustomPaths(temp.Path, null);
        var settings = new AppSettings { GameSaveAutoBackupEnabled = true, SmartSaveDiscoveryEnabled = false };
        using var service = new GameSaveAutoBackupService(accounts, () => settings);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(GameSaveAutoBackupService).GetField("_enabled", flags)!.SetValue(service, true);
        var gate = (SemaphoreSlim)typeof(GameSaveAutoBackupService).GetField("_backupGate", flags)!.GetValue(service)!;
        await gate.WaitAsync();
        var released = false;
        try
        {
            var pending = service.BackupBeforeAccountSwitchAsync(null);
            Assert.IsFalse(pending.IsCompleted, "Switch backup must wait instead of silently skipping.");

            // Stopping the monitor must not cancel an in-flight switch backup.
            typeof(GameSaveAutoBackupService).GetMethod("StopMonitor", flags)?.Invoke(service, null);
            Assert.IsFalse(pending.IsCompleted, "Monitor stop must not cancel switch backup.");
            Assert.IsFalse(pending.IsCanceled);
            Assert.IsFalse(pending.IsFaulted);

            gate.Release();
            released = true;
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(pending.IsCompletedSuccessfully, "Switch backup must complete after the gate is released.");
        }
        finally { if (!released) gate.Release(); }
    }
}
