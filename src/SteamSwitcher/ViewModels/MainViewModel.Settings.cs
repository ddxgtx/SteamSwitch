using System;
using System.Linq;
using System.Threading.Tasks;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.ViewModels;

public partial class MainViewModel
{
    private bool _settingsSaveFailed;
    public bool HasSettingsSaveError => _settingsSaveFailed;
    public bool HasUnsavedSettings => _settingsSession.IsDirty;
    public string SettingsSaveStatusText => HasSettingsSaveError
        ? _settingsSession.SaveProtectionReason != null ? "保存已暂停，原文件已保护" : "保存失败，请重试"
        : HasUnsavedSettings ? "正在保存…" : "更改自动保存";
    public string SettingsSaveErrorSummary => _settingsSession.SaveProtectionReason != null
        ? "设置未保存 · 原文件已保护" : "设置尚未保存 · 正在重试";
    public string? SettingsSaveRecoveryHint => _settingsSession.SaveProtectionReason;
    public string SettingsSaveActionText => _settingsSession.SaveProtectionReason != null ? "导出当前修改" : "立即重试";
    public string CloseButtonToolTip => MinimizeToTray
        ? "收起到托盘（完全退出请右键托盘图标选择“退出”）"
        : "退出 Steam Switch";

    partial void OnMinimizeToTrayChanged(bool value) => OnPropertyChanged(nameof(CloseButtonToolTip));

    public bool SaveSettingsNow()
    {
        _settingsSession.MarkDirty();
        return SavePendingSettings();
    }

    private bool SavePendingSettings()
    {
        if (_applyingSettings) return false;
        _settingsSaveTimer?.Stop();
        var saved = _settingsSession.TrySave();
        OnPropertyChanged(nameof(HasUnsavedSettings));
        if (saved && _settingsSaveFailed) StatusText = "设置已保存";
        _settingsSaveFailed = !saved;
        OnPropertyChanged(nameof(HasSettingsSaveError));
        OnPropertyChanged(nameof(SettingsSaveStatusText));
        OnPropertyChanged(nameof(SettingsSaveErrorSummary));
        OnPropertyChanged(nameof(SettingsSaveRecoveryHint));
        OnPropertyChanged(nameof(SettingsSaveActionText));
        if (!saved)
        {
            var protectionReason = _settingsSession.SaveProtectionReason;
            StatusText = protectionReason ?? "设置尚未保存：请检查磁盘空间和目录权限，程序将自动重试";
            if (protectionReason == null && !_disposed && _settingsSaveTimer != null)
            {
                _settingsSaveTimer.Interval = TimeSpan.FromSeconds(5);
                _settingsSaveTimer.Start();
            }
        }
        return saved;
    }

    /// <summary>Import/reset must replace both the live snapshot and its projected properties.</summary>
    public async Task ApplySettingsAsync(AppSettings settings, bool persist = true, Func<Task>? applyUi = null)
    {
        await _operationGate.WaitAsync(_lifetimeCts.Token);
        var previousReference = _settings;
        var previous = SettingsSession.Copy(_settings);
        try
        {
            await ApplySettingsCoreAsync(settings, persist);
            if (applyUi != null) await applyUi();
        }
        catch
        {
            if (!ReferenceEquals(previousReference, _settings))
            {
                await ApplySettingsCoreAsync(previous, persist);
                if (applyUi != null)
                {
                    try { await applyUi(); }
                    catch (Exception ex) { AppLogger.Error("Settings were rolled back but the interface could not refresh.", ex); }
                }
            }
            throw;
        }
        finally { _operationGate.Release(); }
    }

