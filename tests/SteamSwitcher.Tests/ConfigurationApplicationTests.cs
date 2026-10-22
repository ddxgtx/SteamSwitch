using System.Text.Json;
using System.Windows.Threading;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;
using SteamSwitcher.ViewModels;

namespace SteamSwitcher.Tests;

[TestClass]
public sealed class ConfigurationApplicationTests
{
    [TestMethod]
    public Task StartedGame_RecordFailureWarnsWithoutReportingLaunchFailure() => RunOnDispatcher(async () =>
    {
        using var rig = new Rig();
        await rig.Model.SaveGameBinding(730, "Game", "76561198000000001", "test");
        string? notification = null;
        rig.Model.NotificationRequested += (_, message) => notification = message;
        using var locked = new FileStream(rig.Model.GameBindingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await rig.Model.RecordStartedGameAsync(730, "76561198000000001", "test");
        Assert.IsTrue(notification?.Contains("游戏已启动") == true);
        Assert.IsNotNull(notification);
        Assert.IsTrue(notification.Contains("未能保存"));
        Assert.IsNull(rig.Model.GetGameBinding(730)!.LastPlayed);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task SuccessfulImport_ClearsAnEarlierSettingsSaveFailure(bool bundle) => RunOnDispatcher(async () =>
    {
        using var rig = new Rig();
        rig.CanSave = false;
        rig.Model.MinimizeToTray = false;
        Assert.IsFalse(rig.Model.SaveSettingsNow());
        Assert.IsTrue(rig.Model.HasSettingsSaveError);
        rig.CanSave = true;
        if (bundle) await rig.Model.ImportConfigurationBundleAsync(new ConfigurationBundle { Settings = rig.Initial() });
        else await rig.Model.ApplySettingsAsync(rig.Initial());
        Assert.IsFalse(rig.Model.HasSettingsSaveError);
        Assert.IsFalse(rig.Model.HasUnsavedSettings);
        Assert.AreEqual("更改自动保存", rig.Model.SettingsSaveStatusText);
    });

    [TestMethod]
    public Task ExportBusyGate_RejectsDuplicateClicksAndRecordsOnlyCompletedFiles() => RunOnDispatcher(async () =>
    {
        using var rig = new Rig();
        rig.Model.BackupExportDirectory = rig.Temp.Combine("exports");
        Assert.IsTrue(rig.Model.SaveSettingsNow());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = rig.Model.ExportBackupAsync(BackupExportKind.Settings, "settings.json", async path =>
        {
            started.SetResult();
            await release.Task;
            await File.WriteAllTextAsync(path, "complete");
        }, _ => throw new AssertFailedException("Automatic export asked for a path."));
        await started.Task;
        Assert.IsTrue(rig.Model.IsBackupExporting);
        Assert.IsFalse(rig.Model.CanExportBackup);
        Assert.IsFalse(rig.Model.HasRecentBackupExport);
        var second = await rig.Model.ExportBackupAsync(BackupExportKind.Settings, "settings.json",
            _ => throw new AssertFailedException("A duplicate click started another write."), _ => null);
        Assert.IsNull(second);
        release.SetResult();
        var saved = await first;
        Assert.IsFalse(rig.Model.IsBackupExporting);
        Assert.IsTrue(rig.Model.CanExportBackup);
        Assert.IsTrue(rig.Model.HasRecentBackupExport);
        Assert.AreEqual(saved, rig.Model.RecentBackupExportPath);
        Assert.AreEqual(Path.GetFileName(saved), rig.Model.RecentBackupExportFileName);
        Assert.IsTrue(rig.Model.TryGetRecentBackupExport(out var actual));
        Assert.AreEqual(saved, actual);
        Assert.IsFalse(rig.Model.HasUnsavedSettings, "Session-only backup feedback must not modify saved preferences.");
    });

    [TestMethod]
    public Task FailedOrCancelledExport_PreservesRecentFile_AndDeletedFileCannotBeRevealed() => RunOnDispatcher(async () =>
    {
        using var rig = new Rig();
        rig.Model.BackupExportDirectory = rig.Temp.Combine("exports");
        var saved = await rig.Model.ExportBackupAsync(BackupExportKind.Settings, "settings.json", path => File.WriteAllTextAsync(path, "saved"), _ => null);
        await Assert.ThrowsExactlyAsync<IOException>(() => rig.Model.ExportBackupAsync(BackupExportKind.Settings, "settings.json",
            _ => throw new IOException("simulated disk failure"), _ => null));
        Assert.IsFalse(rig.Model.IsBackupExporting);
        Assert.AreEqual(saved, rig.Model.RecentBackupExportPath);
        rig.Model.AskBeforeBackupExport = true;
        Assert.IsNull(await rig.Model.ExportBackupAsync(BackupExportKind.Settings, "settings.json",
            _ => throw new AssertFailedException("Cancelled export wrote a file."), _ => null));
        Assert.AreEqual(saved, rig.Model.RecentBackupExportPath);
        File.Delete(saved!);
        Assert.IsFalse(rig.Model.TryGetRecentBackupExport(out _));
        Assert.IsFalse(rig.Model.HasRecentBackupExport);
        Assert.AreEqual("", rig.Model.RecentBackupExportFileName);
        Assert.IsTrue(rig.Model.StatusText.Contains("已移动、删除或无法访问"));
    });

    [TestMethod]
    public Task ApplySettings_UpdatesProjectedPropertiesAndLaterSavesTheNewSnapshot() => RunOnDispatcher(async () =>
    {
        using var rig = new Rig();
        rig.Model.TaskbarLyricsWidth = 400;
        var next = rig.Initial();
        next.Theme = "Light";
        next.TaskbarLyricsWidth = 620;
        next.StartWithWindows = true;
        next.TaskbarPinned = true;
        next.DesktopFloatingEnabled = true;
        next.GameSaveManualRetention = 1;
        next.BackupExportDirectory = rig.Temp.Combine("exports");
        next.AskBeforeBackupExport = true;
        await rig.Model.ApplySettingsAsync(next);
        Assert.AreEqual("Light", rig.Model.Theme);
        Assert.AreEqual(620, rig.Model.TaskbarLyricsWidth);
        Assert.IsTrue(rig.Model.IsTaskbarPinned);
        Assert.IsTrue(rig.Model.DesktopFloatingEnabled);
        Assert.IsTrue(rig.StartupState);
        Assert.AreEqual(1, rig.Model.GameSaveManualRetention);
        Assert.AreEqual(next.BackupExportDirectory, rig.Model.BackupExportDirectoryDisplay);
        Assert.IsTrue(rig.Model.AskBeforeBackupExport);
        Assert.IsFalse(rig.Model.HasUnsavedSettings);
        rig.Model.MinimizeToTray = false;
        Assert.IsTrue(rig.Model.SaveSettingsNow());
        var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(rig.Temp.Combine("settings.json")))!;
        Assert.AreEqual(620, saved.TaskbarLyricsWidth);
        Assert.AreEqual("Light", saved.Theme);
        Assert.IsFalse(saved.MinimizeToTray);
        Assert.AreEqual(next.BackupExportDirectory, saved.BackupExportDirectory);
        Assert.IsTrue(saved.AskBeforeBackupExport);
    });

    [TestMethod]
    public Task SettingsInterfaceFailure_RollsBackLiveAndPersistedValues() => RunOnDispatcher(async () =>
    {
        using var rig = new Rig();
        var next = rig.Initial();
        next.Theme = "Light";
        var callbacks = 0;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rig.Model.ApplySettingsAsync(next, applyUi: () =>
        {
            if (++callbacks == 1) throw new InvalidOperationException("failed to apply interface");
            return Task.CompletedTask;
        }));
        Assert.AreEqual("Dark", rig.Model.Theme);
        Assert.AreEqual("Dark", JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(rig.Temp.Combine("settings.json")))!.Theme);
        Assert.AreEqual(2, callbacks);
    });

