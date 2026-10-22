using System.Reflection;
using SteamSwitcher.Core;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class DesktopExitRecoveryTests
{
    [TestMethod]
    public async Task ApplicationExit_DoesNotWaitForBusyDesktopLock_AndRequestsRecoveryOnce()
    {
        var recoveryRequests = 0;
        var service = new DesktopIconService(() => Interlocked.Increment(ref recoveryRequests));
        var sync = typeof(DesktopIconService).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var blockedCallback = Task.Run(() =>
        {
            lock (sync)
            {
                entered.SetResult();
                release.Wait();
            }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Run(service.DisposeForApplicationExit).WaitAsync(TimeSpan.FromSeconds(2));
            service.DisposeForApplicationExit();
            Assert.AreEqual(1, recoveryRequests);
            var disposed = (bool)typeof(DesktopIconService).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            Assert.IsTrue(disposed, "Already-queued callbacks must see disposal before waiting for Explorer.");
        }
        finally
        {
            release.Set();
            await blockedCallback;
            service.DisposeForApplicationExit();
        }
    }

    [TestMethod]
    public void ApplicationExit_AtomicallyReleasesAndDisposesMonitoringTimer()
    {
        var service = new DesktopIconService(() => { });
        var timer = new Timer(_ => Assert.Fail("Exit test must never start a desktop callback."), null, Timeout.Infinite, Timeout.Infinite);
        var timerField = typeof(DesktopIconService).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        timerField.SetValue(service, timer);
        service.DisposeForApplicationExit();
        Assert.IsNull(timerField.GetValue(service));
        Assert.IsFalse(timer.Change(Timeout.Infinite, Timeout.Infinite), "A disposed timer cannot be restarted.");
    }
}
