using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SteamSwitcher.Core;
using SteamSwitcher.Models;

namespace SteamSwitcher.Tests;

[TestClass]
public class AccountLibraryServiceTests
{
    [TestMethod]
    public async Task HundredAccounts_RemainFindableAfterSteamListShrinks_AndCanBeBackedUp()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.Combine("accounts.json");
        var original = Enumerable.Range(0, 100)
            .Select(index => Account((76561198000000000L + index).ToString(), $"saved_{index:D3}", $"玩家 {index:D3}"))
            .ToArray();
        var library = new AccountLibraryService(path);
        library.MergeFromVdf(original, out _);
        await library.SaveAsync();

        // Simulate Steam retaining only a small subset of previously seen users.
        var visible = original.Take(5).ToArray();
        var reloaded = new AccountLibraryService(path);
        reloaded.MergeFromVdf(visible, out _);
        await reloaded.SaveAsync();
        var afterRestart = new AccountLibraryService(path);
        Assert.HasCount(100, afterRestart.Entries);
        var recoverable = afterRestart.BuildArchivedAccounts(visible);
        Assert.HasCount(95, recoverable);
        Assert.IsTrue(recoverable.Any(account => account.AccountName == "saved_099"));

        var backup = directory.Combine("export.json");
        await afterRestart.ExportArchiveAsync(backup);
        var imported = new AccountLibraryService(directory.Combine("imported.json"));
        Assert.AreEqual(100, await imported.ImportArchiveAsync(backup));
        Assert.HasCount(100, new AccountLibraryService(directory.Combine("imported.json")).Entries);
    }

    private static SteamAccount Account(string id, string name, string persona) => new()
    {
        SteamId = id,
        AccountName = name,
        PersonaName = persona,
        Timestamp = 1_700_000_000
    };

    [TestMethod]
    public void MergeFromVdf_AddsNewAccounts()
    {
        using var dir = new TemporaryDirectory();
        var service = new AccountLibraryService(dir.Combine("accounts.json"));

        var changed = service.MergeFromVdf(new[] { Account("1", "alice", "Alice"), Account("2", "bob", "Bob") }, out var added);

        Assert.IsTrue(changed);
        Assert.AreEqual(2, added, "Two brand-new accounts should be reported as added.");
        Assert.AreEqual(2, service.Entries.Count);
        CollectionAssert.AreEquivalent(
            new[] { "1", "2" },
            service.Entries.Select(e => e.SteamId).ToArray());
    }

    [TestMethod]
    public void MergeFromVdf_ReportsZeroAdded_WhenOnlyUpdatingExisting()
    {
        using var dir = new TemporaryDirectory();
        var service = new AccountLibraryService(dir.Combine("accounts.json"));

        service.MergeFromVdf(new[] { Account("1", "alice", "Alice") }, out _);
        var changed = service.MergeFromVdf(new[] { Account("1", "alice", "Renamed") }, out var added);

        Assert.IsTrue(changed, "A persona rename is a change that must be persisted.");
        Assert.AreEqual(0, added, "Updating an existing account is not a new save.");
    }

    [TestMethod]
    public void MergeFromVdf_UpdatesPersonaName_AndKeepsSteamIdKeyedEntry()
    {
        using var dir = new TemporaryDirectory();
        var service = new AccountLibraryService(dir.Combine("accounts.json"));

        service.MergeFromVdf(new[] { Account("1", "alice", "OldName") }, out _);
        var changed = service.MergeFromVdf(new[] { Account("1", "alice", "NewName") }, out _);

        Assert.IsTrue(changed, "Renaming a persona must be persisted.");
        Assert.AreEqual(1, service.Entries.Count);
        Assert.AreEqual("NewName", service.Entries.Single().PersonaName);
    }

    [TestMethod]
    public void MergeFromVdf_NoChange_ReportsFalse()
    {
        using var dir = new TemporaryDirectory();
        var service = new AccountLibraryService(dir.Combine("accounts.json"));

        var account = Account("1", "alice", "Alice");
        service.MergeFromVdf(new[] { account }, out _);

        // Same content; only LastSeenUnix advances, which is a change on the
        // first repeat, so seed a stable snapshot and merge identical data.
        var entry = service.Entries.Single();
        entry.LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.IsFalse(service.MergeFromVdf(new[] { account }, out _));
    }

    [TestMethod]
    public void MergeFromVdf_SkipsAccountsWithoutSteamId()
    {
        using var dir = new TemporaryDirectory();
        var service = new AccountLibraryService(dir.Combine("accounts.json"));

        Assert.IsFalse(service.MergeFromVdf(new[] { Account("", "ghost", "Ghost") }, out _));
        Assert.AreEqual(0, service.Entries.Count);
    }

    [TestMethod]
    public void BuildArchivedAccounts_ReturnsOnlyAccountsMissingFromVdf()
    {
        using var dir = new TemporaryDirectory();
        var service = new AccountLibraryService(dir.Combine("accounts.json"));

        service.MergeFromVdf(new[] { Account("1", "kept", "Kept"), Account("2", "pruned", "Pruned") }, out _);
        var stillOnSteam = new[] { Account("1", "kept", "Kept") };

        var archived = service.BuildArchivedAccounts(stillOnSteam);

        Assert.AreEqual(1, archived.Count);
        Assert.AreEqual("pruned", archived[0].AccountName);
        Assert.IsTrue(archived[0].IsArchived);
        Assert.IsFalse(archived[0].MostRecent);
        Assert.IsFalse(archived[0].RememberPassword);
    }

    [TestMethod]
    public void BuildArchivedAccounts_OrdersByMostRecentlySeen()
    {
        using var dir = new TemporaryDirectory();
        var service = new AccountLibraryService(dir.Combine("accounts.json"));

        service.MergeFromVdf(new[] { Account("old", "old", "Old") }, out _);
        var oldEntry = service.Entries.Single();
        oldEntry.LastSeenUnix = 1_000;

        service.MergeFromVdf(new[] { Account("recent", "recent", "Recent") }, out _);
        service.Entries.Single(e => e.SteamId == "recent").LastSeenUnix = 9_000;

        var archived = service.BuildArchivedAccounts(Array.Empty<SteamAccount>());

        CollectionAssert.AreEqual(
            new[] { "recent", "old" },
            archived.Select(a => a.AccountName).ToArray());
    }

    [TestMethod]
    public async Task SaveAndReload_RoundTripsEntries()
    {
        using var dir = new TemporaryDirectory();
        var path = dir.Combine("accounts.json");
        var service = new AccountLibraryService(path);
        service.MergeFromVdf(new[] { Account("1", "alice", "Alice") }, out _);
        service.Entries.Single().AvatarPath = @"C:\cache\1.png";
        await service.SaveAsync();

        Assert.IsTrue(File.Exists(path), "Library file should be written.");

        var reloaded = new AccountLibraryService(path);
        var entry = reloaded.Entries.Single();
        Assert.AreEqual("alice", entry.AccountName);
        Assert.AreEqual("Alice", entry.PersonaName);
        Assert.AreEqual(@"C:\cache\1.png", entry.AvatarPath);
    }

    [TestMethod]
    public async Task CorruptedLibrary_DoesNotThrow_AndStartsEmpty()
    {
        using var dir = new TemporaryDirectory();
        var path = dir.Combine("accounts.json");
        await File.WriteAllTextAsync(path, "{ this is not valid json ");

        var service = new AccountLibraryService(path);

        Assert.AreEqual(0, service.Entries.Count);
    }

    [TestMethod]
    public void EnsureUserEntry_CreatesCompleteEntry_WhenSteamPrunedIt()
    {
        var users = new Dictionary<string, object>
        {
            ["999"] = new Dictionary<string, object>
            {
                ["AccountName"] = "other",
                ["PersonaName"] = "Other",
                ["MostRecent"] = "1"
            }
        };

        var created = AccountLibraryService.EnsureUserEntry(
            users, Account("1", "restored", "Restored"));

        Assert.IsTrue(created);
        Assert.AreEqual(2, users.Count);

        var restored = (Dictionary<string, object>)users["1"];
        Assert.AreEqual("restored", restored["AccountName"]);
        Assert.AreEqual("Restored", restored["PersonaName"]);
        Assert.AreEqual("1", restored["MostRecent"]);
        Assert.AreEqual("1", restored["RememberPassword"]);
        Assert.IsTrue(long.TryParse(restored["Timestamp"]?.ToString(), out _));

        var demoted = (Dictionary<string, object>)users["999"];
        Assert.AreEqual("0", demoted["MostRecent"], "The previously active account must be demoted.");
    }

    [TestMethod]
    public void EnsureUserEntry_UpdatesExistingEntry_AndKeepsKnownNames()
    {
        var users = new Dictionary<string, object>
        {
            ["1"] = new Dictionary<string, object>
            {
                ["AccountName"] = "alice",
                ["PersonaName"] = "Alice",
                ["MostRecent"] = "0"
            }
        };

        var created = AccountLibraryService.EnsureUserEntry(
            users, Account("1", "alice", "Alice"));

        Assert.IsFalse(created, "Existing entries are updated, not recreated.");
        var data = (Dictionary<string, object>)users["1"];
        Assert.AreEqual("1", data["MostRecent"]);
        Assert.AreEqual("1", data["RememberPassword"]);
        Assert.AreEqual("alice", data["AccountName"]);
    }

    [TestMethod]
    public void EnsureUserEntry_FillsMissingNames_OnPartialEntry()
    {
        var users = new Dictionary<string, object>
        {
            ["1"] = new Dictionary<string, object>
            {
                ["PersonaName"] = "Alice",
                ["MostRecent"] = "0"
            }
        };

        AccountLibraryService.EnsureUserEntry(users, Account("1", "alice", "Alice"));

        var data = (Dictionary<string, object>)users["1"];
        Assert.AreEqual("alice", data["AccountName"], "A missing AccountName must be restored.");
        Assert.AreEqual("Alice", data["PersonaName"]);
    }

    [TestMethod]
    public void EnsureUserEntry_RestoredEntry_SurvivesVdfRoundTrip()
    {
        using var dir = new TemporaryDirectory();
        var parser = new VdfParser();
        var users = new Dictionary<string, object>
        {
            ["999"] = new Dictionary<string, object> { ["AccountName"] = "other", ["MostRecent"] = "1" }
        };

        AccountLibraryService.EnsureUserEntry(users, Account("1", "restored", "Restored"));

        var path = dir.Combine("loginusers.vdf");
        File.WriteAllText(path, parser.Serialize(new Dictionary<string, object> { ["users"] = users }));

        var reparsed = parser.Parse(path);
        var reparsedUsers = (Dictionary<string, object>)reparsed["users"];
        var restored = (Dictionary<string, object>)reparsedUsers["1"];

        Assert.AreEqual("restored", restored["AccountName"]);
        Assert.AreEqual("Restored", restored["PersonaName"]);
        Assert.AreEqual("1", restored["MostRecent"]);
        Assert.AreEqual("1", restored["RememberPassword"]);
    }
}
