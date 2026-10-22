using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class LyricsStabilityTests
{
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public void MissingMedia_InitiallyShowsIdle_ThenHoldsOnlyForBoundedGracePeriod()
    {
        var clock = new Clock();
        var continuity = new MediaDisplayContinuity(clock);
        Assert.IsFalse(continuity.HoldLastMedia(false));
        Assert.IsTrue(continuity.HoldLastMedia(true));
        clock.Now += TimeSpan.FromMilliseconds(1100);
        Assert.IsTrue(continuity.HoldLastMedia(true));
        clock.Now += TimeSpan.FromMilliseconds(100);
        Assert.IsFalse(continuity.HoldLastMedia(true));
        clock.Now += TimeSpan.FromSeconds(10);
        Assert.IsFalse(continuity.HoldLastMedia(true), "Repeated failures must not keep stale media forever.");
    }

    [TestMethod]
    public void HealthyRead_ResetsTheMissingMediaDeadline()
    {
        var clock = new Clock();
        var continuity = new MediaDisplayContinuity(clock);
        Assert.IsTrue(continuity.HoldLastMedia(true));
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.IsFalse(continuity.HoldLastMedia(true));
        continuity.MarkHealthy();
        Assert.IsTrue(continuity.HoldLastMedia(true));
    }

    [TestMethod]
    public void OptionalArtistUpdates_DoNotStartANewTrack()
    {
        var key = MediaTrackIdentity.Resolve("", "player", "Song", "");
        Assert.AreEqual(key, MediaTrackIdentity.Resolve(key, "player", "Song", "Artist"));
        Assert.AreEqual(key, MediaTrackIdentity.Resolve(key, "player", "Song", "", "Artist"));
        Assert.AreNotEqual(key, MediaTrackIdentity.Resolve(key, "player", "Song", "Other artist", "Artist"));
    }

    [TestMethod]
    [DataRow("other-player", "Song", "Artist")]
    [DataRow("player", "Other song", "Artist")]
    [DataRow("player", "Song", "Other artist")]
    public void ActualTrackChanges_AreNotMasked(string source, string title, string artist)
    {
        var key = MediaTrackIdentity.Resolve("", "player", "Song", "Artist");
        Assert.AreNotEqual(key, MediaTrackIdentity.Resolve(key, source, title, artist));
    }

    [TestMethod]
    public void TimelineJitter_DoesNotAlternateLyricsAroundTheBoundary()
    {
        var selector = new StableLyricSelector();
        SyncedLyricLine[] lines = [new(TimeSpan.FromSeconds(1), "First"), new(TimeSpan.FromSeconds(2), "Second")];
        Assert.AreEqual("First", selector.Select(lines, TimeSpan.FromSeconds(1.8))?.Text);
        foreach (var position in new[] { 2.01, 1.94, 2.03, 1.93, 2.1 })
            Assert.AreEqual("Second", selector.Select(lines, TimeSpan.FromSeconds(position))?.Text);
    }

    [TestMethod]
    public void TimelineJitter_AtFirstLineDoesNotFlashBackToTitle()
    {
        var selector = new StableLyricSelector();
        SyncedLyricLine[] lines = [new(TimeSpan.FromSeconds(5), "First")];
        Assert.IsNull(selector.Select(lines, TimeSpan.FromSeconds(4.8)));
        Assert.AreEqual("First", selector.Select(lines, TimeSpan.FromSeconds(5.01))?.Text);
        Assert.AreEqual("First", selector.Select(lines, TimeSpan.FromSeconds(4.94))?.Text);
    }

    [TestMethod]
    public void IntentionalBackwardSeek_ImmediatelySelectsEarlierLyrics()
    {
        var selector = new StableLyricSelector();
        SyncedLyricLine[] lines = [new(TimeSpan.FromSeconds(1), "First"), new(TimeSpan.FromSeconds(2), "Second")];
        selector.Select(lines, TimeSpan.FromSeconds(2.1));
        Assert.AreEqual("First", selector.Select(lines, TimeSpan.FromSeconds(1.2))?.Text);
        Assert.IsNull(selector.Select(lines, TimeSpan.Zero));
    }

    [TestMethod]
    public void ReloadedLyricsAndDuplicateText_ResetUsingTimestampAndSource()
    {
        var selector = new StableLyricSelector();
        SyncedLyricLine[] first = [new(TimeSpan.FromSeconds(1), "Same"), new(TimeSpan.FromSeconds(2), "Same")];
        Assert.AreEqual(TimeSpan.FromSeconds(2), selector.Select(first, TimeSpan.FromSeconds(2.1))?.Time);
        SyncedLyricLine[] replacement = [new(TimeSpan.FromSeconds(1), "New source")];
        Assert.AreEqual("New source", selector.Select(replacement, TimeSpan.FromSeconds(1.94))?.Text);
        Assert.IsNull(selector.Select(Array.Empty<SyncedLyricLine>(), TimeSpan.FromSeconds(2)));
    }
}