    private async Task ApplySettingsCoreAsync(AppSettings settings, bool persist)
    {
        var previousStartup = _readStartupState();
        var startupChanged = false;
        try
        {
            var next = SettingsSession.Copy(settings);
            // This is an observation of the current desktop, not portable configuration.
            next.DesktopIconsHiddenByApp = _settings.DesktopIconsHiddenByApp;
            if (next.StartWithWindows != previousStartup)
            {
                if (!_writeStartupState(next.StartWithWindows))
                    throw new System.IO.IOException("无法应用开机自启设置，导入未生效。");
                startupChanged = true;
            }
            if (!_settingsSession.TryReplace(next, persist))
                throw new System.IO.IOException("设置文件写入失败，原设置和未保存修改已保留。");

            _settingsSaveFailed = false;

            _settingsSaveTimer.Stop();
            _applyingSettings = true;
            _gameSaveAutoBackupService.Enabled = false;
            _injectorAttemptCts?.Cancel();
            await _injectorGate.WaitAsync();
            try { await CleanupInjectorResourcesAsync(); }
            finally { _injectorGate.Release(); }
            IsInjectorConnected = false;
            LoadSettingsFields();
            OnPropertyChanged(string.Empty);
            ThemeManager.ApplyTheme(Theme);
            InitializeMinimalModeTimer();
            MinimalModeChanged?.Invoke(this, MinimalMode);
            DesktopFloatingDockEnabledChanged?.Invoke(this, DesktopFloatingDockEnabled);
            await InitializeAsync(checkForUpdates: false, startIntegrations: false, throwOnFailure: true);
            PinnedAccountsChanged?.Invoke(this, EventArgs.Empty);
            PinnedGamesChanged?.Invoke(this, EventArgs.Empty);
            QuickLaunchChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            if (startupChanged)
                _writeStartupState(previousStartup);
            throw;
        }
        finally
        {
            _applyingSettings = false;
            if (_settingsSession.IsDirty && !_disposed)
                _settingsSaveTimer.Start();
        }
    }

    public ConfigurationBundle CreateConfigurationBundle() => ConfigurationBundleService.Prepare(new ConfigurationBundle
    {
        Settings = _settings,
        Accounts = _accountManager.GetAccountArchiveEntries().ToList(),
        GameBindings = _gameBinding.GetAllBindings(),
        QuickLaunch = _quickLaunch.GetAll()
    });

    public async Task ImportConfigurationBundleAsync(ConfigurationBundle bundle, Func<Task>? applyUi = null)
    {
        var prepared = ConfigurationBundleService.Prepare(bundle);
        await _operationGate.WaitAsync(_lifetimeCts.Token);
        var previousSettings = SettingsSession.Copy(_settings);
        var previousMonitoring = _gameSaveAutoBackupService.Enabled;
        var importStarted = false;
        try
        {
            prepared.Settings.DesktopIconsHiddenByApp = _settings.DesktopIconsHiddenByApp;
            if (!SaveSettingsNow())
                throw new System.IO.IOException("当前设置无法保存，应用配置包导入已中止。");
            _settingsSaveTimer.Stop();
            _applyingSettings = true;
            _gameSaveAutoBackupService.Enabled = false;
            var service = new ConfigurationBundleService(_configurationDirectory);
            importStarted = true;
            await service.ImportAsync(prepared, _lifetimeCts.Token, async () =>
            {
                // The storage transaction resumes off the UI thread.
                await _settingsSaveTimer.Dispatcher.InvokeAsync(async () =>
                {
                    await _accountManager.ReloadAccountLibraryAsync(reloadAccounts: false);
                    await ApplySettingsCoreAsync(prepared.Settings, persist: false);
                    if (applyUi != null) await applyUi();
                }).Task.Unwrap();
            });
        }
        catch
        {
            if (!importStarted) throw;
            // The storage service restores all four original files before propagating failure.
            await _accountManager.ReloadAccountLibraryAsync(reloadAccounts: false);
            await ApplySettingsCoreAsync(previousSettings, persist: false);
            _gameSaveAutoBackupService.Enabled = previousMonitoring;
            if (applyUi != null)
            {
                try { await applyUi(); }
                catch (Exception ex) { AppLogger.Error("Configuration was rolled back but the interface could not refresh.", ex); }
            }
            throw;
        }
        finally
        {
            _applyingSettings = false;
            _operationGate.Release();
        }
    }

