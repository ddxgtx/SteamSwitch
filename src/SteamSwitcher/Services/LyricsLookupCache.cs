using System.Collections.Concurrent;

namespace SteamSwitcher.Services;

internal readonly record struct LyricsCacheKey(string TrackKey, int Sources, long DurationSeconds = 0);

/// <summary>Failed or empty lookups expire quickly; source selections never share entries.</summary>
internal sealed class LyricsLookupCache
{
    private readonly ConcurrentDictionary<LyricsCacheKey, Entry> _entries = new();
    private readonly TimeProvider _time;
    internal static readonly TimeSpan EmptyLifetime = TimeSpan.FromSeconds(30);

    /// <summary>歌词行 + 命中该结果时登记到的曲库时长。时长必须随行一起回读，
    /// 否则缓存命中的那一轮会丢掉曲库时长，无 SMTC 时间轴播放器的重播归位随即失效。</summary>
    private sealed record Entry(IReadOnlyList<SyncedLyricLine> Lines, long SongDurationMs, DateTimeOffset Expires);

    public LyricsLookupCache(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public bool TryGetValue(LyricsCacheKey key, out IReadOnlyList<SyncedLyricLine> lines, out long songDurationMs)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.Expires > _time.GetUtcNow())
        {
            lines = entry.Lines;
            songDurationMs = entry.SongDurationMs;
            return true;
        }
        _entries.TryRemove(key, out _);
        lines = Array.Empty<SyncedLyricLine>();
        songDurationMs = 0;
        return false;
    }

    public void Store(LyricsCacheKey key, IReadOnlyList<SyncedLyricLine> lines, long songDurationMs = 0)
    {
        if (_entries.Count >= 128)
        {
            var victim = _entries.OrderBy(pair => pair.Value.Expires).FirstOrDefault().Key;
            _entries.TryRemove(victim, out _);
        }
        _entries[key] = new(lines, songDurationMs, _time.GetUtcNow() + (lines.Count == 0 ? EmptyLifetime : TimeSpan.FromHours(12)));
    }

    public void Clear() => _entries.Clear();
}
