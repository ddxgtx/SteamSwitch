using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamSwitcher;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;
using SteamSwitcher.ViewModels;
using SteamSwitcher.Views;

internal static class Program
{
    private static readonly string[] MainViews = { "accounts", "accounts-recovery", "accounts-search-empty", "accounts-current", "accounts-target", "accounts-busy", "accounts-failed", "accounts-grid", "accounts-empty", "games", "games-empty", "saves", "saves-empty", "desktop-icons", "lyrics", "lyrics-advanced", "quick", "quick-populated", "settings", "configuration" };
    private static readonly string[] OtherViews = { "update-dialog", "update-dialog-progress", "update-dialog-unverified", "workflow-dialog", "custom-save-dialog", "binding-dialog", "settings-dialog", "taskbar-band", "taskbar-band-hover", "taskbar-band-glass", "taskbar-band-glass-hover", "taskbar-band-paused", "taskbar-band-no-media", "desktop-floating", "desktop-floating-empty", "media-flyout", "volume-flyout", "volume-flyout-muted", "context-menu", "context-menu-accounts", "context-menu-windows", "visual-states" };

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--list" or "--help")
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Usage = "UiCapture <output.png> <mode> [width] [height] [Light|Dark] [default|focus|disabled|selected]",
                MainViews, OtherViews,
                Batch = "UiCapture <output-directory> render-all | all-smoke",
                Smoke = "Append -smoke to any visual mode; lyrics-stability-smoke checks 80 media updates and hover/track transitions. Existing exit-lifecycle-smoke, exit-direct-smoke and settings-apply-smoke remain available.",
                MediaRegression = "lyrics-handoff checks metadata-first handoffs, delayed lyrics/covers, grace expiry, hover/rapid-skip/fixed-width timers and idle cleanup; lyrics-track-change checks single-frame track swaps.",
                SettingsRegression = "maintenance-guidance checks grouped actions at the requested window size; settings-protection-guidance checks real write-protection bindings and recovery instructions without touching the user's profile.",
                Isolation = "Every rendering process uses a new profile below the output directory; no Window.Show, native input, Steam launch, registry startup write or network request is used. Focus is a template visual preview, not OS keyboard navigation validation."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        var outputPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath("ui-preview.png");
        var requestedView = args.Length > 1 ? args[1].ToLowerInvariant() : "accounts";
        if (requestedView is "render-all" or "all-smoke")
        {
            RunBatch(outputPath, requestedView == "all-smoke");
            return;
        }
        var specialSmoke = requestedView is "exit-lifecycle-smoke" or "exit-direct-smoke" or "settings-apply-smoke" or "dialog-resize-smoke";
        var smoke = !specialSmoke && requestedView.EndsWith("-smoke", StringComparison.Ordinal);
        var view = smoke ? requestedView[..^6] : requestedView;
        var state = args.Length > 5 ? args[5].ToLowerInvariant() : "default";
        if (state is not ("default" or "focus" or "disabled" or "selected" or "wallpaper"))
            throw new ArgumentException($"Unknown visual state: {state}.");
        Environment.SetEnvironmentVariable("STEAMSWITCH_LOG_DIRECTORY", Path.Combine(Path.GetDirectoryName(outputPath)!, "logs"));
        var width = args.Length > 2 && int.TryParse(args[2], out var parsedWidth) ? parsedWidth : 1180;
        var height = args.Length > 3 && int.TryParse(args[3], out var parsedHeight) ? parsedHeight : 800;

        App.SuppressFatalDialogs = true;
        var app = new App(uiPreview: true);
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var theme = args.Length > 4 ? args[4] : "Light";
        ThemeManager.ApplyTheme(theme);
        using var profile = new IsolatedPreviewProfile(outputPath, theme);
        var previewDirectory = profile.Directory;
        var previewModel = profile.Model;
        SeedPreviewModel(previewModel, profile.AccountManager, profile.IconPath);

        if (view == "lyrics-hover-profile")
        {
            ProfileLyricsHover(profile.AccountManager, profile.IconPath, outputPath, theme);
            return;
        }

        if (view == "lyrics-hover-exit")
        {
            VerifyHoverExit(profile.AccountManager, profile.IconPath, outputPath, theme);
            return;
        }

        if (view == "floating-glass-regression")
        {
            VerifyFloatingClipping(profile.AccountManager, profile.IconPath);
            Console.WriteLine($"Floating clipping passed ({theme}): icon sizes 16-96, padding 2-30, round/square avatars, compact and narrow taskbar viewports.");
            return;
        }

