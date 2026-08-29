using System.Text.Json;
using SteamSwitcher.Core;
using SteamSwitcher.Models;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class PersistenceConcurrencyTests
{
    [TestMethod]
    public async Task GameBindings_ConcurrentMutations_DoNotLoseNewerEntries()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.Combine("gamebindings.json");
        var service = new GameAccountBinding(path);

        await Task.WhenAll(Enumerable.Range(1, 40).Select(index =>
            service.SetBindingAsync(index, $"Game {index}", $"steam-{index}", $"user-{index}")));

        var persisted = JsonSerializer.Deserialize<Dictionary<int, GameBinding>>(
            await File.ReadAllTextAsync(path));
        Assert.IsNotNull(persisted);
        Assert.AreEqual(40, persisted.Count);
        Assert.IsTrue(Enumerable.Range(1, 40).All(persisted.ContainsKey));
    }

    [TestMethod]
    public async Task QuickLaunch_ConcurrentAdds_PersistEveryItemWithUniqueOrder()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.Combine("quicklaunch.json");
        using var service = new QuickLaunchService(path);

        await Task.WhenAll(Enumerable.Range(1, 40).Select(index =>
            service.AddAsync($"Item {index}", directory.Combine($"missing-{index}.exe"))));

        var persisted = JsonSerializer.Deserialize<List<QuickLaunchItem>>(
            await File.ReadAllTextAsync(path));
        Assert.IsNotNull(persisted);
        Assert.AreEqual(40, persisted.Count);
        Assert.AreEqual(40, persisted.Select(item => item.SortOrder).Distinct().Count());
    }
}
