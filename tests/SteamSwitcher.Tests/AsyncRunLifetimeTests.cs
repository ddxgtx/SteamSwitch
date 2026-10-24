using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class AsyncRunLifetimeTests
{
    [TestMethod]
    public void Begin_WhenPreviousCancellationStopsTheService_ReturnsAnInvalidLeaseWithoutThrowing()
    {
        var lifetime = new AsyncRunLifetime();
        var previous = lifetime.Begin()!;
        using var registration = previous.Token.Register(() => lifetime.Stop(() => { }));
        var interrupted = lifetime.Begin()!;
        Assert.IsFalse(lifetime.IsCurrent(interrupted));
        Assert.IsTrue(interrupted.Token.IsCancellationRequested);
        var restarted = lifetime.Begin()!;
        Assert.IsTrue(lifetime.IsCurrent(restarted));
        lifetime.Stop(() => { }, dispose: true);
    }

    [TestMethod]
    public void Begin_WhenCancellationStartsANewerRun_KeepsEachGenerationsOwnToken()
    {
        var lifetime = new AsyncRunLifetime();
        var previous = lifetime.Begin()!;
        AsyncRunLifetime.Lease? newest = null;
        using var registration = previous.Token.Register(() => newest = lifetime.Begin());
        var superseded = lifetime.Begin()!;
        Assert.IsNotNull(newest);
        Assert.AreNotEqual(superseded.Token, newest.Token, "A superseded run must not borrow the new run's cancellation token.");
        Assert.IsFalse(lifetime.IsCurrent(superseded));
        Assert.IsTrue(superseded.Token.IsCancellationRequested);
        Assert.IsTrue(lifetime.IsCurrent(newest));
        lifetime.Stop(() => { }, dispose: true);
    }
}
