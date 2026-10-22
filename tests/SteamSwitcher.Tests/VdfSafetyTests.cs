using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SteamSwitcher.Core;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class VdfSafetyTests
{
    [TestMethod]
    [DataRow("\"users\" { \"1\" { \"AccountName\" \"alice\"")]
    [DataRow("\"users\" { \"1\" { \"AccountName\" \"alice")]
    [DataRow("\"users\" {} } \"discarded\" \"tail\"")]
    [DataRow("\"users\" { \"1\" }")]
    [DataRow("\"users\" { \"1\" { \"AccountName\" \"alice\" \"accountname\" \"bob\" } }")]
    [DataRow("\"users\" {} \"key-without-value\"")]
    [DataRow("\"users\" { { }")]
    public void MalformedSource_IsRejectedInsteadOfRepairedOrDiscarded(string content)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.Combine("bad.vdf");
        File.WriteAllText(path, content);
        Assert.ThrowsExactly<InvalidDataException>(() => new VdfParser().Parse(path));
        Assert.AreEqual(content, File.ReadAllText(path));
    }

    [TestMethod]
    public void ValidVdf_AllowsCommentsEmptyValuesUnquotedTokensAndEscapedNames()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.Combine("valid.vdf");
        File.WriteAllText(path, "// comment\nusers { \"1\" { \"AccountName\" alice \"PersonaName\" \"a \\\"quote\\\"\" \"Empty\" \"\" } } // tail");
        var parser = new VdfParser();
        var data = parser.Parse(path);
        var user = (Dictionary<string, object>)((Dictionary<string, object>)data["users"])["1"];
        Assert.AreEqual("alice", user["AccountName"]);
        Assert.AreEqual("a \"quote\"", user["PersonaName"]);
        Assert.AreEqual(string.Empty, user["Empty"]);
        File.WriteAllText(path, parser.Serialize(data));
        Assert.HasCount(1, parser.Parse(path));
    }
}
