using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SteamSwitcher.Services
{
    public sealed record SyncedLyricLine(TimeSpan Time, string Text, string? Translation = null);

    public static class SyncedLyricsParser
    {
        private static readonly Regex TimestampRegex = new(
            @"\[(?<minute>\d{1,3}):(?<second>\d{2})(?:[\.:](?<fraction>\d{1,3}))?\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex OffsetRegex = new(
            @"\[offset:\s*(?<offset>[+-]?\d+)\s*\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex MetaTagRegex = new(
            @"^\[(ti|ar|al|by|re|ve|length):.*?\]$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static IReadOnlyList<SyncedLyricLine> Parse(string? lrc)
        {
            if (string.IsNullOrWhiteSpace(lrc))
                return Array.Empty<SyncedLyricLine>();

            var offsetMs = 0;
            var offsetMatch = OffsetRegex.Match(lrc);
            if (offsetMatch.Success && int.TryParse(offsetMatch.Groups["offset"].Value, out var parsedOffset))
            {
                offsetMs = parsedOffset;
            }

            var lines = new List<SyncedLyricLine>();
            foreach (var rawLine in lrc.Replace("\r", string.Empty).Split('\n'))
            {
                var trimmed = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || MetaTagRegex.IsMatch(trimmed) || OffsetRegex.IsMatch(trimmed))
                    continue;

                var matches = TimestampRegex.Matches(rawLine);
                if (matches.Count == 0)
                    continue;

                var text = WebUtility.HtmlDecode(TimestampRegex.Replace(rawLine, string.Empty)).Trim();
                foreach (Match match in matches)
                {
                    if (!int.TryParse(match.Groups["minute"].Value, out var minutes) ||
                        !int.TryParse(match.Groups["second"].Value, out var seconds))
                        continue;

                    var fractionText = match.Groups["fraction"].Value;
                    var milliseconds = fractionText.Length switch
                    {
                        1 => int.Parse(fractionText, CultureInfo.InvariantCulture) * 100,
                        2 => int.Parse(fractionText, CultureInfo.InvariantCulture) * 10,
                        3 => int.Parse(fractionText, CultureInfo.InvariantCulture),
                        _ => 0
                    };

                    var totalTime = TimeSpan.FromMinutes(minutes) +
                                    TimeSpan.FromSeconds(seconds) +
                                    TimeSpan.FromMilliseconds(milliseconds) +
                                    TimeSpan.FromMilliseconds(offsetMs);

                    if (totalTime < TimeSpan.Zero)
                        totalTime = TimeSpan.Zero;

                    lines.Add(new SyncedLyricLine(totalTime, text));
                }
            }

            return lines.OrderBy(line => line.Time).ToArray();
        }

        public static IReadOnlyList<SyncedLyricLine> Merge(IReadOnlyList<SyncedLyricLine> original, IReadOnlyList<SyncedLyricLine> translations)
        {
            if (translations == null || translations.Count == 0)
                return original;

            var result = new List<SyncedLyricLine>();
            foreach (var orig in original)
            {
                var matchedTrans = translations
                    .Where(t => Math.Abs((t.Time - orig.Time).TotalMilliseconds) < 1800)
                    .OrderBy(t => Math.Abs((t.Time - orig.Time).TotalMilliseconds))
                    .FirstOrDefault();

                result.Add(new SyncedLyricLine(orig.Time, orig.Text, matchedTrans?.Text));
            }
            return result;
        }

        public static SyncedLyricLine? FindLyricLine(IReadOnlyList<SyncedLyricLine> lines, TimeSpan position)
        {
            if (lines.Count == 0)
                return null;

            // During prelude (before first lyric line), return null to signal "has lyrics but prelude"
            if (position < lines[0].Time)
                return null;

            var low = 0;
            var high = lines.Count - 1;
            var result = -1;
            while (low <= high)
            {
                var mid = low + (high - low) / 2;
                if (lines[mid].Time <= position + TimeSpan.FromMilliseconds(50))
                {
                    result = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return result >= 0 ? lines[result] : lines[0];
        }

        public static string? FindLine(IReadOnlyList<SyncedLyricLine> lines, TimeSpan position)
        {
            return FindLyricLine(lines, position)?.Text;
        }

        public static string? FindNextLine(IReadOnlyList<SyncedLyricLine> lines, TimeSpan position)
        {
            if (lines.Count == 0) return null;
            if (position < lines[0].Time)
                return lines[0].Text;

            for (var index = 0; index < lines.Count; index++)
            {
                if (lines[index].Time > position + TimeSpan.FromMilliseconds(120))
                    return lines[index].Text;
            }
            return null;
        }
    }

    public static class TrackMetadataSanitizer
    {
        private static readonly Regex NoisePattern = new(
            @"\s*[\(\[\{（【](?:Live|feat\..*?|ft\..*?|Explicit|Official.*?|Remaster.*?|HD|MV|FLAC|320k|伴奏|高音质版|纯音乐|原唱.*?|Cover.*?)[\)\]\}）】]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex FileExtPattern = new(
            @"\.(mp3|flac|wav|m4a|aac|ogg|wma)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static (string CleanTitle, string CleanArtist) Sanitize(string title, string artist)
        {
            var rawTitle = (title ?? string.Empty).Trim();
            var rawArtist = (artist ?? string.Empty).Trim();

            rawTitle = FileExtPattern.Replace(rawTitle, string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(rawArtist) ||
                rawArtist.Equals("未知艺术家", StringComparison.OrdinalIgnoreCase) ||
                rawArtist.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase))
            {
                var dashIndex = rawTitle.IndexOf(" - ", StringComparison.Ordinal);
                if (dashIndex > 0)
                {
                    rawArtist = rawTitle.Substring(0, dashIndex).Trim();
                    rawTitle = rawTitle.Substring(dashIndex + 3).Trim();
                }
                else
                {
                    dashIndex = rawTitle.IndexOf(" — ", StringComparison.Ordinal);
                    if (dashIndex > 0)
                    {
                        rawArtist = rawTitle.Substring(0, dashIndex).Trim();
                        rawTitle = rawTitle.Substring(dashIndex + 3).Trim();
                    }
                }
            }
            else
            {
                if (rawTitle.StartsWith(rawArtist + " - ", StringComparison.OrdinalIgnoreCase))
                {
                    rawTitle = rawTitle.Substring(rawArtist.Length + 3).Trim();
                }
                else if (rawTitle.StartsWith(rawArtist + " — ", StringComparison.OrdinalIgnoreCase))
                {
                    rawTitle = rawTitle.Substring(rawArtist.Length + 3).Trim();
                }
                else if (rawTitle.EndsWith(" - " + rawArtist, StringComparison.OrdinalIgnoreCase))
                {
                    rawTitle = rawTitle.Substring(0, rawTitle.Length - (rawArtist.Length + 3)).Trim();
                }
                else if (rawTitle.EndsWith(" — " + rawArtist, StringComparison.OrdinalIgnoreCase))
                {
                    rawTitle = rawTitle.Substring(0, rawTitle.Length - (rawArtist.Length + 3)).Trim();
                }
            }

            var cleanTitle = NoisePattern.Replace(rawTitle, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleanTitle)) cleanTitle = rawTitle;

            var cleanArtist = NoisePattern.Replace(rawArtist, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleanArtist)) cleanArtist = rawArtist;

            return (cleanTitle, cleanArtist);
        }
    }

    public sealed record TaskbarMediaSnapshot(
        bool HasSession,
        bool IsPlaying,
        string PrimaryText,
        string SecondaryText,
        string SourceText,
        byte[]? Artwork = null,
        string NextText = "",
        double Progress = 0,
        string Title = "",
        string Artist = "",
        string Album = "",
        string TimeString = "",
        bool HasSyncedLyrics = false,
        string CurrentLyric = "",
        string CurrentTranslation = "",
        string TrackKey = "",
        long TrackVersion = 0);

    public sealed class TaskbarMediaService : IDisposable
    {
        private static readonly HttpClient LyricsClient = CreateLyricsClient();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
        private readonly ConcurrentDictionary<string, IReadOnlyList<SyncedLyricLine>> _lyricsCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte[]> _artworkCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _pendingOnlineArtwork = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _pendingSmtcArtwork = new(StringComparer.Ordinal);
        private readonly System.Diagnostics.Stopwatch _playbackStopwatch = new();
        private TimeSpan _playbackBasePosition = TimeSpan.Zero;
        private string _lastActiveTrackKey = string.Empty;
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private IReadOnlyList<SyncedLyricLine> _lyrics = Array.Empty<SyncedLyricLine>();
        private string _trackKey = string.Empty;
        private string _title = string.Empty;
        private string _artist = string.Empty;
        private string _album = string.Empty;
        private long _trackVersion;
        private byte[]? _artwork;
        private byte[]? _onlineArtwork;
        private byte[]? _previousTrackArtwork;
        private string _previousTrackAlbum = string.Empty;
        private DateTimeOffset _lastTrackChangeAt = DateTimeOffset.MinValue;
        private bool _busy;
        private bool _disposed;
        private bool _managerEventsAttached;
        private bool _onlineLyricsEnabled = true;

        public bool OnlineLyricsEnabled
        {
            get => _onlineLyricsEnabled;
            set
            {
                if (_onlineLyricsEnabled == value) return;
                _onlineLyricsEnabled = value;
                _trackKey = string.Empty;
                Interlocked.Increment(ref _trackVersion);
                _onlineArtwork = null;
                if (!value) _lyrics = Array.Empty<SyncedLyricLine>();
            }
        }
        public bool SourceQQEnabled { get; set; } = true;
        public bool SourceNeteaseEnabled { get; set; } = true;
        public bool SourceKugouEnabled { get; set; } = true;
        public bool SourceLrclibEnabled { get; set; } = true;
        public int OffsetMs { get; set; } = 0;
        public bool SplitLongLines { get; set; } = false;
        public int PollingIntervalMs
        {
            get => (int)_timer.Interval.TotalMilliseconds;
            set => _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(value, 60, 1000));
        }

        public TaskbarMediaSnapshot Current { get; private set; } = new(false, false, "等待媒体播放", "支持 网易云、QQ音乐、酷狗、Spotify、Apple Music 等系统媒体", "系统媒体");
        public event EventHandler<TaskbarMediaSnapshot>? StateChanged;

        public TaskbarMediaService()
        {
            _timer.Tick += async (_, _) => await RefreshAsync();
        }

        public async Task StartAsync()
        {
            if (_disposed || _timer.IsEnabled)
                return;

            try
            {
                _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                AttachManagerEvents();
                _timer.Start();
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to initialize system media controls: {ex.Message}");
                Publish(new(false, false, "媒体控制不可用", "请确认 Windows 媒体会话权限", "系统媒体"));
            }
        }

        public void Stop()
        {
            _timer.Stop();
            DetachManagerEvents();
            _session = null;
            _trackKey = string.Empty;
            Interlocked.Increment(ref _trackVersion);
            _lyrics = Array.Empty<SyncedLyricLine>();
            _artwork = null;
            _onlineArtwork = null;
        }

        private void AttachManagerEvents()
        {
            if (_manager == null || _managerEventsAttached)
                return;

            _manager.SessionsChanged += Manager_SessionsChanged;
            _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
            _managerEventsAttached = true;
        }

        private void DetachManagerEvents()
        {
            if (_manager == null || !_managerEventsAttached)
                return;

            _manager.SessionsChanged -= Manager_SessionsChanged;
            _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
            _managerEventsAttached = false;
        }

        private void Manager_SessionsChanged(
            GlobalSystemMediaTransportControlsSessionManager sender,
            SessionsChangedEventArgs args) => QueueRefresh();

        private void Manager_CurrentSessionChanged(
            GlobalSystemMediaTransportControlsSessionManager sender,
            CurrentSessionChangedEventArgs args) => QueueRefresh();

        private void QueueRefresh()
        {
            if (_disposed)
                return;

            if (_timer.Dispatcher.CheckAccess())
            {
                _ = RefreshAsync();
                return;
            }

            _timer.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed)
                    _ = RefreshAsync();
            }));
        }

        public async Task TogglePlayPauseAsync() => await InvokeSessionAsync(session => session.TryTogglePlayPauseAsync().AsTask());
        public async Task StopPlaybackAsync() => await InvokeSessionAsync(session => session.TryStopAsync().AsTask());
        public async Task SkipNextAsync() => await InvokeSessionAsync(session => session.TrySkipNextAsync().AsTask());
        public async Task SkipPreviousAsync() => await InvokeSessionAsync(session => session.TrySkipPreviousAsync().AsTask());

        private async Task InvokeSessionAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
        {
            try
            {
                var session = GetActiveSession();
                if (session != null)
                    await action(session);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"System media command failed: {ex.Message}");
            }
        }

        private GlobalSystemMediaTransportControlsSession? GetActiveSession()
        {
            if (_manager == null) return null;
            try
            {
                var sessions = _manager.GetSessions();
                if (sessions != null && sessions.Count > 0)
                {
                    // 1. 优先获取处于 Playing 状态的会话
                    foreach (var s in sessions)
                    {
                        try
                        {
                            var pb = s.GetPlaybackInfo();
                            if (pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                                return s;
                        }
                        catch { }
                    }

                    // 2. 获取具有活动媒体属性的会话
                    var current = _manager.GetCurrentSession();
                    if (current != null) return current;

                    // 3. 回退为最后一个可用会话
                    return sessions.LastOrDefault();
                }
                return _manager.GetCurrentSession();
            }
            catch
            {
                return _manager?.GetCurrentSession();
            }
        }

        private static readonly (string ProcessName, string AppName)[] KnownMusicPlayers = new[]
        {
            ("cloudmusic", "网易云音乐"),
            ("qqmusic", "QQ音乐"),
            ("kugou", "酷狗音乐"),
            ("kwmusic", "酷我音乐"),
            ("spotify", "Spotify"),
            ("yesplaymusic", "YesPlayMusic"),
            ("applemusic", "Apple Music"),
            ("foobar2000", "foobar2000"),
            ("aimp", "AIMP"),
            ("potplayer64", "PotPlayer"),
            ("potplayermini64", "PotPlayer")
        };

        private (string Title, string Artist, string AppName)? TryGetWindowMediaInfo()
        {
            try
            {
                foreach (var (procName, appName) in KnownMusicPlayers)
                {
                    var procs = System.Diagnostics.Process.GetProcessesByName(procName);
                    foreach (var p in procs)
                    {
                        try
                        {
                            var wTitle = p.MainWindowTitle?.Trim();
                            if (!string.IsNullOrWhiteSpace(wTitle) &&
                                !wTitle.Equals(appName, StringComparison.OrdinalIgnoreCase) &&
                                !wTitle.Equals("网易云音乐", StringComparison.OrdinalIgnoreCase) &&
                                !wTitle.Equals("QQ音乐", StringComparison.OrdinalIgnoreCase) &&
                                !wTitle.Equals("酷狗音乐", StringComparison.OrdinalIgnoreCase) &&
                                !wTitle.Equals("Spotify Free", StringComparison.OrdinalIgnoreCase) &&
                                !wTitle.Equals("Spotify Premium", StringComparison.OrdinalIgnoreCase))
                            {
                                var (cleanT, cleanA) = TrackMetadataSanitizer.Sanitize(wTitle, string.Empty);
                                if (!string.IsNullOrWhiteSpace(cleanT))
                                    return (cleanT, cleanA, appName);
                            }
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
            }
            catch { }
            return null;
        }

        private async Task RefreshAsync()
        {
            if (_busy || _disposed)
                return;

            _busy = true;
            try
            {
                _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var session = GetActiveSession();
                if (session == null)
                {
                    // 若无 WinRT SMTC 会话，尝试通过窗口标题嗅探器（Lyricify 兼容模式）
                    var windowFallback = TryGetWindowMediaInfo();
                    if (windowFallback.HasValue)
                    {
                        var (wTitle, wArtist, wApp) = windowFallback.Value;
                        var fallbackKey = $"{wTitle}\u001f{wArtist}\u001fwin_fallback";
                        if (!string.Equals(fallbackKey, _trackKey, StringComparison.Ordinal))
                        {
                            _trackKey = fallbackKey;
                            var fallbackTrackVersion = Interlocked.Increment(ref _trackVersion);
                            _title = wTitle;
                            _artist = wArtist;
                            _lyrics = Array.Empty<SyncedLyricLine>();
                            _artwork = null;
                            _onlineArtwork = null;
                            _lastTrackChangeAt = DateTimeOffset.Now;

                            // Players without a usable SMTC session cannot expose a thumbnail.
                            // Resolve a metadata-matched online cover instead of permanently
                            // publishing a null artwork value on the compatibility path.
                            if (!string.IsNullOrWhiteSpace(wTitle))
                            {
                                var artworkKey = fallbackKey;
                                var artworkTitle = wTitle;
                                var artworkArtist = wArtist;
                                _ = Task.Run(() => EnsureArtworkAsync(
                                    artworkKey,
                                    artworkTitle,
                                    artworkArtist,
                                    string.Empty,
                                    null,
                                    fallbackTrackVersion));
                            }

                            if (OnlineLyricsEnabled && !string.IsNullOrWhiteSpace(wTitle))
                            {
                                var capKey = fallbackKey;
                                var capTitle = wTitle;
                                var capArtist = wArtist;
                                _ = Task.Run(async () =>
                                {
                                    var lyrics = await LoadLyricsAsync(capKey, capTitle, capArtist, string.Empty, TimeSpan.Zero);
                                    if (string.Equals(_trackKey, capKey, StringComparison.Ordinal))
                                    {
                                        _lyrics = lyrics;
                                    }
                                });
                            }
                        }

                        var adjustedFallbackPos = TimeSpan.Zero + TimeSpan.FromMilliseconds(OffsetMs);
                        var actualLyric = _lyrics.Count > 0 ? (SyncedLyricsParser.FindLine(_lyrics, adjustedFallbackPos) ?? string.Empty) : string.Empty;
                        var primaryFallback = !string.IsNullOrWhiteSpace(actualLyric) ? actualLyric : wTitle;
                        var secondaryFallback = !string.IsNullOrWhiteSpace(wArtist) ? wArtist : wApp;
                        Publish(new(
                            true,
                            true,
                            primaryFallback,
                            secondaryFallback,
                            wApp,
                            _onlineArtwork,
                            _lyrics.Count > 0 ? (SyncedLyricsParser.FindNextLine(_lyrics, adjustedFallbackPos) ?? string.Empty) : string.Empty,
                            0,
                            wTitle,
                            wArtist,
                            string.Empty,
                            string.Empty,
                            _lyrics.Count > 0,
                            actualLyric,
                            string.Empty,
                            fallbackKey,
                            Volatile.Read(ref _trackVersion)));
                        return;
                    }

                    _session = null;
                    _artwork = null;
                    _onlineArtwork = null;
                    if (!string.IsNullOrEmpty(_trackKey))
                    {
                        _trackKey = string.Empty;
                        _lastActiveTrackKey = string.Empty;
                        Interlocked.Increment(ref _trackVersion);
                    }
                    Publish(new(
                        false,
                        false,
                        "等待媒体播放",
                        "打开播放器后将在此显示歌词",
                        "系统媒体",
                        TrackKey: string.Empty,
                        TrackVersion: Volatile.Read(ref _trackVersion)));
                    return;
                }

                _session = session;
                var media = await session.TryGetMediaPropertiesAsync();
                var timeline = session.GetTimelineProperties();
                var playback = session.GetPlaybackInfo();
                var title = media.Title?.Trim() ?? string.Empty;
                var artist = media.Artist?.Trim() ?? string.Empty;
                var album = media.AlbumTitle?.Trim() ?? string.Empty;
                var duration = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime : TimeSpan.Zero;
                var sessionId = session.SourceAppUserModelId?.Trim() ?? string.Empty;
                var trackKey = $"{sessionId}\u001f{title}\u001f{artist}\u001f{album}";

                if (!string.Equals(trackKey, _trackKey, StringComparison.Ordinal))
                {
                    _previousTrackArtwork = _artwork ?? _onlineArtwork;
                    _previousTrackAlbum = _album;
                    _trackKey = trackKey;
                    _title = title;
                    _artist = artist;
                    _album = album;
                    _lyrics = Array.Empty<SyncedLyricLine>();
                    _onlineArtwork = null;
                    _artwork = null;
                    _lastTrackChangeAt = DateTimeOffset.Now;
                    var trackVersion = Interlocked.Increment(ref _trackVersion);

                    if (_artworkCache.TryGetValue(trackKey, out var cachedArt) && cachedArt is { Length: > 0 })
                    {
                        // 命中缓存，直接使用
                        _artwork = cachedArt;
                    }
                    else
                    {
                        var capturedKey = trackKey;
                        var capturedTitle = title;
                        var capturedArtist = artist;
                        var capturedAlbum = album;
                        _ = Task.Run(async () =>
                        {
                            // Several players publish new text before replacing their SMTC thumbnail.
                            // Do not associate that stale thumbnail with the new track: use a
                            // metadata-matched online result first and sample SMTC only after it settles.
                            await EnsureArtworkAsync(capturedKey, capturedTitle, capturedArtist, capturedAlbum, null, trackVersion);
                        });
                    }

                    if (OnlineLyricsEnabled && !string.IsNullOrWhiteSpace(title))
                    {
                        var capturedKey = trackKey;
                        var capturedTitle = title;
                        var capturedArtist = artist;
                        var capturedAlbum = album;
                        var capturedDuration = duration;
                        _ = Task.Run(async () =>
                        {
                            var lyrics = await LoadLyricsAsync(capturedKey, capturedTitle, capturedArtist, capturedAlbum, capturedDuration);
                            if (string.Equals(_trackKey, capturedKey, StringComparison.Ordinal))
                            {
                                _lyrics = lyrics;
                            }
                        });
                    }
                }
                else if (_artwork == null
                    && (DateTimeOffset.Now - _lastTrackChangeAt).TotalMilliseconds >= 180)
                {
                    // Wait for metadata and the player-owned thumbnail to advance together before
                    // accepting SMTC artwork. This path intentionally remains active while an
                    // online cover is available, because the player-owned thumbnail is authoritative.
                    _ = EnsureArtworkAsync(trackKey, title, artist, album, media.Thumbnail, Volatile.Read(ref _trackVersion));
                }

                var isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                bool smtcTimelineValid = timeline.EndTime > TimeSpan.Zero && timeline.LastUpdatedTime.Year > 2000;
                TimeSpan position;

                if (smtcTimelineValid)
                {
                    var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                    position = timeline.Position;
                    if (isPlaying && elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(10))
                        position += elapsed;

                    if (timeline.EndTime > TimeSpan.Zero && position > timeline.EndTime)
                        position = timeline.EndTime;

                    _playbackBasePosition = position;
                    _playbackStopwatch.Restart();
                    if (!isPlaying) _playbackStopwatch.Stop();
                }
                else
                {
                    // 对网易云音乐等未提供 SMTC 时间轴的播放器，采用高精度内部时钟推进
                    if (isPlaying)
                    {
                        if (!_playbackStopwatch.IsRunning)
                            _playbackStopwatch.Start();
                    }
                    else
                    {
                        if (_playbackStopwatch.IsRunning)
                            _playbackStopwatch.Stop();
                    }
                    position = _playbackBasePosition + _playbackStopwatch.Elapsed;
                }

                if (!string.Equals(trackKey, _lastActiveTrackKey, StringComparison.Ordinal))
                {
                    _lastActiveTrackKey = trackKey;
                    if (!smtcTimelineValid)
                    {
                        _playbackBasePosition = TimeSpan.Zero;
                        _playbackStopwatch.Restart();
                        if (!isPlaying) _playbackStopwatch.Stop();
                        position = TimeSpan.Zero;
                    }
                }

                var adjustedPosition = position + TimeSpan.FromMilliseconds(OffsetMs);
                var lyricLine = SyncedLyricsParser.FindLyricLine(_lyrics, adjustedPosition);
                var lyric = lyricLine?.Text;
                var currentTranslation = lyricLine?.Translation ?? string.Empty;
                var nextLyric = SyncedLyricsParser.FindNextLine(_lyrics, adjustedPosition);
                var hasSyncedLyrics = _lyrics.Count > 0;
                var timeString = timeline.EndTime > TimeSpan.Zero
                    ? $"{position:mm\\:ss} / {timeline.EndTime:mm\\:ss}"
                    : (position > TimeSpan.Zero ? $"{position:mm\\:ss}" : string.Empty);
                var primary = !string.IsNullOrWhiteSpace(lyric) ? lyric : "♪ 伴奏音乐中...";
                var secondary = !string.IsNullOrWhiteSpace(currentTranslation)
                    ? currentTranslation
                    : (!string.IsNullOrWhiteSpace(nextLyric) ? nextLyric : timeString);
                var progress = timeline.EndTime > TimeSpan.Zero
                    ? Math.Clamp(position.TotalMilliseconds / timeline.EndTime.TotalMilliseconds, 0, 1)
                    : 0;
                Publish(new(
                    true,
                    isPlaying,
                    primary,
                    secondary,
                    _lyrics.Count > 0 ? "多源同步歌词" : "系统媒体信息",
                    _artwork ?? _onlineArtwork,
                    nextLyric ?? string.Empty,
                    progress,
                    title,
                    artist,
                    album,
                    timeString,
                    hasSyncedLyrics,
                    lyric ?? string.Empty,
                    currentTranslation,
                    trackKey,
                    Volatile.Read(ref _trackVersion)));
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Media state refresh failed: {ex.Message}");
                Publish(new(false, false, "暂时无法读取媒体", "播放器可能尚未公开媒体会话", "系统媒体"));
            }
            finally
            {
                _busy = false;
            }
        }

        private static async Task<byte[]?> LoadArtworkAsync(IRandomAccessStreamReference? reference)
        {
            if (reference == null) return null;
            try
            {
                using var stream = await reference.OpenReadAsync();
                if (stream.Size is 0 or > 5 * 1024 * 1024) return null;
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size);
                var bytes = new byte[checked((int)stream.Size)];
                reader.ReadBytes(bytes);
                return bytes;
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Unable to read media artwork: {ex.Message}");
                return null;
            }
        }

        private async Task EnsureArtworkAsync(
            string key,
            string title,
            string artist,
            string album,
            IRandomAccessStreamReference? thumbnailRef,
            long trackVersion)
        {
            // Keep SMTC reads independent from network lookups. Previously both paths shared one
            // pending flag, so a slow online request prevented the local player thumbnail from
            // ever being sampled during the important post-track-change window.
            if (thumbnailRef != null && IsCurrentTrack(key, trackVersion) && _artwork == null)
            {
                if (!_pendingSmtcArtwork.TryAdd(key, 0))
                    return;

                try
                {
                    var smtcArt = await LoadArtworkAsync(thumbnailRef);
                    if (smtcArt is { Length: > 0 } && IsCurrentTrack(key, trackVersion))
                    {
                        var stillInsideStaleWindow = (DateTimeOffset.Now - _lastTrackChangeAt).TotalMilliseconds < 1400;
                        var albumChanged = !string.Equals(
                            NormalizeArtworkMatchText(_previousTrackAlbum),
                            NormalizeArtworkMatchText(album),
                            StringComparison.Ordinal);
                        if (stillInsideStaleWindow && albumChanged &&
                            _previousTrackArtwork is { Length: > 0 } previousArtwork &&
                            previousArtwork.AsSpan().SequenceEqual(smtcArt))
                        {
                            // Several desktop players update text first and retain the previous
                            // thumbnail for a short period. Retry on the next poll instead of
                            // flashing the previous song's artwork.
                            return;
                        }

                        _artworkCache[key] = smtcArt;
                        _artwork = smtcArt;
                        return;
                    }
                }
                finally
                {
                    _pendingSmtcArtwork.TryRemove(key, out _);
                }
            }

            if (_artworkCache.TryGetValue(key, out var cachedArt) && cachedArt is { Length: > 0 })
            {
                if (IsCurrentTrack(key, trackVersion) && _artwork == null)
                    _artwork = cachedArt;
                return;
            }

            // Refresh runs every 80 ms; only one cover request may be in flight for a track.
            if (!_pendingOnlineArtwork.TryAdd(key, 0))
                return;

            try
            {
                var (cleanTitle, cleanArtist) = TrackMetadataSanitizer.Sanitize(title, artist);
                var onlineArt = await FetchOnlineArtworkDirectAsync(cleanTitle, cleanArtist, album);
                if (onlineArt is { Length: > 0 })
                {
                    if (IsCurrentTrack(key, trackVersion))
                    {
                        _onlineArtwork = onlineArt;
                        // Never replace a player-owned thumbnail that completed while the
                        // network request was in flight.
                        if (_artwork == null)
                            _artworkCache[key] = onlineArt;
                    }
                    else
                    {
                        _artworkCache[key] = onlineArt;
                    }
                    return;
                }
            }
            finally
            {
                _pendingOnlineArtwork.TryRemove(key, out _);
            }
        }

        private bool IsCurrentTrack(string key, long trackVersion) =>
            string.Equals(_trackKey, key, StringComparison.Ordinal) &&
            Volatile.Read(ref _trackVersion) == trackVersion;

        private enum ArtworkProvider
        {
            Netease,
            QQ,
            ITunes,
            Kugou
        }

        private async Task<byte[]?> FetchOnlineArtworkDirectAsync(string title, string artist, string album)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;

            var pending = Enum.GetValues<ArtworkProvider>()
                .Select(provider => FetchOnlineArtworkProviderAsync(provider, title, artist, album))
                .ToList();
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                var artwork = await completed;
                if (artwork is { Length: > 100 })
                    return artwork;
            }

            return null;
        }

        private async Task<byte[]?> FetchOnlineArtworkProviderAsync(
            ArtworkProvider provider,
            string title,
            string artist,
            string album)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;

            // 1. 网易云音乐封面
            if (provider == ArtworkProvider.Netease)
            {
              try
              {
                var query = BuildArtworkQuery(title, artist, album);
                var searchUrl = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(query)}&type=1&offset=0&total=true&limit=3";
                using var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                searchReq.Headers.Referrer = new Uri("https://music.163.com");
                searchReq.Headers.TryAddWithoutValidation("Cookie", "os=pc; osver=Microsoft-Windows-10; appver=2.9.7; NMTID=00O;");
                using var searchResp = await LyricsClient.SendAsync(searchReq);
                if (searchResp.IsSuccessStatusCode)
                {
                    await using var searchStream = await searchResp.Content.ReadAsStreamAsync();
                    using var searchDoc = await JsonDocument.ParseAsync(searchStream);
                    if (searchDoc.RootElement.TryGetProperty("result", out var result) &&
                        result.TryGetProperty("songs", out var songs) &&
                        songs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var song in songs.EnumerateArray())
                        {
                            var candidateTitle = song.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                            var candidateArtist = GetArtistNames(song, "artists", "ar");
                            if (!IsArtworkCandidateMatch(candidateTitle, candidateArtist, title, artist))
                                continue;
                            string? picUrl = null;
                            if ((song.TryGetProperty("album", out var albObj) || song.TryGetProperty("al", out albObj)) &&
                                albObj.TryGetProperty("picUrl", out var picProp) &&
                                picProp.ValueKind == JsonValueKind.String)
                            {
                                picUrl = picProp.GetString();
                            }
                            else if ((song.TryGetProperty("artists", out var artArray) || song.TryGetProperty("ar", out artArray)) &&
                                artArray.ValueKind == JsonValueKind.Array &&
                                artArray.GetArrayLength() > 0 &&
                                artArray[0].TryGetProperty("img1v1Url", out var imgProp) &&
                                imgProp.ValueKind == JsonValueKind.String)
                            {
                                picUrl = imgProp.GetString();
                            }

                            if (!string.IsNullOrWhiteSpace(picUrl))
                            {
                                var fullUrl = picUrl.Contains("126.net", StringComparison.OrdinalIgnoreCase) ? (picUrl + "?param=160y160") : picUrl;
                                var bytes = await LyricsClient.GetByteArrayAsync(fullUrl);
                                if (bytes is { Length: > 100 }) return bytes;
                            }
                        }
                    }
                }
              }
              catch { }
              return null;
            }

            // 2. QQ 音乐封面
            if (provider == ArtworkProvider.QQ)
            {
              try
              {
                var query = BuildArtworkQuery(title, artist, album);
                var searchUrl = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?p=1&n=3&w={Uri.EscapeDataString(query)}&format=json";
                using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                req.Headers.Referrer = new Uri("https://y.qq.com");
                using var resp = await LyricsClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("song", out var songObj) &&
                        songObj.TryGetProperty("list", out var list) &&
                        list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var song in list.EnumerateArray())
                        {
                            var candidateTitle = song.TryGetProperty("songname", out var nameProp) ? nameProp.GetString() : null;
                            var candidateArtist = GetArtistNames(song, "singer", "singers");
                            if (!IsArtworkCandidateMatch(candidateTitle, candidateArtist, title, artist))
                                continue;
                            if ((song.TryGetProperty("albummid", out var albProp) ||
                                 (song.TryGetProperty("album", out var qqAlb) && qqAlb.TryGetProperty("mid", out albProp))) &&
                                albProp.ValueKind == JsonValueKind.String)
                            {
                                var albMid = albProp.GetString();
                                if (!string.IsNullOrWhiteSpace(albMid))
                                {
                                    var bytes = await LyricsClient.GetByteArrayAsync($"https://y.gtimg.cn/music/photo_new/T002R150x150M000{albMid}.jpg");
                                    if (bytes is { Length: > 100 }) return bytes;
                                }
                            }
                        }
                    }
                }
              }
              catch { }
              return null;
            }

            // 3. iTunes 全球音乐库封面 (免费、免Key、高解析度)
            if (provider == ArtworkProvider.ITunes)
            {
              try
              {
                var query = BuildArtworkQuery(title, artist, album);
                var searchUrl = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&entity=song&limit=3";
                using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                using var resp = await LyricsClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);
                    if (doc.RootElement.TryGetProperty("results", out var results) &&
                        results.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in results.EnumerateArray())
                        {
                            var candidateTitle = item.TryGetProperty("trackName", out var trackProp) ? trackProp.GetString() : null;
                            var candidateArtist = item.TryGetProperty("artistName", out var artistProp) ? artistProp.GetString() : null;
                            if (!IsArtworkCandidateMatch(candidateTitle, candidateArtist, title, artist))
                                continue;
                            if (item.TryGetProperty("artworkUrl100", out var artProp) &&
                                artProp.ValueKind == JsonValueKind.String)
                            {
                                var artUrl = artProp.GetString();
                                if (!string.IsNullOrWhiteSpace(artUrl))
                                {
                                    var highResUrl = artUrl.Replace("100x100bb", "300x300bb");
                                    var bytes = await LyricsClient.GetByteArrayAsync(highResUrl);
                                    if (bytes is { Length: > 100 }) return bytes;
                                }
                            }
                        }
                    }
                }
              }
              catch { }
              return null;
            }

            // 4. 酷狗音乐封面
            if (provider == ArtworkProvider.Kugou)
            {
              try
              {
                var query = BuildArtworkQuery(title, artist, album);
                var searchUrl = $"http://mobilecdn.kugou.com/api/v3/search/song?keyword={Uri.EscapeDataString(query)}&page=1&pagesize=3";
                using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                using var resp = await LyricsClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("info", out var list) &&
                        list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var song in list.EnumerateArray())
                        {
                            var candidateTitle = song.TryGetProperty("songname", out var nameProp) ? nameProp.GetString() : null;
                            var candidateArtist = song.TryGetProperty("singername", out var singerProp) ? singerProp.GetString() : null;
                            if (!IsArtworkCandidateMatch(candidateTitle, candidateArtist, title, artist))
                                continue;
                            if (song.TryGetProperty("album_sizable_cover", out var coverProp) &&
                                coverProp.ValueKind == JsonValueKind.String)
                            {
                                var coverTpl = coverProp.GetString();
                                if (!string.IsNullOrWhiteSpace(coverTpl))
                                {
                                    var coverUrl = coverTpl.Replace("{size}", "240");
                                    var bytes = await LyricsClient.GetByteArrayAsync(coverUrl);
                                    if (bytes is { Length: > 100 }) return bytes;
                                }
                            }
                        }
                    }
                }
              }
              catch { }
            }

            return null;
        }

        private static string BuildArtworkQuery(string title, string artist, string album) =>
            string.Join(" ", new[] { title, artist, album }.Where(value => !string.IsNullOrWhiteSpace(value)));

        private static string GetArtistNames(JsonElement song, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                if (!song.TryGetProperty(propertyName, out var artists) || artists.ValueKind != JsonValueKind.Array)
                    continue;

                var names = artists.EnumerateArray()
                    .Where(item => item.TryGetProperty("name", out _))
                    .Select(item => item.GetProperty("name").GetString())
                    .Where(name => !string.IsNullOrWhiteSpace(name));
                return string.Join(" ", names!);
            }

            return string.Empty;
        }

        private static bool IsArtworkCandidateMatch(string? candidateTitle, string? candidateArtist, string title, string artist)
        {
            var expectedTitle = NormalizeArtworkMatchText(title);
            var actualTitle = NormalizeArtworkMatchText(candidateTitle);
            if (expectedTitle.Length < 2 || actualTitle.Length < 2 ||
                (!actualTitle.Contains(expectedTitle, StringComparison.Ordinal) && !expectedTitle.Contains(actualTitle, StringComparison.Ordinal)))
                return false;

            var expectedArtist = NormalizeArtworkMatchText(artist);
            if (expectedArtist.Length < 2)
                return true;

            var actualArtist = NormalizeArtworkMatchText(candidateArtist);
            return actualArtist.Length > 0 &&
                   (actualArtist.Contains(expectedArtist, StringComparison.Ordinal) || expectedArtist.Contains(actualArtist, StringComparison.Ordinal));
        }

        private static string NormalizeArtworkMatchText(string? value) =>
            Regex.Replace(value ?? string.Empty, @"[^\p{L}\p{N}]", string.Empty).ToLowerInvariant();

        private async Task<IReadOnlyList<SyncedLyricLine>> LoadLyricsAsync(string key, string title, string artist, string album, TimeSpan duration)
        {
            if (_lyricsCache.TryGetValue(key, out var cached))
                return cached;
            if (_lyricsCache.Count >= 128 && _lyricsCache.Keys.FirstOrDefault() is { } oldestKey)
                _lyricsCache.TryRemove(oldestKey, out _);

            var (cleanTitle, cleanArtist) = TrackMetadataSanitizer.Sanitize(title, artist);

            try
            {
                // 1. 优先尝试 网易云音乐源（海量全曲库、极高双语翻译覆盖率）
                if (SourceNeteaseEnabled)
                {
                    var neteaseLyrics = await FetchNeteaseLyricsAsync(cleanTitle, cleanArtist, key);
                    if (neteaseLyrics.Count > 0)
                    {
                        // 若含有翻译，立即优先采纳
                        if (neteaseLyrics.Any(l => !string.IsNullOrWhiteSpace(l.Translation)))
                        {
                            _lyricsCache[key] = neteaseLyrics;
                            return neteaseLyrics;
                        }
                    }
                }

                // 2. 尝试 QQ 音乐源（极速响应、正版曲库）
                if (SourceQQEnabled)
                {
                    var qqLyrics = await FetchQQMusicLyricsAsync(cleanTitle, cleanArtist, key);
                    if (qqLyrics.Count > 0)
                    {
                        if (qqLyrics.Any(l => !string.IsNullOrWhiteSpace(l.Translation)))
                        {
                            _lyricsCache[key] = qqLyrics;
                            return qqLyrics;
                        }
                    }
                }

                // 3. 若无双语翻译，按质量依次采纳单语歌词 (网易云 -> QQ音乐 -> 酷狗 -> LRCLIB)
                if (SourceNeteaseEnabled)
                {
                    var neteaseLyrics = await FetchNeteaseLyricsAsync(cleanTitle, cleanArtist);
                    if (neteaseLyrics.Count > 0)
                    {
                        _lyricsCache[key] = neteaseLyrics;
                        return neteaseLyrics;
                    }
                }

                if (SourceQQEnabled)
                {
                    var qqLyrics = await FetchQQMusicLyricsAsync(cleanTitle, cleanArtist);
                    if (qqLyrics.Count > 0)
                    {
                        _lyricsCache[key] = qqLyrics;
                        return qqLyrics;
                    }
                }

                // 4. 尝试 酷狗音乐移动端源
                if (SourceKugouEnabled)
                {
                    var kugouLyrics = await FetchKugouLyricsAsync(cleanTitle, cleanArtist);
                    if (kugouLyrics.Count > 0)
                    {
                        _lyricsCache[key] = kugouLyrics;
                        return kugouLyrics;
                    }
                }

                // 5. 尝试 LRCLIB 国际公共源
                if (SourceLrclibEnabled)
                {
                    var lrclibLyrics = await FetchLrclibLyricsAsync(cleanTitle, cleanArtist, album, duration);
                    if (lrclibLyrics.Count > 0)
                    {
                        _lyricsCache[key] = lrclibLyrics;
                        return lrclibLyrics;
                    }
                }

                // 6. 若带歌手未查到，尝试仅用纯歌名在已启用的源中进行检索
                if (!string.IsNullOrWhiteSpace(cleanArtist))
                {
                    if (SourceNeteaseEnabled)
                    {
                        var fallbackNetease = await FetchNeteaseLyricsAsync(cleanTitle, string.Empty);
                        if (fallbackNetease.Count > 0)
                        {
                            _lyricsCache[key] = fallbackNetease;
                            return fallbackNetease;
                        }
                    }

                    if (SourceQQEnabled)
                    {
                        var fallbackQQ = await FetchQQMusicLyricsAsync(cleanTitle, string.Empty);
                        if (fallbackQQ.Count > 0)
                        {
                            _lyricsCache[key] = fallbackQQ;
                            return fallbackQQ;
                        }
                    }

                    if (SourceKugouEnabled)
                    {
                        var fallbackKg = await FetchKugouLyricsAsync(cleanTitle, string.Empty);
                        if (fallbackKg.Count > 0)
                        {
                            _lyricsCache[key] = fallbackKg;
                            return fallbackKg;
                        }
                    }
                }

                var noLyrics = Array.Empty<SyncedLyricLine>();
                _lyricsCache[key] = noLyrics;
                return noLyrics;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Multi-source lyrics lookup failed: {ex.Message}");
                var noLyrics = Array.Empty<SyncedLyricLine>();
                _lyricsCache[key] = noLyrics;
                return noLyrics;
            }
        }

        private async Task<IReadOnlyList<SyncedLyricLine>> FetchNeteaseLyricsAsync(string title, string artist, string key = "")
        {
            try
            {
                var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
                var searchUrl = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(query)}&type=1&offset=0&total=true&limit=3";
                using var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                searchReq.Headers.Referrer = new Uri("https://music.163.com");
                searchReq.Headers.TryAddWithoutValidation("Cookie", "os=pc; osver=Microsoft-Windows-10; appver=2.9.7; NMTID=00O;");
                using var searchResp = await LyricsClient.SendAsync(searchReq);
                if (!searchResp.IsSuccessStatusCode) return Array.Empty<SyncedLyricLine>();

                await using var searchStream = await searchResp.Content.ReadAsStreamAsync();
                using var searchDoc = await JsonDocument.ParseAsync(searchStream);
                if (!searchDoc.RootElement.TryGetProperty("result", out var result) ||
                    !result.TryGetProperty("songs", out var songs) ||
                    songs.ValueKind != JsonValueKind.Array)
                    return Array.Empty<SyncedLyricLine>();

                foreach (var song in songs.EnumerateArray())
                {
                    if (song.TryGetProperty("id", out var idProp))
                    {
                        var songId = idProp.GetInt64();
                        var lyricUrl = $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=1";
                        using var lyricReq = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
                        lyricReq.Headers.Referrer = new Uri("https://music.163.com");
                        lyricReq.Headers.TryAddWithoutValidation("Cookie", "os=pc; osver=Microsoft-Windows-10; appver=2.9.7; NMTID=00O;");
                        using var lyricResp = await LyricsClient.SendAsync(lyricReq);
                        if (lyricResp.IsSuccessStatusCode)
                        {
                            await using var lyricStream = await lyricResp.Content.ReadAsStreamAsync();
                            using var lyricDoc = await JsonDocument.ParseAsync(lyricStream);
                            var origStr = lyricDoc.RootElement.TryGetProperty("lrc", out var lrc) &&
                                          lrc.TryGetProperty("lyric", out var lProp) &&
                                          lProp.ValueKind == JsonValueKind.String
                                ? lProp.GetString()
                                : null;

                            var transStr = lyricDoc.RootElement.TryGetProperty("tlyric", out var tlrc) &&
                                           tlrc.TryGetProperty("lyric", out var tlProp) &&
                                           tlProp.ValueKind == JsonValueKind.String
                                ? tlProp.GetString()
                                : null;

                            if (!string.IsNullOrWhiteSpace(origStr))
                            {
                                var origLines = SyncedLyricsParser.Parse(origStr);
                                var transLines = !string.IsNullOrWhiteSpace(transStr)
                                    ? SyncedLyricsParser.Parse(transStr)
                                    : Array.Empty<SyncedLyricLine>();

                                var merged = SyncedLyricsParser.Merge(origLines, transLines);
                                if (merged.Count > 0)
                                {
                                    return merged;
                                }
                            }
                        }
                    }
                }
                return Array.Empty<SyncedLyricLine>();
            }
            catch { return Array.Empty<SyncedLyricLine>(); }
        }

        private async Task<IReadOnlyList<SyncedLyricLine>> FetchQQMusicLyricsAsync(string title, string artist, string key = "")
        {
            try
            {
                var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
                var searchUrl = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={Uri.EscapeDataString(query)}&format=json&p=1&n=3";
                using var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                searchReq.Headers.Referrer = new Uri("https://y.qq.com");
                using var searchResp = await LyricsClient.SendAsync(searchReq);
                if (!searchResp.IsSuccessStatusCode) return Array.Empty<SyncedLyricLine>();

                await using var searchStream = await searchResp.Content.ReadAsStreamAsync();
                using var searchDoc = await JsonDocument.ParseAsync(searchStream);
                if (!searchDoc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("song", out var songObj) ||
                    !songObj.TryGetProperty("list", out var list) ||
                    list.ValueKind != JsonValueKind.Array)
                    return Array.Empty<SyncedLyricLine>();

                foreach (var song in list.EnumerateArray())
                {
                    if (song.TryGetProperty("songmid", out var midProp))
                    {
                        var songmid = midProp.GetString();
                        if (string.IsNullOrWhiteSpace(songmid)) continue;
                        var lyricUrl = $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Uri.EscapeDataString(songmid)}&format=json&nobase64=1";
                        using var req = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
                        req.Headers.Referrer = new Uri("https://y.qq.com");
                        using var lyricResp = await LyricsClient.SendAsync(req);
                        if (lyricResp.IsSuccessStatusCode)
                        {
                            await using var lyricStream = await lyricResp.Content.ReadAsStreamAsync();
                            using var lyricDoc = await JsonDocument.ParseAsync(lyricStream);
                            var origStr = lyricDoc.RootElement.TryGetProperty("lyric", out var lyricProp) &&
                                          lyricProp.ValueKind == JsonValueKind.String
                                ? WebUtility.HtmlDecode(lyricProp.GetString())
                                : null;

                            var transStr = lyricDoc.RootElement.TryGetProperty("trans", out var transProp) &&
                                           transProp.ValueKind == JsonValueKind.String
                                ? WebUtility.HtmlDecode(transProp.GetString())
                                : null;

                            if (!string.IsNullOrWhiteSpace(origStr))
                            {
                                var origLines = SyncedLyricsParser.Parse(origStr);
                                var transLines = !string.IsNullOrWhiteSpace(transStr)
                                    ? SyncedLyricsParser.Parse(transStr)
                                    : Array.Empty<SyncedLyricLine>();

                                var merged = SyncedLyricsParser.Merge(origLines, transLines);
                                if (merged.Count > 0)
                                {
                                    return merged;
                                }
                            }
                        }
                    }
                }
                return Array.Empty<SyncedLyricLine>();
            }
            catch { return Array.Empty<SyncedLyricLine>(); }
        }

        private static async Task<IReadOnlyList<SyncedLyricLine>> FetchKugouLyricsAsync(string title, string artist)
        {
            try
            {
                var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
                var searchUrl = $"http://mobilecdn.kugou.com/api/v3/search/song?keyword={Uri.EscapeDataString(query)}&page=1&pagesize=3";
                using var searchResp = await LyricsClient.GetAsync(searchUrl);
                if (!searchResp.IsSuccessStatusCode) return Array.Empty<SyncedLyricLine>();

                await using var searchStream = await searchResp.Content.ReadAsStreamAsync();
                using var searchDoc = await JsonDocument.ParseAsync(searchStream);
                if (!searchDoc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("info", out var infoList) ||
                    infoList.ValueKind != JsonValueKind.Array)
                    return Array.Empty<SyncedLyricLine>();

                foreach (var info in infoList.EnumerateArray())
                {
                    if (info.TryGetProperty("hash", out var hashProp))
                    {
                        var hash = hashProp.GetString();
                        if (string.IsNullOrWhiteSpace(hash)) continue;

                        var lrcSearchUrl = $"http://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=&duration=&hash={hash}";
                        using var lrcSearchResp = await LyricsClient.GetAsync(lrcSearchUrl);
                        if (!lrcSearchResp.IsSuccessStatusCode) continue;

                        await using var lrcSearchStream = await lrcSearchResp.Content.ReadAsStreamAsync();
                        using var lrcSearchDoc = await JsonDocument.ParseAsync(lrcSearchStream);
                        if (lrcSearchDoc.RootElement.TryGetProperty("candidates", out var candidates) &&
                            candidates.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var cand in candidates.EnumerateArray())
                            {
                                if (cand.TryGetProperty("id", out var idProp) &&
                                    cand.TryGetProperty("accesskey", out var keyProp))
                                {
                                    var id = idProp.GetString() ?? idProp.GetRawText();
                                    var key = keyProp.GetString();
                                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(key)) continue;

                                    var downUrl = $"http://krcs.kugou.com/download?ver=1&man=yes&client=mobi&id={id}&accesskey={key}&fmt=lrc&charset=utf8";
                                    using var downResp = await LyricsClient.GetAsync(downUrl);
                                    if (downResp.IsSuccessStatusCode)
                                    {
                                        await using var downStream = await downResp.Content.ReadAsStreamAsync();
                                        using var downDoc = await JsonDocument.ParseAsync(downStream);
                                        if (downDoc.RootElement.TryGetProperty("content", out var contentProp) &&
                                            contentProp.ValueKind == JsonValueKind.String)
                                        {
                                            var base64 = contentProp.GetString();
                                            if (!string.IsNullOrWhiteSpace(base64))
                                            {
                                                var bytes = Convert.FromBase64String(base64);
                                                var lrcText = System.Text.Encoding.UTF8.GetString(bytes);
                                                var parsed = SyncedLyricsParser.Parse(lrcText);
                                                if (parsed.Count > 0)
                                                    return parsed;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                return Array.Empty<SyncedLyricLine>();
            }
            catch { return Array.Empty<SyncedLyricLine>(); }
        }

        private static async Task<IReadOnlyList<SyncedLyricLine>> FetchLrclibLyricsAsync(string title, string artist, string album, TimeSpan duration)
        {
            try
            {
                var url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) +
                          "&artist_name=" + Uri.EscapeDataString(artist) +
                          "&album_name=" + Uri.EscapeDataString(album) +
                          "&duration=" + Math.Max(0, Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                using var response = await LyricsClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    using var json = await JsonDocument.ParseAsync(stream);
                    var synced = json.RootElement.TryGetProperty("syncedLyrics", out var element) && element.ValueKind == JsonValueKind.String
                        ? element.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(synced))
                    {
                        var parsed = SyncedLyricsParser.Parse(synced);
                        if (parsed.Count > 0) return parsed;
                    }
                }

                var searchCandidate = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
                var searchUrl = "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(searchCandidate);
                using var searchResponse = await LyricsClient.GetAsync(searchUrl);
                if (searchResponse.IsSuccessStatusCode)
                {
                    await using var searchStream = await searchResponse.Content.ReadAsStreamAsync();
                    using var searchJson = await JsonDocument.ParseAsync(searchStream);
                    if (searchJson.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in searchJson.RootElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("syncedLyrics", out var syn) &&
                                syn.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(syn.GetString()))
                            {
                                var parsed = SyncedLyricsParser.Parse(syn.GetString());
                                if (parsed.Count > 0)
                                    return parsed;
                            }
                        }
                    }
                }
                return Array.Empty<SyncedLyricLine>();
            }
            catch { return Array.Empty<SyncedLyricLine>(); }
        }

        private void Publish(TaskbarMediaSnapshot snapshot)
        {
            if (snapshot == Current)
                return;
            Current = snapshot;
            StateChanged?.Invoke(this, snapshot);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The process-wide HttpClient owns and disposes its handler via disposeHandler: true.")]
        private static HttpClient CreateLyricsClient()
        {
            var client = new HttpClient(
                new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    EnableMultipleHttp2Connections = true
                },
                disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(6)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            return client;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer.Stop();
            DetachManagerEvents();
            _session = null;
            _manager = null;
        }
    }
}
