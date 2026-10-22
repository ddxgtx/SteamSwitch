using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class AccountLifetimeTests
{
    private static AccountManager Manager(TemporaryDirectory directory)
    {
        var library = new AccountLibraryService(directory.Combine("accounts.json"));
        library.Upsert(new SteamAccount { SteamId = "76561198000000001", AccountName = "alice", PersonaName = "Alice" });
        return new AccountManager(new SteamService(), library, () => null, downloadAvatars: false);
    }

    private static SemaphoreSlim OperationGate(AccountManager manager) =>
        (SemaphoreSlim)typeof(AccountManager).GetField("_switchGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;

    [TestMethod]
    public async Task Dispose_CancelsQueuedOperations_WithoutDisposingTheActiveGate()
    {
        using var directory = new TemporaryDirectory();
        using var manager = Manager(directory);
        var gate = OperationGate(manager);
        await gate.WaitAsync();
        var pendingLoad = manager.LoadAccountsAsync();
        var pendingExport = manager.ExportAccountArchiveAsync(directory.Combine("export.json"));
        var pendingForget = manager.ForgetAccountAsync("76561198000000001");

        manager.Dispose();

        await pendingLoad.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<OperationCanceledException>(() => pendingExport.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<OperationCanceledException>(() => pendingForget.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsFalse(manager.DisposalCompletion.IsCompleted, "The active operation still owns the gate.");
        gate.Release(); // A real in-flight operation must still be able to leave normally.
        await manager.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        manager.Dispose();
    }

    [TestMethod]
    public async Task ActiveMetadataExport_CompletesNormally_WhenManagerIsDisposed()
    {
        using var directory = new TemporaryDirectory();
        using var manager = Manager(directory);
        var path = directory.Combine("export.json");
        await AtomicJsonFile.WriteAsync(path, new { Initial = true });
        var locks = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(AtomicJsonFile)
            .GetField("PathLocks", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var diskGate = locks[path];
        await diskGate.WaitAsync();
        var activeExport = manager.ExportAccountArchiveAsync(path);
        var queued = manager.ReloadAccountLibraryAsync(reloadAccounts: false);
        Assert.AreEqual(0, OperationGate(manager).CurrentCount);

        manager.Dispose();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsFalse(manager.DisposalCompletion.IsCompleted);
        diskGate.Release();

        await activeExport.WaitAsync(TimeSpan.FromSeconds(3));
        await manager.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue((await File.ReadAllTextAsync(path)).Contains("alice", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DisposedManager_RejectsNewWork_AndLoadSwitchCompleteWithoutHanging()
    {
        using var directory = new TemporaryDirectory();
        using var manager = Manager(directory);
        manager.Dispose();
        await manager.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        await manager.LoadAccountsAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(await manager.SwitchAccountAsync(new SteamAccount { SteamId = "1", AccountName = "alice" }));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => manager.ExportAccountArchiveAsync(directory.Combine("export.json")));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => manager.ReloadAccountLibraryAsync());
        Assert.IsFalse(File.Exists(directory.Combine("export.json")));
    }
}
