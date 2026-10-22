using SteamSwitcher.Services;
using System.Diagnostics;
using System.IO.Pipes;

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

        // Mutex ownership is thread-affine and recursive on the owner thread;
        // use another thread to model a real secondary process.
        RunWorker(() =>
        {
            using var secondary = new SingleInstanceManager(applicationMutex, userMutex, pipe);
            Assert.IsFalse(secondary.IsPrimaryInstance);
            Assert.AreEqual(primary.NoncePath, secondary.NoncePath);
            Assert.IsTrue(secondary.SignalPrimaryInstance(["--shutdown-for-update"]));
        });

        Assert.IsTrue(received.Task.Wait(TimeSpan.FromSeconds(3)), "Primary instance did not receive the forwarded arguments.");
        var arguments = received.Task.GetAwaiter().GetResult();
        CollectionAssert.AreEqual(arguments, new[] { "--shutdown-for-update" });

        primary.Dispose();
        using var replacement = new SingleInstanceManager(applicationMutex, userMutex, pipe);
        Assert.IsTrue(replacement.IsPrimaryInstance, "Disposed mutex handles must allow a clean restart.");
    }

    [TestMethod]
    public void ReleasedMutexWithSecondaryHandles_CanBeAcquiredByReplacement()
    {
        using var directory = new TemporaryDirectory();
        var suffix = Guid.NewGuid().ToString("N");
        var application = $@"Local\SteamSwitch.Tests.App.{suffix}";
        var legacy = $@"Local\SteamSwitch.Tests.User.{suffix}";
        var pipe = $"SteamSwitch.Tests.{suffix}";
        var nonce = directory.Combine("nonce.txt");
        using var primary = new SingleInstanceManager(application, legacy, pipe, nonce);
        using var applicationObserver = new Mutex(false, application);
        using var legacyObserver = new Mutex(false, legacy);
        primary.Dispose();

        using var replacement = new SingleInstanceManager(application, legacy, pipe, nonce);
        Assert.IsTrue(replacement.IsPrimaryInstance,
            "An existing kernel object is not proof that another process still owns its mutex.");
        Assert.IsTrue(File.Exists(nonce));
    }

    [TestMethod]
    public void AbandonedMutex_IsAcquiredByReplacement()
    {
        using var directory = new TemporaryDirectory();
        var suffix = Guid.NewGuid().ToString("N");
        var application = $@"Local\SteamSwitch.Tests.Abandoned.{suffix}";
        using var abandoned = new Mutex(false, application);
        var owner = new Thread(() => abandoned.WaitOne());
        owner.Start();
        Assert.IsTrue(owner.Join(TimeSpan.FromSeconds(3)));

        using var replacement = new SingleInstanceManager(application,
            $@"Local\SteamSwitch.Tests.User.{suffix}", $"SteamSwitch.Tests.{suffix}", directory.Combine("nonce.txt"));
        Assert.IsTrue(replacement.IsPrimaryInstance);
    }

    [TestMethod]
    public void CustomNames_UseDeterministicIsolatedNoncePath()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceManager($@"Local\SteamSwitch.Tests.App.{suffix}",
            $@"Local\SteamSwitch.Tests.User.{suffix}", $"SteamSwitch.Tests.{suffix}");
        Assert.IsTrue(primary.NoncePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(File.Exists(primary.NoncePath));
        var productionNonce = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamSwitch", "instance-nonce.txt");
        Assert.AreNotEqual(productionNonce, primary.NoncePath, "Unit tests must not overwrite a running app's nonce.");
        primary.Dispose();
        Assert.IsFalse(File.Exists(primary.NoncePath));
    }

    [TestMethod]
    public void Dispose_DoesNotDeleteANonceWrittenByAnotherOwner()
    {
        using var directory = new TemporaryDirectory();
        var suffix = Guid.NewGuid().ToString("N");
        var nonce = directory.Combine("nonce.txt");
        using var primary = new SingleInstanceManager($@"Local\SteamSwitch.Tests.App.{suffix}",
            $@"Local\SteamSwitch.Tests.User.{suffix}", $"SteamSwitch.Tests.{suffix}", nonce);
        File.WriteAllText(nonce, "replacement-token");
        primary.Dispose();
        primary.Dispose();
        Assert.AreEqual("replacement-token", File.ReadAllText(nonce));
    }

    [TestMethod]
    public void ConcurrentHandoff_PreservesReplacementNonce()
    {
        using var directory = new TemporaryDirectory();
        var suffix = Guid.NewGuid().ToString("N");
        var application = $@"Local\SteamSwitch.Tests.App.{suffix}";
        var legacy = $@"Local\SteamSwitch.Tests.User.{suffix}";
        var pipe = $"SteamSwitch.Tests.{suffix}";
        var nonce = directory.Combine("nonce.txt");
        using var primary = new SingleInstanceManager(application, legacy, pipe, nonce);
        using var ready = new ManualResetEventSlim();
        using var replacementReady = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        Exception? failure = null;
        string? replacementNonce = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var observer = new Mutex(false, application);
                ready.Set();
                if (!observer.WaitOne(TimeSpan.FromSeconds(3))) throw new TimeoutException();
                observer.ReleaseMutex();
                using var replacement = new SingleInstanceManager(application, legacy, pipe, nonce);
                Assert.IsTrue(replacement.IsPrimaryInstance);
                replacementNonce = File.ReadAllText(nonce);
                replacementReady.Set();
                if (!finish.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException();
            }
            catch (Exception ex) { failure = ex; replacementReady.Set(); }
        }) { IsBackground = true };
        worker.Start();
        Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(3)));
        primary.Dispose();
        Assert.IsTrue(replacementReady.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            Assert.IsNull(failure);
            Assert.IsNotNull(replacementNonce);
            Assert.AreEqual(replacementNonce, File.ReadAllText(nonce));
        }
        finally { finish.Set(); worker.Join(TimeSpan.FromSeconds(3)); }
        Assert.IsNull(failure);
    }

    [TestMethod]
    public void Listener_RejectsDuplicateStart_AndCancelsSilentClientOnDispose()
    {
        using var directory = new TemporaryDirectory();
        var suffix = Guid.NewGuid().ToString("N");
        var pipe = $"SteamSwitch.Tests.{suffix}";
        using var primary = new SingleInstanceManager($@"Local\SteamSwitch.Tests.App.{suffix}",
            $@"Local\SteamSwitch.Tests.User.{suffix}", pipe, directory.Combine("nonce.txt"));
        primary.StartListening(_ => { });
        Assert.ThrowsExactly<InvalidOperationException>(() => primary.StartListening(_ => { }));
        using var connected = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        Exception? failure = null;
        var client = new Thread(() =>
        {
            try
            {
                using var connection = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
                connection.Connect(3000);
                connected.Set();
                finish.Wait(TimeSpan.FromSeconds(4));
            }
            catch (Exception ex) { failure = ex; connected.Set(); }
        }) { IsBackground = true };
        client.Start();
        Assert.IsTrue(connected.Wait(TimeSpan.FromSeconds(4)));
        var elapsed = Stopwatch.StartNew();
        primary.Dispose();
        elapsed.Stop();
        finish.Set();
        Assert.IsTrue(client.Join(TimeSpan.FromSeconds(3)));
        Assert.IsNull(failure);
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(2), "A client that sends no line must not block application exit.");
        Assert.ThrowsExactly<ObjectDisposedException>(() => primary.StartListening(_ => { }));
    }

    private static void RunWorker(Action action)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        worker.Start();
        Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(12)), "Secondary instance did not finish in time.");
        Assert.IsNull(failure);
    }
}