    public Task ForgetAccountAsync(string steamId)
    {
        var bundle = CreateConfigurationBundle();
        bundle.Accounts.RemoveAll(entry => entry.SteamId == steamId);
        bundle.Accounts.Add(new SavedAccountEntry { SteamId = steamId, IsIgnored = true });
        foreach (var key in bundle.GameBindings.Where(pair => pair.Value.AccountSteamId == steamId).Select(pair => pair.Key).ToList())
            bundle.GameBindings.Remove(key);
        bundle.Settings.PinnedAccountIds.RemoveAll(id => string.Equals(id, steamId, StringComparison.OrdinalIgnoreCase));
        bundle.Settings.PinnedPanelItemOrder.RemoveAll(key => key == $"account:{steamId}");
        return ImportConfigurationBundleAsync(bundle);
    }

    // Apply a complete snapshot before emitting one binding refresh, without firing
    // the individual setting side effects against a half-applied configuration.
#pragma warning disable MVVMTK0034
    private void LoadSettingsFields()
    {
            _backupExportDirectory = _settings.BackupExportDirectory;
            _askBeforeBackupExport = _settings.AskBeforeBackupExport;
            _autoStartSteam = _settings.AutoStartSteam;
            _minimizeToTray = _settings.MinimizeToTray;
            _startWithWindows = _settings.StartWithWindows;
            _isTaskbarPinned = _settings.TaskbarPinned;
            _taskbarPosition = _settings.TaskbarPosition;
            _taskbarMonitorIndex = Math.Clamp(_settings.TaskbarMonitorIndex, 0, 8);
            _taskbarOffsetX = _settings.TaskbarOffsetX;
            _taskbarOffsetY = _settings.TaskbarOffsetY;
            _taskbarWindowSize = _settings.TaskbarWindowSize;
            _taskbarAvatarSize = _settings.TaskbarAvatarSize;
            _taskbarGlassEnabled = _settings.TaskbarGlassEnabled;
            _taskbarRoundedMode = _settings.TaskbarRoundedMode;
            var desktopEnhance = _settings.DesktopEnhance ??= new DesktopEnhanceSettings();
            _desktopAutoHideIconsEnabled = desktopEnhance.EnableAutoHideIcons;
            _desktopIconHideMode = Enum.IsDefined(desktopEnhance.HideMode) ? desktopEnhance.HideMode : DesktopIconHideMode.Smart;
            _desktopIconIdleSeconds = Math.Clamp(desktopEnhance.IdleSeconds, 5, 300);
            _desktopRestoreByMouseMove = desktopEnhance.RestoreByMouseMove;
            _desktopRestoreByTaskbarClick = desktopEnhance.RestoreByTaskbarClick;
            _desktopToggleIconsOnClick = desktopEnhance.ToggleIconsOnDesktopClick;
            _desktopIconAnimationType = Enum.IsDefined(desktopEnhance.AnimationType) ? desktopEnhance.AnimationType : DesktopIconAnimationType.Fade;
            _desktopIconAnimationDuration = Math.Clamp(desktopEnhance.AnimationDuration, 200, 1000);
            _desktopImmersiveModeEnabled = desktopEnhance.EnableImmersiveMode;
            _desktopIconsHiddenByApp = _settings.DesktopIconsHiddenByApp;
            _taskbarLyricsEnabled = _settings.TaskbarLyricsEnabled;
            _taskbarLyricsShowControls = _settings.TaskbarLyricsShowControls;
            _taskbarLyricsOnlineEnabled = _settings.TaskbarLyricsOnlineEnabled;
            _taskbarLyricsArtworkRounded = _settings.TaskbarLyricsArtworkRounded;
            _taskbarLyricsShowVisualizer = _settings.TaskbarLyricsShowVisualizer;
            _taskbarLyricsTransparentBackground = _settings.TaskbarLyricsTransparentBackground;
            _taskbarLyricsAdaptiveWidth = _settings.TaskbarLyricsAdaptiveWidth;
            _taskbarLyricsWidth = Math.Clamp(_settings.TaskbarLyricsWidth, 240, 720);
            _taskbarLyricsTrackInfoOnly = _settings.TaskbarLyricsTrackInfoOnly;
            _taskbarLyricsShowSecondaryLine = _settings.TaskbarLyricsShowSecondaryLine;
            _taskbarLyricsShowProgressBar = _settings.TaskbarLyricsShowProgressBar;
            _taskbarLyricsHideWhenNoMedia = _settings.TaskbarLyricsHideWhenNoMedia;
            _taskbarLyricsMusicOnly = _settings.TaskbarLyricsMusicOnly;
            _taskbarLyricsPollingIntervalMs = Math.Clamp(_settings.TaskbarLyricsPollingIntervalMs, 60, 1000);
            _taskbarLyricsVerticalOffset = Math.Clamp(_settings.TaskbarLyricsVerticalOffset, -12, 12);
            _lyricSourceQQEnabled = _settings.LyricSourceQQEnabled;
            _lyricSourceNeteaseEnabled = _settings.LyricSourceNeteaseEnabled;
            _lyricSourceKugouEnabled = _settings.LyricSourceKugouEnabled;
            _lyricSourceLrclibEnabled = _settings.LyricSourceLrclibEnabled;
            _lyricOffsetMs = Math.Clamp(_settings.LyricOffsetMs, -3000, 3000);
            _lyricSplitLongLines = _settings.LyricSplitLongLines;
            _settings.TaskbarLyricsWidth = _taskbarLyricsWidth;
            _gameSaveManualRetention = Math.Clamp(_settings.GameSaveManualRetention, 1, 100);
            _gameSaveSafetyRetention = Math.Clamp(_settings.GameSaveSafetyRetention, 1, 50);
            _settings.GameSaveManualRetention = _gameSaveManualRetention;
            _settings.GameSaveSafetyRetention = _gameSaveSafetyRetention;
            _gameSaveAutoBackupEnabled = _settings.GameSaveAutoBackupEnabled;
            _gameSavePeriodicBackupEnabled = _settings.GameSavePeriodicBackupEnabled;
            _gameSavePeriodicIntervalMinutes = Math.Clamp(_settings.GameSavePeriodicIntervalMinutes, 5, 1440);
            _settings.GameSavePeriodicIntervalMinutes = _gameSavePeriodicIntervalMinutes;
            _smartSaveDiscoveryEnabled = _settings.SmartSaveDiscoveryEnabled;
            _gameSaveAutoBackupStatusText = CreateGameSaveAutoBackupStatusText();
            _gameSaveCenterEnabled = _settings.GameSaveCenterEnabled;
            _desktopIconHiderEnabled = _settings.DesktopIconHiderEnabled;
            _taskbarLyricsModuleEnabled = _settings.TaskbarLyricsModuleEnabled;

            _desktopFloatingEnabled = _settings.DesktopFloatingEnabled;
            _desktopFloatingTopmost = _settings.DesktopFloatingTopmost;
            _desktopFloatingLocked = _settings.DesktopFloatingLocked;
            _desktopFloatingOpacity = _settings.DesktopFloatingOpacity;
            _desktopFloatingAvatarSize = _settings.DesktopFloatingAvatarSize;
            _desktopFloatingGlassEnabled = _settings.DesktopFloatingGlassEnabled;
            _desktopFloatingRoundedMode = _settings.DesktopFloatingRoundedMode;
            _desktopFloatingGlassColor = _settings.DesktopFloatingGlassColor;
            _playSoundOnSwitch = _settings.PlaySoundOnSwitch;
            _showSwitchNotification = _settings.ShowSwitchNotification;
            _taskbarLyricsFontSize = Math.Clamp(_settings.TaskbarLyricsFontSize, 12, 18);
            _taskbarLyricsFontFamily = _settings.TaskbarLyricsFontFamily;
            _theme = _settings.Theme;
            _minimalMode = _settings.MinimalMode;
            _desktopFloatingDockEnabled = _settings.DesktopFloatingDockEnabled;
            _enableLibraryInjection = _settings.EnableLibraryInjection;
            _autoScanGamesOnStartup = _settings.AutoScanGamesOnStartup;
            _silentCloseSteam = _settings.SilentCloseSteam;
            _useBetaUpdateChannel = _settings.UseBetaUpdateChannel;
            _checkUpdateOnStartup = _settings.CheckUpdateOnStartup;
            _autoInstallUpdates = _settings.AutoInstallUpdates;
            _showNotificationOnSteamClose = _settings.ShowNotificationOnSteamClose;
            _customSteamPath = _settings.CustomSteamPath ?? "";
            _customLoginUsersPath = _settings.CustomLoginUsersPath ?? "";
    }
#pragma warning restore MVVMTK0034
        private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_applyingSettings || _disposed) return;
            var shouldSaveSettings = true;
            switch (e.PropertyName)
            {
                case nameof(BackupExportDirectory):
                    _settings.BackupExportDirectory = BackupExportDirectory;
                    break;
                case nameof(AskBeforeBackupExport):
                    _settings.AskBeforeBackupExport = AskBeforeBackupExport;
                    break;
                case nameof(AutoStartSteam):
                    _settings.AutoStartSteam = AutoStartSteam;
                    break;
                case nameof(MinimizeToTray):
                    _settings.MinimizeToTray = MinimizeToTray;
                    break;
                case nameof(StartWithWindows):
                    if (_restoringStartWithWindows)
                    {
                        shouldSaveSettings = false;
                        break;
                    }

                    var requestedStartWithWindows = StartWithWindows;
                    var registrationSucceeded = _writeStartupState(requestedStartWithWindows);
                    var actualStartWithWindows = _readStartupState();
                    _settings.StartWithWindows = actualStartWithWindows;
                    if (!registrationSucceeded || actualStartWithWindows != requestedStartWithWindows)
                    {
                        _restoringStartWithWindows = true;
                        StartWithWindows = actualStartWithWindows;
                        _restoringStartWithWindows = false;
                        StatusText = requestedStartWithWindows
                            ? "开机自启设置失败，请检查当前用户注册表权限"
                            : "关闭开机自启失败，请检查当前用户注册表权限";
                    }
                    break;
                case nameof(IsTaskbarPinned):
                    _settings.TaskbarPinned = IsTaskbarPinned;
                    break;
                case nameof(TaskbarPosition):
                    _settings.TaskbarPosition = TaskbarPosition;
                    break;
                case nameof(TaskbarMonitorIndex):
                    _settings.TaskbarMonitorIndex = Math.Clamp(TaskbarMonitorIndex, 0, 8);
                    break;
                case nameof(TaskbarOffsetX):
                    _settings.TaskbarOffsetX = TaskbarOffsetX;
                    break;
                case nameof(TaskbarOffsetY):
                    _settings.TaskbarOffsetY = TaskbarOffsetY;
                    break;
                case nameof(TaskbarWindowSize):
                    _settings.TaskbarWindowSize = TaskbarWindowSize;
                    break;
                case nameof(TaskbarAvatarSize):
                    _settings.TaskbarAvatarSize = TaskbarAvatarSize;
                    break;
                case nameof(TaskbarGlassEnabled):
                    _settings.TaskbarGlassEnabled = TaskbarGlassEnabled;
                    break;
                case nameof(TaskbarRoundedMode):
                    _settings.TaskbarRoundedMode = TaskbarRoundedMode;
                    break;
                case nameof(DesktopAutoHideIconsEnabled):
                    _settings.DesktopEnhance.EnableAutoHideIcons = DesktopAutoHideIconsEnabled;
                    break;
                case nameof(DesktopIconHideMode):
                    _settings.DesktopEnhance.HideMode = DesktopIconHideMode;
                    break;
                case nameof(DesktopIconIdleSeconds):
                    _settings.DesktopEnhance.IdleSeconds = Math.Clamp(DesktopIconIdleSeconds, 5, 300);
                    break;
                case nameof(DesktopRestoreByMouseMove):
                    _settings.DesktopEnhance.RestoreByMouseMove = DesktopRestoreByMouseMove;
                    break;
                case nameof(DesktopRestoreByTaskbarClick):
                    _settings.DesktopEnhance.RestoreByTaskbarClick = DesktopRestoreByTaskbarClick;
                    break;
                case nameof(DesktopToggleIconsOnClick):
                    _settings.DesktopEnhance.ToggleIconsOnDesktopClick = DesktopToggleIconsOnClick;
                    break;
                case nameof(DesktopIconAnimationType):
                    _settings.DesktopEnhance.AnimationType = DesktopIconAnimationType;
                    break;
                case nameof(DesktopIconAnimationDuration):
                    _settings.DesktopEnhance.AnimationDuration = Math.Clamp(DesktopIconAnimationDuration, 200, 1000);
                    break;
                case nameof(DesktopImmersiveModeEnabled):
                    _settings.DesktopEnhance.EnableImmersiveMode = DesktopImmersiveModeEnabled;
                    break;
                case nameof(DesktopIconsHiddenByApp):
                    _settings.DesktopIconsHiddenByApp = DesktopIconsHiddenByApp;
                    break;
                case nameof(TaskbarLyricsEnabled):
                    _settings.TaskbarLyricsEnabled = TaskbarLyricsEnabled;
                    break;
                case nameof(TaskbarLyricsShowControls):
                    _settings.TaskbarLyricsShowControls = TaskbarLyricsShowControls;
                    break;
                case nameof(TaskbarLyricsOnlineEnabled):
                    _settings.TaskbarLyricsOnlineEnabled = TaskbarLyricsOnlineEnabled;
                    break;
                case nameof(TaskbarLyricsArtworkRounded):
                    _settings.TaskbarLyricsArtworkRounded = TaskbarLyricsArtworkRounded;
                    break;
                case nameof(TaskbarLyricsShowVisualizer):
                    _settings.TaskbarLyricsShowVisualizer = TaskbarLyricsShowVisualizer;
                    break;
                case nameof(TaskbarLyricsTransparentBackground):
                    _settings.TaskbarLyricsTransparentBackground = TaskbarLyricsTransparentBackground;
                    break;
                case nameof(TaskbarLyricsAdaptiveWidth):
                    _settings.TaskbarLyricsAdaptiveWidth = TaskbarLyricsAdaptiveWidth;
                    break;
                case nameof(TaskbarLyricsWidth):
                    _settings.TaskbarLyricsWidth = Math.Clamp(TaskbarLyricsWidth, 240, 720);
                    break;
                case nameof(TaskbarLyricsTrackInfoOnly):
                    _settings.TaskbarLyricsTrackInfoOnly = TaskbarLyricsTrackInfoOnly;
                    break;
                case nameof(TaskbarLyricsShowSecondaryLine):
                    _settings.TaskbarLyricsShowSecondaryLine = TaskbarLyricsShowSecondaryLine;
                    break;
                case nameof(TaskbarLyricsShowProgressBar):
                    _settings.TaskbarLyricsShowProgressBar = TaskbarLyricsShowProgressBar;
                    break;
                case nameof(TaskbarLyricsHideWhenNoMedia):
                    _settings.TaskbarLyricsHideWhenNoMedia = TaskbarLyricsHideWhenNoMedia;
                    break;
                case nameof(TaskbarLyricsPollingIntervalMs):
                    _settings.TaskbarLyricsPollingIntervalMs = Math.Clamp(TaskbarLyricsPollingIntervalMs, 60, 1000);
                    break;
                case nameof(TaskbarLyricsVerticalOffset):
                    _settings.TaskbarLyricsVerticalOffset = Math.Clamp(TaskbarLyricsVerticalOffset, -12, 12);
                    break;
                case nameof(LyricSourceQQEnabled):
                    _settings.LyricSourceQQEnabled = LyricSourceQQEnabled;
                    break;
                case nameof(LyricSourceNeteaseEnabled):
                    _settings.LyricSourceNeteaseEnabled = LyricSourceNeteaseEnabled;
                    break;
                case nameof(LyricSourceKugouEnabled):
                    _settings.LyricSourceKugouEnabled = LyricSourceKugouEnabled;
                    break;
                case nameof(LyricSourceLrclibEnabled):
                    _settings.LyricSourceLrclibEnabled = LyricSourceLrclibEnabled;
                    break;
                case nameof(LyricOffsetMs):
                    _settings.LyricOffsetMs = Math.Clamp(LyricOffsetMs, -3000, 3000);
                    break;
                case nameof(LyricSplitLongLines):
                    _settings.LyricSplitLongLines = LyricSplitLongLines;
                    break;
                case nameof(TaskbarLyricsMusicOnly):
                    _settings.TaskbarLyricsMusicOnly = TaskbarLyricsMusicOnly;
                    break;
                case nameof(GameSaveManualRetention):
                    _settings.GameSaveManualRetention = Math.Clamp(GameSaveManualRetention, 1, 100);
                    break;
                case nameof(GameSaveSafetyRetention):
                    _settings.GameSaveSafetyRetention = Math.Clamp(GameSaveSafetyRetention, 1, 50);
                    break;
                case nameof(GameSaveAutoBackupEnabled):
                    _settings.GameSaveAutoBackupEnabled = GameSaveAutoBackupEnabled;
                    SyncGameSaveAutoBackupService();
                    GameSaveAutoBackupStatusText = CreateGameSaveAutoBackupStatusText();
                    break;
                case nameof(GameSavePeriodicBackupEnabled):
                    _settings.GameSavePeriodicBackupEnabled = GameSavePeriodicBackupEnabled;
                    SyncGameSaveAutoBackupService();
                    GameSaveAutoBackupStatusText = CreateGameSaveAutoBackupStatusText();
                    break;
                case nameof(GameSavePeriodicIntervalMinutes):
                    _settings.GameSavePeriodicIntervalMinutes = Math.Clamp(GameSavePeriodicIntervalMinutes, 5, 1440);
                    if (GameSavePeriodicBackupEnabled)
                        GameSaveAutoBackupStatusText = CreateGameSaveAutoBackupStatusText();
                    break;
                case nameof(SmartSaveDiscoveryEnabled):
                    _settings.SmartSaveDiscoveryEnabled = SmartSaveDiscoveryEnabled;
                    break;
                case nameof(GameSaveCenterEnabled):
                    _settings.GameSaveCenterEnabled = GameSaveCenterEnabled;
                    SyncGameSaveAutoBackupService();
                    GameSaveAutoBackupStatusText = CreateGameSaveAutoBackupStatusText();
                    break;
                case nameof(DesktopIconHiderEnabled):
                    _settings.DesktopIconHiderEnabled = DesktopIconHiderEnabled;
                    break;
                case nameof(TaskbarLyricsModuleEnabled):
                    _settings.TaskbarLyricsModuleEnabled = TaskbarLyricsModuleEnabled;
                    break;
                case nameof(PlaySoundOnSwitch):
                    _settings.PlaySoundOnSwitch = PlaySoundOnSwitch;
                    break;
                case nameof(ShowSwitchNotification):
                    _settings.ShowSwitchNotification = ShowSwitchNotification;
                    break;
                case nameof(TaskbarLyricsFontSize):
                    _settings.TaskbarLyricsFontSize = Math.Clamp(TaskbarLyricsFontSize, 12, 18);
                    break;
                case nameof(TaskbarLyricsFontFamily):
                    _settings.TaskbarLyricsFontFamily = string.IsNullOrWhiteSpace(TaskbarLyricsFontFamily)
                        ? "Microsoft YaHei UI"
                        : TaskbarLyricsFontFamily.Trim();
                    break;
                case nameof(DesktopFloatingEnabled):
                    _settings.DesktopFloatingEnabled = DesktopFloatingEnabled;
                    break;
                case nameof(DesktopFloatingTopmost):
                    _settings.DesktopFloatingTopmost = DesktopFloatingTopmost;
                    break;
                case nameof(DesktopFloatingLocked):
                    _settings.DesktopFloatingLocked = DesktopFloatingLocked;
                    break;
                case nameof(DesktopFloatingOpacity):
                    _settings.DesktopFloatingOpacity = Math.Clamp(DesktopFloatingOpacity, 20, 100);
                    break;
                case nameof(DesktopFloatingAvatarSize):
                    _settings.DesktopFloatingAvatarSize = DesktopFloatingAvatarSize;
                    break;
                case nameof(DesktopFloatingGlassEnabled):
                    _settings.DesktopFloatingGlassEnabled = DesktopFloatingGlassEnabled;
                    break;
                case nameof(DesktopFloatingRoundedMode):
                    _settings.DesktopFloatingRoundedMode = DesktopFloatingRoundedMode;
                    break;
                case nameof(DesktopFloatingGlassColor):
                    _settings.DesktopFloatingGlassColor = DesktopFloatingGlassColor;
                    break;
                case nameof(Theme):
                    _settings.Theme = Theme;
                    ThemeManager.ApplyTheme(Theme);
                    OnPropertyChanged(nameof(IsDarkTheme));
                    break;
                case nameof(EnableLibraryInjection):
                    _settings.EnableLibraryInjection = EnableLibraryInjection;
                    if (EnableLibraryInjection)
                    {
                        _ = StartInjectorAsync().ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                AppLogger.Error("StartInjectorAsync failed", t.Exception);
                            }
                        }, TaskContinuationOptions.NotOnCanceled);
                    }
                    else
                        _ = StopInjectorAsync();
                    break;
                case nameof(AutoScanGamesOnStartup):
                    _settings.AutoScanGamesOnStartup = AutoScanGamesOnStartup;
                    break;
                case nameof(SilentCloseSteam):
                    _settings.SilentCloseSteam = SilentCloseSteam;
                    break;
                case nameof(UseBetaUpdateChannel):
                    _settings.UseBetaUpdateChannel = UseBetaUpdateChannel;
                    break;
                case nameof(CheckUpdateOnStartup):
                    _settings.CheckUpdateOnStartup = CheckUpdateOnStartup;
                    break;
                case nameof(AutoInstallUpdates):
                    _settings.AutoInstallUpdates = AutoInstallUpdates;
                    break;
                case nameof(ShowNotificationOnSteamClose):
                    _settings.ShowNotificationOnSteamClose = ShowNotificationOnSteamClose;
                    break;
                case nameof(CustomSteamPath):
                    _settings.CustomSteamPath = CustomSteamPath;
                    _accountManager.GetSteamService().SetCustomPaths(CustomSteamPath, CustomLoginUsersPath);
                    break;
                case nameof(CustomLoginUsersPath):
                    _settings.CustomLoginUsersPath = CustomLoginUsersPath;
                    _accountManager.GetSteamService().SetCustomPaths(CustomSteamPath, CustomLoginUsersPath);
                    break;
                case nameof(MinimalMode):
                    _settings.MinimalMode = MinimalMode;
                    InitializeMinimalModeTimer();
                    MinimalModeChanged?.Invoke(this, MinimalMode);
                    break;
                case nameof(DesktopFloatingDockEnabled):
                    _settings.DesktopFloatingDockEnabled = DesktopFloatingDockEnabled;
                    DesktopFloatingDockEnabledChanged?.Invoke(this, DesktopFloatingDockEnabled);
                    break;
                default:
                    shouldSaveSettings = false;
                    break;
            }

            if (!shouldSaveSettings)
                return;

            ScheduleSettingsSave();
        }

        private void ScheduleSettingsSave()
        {
            _settingsSession.MarkDirty();
            OnPropertyChanged(nameof(HasUnsavedSettings));
            OnPropertyChanged(nameof(SettingsSaveStatusText));
            if (_applyingSettings || _disposed) return;
            _settingsSaveTimer.Interval = TimeSpan.FromMilliseconds(500);
            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Start();
        }


}
