using System.Collections.Concurrent;
using System.Reflection;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class CollectionPersistenceRecoveryTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FailedEdit_DoesNotLeakChangedPropertiesIntoExistingItems(bool quickLaunch)
    {
        using var rig = new Rig(quickLaunch);
        await rig.AddAsync(1);
        rig.Notifications = 0;
        using var locked = new FileStream(rig.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<IOException>(rig.EditAsync);
        Assert.IsTrue(rig.HasOriginalProperties);
        Assert.AreEqual(0, rig.Notifications);
    }

    [TestMethod]
    public async Task QuickLaunch_AddAfterRemovingMiddleItem_AppendsWithUniqueOrder()
    {
        using var temp = new TemporaryDirectory();
        using var service = new QuickLaunchService(temp.Combine("quicklaunch.json"));
        await service.AddAsync("first", temp.Combine("missing.exe"), iconPath: "");
        var middle = await service.AddAsync("middle", temp.Combine("missing.exe"), iconPath: "");
        await service.AddAsync("last", temp.Combine("missing.exe"), iconPath: "");
        await service.RemoveAsync(middle.Id);
        await service.AddAsync("new", temp.Combine("missing.exe"), iconPath: "");
        await service.LoadAsync();
        var items = service.GetAll();
        CollectionAssert.AreEqual(new[] { "first", "last", "new" }, items.Select(item => item.Name).ToArray());
        Assert.AreEqual(3, items.Select(item => item.SortOrder).Distinct().Count());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReloadAndQueuedMutation_PublishTheSameCompleteSnapshotAsDisk(bool quickLaunch)
    {
        using var rig = new Rig(quickLaunch);
        await rig.AddAsync(1);
        var locks = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(AtomicJsonFile)
            .GetField("PathLocks", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var gate = locks[rig.Path];
        await gate.WaitAsync();
        Task loading;
        Task editing;
        try
        {
            loading = rig.LoadAsync();
            editing = rig.AddAsync(2);
            Assert.IsFalse(loading.IsCompleted);
            Assert.IsFalse(editing.IsCompleted);
        }
        finally { gate.Release(); }
        await Task.WhenAll(loading, editing).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, rig.Count);
        await rig.LoadAsync();
        Assert.AreEqual(2, rig.Count);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FailedRead_PreservesKnownItemsAndBlocksDestructiveWrites(bool quickLaunch)
    {
        using var rig = new Rig(quickLaunch);
        await rig.AddAsync(1);
        await File.WriteAllTextAsync(rig.Path, "{");
        await File.WriteAllTextAsync(rig.Path + ".bak", "{");
        await Assert.ThrowsAsync<InvalidDataException>(rig.LoadAsync);
        Assert.AreEqual(1, rig.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.AddAsync(2));
        Assert.AreEqual(1, rig.Count);
        Assert.AreEqual("{", await File.ReadAllTextAsync(rig.Path));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RepairedFile_ReloadRestoresDurableEditing(bool quickLaunch)
    {
        using var rig = new Rig(quickLaunch);
        await rig.AddAsync(1);
        var valid = await File.ReadAllTextAsync(rig.Path);
        await File.WriteAllTextAsync(rig.Path, "{");
        await File.WriteAllTextAsync(rig.Path + ".bak", "{");
        try { await rig.LoadAsync(); } catch (InvalidDataException) { }
        await File.WriteAllTextAsync(rig.Path, valid);
        await rig.LoadAsync();
        await rig.AddAsync(2);
        await rig.LoadAsync(); // Read from disk again: an in-memory-only addition cannot pass.
        Assert.AreEqual(2, rig.Count);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LockedTarget_ReportsFailureWithoutChangingMemoryOrPublishingSuccess(bool quickLaunch)
    {
        using var rig = new Rig(quickLaunch);
        await rig.AddAsync(1);
        var original = await File.ReadAllTextAsync(rig.Path);
        rig.Notifications = 0;
        using (var locked = new FileStream(rig.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => rig.AddAsync(2));
            Assert.AreEqual(1, rig.Count);
            Assert.AreEqual(0, rig.Notifications);
        }
        Assert.AreEqual(original, await File.ReadAllTextAsync(rig.Path));
        await rig.AddAsync(3);
        await rig.LoadAsync();
        Assert.AreEqual(2, rig.Count);
    }

    [TestMethod]
    [DataRow(true, "[null]")]
    [DataRow(false, "{\"1\":null}")]
    [DataRow(true, "null")]
    [DataRow(false, "null")]
    public async Task InvalidEntries_DoNotPartiallyReplaceKnownItems(bool quickLaunch, string invalidJson)
    {
        using var rig = new Rig(quickLaunch);
        await rig.AddAsync(1);
        await File.WriteAllTextAsync(rig.Path, invalidJson);
        if (File.Exists(rig.Path + ".bak")) File.Delete(rig.Path + ".bak");
        await Assert.ThrowsAsync<InvalidDataException>(rig.LoadAsync);
        Assert.AreEqual(1, rig.Count);
    }

    private sealed class Rig : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly QuickLaunchService? _quick;
        private readonly GameAccountBinding? _bindings;
        public string Path { get; }
        public int Notifications { get; set; }
        public int Count => _quick?.GetAll().Count ?? _bindings!.GetAllBindings().Count;
        public bool HasOriginalProperties => _quick != null ? !_quick.GetAll().Single().IsPinned : _bindings!.GetBinding(1)!.AutoSwitch;

        public Rig(bool quickLaunch)
        {
            Path = _directory.Combine(quickLaunch ? "quicklaunch.json" : "gamebindings.json");
            if (quickLaunch)
            {
                _quick = new QuickLaunchService(Path);
                _quick.ItemsChanged += (_, _) => Notifications++;
            }
            else
            {
                _bindings = new GameAccountBinding(Path);
                _bindings.BindingsChanged += (_, _) => Notifications++;
            }
        }

        public Task LoadAsync() => _quick?.LoadAsync() ?? _bindings!.LoadAsync();
        public Task EditAsync() => _quick != null ? _quick.TogglePinAsync(_quick.GetAll().Single().Id) : _bindings!.SetAutoSwitchAsync(1, false);
        public async Task AddAsync(int index)
        {
            if (_quick != null) await _quick.AddAsync($"Item {index}", _directory.Combine($"missing-{index}.exe"), iconPath: "");
            else await _bindings!.SetBindingAsync(index, $"Game {index}", $"steam-{index}", $"user-{index}");
        }

        public void Dispose() { _quick?.Dispose(); _directory.Dispose(); }
    }
}
