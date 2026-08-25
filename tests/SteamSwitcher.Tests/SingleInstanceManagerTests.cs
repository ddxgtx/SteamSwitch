using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class SingleInstanceManagerTests
{
    [TestMethod]
    public void SecondInstance_ForwardsArgumentsToPrimary()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var applicationMutex = $@"Local\SteamSwitch.Tests.App.{suffix}";
        var userMutex = $@"Local\SteamSwitch.Tests.User.{suffix}";
        var pipe = $"SteamSwitch.Tests.{suffix}";
        using var primary = new SingleInstanceManager(applicationMutex, userMutex, pipe);
        Assert.IsTrue(primary.IsPrimaryInstance);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(arguments => received.TrySetResult(arguments));

        using var secondary = new SingleInstanceManager(applicationMutex, userMutex, pipe);
        Assert.IsFalse(secondary.IsPrimaryInstance);
        Assert.IsTrue(secondary.SignalPrimaryInstance(["--shutdown-for-update"]));

        Assert.IsTrue(received.Task.Wait(TimeSpan.FromSeconds(3)), "Primary instance did not receive the forwarded arguments.");
        var arguments = received.Task.GetAwaiter().GetResult();
        CollectionAssert.AreEqual(arguments, new[] { "--shutdown-for-update" });

        primary.Dispose();
        secondary.Dispose();
        using var replacement = new SingleInstanceManager(applicationMutex, userMutex, pipe);
        Assert.IsTrue(replacement.IsPrimaryInstance, "Disposed mutex handles must allow a clean restart.");
    }
}
