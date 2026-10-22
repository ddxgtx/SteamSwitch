using System.IO.Compression;
using System.Text.Json.Nodes;
using SteamSwitcher.Core;
using SteamSwitcher.Models;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class GameSaveRecoveryTests
{
    private const string SteamId = "76561198000000000";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingOrEmptyUserdata_StillDiscoversAndRestoresBackup(bool leaveEmptyDirectory)
    {
        using var temp = new TemporaryDirectory();
        var steam = temp.Combine("steam");
        var save = Path.Combine(steam, "userdata", GameSaveBackupService.ConvertSteamIdToUserDataId(SteamId), "730");
        Directory.CreateDirectory(save);
        await File.WriteAllTextAsync(Path.Combine(save, "save.dat"), "recovery");
        var service = new GameSaveBackupService(steam, temp.Combine("backups"));
        var accounts = new[] { new SteamAccount { SteamId = SteamId, AccountName = "test" } };
        var entries = await service.DiscoverAsync(accounts, Array.Empty<SteamLibraryGame>(), Array.Empty<CustomGameSaveDefinition>(), false);
        await service.BackupAsync(entries.Single());
        Directory.Delete(save, true);
        if (leaveEmptyDirectory) Directory.CreateDirectory(save);

        var recovery = (await service.DiscoverAsync(accounts, Array.Empty<SteamLibraryGame>(), Array.Empty<CustomGameSaveDefinition>(), false)).Single();
        Assert.IsFalse(recovery.IsAvailable, "Empty sources must not create automatic empty snapshots.");
        Assert.AreEqual(1, recovery.BackupCount);
        Assert.AreEqual(save, recovery.SavePath);
        await service.RestoreLatestAsync(recovery);
        Assert.AreEqual("recovery", await File.ReadAllTextAsync(Path.Combine(save, "save.dat")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingSmartSave_RestoresUsingKnownGameRulesIncludingLegacyManifest(bool legacyManifest)
    {
        using var temp = new TemporaryDirectory();
        var game = new SteamLibraryGame { AppId = 730, Name = "Audit Game", InstallDir = "AuditGame", LibraryPath = temp.Path };
        var save = temp.Combine("steamapps", "common", "AuditGame", "SaveGames");
        Directory.CreateDirectory(save);
        await File.WriteAllTextAsync(Path.Combine(save, "save.dat"), "smart-recovery");
        var service = new GameSaveBackupService(temp.Combine("steam"), temp.Combine("backups"));
        var original = (await service.DiscoverAsync(Array.Empty<SteamAccount>(), new[] { game })).Single();
        var backup = await service.BackupAsync(original);
        if (legacyManifest) EditManifest(backup.FilePath, manifest => { manifest.Remove("SourceSavePath"); manifest.Remove("IsDetected"); });
        Directory.Delete(save, true);

        var recovered = (await service.DiscoverAsync(Array.Empty<SteamAccount>(), new[] { game })).Single();
        Assert.AreEqual(original.EntryId, recovered.EntryId);
        Assert.AreEqual(save, recovered.SavePath);
        Assert.IsTrue(recovered.IsDetected);
        Assert.IsFalse(recovered.IsAvailable);
        await service.RestoreLatestAsync(recovered);
        Assert.AreEqual("smart-recovery", await File.ReadAllTextAsync(Path.Combine(save, "save.dat")));
    }

    [TestMethod]
    public async Task BackupIndex_DoesNotTrustSmartManifestPathOrInventedIdentity()
    {
        using var temp = new TemporaryDirectory();
        var game = new SteamLibraryGame { AppId = 730, Name = "Audit Game", InstallDir = "AuditGame", LibraryPath = temp.Path };
        var save = temp.Combine("steamapps", "common", "AuditGame", "SaveGames");
        Directory.CreateDirectory(save);
        await File.WriteAllTextAsync(Path.Combine(save, "save.dat"), "original");
        var service = new GameSaveBackupService(temp.Combine("steam"), temp.Combine("backups"));
        var original = (await service.DiscoverAsync(Array.Empty<SteamAccount>(), new[] { game })).Single();
        var backup = await service.BackupAsync(original);
        var foreignPath = temp.Combine("unrelated");
        Directory.CreateDirectory(foreignPath);
        await File.WriteAllTextAsync(Path.Combine(foreignPath, "sentinel"), "keep");
        EditManifest(backup.FilePath, manifest => manifest["SourceSavePath"] = foreignPath);
        Directory.Delete(save, true);
        var recovered = (await service.DiscoverAsync(Array.Empty<SteamAccount>(), new[] { game })).Single();
        Assert.AreEqual(save, recovered.SavePath, "A manifest path cannot redirect a known stable identity.");

        var inventedId = Guid.NewGuid().ToString("N");
        var inventedDirectory = temp.Combine("backups", "custom", inventedId);
        Directory.CreateDirectory(inventedDirectory);
        File.Copy(backup.FilePath, Path.Combine(inventedDirectory, "invented.zip"));
        EditManifest(Path.Combine(inventedDirectory, "invented.zip"), manifest => manifest["EntryId"] = "custom:" + inventedId);
        var entries = await service.DiscoverAsync(Array.Empty<SteamAccount>(), new[] { game });
        Assert.HasCount(1, entries);
        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(foreignPath, "sentinel")));
    }

    [TestMethod]
    public async Task BackupIndex_UserdataPathIsRebuiltFromStorageIdentity()
    {
        using var temp = new TemporaryDirectory();
        var steam = temp.Combine("steam");
        var entry = CreateSteamEntry(steam);
        Directory.CreateDirectory(entry.SavePath);
        await File.WriteAllTextAsync(Path.Combine(entry.SavePath, "save.dat"), "data");
        var service = new GameSaveBackupService(steam, temp.Combine("backups"));
        var backup = await service.BackupAsync(entry);
        EditManifest(backup.FilePath, manifest =>
        {
            manifest["SourceSavePath"] = temp.Combine("outside");
            manifest["UserDataId"] = "..\\outside";
        });
        Directory.Delete(entry.SavePath, true);
        var recovered = (await service.DiscoverAsync(Array.Empty<SteamAccount>(), Array.Empty<SteamLibraryGame>(), Array.Empty<CustomGameSaveDefinition>(), false)).Single();
        Assert.AreEqual(entry.SavePath, recovered.SavePath);
        await service.RestoreLatestAsync(recovered);
        Assert.AreEqual("data", await File.ReadAllTextAsync(Path.Combine(entry.SavePath, "save.dat")));
        Assert.IsFalse(Directory.Exists(temp.Combine("outside")));
    }

    [TestMethod]
    public async Task DifferentialExport_IsSelfContainedAndPreservesChangesAndDeletions()
    {
        using var temp = new TemporaryDirectory();
        var entry = CreateCustomEntry(temp.Combine("saves"));
        Directory.CreateDirectory(entry.SavePath);
        await File.WriteAllTextAsync(Path.Combine(entry.SavePath, "save.dat"), "one");
        await File.WriteAllTextAsync(Path.Combine(entry.SavePath, "deleted.dat"), "remove");
        var service = new GameSaveBackupService(temp.Combine("steam"), temp.Combine("backups"));
        await service.BackupAsync(entry);
        await File.WriteAllTextAsync(Path.Combine(entry.SavePath, "save.dat"), "two");
        File.Delete(Path.Combine(entry.SavePath, "deleted.dat"));
        await File.WriteAllTextAsync(Path.Combine(entry.SavePath, "new.dat"), "new");
        var differential = await service.BackupAsync(entry);
        Assert.IsTrue(differential.IsDifferential);
        var exported = temp.Combine("portable.zip");
        await service.ExportBackupAsync(entry, differential.FilePath, exported);
        Directory.Delete(temp.Combine("backups"), true);
        var imported = await service.ImportBackupAsync(entry, exported);
        await service.VerifyBackupAsync(entry, imported);
        await File.WriteAllTextAsync(Path.Combine(entry.SavePath, "save.dat"), "damaged");
        await service.RestoreAsync(entry, imported);
        Assert.AreEqual("two", await File.ReadAllTextAsync(Path.Combine(entry.SavePath, "save.dat")));
        Assert.AreEqual("new", await File.ReadAllTextAsync(Path.Combine(entry.SavePath, "new.dat")));
        Assert.IsFalse(File.Exists(Path.Combine(entry.SavePath, "deleted.dat")));
    }

    [TestMethod]
    public async Task FailedExport_PreservesDestinationAndDoesNotLeavePartialArchive()
    {
        using var temp = new TemporaryDirectory();
        var entry = CreateCustomEntry(temp.Combine("saves"));
        Directory.CreateDirectory(entry.SavePath);
        await File.WriteAllTextAsync(Path.Combine(entry.SavePath, "save.dat"), "one");
        var service = new GameSaveBackupService(temp.Combine("steam"), temp.Combine("backups"));
        var backup = await service.BackupAsync(entry);
        using (var zip = ZipFile.Open(backup.FilePath, ZipArchiveMode.Update)) zip.GetEntry("data/save.dat")!.Delete();
        var exported = temp.Combine("portable.zip");
        await File.WriteAllTextAsync(exported, "existing-export");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ExportBackupAsync(entry, backup.FilePath, exported));
        Assert.AreEqual("existing-export", await File.ReadAllTextAsync(exported));
        Assert.IsFalse(Directory.EnumerateFiles(temp.Path, "*.tmp").Any());
    }

    [TestMethod]
    public async Task CustomMigration_RequiresExplicitApiAndRestoresToConfirmedTarget()
    {
        using var temp = new TemporaryDirectory();
        var source = CreateCustomEntry(temp.Combine("old-saves"));
        Directory.CreateDirectory(source.SavePath);
        await File.WriteAllTextAsync(Path.Combine(source.SavePath, "save.dat"), "portable");
        var service = new GameSaveBackupService(temp.Combine("steam"), temp.Combine("backups"));
        var backup = await service.BackupAsync(source);
        var exported = temp.Combine("portable.zip");
        await service.ExportBackupAsync(source, backup.FilePath, exported);
        var target = CreateCustomEntry(temp.Combine("new-saves"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ImportBackupAsync(target, exported));
        var preview = await service.GetImportPreviewAsync(target, exported);
        Assert.IsFalse(preview.MatchesTarget);
        Assert.IsTrue(preview.CanReassociate);
        Assert.AreEqual(source.SavePath, preview.SourceSavePath);
        var imported = await service.ImportReassociatedCustomBackupAsync(target, exported, preview.ArchiveSha256);
        await service.VerifyBackupAsync(target, imported);
        await service.RestoreAsync(target, imported);
        Assert.AreEqual("portable", await File.ReadAllTextAsync(Path.Combine(target.SavePath, "save.dat")));
        Assert.IsTrue(File.Exists(backup.FilePath), "Migration must not move or rewrite the original archive.");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.VerifyBackupAsync(source, imported));
    }

    [TestMethod]
    public async Task CustomMigration_RejectsArchiveChangedSinceConfirmation()
    {
        using var temp = new TemporaryDirectory();
        var source = CreateCustomEntry(temp.Combine("old-saves"));
        Directory.CreateDirectory(source.SavePath);
        await File.WriteAllTextAsync(Path.Combine(source.SavePath, "save.dat"), "portable");
        var service = new GameSaveBackupService(temp.Combine("steam"), temp.Combine("backups"));
        var backup = await service.BackupAsync(source);
        var target = CreateCustomEntry(temp.Combine("new-saves"));
        var preview = await service.GetImportPreviewAsync(target, backup.FilePath);
        EditManifest(backup.FilePath, manifest => manifest["GameName"] = "changed-after-confirmation");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ImportReassociatedCustomBackupAsync(target, backup.FilePath, preview.ArchiveSha256));
        Assert.IsEmpty(service.GetBackups(target));
        Assert.IsFalse(Directory.Exists(target.SavePath));
    }

    [TestMethod]
    public async Task CustomMigration_DoesNotPermitUserdataIdentityBypass()
    {
        using var temp = new TemporaryDirectory();
        var steam = temp.Combine("steam");
        var source = CreateSteamEntry(steam);
        Directory.CreateDirectory(source.SavePath);
        await File.WriteAllTextAsync(Path.Combine(source.SavePath, "save.dat"), "userdata");
        var service = new GameSaveBackupService(steam, temp.Combine("backups"));
        var backup = await service.BackupAsync(source);
        var target = CreateCustomEntry(temp.Combine("custom"));
        var preview = await service.GetImportPreviewAsync(target, backup.FilePath);
        Assert.IsFalse(preview.CanReassociate);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ImportReassociatedCustomBackupAsync(target, backup.FilePath, preview.ArchiveSha256));
    }

    [TestMethod]
    public async Task ConcurrentSiblingRestores_KeepOtherTransactionsAndLegacyRecoveryDirectories()
    {
        using var temp = new TemporaryDirectory();
        var a = CreateCustomEntry(temp.Combine("saves", "A"));
        var b = CreateCustomEntry(temp.Combine("saves", "B"));
        Directory.CreateDirectory(a.SavePath);
        Directory.CreateDirectory(b.SavePath);
        await File.WriteAllTextAsync(Path.Combine(a.SavePath, "save.dat"), "A");
        await File.WriteAllTextAsync(Path.Combine(b.SavePath, "save.dat"), "B");
        var service = new GameSaveBackupService(temp.Combine("steam"), temp.Combine("backups"));
        var backupA = await service.BackupAsync(a);
        var backupB = await service.BackupAsync(b);
        var legacyDirectory = temp.Combine("saves", ".0.steamswitch-old-interrupted");
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "recovery.dat"), "preserve");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingA = service.RestoreAsync(a, backupA.FilePath, beforeCommit: async _ => { entered.SetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await service.RestoreAsync(b, backupB.FilePath); }
        finally { release.TrySetResult(); }
        await pendingA;
        Assert.AreEqual("A", await File.ReadAllTextAsync(Path.Combine(a.SavePath, "save.dat")));
        Assert.AreEqual("B", await File.ReadAllTextAsync(Path.Combine(b.SavePath, "save.dat")));
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(Path.Combine(legacyDirectory, "recovery.dat")));
        Assert.IsFalse(Directory.EnumerateDirectories(temp.Combine("saves"), ".steamswitch-*").Any());
    }

    [TestMethod]
    public void ExportFileName_UsesSafeTimestampAndSanitizedGameName()
    {
        var backup = new GameSaveBackupInfo { CreatedAt = new DateTime(2026, 10, 20, 15, 30, 45) };
        var name = backup.GetExportFileName("A:B/C*D?\"<E>|F");
        Assert.IsFalse(name.Any(character => Path.GetInvalidFileNameChars().Contains(character)));
        StringAssert.EndsWith(name, "-20261020-153045.zip");
        StringAssert.StartsWith(backup.GetExportFileName(""), "SteamSave-");
    }

    private static GameSaveEntry CreateCustomEntry(string path)
    {
        var id = Guid.NewGuid().ToString("N");
        return new GameSaveEntry { EntryId = "custom:" + id, CustomId = id, IsCustom = true, SteamId = "custom", AppId = 0, GameName = "Audit", SavePath = path };
    }

    private static GameSaveEntry CreateSteamEntry(string steam)
    {
        var userDataId = GameSaveBackupService.ConvertSteamIdToUserDataId(SteamId);
        return new GameSaveEntry { EntryId = $"steam:{SteamId}:730", SteamId = SteamId, UserDataId = userDataId, AppId = 730, GameName = "Audit", SavePath = Path.Combine(steam, "userdata", userDataId, "730") };
    }

    private static void EditManifest(string archivePath, Action<JsonObject> edit)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = archive.GetEntry("steamswitch-backup.json")!;
        JsonObject manifest;
        using (var reader = new StreamReader(entry.Open())) manifest = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        edit(manifest);
        entry.Delete();
        using var writer = new StreamWriter(archive.CreateEntry("steamswitch-backup.json").Open());
        writer.Write(manifest.ToJsonString());
    }
}
