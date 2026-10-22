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

        // 制作信息行（作词/作曲/编曲…）：任务栏上只会无意义地闪一下，还会抢走
        // 首句译文的时间窗（这些行全挤在 0~1s，正好覆盖首句译文的时间戳）。
        // 与 Lyricify Lyrics Helper 的「识别并处理信息行」同一思路。
        private static readonly Regex CreditLineRegex = new(
            @"^\s*(?:作词|作詞|作曲|编曲|編曲|作词作曲|词曲|詞曲|混音|母带|母帶|制作人|製作人|监制|監製|策划|策劃|出品|和声|和聲|录音|錄音|发行|發行|封面|插图|插圖)\s*[:：\s]|^\s*(?:Lyrics?|Lyricist|Lyrics\s*by|Composer|Composed\s*by|Arranger|Arranged\s*by|Producer|Produced\s*by|Written\s*by|Words\s*by|Music\s*by|Vocals?|Guitar|Bass|Drums?|Keyboard|Strings|Piano|Engineer|Mix(?:ed)?(?:\s*by)?|Master(?:ing)?|Programming|Original\s*Song)(?:\s*[:：]|\s+\S)|^\s*纯音乐[，,。 ]*请欣赏\s*$",
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
                    // 空时间轴行只会制造“下一句”假提示，不应进入渲染队列。
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    // 制作信息行同理：既不渲染，也不该参与译文配对。
                    if (CreditLineRegex.IsMatch(text))
                        continue;
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
            var usedTranslations = new HashSet<int>();
            foreach (var orig in original)
            {
                var matched = translations
                    .Select((line, index) => (line, index))
                    .Where(item => !usedTranslations.Contains(item.index))
                    // 宽松匹配会把下一句翻译错误绑定到当前歌词，导致“即将播放”错觉。
                    .Where(item => Math.Abs((item.line.Time - orig.Time).TotalMilliseconds) <= 800 &&
                                   item.line.Time <= orig.Time + TimeSpan.FromMilliseconds(450))
                    .OrderBy(item => Math.Abs((item.line.Time - orig.Time).TotalMilliseconds))
                    .FirstOrDefault();

                var matchedTrans = matched.line;
                if (matched.line is not null)
                    usedTranslations.Add(matched.index);

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

        /// <summary>译文覆盖率（0~1）：多版本候选择优用，优先带完整译文的版本。</summary>
        public static double TranslationCoverage(IReadOnlyList<SyncedLyricLine> lines)
        {
            if (lines.Count == 0) return 0;
            return lines.Count(line => !string.IsNullOrWhiteSpace(line.Translation)) / (double)lines.Count;
        }

        public static string? FindNextLine(IReadOnlyList<SyncedLyricLine> lines, TimeSpan position)
        {
            if (lines.Count == 0) return null;
            // 前奏阶段不要提前显示第一句歌词，避免出现“即将播放”的误导提示。
            if (position < lines[0].Time)
                return null;

            for (var index = 0; index < lines.Count; index++)
            {
                if (lines[index].Time > position + TimeSpan.FromMilliseconds(120))
                {
                    // 仅在下一句即将到来时提供短暂预告。1 秒窗口可避免
                    // 歌词间奏较长时副行长期显示“即将播放”。
                    return lines[index].Time - position <= TimeSpan.FromSeconds(1)
                        ? lines[index].Text
                        : null;
                }
            }
            return null;
        }

}

    /// <summary>
    /// 歌词候选匹配打分（移植自 ChissaQAQ/TaskbarLyrics 的择优思路，MIT）：
    /// 歌名归一化三档打分 + 版本标签罚分 + 歌手宽松匹配 + 时长过滤。
    /// 没有这层校验时，同名翻唱 / Live / Remix 会被整首错配。
    /// </summary>
    public static class LyricMatch
    {
        internal const int TitleScoreMax = 4;

        private static readonly Regex VersionTagRegex = new(
            @"(?<![a-z])(?:live|inst(?:rumental)?|dj|remix(?:ed)?|cover|karaoke|acoustic|unplugged|piano|sped\s*up|speed\s*up|nightcore|slowed)(?![a-z])|现场|現場|伴奏|纯音乐|純音樂|翻唱|弹唱|彈唱|钢琴版|鋼琴版|加速版|降速版|慢速版",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly char[] ArtistSeparators = { '/', '、', '，', ',', '&', '+', ';', '；' };

        /// <summary>匹配用归一化：全角转半角、小写化，只留字母和数字（忽略空白与标点差异）。</summary>
        public static string Normalize(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c0 in s)
            {
                var c = c0;
                if (c is >= '\uFF01' and <= '\uFF5E') c = (char)(c - 0xFEE0);
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 歌名匹配度：0=不匹配；归一化完全相等为 4，一方包含另一方（覆盖 “(Live)” 等后缀差异）为 2。
        /// 候选带了歌名里没有的版本标签（Live/伴奏/Remix…）再减 1，只在同一档内后移、不跨档。
        /// </summary>
        public static int TitleScore(string? candidate, string title)
        {
            var a = Normalize(candidate);
            var b = Normalize(title);
            if (a.Length == 0 || b.Length == 0) return 0;
            var score = a == b ? TitleScoreMax : a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal) ? 2 : 0;
            if (score == 0) return 0;
            var titleTags = VersionTagsOf(title);
            var candTags = VersionTagsOf(candidate);
            return candTags.Any(tag => !titleTags.Contains(tag)) ? score - 1 : score;
        }

        /// <summary>
        /// 歌手宽松匹配：SMTC 的歌手串与候选的任一歌手名，拆成单个歌手、归一化之后，
        /// 只要有一对相等或一方包含另一方就算对上。
        /// </summary>
        public static bool ArtistMatches(string? candidateNames, string artist)
        {
            if (string.IsNullOrWhiteSpace(candidateNames)) return false;
            var want = SplitArtists(artist);
            if (want.Count == 0) return false;
            foreach (var part in SplitArtists(candidateNames))
                foreach (var w in want)
                    if (part == w || part.Contains(w, StringComparison.Ordinal) || w.Contains(part, StringComparison.Ordinal))
                        return true;
            return false;
        }

        private static List<string> SplitArtists(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new List<string>();
            var parts = s.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                // “A feat. B” / “A x B” 再拆一层
                var feats = System.Text.RegularExpressions.Regex.Split(part, @"\s+(?:feat\.?|ft\.?|featuring|x|X)\s+");
                foreach (var f in feats)
                {
                    var n = Normalize(f);
                    if (n.Length > 0) result.Add(n);
                }
            }
            return result;
        }

        private static HashSet<string> VersionTagsOf(string? s)
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(s)) return tags;
            foreach (Match m in VersionTagRegex.Matches(s))
            {
                tags.Add(m.Value.ToLowerInvariant() switch
                {
                    "现场" or "現場" => "live",
                    "instrumental" or "伴奏" or "纯音乐" or "純音樂" or "karaoke" => "inst",
                    "remixed" => "remix",
                    "翻唱" => "cover",
                    "unplugged" or "弹唱" or "彈唱" => "acoustic",
                    "钢琴版" or "鋼琴版" => "piano",
                    "nightcore" or "加速版" => "fast",
                    "slowed" or "降速版" or "慢速版" => "slow",
                    var v when v.StartsWith("sped", StringComparison.Ordinal) || v.StartsWith("speed", StringComparison.Ordinal) => "fast",
                    var v => v,
                });
            }
            return tags;
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

    /// <summary>一次歌词抓取的结果：歌词行 + 匹配到的曲库登记时长（毫秒，未知为 0）。
    /// 曲库时长供无 SMTC 时间轴播放器的单曲循环重播检测使用。</summary>
    internal sealed record LyricsFetchResult(IReadOnlyList<SyncedLyricLine> Lines, long SongDurationMs)
    {
        public static readonly LyricsFetchResult Empty = new(Array.Empty<SyncedLyricLine>(), 0);
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
        long TrackVersion = 0,
        bool CanControlPlayback = true,
        IReadOnlyList<SyncedLyricLine>? LayoutLyrics = null);

    public sealed class TaskbarMediaService : IDisposable
    {
        private const int MaxArtworkCacheEntries = 128;
        private static readonly HttpClient LyricsClient = CreateLyricsClient();
        // Default poll raised from 80ms to 250ms: 80ms burned CPU for
        // negligible lyric-sync benefit and starved the UI thread.
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private readonly LyricsLookupCache _lyricsCache = new();
        private readonly MediaDisplayContinuity _displayContinuity = new();
        private readonly StableLyricSelector _lyricSelector = new();
        private readonly object _lyricsRequestSync = new();
        private CancellationTokenSource? _lyricsRequestCts;
        private int _lyricsSources = 15;
        private long _lyricsLookupVersion;
        private DateTimeOffset _nextLyricsRetry = DateTimeOffset.MinValue;
        private readonly ConcurrentDictionary<string, byte[]> _artworkCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _pendingOnlineArtwork = new(StringComparer.Ordinal);
        // 在线封面取不到的 trackKey → 上次失败时间，用于负缓存（避免每轮重试）。
        private readonly ConcurrentDictionary<string, DateTimeOffset> _artworkMisses = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _pendingSmtcArtwork = new(StringComparer.Ordinal);
        private readonly System.Diagnostics.Stopwatch _playbackStopwatch = new();
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly AsyncRunLifetime _runLifetime = new();
        private readonly object _startSync = new();
        private readonly Func<CancellationToken, Task<GlobalSystemMediaTransportControlsSessionManager?>> _managerFactory;
        private Task? _startTask;
        private AsyncRunLifetime.Lease? _activeRun;
        internal bool IsPolling => _timer.IsEnabled;
        internal bool HasManagerSubscriptions => _managerEventsAttached;
        private TimeSpan _playbackBasePosition = TimeSpan.Zero;
        private string _lastActiveTrackKey = string.Empty;
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private IReadOnlyList<SyncedLyricLine> _lyrics = Array.Empty<SyncedLyricLine>();
        // 匹配到的曲库歌曲时长（毫秒）：网易云等播放器不上报 SMTC 时间轴时，
        // 单曲循环重播检测靠它判断内部时钟何时越界（见 RefreshAsync）。
        private long _lyricsSongDurationMs;
        private DateTimeOffset _lastReplayResetAt = DateTimeOffset.MinValue;
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
        private volatile bool _busy;
        private bool _disposed;
        private bool _managerEventsAttached;
        private bool _onlineLyricsEnabled = true;
        private long _lastWindowSniffTick;
        private (string Title, string Artist, string AppName)? _lastWindowSniff;
        private const int WindowSniffMinIntervalMs = 1000;

        public bool OnlineLyricsEnabled
        {
            get => _onlineLyricsEnabled;
            set
            {
                if (_onlineLyricsEnabled == value) return;
                _onlineLyricsEnabled = value;
                ReloadLyrics();
                _onlineArtwork = null;
            }
        }

        private bool _onlyMusicLyrics = true;

        /// <summary>
        /// 对齐 Lyricify：仅当系统媒体会话属于音乐时才显示歌词。
        /// 播放视频、图片等会话时按"无媒体"处理，歌词岛整体隐藏；
        /// 关闭后恢复为任意媒体会话都显示歌词的旧行为。
        /// </summary>
        public bool OnlyMusicLyrics
        {
            get => _onlyMusicLyrics;
            set
            {
                if (_onlyMusicLyrics == value) return;
                _onlyMusicLyrics = value;
                // 切换后立即重判当前会话：视频正在播放时打开开关要马上收起歌词岛。
                if (_timer.IsEnabled) QueueRefresh();
            }
        }
        public bool SourceQQEnabled { get => (_lyricsSources & 1) != 0; set => SetSource(1, value); }
        public bool SourceNeteaseEnabled { get => (_lyricsSources & 2) != 0; set => SetSource(2, value); }
        public bool SourceKugouEnabled { get => (_lyricsSources & 4) != 0; set => SetSource(4, value); }
        public bool SourceLrclibEnabled { get => (_lyricsSources & 8) != 0; set => SetSource(8, value); }

        public void ConfigureLyricsSources(bool qq, bool netease, bool kugou, bool lrclib)
        {
            var sources = (qq ? 1 : 0) | (netease ? 2 : 0) | (kugou ? 4 : 0) | (lrclib ? 8 : 0);
            if (Interlocked.Exchange(ref _lyricsSources, sources) != sources) ReloadLyrics();
        }

        private void SetSource(int flag, bool enabled)
        {
            var sources = enabled ? _lyricsSources | flag : _lyricsSources & ~flag;
            if (Interlocked.Exchange(ref _lyricsSources, sources) != sources) ReloadLyrics();
        }

        public void RetryLyrics() => ReloadLyrics(clearCache: true);

        private void ReloadLyrics(bool clearCache = false)
        {
            if (_disposed) return;
            lock (_lyricsRequestSync)
            {
                _lyricsRequestCts?.Cancel();
                _lyricsRequestCts?.Dispose();
                _lyricsRequestCts = null;
                _lyricsLookupVersion++;
                _nextLyricsRetry = DateTimeOffset.MinValue;
                _lyrics = Array.Empty<SyncedLyricLine>();
                _lyricsSongDurationMs = 0;
                if (clearCache) _lyricsCache.Clear();
            }
            if (_timer.IsEnabled) QueueRefresh();
        }
        public int OffsetMs { get; set; } = 0;
        public bool SplitLongLines { get; set; } = false;
        public int PollingIntervalMs
        {
            get => (int)_timer.Interval.TotalMilliseconds;
            set => _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(value, 60, 1000));
        }

        // 无媒体时不再下发占位文案：任务栏歌词岛只呈现真实内容，没有内容就是空的。
        public TaskbarMediaSnapshot Current { get; private set; } = new(false, false, string.Empty, string.Empty, "系统媒体");
        public event EventHandler<TaskbarMediaSnapshot>? StateChanged;

        public TaskbarMediaService() : this(async cancellationToken =>
            await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken))
        {
        }

        internal TaskbarMediaService(Func<CancellationToken, Task<GlobalSystemMediaTransportControlsSessionManager?>> managerFactory)
        {
            _managerFactory = managerFactory ?? throw new ArgumentNullException(nameof(managerFactory));
            // Synchronous tick + background refresh via SemaphoreSlim. The
            // previous `async void Tick` could re-enter and produced
            // unobserved exceptions that crashed or stalled the dispatcher.
            _timer.Tick += (_, _) => QueueRefresh();
        }

        public Task StartAsync()
        {
            lock (_startSync)
            {
                if (_disposed || _timer.IsEnabled) return Task.CompletedTask;
                if (_startTask is { IsCompleted: false }) return _startTask;
                var lease = _runLifetime.Begin();
                if (lease == null) return Task.CompletedTask;
                _activeRun = lease;
                return _startTask = StartCoreAsync(lease);
            }
        }

        private async Task StartCoreAsync(AsyncRunLifetime.Lease lease)
        {
            try
            {
                var manager = _manager ?? await _managerFactory(lease.Token);
                if (manager == null)
                {
                    if (_runLifetime.IsCurrent(lease))
                        Publish(new(false, false, "媒体控制不可用", "未获取到系统媒体管理器，可稍后重试", "系统媒体"));
                    return;
                }
                if (!_runLifetime.TryActivate(lease, () =>
                {
                    _manager = manager;
                    AttachManagerEvents();
                    _timer.Start();
                })) return;
                if (!_runLifetime.IsCurrent(lease)) return;
                if (_refreshGate.Wait(0))
                {
                    try { await RefreshAsync(); }
                    finally { try { _refreshGate.Release(); } catch (ObjectDisposedException) { } }
                }
            }
            catch (OperationCanceledException) when (!_runLifetime.IsCurrent(lease)) { }
            catch (ObjectDisposedException) when (!_runLifetime.IsCurrent(lease)) { }
            catch (Exception ex)
            {
                if (!_runLifetime.IsCurrent(lease)) return;
                AppLogger.Warn($"Unable to initialize system media controls: {ex.Message}");
                Publish(new(false, false, "媒体控制不可用", "请确认 Windows 媒体会话权限", "系统媒体"));
            }
        }

        public void Stop()
        {
            lock (_startSync)
            {
                _startTask = null;
                _activeRun = null;
                _runLifetime.Stop(() =>
                {
                    _timer.Stop();
                    ReloadLyrics();
                    DetachManagerEvents();
                    _session = null;
                    _trackKey = string.Empty;
                    _displayContinuity.MarkHealthy();
                    Interlocked.Increment(ref _trackVersion);
                    _lyrics = Array.Empty<SyncedLyricLine>();
                    _lyricsSongDurationMs = 0;
                    _artwork = null;
                    _onlineArtwork = null;
                    // 负缓存必须一并清空：它按 trackKey 单调增长，
                    // 长会话下每首取不到封面的歌都会留下一条。
                    _artworkMisses.Clear();
                    // 若上一次刷新卡在 WinRT 调用上，_busy 可能一直是 true；
                    // 不复位的话服务重启后也永远不会再刷新。
                    _busy = false;
                });
            }
        }

        private void AttachManagerEvents()
        {
            if (_manager == null || _managerEventsAttached)
                return;

            _managerEventsAttached = true;
            try
            {
                _manager.SessionsChanged += Manager_SessionsChanged;
                _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
            }
            catch
            {
                // Roll back a partial COM event subscription as well.
                DetachManagerEvents();
                throw;
            }
        }

        private void DetachManagerEvents()
        {
            if (_manager == null || !_managerEventsAttached)
                return;

            var manager = _manager;
            _managerEventsAttached = false;
            ExitCleanup.Run(
                ("解绑媒体会话列表事件", () => manager.SessionsChanged -= Manager_SessionsChanged),
                ("解绑当前媒体会话事件", () => manager.CurrentSessionChanged -= Manager_CurrentSessionChanged));
        }

        private void Manager_SessionsChanged(
            GlobalSystemMediaTransportControlsSessionManager sender,
            SessionsChangedEventArgs args) => QueueRefresh();

        private void Manager_CurrentSessionChanged(
            GlobalSystemMediaTransportControlsSessionManager sender,
            CurrentSessionChangedEventArgs args) => QueueRefresh();

        private void QueueRefresh()
        {
            if (_disposed || _activeRun is not { } run || !_runLifetime.IsCurrent(run))
                return;

            // Non-blocking reentrancy guard: drop the tick if a refresh is
            // already in flight instead of stacking async void continuations.
            // Wait(0) can throw ObjectDisposedException when Dispose races
            // with a dispatcher tick; treat that as "skip this refresh".
            try
            {
                if (!_refreshGate.Wait(0))
                    return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshAsync();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Media refresh failed: {ex.Message}");
                }
                finally
                {
                    try { _refreshGate.Release(); } catch (ObjectDisposedException) { }
                }
            });
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

        /// <summary>
        /// 判定 SMTC 播放类型是否按音乐处理。音乐与未上报（Unknown/null）都算音乐——
        /// 相当多的播放器根本不上报 PlaybackType，一刀切只认 Music 会误伤它们；
        /// 视频与图片明确排除：浏览器和本地播放器放视频时不再出歌词（对齐 Lyricify）。
        /// 注意属性投影类型是 Windows.Media.MediaPlaybackType，不在 Windows.Media.Control 命名空间。
        /// </summary>
        internal static bool IsMusicPlaybackType(Windows.Media.MediaPlaybackType? playbackType) =>
            playbackType is null
                or Windows.Media.MediaPlaybackType.Music
                or Windows.Media.MediaPlaybackType.Unknown;

        private bool IsMusicSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (session is null) return false;
            if (!_onlyMusicLyrics) return true;
            try
            {
                return IsMusicPlaybackType(session.GetPlaybackInfo()?.PlaybackType);
            }
            catch
            {
                // 读取失败时按音乐处理：宁可多显示，不要因为一次 Win32 抖动把音乐也藏掉。
                return true;
            }
        }

        private static bool IsPlaying(GlobalSystemMediaTransportControlsSession? session)
        {
            try
            {
                return session?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                return false;
            }
        }

        private bool HasAnySession()
        {
            if (_manager == null) return false;
            try
            {
                return _manager.GetSessions() is { Count: > 0 };
            }
            catch
            {
                return false;
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
                    // Keep the selected playing session when providers reorder
                    // their list; notification sounds must not alternate players.
                    var selectedId = _session?.SourceAppUserModelId;
                    foreach (var candidate in sessions)
                    {
                        if (candidate.SourceAppUserModelId != selectedId) continue;
                        if (IsPlaying(candidate) && IsMusicSession(candidate))
                            return candidate;
                    }
                    // Otherwise prefer the OS current playing session.
                    var currentPlaying = _manager.GetCurrentSession();
                    if (IsPlaying(currentPlaying) && IsMusicSession(currentPlaying))
                        return currentPlaying;
                    // 1. 优先获取处于 Playing 状态的会话
                    foreach (var s in sessions)
                    {
                        if (IsPlaying(s) && IsMusicSession(s))
                            return s;
                    }

                    // 2. 获取具有活动媒体属性的会话
                    var current = _manager.GetCurrentSession();
                    if (current != null && IsMusicSession(current)) return current;

                    // 3. 回退：过滤开启时取最后一个音乐会话（暂停中的音乐也要能显示），
                    //    全部被过滤掉则返回 null，由 RefreshAsync 按"无媒体"收起歌词岛。
                    if (_onlyMusicLyrics)
                    {
                        for (var i = sessions.Count - 1; i >= 0; i--)
                        {
                            if (IsMusicSession(sessions[i])) return sessions[i];
                        }
                        return null;
                    }
                    return sessions.LastOrDefault();
                }
                var sole = _manager.GetCurrentSession();
                return IsMusicSession(sole) ? sole : null;
            }
            catch
            {
                var last = _manager?.GetCurrentSession();
                return IsMusicSession(last) ? last : null;
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
            // Process.GetProcessesByName is expensive; without an SMTC session
            // this ran on every poll. Cap it to once per second and reuse the
            // last result so the UI does not flap to "no media" mid-throttle.
            var now = Environment.TickCount64;
            if (now - Volatile.Read(ref _lastWindowSniffTick) < WindowSniffMinIntervalMs)
                return _lastWindowSniff;
            Volatile.Write(ref _lastWindowSniffTick, now);

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
                                {
                                    _lastWindowSniff = (cleanT, cleanA, appName);
                                    return _lastWindowSniff;
                                }
                            }
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
            }
            catch { }
            _lastWindowSniff = null;
            return null;
        }

        /// <summary>等待任务完成，超时返回 default，避免 WinRT/COM 调用挂起拖死整个刷新循环。</summary>
        private static async Task<T?> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout) where T : class
        {
            using var delayCts = new CancellationTokenSource();
            var delay = Task.Delay(timeout, delayCts.Token);
            var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
            if (completed != task)
                return null;

            // 取消延时计时器：否则每次调用都会留下一个要等到超时的定时器，
            // WinRT 频繁挂起时会持续累积。
            delayCts.Cancel();
            return await task.ConfigureAwait(false);
        }

        private async Task RefreshAsync()
        {
            if (_busy || _disposed)
                return;

            _busy = true;
            try
            {
                // Manager acquisition belongs to StartCoreAsync. Reacquiring it
                // here could revive a stopped service after a late continuation.
                if (_activeRun is not { } run || !_runLifetime.IsCurrent(run) || _manager == null) return;
                var session = GetActiveSession();
                if (session == null)
                {
                    if (_displayContinuity.HoldLastMedia(Current.HasSession)) return;
                    // 过滤开启时要分清"系统里没有任何会话"和"有会话但都被音乐过滤挡掉"：
                    // 后者不能走窗口标题嗅探——嗅探名单含 PotPlayer 等全能播放器，
                    // 会把正在放视频的它们误当成音乐源，过滤就白做了。
                    bool anySystemSession = HasAnySession();
                    (string Title, string Artist, string AppName)? windowFallback = null;
                    if (!anySystemSession)
                    {
                        // 若无 WinRT SMTC 会话，尝试通过窗口标题嗅探器（Lyricify 兼容模式）
                        windowFallback = TryGetWindowMediaInfo();
                    }
                    if (windowFallback.HasValue)
                    {
                        var (wTitle, wArtist, wApp) = windowFallback.Value;
                        var fallbackKey = $"{wTitle}\u001f{wArtist}\u001fwin_fallback";
                        if (!string.Equals(fallbackKey, _trackKey, StringComparison.Ordinal))
                        {
                            _trackKey = fallbackKey;
                            ReloadLyrics();
                            var fallbackTrackVersion = Interlocked.Increment(ref _trackVersion);
                            _title = wTitle;
                            _artist = wArtist;
                            _lyrics = Array.Empty<SyncedLyricLine>();
                            _lyricsSongDurationMs = 0;
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
                                    fallbackTrackVersion, run));
                            }

                        }
                        _session = null;
                        Publish(CreateTitleOnlySnapshot(wTitle, wArtist, wApp, _onlineArtwork,
                            fallbackKey, Volatile.Read(ref _trackVersion)));
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
                    // 没有可显示的媒体就保持空白，不再下发"等待音乐播放/等待媒体播放"
                    // 之类的占位提示：那句文案既占宽度又容易被当成程序出错。
                    Publish(new(
                        false,
                        false,
                        string.Empty,
                        string.Empty,
                        "系统媒体",
                        TrackKey: string.Empty,
                        TrackVersion: Volatile.Read(ref _trackVersion)));
                    return;
                }

                _session = session;
                // 播放器进程僵死时这个 WinRT 调用可能永不返回，进而让 _busy
                // 永久卡在 true（finally 不执行），歌词岛从此不再刷新。加超时。
                var media = await WithTimeoutAsync(
                    session.TryGetMediaPropertiesAsync().AsTask(),
                    TimeSpan.FromSeconds(3));
                if (media == null || !_runLifetime.IsCurrent(run)) return;
                var timeline = session.GetTimelineProperties();
                var playback = session.GetPlaybackInfo();
                var title = media.Title?.Trim() ?? string.Empty;
                var artist = media.Artist?.Trim() ?? string.Empty;
                var album = media.AlbumTitle?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(title) && _displayContinuity.HoldLastMedia(Current.HasSession)) return;
                // WinRT 侧这两个属性可能为 null（会话刚建立/播放器已退出），
                // 直接解引用会在每次轮询里抛 NRE，歌词岛会一直显示"无法读取媒体"。
                var duration = timeline?.EndTime > TimeSpan.Zero ? timeline.EndTime : TimeSpan.Zero;
                var sessionId = session.SourceAppUserModelId?.Trim() ?? string.Empty;
                var trackKey = MediaTrackIdentity.Resolve(_trackKey, sessionId, title, artist, _artist);
                if (trackKey == _trackKey)
                {
                    if (string.IsNullOrWhiteSpace(artist)) artist = _artist;
                    if (string.IsNullOrWhiteSpace(album)) album = _album;
                }
                _displayContinuity.MarkHealthy();

                if (!string.Equals(trackKey, _trackKey, StringComparison.Ordinal))
                {
                    _previousTrackArtwork = _artwork ?? _onlineArtwork;
                    _previousTrackAlbum = _album;
                    _trackKey = trackKey;
                    ReloadLyrics();
                    _title = title;
                    _artist = artist;
                    _album = album;
                    _lyrics = Array.Empty<SyncedLyricLine>();
                    _lyricsSongDurationMs = 0;
                    _onlineArtwork = null;
                    _artwork = null;
                    _lastTrackChangeAt = DateTimeOffset.Now;
                    var trackVersion = Interlocked.Increment(ref _trackVersion);

                    if (_artworkCache.TryGetValue(trackKey, out var cachedArt) && cachedArt is { Length: > 0 })
                    {
                        // 命中缓存，直接使用
                        _runLifetime.TryActivate(run, () =>
                        {
                            if (IsCurrentTrack(trackKey, trackVersion)) _artwork = cachedArt;
                        });
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
                            await EnsureArtworkAsync(capturedKey, capturedTitle, capturedArtist, capturedAlbum, null, trackVersion, run);
                        });
                    }

                }
                else if (_artwork == null
                    && (DateTimeOffset.Now - _lastTrackChangeAt).TotalMilliseconds >= 180)
                {
                    // Wait for metadata and the player-owned thumbnail to advance together before
                    // accepting SMTC artwork. This path intentionally remains active while an
                    // online cover is available, because the player-owned thumbnail is authoritative.
                    _ = EnsureArtworkAsync(trackKey, title, artist, album, media.Thumbnail, Volatile.Read(ref _trackVersion), run);
                }

                _title = title;
                _artist = artist;
                _album = album;

                EnsureLyricsLookup(trackKey, title, artist, album, duration, Volatile.Read(ref _trackVersion));
                var isPlaying = playback != null &&
                    playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                bool smtcTimelineValid = timeline != null &&
                    timeline.EndTime > TimeSpan.Zero && timeline.LastUpdatedTime.Year > 2000;
                TimeSpan position;

                if (smtcTimelineValid && timeline != null)
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

                    // 单曲循环/列表循环重播：这类播放器重播时不会把 Position 归零，
                    // 内部时钟越过曲库时长后对歌长取模归位，否则歌词永远停在最后一句。
                    // 归位必须节流（半首歌长的闸门）：播放器把时长报小时，不节流会每轮
                    // 归位一次、歌词永远卡在第一句；真正的重播走完整首才会再次触发。
                    var lyricsSongDurationMs = Volatile.Read(ref _lyricsSongDurationMs);
                    if (lyricsSongDurationMs > 0 &&
                        position.TotalMilliseconds > lyricsSongDurationMs + 5000)
                    {
                        var replayGuard = TimeSpan.FromSeconds(Math.Max(20, lyricsSongDurationMs / 2000.0));
                        if (DateTimeOffset.Now - _lastReplayResetAt > replayGuard)
                        {
                            _lastReplayResetAt = DateTimeOffset.Now;
                            // 归位到「已经进入这一遍多少」，而不是一律归零：
                            // 判定天生滞后（得等时钟越过末尾才知道），归零会把这段
                            // 滞后量摊到整首歌上；对歌长取模可覆盖循环过好几遍的情况。
                            var overMs = position.TotalMilliseconds % lyricsSongDurationMs;
                            _playbackBasePosition = TimeSpan.FromMilliseconds(overMs);
                            _playbackStopwatch.Restart();
                            if (!isPlaying) _playbackStopwatch.Stop();
                            position = TimeSpan.FromMilliseconds(overMs);
                        }
                    }
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
                var lyrics = _lyrics;
                var lyricLine = _lyricSelector.Select(lyrics, adjustedPosition);
                var lyric = lyricLine?.Text;
                var currentTranslation = lyricLine?.Translation ?? string.Empty;
                if (SplitLongLines && !string.IsNullOrWhiteSpace(lyric) && string.IsNullOrWhiteSpace(currentTranslation))
                {
                    var split = SplitLongLyric(lyric);
                    lyric = split.Primary;
                    currentTranslation = split.Secondary;
                }
                // A paused track must not advertise a line as "即将播放".
                // Media sessions can keep their last timeline position while
                // paused, so calculating a preview here leaves stale text on
                // the taskbar until the next lyric boundary.
                var nextLyric = isPlaying
                    ? SyncedLyricsParser.FindNextLine(lyrics, adjustedPosition)
                    : null;
                // 前奏与间奏不再另行提示：这段时间主行回到歌名，由副行承载
                // 歌手/专辑，避免倒计时数字逐秒刷新带来的视觉噪音。
                var hasSyncedLyrics = lyrics.Count > 0;
                // 网易云音乐等播放器不上报 SMTC 时间轴（EndTime 恒为 0）。此时改用歌词源
                // 登记到的曲库时长兜底，否则进度条与进度文案在这类播放器上永远停在开头。
                var libraryDurationMs = Volatile.Read(ref _lyricsSongDurationMs);
                var effectiveEndTime = timeline?.EndTime > TimeSpan.Zero
                    ? timeline.EndTime
                    : libraryDurationMs > 0 ? TimeSpan.FromMilliseconds(libraryDurationMs) : TimeSpan.Zero;
                var timeString = effectiveEndTime > TimeSpan.Zero
                    ? $"{position:mm\\:ss} / {effectiveEndTime:mm\\:ss}"
                    : (position > TimeSpan.Zero ? $"{position:mm\\:ss}" : string.Empty);
                var progress = effectiveEndTime > TimeSpan.Zero
                    ? Math.Clamp(position.TotalMilliseconds / effectiveEndTime.TotalMilliseconds, 0, 1)
                    : 0;
                Publish(new(
                    true,
                    isPlaying,
                    title,
                    artist,
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
                    Volatile.Read(ref _trackVersion), LayoutLyrics: lyrics));
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Media state refresh failed: {ex.Message}");
                if (!_displayContinuity.HoldLastMedia(Current.HasSession))
                    Publish(new(false, false, "暂时无法读取媒体", "播放器可能尚未公开媒体会话", "系统媒体", TrackVersion: Volatile.Read(ref _trackVersion)));
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>Performs a short connectivity probe against an enabled lyric source.</summary>
        public static async Task<bool> CheckLyricsConnectivityAsync(CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://music.163.com");
            using var response = await LyricsClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)response.StatusCode < 500;
        }

        private static (string Primary, string Secondary) SplitLongLyric(string text)
        {
            text = text.Trim();
            var threshold = text.Any(ch => ch >= '\u2E80') ? 18 : 42;
            if (text.Length <= threshold) return (text, string.Empty);
            var midpoint = text.Length / 2;
            var splitAt = -1;
            for (var i = Math.Max(1, midpoint - 10); i < Math.Min(text.Length - 1, midpoint + 10); i++)
            {
                if (" ，。！？；：、,.!?;: ".Contains(text[i])) { splitAt = i + 1; break; }
            }
            if (splitAt <= 0) splitAt = midpoint;
            return (text[..splitAt].Trim(), text[splitAt..].Trim());
        }

        private static async Task<byte[]?> LoadArtworkAsync(IRandomAccessStreamReference? reference, CancellationToken cancellationToken)
        {
            if (reference == null) return null;
            try
            {
                using var stream = await reference.OpenReadAsync().AsTask(cancellationToken);
                if (stream.Size is 0 or > 5 * 1024 * 1024) return null;
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = new byte[checked((int)stream.Size)];
                reader.ReadBytes(bytes);
                return bytes;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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
            long trackVersion,
            AsyncRunLifetime.Lease run)
        {
            if (!_runLifetime.IsCurrent(run) || !IsCurrentTrack(key, trackVersion)) return;
            try
            {
                await EnsureArtworkCoreAsync(key, title, artist, album, thumbnailRef, trackVersion, run);
            }
            catch (OperationCanceledException) when (!_runLifetime.IsCurrent(run)) { }
            catch (Exception ex) { AppLogger.Debug($"Artwork refresh failed: {ex.Message}"); }
        }

        private async Task EnsureArtworkCoreAsync(string key, string title, string artist, string album,
            IRandomAccessStreamReference? thumbnailRef, long trackVersion, AsyncRunLifetime.Lease run)
        {
            run.Token.ThrowIfCancellationRequested();
            // Keep SMTC reads independent from network lookups. Previously both paths shared one
            // pending flag, so a slow online request prevented the local player thumbnail from
            // ever being sampled during the important post-track-change window.
            if (thumbnailRef != null && IsCurrentTrack(key, trackVersion) && _artwork == null)
            {
                if (!_pendingSmtcArtwork.TryAdd(key, 0))
                    return;

                try
                {
                    var smtcArt = await LoadArtworkAsync(thumbnailRef, run.Token);
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

                        _runLifetime.TryActivate(run, () =>
                        {
                            if (!IsCurrentTrack(key, trackVersion)) return;
                            CacheArtwork(key, smtcArt);
                            _artwork = smtcArt;
                        });
                        return;
                    }
                }
                finally
                {
                    _pendingSmtcArtwork.TryRemove(key, out _);
                }
            }

            if (!_runLifetime.IsCurrent(run) || !IsCurrentTrack(key, trackVersion)) return;
            if (_artworkCache.TryGetValue(key, out var cachedArt) && cachedArt is { Length: > 0 })
            {
                _runLifetime.TryActivate(run, () =>
                {
                    if (IsCurrentTrack(key, trackVersion) && _artwork == null) _artwork = cachedArt;
                });
                return;
            }

            // Refresh runs every 80 ms; only one cover request may be in flight for a track.
            if (!_pendingOnlineArtwork.TryAdd(key, 0))
                return;

            // 负缓存：取不到封面的歌会在每个轮询周期（默认 250ms）重新发起一次
            // 在线请求，相当于每秒 4 次，很容易触发对方限流。
            if (_artworkMisses.TryGetValue(key, out var missedAt) &&
                DateTimeOffset.UtcNow - missedAt < TimeSpan.FromSeconds(60))
            {
                _pendingOnlineArtwork.TryRemove(key, out _);
                return;
            }

            try
            {
                var (cleanTitle, cleanArtist) = TrackMetadataSanitizer.Sanitize(title, artist);
                var onlineArt = await FetchOnlineArtworkDirectAsync(cleanTitle, cleanArtist, album, run.Token);
                if (onlineArt is { Length: > 0 })
                {
                    _artworkMisses.TryRemove(key, out _);
                    _runLifetime.TryActivate(run, () =>
                    {
                        if (!IsCurrentTrack(key, trackVersion)) return;
                        _onlineArtwork = onlineArt;
                        // Never replace a player-owned thumbnail that completed while the
                        // network request was in flight.
                        if (_artwork == null)
                            CacheArtwork(key, onlineArt);
                    });
                    return;
                }

                _artworkMisses[key] = DateTimeOffset.UtcNow;
            }
            finally
            {
                _pendingOnlineArtwork.TryRemove(key, out _);
            }
        }

        private bool IsCurrentTrack(string key, long trackVersion) =>
            !_disposed && string.Equals(_trackKey, key, StringComparison.Ordinal) &&
            Volatile.Read(ref _trackVersion) == trackVersion;

        private enum ArtworkProvider
        {
            Netease,
            QQ,
            ITunes,
            Kugou
        }

        private async Task<byte[]?> FetchOnlineArtworkDirectAsync(string title, string artist, string album, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;

            return await ArtworkLookupRace.FirstMatchAsync(Enum.GetValues<ArtworkProvider>()
                .Select(provider => new Func<CancellationToken, Task<byte[]?>>(
                    token => FetchOnlineArtworkProviderAsync(provider, title, artist, album, token))), cancellationToken);
        }

        private async Task<byte[]?> FetchOnlineArtworkProviderAsync(
            ArtworkProvider provider,
            string title,
            string artist,
            string album,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                using var searchResp = await LyricsClient.SendAsync(searchReq, cancellationToken);
                if (searchResp.IsSuccessStatusCode)
                {
                    await using var searchStream = await searchResp.Content.ReadAsStreamAsync(cancellationToken);
                    using var searchDoc = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken);
                    if (searchDoc.RootElement.TryGetProperty("result", out var result) &&
                        result.TryGetProperty("songs", out var songs) &&
                        songs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var song in songs.EnumerateArray())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
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
                                var bytes = await LyricsClient.GetByteArrayAsync(fullUrl, cancellationToken);
                                if (bytes is { Length: > 100 }) return bytes;
                            }
                        }
                    }
                }
              }
              catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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
                using var resp = await LyricsClient.SendAsync(req, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("song", out var songObj) &&
                        songObj.TryGetProperty("list", out var list) &&
                        list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var song in list.EnumerateArray())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
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
                                    var bytes = await LyricsClient.GetByteArrayAsync($"https://y.gtimg.cn/music/photo_new/T002R150x150M000{albMid}.jpg", cancellationToken);
                                    if (bytes is { Length: > 100 }) return bytes;
                                }
                            }
                        }
                    }
                }
              }
              catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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
                using var resp = await LyricsClient.SendAsync(req, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    if (doc.RootElement.TryGetProperty("results", out var results) &&
                        results.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in results.EnumerateArray())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
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
                                    var bytes = await LyricsClient.GetByteArrayAsync(highResUrl, cancellationToken);
                                    if (bytes is { Length: > 100 }) return bytes;
                                }
                            }
                        }
                    }
                }
              }
              catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
              catch { }
              return null;
            }

            // 4. 酷狗音乐封面
            if (provider == ArtworkProvider.Kugou)
            {
              try
              {
                var query = BuildArtworkQuery(title, artist, album);
                // Upstream limitation: mobilecdn.kugou.com does not answer on
                // HTTPS, so this search endpoint stays plaintext. It only
                // carries a song title query, never the download credentials
                // (those use krcs.kugou.com, which is HTTPS).
                var searchUrl = $"http://mobilecdn.kugou.com/api/v3/search/song?keyword={Uri.EscapeDataString(query)}&page=1&pagesize=3";
                using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                using var resp = await LyricsClient.SendAsync(req, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("info", out var list) &&
                        list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var song in list.EnumerateArray())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
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
                                    var bytes = await LyricsClient.GetByteArrayAsync(coverUrl, cancellationToken);
                                    if (bytes is { Length: > 100 }) return bytes;
                                }
                            }
                        }
                    }
                }
              }
              catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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

        internal static TaskbarMediaSnapshot CreateTitleOnlySnapshot(string title, string artist, string app,
            byte[]? artwork, string key, long version) => new(
                true, false, title, string.IsNullOrWhiteSpace(artist) ? app : artist,
                "窗口标题识别 · 无播放进度或控制", artwork, Title: title, Artist: artist,
                TrackKey: key, TrackVersion: version, CanControlPlayback: false);

        private void EnsureLyricsLookup(string key, string title, string artist, string album, TimeSpan duration, long trackVersion)
        {
            lock (_lyricsRequestSync)
            {
                if (_disposed || !OnlineLyricsEnabled || _lyricsSources == 0 || string.IsNullOrWhiteSpace(title) ||
                    _lyrics.Count > 0 || _lyricsRequestCts != null || DateTimeOffset.UtcNow < _nextLyricsRetry) return;
                var sources = _lyricsSources;
                var generation = ++_lyricsLookupVersion;
                var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                request.CancelAfter(TimeSpan.FromSeconds(20));
                _lyricsRequestCts = request;
                var token = request.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await LoadLyricsAsync(key, title, artist, album, duration, sources, token).ConfigureAwait(false);
                        lock (_lyricsRequestSync)
                        {
                            if (generation != _lyricsLookupVersion || !IsCurrentTrack(key, trackVersion) || !OnlineLyricsEnabled) return;
                            _lyrics = result.Lines;
                            Volatile.Write(ref _lyricsSongDurationMs, result.SongDurationMs);
                            _nextLyricsRetry = result.Lines.Count == 0
                                ? DateTimeOffset.UtcNow + LyricsLookupCache.EmptyLifetime : DateTimeOffset.MaxValue;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        lock (_lyricsRequestSync)
                        {
                            if (generation == _lyricsLookupVersion && IsCurrentTrack(key, trackVersion))
                                _nextLyricsRetry = DateTimeOffset.UtcNow + LyricsLookupCache.EmptyLifetime;
                        }
                    }
                    catch (Exception ex) { AppLogger.Warn($"Lyrics lookup failed: {ex.Message}"); }
                    finally
                    {
                        lock (_lyricsRequestSync)
                        {
                            if (ReferenceEquals(_lyricsRequestCts, request)) _lyricsRequestCts = null;
                            request.Dispose();
                        }
                    }
                });
            }
        }

        private async Task<LyricsFetchResult> LoadLyricsAsync(string key, string title, string artist, string album, TimeSpan duration,
            int sources, CancellationToken cancellationToken)
        {
            var cacheKey = new LyricsCacheKey(key, sources, (long)Math.Round(duration.TotalSeconds));
            cancellationToken.ThrowIfCancellationRequested();
            if (_lyricsCache.TryGetValue(cacheKey, out var cached, out var cachedDuration))
                return new LyricsFetchResult(cached, cachedDuration);

            var (cleanTitle, cleanArtist) = TrackMetadataSanitizer.Sanitize(title, artist);
            LyricsFetchResult neteaseLyrics = LyricsFetchResult.Empty;
            LyricsFetchResult qqLyrics = LyricsFetchResult.Empty;

            try
            {
                // 1. 优先尝试 网易云音乐源（海量全曲库、极高双语翻译覆盖率）
                if ((sources & 2) != 0)
                {
                    neteaseLyrics = await FetchNeteaseLyricsAsync(cleanTitle, cleanArtist, duration, cancellationToken);
                    if (neteaseLyrics.Lines.Count > 0)
                    {
                        // 若含有翻译，立即优先采纳
                        if (neteaseLyrics.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Translation)))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            _lyricsCache.Store(cacheKey, neteaseLyrics.Lines, neteaseLyrics.SongDurationMs);
                            return neteaseLyrics;
                        }
                    }
                }

                // 2. 尝试 QQ 音乐源（极速响应、正版曲库）
                if ((sources & 1) != 0)
                {
                    qqLyrics = await FetchQQMusicLyricsAsync(cleanTitle, cleanArtist, duration, cancellationToken);
                    if (qqLyrics.Lines.Count > 0)
                    {
                        if (qqLyrics.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Translation)))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            _lyricsCache.Store(cacheKey, qqLyrics.Lines, qqLyrics.SongDurationMs);
                            return qqLyrics;
                        }
                    }
                }

                // 3. 若无双语翻译，按质量依次采纳单语歌词 (网易云 -> QQ音乐 -> 酷狗 -> LRCLIB)
                if ((sources & 2) != 0 && neteaseLyrics.Lines.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _lyricsCache.Store(cacheKey, neteaseLyrics.Lines, neteaseLyrics.SongDurationMs);
                    return neteaseLyrics;
                }

                if ((sources & 1) != 0 && qqLyrics.Lines.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _lyricsCache.Store(cacheKey, qqLyrics.Lines, qqLyrics.SongDurationMs);
                    return qqLyrics;
                }

                // 4. 尝试 酷狗音乐移动端源
                if ((sources & 4) != 0)
                {
                    var kugouLyrics = await FetchKugouLyricsAsync(cleanTitle, cleanArtist, duration, cancellationToken);
                    if (kugouLyrics.Lines.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _lyricsCache.Store(cacheKey, kugouLyrics.Lines, kugouLyrics.SongDurationMs);
                        return kugouLyrics;
                    }
                }

                // 5. 尝试 LRCLIB 国际公共源
                if ((sources & 8) != 0)
                {
                    var lrclibLyrics = await FetchLrclibLyricsAsync(cleanTitle, cleanArtist, album, duration, cancellationToken);
                    if (lrclibLyrics.Lines.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _lyricsCache.Store(cacheKey, lrclibLyrics.Lines, lrclibLyrics.SongDurationMs);
                        return lrclibLyrics;
                    }
                }

                // 6. 若带歌手未查到，尝试仅用纯歌名在已启用的源中进行检索
                if (!string.IsNullOrWhiteSpace(cleanArtist))
                {
                    if ((sources & 2) != 0)
                    {
                        var fallbackNetease = await FetchNeteaseLyricsAsync(cleanTitle, string.Empty, duration, cancellationToken);
                        if (fallbackNetease.Lines.Count > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            _lyricsCache.Store(cacheKey, fallbackNetease.Lines, fallbackNetease.SongDurationMs);
                            return fallbackNetease;
                        }
                    }

                    if ((sources & 1) != 0)
                    {
                        var fallbackQQ = await FetchQQMusicLyricsAsync(cleanTitle, string.Empty, duration, cancellationToken);
                        if (fallbackQQ.Lines.Count > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            _lyricsCache.Store(cacheKey, fallbackQQ.Lines, fallbackQQ.SongDurationMs);
                            return fallbackQQ;
                        }
                    }

                    if ((sources & 4) != 0)
                    {
                        var fallbackKg = await FetchKugouLyricsAsync(cleanTitle, string.Empty, duration, cancellationToken);
                        if (fallbackKg.Lines.Count > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            _lyricsCache.Store(cacheKey, fallbackKg.Lines, fallbackKg.SongDurationMs);
                            return fallbackKg;
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                _lyricsCache.Store(cacheKey, Array.Empty<SyncedLyricLine>(), 0);
                return LyricsFetchResult.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppLogger.Warn($"Multi-source lyrics lookup failed: {ex.Message}");
                cancellationToken.ThrowIfCancellationRequested();
                _lyricsCache.Store(cacheKey, Array.Empty<SyncedLyricLine>(), 0);
                return LyricsFetchResult.Empty;
            }
        }

        private async Task<LyricsFetchResult> FetchNeteaseLyricsAsync(string title, string artist, TimeSpan duration, CancellationToken cancellationToken)
        {
            try
            {
                var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
                var searchUrl = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(query)}&type=1&offset=0&total=true&limit=5";
                using var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                searchReq.Headers.Referrer = new Uri("https://music.163.com");
                searchReq.Headers.TryAddWithoutValidation("Cookie", "os=pc; osver=Microsoft-Windows-10; appver=2.9.7; NMTID=00O;");
                using var searchResp = await LyricsClient.SendAsync(searchReq, cancellationToken);
                if (!searchResp.IsSuccessStatusCode) return LyricsFetchResult.Empty;

                await using var searchStream = await searchResp.Content.ReadAsStreamAsync(cancellationToken);
                using var searchDoc = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken);
                if (!searchDoc.RootElement.TryGetProperty("result", out var result) ||
                    !result.TryGetProperty("songs", out var songs) ||
                    songs.ValueKind != JsonValueKind.Array)
                    return LyricsFetchResult.Empty;

                var durationS = duration.TotalSeconds;
                var scored = new List<(JsonElement Song, long Id, int TitleScore, bool ArtistMatched, double DurationDiff, long DurationMs)>();
                foreach (var song in songs.EnumerateArray())
                {
                    if (!song.TryGetProperty("id", out var idProp) || !idProp.ValueKind.Equals(JsonValueKind.Number)) continue;
                    var name = song.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var titleScore = LyricMatch.TitleScore(name, title);
                    if (titleScore <= 0) continue;
                    long durationMs = 0;
                    if (song.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number)
                        durationMs = durProp.GetInt64();
                    // 时长差太多基本是另一版本/另一首歌（现场版、remix 宁缺毋滥）
                    if (durationS > 0 && durationMs > 0 && Math.Abs(durationMs / 1000.0 - durationS) > 20) continue;
                    scored.Add((song, idProp.GetInt64(), titleScore,
                        LyricMatch.ArtistMatches(GetArtistNames(song, "artists", "ar"), artist),
                        durationS > 0 && durationMs > 0 ? Math.Abs(durationMs / 1000.0 - durationS) : 0,
                        durationMs));
                }

                // 候选池收敛：只要有一个候选的歌手对得上，就只在这些候选里挑。
                // 歌名相同而歌手不符，基本就是同名的另一首歌（翻唱/同名曲）。
                if (scored.Any(entry => entry.ArtistMatched))
                    scored = scored.Where(entry => entry.ArtistMatched).ToList();

                var ordered = scored
                    .OrderByDescending(entry => entry.TitleScore)
                    .ThenByDescending(entry => entry.ArtistMatched)
                    .ThenBy(entry => entry.DurationDiff)
                    .ToList();

                // 候选逐个尝试：同一首歌常有多个版本，有的版本没译文；
                // 优先带译文的版本，歌名满分且译文全覆盖立即收工。
                var best = LyricsFetchResult.Empty;
                var bestTitleScore = 0;
                var bestCoverage = -1.0;
                foreach (var candidate in ordered.Take(3))
                {
                    var lyricUrl = $"https://music.163.com/api/song/lyric?id={candidate.Id}&lv=1&kv=1&tv=1";
                    using var lyricReq = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
                    lyricReq.Headers.Referrer = new Uri("https://music.163.com");
                    lyricReq.Headers.TryAddWithoutValidation("Cookie", "os=pc; osver=Microsoft-Windows-10; appver=2.9.7; NMTID=00O;");
                    using var lyricResp = await LyricsClient.SendAsync(lyricReq, cancellationToken);
                    if (!lyricResp.IsSuccessStatusCode) continue;

                    await using var lyricStream = await lyricResp.Content.ReadAsStreamAsync(cancellationToken);
                    using var lyricDoc = await JsonDocument.ParseAsync(lyricStream, cancellationToken: cancellationToken);
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

                    if (string.IsNullOrWhiteSpace(origStr)) continue;

                    var origLines = SyncedLyricsParser.Parse(origStr);
                    var transLines = !string.IsNullOrWhiteSpace(transStr)
                        ? SyncedLyricsParser.Parse(transStr)
                        : Array.Empty<SyncedLyricLine>();

                    var merged = SyncedLyricsParser.Merge(origLines, transLines);
                    if (merged.Count == 0) continue;

                    var picked = new LyricsFetchResult(merged, candidate.DurationMs);
                    var coverage = SyncedLyricsParser.TranslationCoverage(merged);
                    if (candidate.TitleScore >= LyricMatch.TitleScoreMax && coverage >= 1)
                        return picked;
                    if (candidate.TitleScore > bestTitleScore ||
                        (candidate.TitleScore == bestTitleScore && coverage > bestCoverage))
                    {
                        bestTitleScore = candidate.TitleScore;
                        bestCoverage = coverage;
                        best = picked;
                    }
                }
                return best;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return LyricsFetchResult.Empty; }
        }

        private async Task<LyricsFetchResult> FetchQQMusicLyricsAsync(string title, string artist, TimeSpan duration, CancellationToken cancellationToken)
        {
            try
            {
                var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
                var searchUrl = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={Uri.EscapeDataString(query)}&format=json&p=1&n=5";
                using var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                searchReq.Headers.Referrer = new Uri("https://y.qq.com");
                using var searchResp = await LyricsClient.SendAsync(searchReq, cancellationToken);
                if (!searchResp.IsSuccessStatusCode) return LyricsFetchResult.Empty;

                await using var searchStream = await searchResp.Content.ReadAsStreamAsync(cancellationToken);
                using var searchDoc = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken);
                if (!searchDoc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("song", out var songObj) ||
                    !songObj.TryGetProperty("list", out var list) ||
                    list.ValueKind != JsonValueKind.Array)
                    return LyricsFetchResult.Empty;

                var durationS = duration.TotalSeconds;
                var scored = new List<(JsonElement Song, string SongMid, int TitleScore, bool ArtistMatched, double DurationDiff, long DurationMs)>();
                foreach (var song in list.EnumerateArray())
                {
                    var songmid = song.TryGetProperty("songmid", out var midProp) && midProp.ValueKind == JsonValueKind.String
                        ? midProp.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(songmid)) continue;
                    var name = song.TryGetProperty("songname", out var nameProp) ? nameProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var titleScore = LyricMatch.TitleScore(name, title);
                    if (titleScore <= 0) continue;
                    long durationMs = 0;
                    if (song.TryGetProperty("interval", out var intervalProp) && intervalProp.ValueKind == JsonValueKind.Number)
                        durationMs = intervalProp.GetInt64() * 1000;
                    if (durationS > 0 && durationMs > 0 && Math.Abs(durationMs / 1000.0 - durationS) > 20) continue;
                    scored.Add((song, songmid, titleScore,
                        LyricMatch.ArtistMatches(GetArtistNames(song, "singer", "singers"), artist),
                        durationS > 0 && durationMs > 0 ? Math.Abs(durationMs / 1000.0 - durationS) : 0,
                        durationMs));
                }

                if (scored.Any(entry => entry.ArtistMatched))
                    scored = scored.Where(entry => entry.ArtistMatched).ToList();

                var ordered = scored
                    .OrderByDescending(entry => entry.TitleScore)
                    .ThenByDescending(entry => entry.ArtistMatched)
                    .ThenBy(entry => entry.DurationDiff)
                    .ToList();

                var best = LyricsFetchResult.Empty;
                var bestTitleScore = 0;
                var bestCoverage = -1.0;
                foreach (var candidate in ordered.Take(3))
                {
                    var lyricUrl = $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={Uri.EscapeDataString(candidate.SongMid)}&format=json&nobase64=1";
                    using var req = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
                    req.Headers.Referrer = new Uri("https://y.qq.com");
                    using var lyricResp = await LyricsClient.SendAsync(req, cancellationToken);
                    if (!lyricResp.IsSuccessStatusCode) continue;

                    await using var lyricStream = await lyricResp.Content.ReadAsStreamAsync(cancellationToken);
                    using var lyricDoc = await JsonDocument.ParseAsync(lyricStream, cancellationToken: cancellationToken);
                    var origStr = lyricDoc.RootElement.TryGetProperty("lyric", out var lyricProp) &&
                                  lyricProp.ValueKind == JsonValueKind.String
                        ? WebUtility.HtmlDecode(lyricProp.GetString())
                        : null;

                    var transStr = lyricDoc.RootElement.TryGetProperty("trans", out var transProp) &&
                                   transProp.ValueKind == JsonValueKind.String
                        ? WebUtility.HtmlDecode(transProp.GetString())
                        : null;

                    if (string.IsNullOrWhiteSpace(origStr)) continue;

                    var origLines = SyncedLyricsParser.Parse(origStr);
                    var transLines = !string.IsNullOrWhiteSpace(transStr)
                        ? SyncedLyricsParser.Parse(transStr)
                        : Array.Empty<SyncedLyricLine>();

                    var merged = SyncedLyricsParser.Merge(origLines, transLines);
                    if (merged.Count == 0) continue;

                    var picked = new LyricsFetchResult(merged, candidate.DurationMs);
                    var coverage = SyncedLyricsParser.TranslationCoverage(merged);
                    if (candidate.TitleScore >= LyricMatch.TitleScoreMax && coverage >= 1)
                        return picked;
                    if (candidate.TitleScore > bestTitleScore ||
                        (candidate.TitleScore == bestTitleScore && coverage > bestCoverage))
                    {
                        bestTitleScore = candidate.TitleScore;
                        bestCoverage = coverage;
                        best = picked;
                    }
                }
                return best;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return LyricsFetchResult.Empty; }
        }

        private static async Task<LyricsFetchResult> FetchKugouLyricsAsync(string title, string artist, TimeSpan duration, CancellationToken cancellationToken)
        {
            try
            {
                var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
                // Upstream limitation: mobilecdn.kugou.com does not answer on
                // HTTPS, so this search endpoint stays plaintext. It only
                // carries a song title query, never the download credentials
                // (those use krcs.kugou.com, which is HTTPS).
                var searchUrl = $"http://mobilecdn.kugou.com/api/v3/search/song?keyword={Uri.EscapeDataString(query)}&page=1&pagesize=5";
                using var searchResp = await LyricsClient.GetAsync(searchUrl, cancellationToken);
                if (!searchResp.IsSuccessStatusCode) return LyricsFetchResult.Empty;

                await using var searchStream = await searchResp.Content.ReadAsStreamAsync(cancellationToken);
                using var searchDoc = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken);
                if (!searchDoc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("info", out var infoList) ||
                    infoList.ValueKind != JsonValueKind.Array)
                    return LyricsFetchResult.Empty;

                var durationS = duration.TotalSeconds;
                var scored = new List<(string Hash, int TitleScore, bool ArtistMatched, double DurationDiff, long DurationMs)>();
                foreach (var info in infoList.EnumerateArray())
                {
                    var hash = info.TryGetProperty("hash", out var hashProp) && hashProp.ValueKind == JsonValueKind.String
                        ? hashProp.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(hash)) continue;
                    var name = info.TryGetProperty("songname", out var nameProp) ? nameProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var titleScore = LyricMatch.TitleScore(name, title);
                    if (titleScore <= 0) continue;
                    long durationMs = 0;
                    if (info.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number)
                        durationMs = durProp.GetInt64() * 1000;
                    if (durationS > 0 && durationMs > 0 && Math.Abs(durationMs / 1000.0 - durationS) > 20) continue;
                    var singer = info.TryGetProperty("singername", out var singerProp) ? singerProp.GetString() : null;
                    scored.Add((hash, titleScore, LyricMatch.ArtistMatches(singer, artist),
                        durationS > 0 && durationMs > 0 ? Math.Abs(durationMs / 1000.0 - durationS) : 0,
                        durationMs));
                }

                if (scored.Any(entry => entry.ArtistMatched))
                    scored = scored.Where(entry => entry.ArtistMatched).ToList();

                var ordered = scored
                    .OrderByDescending(entry => entry.TitleScore)
                    .ThenByDescending(entry => entry.ArtistMatched)
                    .ThenBy(entry => entry.DurationDiff)
                    .ToList();

                foreach (var candidate in ordered.Take(3))
                {
                    var lrcSearchUrl = $"https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=&duration=&hash={candidate.Hash}";
                    using var lrcSearchResp = await LyricsClient.GetAsync(lrcSearchUrl, cancellationToken);
                    if (!lrcSearchResp.IsSuccessStatusCode) continue;

                    await using var lrcSearchStream = await lrcSearchResp.Content.ReadAsStreamAsync(cancellationToken);
                    using var lrcSearchDoc = await JsonDocument.ParseAsync(lrcSearchStream, cancellationToken: cancellationToken);
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

                                var downUrl = $"https://krcs.kugou.com/download?ver=1&man=yes&client=mobi&id={id}&accesskey={key}&fmt=lrc&charset=utf8";
                                using var downResp = await LyricsClient.GetAsync(downUrl, cancellationToken);
                                if (downResp.IsSuccessStatusCode)
                                {
                                    await using var downStream = await downResp.Content.ReadAsStreamAsync(cancellationToken);
                                    using var downDoc = await JsonDocument.ParseAsync(downStream, cancellationToken: cancellationToken);
                                    if (downDoc.RootElement.TryGetProperty("content", out var contentProp) &&
                                        contentProp.ValueKind == JsonValueKind.String)
                                    {
                                        var base64 = contentProp.GetString();
                                        if (!string.IsNullOrWhiteSpace(base64))
                                        {
                                            var bytes = Convert.FromBase64String(base64);
                                            // Trim() 不会去掉 BOM，残留的 U+FEFF 会被当作首行歌词内容，
                                            // 让首句偏移并让自适应宽度算多一个字符。
                                            var lrcText = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                                            var parsed = SyncedLyricsParser.Parse(lrcText);
                                            if (parsed.Count > 0)
                                                return new LyricsFetchResult(parsed, candidate.DurationMs);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                return LyricsFetchResult.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return LyricsFetchResult.Empty; }
        }

        private static async Task<LyricsFetchResult> FetchLrclibLyricsAsync(string title, string artist, string album, TimeSpan duration, CancellationToken cancellationToken)
        {
            try
            {
                var url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) +
                          "&artist_name=" + Uri.EscapeDataString(artist) +
                          "&album_name=" + Uri.EscapeDataString(album) +
                          "&duration=" + Math.Max(0, Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                using var response = await LyricsClient.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    var synced = json.RootElement.TryGetProperty("syncedLyrics", out var element) && element.ValueKind == JsonValueKind.String
                        ? element.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(synced))
                    {
                        var parsed = SyncedLyricsParser.Parse(synced);
                        if (parsed.Count > 0) return new LyricsFetchResult(parsed, 0);
                    }
                }

                var searchCandidate = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
                var searchUrl = "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(searchCandidate);
                using var searchResponse = await LyricsClient.GetAsync(searchUrl, cancellationToken);
                if (searchResponse.IsSuccessStatusCode)
                {
                    await using var searchStream = await searchResponse.Content.ReadAsStreamAsync(cancellationToken);
                    using var searchJson = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken);
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
                                    return new LyricsFetchResult(parsed, 0);
                            }
                        }
                    }
                }
                return LyricsFetchResult.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return LyricsFetchResult.Empty; }
        }

        private void CacheArtwork(string key, byte[] artwork)
        {
            while (_artworkCache.Count >= MaxArtworkCacheEntries &&
                   _artworkCache.Keys.FirstOrDefault() is { } evictionKey)
            {
                if (!_artworkCache.TryRemove(evictionKey, out _))
                    break;
            }
            _artworkCache[key] = artwork;
        }

        private void Publish(TaskbarMediaSnapshot snapshot)
        {
            if (_disposed || _activeRun is not { } run || !_runLifetime.IsCurrent(run))
                return;
            // Async lyric/artwork refreshes can finish after a track change or
            // Stop(). Never let an old snapshot overwrite the current media
            // state; doing so causes a visible flash and stale "即将播放"
            // text in the taskbar. TrackVersion=0 is reserved for static
            // service-status snapshots that are not tied to a track.
            var currentVersion = Volatile.Read(ref _trackVersion);
            if (snapshot.TrackVersion > 0 && snapshot.TrackVersion != currentVersion)
                return;
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
            lock (_startSync)
            {
                if (_disposed) return;
                _disposed = true;
                _activeRun = null;
                _startTask = null;
                _runLifetime.Stop(() => ExitCleanup.Run(
                    ("停止媒体轮询", () => _timer.Stop()),
                    ("取消媒体网络请求", () => _lifetimeCts.Cancel()),
                    ("取消歌词请求", () =>
                    {
                        lock (_lyricsRequestSync)
                        {
                            _lyricsRequestCts?.Cancel();
                            _lyricsRequestCts?.Dispose();
                            _lyricsRequestCts = null;
                            _lyricsLookupVersion++;
                        }
                    }),
                    ("解绑系统媒体事件", DetachManagerEvents),
                    ("清空媒体状态和缓存", () =>
                    {
                        _session = null;
                        _manager = null;
                        _lyrics = Array.Empty<SyncedLyricLine>();
                        _lyricsSongDurationMs = 0;
                        _artwork = null;
                        _onlineArtwork = null;
                        _previousTrackArtwork = null;
                        _lyricsCache.Clear();
                        _artworkCache.Clear();
                        _pendingOnlineArtwork.Clear();
                        _pendingSmtcArtwork.Clear();
                        _busy = false;
                        StateChanged = null;
                    }),
                    ("释放媒体取消令牌", () => _lifetimeCts.Dispose()),
                    ("释放媒体刷新锁", () => _refreshGate.Dispose())), dispose: true);
            }
        }
    }
}
