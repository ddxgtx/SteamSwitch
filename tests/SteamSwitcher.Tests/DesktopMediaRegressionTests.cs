using System.Windows;
using SteamSwitcher.Core;
using SteamSwitcher.Services;
using SteamSwitcher.Views;
using Windows.Media.Control;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class DesktopMediaRegressionTests
{
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-20T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public void EmptyLyricsCache_ExpiresAndDoesNotBlockNetworkRecovery()
    {
        var clock = new TestClock();
        var cache = new LyricsLookupCache(clock);
        var key = new LyricsCacheKey("song", 15);
        cache.Store(key, Array.Empty<SyncedLyricLine>());
        Assert.IsTrue(cache.TryGetValue(key, out var empty, out _));
        Assert.HasCount(0, empty);
        clock.Now += LyricsLookupCache.EmptyLifetime;
        Assert.IsFalse(cache.TryGetValue(key, out _, out _));
    }

    [TestMethod]
    public void LyricsCache_DifferentSourceSelectionsNeverReuseOldResults()
    {
        var cache = new LyricsLookupCache();
        cache.Store(new("song", 2), new[] { new SyncedLyricLine(TimeSpan.Zero, "网易云歌词") }, 214_000);
        Assert.IsFalse(cache.TryGetValue(new("song", 1), out _, out _));
        Assert.IsFalse(cache.TryGetValue(new("song", 2, 240), out _, out _), "时长不同的录音版本不得复用匹配结果");
        Assert.IsTrue(cache.TryGetValue(new("song", 2), out var lyrics, out var durationMs));
        Assert.AreEqual(214_000L, durationMs,
            "缓存命中必须回读登记的曲库时长；丢了它，无时间轴播放器的单曲循环归位在第二轮就失效");
        Assert.AreEqual("网易云歌词", lyrics[0].Text);
        cache.Clear();
        Assert.IsFalse(cache.TryGetValue(new("song", 2), out _, out _), "手动重试必须绕过成功与失败缓存");
    }

    [TestMethod]
    public void WindowTitleFallback_ReportsMetadataWithoutPretendingToKnowPlayback()
    {
        var snapshot = TaskbarMediaService.CreateTitleOnlySnapshot("Song", "Artist", "Player", null, "key", 3);
        Assert.IsTrue(snapshot.HasSession);
        Assert.IsFalse(snapshot.IsPlaying);
        Assert.IsFalse(snapshot.CanControlPlayback);
        Assert.IsFalse(snapshot.HasSyncedLyrics);
        Assert.AreEqual(string.Empty, snapshot.CurrentLyric);
        Assert.AreEqual(string.Empty, snapshot.NextText);
        Assert.AreEqual(string.Empty, snapshot.TimeString);
        Assert.AreEqual(0d, snapshot.Progress);
        Assert.AreEqual("Song", snapshot.Title);
    }

    // 对齐 Lyricify：播放视频、图片等其他媒体时不出歌词。同时未上报
    // PlaybackType 的播放器（大量存在）必须继续显示，否则是误伤。
    [TestMethod]
    public void MusicPlaybackTypeFilter_OnlyMusicAndUnknownCountAsMusic()
    {
        Assert.IsTrue(TaskbarMediaService.IsMusicPlaybackType(Windows.Media.MediaPlaybackType.Music),
            "音乐会话必须显示歌词");
        Assert.IsTrue(TaskbarMediaService.IsMusicPlaybackType(Windows.Media.MediaPlaybackType.Unknown),
            "未上报类型的播放器不能误伤");
        Assert.IsTrue(TaskbarMediaService.IsMusicPlaybackType(null),
            "读不到 PlaybackInfo 时按音乐处理");
        Assert.IsFalse(TaskbarMediaService.IsMusicPlaybackType(Windows.Media.MediaPlaybackType.Video),
            "视频会话不得显示歌词");
        Assert.IsFalse(TaskbarMediaService.IsMusicPlaybackType(Windows.Media.MediaPlaybackType.Image),
            "图片会话不得显示歌词");
    }

    [TestMethod]
    public void FloatingPosition_RestoresNegativeMonitorWithItsOwnDpi()
    {
        FloatingMonitor[] monitors =
        {
            new("primary", new Rect(0, 0, 1920, 1040), 1, 1, true),
            new("left", new Rect(-2560, -200, 2560, 1400), 1.5, 1.5)
        };
        var restored = FloatingPlacementGeometry.Restore(new("left", .25, .5), monitors, 400, 100);
        Assert.AreEqual(-2070d, restored.Left);
        Assert.AreEqual(425d, restored.Top);
        Assert.AreEqual(600d, restored.Width);
        var captured = FloatingPlacementGeometry.Capture(restored, monitors[1]);
        Assert.AreEqual(.25, captured.RelativeLeft, 0.00001);
        Assert.AreEqual(.5, captured.RelativeTop, 0.00001);
    }

    [TestMethod]
    public void FloatingPosition_DisconnectedMonitorFallsBackInsidePrimaryWorkArea()
    {
        FloatingMonitor[] monitors = { new("primary", new Rect(0, 40, 1920, 1000), 1.25, 1.25, true) };
        var restored = FloatingPlacementGeometry.Restore(new("removed", 1, 1), monitors, 400, 100);
        Assert.AreEqual(1420d, restored.Left);
        Assert.AreEqual(915d, restored.Top);
        Assert.IsTrue(monitors[0].WorkArea.Contains(restored));
        Assert.AreEqual(monitors[0], FloatingPlacementGeometry.Nearest(monitors, new Rect(-2000, -100, 400, 100)));
    }

    [TestMethod]
    public void FloatingPosition_ClampsLegacyOffscreenCoordinatesToNearestScreen()
    {
        var work = new Rect(-1920, 100, 1920, 980);
        var restored = FloatingPlacementGeometry.Clamp(new Rect(-2500, -100, 400, 100), work);
        Assert.AreEqual(-1920d, restored.Left);
        Assert.AreEqual(100d, restored.Top);
        Assert.IsTrue(work.Contains(restored));
    }

    [TestMethod]
    public async Task CefHandshake_RejectsOldSessionAndCannotReconnectAfterStopping()
    {
        // Constructor and handshake logic do not inspect or operate the real Steam client.
        using var injector = new SteamCEFInjector(null!, null!, 0, 0, "test-token");
        Assert.IsFalse(injector.ConfirmHandshake("previous-host-session"));
        Assert.IsFalse(injector.IsConnected);
        Assert.IsTrue(injector.ConfirmHandshake(injector.SessionId));
        Assert.IsTrue(injector.IsConnected);
        injector.NotifyControlChannelDisconnected();
        Assert.IsFalse(injector.IsConnected);
        Assert.IsTrue(injector.ConfirmHandshake(injector.SessionId));
        await injector.DisconnectAsync();
        Assert.IsFalse(injector.IsConnected);
        Assert.IsFalse(injector.ConfirmHandshake(injector.SessionId));
    }

    // 歌词岛的悬停不能只信 MouseEnter/MouseLeave：tooltip 弹出、可视化柱重排、
    // 宽度微调都会让 WPF 丢掉 IsMouseOver，而鼠标不动就再没有 MouseEnter 纠偏。
    // 判决必须回到光标的物理坐标，这里是它的几何核心。
    [TestMethod]
    public void LyricsHover_PointerGeometryToleratesJitterButDetectsRealExit()
    {
        const double left = 100, top = 500, width = 320, height = 40;

        // 停在岛中央。
        Assert.IsTrue(TaskbarBandWindow.IsPointerInsideBand(260, left, width, 520, top, height));
        // 边缘亚像素抖动不得判为离开——这正是原先控制条自己收起的原因。
        Assert.IsTrue(TaskbarBandWindow.IsPointerInsideBand(left - 1.5, left, width, top - 1.5, top, height));
        Assert.IsTrue(TaskbarBandWindow.IsPointerInsideBand(left + width + 1.5, left, width, top + height + 1.5, top, height));
        // 真的走出去了才判为离开，否则悬停会一直黏住。
        Assert.IsFalse(TaskbarBandWindow.IsPointerInsideBand(left - 8, left, width, 520, top, height));
        Assert.IsFalse(TaskbarBandWindow.IsPointerInsideBand(260, left, width, top + height + 8, top, height));
        Assert.IsFalse(TaskbarBandWindow.IsPointerInsideBand(left + width + 8, left, width, 520, top, height));
    }

    [TestMethod]
    public async Task MediaStart_LateManagerAfterStopDoesNotRestartPolling()
    {
        var ready = new TaskCompletionSource<GlobalSystemMediaTransportControlsSessionManager?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializationToken = CancellationToken.None;
        using var service = new TaskbarMediaService(token =>
        {
            initializationToken = token;
            return ready.Task; // Deliberately ignore cancellation to exercise the generation guard.
        });
        var start = service.StartAsync();
        service.Stop();
        Assert.IsTrue(initializationToken.IsCancellationRequested);
        ready.SetResult(null);
        await start;
        Assert.IsFalse(service.IsPolling);
        Assert.IsFalse(service.HasManagerSubscriptions);
    }

    [TestMethod]
    public async Task MediaStart_LateManagerAfterDisposeCannotResurrectService()
    {
        var ready = new TaskCompletionSource<GlobalSystemMediaTransportControlsSessionManager?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var service = new TaskbarMediaService(_ => { calls++; return ready.Task; });
        var start = service.StartAsync();
        service.Dispose();
        ready.SetResult(null);
        await start;
        await service.StartAsync();
        Assert.AreEqual(1, calls, "Dispose 后不得再次请求系统媒体管理器");
        Assert.IsFalse(service.IsPolling);
        Assert.IsFalse(service.HasManagerSubscriptions);
        service.Dispose();
    }

    [TestMethod]
    public async Task MediaStart_ConcurrentCallersShareInitializationAndNullManagerCanBeRetried()
    {
        var first = new TaskCompletionSource<GlobalSystemMediaTransportControlsSessionManager?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<GlobalSystemMediaTransportControlsSessionManager?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var service = new TaskbarMediaService(_ => ++calls == 1 ? first.Task : second.Task);
        var oldStart = service.StartAsync();
        Assert.AreSame(oldStart, service.StartAsync());
        Assert.AreEqual(1, calls);
        service.Stop();
        var newStart = service.StartAsync();
        Assert.AreEqual(2, calls);
        first.SetResult(null);
        await oldStart;
        Assert.IsFalse(service.IsPolling);
        second.SetResult(null);
        await newStart;
        Assert.IsFalse(service.IsPolling, "未获得媒体管理器时不得启动无效的后台轮询");
        await service.StartAsync();
        Assert.AreEqual(3, calls, "null 结果不能阻止下一次显式重试");
        Assert.IsFalse(service.IsPolling);
        service.Stop();
        Assert.IsFalse(service.IsPolling);
    }

    [TestMethod]
    public void MediaLifetime_OldGenerationCannotActivateAndDisposePreventsRestart()
    {
        var lifetime = new AsyncRunLifetime();
        var oldRun = lifetime.Begin()!;
        lifetime.Stop(() => { });
        var newRun = lifetime.Begin()!;
        var activations = 0;
        Assert.IsFalse(lifetime.TryActivate(oldRun, () => activations++));
        Assert.IsTrue(lifetime.TryActivate(newRun, () => activations++));
        Assert.AreEqual(1, activations);
        Assert.IsTrue(lifetime.IsCurrent(newRun));
        lifetime.Stop(() => { }, dispose: true);
        Assert.IsFalse(lifetime.TryActivate(newRun, () => activations++));
        Assert.IsNull(lifetime.Begin());
    }

    [TestMethod]
    public async Task Artwork_StopCancelsWaitAndLateResultCannotRefillCache()
    {
        var lifetime = new AsyncRunLifetime();
        var run = lifetime.Begin()!;
        var late = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new Dictionary<string, byte[]>();
        CancellationToken providerToken = default;
        async Task LookupAndCommitAsync()
        {
            var bytes = await ArtworkLookupRace.FirstMatchAsync(new Func<CancellationToken, Task<byte[]?>>[]
            {
                token => { providerToken = token; return late.Task; }
            }, run.Token);
            if (bytes != null) lifetime.TryActivate(run, () => cache["song"] = bytes);
        }
        var pending = LookupAndCommitAsync();
        lifetime.Stop(() => cache.Clear());
        Assert.IsTrue(providerToken.IsCancellationRequested);
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        late.SetResult(new byte[128]); // Simulate a transport that ignored cancellation.
        Assert.IsFalse(lifetime.TryActivate(run, () => cache["song"] = new byte[128]));
        Assert.HasCount(0, cache);
    }

    [TestMethod]
    public async Task Artwork_FirstSuccessfulProviderCancelsOtherRequests()
    {
        using var lifetime = new CancellationTokenSource();
        var winner = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loser = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken loserToken = default;
        var lookup = ArtworkLookupRace.FirstMatchAsync(new Func<CancellationToken, Task<byte[]?>>[]
        {
            _ => winner.Task,
            token => { loserToken = token; return loser.Task; }
        }, lifetime.Token);
        var expected = new byte[128];
        winner.SetResult(expected);
        Assert.AreSame(expected, await lookup.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(loserToken.IsCancellationRequested, "得到封面后必须取消尚未完成的其他源");
        loser.SetResult(null);
    }

    [TestMethod]
    public async Task Artwork_CancellationPreventsStartingAnotherProvider()
    {
        using var cancellation = new CancellationTokenSource();
        var secondStarted = false;
        var lookup = ArtworkLookupRace.FirstMatchAsync(new Func<CancellationToken, Task<byte[]?>>[]
        {
            _ => { cancellation.Cancel(); return Task.FromResult<byte[]?>(null); },
            _ => { secondStarted = true; return Task.FromResult<byte[]?>(new byte[128]); }
        }, cancellation.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => lookup);
        Assert.IsFalse(secondStarted);
    }
}
