using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SteamSwitcher.Core;

namespace SteamSwitcher.Tests;

[TestClass]
public class ShortcutHelperTests
{
    [TestMethod]
    public void SanitizeFileName_ReplacesInvalidCharacters()
    {
        var result = ShortcutHelper.SanitizeFileName("bad<name>/file:test?", "Fallback");

        Assert.IsFalse(result.Any(character => Path.GetInvalidFileNameChars().Contains(character)));
        Assert.IsFalse(string.IsNullOrWhiteSpace(result));
    }

    [TestMethod]
    public void SanitizeFileName_TrimsAndUsesFallback_WhenNothingRemains()
    {
        Assert.AreEqual("Steam Account", ShortcutHelper.SanitizeFileName("   ", "Steam Account"));
        Assert.AreEqual("Steam Game", ShortcutHelper.SanitizeFileName(string.Empty, "Steam Game"));
    }

    [TestMethod]
    public void SanitizeFileName_KeepsValidNames()
    {
        Assert.AreEqual("My Game", ShortcutHelper.SanitizeFileName("  My Game  ", "Fallback"));
    }

    [TestMethod]
    public void GetUniquePath_ReturnsOriginal_WhenFree()
    {
        using var dir = new TemporaryDirectory();

        var path = ShortcutHelper.GetUniquePath(dir.Path, "Game.lnk");

        Assert.AreEqual(dir.Combine("Game.lnk"), path);
    }

    [TestMethod]
    public void GetUniquePath_AppendsCounter_WhenTaken()
    {
        using var dir = new TemporaryDirectory();
        File.WriteAllText(dir.Combine("Game.lnk"), "x");

        var path = ShortcutHelper.GetUniquePath(dir.Path, "Game.lnk");

        Assert.AreEqual(dir.Combine("Game (2).lnk"), path);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void GetUniquePath_KeepsCounting_UntilFree()
    {
        using var dir = new TemporaryDirectory();
        File.WriteAllText(dir.Combine("Game.lnk"), "x");
        File.WriteAllText(dir.Combine("Game (2).lnk"), "x");
        File.WriteAllText(dir.Combine("Game (3).lnk"), "x");

        var path = ShortcutHelper.GetUniquePath(dir.Path, "Game.lnk");

        Assert.AreEqual(dir.Combine("Game (4).lnk"), path);
    }

    [TestMethod]
    public void FindExistingShortcut_ReturnsNull_WhenNothingExists()
    {
        using var dir = new TemporaryDirectory();

        Assert.IsNull(ShortcutHelper.FindExistingShortcut(
            dir.Path, "Missing.lnk", "SteamSwitch.exe", "--switch-account 1"));
    }

    [TestMethod]
    public void FindExistingShortcut_IsCaseInsensitive_OnArguments()
    {
        using var dir = new TemporaryDirectory();
        File.WriteAllText(dir.Combine("Account.lnk"), "not a real shortcut");

        // WScript.Shell cannot read a non-lnk file; the helper must swallow the
        // COM failure and return null instead of throwing.
        Assert.IsNull(ShortcutHelper.FindExistingShortcut(
            dir.Path, "Account.lnk", "SteamSwitch.exe", "--switch-account 1"));
    }
}

[TestClass]
public class SteamServicePathGuardTests
{
    [TestMethod]
    public void CustomSteamPath_RejectsUncPath()
    {
        var service = new SteamService();

        service.SetCustomPaths(@"\\attacker\share\steam", null);

        Assert.IsNull(service.CustomSteamPath, "A UNC Steam directory must be rejected.");
        Assert.IsFalse(
            service.SteamPath?.StartsWith(@"\\", StringComparison.Ordinal) ?? false,
            "Steam must not resolve to a network share.");
        Assert.IsFalse(
            service.SteamExePath?.StartsWith(@"\\", StringComparison.Ordinal) ?? false,
            "The executable path must stay on a local drive.");
    }

    [TestMethod]
    public void CustomSteamPath_AcceptsLocalPath_OnTheCurrentDrive()
    {
        var service = new SteamService();

        service.SetCustomPaths(Path.GetTempPath(), null);

        Assert.AreEqual(Path.GetTempPath(), service.CustomSteamPath);
    }

    [TestMethod]
    public void CustomSteamPath_AllowsClearing()
    {
        var service = new SteamService();
        service.SetCustomPaths(Path.GetTempPath(), null);

        service.SetCustomPaths(string.Empty, null);

        Assert.AreEqual(string.Empty, service.CustomSteamPath);
    }
}