    [TestMethod]
    public Task FailedImportPreflight_PreservesPendingEditsAndTheirRetryState() => RunOnDispatcher(async () =>
    {
        using var rig = new Rig();
        rig.Model.TaskbarLyricsWidth = 600;
        rig.CanSave = false;
        await Assert.ThrowsExactlyAsync<IOException>(() => rig.Model.ImportConfigurationBundleAsync(new ConfigurationBundle { Settings = rig.Initial() }));
        Assert.IsTrue(rig.Model.HasUnsavedSettings);
        Assert.IsTrue(rig.Model.HasSettingsSaveError);
        Assert.AreEqual(600, rig.Model.TaskbarLyricsWidth);
        rig.CanSave = true;
        Assert.IsTrue(rig.Model.SaveSettingsNow());
        Assert.IsFalse(rig.Model.HasUnsavedSettings);
        Assert.IsFalse(rig.Model.HasSettingsSaveError);
    });

    [TestMethod]
    public Task InterfaceFailure_RestoresFilesAndClearsImportedCollections() => RunOnDispatcher(async () =>
    {
        using var rig = new Rig();
        var bundle = new ConfigurationBundle
        {
            Settings = rig.Initial(),
            GameBindings = new() { [10] = new GameBinding { AppId = 10, GameName = "Imported" } },
            QuickLaunch = new() { new QuickLaunchItem { Id = "imported", ExecutablePath = "C:\\example.exe" } }
        };
        bundle.Settings.Theme = "Light";
        var calls = 0;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rig.Model.ImportConfigurationBundleAsync(bundle, () =>
        {
            if (++calls == 1) throw new InvalidOperationException("simulated interface failure");
            return Task.CompletedTask;
        }));
        Assert.AreEqual("Dark", rig.Model.Theme);
        Assert.HasCount(0, rig.Model.GameBindings);
        Assert.HasCount(0, rig.Model.QuickLaunchList);
        Assert.IsFalse(File.Exists(rig.Temp.Combine("gamebindings.json")));
        Assert.IsFalse(File.Exists(rig.Temp.Combine("quicklaunch.json")));
        Assert.AreEqual(2, calls);
    });

    [TestMethod]
    public Task BundleImport_KeepsCurrentDesktopRecoveryMarkerAndDoesNotStartIntegrations() => RunOnDispatcher(async () =>
    {
        using var rig = new Rig();
        rig.Model.DesktopIconsHiddenByApp = true;
        var next = rig.Initial();
        next.EnableLibraryInjection = true;
        await rig.Model.ImportConfigurationBundleAsync(new ConfigurationBundle { Settings = next });
        var disk = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(rig.Temp.Combine("settings.json")))!;
        Assert.IsTrue(disk.DesktopIconsHiddenByApp);
        Assert.IsTrue(rig.Model.DesktopIconsHiddenByApp);
        Assert.IsFalse(rig.Model.IsInjectorConnected);
    });

    private static async Task RunOnDispatcher(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class Rig : IDisposable
    {
        public TemporaryDirectory Temp { get; } = new();
        public bool CanSave { get; set; } = true;
        public bool StartupState { get; set; }
        public MainViewModel Model { get; }

        public AppSettings Initial() => new()
        {
            CustomSteamPath = Temp.Path,
            CustomLoginUsersPath = Temp.Combine("loginusers.vdf"),
            CheckUpdateOnStartup = false,
            GameSavePeriodicBackupEnabled = false,
            Theme = "Dark"
        };

        public Rig()
        {
            File.WriteAllText(Temp.Combine("steam.exe"), "test stub, never execute");
            File.WriteAllText(Temp.Combine("loginusers.vdf"), "\"users\" { }");
            var steam = new SteamService();
            steam.SetCustomPaths(Temp.Path, Temp.Combine("loginusers.vdf"));
            var accounts = new AccountManager(steam, new AccountLibraryService(Temp.Combine("accounts.json")), () => null, downloadAvatars: false);
            var session = new SettingsSession(Initial(), settings =>
            {
                if (!CanSave) return false;
                File.WriteAllText(Temp.Combine("settings.json"), JsonSerializer.Serialize(settings));
                return true;
            });
            Model = new MainViewModel(accounts, new GameAccountBinding(Temp.Combine("gamebindings.json")),
                new QuickLaunchService(Temp.Combine("quicklaunch.json")), session,
                () => StartupState, value => { StartupState = value; return true; }, Temp.Path);
        }

        public void Dispose() { Model.Dispose(); Temp.Dispose(); }
    }
}
