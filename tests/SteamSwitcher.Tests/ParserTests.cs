using SteamSwitcher.Core;
using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class ParserTests
{
    [TestMethod]
    public void SyncedLyricsParser_SelectsCurrentLine()
    {
        var lines = SyncedLyricsParser.Parse("[00:01.00]first\n[00:02.50]second");
        Assert.HasCount(2, lines);
        Assert.AreEqual("second", SyncedLyricsParser.FindLine(lines, TimeSpan.FromSeconds(2.6)));
        Assert.AreEqual("second", SyncedLyricsParser.FindNextLine(lines, TimeSpan.FromSeconds(1.2)));
        Assert.IsNull(SyncedLyricsParser.FindNextLine(lines, TimeSpan.FromSeconds(2.6)));
    }

    [TestMethod]
    public void SyncedLyricsParser_ParsesOffsetAndDecodesHtmlEntities()
    {
        var lrc = "[offset:500]\n[00:01.00]Tom &amp; Jerry &apos;Show&apos;\n[00:02.00]rock &quot;n&quot; roll";
        var lines = SyncedLyricsParser.Parse(lrc);
        Assert.HasCount(2, lines);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1500), lines[0].Time);
        Assert.AreEqual("Tom & Jerry 'Show'", lines[0].Text);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2500), lines[1].Time);
        Assert.AreEqual("rock \"n\" roll", lines[1].Text);
    }

    [TestMethod]
    public void SyncedLyricsParser_FiltersMetadataTags()
    {
        var lrc = "[ti:Title]\n[ar:Artist]\n[al:Album]\n[by:Author]\n[00:01.20]Hello World";
        var lines = SyncedLyricsParser.Parse(lrc);
        Assert.HasCount(1, lines);
        Assert.AreEqual("Hello World", lines[0].Text);
    }

    [TestMethod]
    public void SyncedLyricsParser_MergesBilingualLyrics()
    {
        var origLrc = "[00:11.77]There is an angel in the snow\n[00:15.81]And I might not be the one for you";
        var transLrc = "[00:11.77]雪中氤氲出天使的身影\n[00:15.80]或许我并非你的命中注定";

        var orig = SyncedLyricsParser.Parse(origLrc);
        var trans = SyncedLyricsParser.Parse(transLrc);
        var merged = SyncedLyricsParser.Merge(orig, trans);

        Assert.HasCount(2, merged);
        Assert.AreEqual("There is an angel in the snow", merged[0].Text);
        Assert.AreEqual("雪中氤氲出天使的身影", merged[0].Translation);
        Assert.AreEqual("And I might not be the one for you", merged[1].Text);
        Assert.AreEqual("或许我并非你的命中注定", merged[1].Translation);

        var line = SyncedLyricsParser.FindLyricLine(merged, TimeSpan.FromSeconds(16));
        Assert.IsNotNull(line);
        Assert.AreEqual("And I might not be the one for you", line.Text);
        Assert.AreEqual("或许我并非你的命中注定", line.Translation);
    }

    [TestMethod]
    public async Task VdfParser_RoundTripsEscapedValues()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.Combine("loginusers.vdf");
        var source = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["users"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["76561198000000000"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AccountName"] = "audit_user",
                    ["PersonaName"] = "quoted \"name\"",
                    ["MostRecent"] = "1"
                }
            }
        };
        var parser = new VdfParser();
        await File.WriteAllTextAsync(path, parser.Serialize(source));

        var parsed = parser.Parse(path);
        var users = (Dictionary<string, object>)parsed["users"];
        var user = (Dictionary<string, object>)users["76561198000000000"];
        Assert.AreEqual("audit_user", user["AccountName"]);
        Assert.AreEqual("quoted \"name\"", user["PersonaName"]);
        Assert.AreEqual("1", user["MostRecent"]);
    }

    [TestMethod]
    public void TrackMetadataSanitizer_ExtractsAndCleansTrackInfo()
    {
        var (t1, a1) = TrackMetadataSanitizer.Sanitize("周杰伦 - 晴天 (Live)", "");
        Assert.AreEqual("晴天", t1);
        Assert.AreEqual("周杰伦", a1);

        var (t2, a2) = TrackMetadataSanitizer.Sanitize("Cruel Summer [Official Video].mp3", "Taylor Swift");
        Assert.AreEqual("Cruel Summer", t2);
        Assert.AreEqual("Taylor Swift", a2);

        var (t3, a3) = TrackMetadataSanitizer.Sanitize("夜に駆ける (Cover by XYZ)", "YOASOBI");
        Assert.AreEqual("夜に駆ける", t3);
        Assert.AreEqual("YOASOBI", a3);
    }
}
