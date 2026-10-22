using SteamSwitcher.Services;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class SettingsSessionTests
{
    [TestMethod]
    public void Import_ReplacesPendingSnapshot_AndLaterSaveCannotRestoreOldValues()
    {
        AppSettings? disk = null;
        var session = new SettingsSession(new AppSettings { Theme = "Dark" }, s =>
        {
            disk = SettingsSession.Copy(s);
            return true;
        });
        session.Current.TaskbarLyricsWidth = 300;
        session.MarkDirty();
        var imported = new AppSettings { Theme = "Light", TaskbarLyricsWidth = 600 };
        Assert.IsTrue(session.TryReplace(imported));
        imported.TaskbarLyricsWidth = 400;
        Assert.IsFalse(session.IsDirty);
        Assert.IsTrue(session.TrySave());
        Assert.AreEqual("Light", session.Current.Theme);
        Assert.AreEqual(600, disk!.TaskbarLyricsWidth);
        session.Current.MinimizeToTray = false;
        session.MarkDirty();
        Assert.IsTrue(session.TrySave());
        Assert.AreEqual(600, disk.TaskbarLyricsWidth);
    }

    [TestMethod]
    public void FailedSave_KeepsDirtyAndRetriesLatestValues()
    {
        var canSave = false;
        AppSettings? disk = null;
        var session = new SettingsSession(new AppSettings(), s =>
        {
            if (!canSave) return false;
            disk = SettingsSession.Copy(s);
            return true;
        });
        session.MarkDirty();
        Assert.IsFalse(session.TrySave());
        Assert.IsTrue(session.IsDirty);
        session.Current.TaskbarLyricsWidth = 550;
        canSave = true;
        Assert.IsTrue(session.TrySave());
        Assert.IsFalse(session.IsDirty);
        Assert.AreEqual(550, disk!.TaskbarLyricsWidth);
    }

    [TestMethod]
    public void FailedImport_PreservesCurrentSnapshotAndPendingChanges()
    {
        var session = new SettingsSession(new AppSettings { Theme = "Dark" }, _ => false);
        session.MarkDirty();
        Assert.IsFalse(session.TryReplace(new AppSettings { Theme = "Light" }));
        Assert.AreEqual("Dark", session.Current.Theme);
        Assert.IsTrue(session.IsDirty);
    }
}