        if (view == "lyrics-stability")
        {
            var band = new TaskbarBandWindow(profile.AccountManager);
            band.SetLyricsSettings(false, true, false, 320, showVisualizer: false, adaptiveWidth: true, hideWhenNoMedia: false);
            band.SetPreviewLyricsEnabled(true);
            SyncedLyricLine[] sheet =
            [
                new(TimeSpan.Zero, "短句", "A short line"),
                new(TimeSpan.FromSeconds(2), "这是一句用于验证整首歌布局稳定的较长歌词", "A longer translation used to measure the song layout once")
            ];
            var artwork = File.ReadAllBytes(profile.IconPath);
            var initial = new TaskbarMediaSnapshot(true, true, "", "", "同步歌词", artwork,
                Title: "测试曲目", Artist: "测试歌手", Album: "测试专辑", HasSyncedLyrics: true,
                CurrentLyric: sheet[0].Text, CurrentTranslation: sheet[0].Translation!,
                TrackKey: "stability-song", TrackVersion: 1, LayoutLyrics: sheet);
            band.SetPreviewMedia(initial);
            var lyric = (TextBlock)band.FindName("LyricText");
            var subtitle = (TextBlock)band.FindName("MediaText");
            var host = (FrameworkElement)band.FindName("LyricsHost");
            var panel = (FrameworkElement)band.FindName("LyricsTextPanel");
            var cover = (Image)band.FindName("AlbumArtwork");
            if (lyric.Text != sheet[0].Text) throw new InvalidOperationException("An artificial title banner replaced valid lyrics.");
            // 切歌首帧刻意不重算宽度（否则文字、封面、进度条、窗口尺寸同一帧全改，
            // 观感就是整个岛闪一下）。用一次同曲更新让宽度就位，基准值才有意义。
            // 注意不能抬 TrackVersion：UpdateMediaUi 会丢弃旧版本的快照。
            band.SetPreviewMedia(initial with { NextText = "下一句预告" });
            var stableWidth = host.Width;
            var stableFont = lyric.FontSize;
            var stableCover = cover.Source;
            if (stableCover == null) throw new InvalidOperationException("The initial cover failed to decode.");
            for (var index = 0; index < 80; index++)
            {
                band.SetPreviewMedia(initial with
                {
                    CurrentLyric = sheet[index % 2].Text, CurrentTranslation = "",
                    NextText = index % 2 == 0 ? "下一句预告" : "", TimeString = $"00:{index % 60:00}",
                    Artwork = index % 3 == 0 ? null : artwork.ToArray(), Progress = index / 100d
                });
                if (host.Width != stableWidth || lyric.FontSize != stableFont)
                    throw new InvalidOperationException("Per-line text or time updates changed band geometry/font size.");
                if (!ReferenceEquals(cover.Source, stableCover))
                    throw new InvalidOperationException("A transient/identical cover caused image clearing or decoding.");
                if (subtitle.Text != "测试歌手 · 测试专辑")
                    throw new InvalidOperationException("A short next-line preview replaced stable metadata.");
            }
            band.SetPreviewControlsVisible(true);
            band.SetPreviewMedia(initial with { CurrentLyric = sheet[1].Text });
            // 悬停不再是"歌词消失、控制条顶替"：歌词要让位并保持可读。
            if (panel.Opacity <= 0.05 || lyric.Text != sheet[1].Text)
                throw new InvalidOperationException("Hovering erased the lyrics instead of leaving them visible beside the controls.");
            band.SetPreviewControlsVisible(false);
            if (panel.Opacity != 1)
                throw new InvalidOperationException("Lyrics did not return to full opacity when the controls left.");

            // Deliver a burst through the production event path: only the latest
            // state should reach the dispatcher, rather than replaying every line.
            var mediaChanged = typeof(TaskbarBandWindow).GetMethod("MediaService_StateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var textProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            int textChanges = 0;
            EventHandler countChanges = (_, _) => textChanges++;
            textProperty.AddValueChanged(lyric, countChanges);
            for (var index = 0; index < 80; index++)
                mediaChanged.Invoke(band, new object?[] { null, initial with { CurrentLyric = $"Queued lyric {index}" } });
            var frame = new System.Windows.Threading.DispatcherFrame();
            app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            textProperty.RemoveValueChanged(lyric, countChanges);
            if (textChanges != 1 || lyric.Text != "Queued lyric 79")
                throw new InvalidOperationException("Queued media events replayed stale lyrics instead of the latest state.");

            band.SetLyricsSettings(false, true, false, 320, showVisualizer: false, adaptiveWidth: false, hideWhenNoMedia: false);
            band.SetPreviewLyricsEnabled(true);
            band.SetPreviewMedia(initial);
            if (host.Width != 320 || lyric.FontSize != stableFont) throw new InvalidOperationException("Fixed-width lyrics changed.");
            const string nextSongLyric = "下一首歌词：这是一段超过固定任务栏宽度的长歌词，应保持字号并在末尾显示省略号";
            band.SetPreviewMedia(initial with { TrackKey = "next-song", TrackVersion = 2, Artwork = null,
                CurrentLyric = nextSongLyric, CurrentTranslation = sheet[1].Translation! });
            if (!ReferenceEquals(cover.Source, stableCover) || lyric.Text != nextSongLyric)
                throw new InvalidOperationException("Track handoff cleared the cover or retained stale lyrics.");
            var stabilitySize = NaturalSize((FrameworkElement)band.Content, 440, 56);
            CaptureVisual((FrameworkElement)band.Content, stabilitySize.Width, stabilitySize.Height, outputPath, requestedView, theme, state, smoke);
            if (lyric.ActualWidth > panel.ActualWidth + 1 || subtitle.ActualWidth > panel.ActualWidth + 1 || panel.ActualWidth <= 0)
                throw new InvalidOperationException("Long lyrics/translation escaped the constrained text area.");
            band.Close();
            band.SetPreviewMedia(initial with { TrackVersion = 3, CurrentLyric = "Must not render after close" });
            if (lyric.Text != nextSongLyric) throw new InvalidOperationException("Closed band accepted a late media update.");
            Console.WriteLine("Lyrics stability passed: 80 updates, 80 coalesced events, constant width/font, stable cover/subtitle, hover isolation and real track/close transitions.");
            return;
        }

        if (view == "lyrics-track-change")
        {
            // 切歌是整块换内容，最容易表现为"整个岛闪一下"：歌词淡入、封面先清成
            // fallback 再换、进度条瞬移、窗口宽度同一帧跳变——四件事叠在一帧里
            // 就是一次明显的闪烁。这里逐项钉住。
            var band = new TaskbarBandWindow(profile.AccountManager);
            band.SetLyricsSettings(true, true, true, 320, showVisualizer: true, adaptiveWidth: true);
            band.SetPreviewLyricsEnabled(true);
            SyncedLyricLine[] firstSheet =
            [
                new(TimeSpan.Zero, "短", "short"),
                new(TimeSpan.FromSeconds(2), "第二句", "second line")
            ];
            // 第二首刻意写得长很多，确保自适应宽度本来就会变化。
            SyncedLyricLine[] secondSheet =
            [
                new(TimeSpan.Zero, "这是一段用来验证切歌时宽度延后更新的较长歌词文本", "A much longer line used to prove the band width changes on the next poll")
            ];
            var artwork = File.ReadAllBytes(profile.IconPath);
            var firstTrack = new TaskbarMediaSnapshot(true, true, firstSheet[0].Text, firstSheet[0].Translation!, "同步歌词", artwork,
                Title: "第一首", Artist: "歌手甲", Album: "专辑甲", HasSyncedLyrics: true,
                TrackKey: "track-a", TrackVersion: 1, LayoutLyrics: firstSheet);
            band.SetPreviewMedia(firstTrack);
            var lyric = (TextBlock)band.FindName("LyricText");
            var subtitle = (TextBlock)band.FindName("MediaText");
            var cover = (Image)band.FindName("AlbumArtwork");
            var host = (FrameworkElement)band.FindName("LyricsHost");
            var fallback = (FrameworkElement)band.FindName("ArtworkFallback");
            var icon = (FrameworkElement)band.FindName("MediaIcon");
            if (lyric.Opacity != 1) throw new InvalidOperationException("The first lyric faded in instead of being shown outright.");
            if (cover.Source == null) throw new InvalidOperationException("The initial cover failed to decode.");
            double widthBefore = host.Width;

            var secondTrack = firstTrack with
            {
                Title = "第二首",
                Artist = "歌手乙",
                Album = "专辑乙",
                CurrentLyric = secondSheet[0].Text,
                CurrentTranslation = secondSheet[0].Translation!,
                TrackKey = "track-b",
                TrackVersion = 2,
                LayoutLyrics = secondSheet,
                Artwork = artwork.ToArray()
            };
            band.SetPreviewMedia(secondTrack);

            if (lyric.Opacity != 1)
                throw new InvalidOperationException("A track change faded the new lyric in — that fade is the visible flicker.");
            if (subtitle.Opacity != 1)
                throw new InvalidOperationException("A track change faded the secondary line in.");
            if (cover.Source == null || fallback.Visibility == Visibility.Visible)
                throw new InvalidOperationException("A track change blanked the cover to the fallback glyph before swapping it.");
            if (host.Width != widthBefore)
                throw new InvalidOperationException("A track change resized the band in the same frame as the content swap.");

            // 下一轮轮询才允许宽度跟上（经合并窗口去抖后一次性应用）。
            // headless 下 DispatcherTimer 不会自行跳动，直接触发提交。
            band.SetPreviewMedia(secondTrack with { TrackVersion = 3 });
            var commitWidth = typeof(TaskbarBandWindow).GetMethod("CommitAdaptiveLyricsWidth", BindingFlags.Instance | BindingFlags.NonPublic)!;
            commitWidth.Invoke(band, null);
            if (host.Width == widthBefore)
                throw new InvalidOperationException("The band width never caught up with the new, longer song.");

            // 无翻译时副行回落到曲目信息并亮起图标；有翻译时图标熄灭。两种状态
            // 共用同一个 14px 占位，副行文字不会因此左右跳一格。
            if (icon.Width != 14) throw new InvalidOperationException("The subtitle icon lost its reserved slot.");
            band.SetPreviewMedia(secondTrack with { TrackVersion = 4, CurrentTranslation = string.Empty });
            if (icon.Opacity != 1 || subtitle.Text != "歌手乙 · 专辑乙")
                throw new InvalidOperationException("The secondary line did not fall back to track metadata with its icon.");
            band.SetPreviewMedia(secondTrack with { TrackVersion = 5, CurrentTranslation = secondSheet[0].Translation! });
            if (icon.Opacity != 0)
                throw new InvalidOperationException("The icon did not dim for the translated line.");

            // 没有媒体会话时歌词岛要收起（"没有歌曲播放时不显示任务栏"），有歌再回来。
            band.SetPreviewMedia(secondTrack with { TrackVersion = 6, HasSession = false });
            if (host.Visibility == Visibility.Visible)
                throw new InvalidOperationException("The band stayed visible with no media session.");
            band.SetPreviewMedia(secondTrack with { TrackVersion = 7 });
            if (host.Visibility != Visibility.Visible)
                throw new InvalidOperationException("The band did not come back when media returned.");

            Console.WriteLine("Track change passed: no fade-in, no cover blanking, width deferred to the next poll, stable subtitle.");
            return;
        }

        if (view == "lyrics-handoff")
        {
            // Reproduce the real sequence: metadata first, then network lyrics/artwork.
            // Collect failures so one run audits all independent handoff regressions.
            var failures = new List<string>();
            void Check(bool ok, string message) { if (!ok) failures.Add(message); }
            void AdvanceAnimation(int milliseconds = 30)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            var band = new TaskbarBandWindow(profile.AccountManager);
            band.SetLyricsSettings(false, true, false, 320, showVisualizer: false,
                adaptiveWidth: true, hideWhenNoMedia: false);
            band.SetPreviewLyricsEnabled(true);
            var lyric = (TextBlock)band.FindName("LyricText");
            var subtitle = (TextBlock)band.FindName("MediaText");
            var cover = (Image)band.FindName("AlbumArtwork");
            var host = (FrameworkElement)band.FindName("LyricsHost");
            var commitWidth = typeof(TaskbarBandWindow).GetMethod("CommitAdaptiveLyricsWidth", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void CommitWidth() => commitWidth.Invoke(band, null);
            void CaptureStage(string suffix)
            {
                var root = (FrameworkElement)band.Content;
                var size = NaturalSize(root, 440, 56);
                CaptureVisual(root, size.Width, size.Height,
                    Path.Combine(Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath) + suffix + ".png"),
                    requestedView, theme, state, false);
            }
            SyncedLyricLine[] sheet = [new(TimeSpan.Zero, "第一句歌词", "First line"), new(TimeSpan.FromSeconds(3), "第二句歌词", "Second line")];
            var first = new TaskbarMediaSnapshot(true, true, "", "", "同步歌词", File.ReadAllBytes(profile.IconPath),
                Title: "旧歌", Artist: "歌手", HasSyncedLyrics: true, CurrentLyric: sheet[0].Text,
                CurrentTranslation: sheet[0].Translation!, TrackKey: "old", TrackVersion: 1, LayoutLyrics: sheet);
            band.SetPreviewMedia(first);
            band.SetPreviewMedia(first);
            CommitWidth();
            var originalCover = cover.Source;
            var metadata = first with { Title = "新歌", TrackKey = "new", TrackVersion = 2,
                CurrentLyric = "", CurrentTranslation = "", HasSyncedLyrics = false, LayoutLyrics = null, Artwork = null };
            band.SetPreviewMedia(metadata);
            Check(ReferenceEquals(originalCover, cover.Source), "Metadata-only handoff cleared the cover before its replacement arrived.");
            Check(lyric.Text == "新歌", "The handoff displayed the previous song's lyric.");
            CaptureStage("-01-metadata");
            var arrived = metadata with { HasSyncedLyrics = true, CurrentLyric = sheet[0].Text,
                CurrentTranslation = sheet[0].Translation!, LayoutLyrics = sheet };
            band.SetPreviewMedia(arrived);
            AdvanceAnimation();
            Check(lyric.Opacity == 1 && subtitle.Opacity == 1, "Asynchronous first lyrics faded from transparent after the metadata frame.");
            CaptureStage("-02-lyrics");
            band.SetPreviewMedia(arrived with { CurrentLyric = sheet[1].Text });
            AdvanceAnimation();
            Check(lyric.Opacity >= .8 && subtitle.Opacity >= .8, "Normal line updates briefly erased the text.");
            band.SetPreviewMedia(arrived with { TrackKey = "same-text", TrackVersion = 3, CurrentLyric = sheet[1].Text });
            Check(lyric.Opacity == 1 && subtitle.Opacity == 1, "Changing tracks with identical text did not cancel the preceding animation.");
            // A failed cover request must eventually show the fallback rather than an
            // unrelated previous cover. Use a real dispatcher turn, not Thread.Sleep.
            AdvanceAnimation(1250);
            band.SetPreviewMedia(arrived with { TrackKey = "same-text", TrackVersion = 3, CurrentLyric = sheet[1].Text });
            Check(cover.Source == null, "Missing artwork retained the previous song's cover past the grace period.");
            band.SetPreviewMedia(arrived with { TrackKey = "same-text", TrackVersion = 3, Artwork = new byte[] { 1, 2, 3 } });
            Check(cover.Source == null, "Malformed artwork bypassed cover validation.");
            band.SetPreviewMedia(arrived with { TrackKey = "same-text", TrackVersion = 3, Artwork = first.Artwork });
            Check(cover.Source != null, "A delayed valid cover did not replace the fallback.");
            band.SetPreviewMedia(first with { TrackVersion = 1 });
            Check(lyric.Text == sheet[0].Text, "An old track version overwrote the latest lyrics.");

            SyncedLyricLine[] longSheet = [new(TimeSpan.Zero, new string('长', 48))];
            var longTrack = arrived with { TrackKey = "long", TrackVersion = 4, CurrentLyric = longSheet[0].Text, LayoutLyrics = longSheet };
            band.SetPreviewMedia(longTrack);
            band.SetPreviewMedia(longTrack);
            var widthBefore = host.Width;
            band.SetPreviewControlsVisible(true);
            CommitWidth();
            Check(host.Width == widthBefore, "A pending width timer moved the band while the pointer was hovering.");
            band.SetPreviewControlsVisible(false);
            band.SetPreviewMedia(longTrack);
            CommitWidth();
            Check(host.Width > widthBefore, "The deferred layout was not applied after hover ended.");

            // Queue a short song's width, then change tracks before the timer fires.
            var shortTrack = arrived with { TrackKey = "short", TrackVersion = 5, LayoutLyrics = sheet };
            band.SetPreviewMedia(shortTrack);
            band.SetPreviewMedia(shortTrack);
            band.SetPreviewMedia(longTrack with { TrackKey = "rapid-next", TrackVersion = 6 });
            widthBefore = host.Width;
            CommitWidth();
            Check(host.Width == widthBefore, "Rapid skipping committed a width calculated for the previous song.");

            band.SetPreviewMedia(longTrack with { TrackKey = "fixed", TrackVersion = 7 });
            band.SetPreviewMedia(longTrack with { TrackKey = "fixed", TrackVersion = 7 });
            band.SetLyricsSettings(false, true, false, 320, showVisualizer: false,
                adaptiveWidth: false, hideWhenNoMedia: false);
            band.SetPreviewLyricsEnabled(true);
            CommitWidth();
            Check(host.Width == 320, "A queued adaptive timer overwrote the user's fixed width.");
            band.SetPreviewMedia(arrived with { TrackVersion = 8, HasSession = false });
            Check(cover.Source == null, "Idle media retained artwork indefinitely.");
            CaptureStage("-03-idle");
            band.Close();
            Console.WriteLine(JsonSerializer.Serialize(new { Mode = "lyrics-handoff", Theme = theme, Failures = failures }));
            if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
            Console.WriteLine("Lyrics handoff passed: delayed lyrics/artwork, readable transitions, identical text, hover, rapid skipping, fixed width and idle cleanup.");
            return;
        }

        if (view == "context-menu-live")
        {
            VerifyContextMenus(previewModel, profile.AccountManager, outputPath, theme);
            return;
        }

        if (view.StartsWith("taskbar-band", StringComparison.Ordinal))
        {
            var bandWindow = new TaskbarBandWindow(profile.AccountManager);
            bandWindow.SetLyricsSettings(false, true, false, 320, showVisualizer: view.Contains("glass", StringComparison.Ordinal), adaptiveWidth: false,
                transparentBackground: !view.Contains("glass", StringComparison.Ordinal), hideWhenNoMedia: false);
            bandWindow.SetPreviewLyricsEnabled(true);
            var previewIcon = profile.IconPath;
            bandWindow.SetPinnedGames(new List<GameListViewModel>
            {
                new(730, "Counter-Strike 2") { IconPath = previewIcon, HasBinding = true, BindingAccountName = "主账号" }
            });
            bandWindow.SetPinnedAccounts(new List<SteamAccount>
            {
                new() { SteamId = "76561198000000001", AccountName = "main_account", PersonaName = "主账号", AvatarPath = previewIcon },
                new() { SteamId = "76561198000000002", AccountName = "second_account", PersonaName = "小号", AvatarPath = previewIcon },
                new() { SteamId = "76561198000000003", AccountName = "third_account", PersonaName = "备用账号", AvatarPath = previewIcon }
            });
            bandWindow.UpdateCurrentAccount(profile.AccountManager.Accounts[0]);
            bandWindow.SetPreviewMedia(view == "taskbar-band-no-media"
                ? new TaskbarMediaSnapshot(false, false, string.Empty, string.Empty, "系统媒体", CanControlPlayback: false)
                : new TaskbarMediaSnapshot(true, view != "taskbar-band-paused", "一整片海洋都在你的眼睛里", "房东的猫 · 世界/青年", "同步歌词预览",
                    File.ReadAllBytes(previewIcon), view == "taskbar-band-paused" ? "" : "下一句会随着旋律轻轻浮现", .42,
                    Artist: "房东的猫", Album: "世界/青年", HasSyncedLyrics: true, CurrentLyric: "一整片海洋都在你的眼睛里", TrackKey: "preview-track", TrackVersion: 1));
            var bandSize = new Size(Math.Max(1, bandWindow.Width), view.Contains("compact", StringComparison.Ordinal) ? 30 : Math.Max(1, bandWindow.Height));
            var bandRoot = (FrameworkElement)bandWindow.Content;
            bandRoot.Measure(bandSize);
            bandRoot.Arrange(new Rect(0, 0, bandSize.Width, bandSize.Height));
            bandRoot.UpdateLayout();
            if (view.EndsWith("hover", StringComparison.Ordinal))
            {
                bandWindow.SetPreviewControlsVisible(true);
                bandRoot.UpdateLayout();
            }
            CaptureVisual(bandRoot, (int)Math.Ceiling(bandSize.Width), (int)Math.Ceiling(bandSize.Height), outputPath, requestedView, theme, state, smoke);
            bandWindow.Close();
            Console.WriteLine(smoke ? "TaskbarBandWindow isolated visual smoke passed." : outputPath);
            return;
        }

        if (view.StartsWith("update-dialog", StringComparison.Ordinal))
        {
            using var updateService = profile.CreateUpdateService();
            var dialog = new UpdateDialog(updateService, new UpdateInfo
            {
                Version = "3.3.0",
                Changelog = "界面清晰度与响应式布局优化\n\n• 账号、游戏与存档页面使用一致的排版和对比度\n• 完善任务栏歌词和桌面浮窗的视觉层次\n• 改善设置、键盘焦点与禁用状态",
                ExpectedSha256 = view == "update-dialog-unverified" ? "" : new string('A', 64)
            });
            if (view == "update-dialog-progress")
            {
                SetVisibility(dialog, "ProgressPanel", Visibility.Visible);
                SetVisibility(dialog, "DownloadButton", Visibility.Collapsed);
                SetVisibility(dialog, "CancelButton", Visibility.Visible);
                SetText(dialog, "DownloadStatusText", "正在下载… 18.4 MB / 48.0 MB");
                SetText(dialog, "DownloadPercentText", "38%");
                if (dialog.FindName("DownloadProgressBar") is ProgressBar progress) progress.Value = 38;
            }
            var dialogRoot = (FrameworkElement)dialog.Content;
            var dialogSize = new Size(dialog.Width, dialog.Height);
            dialogRoot.Measure(dialogSize);
            dialogRoot.Arrange(new Rect(dialogSize));
            dialogRoot.UpdateLayout();
            CaptureVisual(dialogRoot, (int)dialog.Width, (int)dialog.Height, outputPath, requestedView, theme, state, smoke);
            dialog.Close();
            Console.WriteLine(smoke ? "UpdateDialog isolated visual smoke passed." : outputPath);
            return;
        }

        if (view == "workflow-dialog")
        {
            var workflowViewModel = previewModel;
            var dialog = new GameLaunchWorkflowWindow(workflowViewModel, new GameListViewModel(730, "Counter-Strike 2"));
            var dialogRoot = (FrameworkElement)dialog.Content;
            var dialogSize = new Size(dialog.Width, dialog.Height);
            dialogRoot.Measure(dialogSize);
            dialogRoot.Arrange(new Rect(dialogSize));
            dialogRoot.UpdateLayout();
            SetText(dialog, "LaunchArgumentsBox", "-novid -language schinese");
            if (dialog.FindName("BackupBeforeLaunchCheck") is CheckBox before) before.IsChecked = true;
            if (dialog.FindName("BackupAfterExitCheck") is CheckBox after) after.IsChecked = true;
            CaptureVisual(dialogRoot, (int)dialog.Width, (int)dialog.Height, outputPath, requestedView, theme, state, smoke);
            dialog.Close();
            Console.WriteLine(smoke ? "GameLaunchWorkflowWindow isolated visual smoke passed." : outputPath);
            return;
        }

        if (view == "custom-save-dialog")
        {
            var dialog = new CustomSaveFolderDialog(new[]
            {
                new SteamAccount { SteamId = "1", AccountName = "main_account", PersonaName = "主账号" },
                new SteamAccount { SteamId = "2", AccountName = "alt_blue", PersonaName = "备用账号 01" }
            });
            var dialogRoot = (FrameworkElement)dialog.Content;
            var dialogSize = new Size(dialog.Width, dialog.Height);
            dialogRoot.Measure(dialogSize);
            dialogRoot.Arrange(new Rect(dialogSize));
            dialogRoot.UpdateLayout();
            SetText(dialog, "GameNameBox", "ELDEN RING");
            SetText(dialog, "FolderPathBox", Path.Combine(previewDirectory, "sample-saves", "EldenRing"));
            CaptureVisual(dialogRoot, (int)dialog.Width, (int)dialog.Height, outputPath, requestedView, theme, state, smoke);
            dialog.Close();
            Console.WriteLine(outputPath);
            return;
        }

        if (view is "binding-dialog" or "settings-dialog")
        {
            Window dialog = view == "binding-dialog"
                ? new GameBindingWindow(previewModel, profile.Bindings)
                : new SettingsWindow(previewModel);
            if (view == "binding-dialog") SetText(dialog, "GameCountLabel", "— 5 款游戏");
            CaptureVisual((FrameworkElement)dialog.Content, (int)dialog.Width, (int)dialog.Height,
                outputPath, requestedView, theme, state, smoke);
            dialog.Close();
            Console.WriteLine(smoke ? $"{view} isolated visual smoke passed." : outputPath);
            return;
        }

        if (view.StartsWith("desktop-floating", StringComparison.Ordinal))
        {
            var floating = new DesktopFloatingWindow(profile.AccountManager);
            if (view != "desktop-floating-empty")
            {
                floating.SetPinnedAccounts(profile.AccountManager.Accounts.Take(2).ToList());
                floating.SetPinnedGames(previewModel.GameList.Take(2).ToList());
                floating.UpdateCurrentAccount(profile.AccountManager.Accounts[0]);
            }
            floating.SetOpacityPercent(100);
            var floatingRoot = (FrameworkElement)floating.Content;
            var desired = NaturalSize(floatingRoot, 900, 120);
            CaptureVisual(floatingRoot, desired.Width, desired.Height, outputPath, requestedView, theme, state, smoke);
            floating.Close();
            Console.WriteLine(smoke ? $"{view} isolated visual smoke passed." : outputPath);
            return;
        }

        if (view is "media-flyout" or "volume-flyout" or "volume-flyout-muted" or "notification-flyout" or "notification-flyout-info" or "notification-flyout-error")
        {
            var flyout = new TaskbarFlyoutWindow();
            if (view.StartsWith("notification-flyout", StringComparison.Ordinal))
            {
                var sampleMsg = view == "notification-flyout-error"
                    ? "切换失败，请确保 Steam 已关闭"
                    : view == "notification-flyout-info"
                        ? "主账号 已是当前账号"
                        : "已切换到 主账号，Steam 已启动";
                flyout.ConfigureNotificationContent(sampleMsg);
            }
            else
            {
                // ShowControls/ShowVolume make a native window visible; configure only its content instead.
                SetVisibility(flyout, "ControlsHost", view == "media-flyout" ? Visibility.Visible : Visibility.Collapsed);
                SetVisibility(flyout, "VolumeHost", view == "media-flyout" ? Visibility.Collapsed : Visibility.Visible);
                SetText(flyout, "VolumePercentText", view == "volume-flyout-muted" ? "0%" : "68%");
                SetText(flyout, "VolumeIcon", view == "volume-flyout-muted" ? "\uE74F" : "\uE767");
                if (flyout.FindName("VolumeProgressBar") is ProgressBar volume) volume.Value = view == "volume-flyout-muted" ? 0 : 68;
            }
            var flyoutRoot = (FrameworkElement)flyout.Content;
            var desired = NaturalSize(flyoutRoot, 260, 90);
            CaptureVisual(flyoutRoot, desired.Width, desired.Height, outputPath, requestedView, theme, state, smoke);
            flyout.Close();
            Console.WriteLine(smoke ? $"{view} isolated visual smoke passed." : outputPath);
            return;
        }

        if (view is "context-menu" or "context-menu-accounts" or "context-menu-windows")
        {
            var menu = SharedContextMenuBuilder.Build(new System.Collections.ObjectModel.ObservableCollection<AccountViewModel>(previewModel.Accounts.Take(2)), previewModel.SelectedAccount,
                () => { }, _ => { }, () => { }, () => { }, () => { }, showDetach: true,
                isDesktopFloatingEnabled: true, isTaskbarPinned: true, toggleDesktopFloating: _ => { },
                toggleDesktopFloatingTopmost: _ => { }, toggleTaskbarPinned: _ => { },
                isTaskbarLyricsEnabled: true, toggleTaskbarLyrics: _ => { },
                emergencyRestore: () => { }, exit: () => { });
            if (view != "context-menu")
            {
                var parent = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, view == "context-menu-accounts" ? "Accounts" : "Windows"));
                menu = DetachSubmenuForCapture(parent);
            }
            menu.Width = 310;
            var desired = NaturalSize(menu, 310, 600);
            CaptureVisual(menu, desired.Width, desired.Height, outputPath, requestedView, theme, state, smoke);
            Console.WriteLine(smoke ? "Shared context menu isolated visual smoke passed." : outputPath);
            return;
        }

        if (view is "visual-states" or "style")
        {
            var contrasts = VerifyThemeContrast();
            var gallery = CreateStateGallery();
            CaptureVisual(gallery, 1100, 690, outputPath, requestedView, theme, "gallery", smoke, contrasts);
            Console.WriteLine(smoke ? "Theme contrast and visual states smoke passed (focus is a template preview, not OS input)." : outputPath);
            return;
        }

        // Borderless dialogs must expose real resize edges. WindowStyle=None +
        // AllowsTransparency strips the system frame, and without WindowChrome
        // WM_NCHITTEST answers HTCLIENT on every edge — the window becomes
        // impossible to resize while still advertising ResizeMode/MinWidth.
        // EnsureHandle materializes the HWND without Show(), so the dialog's
        // game-scanning Loaded handler never runs.
        if (requestedView == "dialog-resize-smoke")
        {
            Exception? failure = null;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var dialog = new GameBindingWindow(previewModel, profile.Bindings);
                    if (dialog.ResizeMode == ResizeMode.NoResize)
                        throw new InvalidOperationException("GameBindingWindow is no longer resizable.");
                    var helper = new WindowInteropHelper(dialog);
                    helper.EnsureHandle();
                    dialog.UpdateLayout();
                    NativeMessages.GetWindowRect(helper.Handle, out var rect);

                    (int x, int y, int expected, string label)[] probes =
                    {
                        (rect.Left + 1, rect.Top + rect.Bottom / 2, 10, "left"),
                        (rect.Right - 1, rect.Top + rect.Bottom / 2, 11, "right"),
                        (rect.Left + rect.Right / 2, rect.Top + 1, 12, "top"),
                        (rect.Left + rect.Right / 2, rect.Bottom - 1, 15, "bottom"),
                        (rect.Left + 1, rect.Top + 1, 13, "top-left"),
                        (rect.Right - 1, rect.Top + 1, 14, "top-right"),
                        (rect.Left + 1, rect.Bottom - 1, 16, "bottom-left"),
                        (rect.Right - 1, rect.Bottom - 1, 17, "bottom-right"),
                        (rect.Left + rect.Right / 2, rect.Top + rect.Bottom / 2, 1, "client"),
                    };
                    foreach (var (x, y, expected, label) in probes)
                    {
                        var hit = NativeMessages.HitTest(helper.Handle, x, y);
                        if (hit != expected)
                            throw new InvalidOperationException(
                                $"GameBindingWindow {label} edge hit test returned {hit}, expected {expected}.");
                    }

                    dialog.Close();
                }
                catch (Exception ex) { failure = ex; }
                finally { app.Shutdown(); }
            }));
            app.Run();
            if (failure != null) throw failure;
            Console.WriteLine("WPF dialog-resize-smoke passed: all eight resize edges answer hit testing.");
            return;
        }

        if (!MainViews.Contains(view) && !specialSmoke && view is not ("main-guidance" or "account-library-guidance" or "settings-backup-guidance" or "settings-advanced" or "backup-feedback-guidance" or "persistence-error-guidance" or "maintenance-guidance" or "settings-protection-guidance")) throw new ArgumentException($"Unknown view mode: {view}. Use --list.");

        if (view == "accounts-failed")
            typeof(MainViewModel).GetField("_lastFailedSwitchAccount", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(previewModel, previewModel.Accounts.First(account => !account.IsCurrent && !account.IsArchived));

        var window = new MainWindow(previewModel, previewMode: true)
        {
            Width = width,
            Height = height
        };

        var viewModel = (MainViewModel)window.DataContext;
        if (view is "exit-lifecycle-smoke" or "exit-direct-smoke")
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.MainWindow = window;
            Exception? failure = null;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    viewModel.MinimizeToTray = true;
                    if (!viewModel.CloseButtonToolTip.Contains("托盘"))
                        throw new InvalidOperationException("Close tooltip does not describe the tray behavior.");
                    window.Close();
                    if (window.IsShutdownStarted || closed)
                        throw new InvalidOperationException("Ordinary close did not keep the tray utility running.");
                    if (view == "exit-direct-smoke")
                    {
                        viewModel.MinimizeToTray = false;
                        if (viewModel.CloseButtonToolTip.Contains("托盘"))
                            throw new InvalidOperationException("Close tooltip was not updated after disabling the tray.");
                        window.Close();
                        app.Shutdown();
                    }
                    else
                    {
                        typeof(MainWindow).GetMethod("RequestExit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
                    }
                    if (!window.IsShutdownStarted || !closed)
                        throw new InvalidOperationException("Full exit did not clean up and close the main window.");
                    window.CleanupForApplicationExit(); // A second cleanup must be harmless.
                }
                catch (Exception ex)
                {
                    failure = ex;
                    app.Shutdown(-1);
                }
            }));
            var exitCode = app.Run();
            if (failure != null) throw failure;
            if (exitCode != 0 || !closed) throw new InvalidOperationException($"WPF shutdown failed: {exitCode}.");
            Console.WriteLine($"WPF {view} passed: close-to-tray, full cleanup, window closed and dispatcher exited.");
            return;
        }

        if (view == "quick-populated")
        {
            AddPreviewQuickLaunch(viewModel, "Discord", @"C:\Program Files\Discord\Discord.exe", true, true);
            AddPreviewQuickLaunch(viewModel, "OBS Studio", @"C:\Program Files\obs-studio\bin\64bit\obs64.exe", false, false);
            AddPreviewQuickLaunch(viewModel, "SteamDB", @"C:\Users\Preview\Desktop\SteamDB.url", false, false);
            AddPreviewQuickLaunch(viewModel, "Visual Studio Code", @"C:\Program Files\Microsoft VS Code\Code.exe", true, false);
            AddPreviewQuickLaunch(viewModel, "Spotify", @"C:\Users\Preview\AppData\Roaming\Spotify\Spotify.exe", false, false);
            AddPreviewQuickLaunch(viewModel, "Notion", @"C:\Users\Preview\AppData\Local\Programs\Notion\Notion.exe", false, false);
        }
        viewModel.StatusText = $"v{UpdateService.GetCurrentVersion()} · 界面预览";
        if (view == "account-library-guidance")
        {
            VerifyAccountLibraryGuidance(window, viewModel, width, height);
            return;
        }
        if (view == "accounts-recovery")
            ((Button)window.FindName("ShowRecoveryAccountsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (view == "accounts-search-empty")
        {
            var search = (TextBox)window.FindName("AccountSearchBox");
            typeof(MainWindow).GetMethod("AccountSearchBox_GotFocus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object?[] { search, null });
            search.Text = "找不到的账号";
        }
        if (view == "main-guidance")
        {
            VerifyMainGuidance(window, viewModel, width, height);
            return;
        }
        if (view == "settings-backup-guidance")
        {
            VerifySettingsBackupGuidance(window, viewModel, width, height, profile.Directory);
            return;
        }
        if (view == "backup-feedback-guidance")
        {
            VerifyBackupFeedback(window, viewModel, width, height, profile, outputPath, theme);
            return;
        }
        if (view == "persistence-error-guidance")
        {
            VerifyPersistenceErrorUi(window, viewModel, profile, outputPath, width, height, theme);
            return;
        }
        if (view is "maintenance-guidance" or "settings-protection-guidance")
        {
            typeof(MainWindow).GetMethod("SwitchToSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var maintenanceRoot = (FrameworkElement)window.FindName("RootBorder");
            void Layout() { maintenanceRoot.Measure(new Size(width, height)); maintenanceRoot.Arrange(new Rect(0, 0, width, height)); maintenanceRoot.UpdateLayout(); }
            if (view == "settings-protection-guidance")
            {
                profile.SaveProtectionReason = "启动时未能完整读取设置，原文件已保护。请先导出当前修改，再重启程序重新读取原设置。";
                viewModel.BackupExportDirectory = Path.Combine(profile.Directory, "exports");
                viewModel.TaskbarLyricsWidth = 600;
                if (viewModel.SaveSettingsNow() || !viewModel.HasUnsavedSettings || !viewModel.HasSettingsSaveError)
                    throw new InvalidOperationException("A protected write lost pending settings or reported success.");
                Layout();
                if (!((TextBlock)window.FindName("SettingsSaveStateLabel")).Text.Contains("原文件已保护") ||
                    !((TextBlock)window.FindName("ToastText")).Text.Contains("重启"))
                    throw new InvalidOperationException("The real UI omitted the write-protection recovery instructions.");
            }
            else
            {
                ((TabControl)window.FindName("SettingsTabs")).SelectedIndex = 1;
                Layout();
                ((ScrollViewer)window.FindName("AdvancedSettingsScroll")).ScrollToEnd();
            }
            PumpPreview(450, Layout);
            if (view == "maintenance-guidance")
            {
                foreach (var name in new[] { "DiagnosticsActionsPanel", "ConfigurationTransferActionsPanel", "RecoveryActionsPanel" })
                {
                    var panel = (FrameworkElement)window.FindName(name);
                    var bounds = panel.TransformToAncestor(maintenanceRoot).TransformBounds(new Rect(panel.RenderSize));
                    if (bounds.Top < 250 || bounds.Bottom > height - 70 || bounds.Left < 230 || bounds.Right > width)
                        throw new InvalidOperationException($"Maintenance actions are clipped or overlap navigation/footer: {name} {bounds}.");
                }
            }
            CaptureVisual(maintenanceRoot, width, height, outputPath, view, theme, "default", false);
            if (view == "settings-protection-guidance")
            {
                var action = (Button)window.FindName("RetrySettingsSaveButton");
                if (!Equals(action.Content, "导出当前修改"))
                    throw new InvalidOperationException("Read protection offered a futile save retry instead of preserving current edits.");
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var exportWait = Stopwatch.StartNew();
                while (!viewModel.HasRecentBackupExport && exportWait.ElapsedMilliseconds < 5000) PumpPreview(20, Layout);
                if (!viewModel.HasRecentBackupExport || !viewModel.HasUnsavedSettings || !viewModel.HasSettingsSaveError)
                    throw new InvalidOperationException("The protection action failed to export edits or incorrectly cleared write protection.");
                var exported = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(viewModel.RecentBackupExportPath!))!;
                if (exported.TaskbarLyricsWidth != 600)
                    throw new InvalidOperationException("The recovery export lost the pending lyric width.");
            }
            Console.WriteLine($"{view} passed ({width}x{height}, {theme}): actual WPF bindings and layout, isolated settings only.");
            return;
        }
        if (view is "accounts-current" or "accounts-target" or "accounts-busy" or "accounts-failed")
        {
            viewModel.SelectedAccount = view == "accounts-current"
                ? viewModel.Accounts.First(account => account.IsCurrent && !account.IsArchived)
                : viewModel.Accounts.First(account => !account.IsCurrent && !account.IsArchived);
            viewModel.StatusText = view switch
            {
                "accounts-busy" => "正在切换至备用账号 01，请稍候…",
                "accounts-failed" => "切换未完成，请关闭 Steam 后点击「重试切换」。",
                _ => "选择账号后，确认右侧操作目标再执行。"
            };
            viewModel.IsLoading = view == "accounts-busy";
        }

        if (view == "settings-apply-smoke")
        {
            File.WriteAllText(Path.Combine(previewDirectory, "steam.exe"), "test stub, never execute");
            File.WriteAllText(Path.Combine(previewDirectory, "loginusers.vdf"), "\"users\" { }");
            var imported = SettingsSession.Copy(viewModel.GetSettings());
            imported.CustomSteamPath = previewDirectory;
            imported.CustomLoginUsersPath = Path.Combine(previewDirectory, "loginusers.vdf");
            imported.GameSaveManualRetention = 1;
            imported.GameSavePeriodicIntervalMinutes = 17;
            imported.TaskbarLyricsWidth = 630;
            var frame = new System.Windows.Threading.DispatcherFrame();
            Exception? failure = null;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await viewModel.ApplySettingsAsync(imported);
                    typeof(MainWindow).GetMethod("SyncGameSaveSettingsUi", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
                    var intervalCombo = (System.Windows.Controls.ComboBox)window.FindName("SettingsGameSaveIntervalCombo");
                    var retentionCombo = (System.Windows.Controls.ComboBox)window.FindName("SettingsGameSaveRetentionCombo");
                    if (((System.Windows.Controls.ComboBoxItem)intervalCombo.SelectedItem).Tag?.ToString() != "17" ||
                        ((System.Windows.Controls.ComboBoxItem)retentionCombo.SelectedItem).Tag?.ToString() != "1" ||
                        viewModel.GameSavePeriodicIntervalMinutes != 17 || viewModel.GameSaveManualRetention != 1 ||
                        viewModel.TaskbarLyricsWidth != 630)
                        throw new InvalidOperationException("WPF settings controls reverted the imported values.");

                    // The title-bar maximize control must track WindowState: the
                    // glyph swaps to "restore", the tooltip follows, and the shell
                    // flattens its rounded corners so DWM's work-area clip cannot
                    // shear them off against the window background.
                    // The maximize control's glyph/tooltip and the shell's corner
                    // flattening hang off the live WindowState pipeline. Offscreen
                    // previews never Show the window, and WPF keeps StateChanged
                    // events and AncestorType bindings dormant until an HWND
                    // exists. Materialize the pipeline once, parked offscreen so
                    // nothing flashes on screen.
                    // Maximizing repositions the window onto the primary monitor's
                    // work area even from -32000, so run every state toggle fully
                    // transparent: the assertions read properties, not pixels.
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -32000;
                    window.Top = -32000;
                    window.Show();
                    window.Opacity = 0;
                    var maximizeButton = (System.Windows.Controls.Button)window.FindName("BtnWindowMaximize");
                    var rootBorder = (System.Windows.Controls.Border)window.FindName("RootBorder");
                    var glyphGrid = (System.Windows.Controls.Grid)maximizeButton.Content;
                    var restoredGlyph = (System.Windows.Controls.TextBlock)glyphGrid.Children[0];
                    var maximizedGlyph = (System.Windows.Controls.TextBlock)glyphGrid.Children[1];
                    window.WindowState = WindowState.Maximized;
                    if (maximizeButton.ToolTip as string != "还原" ||
                        maximizedGlyph.Visibility != Visibility.Visible ||
                        restoredGlyph.Visibility != Visibility.Collapsed ||
                        rootBorder.CornerRadius != new CornerRadius(0) ||
                        rootBorder.BorderThickness != new Thickness(0))
                        throw new InvalidOperationException("Maximize control and shell corners did not follow WindowState.Maximized.");
                    window.WindowState = WindowState.Normal;
                    if (maximizeButton.ToolTip as string != "最大化" ||
                        maximizedGlyph.Visibility != Visibility.Collapsed ||
                        restoredGlyph.Visibility != Visibility.Visible ||
                        rootBorder.CornerRadius != new CornerRadius(16) ||
                        rootBorder.BorderThickness != new Thickness(1))
                        throw new InvalidOperationException("Maximize control and shell corners did not restore with WindowState.Normal.");

                    // Win11 Snap Layouts open only when WM_NCHITTEST answers
                    // HTMAXBUTTON over the maximize button. Verify the whole
                    // chain: the hit test itself, the hover highlight it drives
                    // (ChromeButtonState, since the button no longer sees mouse
                    // events), the client area staying untouched, and the
                    // non-client click toggling the window exactly once.
                    var mainHwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    var buttonDpi = VisualTreeHelper.GetDpi(maximizeButton);
                    var buttonOrigin = maximizeButton.PointToScreen(new Point(0, 0));
                    int buttonX = (int)((buttonOrigin.X + maximizeButton.ActualWidth / 2) * buttonDpi.DpiScaleX);
                    int buttonY = (int)((buttonOrigin.Y + maximizeButton.ActualHeight / 2) * buttonDpi.DpiScaleY);
                    int neighborX = buttonX - 70; // the minimize button: plain client area
                    if (NativeMessages.HitTest(mainHwnd, buttonX, buttonY) != 9)
                        throw new InvalidOperationException("Maximize button did not report HTMAXBUTTON; the Win11 snap-layouts flyout would never open.");
                    if (!ChromeButtonState.GetIsHot(maximizeButton))
                        throw new InvalidOperationException("Chrome hover highlight did not follow the maximize hit test.");
                    if (NativeMessages.HitTest(mainHwnd, neighborX, buttonY) != 1)
                        throw new InvalidOperationException("Chrome hit test leaked beyond the maximize button.");
                    if (ChromeButtonState.GetIsHot(maximizeButton))
                        throw new InvalidOperationException("Chrome hover highlight did not clear outside the button.");
                    NativeMessages.NonClientButton(mainHwnd, 0x00A1, 9, buttonX, buttonY); // WM_NCLBUTTONDOWN
                    if (!ChromeButtonState.GetIsPressed(maximizeButton))
                        throw new InvalidOperationException("Chrome pressed state did not follow the non-client button down.");
                    NativeMessages.NonClientButton(mainHwnd, 0x00A2, 9, buttonX, buttonY); // WM_NCLBUTTONUP
                    if (window.WindowState != WindowState.Maximized)
                        throw new InvalidOperationException("Non-client click on the maximize button did not maximize the window.");
                    if (ChromeButtonState.GetIsPressed(maximizeButton))
                        throw new InvalidOperationException("Chrome pressed state did not clear after the non-client click.");
                    NativeMessages.NonClientButton(mainHwnd, 0x00A1, 9, buttonX, buttonY);
                    NativeMessages.NonClientButton(mainHwnd, 0x00A2, 9, buttonX, buttonY);
                    if (window.WindowState != WindowState.Normal)
                        throw new InvalidOperationException("Non-client click on the restore button did not restore the window.");
                    window.Opacity = 1;
                }
                catch (Exception ex) { failure = ex; }
                finally { frame.Continue = false; }
            }));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            viewModel.Dispose();
            if (failure != null) throw failure;
            Console.WriteLine("WPF settings apply smoke passed: live values, non-preset interval, retention=1 and binding projection.");
            return;
        }

        var navigationMethod = view switch
        {
            "games" or "games-empty" => "SwitchToGames",
            "saves" or "saves-empty" => "SwitchToSaveCenter",
            "desktop-icons" => "SwitchToDesktopIcons",
            "lyrics" => "SwitchToTaskbarLyrics",
            "lyrics-advanced" => "SwitchToTaskbarLyricsAdvanced",
            "quick" or "quick-populated" => "SwitchToQuickLaunch",
            "settings" or "settings-advanced" or "configuration" or "maintenance-guidance" or "settings-protection-guidance" => "SwitchToSettings",
            _ => "SwitchToAccounts"
        };
        var minimalModeField = typeof(MainViewModel).GetField(
            "_minimalMode",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        minimalModeField?.SetValue(viewModel, true);
        if ((view is "saves" or "saves-empty") && window.FindName("SaveCenterHost") is System.Windows.Controls.ContentControl saveHost)
        {
            var saveCenter = new GameSaveCenterWindow(viewModel);
            var cs2Icon = profile.IconPath;
            var pubgIcon = profile.IconPath;
            var eldenRingIcon = profile.IconPath;
            saveCenter.SetPreviewEntries(view == "saves-empty" ? Array.Empty<GameSaveEntry>() : new[]
            {
                new GameSaveEntry { EntryId = "steam:1:730", SteamId = "1", AccountName = "main_account", PersonaName = "主账号", AppId = 730, GameName = "Counter-Strike 2", IconPath = cs2Icon, SavePath = @"C:\Program Files (x86)\Steam\userdata\1\730", FileCount = 42, TotalSize = 18_874_368, LastWriteTime = DateTime.Now.AddMinutes(-18), BackupCount = 4, LastBackupTime = DateTime.Now.AddHours(-2) },
                new GameSaveEntry { EntryId = "steam:2:578080", SteamId = "2", AccountName = "alt_blue", PersonaName = "备用账号 01", AppId = 578080, GameName = "PUBG: BATTLEGROUNDS", IconPath = pubgIcon, SavePath = @"C:\Program Files (x86)\Steam\userdata\2\578080", FileCount = 19, TotalSize = 7_602_176, LastWriteTime = DateTime.Now.AddDays(-1), BackupCount = 2, LastBackupTime = DateTime.Now.AddDays(-1) },
                new GameSaveEntry { EntryId = "detected:1245620", SteamId = "detected", AccountName = "本机通用存档", AppId = 1245620, GameName = "ELDEN RING", IconPath = eldenRingIcon, SavePath = @"C:\Users\Preview\AppData\Roaming\EldenRing", FileCount = 8, TotalSize = 31_457_280, LastWriteTime = DateTime.Now.AddHours(-5), IsCustom = true, IsDetected = true, DetectionRule = "AppData Saves", BackupCount = 1, LastBackupTime = DateTime.Now.AddDays(-3) }
            });
            saveHost.Content = saveCenter;
        }
        typeof(MainWindow)
            .GetMethod(navigationMethod, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null);
        minimalModeField?.SetValue(viewModel, false);
        if (view is "settings-advanced" or "configuration") ((TabControl)window.FindName("SettingsTabs")).SelectedIndex = 1;
        if (view == "accounts-grid")
            typeof(MainWindow).GetMethod("SetAccountGridMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { true });
        if (view == "accounts-empty") viewModel.Accounts.Clear();
        if (view == "games-empty")
        {
            // Navigate with seeded data first so SwitchToGames never launches a scan.
            viewModel.GameList.Clear();
            SetText(window, "GameScanStatus", "尚未添加游戏");
        }
        if ((view is "games" or "games-empty") && window.FindName("GameEmptyHint") is UIElement gameEmptyHint)
            gameEmptyHint.Visibility = view == "games-empty" ? Visibility.Visible : Visibility.Collapsed;

        var root = (FrameworkElement)window.FindName("RootBorder");
        var size = new Size(width, height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        if (view == "configuration" && window.FindName("AdvancedSettingsScroll") is ScrollViewer scroll)
        {
            if (window.FindName("ExportConfigurationBundleButton") is FrameworkElement target)
            {
                var position = target.TransformToAncestor(scroll).Transform(new Point());
                scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + position.Y - 150));
            }
            root.UpdateLayout();
        }

        CaptureVisual(root, width, height, outputPath, requestedView, theme, state, smoke);
        window.PrepareForApplicationShutdown();
        window.Close();

        Console.WriteLine(smoke ? $"{view} isolated visual smoke passed." : outputPath);
    }

    private static readonly List<Control> GalleryFocusTargets = new();
    private static string? ActiveProfileDirectory;

    private sealed class IsolatedPreviewProfile : IDisposable
    {
        public bool CanSaveSettings { get; set; } = true;
        public string? SaveProtectionReason { get; set; }
        public string Directory { get; }
        public string IconPath { get; }
        public AccountManager AccountManager { get; }
        public GameAccountBinding Bindings { get; }
        public QuickLaunchService QuickLaunch { get; }
        public MainViewModel Model { get; }

        public IsolatedPreviewProfile(string outputPath, string theme)
        {
            Directory = Path.Combine(Path.GetDirectoryName(outputPath)!, "isolated-profile", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            ActiveProfileDirectory = Directory;
            IconPath = Path.Combine(SourceRoot(), "src", "SteamSwitcher", "Resources", "icon.png");
            // Even an accidentally invoked read/scan must resolve to the fake profile.
            File.WriteAllText(Path.Combine(Directory, "steam.exe"), "OFFSCREEN PREVIEW STUB - NEVER EXECUTE");
            var loginUsers = Path.Combine(Directory, "loginusers.vdf");
            File.WriteAllText(loginUsers, "\"users\" { }");
            var steam = new SteamService();
            steam.SetCustomPaths(Directory, loginUsers);
            AccountManager = new AccountManager(steam, new AccountLibraryService(Path.Combine(Directory, "accounts.json")), () => null, downloadAvatars: false);
            Bindings = new GameAccountBinding(Path.Combine(Directory, "gamebindings.json"));
            var settings = new AppSettings
            {
                Theme = theme, CheckUpdateOnStartup = false, AutoInstallUpdates = false, AutoStartSteam = false,
                AutoScanGamesOnStartup = false, EnableLibraryInjection = false, GameSaveAutoBackupEnabled = false,
                GameSavePeriodicBackupEnabled = false, TaskbarLyricsEnabled = false, TaskbarLyricsOnlineEnabled = false,
                DesktopFloatingEnabled = false, StartWithWindows = false,
                CustomSteamPath = Directory, CustomLoginUsersPath = loginUsers, DesktopEnhance = new DesktopEnhanceSettings()
            };
            QuickLaunch = new QuickLaunchService(Path.Combine(Directory, "quicklaunch.json"));
            Model = new MainViewModel(AccountManager, Bindings, QuickLaunch,
                new SettingsSession(settings, snapshot =>
                {
                    if (SaveProtectionReason != null)
                        return SettingsService.SaveToDirectory(snapshot, Directory, primaryReadIncomplete: true);
                    if (!CanSaveSettings) return false;
                    File.WriteAllText(Path.Combine(Directory, "settings.json"), JsonSerializer.Serialize(snapshot));
                    return true;
                }, () => SaveProtectionReason), () => false, _ => true, Directory, monitorSystemState: false);
        }

        public UpdateService CreateUpdateService() => new(new NoNetworkHandler(), Path.Combine(Directory, "update-rate-limit.json"), Path.Combine(Directory, "updates"));
        public void Dispose() => Model.Dispose();
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("Network access is disabled in the UI preview harness."));
    }

    private static string SourceRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory != null && depth < 12; depth++, directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "SteamSwitcher", "SteamSwitcher.csproj"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Run UiCapture from its repository or its built output directory.");
    }

    private static void SeedPreviewModel(MainViewModel model, AccountManager manager, string iconPath)
    {
        AddPreviewAccount(model, "main_account", "主账号", true);
        AddPreviewAccount(model, "alt_blue", "备用账号 01", false);
        AddPreviewAccount(model, "alt_night", "备用账号 02", false);
        AddPreviewAccount(model, "family_share", "家庭共享", false);
        AddPreviewAccount(model, "work_profile", "工作账号", false);
        AddPreviewAccount(model, "guest_profile", "访客账号", false);
        // 预览专用：把已存档账号的时间戳压到最近，确保它出现在列表可见区域。
        AddPreviewAccount(model, "old_raider", "已弃用 · 老账号", false, archived: true);
        if (model.Accounts.Count > 0)
            model.Accounts[^1].Account.Timestamp = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds();
        foreach (var account in model.Accounts)
        {
            account.Account.AvatarPath = iconPath;
            account.Avatar = AccountManager.LoadImage(iconPath, 64);
        }
        manager.Accounts.AddRange(model.Accounts.Select(account => account.Account));
        model.SelectedAccount = model.Accounts[0];
        model.IsSteamRunning = true;
        // 预览专用：让已存档账号处于选中态，验证详情面板的找回指引卡。
        var archivedPreview = model.Accounts.FirstOrDefault(a => a.IsArchived);
        if (archivedPreview != null) model.SelectedAccount = archivedPreview;
        AddPreviewGame(model, 730, "Counter-Strike 2", "主账号", true, "今天 19:20");
        AddPreviewGame(model, 578080, "PUBG: BATTLEGROUNDS", "备用账号 01", false, "昨天 22:14");
        AddPreviewGame(model, 271590, "Grand Theft Auto V", "备用账号 02", false, "10-18 21:05");
        AddPreviewGame(model, 1172470, "Apex Legends", "备用账号 01", true, "10-17 18:42");
        AddPreviewGame(model, 1245620, "ELDEN RING", null, false, null);
        foreach (var game in model.GameList.Where(game => game.HasBinding))
            game.BindingAccountSteamId = model.Accounts.FirstOrDefault(account => account.DisplayName == game.BindingAccountName)?.SteamId;
        model.SelectedGame = model.GameList[0];
        model.SelectedBindingAccount = model.Accounts[0];
    }

    private static void SetVisibility(Window window, string name, Visibility visibility)
    {
        if (window.FindName(name) is UIElement element) element.Visibility = visibility;
    }

    private static void SetText(Window window, string name, string text)
    {
        if (window.FindName(name) is TextBlock label) label.Text = text;
        else if (window.FindName(name) is TextBox input) input.Text = text;
    }

    private static (int Width, int Height) NaturalSize(FrameworkElement element, int fallbackWidth, int fallbackHeight)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = element.DesiredSize;
        return ((int)Math.Ceiling(double.IsFinite(size.Width) && size.Width > 0 ? size.Width : fallbackWidth),
            (int)Math.Ceiling(double.IsFinite(size.Height) && size.Height > 0 ? size.Height : fallbackHeight));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static bool VisibleInPreview(FrameworkElement element, FrameworkElement root)
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement visual && visual.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(current, root)) return element.ActualWidth > 0 && element.ActualHeight > 0;
        }
        return false;
    }

    private static Brush ResourceBrush(string name) => Application.Current.TryFindResource(name) as Brush
        ?? throw new InvalidOperationException($"Missing preview theme token: {name}.");

    private static IReadOnlyDictionary<string, double> VerifyThemeContrast()
    {
        Color ColorOf(string key) => ResourceBrush(key) is SolidColorBrush brush
            ? brush.Color : throw new InvalidOperationException($"Contrast token must be solid: {key}.");
        static double Luminance(Color color)
        {
            static double Linear(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        var results = new Dictionary<string, double>();
        foreach (var pair in new[] { ("TextPrimaryBrush", "CardBgBrush"), ("TextSecondaryBrush", "CardBgBrush"), ("AccentForegroundBrush", "AccentBrush") })
        {
            var foreground = ColorOf(pair.Item1);
            var background = ColorOf(pair.Item2);
            if (foreground.A != 255 || background.A != 255) throw new InvalidOperationException($"Contrast check requires opaque tokens: {pair}.");
            var first = Luminance(foreground); var second = Luminance(background);
            var ratio = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
            results[$"{pair.Item1}/{pair.Item2}"] = Math.Round(ratio, 3);
            if (ratio < 4.5) throw new InvalidOperationException($"Theme contrast failed: {pair.Item1}/{pair.Item2} = {ratio:F2}, expected >= 4.5.");
        }
        return results;
    }

    private static FrameworkElement CreateStateGallery()
    {
        GalleryFocusTargets.Clear();
        var stack = new StackPanel { Margin = new Thickness(36) };
        stack.Children.Add(new TextBlock { Text = "控件状态与主题对比度", FontSize = 25, FontWeight = FontWeights.SemiBold, Foreground = ResourceBrush("TextPrimaryBrush"), Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock { Text = "真实共享样式 · 视觉状态预览 · 不发送键盘或鼠标输入", Foreground = ResourceBrush("TextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 24) });
        foreach (var state in new[] { "default", "focus", "disabled", "selected" })
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 18), MinHeight = 82 };
            foreach (var width in new[] { 105d, 180, 280, 115, 250 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            var label = new TextBlock { Text = state, Foreground = ResourceBrush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
            var button = new Button { Content = state == "selected" ? "默认操作" : "保存设置", Width = 150, Height = 40, IsEnabled = state != "disabled", IsDefault = state == "selected", Style = Application.Current.TryFindResource("PrimaryButtonStyle") as Style };
            var input = new TextBox { Text = "account_preview", Width = 250, Height = 42, VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = state != "disabled", Style = Application.Current.TryFindResource("FormTextBoxStyle") as Style };
            var toggle = new CheckBox { IsChecked = state is "selected" or "disabled", IsEnabled = state != "disabled", VerticalAlignment = VerticalAlignment.Center, Style = Application.Current.TryFindResource("SwitchToggle") as Style };
            var selection = new ListBox { Width = 220, Height = 74, Background = ResourceBrush("CardBgBrush"), Foreground = ResourceBrush("TextPrimaryBrush"), BorderBrush = ResourceBrush("BorderBrush"), BorderThickness = new Thickness(1), IsEnabled = state != "disabled" };
            selection.Items.Add(new ListBoxItem { Content = "主账号", Padding = new Thickness(8, 4, 8, 4) });
            selection.Items.Add(new ListBoxItem { Content = "备用账号", Padding = new Thickness(8, 4, 8, 4) });
            selection.SelectedIndex = state == "selected" ? 1 : 0;
            var elements = new FrameworkElement[] { label, button, input, toggle, selection };
            for (var index = 0; index < elements.Length; index++) { Grid.SetColumn(elements[index], index); row.Children.Add(elements[index]); }
            if (state == "focus") GalleryFocusTargets.AddRange(new Control[] { button, input, toggle });
            if (state == "selected") input.SelectAll();
            stack.Children.Add(row);
        }
        stack.Children.Add(new TextBlock { Text = "焦点行使用模板焦点装饰；离屏渲染无法证明真实键盘焦点或 Tab 顺序。\n禁用状态展示真实 IsEnabled=false；选中状态使用真实 IsChecked / SelectedIndex。", Foreground = ResourceBrush("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        return new Border { Background = ResourceBrush("WindowBgBrush"), Child = stack };
    }

    private static int PreviewFocus(Control control)
    {
        control.ApplyTemplate();
        var rings = Descendants(control).OfType<Border>().Where(border => border.Name.Contains("focus", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var ring in rings) ring.Opacity = 1;
        if (rings.Count == 0)
        {
            var surface = Descendants(control).OfType<Border>().FirstOrDefault(border => border.Name is "InputSurface" or "CloseSurface" or "CheckSurface" or "RadioSurface");
            if (surface != null) { surface.BorderBrush = ResourceBrush("FocusRingBrush"); surface.BorderThickness = new Thickness(2); }
            else { control.BorderBrush = ResourceBrush("FocusRingBrush"); control.BorderThickness = new Thickness(2); }
        }
        return Math.Max(1, rings.Count);
    }

    private static IEnumerable<GlyphRunDrawing> GlyphDrawings(Drawing? drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var item in GlyphDrawings(child)) yield return item;
    }

    private static void CaptureVisual(FrameworkElement root, int width, int height, string outputPath,
        string mode, string theme, string state, bool smoke, IReadOnlyDictionary<string, double>? contrasts = null)
    {
        width = Math.Clamp(width, 1, 4096); height = Math.Clamp(height, 1, 4096);
        void Layout() { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
        Layout();
        var controls = Descendants(root).OfType<Control>().Where(control => VisibleInPreview(control, root)).ToList();
        var stateEvidence = new List<string>();
        if (state == "disabled")
        {
            foreach (var control in controls.Where(control => control is Button or TextBox or CheckBox).Take(4))
            { control.IsEnabled = false; stateEvidence.Add($"{control.GetType().Name}:{control.Name}:IsEnabled=false"); }
        }
        else if (state == "selected")
        {
            foreach (var selector in controls.OfType<ListBox>().Where(list => list.Items.Count > 0).Take(1))
            { selector.SelectedIndex = Math.Min(1, selector.Items.Count - 1); stateEvidence.Add($"{selector.Name}:SelectedIndex={selector.SelectedIndex}"); }
        }
        else if (state == "focus")
        {
            var target = controls.FirstOrDefault(control => control is TextBox && control.IsEnabled) ?? controls.FirstOrDefault(control => control is Button && control.IsEnabled);
            if (target != null) { PreviewFocus(target); stateEvidence.Add($"{target.GetType().Name}:{target.Name}:template focus visual only"); }
        }
        else if (state == "gallery")
            foreach (var control in GalleryFocusTargets) { PreviewFocus(control); stateEvidence.Add($"{control.GetType().Name}:template focus visual only"); }
        Layout();
        if (!smoke) SaveElement(root, width, height, outputPath, state == "wallpaper");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath + ".json", JsonSerializer.Serialize(new
        {
            Mode = mode, Theme = theme, State = state, Width = width, Height = height, Image = smoke ? null : outputPath,
            Profile = ActiveProfileDirectory, Controls = controls.Count, StateEvidence = stateEvidence, ContrastRatios = contrasts,
            FontEvidence = Descendants(root).OfType<TextBlock>()
                .Where(text => VisibleInPreview(text, root) && text.Text.Any(char.IsAsciiLetter)).Take(16)
                .Select(text => new
                {
                    text.Text, Family = text.FontFamily.Source, Weight = text.FontWeight.ToString(),
                    ResolvedFamilies = GlyphDrawings(VisualTreeHelper.GetDrawing(text))
                        .SelectMany(drawing => drawing.GlyphRun.GlyphTypeface.Win32FamilyNames.Values).Distinct().ToArray()
                }).ToArray(),
            InputBoundary = "Offscreen template rendering only; no OS keyboard, mouse, popup opening, Steam/Explorer action or network request.",
            FocusBoundary = "Focus indicators are rendered from template parts/tokens; actual keyboard focus and navigation are not verified."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RunBatch(string directory, bool smoke)
    {
        Directory.CreateDirectory(directory);
        var results = new List<object>();
        var failures = 0;
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var mode in MainViews.Concat(OtherViews))
        foreach (var size in MainViews.Contains(mode) ? new[] { (1360, 900), (1120, 760) } : new[] { (1180, 800) })
        {
            var output = Path.Combine(directory, $"{mode}-{theme.ToLowerInvariant()}-{size.Item1}x{size.Item2}.png");
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the UiCapture process.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = SourceRoot() };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach (var argument in new[] { output, smoke ? mode + "-smoke" : mode, size.Item1.ToString(), size.Item2.ToString(), theme }) start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start isolated UiCapture child.");
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            var timedOut = !child.WaitForExit(60_000);
            if (timedOut) { child.Kill(entireProcessTree: true); child.WaitForExit(); }
            if (timedOut || child.ExitCode != 0) failures++;
            results.Add(new { Mode = mode, Theme = theme, Width = size.Item1, Height = size.Item2, Output = output, ExitCode = child.ExitCode, TimedOut = timedOut, Stdout = stdout.GetAwaiter().GetResult(), Stderr = stderr.GetAwaiter().GetResult() });
            Console.WriteLine($"{mode} {theme} {size.Item1}x{size.Item2}: {(timedOut ? "timeout" : child.ExitCode == 0 ? "passed" : "failed")}");
        }
        File.WriteAllText(Path.Combine(directory, "matrix.json"), JsonSerializer.Serialize(new { Failures = failures, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        return null;
    }

    private static void VerifyPersistenceErrorUi(MainWindow window, MainViewModel model, IsolatedPreviewProfile profile,
        string outputPath, int width, int height, string theme)
    {
        var root = (FrameworkElement)window.FindName("RootBorder");
        var size = new Size(width, height);
        void Layout() { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
        void Until(Func<bool> ready)
        {
            var timer = Stopwatch.StartNew();
            while (!ready() && timer.ElapsedMilliseconds < 5000) PumpPreview(20, Layout);
            if (!ready()) throw new InvalidOperationException($"The UI persistence operation did not settle: {model.StatusText}");
        }
        void OnUi(Action action)
        {
            var previous = System.Threading.SynchronizationContext.Current;
            try
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
                action();
            }
            finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previous); }
        }
        void Run(Func<Task> action)
        {
            Task? task = null;
            OnUi(() => task = action());
            Until(() => task!.IsCompleted);
            task!.GetAwaiter().GetResult();
        }
        void Invoke(string method, object? sender = null) => OnUi(() => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, sender == null ? null : new object?[] { sender, new RoutedEventArgs() }));
        Run(() => profile.QuickLaunch.AddAsync("测试快捷入口", Path.Combine(profile.Directory, "missing.exe"), iconPath: ""));
        var item = profile.QuickLaunch.GetAll().Single();
        Invoke("SwitchToQuickLaunch");
        Layout();
        ToggleButton Pin() => Descendants(root).OfType<ToggleButton>().Single(button => Equals(button.Tag, item.Id));
        void ClickPin() => OnUi(() => typeof(ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Pin(), null));
        using (var locked = new FileStream(Path.Combine(profile.Directory, "quicklaunch.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            model.StatusText = "等待固定测试";
            ClickPin();
            Until(() => model.StatusText.StartsWith("固定快速启动项失败"));
            if (profile.QuickLaunch.GetById(item.Id)!.IsPinned || model.QuickLaunchList.Single().IsPinned || Pin().IsChecked != false)
                throw new InvalidOperationException("A failed pin operation published a false state.");
        }
        ClickPin();
        Until(() => profile.QuickLaunch.GetById(item.Id)!.IsPinned && model.QuickLaunchList.Single().IsPinned);
        var game = model.GameList.First();
        var original = model.Accounts.First();
        var target = model.Accounts.First(account => account.SteamId != original.SteamId);
        Run(() => model.SaveGameBinding(game.AppId, game.GameName, original.SteamId, original.DisplayName));
        model.SelectedGame = game;
        model.SelectedBindingAccount = target;
        Invoke("SwitchToGames");
        using (var locked = new FileStream(profile.Bindings.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            model.StatusText = "等待绑定测试";
            Invoke("GameSaveBinding_Click", window);
            Until(() => model.StatusText.StartsWith("保存绑定失败"));
            if (profile.Bindings.GetBinding(game.AppId)!.AccountSteamId != original.SteamId || game.BindingAccountSteamId != original.SteamId)
                throw new InvalidOperationException("A failed binding edit changed the displayed or stored account.");
            PumpPreview(450, Layout);
            CaptureVisual(root, width, height, outputPath, "persistence-error", theme, "default", false);
        }
        Invoke("GameSaveBinding_Click", window);
        Until(() => model.StatusText.StartsWith("已绑定") && game.BindingAccountSteamId == target.SteamId);
        if (profile.Bindings.GetBinding(game.AppId)!.AccountSteamId != target.SteamId)
            throw new InvalidOperationException("The corrected binding failed to persist after the file was unlocked.");
        Console.WriteLine("Persistence UI passed: actual pin/binding handlers report locked-file errors, preserve old state and recover after unlock; no game or Steam process launched.");
    }

    private static void VerifyBackupFeedback(MainWindow window, MainViewModel model, int width, int height,
        IsolatedPreviewProfile profile, string outputPath, string theme)
    {
        var root = (FrameworkElement)window.FindName("RootBorder");
        var size = new Size(width, height);
        void Layout() { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
        void Navigate(string method) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Button Button(string name) => (Button)window.FindName(name);
        void AssertVisible(string name)
        {
            var control = (FrameworkElement)window.FindName(name);
            var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            if (control.Visibility != Visibility.Visible || bounds.Width < 20 || bounds.Left < 0 || bounds.Right > width || bounds.Top < 0 || bounds.Bottom > height)
                throw new InvalidOperationException($"Backup feedback is not visible: {name}; {control.Visibility}, bounds={bounds}, recent={model.HasRecentBackupExport}.");
        }
        Navigate("SwitchToSettings");
        model.BackupExportDirectory = Path.Combine(profile.Directory, "backups");
        model.SaveSettingsNow();
        Layout();
        if (Button("ToastActionButton").Visibility != Visibility.Collapsed) throw new InvalidOperationException("A backup action is visible before an export exists.");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> export;
        var previousContext = System.Threading.SynchronizationContext.Current;
        try
        {
            // Match the production dispatcher context so async binding updates are
            // committed on the UI thread before asserting the rendered geometry.
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            export = model.ExportBackupAsync(BackupExportKind.Accounts, "accounts.json", async path =>
            {
                await release.Task;
                await File.WriteAllTextAsync(path, "[]");
            }, _ => throw new InvalidOperationException("An automatic backup opened a path prompt."));
        }
        finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext); }
        Layout();
        foreach (var name in new[] { "BackupAccountsButton", "SelectBackupExportDirectoryButton", "ExportSettingsButton", "ExportConfigurationBundleButton" })
            if (Button(name).IsEnabled) throw new InvalidOperationException("Export controls remain enabled while a backup is writing.");
        if (!model.IsBackupExporting || !model.StatusText.Contains("正在保存备份")) throw new InvalidOperationException("Export has no busy feedback.");
        release.SetResult();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!export.IsCompleted && watch.ElapsedMilliseconds < 5000) PumpPreview(20, Layout);
        if (!export.IsCompleted) throw new TimeoutException("The isolated backup did not finish within 5 seconds.");
        var saved = export.GetAwaiter().GetResult()!;
        PumpPreview(30, Layout);
        AssertVisible("RecentBackupExportCard");
        AssertVisible("RevealRecentBackupExportButton");
        AssertVisible("ToastHost");
        AssertVisible("ToastActionButton");
        if (!Equals(Button("ToastActionButton").Content, "定位备份"))
            throw new InvalidOperationException("Toast did not offer the reveal-backup action.");
        if (((TextBlock)window.FindName("RecentBackupExportFileLabel")).Text != Path.GetFileName(saved) || !Button("BackupAccountsButton").IsEnabled)
            throw new InvalidOperationException("The completed backup lost its file identity or left controls disabled.");
        profile.CanSaveSettings = false;
        model.MinimizeToTray = !model.MinimizeToTray;
        if (model.SaveSettingsNow()) throw new InvalidOperationException("The simulated settings failure unexpectedly succeeded.");
        Layout();
        AssertVisible("RetrySettingsSaveButton");
        AssertVisible("ToastHost");
        AssertVisible("ToastActionButton");
        if (!Equals(Button("ToastActionButton").Content, "立即重试"))
            throw new InvalidOperationException("Toast did not switch to the retry-save action on failure.");
        if (!((TextBlock)window.FindName("SettingsSaveStateLabel")).Text.Contains("保存失败")) throw new InvalidOperationException("A failed write still claims settings are saved.");
        if (((TextBlock)window.FindName("ToastText")).ActualWidth < 200)
            throw new InvalidOperationException("Retry and reveal actions squeezed the failure feedback too far.");
        PumpPreview(450, Layout); // Capture the settled page, after its navigation fade.
        CaptureVisual(root, width, height, Path.ChangeExtension(outputPath, ".save-error.png"), "backup-save-error", theme, "default", false);
        profile.CanSaveSettings = true;
        Button("RetrySettingsSaveButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Layout();
        if (model.HasSettingsSaveError || model.HasUnsavedSettings || Button("RetrySettingsSaveButton").Visibility != Visibility.Collapsed || Equals(Button("ToastActionButton").Content, "立即重试"))
            throw new InvalidOperationException("The actual retry button did not clear the failed save state.");
        if (((TextBlock)window.FindName("ToastText")).ActualWidth < 200)
            throw new InvalidOperationException("Backup actions squeezed feedback below the supported readable width.");
        CaptureVisual(root, width, height, outputPath, "backup-feedback", theme, "default", false);
        File.Delete(saved);
        // The real reveal handler must report a missing file and stop before launching Explorer.
        Button("ToastActionButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Layout();
        if (model.HasRecentBackupExport || Button("ToastActionButton").Visibility != Visibility.Collapsed || !model.StatusText.Contains("已移动、删除或无法访问"))
            throw new InvalidOperationException("A missing backup left a stale reveal action enabled.");
        Console.WriteLine($"Backup feedback passed ({width}x{height}): busy gating, visible recent file/reveal actions, settings failure/retry, readable footer and missing-file handling; no Explorer or clipboard operation executed.");
    }

    private static void VerifySettingsBackupGuidance(MainWindow window, MainViewModel model, int width, int height, string profileDirectory)
    {
        void Navigate(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        var root = (FrameworkElement)window.FindName("RootBorder");
        var size = new Size(width, height);
        void Layout() { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
        Navigate("SwitchToSettings");
        Layout();
        var tabs = (TabControl)window.FindName("SettingsTabs");
        var general = (TabItem)window.FindName("GeneralSettingsTab");
        var advanced = (TabItem)window.FindName("AdvancedSettingsTab");
        if (tabs.Items.Count != 2 || tabs.SelectedItem != general) throw new InvalidOperationException("Settings do not start in the general category.");
        foreach (var name in new[] { "BackupExportSettingsCard", "SelectBackupExportDirectoryButton", "BackupExportDirectoryBox", "AskBeforeBackupExportCheckBox" })
        {
            var control = (FrameworkElement)window.FindName(name);
            var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            if (bounds.Width < 20 || bounds.Left < 0 || bounds.Right > width || bounds.Top < 0 || bounds.Bottom > height)
                throw new InvalidOperationException($"The default backup location is not accessible at {width}x{height}: {name}.");
        }
        var directory = Path.Combine(profileDirectory, "备份导出目录", "a-fairly-long-folder-name-for-layout-verification");
        model.BackupExportDirectory = directory;
        ((CheckBox)window.FindName("AskBeforeBackupExportCheckBox")).IsChecked = true;
        if (!model.AskBeforeBackupExport) throw new InvalidOperationException("The ask-for-location option did not update the live setting.");
        model.AskBeforeBackupExport = false;
        Layout();
        if (((TextBox)window.FindName("BackupExportDirectoryBox")).Text != directory || !model.SaveSettingsNow())
            throw new InvalidOperationException("The selected backup directory failed to bind or save.");
        for (var index = 0; index < 3; index++)
        {
            tabs.SelectedItem = advanced;
            Layout();
            if (!((TextBlock)window.FindName("SettingsCategoryHint")).Text.StartsWith("高级设置")) throw new InvalidOperationException("Advanced category guidance did not follow selection.");
            tabs.SelectedItem = general;
            Layout();
        }
        if (model.BackupExportDirectory != directory || model.AskBeforeBackupExport) throw new InvalidOperationException("Switching categories reset settings.");
        void Backup(string buttonName, string success)
        {
            model.StatusText = "等待备份验证";
            ((Button)window.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!model.StatusText.StartsWith(success) && deadline.ElapsedMilliseconds < 5000) PumpPreview(20, Layout);
            if (!model.StatusText.StartsWith(success)) throw new InvalidOperationException($"The actual backup action failed: {model.StatusText}");
        }
        Navigate("SwitchToAccounts");
        Backup("BackupAccountsButton", "账号库已备份");
        Backup("BackupAccountsButton", "账号库已备份");
        if (Directory.GetFiles(Path.Combine(directory, "Accounts"), "*.json").Length != 2)
            throw new InvalidOperationException("Repeated account backups overwrote history or saved to the wrong location.");
        Navigate("SwitchToSettings");
        tabs.SelectedItem = advanced;
        Layout();
        Backup("ExportSettingsButton", "设置已备份");
        Backup("ExportConfigurationBundleButton", "应用配置包已备份");
        var config = Directory.GetFiles(Path.Combine(directory, "Configuration"), "*.steamswitch.json").Single();
        var document = JsonDocument.Parse(File.ReadAllText(config));
        if (document.RootElement.GetProperty("Settings").GetProperty("BackupExportDirectory").GetString() != directory)
            throw new InvalidOperationException("The exported configuration lost the backup policy.");
        typeof(MainWindow).GetMethod("ResetBackupExportDirectory_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object?[] { window, null });
        if (model.BackupExportDirectory != "" || model.BackupExportDirectoryDisplay != BackupExportService.DefaultDirectory || !File.Exists(config))
            throw new InvalidOperationException("Resetting the default directory lost earlier backups.");
        Console.WriteLine($"Settings/backup guidance passed ({width}x{height}): visible default location, general/advanced switching, long path binding, persisted policy, actual account/settings/configuration backup buttons without dialogs, preserved history and reset; isolated profile only.");
    }

    private static ContextMenu DetachSubmenuForCapture(MenuItem parent)
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(FrameworkElement.StyleProperty, "ModernContextMenu");
        while (parent.Items.Count > 0)
        {
            var item = parent.Items[0];
            parent.Items.RemoveAt(0);
            menu.Items.Add(item);
        }
        return menu;
    }

    private static void VerifyContextMenus(MainViewModel model, AccountManager manager, string outputPath, string theme)
    {
        MenuItem ByTag(ItemsControl parent, string tag) => parent.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, tag));
        void Click(MenuItem item)
        {
            typeof(MenuItem).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(item, null);
            PumpPreview(40, () => { });
        }
        void Measure(ContextMenu menu)
        {
            menu.Width = 310;
            menu.Measure(new Size(310, 800));
            menu.Arrange(new Rect(new Point(), menu.DesiredSize));
            menu.UpdateLayout();
            if (menu.ActualHeight < 50 || menu.ActualHeight > (double)Application.Current.Resources["MenuMaxHeight"] + 34)
                throw new InvalidOperationException("The menu is empty or exceeds its scrollable height.");
            foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.IsEnabled))
                if (!item.Focusable || item.Template?.FindName("Row", item) == null)
                    throw new InvalidOperationException("An actionable menu item lost keyboard support or the shared template.");
            if (menu.ItemContainerStyle != null) throw new InvalidOperationException("A MenuItem-only container style may break Separator containers.");
        }
        var band = new TaskbarBandWindow(manager);
        band.SetViewModel(model);
        band.SetLyricsSettings(false, true, true, 320, hideWhenNoMedia: false);
        band.SetPreviewLyricsEnabled(true);
        var build = typeof(TaskbarBandWindow).GetMethod("BuildContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var menu = (ContextMenu)build.Invoke(band, null)!;
        Measure(menu);
        var unpinned = model.Accounts.First(account => !account.IsPinned && !account.IsCurrent);
        string? switched = null;
        band.AccountSwitchRequested += (_, account) => switched = account.SteamId;
        Click(ByTag(ByTag(menu, "Accounts"), unpinned.SteamId));
        if (switched != unpinned.SteamId) throw new InvalidOperationException("An unpinned taskbar-menu account silently did nothing.");
        var actual = model.Accounts.First(account => account.IsCurrent);
        if (!ByTag(ByTag(menu, "Accounts"), actual.SteamId).IsChecked || ByTag(ByTag(menu, "Accounts"), unpinned.SteamId).IsChecked)
            throw new InvalidOperationException("The current marker follows selection instead of the active account.");
        band.SetPreviewMedia(new TaskbarMediaSnapshot(false, false, "", "", "系统媒体", null));
        var withoutMedia = (ContextMenu)build.Invoke(band, null)!;
        if (ByTag(ByTag(withoutMedia, "Tools"), "RetryLyrics").IsEnabled)
            throw new InvalidOperationException("Retry lyrics is enabled without controllable media.");
        var desktop = new DesktopFloatingWindow(manager);
        desktop.SetViewModel(model);
        desktop.AccountSwitchRequested += (_, account) => switched = account.SteamId;
        var desktopMenu = (ContextMenu)typeof(DesktopFloatingWindow).GetMethod("BuildContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(desktop, null)!;
        switched = null;
        Click(ByTag(ByTag(desktopMenu, "Accounts"), unpinned.SteamId));
        if (switched != unpinned.SteamId) throw new InvalidOperationException("An unpinned desktop-menu account silently did nothing.");
        Measure(desktopMenu);

        for (var index = 0; index < 100; index++) model.Accounts.Add(new AccountViewModel(new SteamAccount
        {
            SteamId = (76561199100000000L + index).ToString(), AccountName = $"saved_menu_{index:D3}",
            PersonaName = index == 0 ? new string('长', 140) : $"本地账号 {index:D3}", IsArchived = true
        }));
        bool? toggled = null;
        var large = SharedContextMenuBuilder.Build(model.Accounts, unpinned, () => { }, _ => { }, () => { }, () => { }, null,
            toggleDesktopFloating: value => toggled = value, toggleDesktopFloatingTopmost: _ => { },
            isDesktopFloatingEnabled: false, exit: () => { });
        Measure(large);
        if (large.Items.Count > 10) throw new InvalidOperationException("Accounts expanded the root menu instead of a submenu.");
        var windows = ByTag(large, "Windows").Items.OfType<MenuItem>().ToArray();
        if (windows[1].IsEnabled || windows[0].IsChecked) throw new InvalidOperationException("Unavailable topmost or toggle state is misleading.");
        Click(windows[0]);
        if (toggled != true) throw new InvalidOperationException("A checkable menu action failed to report the new toggle state.");
        var accountsMenu = DetachSubmenuForCapture(ByTag(large, "Accounts"));
        Measure(accountsMenu);
        var scroller = FindDescendant<ScrollViewer>(accountsMenu)!;
        if (scroller.ScrollableHeight <= 0) throw new InvalidOperationException("A 100-account submenu cannot scroll.");
        scroller.ScrollToEnd();
        accountsMenu.UpdateLayout();
        if (scroller.VerticalOffset <= 0) throw new InvalidOperationException("Scrolling does not expose the end of the account list.");
        var last = model.Accounts.Last();
        if (ByTag(accountsMenu, last.SteamId).ToolTip is not string hint || !hint.Contains("验证码"))
            throw new InvalidOperationException("Saved accounts lost recovery guidance.");
        scroller.ScrollToHome();
        accountsMenu.UpdateLayout();
        CaptureVisual(accountsMenu, 310, (int)Math.Ceiling(accountsMenu.ActualHeight), outputPath, "context-menu-100", theme, "default", false);
        var empty = SharedContextMenuBuilder.Build(null, null, () => { }, null, null, null, null, exit: () => { });
        Measure(empty);
        if (empty.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "Accounts") || Equals(item.Tag, "Windows")))
            throw new InvalidOperationException("An empty menu contains empty submenus.");
        band.Close();
        desktop.Close();
        Console.WriteLine("Context menus passed: shared templates/separators, bounded 100-account scrolling, long names, current/recovery states, native checkable actions, disabled media/topmost and unpinned account callbacks on both floating surfaces. No Steam login or OS input executed.");
    }

    private static void VerifyAccountLibraryGuidance(MainWindow window, MainViewModel model, int width, int height)
    {
        AccountViewModel? lastSaved = null;
        for (var index = 0; index < 100; index++)
        {
            lastSaved = new AccountViewModel(new SteamAccount
            {
                SteamId = (76561199000000000L + index).ToString(), AccountName = $"stored_{index:D3}",
                PersonaName = $"本地玩家 {index:D3}", IsArchived = true
            });
            model.Accounts.Add(lastSaved);
        }
        var root = (FrameworkElement)window.FindName("RootBorder");
        var size = new Size(width, height);
        void Layout() { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
        var list = (ListBox)window.FindName("AccountListBox");
        var recovery = (Button)window.FindName("ShowRecoveryAccountsButton");
        var summary = (TextBlock)window.FindName("AccountResultsSummary");
        var empty = (FrameworkElement)window.FindName("AccountResultsEmptyState");
        var clearSearch = (Button)window.FindName("ClearAccountSearchButton");
        void Filter(string mode) => typeof(MainWindow).GetMethod("ApplyAccountFilterMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { mode });
        Layout();
        foreach (var name in new[] { "AccountLibraryHero", "CollectAccountsButton", "ImportAccountsButton", "BackupAccountsButton", "ShowRecoveryAccountsButton", "ShowAllSavedAccountsButton" })
        {
            var control = (FrameworkElement)window.FindName(name);
            var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            if (control.Visibility != Visibility.Visible || bounds.Width < 20 || bounds.Left < 0 || bounds.Right > width || bounds.Top < 0 || bounds.Bottom > height)
                throw new InvalidOperationException($"The account-library entry is not visible: {name}.");
        }
        if (model.Accounts.Count != model.SteamListedAccountCount + model.ArchivedAccountCount || !recovery.IsEnabled)
            throw new InvalidOperationException("Library counts or recovery availability are incorrect.");
        var before = model.GetAccountManager().CurrentAccount;
        var search = (TextBox)window.FindName("AccountSearchBox");
        typeof(MainWindow).GetMethod("AccountSearchBox_GotFocus", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object?[] { search, null });
        foreach (var query in new[] { lastSaved!.Username, lastSaved.SteamId, lastSaved.DisplayName })
        {
            search.Text = query;
            Layout();
            if (list.Items.Count != 1 || !ReferenceEquals(list.Items[0], lastSaved))
                throw new InvalidOperationException("A saved account among 100 historical entries could not be found by login name, Steam ID or nickname.");
            if (!summary.Text.Contains("搜索结果 1 个") || empty.Visibility != Visibility.Collapsed || clearSearch.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Search feedback did not show the result count and clear action.");
        }
        search.Text = "not-a-matching-saved-account";
        Layout();
        if (list.Items.Count != 0) throw new InvalidOperationException("Account search did not filter the library.");
        if (empty.Visibility != Visibility.Visible || !summary.Text.Contains("搜索结果 0 个") || model.SelectedAccount != null || ((Button)window.FindName("SelectedAccountActionButton")).IsEnabled)
            throw new InvalidOperationException("Empty search lacks guidance or leaves an invisible account actionable.");
        clearSearch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        if (list.Items.Count != model.Accounts.Count || search.Text.Length != 0 || clearSearch.Visibility != Visibility.Collapsed || empty.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Clearing search failed to restore results.");
        search.Text = "not-a-matching-saved-account";
        recovery.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        if (list.Items.Count != model.ArchivedAccountCount || list.Items.Cast<AccountViewModel>().Any(account => !account.IsArchived) ||
            model.SelectedAccount?.IsArchived != true || ((TextBlock)window.FindName("SelectedAccountActionLabel")).Text != "找回并打开 Steam")
            throw new InvalidOperationException("Recovery entry failed to clear search, filter saved-only accounts and reveal the recovery action.");
        if (!ReferenceEquals(before, model.GetAccountManager().CurrentAccount))
            throw new InvalidOperationException("Viewing recoverable accounts unexpectedly switched the active account.");
        ((Button)window.FindName("ShowAllSavedAccountsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        if (list.Items.Count != model.Accounts.Count) throw new InvalidOperationException("Show-all left accounts hidden behind an old filter/search.");
        Filter("Pinned");
        foreach (var account in model.Accounts.ToArray()) account.IsPinned = false;
        PumpPreview(60, Layout);
        if (list.Items.Count != 0 || empty.Visibility != Visibility.Visible || !((TextBlock)window.FindName("AccountResultsEmptyHint")).Text.Contains("星标"))
            throw new InvalidOperationException("Unpinning accounts failed to update the active filter and guidance.");
        lastSaved!.IsPinned = true;
        PumpPreview(60, Layout);
        if (list.Items.Count != 1 || !ReferenceEquals(list.Items[0], lastSaved) || empty.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Pinning an account failed to update the active filter.");
        ((Button)window.FindName("ResetAccountFiltersButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        if (list.Items.Count != model.Accounts.Count) throw new InvalidOperationException("Empty-state reset did not restore all accounts.");
        typeof(MainWindow).GetMethod("AccountSortOption_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object?[] { new MenuItem { Tag = "Pinned" }, null });
        PumpPreview(60, Layout);
        if (!ReferenceEquals(list.Items[0], lastSaved)) throw new InvalidOperationException("Pinned-first sorting did not promote the pinned account.");
        var newlyPinned = model.Accounts.First(account => !ReferenceEquals(account, lastSaved));
        lastSaved.IsPinned = false;
        newlyPinned.IsPinned = true;
        PumpPreview(60, Layout);
        if (!ReferenceEquals(list.Items[0], newlyPinned)) throw new InvalidOperationException("Pinned-first sorting did not follow a pin change.");
        foreach (var archived in model.Accounts.Where(account => account.IsArchived).ToArray()) model.Accounts.Remove(archived);
        Layout();
        if (recovery.IsEnabled || model.ArchivedAccountCount != 0)
            throw new InvalidOperationException("The recovery entry is enabled when no saved-only accounts exist.");
        model.Accounts.Clear();
        Layout();
        if (empty.Visibility != Visibility.Visible || ((FrameworkElement)window.FindName("EmptyCollectAccountsButton")).Visibility != Visibility.Visible ||
            ((FrameworkElement)window.FindName("ResetAccountFiltersButton")).Visibility != Visibility.Collapsed || !summary.Text.Contains("显示 0 个 / 本地收录 0 个"))
            throw new InvalidOperationException("A new account library lacks first-use guidance or shows stale counts.");
        Console.WriteLine($"Account library guidance passed ({width}x{height}): 100 searchable records, result counts, no-match guidance, selection safety, clear/reset, live pin filtering, empty-library guidance and recovery entry; no login or export performed.");
    }

    private static void VerifyMainGuidance(MainWindow window, MainViewModel model, int width, int height)
    {
        var root = (FrameworkElement)window.FindName("RootBorder");
        var size = new Size(width, height);
        void Layout() { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
        TextBlock Text(string name) => (TextBlock)window.FindName(name);
        var action = (Button)window.FindName("SelectedAccountActionButton");
        var current = model.Accounts.First(account => account.IsCurrent && !account.IsArchived);
        var target = model.Accounts.First(account => !account.IsCurrent && !account.IsArchived);
        var archived = model.Accounts.First(account => account.IsArchived);
        foreach (var (account, expected) in new[] { (current, "打开当前账号的 Steam"), (target, "切换账号并打开 Steam"), (archived, "找回并打开 Steam") })
        {
            model.SelectedAccount = account;
            Layout();
            if (Text("SelectedAccountActionLabel").Text != expected || !action.IsEnabled)
                throw new InvalidOperationException("Selected account action did not follow the current/target/archived state.");
        }
        model.SelectedAccount = current;
        Layout();
        if (model.SelectedAccountBoundGameCount != 1)
            throw new InvalidOperationException("The account details show all-library bindings instead of the selected account's bindings.");
        model.SelectedAccount = archived;
        Layout();
        if (model.SelectedAccountBoundGameCount != 0 || !Text("SelectedAccountActionHint").Text.Contains("验证码"))
            throw new InvalidOperationException("Archived account details lost the re-login hint or show another account's games.");
        model.IsLoading = true;
        model.StatusText = "正在处理测试操作";
        Layout();
        if (action.IsEnabled || Text("OperationBusyText").Text != model.StatusText)
            throw new InvalidOperationException("Busy state did not disable the primary action or update feedback.");
        model.IsLoading = false;
        model.SelectedAccount = null;
        Layout();
        if (action.IsEnabled) throw new InvalidOperationException("An unselected account still exposes an enabled action.");
        model.SelectedAccount = target;
        Layout();
        var intro = (FrameworkElement)window.FindName("AccountPageIntro");
        var archiveButton = (FrameworkElement)window.FindName("AccountArchiveButton");
        var introBounds = intro.TransformToAncestor(root).TransformBounds(new Rect(intro.RenderSize));
        var archiveBounds = archiveButton.TransformToAncestor(root).TransformBounds(new Rect(archiveButton.RenderSize));
        if (introBounds.Right > archiveBounds.Left)
            throw new InvalidOperationException("Account guidance overlaps the archive button.");
        if (Text("ToastText").ActualWidth < 200 || ((FrameworkElement)window.FindName("ToastHost")).Visibility != Visibility.Visible)
            throw new InvalidOperationException("Operation feedback has too little space at the supported minimum window width.");

        typeof(MainWindow).GetMethod("SwitchToGames", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Layout();
        if (((FrameworkElement)window.FindName("GameAccountList")).ActualHeight < 54)
            throw new InvalidOperationException("The game binding actions leave no complete account row visible.");

        typeof(MainWindow).GetMethod("SwitchToTaskbarLyrics", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        foreach (var enabled in new[] { false, true, false })
        {
            model.TaskbarLyricsEnabled = enabled;
            Layout();
            if (Text("LyricsEnabledStateText").Text != (enabled ? "功能已开启" : "尚未启用"))
                throw new InvalidOperationException("The lyrics preview claims an active media session or fails to reflect its enable switch.");
        }
        Console.WriteLine($"Main guidance passed ({width}x{height}): target/current/archived labels, selected-account counts, busy/empty gating, feedback binding, header bounds, and lyrics enable-state feedback.");
    }

    private sealed class LayoutCounter : Decorator
    {
        public int Measures;
        public int Arranges;
        protected override Size MeasureOverride(Size constraint) { Measures++; return base.MeasureOverride(constraint); }
        protected override Size ArrangeOverride(Size size) { Arranges++; return base.ArrangeOverride(size); }
    }

    private static void PumpPreview(int milliseconds, Action sample)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var watch = Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(8) };
        timer.Tick += (_, _) =>
        {
            sample();
            if (watch.ElapsedMilliseconds >= milliseconds) { timer.Stop(); frame.Continue = false; }
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void VerifyHoverExit(AccountManager manager, string artwork, string output, string theme)
    {
        Point? cursor = null;
        var band = new TaskbarBandWindow(manager, () => cursor) { SuppressAutoEmbed = true };
        var evidence = new List<object>();
        try
        {
            band.SetLyricsSettings(false, true, false, 360, showVisualizer: false, adaptiveWidth: false,
                transparentBackground: false, hideWhenNoMedia: false);
            band.SetPreviewLyricsEnabled(true);
            band.SetPreviewMedia(new TaskbarMediaSnapshot(true, true, "移出时立即收起控制栏", "保留歌词与控制交互", "测试",
                File.ReadAllBytes(artwork), HasSyncedLyrics: true, CurrentLyric: "移出时立即收起控制栏", TrackKey: "hover-exit"));
            band.Left = -32000;
            band.Top = -32000;
            band.ShowActivated = false;
            band.Show();
            var naturalHeight = band.Height;
            var host = (FrameworkElement)band.FindName("LyricsHost");
            var controls = (FrameworkElement)band.FindName("LyricsControlsGlass");
            var enter = typeof(TaskbarBandWindow).GetMethod("LyricsHost_MouseEnter", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var leave = typeof(TaskbarBandWindow).GetMethod("LyricsHost_MouseLeave", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Invoke(MethodInfo method) => method.Invoke(band, new object?[] { host, null });
            foreach (var height in new[] { naturalHeight, 30d })
            {
                band.Height = height;
                PumpPreview(45, band.UpdateLayout);
                var bounds = band.GetLyricsScreenBounds() ?? throw new InvalidOperationException("Missing physical lyric bounds.");
                var dpi = VisualTreeHelper.GetDpi(band);
                if (bounds.Height > height * dpi.DpiScaleY + 2)
                    throw new InvalidOperationException("Hover bounds ignore the actual taskbar viewport scale.");
                var inside = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                var outside = new Point(bounds.Right + 10, bounds.Bottom + 10);
                cursor = inside;
                Invoke(enter);
                PumpPreview(220, band.UpdateLayout);
                if (!controls.IsHitTestVisible || controls.Opacity < 0.99)
                    throw new InvalidOperationException("Pointer inside the actual rendered area lost the controls.");
                Invoke(leave); // Tooltip/layout can emit leave without a real departure.
                PumpPreview(35, band.UpdateLayout);
                if (!controls.IsHitTestVisible || controls.Opacity < 0.99)
                    throw new InvalidOperationException("A false MouseLeave hid the controls while the physical cursor was still inside.");

                cursor = outside;
                var watch = Stopwatch.StartNew();
                Invoke(leave);
                if (controls.IsHitTestVisible)
                    throw new InvalidOperationException("Real MouseLeave waited for the hover probe before starting to hide.");
                double? hiddenMs = null;
                PumpPreview(180, () =>
                {
                    band.UpdateLayout();
                    if (hiddenMs == null && controls.Opacity <= 0.05) hiddenMs = watch.Elapsed.TotalMilliseconds;
                });
                if (hiddenMs == null || controls.Opacity > 0.01)
                    throw new InvalidOperationException("The controls did not finish their immediate-exit fade.");

                cursor = inside;
                Invoke(enter);
                PumpPreview(220, band.UpdateLayout);
                cursor = outside; // No MouseLeave event: verify watchdog recovery.
                watch.Restart();
                double? watchdogMs = null;
                PumpPreview(180, () =>
                {
                    band.UpdateLayout();
                    if (watchdogMs == null && controls.Opacity <= 0.05) watchdogMs = watch.Elapsed.TotalMilliseconds;
                });
                if (watchdogMs == null || controls.IsHitTestVisible)
                    throw new InvalidOperationException("A missing leave event left the controls stuck open.");

                cursor = inside;
                Invoke(enter);
                PumpPreview(30, band.UpdateLayout);
                cursor = outside;
                Invoke(leave);
                PumpPreview(100, band.UpdateLayout);
                if (controls.IsHitTestVisible || controls.Opacity > 0.01)
                    throw new InvalidOperationException("A short pointer visit left a long animation tail.");
                evidence.Add(new { ViewportHeight = height, ScreenBounds = bounds.ToString(), Dpi = dpi.DpiScaleY,
                    LeaveStateChangedImmediately = true, HiddenAfterMs = hiddenMs, WatchdogHiddenAfterMs = watchdogMs,
                    FalseLeave = "held", FastExit = "passed" });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var json = JsonSerializer.Serialize(new { Theme = theme, Evidence = evidence,
                Boundary = "Production enter/leave handlers and actual WPF screen transforms, using an injected cursor in an isolated offscreen window; no real mouse movement or desktop frame-rate measurement." }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(output + ".json", json);
            Console.WriteLine(json);
        }
        finally { band.Close(); }
    }

    private static void ProfileLyricsHover(AccountManager manager, string artwork, string output, string theme)
    {
        var band = new TaskbarBandWindow(manager) { SuppressAutoEmbed = true };
        try
        {
            band.SetLyricsSettings(false, true, false, 360, showVisualizer: false, adaptiveWidth: false,
                transparentBackground: false, hideWhenNoMedia: false);
            band.SetPreviewLyricsEnabled(true);
            band.SetPreviewMedia(new TaskbarMediaSnapshot(true, true, "让歌词保持可读，控制条平滑地展开", "稳定的翻译副行", "测试",
                File.ReadAllBytes(artwork), HasSyncedLyrics: true, CurrentLyric: "让歌词保持可读，控制条平滑地展开", TrackKey: "hover-profile"));
            band.SetPreviewControlsVisible(false);
            var root = (FrameworkElement)band.Content;
            var text = (FrameworkElement)band.FindName("LyricsTextPanel");
            var controls = (FrameworkElement)band.FindName("LyricsControlsGlass");
            var parent = (Grid)text.Parent;
            var index = parent.Children.IndexOf(text);
            parent.Children.RemoveAt(index);
            var counter = new LayoutCounter { Child = text, VerticalAlignment = VerticalAlignment.Center };
            parent.Children.Insert(index, counter);
            // A presentation source is required for WPF's animation clock.
            // Keep the isolated test window offscreen and never activate it.
            band.Left = -32000;
            band.Top = -32000;
            band.ShowActivated = false;
            band.Show();
            var size = new Size(band.Width, band.Height);
            void Layout() { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
            Layout();
            PumpPreview(40, Layout);
            var show = typeof(TaskbarBandWindow).GetMethod("ShowLyricsControls", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(FrameworkElement.MarginProperty, typeof(FrameworkElement));
            var marginChanges = 0;
            EventHandler changed = (_, _) => marginChanges++;
            descriptor.AddValueChanged(text, changed);
            var evidence = new List<object>();
            foreach (var visible in new[] { true, false, true, false })
            {
                counter.Measures = counter.Arranges = marginChanges = 0;
                show.Invoke(band, new object[] { visible, true });
                var opacities = new List<double>();
                PumpPreview(280, () => { Layout(); opacities.Add(controls.Opacity); });
                evidence.Add(new { Visible = visible, counter.Measures, counter.Arranges, MarginChanges = marginChanges,
                    Samples = opacities.Count, OpacityMin = opacities.Min(), OpacityMax = opacities.Max(), FinalOpacity = controls.Opacity });
                if (Math.Abs(controls.Opacity - (visible ? 1 : 0)) > 0.01 || controls.IsHitTestVisible != visible)
                    throw new InvalidOperationException("Hover animation failed to settle to its requested state.");
                if (counter.Measures > 2 || counter.Arranges > 2 || marginChanges > 2)
                    throw new InvalidOperationException("Hover animation repeatedly remeasured/rearranged the lyric text.");
            }
            var translation = (TranslateTransform)band.FindName("LyricsTranslate");
            foreach (var visible in new[] { true, false, true, false, true, false })
            {
                var previousRight = translation.X - text.Margin.Right;
                var previousOpacity = controls.Opacity;
                show.Invoke(band, new object[] { visible, true });
                if (Math.Abs(translation.X - text.Margin.Right - previousRight) > 0.1 ||
                    Math.Abs(controls.Opacity - previousOpacity) > 0.01)
                    throw new InvalidOperationException($"Reversal {visible}: text {previousRight:F3} -> {translation.X - text.Margin.Right:F3}, opacity {previousOpacity:F3} -> {controls.Opacity:F3}.");
                var marginBeforeDuplicate = marginChanges;
                show.Invoke(band, new object[] { visible, true });
                if (marginChanges != marginBeforeDuplicate)
                    throw new InvalidOperationException("Duplicate hover events restarted text layout.");
                PumpPreview(40, Layout);
            }
            PumpPreview(200, Layout);
            if (controls.Opacity > 0.01 || Math.Abs(translation.X) > 0.01 || text.Margin.Right != 0)
                throw new InvalidOperationException("Rapid pointer reversals left a stale control/text offset.");

            ((FrameworkElement)band.FindName("LyricsHost")).Width = 160;
            Layout();
            show.Invoke(band, new object[] { true, true });
            PumpPreview(220, Layout);
            if (text.Opacity < 0.29 || text.Margin.Right != 0 || !controls.IsHitTestVisible)
                throw new InvalidOperationException("Narrow lyrics disappeared or lost control interaction.");
            show.Invoke(band, new object[] { false, true });
            PumpPreview(200, Layout);
            if (text.Opacity != 1 || controls.Opacity > 0.01)
                throw new InvalidOperationException("Narrow lyric opacity did not restore after hover.");
            descriptor.RemoveValueChanged(text, changed);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var json = JsonSerializer.Serialize(new { Theme = theme, Evidence = evidence,
                ReversalChecks = 6, DuplicateEvents = 6, NarrowLyrics = "passed",
                Boundary = "Isolated offscreen WPF window animation and layout sampling; these are layout counts, not desktop frame-rate measurements." }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(output + ".json", json);
            Console.WriteLine(json);
        }
        finally { band.Close(); }
    }

    private static void VerifyFloatingClipping(AccountManager manager, string artwork)
    {
        // Verify actual WPF layout after the host constrains the viewport, as
        // Explorer does when configured icons exceed the available height.
        var checks = 0;
        foreach (var iconSize in new[] { 16, 24, 38, 48, 72, 96 })
        foreach (var padding in new[] { 2, 8, 30 })
        foreach (var rounded in new[] { true, false })
        {
            var band = new TaskbarBandWindow(manager) { SuppressAutoEmbed = true };
            try
            {
                band.SetLyricsSettings(false, true, false, 320, showVisualizer: false,
                    adaptiveWidth: false, transparentBackground: false, hideWhenNoMedia: false);
                band.SetAvatarSize(iconSize);
                band.SetWindowSize(padding);
                band.SetRoundedMode(rounded);
                band.SetPinnedGames(new() { new(730, "Game") { IconPath = artwork } });
                band.SetPinnedAccounts(new()
                {
                    new() { SteamId = "76561198000000001", PersonaName = "One", AvatarPath = artwork },
                    new() { SteamId = "76561198000000002", PersonaName = "Two", AvatarPath = artwork },
                    new() { SteamId = "76561198000000003", PersonaName = "Three", AvatarPath = artwork }
                });
                band.SetPreviewLyricsEnabled(true);
                var root = (FrameworkElement)band.Content;
                var shell = (Border)band.FindName("GameAvatarContainer");
                var icons = ((Panel)band.FindName("GamePanel")).Children.Cast<Border>()
                    .Concat(((Panel)band.FindName("AvatarPanel")).Children.Cast<Border>()).ToArray();
                foreach (var viewport in new[] { new Size(band.Width, band.Height), new Size(band.Width, 30), new Size(band.Width * 0.7, 30) })
                {
                    root.Measure(viewport);
                    root.Arrange(new Rect(viewport));
                    root.UpdateLayout();
                    foreach (var icon in icons)
                    {
                        var bounds = icon.TransformToAncestor(root).TransformBounds(new Rect(icon.RenderSize));
                        if (bounds.Left < -0.1 || bounds.Top < -0.1 || bounds.Right > viewport.Width + 0.1 || bounds.Bottom > viewport.Height + 0.1)
                            throw new InvalidOperationException($"Icon overflow: size={iconSize}, padding={padding}, viewport={viewport}, bounds={bounds}.");
                        var inShell = icon.TransformToAncestor(shell).TransformBounds(new Rect(icon.RenderSize));
                        if (inShell.Left < 1 || inShell.Top < 1 || inShell.Right > shell.ActualWidth - 1 || inShell.Bottom > shell.ActualHeight - 1)
                            throw new InvalidOperationException("Icon overlaps the capsule rim.");
                        if (icon.Clip?.FillContains(new Point(0, 0)) != false)
                            throw new InvalidOperationException("An icon has no rounded child clip.");
                        checks++;
                    }
                    if (shell.Clip?.FillContains(new Point(0, 0)) != false)
                        throw new InvalidOperationException("Capsule children can leak through a round corner.");
                }
            }
            finally { band.Close(); }
        }

        foreach (var iconSize in new[] { 24, 42, 72 })
        foreach (var rounded in new[] { true, false })
        {
            var floating = new DesktopFloatingWindow(manager);
            try
            {
                floating.SetAvatarSize(iconSize);
                floating.SetRoundedMode(rounded);
                floating.SetPinnedAccounts(new()
                {
                    new() { SteamId = "76561198000000001", PersonaName = "One", AvatarPath = artwork },
                    new() { SteamId = "76561198000000002", PersonaName = "Two", AvatarPath = artwork }
                });
                floating.SetPinnedGames(new() { new(730, "Game") { IconPath = artwork } });
                var root = (FrameworkElement)floating.Content;
                var desired = NaturalSize(root, 1200, 200);
                var size = new Size(desired.Width, desired.Height);
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
                var shell = (Border)floating.FindName("RootGlass");
                var icons = ((Panel)floating.FindName("GamePanel")).Children.Cast<Border>()
                    .Concat(((Panel)floating.FindName("AvatarPanel")).Children.Cast<Border>());
                foreach (var icon in icons)
                {
                    var bounds = icon.TransformToAncestor(shell).TransformBounds(new Rect(icon.RenderSize));
                    if (bounds.Left < 1 || bounds.Top < 1 || bounds.Right > shell.ActualWidth - 1 || bounds.Bottom > shell.ActualHeight - 1 ||
                        icon.Clip?.FillContains(new Point(0, 0)) != false)
                        throw new InvalidOperationException("Desktop icon overflow or missing round clip.");
                    checks++;
                }
            }
            finally { floating.Close(); }
        }

        // A square child must really disappear outside the curved boundary,
        // rather than merely passing a geometry/property inspection.
        var probe = new SteamSwitcher.Controls.RoundedClipBorder
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
            Child = new Border { Background = Brushes.Red }
        };
        probe.Measure(new Size(40, 40));
        probe.Arrange(new Rect(0, 0, 40, 40));
        probe.UpdateLayout();
        var pixels = new byte[40 * 40 * 4];
        var bitmap = new RenderTargetBitmap(40, 40, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(probe);
        bitmap.CopyPixels(pixels, 40 * 4, 0);
        if (pixels[3] != 0 || pixels[(20 * 40 + 20) * 4 + 3] != 255)
            throw new InvalidOperationException("Rounded child clipping failed the raster corner/center check.");
        Console.WriteLine($"Verified {checks} taskbar/desktop icon placements and a rendered corner clipping probe.");
    }

    private static void SaveElement(FrameworkElement element, int width, int height, string outputPath, bool wallpaper = false)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        if (wallpaper)
        {
            // A colorful backdrop exposes transparency and edge leaks that a
            // transparent PNG viewer's black/white background can conceal.
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen())
            {
                var brush = new LinearGradientBrush(Colors.DarkViolet, Colors.Crimson, 0);
                brush.GradientStops.Add(new GradientStop(Colors.DarkSlateBlue, 0.6));
                drawing.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            }
            bitmap.Render(background);
        }
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static void AddPreviewAccount(
        MainViewModel viewModel,
        string accountName,
        string personaName,
        bool mostRecent,
        bool archived = false)
    {
        var account = new SteamAccount
        {
            SteamId = $"7656119800000000{viewModel.Accounts.Count + 1}",
            AccountName = accountName,
            PersonaName = personaName,
            MostRecent = mostRecent,
            IsArchived = archived,
            Timestamp = DateTimeOffset.UtcNow.AddHours(-(viewModel.Accounts.Count * 7 + 1)).ToUnixTimeSeconds(),
            AvatarPath = Path.Combine(SourceRoot(), "src", "SteamSwitcher", "Resources", "icon.png")
        };
        viewModel.Accounts.Add(new AccountViewModel(account));
    }

    private static void AddPreviewGame(
        MainViewModel viewModel,
        int appId,
        string name,
        string? accountName,
        bool pinned,
        string? lastPlayed)
    {
        viewModel.GameList.Add(new GameListViewModel(appId, name)
        {
            IconPath = Path.Combine(SourceRoot(), "src", "SteamSwitcher", "Resources", "icon.png"),
            SizeDisplay = appId % 2 == 0 ? "38.6 GB" : "74.2 GB",
            HasBinding = !string.IsNullOrWhiteSpace(accountName),
            BindingAccountName = accountName,
            BindingLastPlayed = lastPlayed,
            IsPinned = pinned
        });
    }

    private static void AddPreviewQuickLaunch(
        MainViewModel viewModel,
        string name,
        string executablePath,
        bool pinned,
        bool withIcon)
    {
        viewModel.QuickLaunchList.Add(new QuickLaunchViewModel
        {
            Id = $"preview-{viewModel.QuickLaunchList.Count + 1}",
            Name = name,
            ExecutablePath = executablePath,
            IconPath = withIcon ? Path.Combine(SourceRoot(), "src", "SteamSwitcher", "Resources", "icon.png") : null,
            IsPinned = pinned,
            SortOrder = viewModel.QuickLaunchList.Count
        });
    }
}

/// <summary>Win32 message plumbing for hit-test and non-client button assertions.</summary>
internal static class NativeMessages
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetWindowRectNative(IntPtr hWnd, out NativeRect lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private const int WmNcHitTest = 0x0084;

    public static int HitTest(IntPtr hwnd, int x, int y) =>
        (int)SendMessage(hwnd, WmNcHitTest, IntPtr.Zero, Pack(x, y));

    public static void NonClientButton(IntPtr hwnd, int message, int hitTest, int x, int y) =>
        SendMessage(hwnd, message, (IntPtr)hitTest, Pack(x, y));

    public static void GetWindowRect(IntPtr hwnd, out (int Left, int Top, int Right, int Bottom) rect)
    {
        GetWindowRectNative(hwnd, out var native);
        rect = (native.Left, native.Top, native.Right, native.Bottom);
    }

    private static IntPtr Pack(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));
}
