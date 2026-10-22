using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class ExitCleanupTests
{
    [TestMethod]
    public void FailedResourceRelease_DoesNotSkipRemainingCleanup()
    {
        var order = new List<string>();
        var failures = ExitCleanup.Run(
            ("media", () => { order.Add("media"); throw new InvalidOperationException("late media completion"); }),
            ("tray", () => order.Add("tray")),
            ("view model", () => order.Add("view model")));
        Assert.AreEqual(1, failures);
        CollectionAssert.AreEqual(new[] { "media", "tray", "view model" }, order);
    }

    [TestMethod]
    public void FailedClose_StillInvokesApplicationShutdown()
    {
        var shutdown = false;
        var failures = ExitCleanup.Run(
            ("cleanup", () => throw new IOException("cleanup failed")),
            ("close", () => throw new InvalidOperationException("window closing callback failed")),
            ("shutdown", () => shutdown = true));
        Assert.AreEqual(2, failures);
        Assert.IsTrue(shutdown);
    }
}
