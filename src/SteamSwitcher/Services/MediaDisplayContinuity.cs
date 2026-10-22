namespace SteamSwitcher.Services;

/// <summary>Keep a valid media display through brief SMTC handoff/read gaps.</summary>
internal sealed class MediaDisplayContinuity
{
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromMilliseconds(1200);
    private readonly TimeProvider _clock;
    private DateTimeOffset? _missingSince;

    internal MediaDisplayContinuity(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;
    internal void MarkHealthy() => _missingSince = null;
    internal bool HoldLastMedia(bool hasLastMedia)
    {
        if (!hasLastMedia) return false;
        var now = _clock.GetUtcNow();
        _missingSince ??= now;
        return now - _missingSince.Value < GracePeriod;
    }
}

/// <summary>Small timeline corrections must not repeatedly select the preceding lyric.</summary>
internal sealed class StableLyricSelector
{
    private IReadOnlyList<SyncedLyricLine>? _source;
    private SyncedLyricLine? _last;
    private TimeSpan _lastPosition;

    internal SyncedLyricLine? Select(IReadOnlyList<SyncedLyricLine> lines, TimeSpan position)
    {
        if (!ReferenceEquals(_source, lines)) { _source = lines; _last = null; }
        var candidate = SyncedLyricsParser.FindLyricLine(lines, position);
        var smallCorrection = (_lastPosition - position).Duration() <= TimeSpan.FromMilliseconds(300);
        if (_last != null && (candidate == null || candidate.Time < _last.Time) && smallCorrection &&
            position >= _last.Time - TimeSpan.FromMilliseconds(150))
            candidate = _last;
        _lastPosition = position;
        return _last = candidate;
    }
}

internal static class MediaTrackIdentity
{
    // Album/artist fields often arrive in separate SMTC notifications. Filling an
    // optional field is a metadata refresh, not a song change that clears lyrics.
    internal static string Resolve(string previousKey, string source, string title, string artist, string knownArtist = "")
    {
        var previous = previousKey.Split('\u001f');
        var previousArtist = previous.Length == 3 && !string.IsNullOrWhiteSpace(previous[2]) ? previous[2] : knownArtist;
        if (previous.Length == 3 && previous[0] == source && previous[1] == title &&
            (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(previousArtist) || previousArtist == artist))
            return previousKey;
        return $"{source}\u001f{title}\u001f{artist}";
    }
}
