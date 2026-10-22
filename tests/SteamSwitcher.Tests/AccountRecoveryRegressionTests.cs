using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SteamSwitcher.Core;
using SteamSwitcher.Models;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class AccountRecoveryRegressionTests
{
    private static SteamAccount Account(string id = "76561198000000001", string name = "alice") => new()
    {
        SteamId = id, AccountName = name, PersonaName = name,
        Timestamp = 1_700_000_000
    };

    private static AccountManager Manager(TemporaryDirectory directory, AccountLibraryService library)
    {
        var steam = new SteamService();
        steam.SetCustomPaths(directory.Path, directory.Combine("config", "loginusers.vdf"));
        return new AccountManager(steam, library, () => null, downloadAvatars: false);
    }

    private static string WriteVdf(TemporaryDirectory directory, params SteamAccount[] accounts)
    {
        var users = accounts.ToDictionary(account => account.SteamId, account => (object)new Dictionary<string, object>
        {
            ["AccountName"] = account.AccountName, ["PersonaName"] = account.PersonaName,
            ["MostRecent"] = "0", ["RememberPassword"] = "0"
        });
        var path = directory.Combine("config", "loginusers.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new VdfParser().Serialize(new Dictionary<string, object> { ["users"] = users }));
        return path;
    }

    [TestMethod]
    public async Task ExistingLibrary_ConstructionOnUiContext_DoesNotWaitForDispatcher()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.Combine("accounts.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[]
        {
            new SavedAccountEntry { SteamId = "1", AccountName = "alice", PersonaName = "Alice" }
        }));
        var context = new QueuedContext();
        using var completed = new ManualResetEventSlim();
        Exception? failure = null;
        var count = 0;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try { count = new AccountLibraryService(path).Entries.Count; }
            catch (Exception ex) { failure = ex; }
            finally { completed.Set(); }
        }) { IsBackground = true };
        thread.Start();
        var finishedWithoutPumping = completed.Wait(TimeSpan.FromSeconds(2));
        // Unblock a regressed implementation so a failed test leaves no worker behind.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!completed.IsSet && DateTime.UtcNow < deadline)
        {
            if (context.Queue.TryDequeue(out var item)) item.Callback(item.State);
            else Thread.Sleep(5);
        }
        Assert.IsTrue(finishedWithoutPumping, "Constructor captured and waited for the blocked UI context.");
        Assert.IsNull(failure);
        Assert.AreEqual(1, count);
        Assert.AreEqual(0, context.Queue.Count);
    }

    [TestMethod]
    public async Task MissingVdf_ShowsSavedMetadata_WithoutCreatingSteamFiles()
    {
        using var directory = new TemporaryDirectory();
        var library = new AccountLibraryService(directory.Combine("accounts.json"));
        library.Upsert(Account());
        await library.SaveAsync();
        using var manager = Manager(directory, library);

        Assert.IsTrue(await manager.InitializeAsync());
        Assert.HasCount(1, manager.Accounts);
        Assert.IsTrue(manager.Accounts[0].IsArchived);
        Assert.IsNotNull(manager.AccountSourceWarning);
        Assert.IsNull(manager.CurrentAccount);
        Assert.IsFalse(File.Exists(directory.Combine("config", "loginusers.vdf")));
    }

    [TestMethod]
    public async Task CorruptVdf_ShowsSavedMetadata_AndSwitchDoesNotChangeSource()
    {
        using var directory = new TemporaryDirectory();
        var account = Account();
        var library = new AccountLibraryService(directory.Combine("accounts.json"));
        library.Upsert(account);
        var path = WriteVdf(directory, account);
        const string corrupt = "\"users\" { \"76561198000000001\" { \"AccountName\" \"alice";
        await File.WriteAllTextAsync(path, corrupt);
        using var manager = Manager(directory, library);
        var beforeSwitchCalled = false;
        manager.BeforeAccountSwitchAsync = _ => { beforeSwitchCalled = true; return Task.CompletedTask; };

        await manager.LoadAccountsAsync();
        Assert.HasCount(1, manager.Accounts);
        Assert.IsTrue(manager.Accounts[0].IsArchived);
        Assert.IsFalse(await manager.SwitchAccountAsync(account));
        Assert.IsFalse(await manager.UpdateLoginUsersVdfAsync(account));
        Assert.IsFalse(beforeSwitchCalled);
        Assert.AreEqual(corrupt, await File.ReadAllTextAsync(path));
        Assert.IsFalse(File.Exists(path + ".steamswitch.bak"));
    }

    [TestMethod]
    public async Task Forget_IsPersisted_HidesSteamEntry_UntilCollectionIsRestored()
    {
        using var directory = new TemporaryDirectory();
        var account = Account();
        var path = WriteVdf(directory, account);
        var source = await File.ReadAllTextAsync(path);
        var libraryPath = directory.Combine("accounts.json");
        using (var manager = Manager(directory, new AccountLibraryService(libraryPath)))
        {
            await manager.LoadAccountsAsync();
            Assert.IsTrue(await manager.ForgetAccountAsync(account.SteamId));
            Assert.HasCount(0, manager.Accounts);
            Assert.HasCount(1, manager.IgnoredAccountIds);
        }
        var reloaded = new AccountLibraryService(libraryPath);
        Assert.HasCount(0, reloaded.Entries);
        Assert.IsFalse((await File.ReadAllTextAsync(libraryPath)).Contains("alice", StringComparison.Ordinal));
        using var restored = Manager(directory, reloaded);
        await restored.LoadAccountsAsync();
        Assert.HasCount(0, restored.Accounts);
        Assert.IsTrue(await restored.RestoreAccountCollectionAsync(account.SteamId));
        Assert.HasCount(1, restored.Accounts);
        Assert.AreEqual(source, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task MetadataArchive_RoundTrips_WithoutPathsOrCredentials_AndRespectsIgnoredIds()
    {
        using var directory = new TemporaryDirectory();
        var source = new AccountLibraryService(directory.Combine("source.json"));
        var account = Account();
        account.AvatarPath = @"C:\private\avatar.png";
        source.Upsert(account);
        var exportPath = directory.Combine("export.json");
        await source.ExportArchiveAsync(exportPath);
        var exported = await File.ReadAllTextAsync(exportPath);
        Assert.IsFalse(exported.Contains("AvatarPath", StringComparison.Ordinal));
        Assert.IsFalse(exported.Contains("RememberPassword", StringComparison.Ordinal));
        Assert.IsFalse(exported.Contains("private", StringComparison.Ordinal));

        var target = new AccountLibraryService(directory.Combine("target.json"));
        Assert.AreEqual(1, await target.ImportArchiveAsync(exportPath));
        Assert.IsNull(target.Entries.Single().AvatarPath);
        await target.ForgetAsync(account.SteamId);
        Assert.AreEqual(0, await target.ImportArchiveAsync(exportPath));
        Assert.HasCount(0, target.Entries);
        await target.RestoreCollectionAsync(account.SteamId);
        Assert.AreEqual(1, await target.ImportArchiveAsync(exportPath));
    }

    [TestMethod]
    public async Task LegacyMetadataImport_DiscardsExtraCredentialFields_AndRejectsInvalidBatchAtomically()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.Combine("legacy.json");
        await File.WriteAllTextAsync(path,
            "[{\"SteamId\":\"1\",\"AccountName\":\"alice\",\"PersonaName\":\"Alice\",\"AvatarPath\":\"C:/private.png\",\"Password\":\"secret\",\"RefreshToken\":\"token\"}]");
        var library = new AccountLibraryService(directory.Combine("accounts.json"));
        Assert.AreEqual(1, await library.ImportArchiveAsync(path));
        var persisted = await File.ReadAllTextAsync(library.LibraryPath);
        Assert.IsFalse(persisted.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(persisted.Contains("token", StringComparison.Ordinal));
        Assert.IsFalse(persisted.Contains("private", StringComparison.Ordinal));
        await File.WriteAllTextAsync(path,
            "[{\"SteamId\":\"2\",\"AccountName\":\"bob\"},{\"SteamId\":\"../bad\",\"AccountName\":\"bad\"}]");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => library.ImportArchiveAsync(path));
        Assert.AreEqual(persisted, await File.ReadAllTextAsync(library.LibraryPath));
        Assert.AreEqual("1", library.Entries.Single().SteamId);
    }

    [TestMethod]
    public async Task AccountEntriesImport_PreservesIgnoreTombstones_InSafeSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var library = new AccountLibraryService(directory.Combine("accounts.json"));
        await library.ImportEntriesAsync(new[]
        {
            new SavedAccountEntry { SteamId = "1", IsIgnored = true, AccountName = "forget-me", AvatarPath = "private" },
            new SavedAccountEntry { SteamId = "2", AccountName = "bob", PersonaName = "Bob", AvatarPath = "private" }
        });
        var snapshot = library.GetArchiveEntries();
        Assert.HasCount(2, snapshot);
        Assert.AreEqual(string.Empty, snapshot.Single(entry => entry.IsIgnored).AccountName);
        Assert.IsTrue(snapshot.All(entry => entry.AvatarPath == null));
        Assert.IsTrue(new AccountLibraryService(library.LibraryPath).IsIgnored("1"));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"unrelated\":true}")]
    [DataRow("{\"FormatVersion\":1}")]
    [DataRow("{\"Accounts\":[]}")]
    [DataRow("{\"FormatVersion\":1,\"Accounts\":null}")]
    [DataRow("{\"FormatVersion\":2,\"Accounts\":[]}")]
    [DataRow("{\"FormatVersion\":1,\"formatversion\":1,\"Accounts\":[]}")]
    [DataRow("{\"FormatVersion\":1,\"Accounts\":[],\"accounts\":[]}")]
    [DataRow("null")]
    public async Task UnrecognizedMetadataObject_IsRejectedWithoutRotatingLibraryOrBackup(string json)
    {
        using var directory = new TemporaryDirectory();
        var library = new AccountLibraryService(directory.Combine("accounts.json"));
        library.Upsert(Account());
        await library.SaveAsync();
        await library.SaveAsync();
        var primary = await File.ReadAllBytesAsync(library.LibraryPath);
        var backup = await File.ReadAllBytesAsync(library.LibraryPath + ".bak");
        var importedPath = directory.Combine("import.json");
        await File.WriteAllTextAsync(importedPath, json);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => library.ImportArchiveAsync(importedPath));

        Assert.HasCount(1, library.Entries);
        CollectionAssert.AreEqual(primary, await File.ReadAllBytesAsync(library.LibraryPath));
        CollectionAssert.AreEqual(backup, await File.ReadAllBytesAsync(library.LibraryPath + ".bak"));
    }

    [TestMethod]
    public async Task MetadataObject_RecognizesRequiredFieldsIgnoringCase()
    {
        using var directory = new TemporaryDirectory();
        var importedPath = directory.Combine("import.json");
        await File.WriteAllTextAsync(importedPath,
            "{\"formatversion\":1,\"accounts\":[{\"SteamId\":\"1\",\"AccountName\":\"alice\",\"PersonaName\":\"Alice\"}]}");
        var library = new AccountLibraryService(directory.Combine("accounts.json"));
        Assert.AreEqual(1, await library.ImportArchiveAsync(importedPath));
        Assert.AreEqual("alice", library.Entries.Single().AccountName);
    }

    [TestMethod]
    public async Task UpdatingVdf_PreservesOtherAccountsAndUnknownFields_AndKeepsPreSwitchBackup()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteVdf(directory, Account(), Account("76561198000000002", "bob"));
        var parser = new VdfParser();
        var data = parser.Parse(path);
        var users = (Dictionary<string, object>)data["users"];
        ((Dictionary<string, object>)users["76561198000000002"])["CustomField"] = "preserve-me";
        await File.WriteAllTextAsync(path, parser.Serialize(data));
        var original = await File.ReadAllBytesAsync(path);
        using var manager = Manager(directory, new AccountLibraryService(directory.Combine("accounts.json")));
        Assert.IsTrue(await manager.UpdateLoginUsersVdfAsync(Account("76561198000000003", "carol")));
        var updated = (Dictionary<string, object>)parser.Parse(path)["users"];
        Assert.HasCount(3, updated);
        Assert.AreEqual("preserve-me", ((Dictionary<string, object>)updated["76561198000000002"])["CustomField"]);
        Assert.AreEqual("1", ((Dictionary<string, object>)updated["76561198000000003"])["MostRecent"]);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path + ".steamswitch.bak"));
    }

    [TestMethod]
    [DataRow("\"users\" { \"76561198000000001\" \"scalar-entry\" }")]
    [DataRow("\"users\" { \"76561198000000001\" { \"PersonaName\" \"Missing account name\" } }")]
    [DataRow("\"users\" { \"../bad\" { \"AccountName\" \"alice\" } }")]
    public async Task InvalidAccountSection_FallsBackToLibrary_AndIsNeverRewritten(string content)
    {
        using var directory = new TemporaryDirectory();
        var path = WriteVdf(directory, Account());
        await File.WriteAllTextAsync(path, content);
        var library = new AccountLibraryService(directory.Combine("accounts.json"));
        library.Upsert(Account());
        using var manager = Manager(directory, library);
        await manager.LoadAccountsAsync();
        Assert.IsTrue(manager.Accounts.Single().IsArchived);
        Assert.IsNotNull(manager.AccountSourceWarning);
        Assert.IsFalse(await manager.UpdateLoginUsersVdfAsync(Account()));
        Assert.AreEqual(content, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task StaleTargetAccountName_IsRejected_WithoutChangingVdf()
    {
        using var directory = new TemporaryDirectory();
        var path = WriteVdf(directory, Account());
        var original = await File.ReadAllTextAsync(path);
        using var manager = Manager(directory, new AccountLibraryService(directory.Combine("accounts.json")));
        Assert.IsFalse(await manager.UpdateLoginUsersVdfAsync(Account(name: "stale-name")));
        Assert.AreEqual(original, await File.ReadAllTextAsync(path));
        Assert.IsFalse(File.Exists(path + ".steamswitch.bak"));
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        public ConcurrentQueue<(SendOrPostCallback Callback, object? State)> Queue { get; } = new();
        public override void Post(SendOrPostCallback callback, object? state) => Queue.Enqueue((callback, state));
    }
}
