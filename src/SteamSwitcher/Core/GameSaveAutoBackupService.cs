using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    public sealed class SteamExitTrigger
    {
        private bool _initialized;
        private bool _wasRunning;

        public void Reset(bool isRunning)
        {
            _initialized = true;
            _wasRunning = isRunning;
        }

        public bool Observe(bool isRunning)
        {
            if (!_initialized)
            {
                Reset(isRunning);
                return false;
            }

            var exited = _wasRunning && !isRunning;
            _wasRunning = isRunning;
            return exited;
        }
    }

    public sealed record GameSaveAutoBackupResult(
        int Created,
        int Skipped,
        int Failed,
        int Unavailable,
        DateTime CompletedAt);

    public sealed class GameSaveAutoBackupService : IDisposable
    {
        private readonly AccountManager _accountManager;
        private readonly Func<AppSettings> _settingsProvider;
        private readonly SteamExitTrigger _exitTrigger = new();
        private readonly SemaphoreSlim _backupGate = new(1, 1);
        private CancellationTokenSource? _monitorCts;
        private Task? _monitorTask;
        private DateTime _lastBackupUtc = DateTime.MinValue;
        private DateTime _lastPeriodicBackupUtc = DateTime.MinValue;
        private bool _enabled;
        private bool _disposed;

        public event EventHandler<GameSaveAutoBackupResult>? BackupCompleted;

        public GameSaveAutoBackupService(AccountManager accountManager, Func<AppSettings> settingsProvider)
        {
            _accountManager = accountManager;
            _settingsProvider = settingsProvider;
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_disposed || _enabled == value) return;
                _enabled = value;
                if (value) StartMonitor();
                else StopMonitor();
            }
        }

        public Task BackupBeforeAccountSwitchAsync(SteamAccount? currentAccount) =>
            Enabled && _settingsProvider().GameSaveAutoBackupEnabled
                ? BackupCurrentAccountAsync(currentAccount?.SteamId, force: true, _monitorCts?.Token ?? CancellationToken.None)
                : Task.CompletedTask;

        public async Task<GameSaveAutoBackupResult> BackupGameAsync(
            string steamId,
            int appId,
            string gameName,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _backupGate.WaitAsync(cancellationToken);
            try
            {
                var (service, entries) = await DiscoverEntriesAsync(cancellationToken);
                var selected = entries.Where(entry => entry.IsAvailable &&
                    (entry.IsDetected
                        ? entry.AppId == appId
                        : entry.IsCustom
                            ? string.Equals(entry.GameName, gameName, StringComparison.CurrentCultureIgnoreCase) &&
                              (entry.SteamId == "custom" || string.Equals(entry.SteamId, steamId, StringComparison.Ordinal))
                            : entry.AppId == appId && string.Equals(entry.SteamId, steamId, StringComparison.Ordinal)))
                    .ToList();
                return await BackupEntriesAsync(service, selected, "Launch workflow backup", cancellationToken);
            }
            finally
            {
                _backupGate.Release();
            }
        }

        private void StartMonitor()
        {
            StopMonitor();
            _monitorCts = new CancellationTokenSource();
            _lastPeriodicBackupUtc = DateTime.UtcNow;
            _exitTrigger.Reset(_accountManager.GetSteamService().IsSteamRunning());
            _monitorTask = MonitorAsync(_monitorCts.Token);
        }

        private void StopMonitor()
        {
            var cancellation = _monitorCts;
            var monitorTask = _monitorTask;
            _monitorCts = null;
            _monitorTask = null;

            if (cancellation == null)
                return;

            cancellation.Cancel();
            if (monitorTask == null || monitorTask.IsCompleted)
            {
                cancellation.Dispose();
                return;
            }

            // Do not dispose the token source while the previous monitor is still
            // unwinding. Observing the continuation also prevents a background fault
            // from becoming unobserved when the setting is toggled rapidly.
            _ = monitorTask.ContinueWith(
                completedTask =>
                {
                    _ = completedTask.Exception;
                    cancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task MonitorAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    var settings = _settingsProvider();
                    var steamService = _accountManager.GetSteamService();
                    var steamRunning = steamService.IsSteamRunning();

                    // 1. Steam Exit Trigger Auto-Backup
                    var steamExited = _exitTrigger.Observe(steamRunning);
                    if (settings.GameSaveAutoBackupEnabled && steamExited)
                    {
                        // Allow Steam Cloud and child processes a short window to finish
                        // their final writes, then confirm Steam is still stopped.
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                        if (!steamService.IsSteamRunning())
                        {
                            try
                            {
                                await BackupCurrentAccountAsync(_accountManager.CurrentAccount?.SteamId, force: false, cancellationToken);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Error("Game save auto-backup attempt failed; monitoring will continue.", ex);
                            }
                        }
                    }

                    // 2. Periodic Scheduled Auto-Backup
                    if (settings.GameSavePeriodicBackupEnabled && settings.GameSavePeriodicIntervalMinutes > 0)
                    {
                        var intervalMinutes = Math.Clamp(settings.GameSavePeriodicIntervalMinutes, 1, 1440);
                        if (DateTime.UtcNow - _lastPeriodicBackupUtc >= TimeSpan.FromMinutes(intervalMinutes))
                        {
                            _lastPeriodicBackupUtc = DateTime.UtcNow;
                            try
                            {
                                await BackupPeriodicAsync(_accountManager.CurrentAccount?.SteamId, cancellationToken);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Error("Game save periodic auto-backup attempt failed; monitoring will continue.", ex);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppLogger.Error("Game save auto-backup monitor stopped unexpectedly.", ex);
            }
        }

        private async Task BackupPeriodicAsync(string? steamId, CancellationToken cancellationToken)
        {
            if (!_enabled || _disposed) return;
            if (!await _backupGate.WaitAsync(0, cancellationToken)) return;

            try
            {
                var steamService = _accountManager.GetSteamService();
                if (string.IsNullOrWhiteSpace(steamService.SteamPath)) return;

                var (service, entries) = await DiscoverEntriesAsync(cancellationToken);
                var selected = entries.Where(entry =>
                        entry.IsDetected ||
                        (entry.IsCustom
                            ? entry.SteamId == "custom" || string.Equals(entry.SteamId, steamId, StringComparison.Ordinal)
                            : string.Equals(entry.SteamId, steamId, StringComparison.Ordinal)))
                    .ToList();

                var resultSummary = await BackupEntriesAsync(service, selected, "Periodic backup", cancellationToken);
                BackupCompleted?.Invoke(this, resultSummary);
            }
            finally
            {
                _backupGate.Release();
            }
        }

        private async Task BackupCurrentAccountAsync(string? steamId, bool force, CancellationToken cancellationToken)
        {
            if (!_enabled || _disposed) return;
            if (!force && DateTime.UtcNow - _lastBackupUtc < TimeSpan.FromSeconds(15)) return;
            if (!await _backupGate.WaitAsync(0, cancellationToken)) return;

            try
            {
                if (!force && DateTime.UtcNow - _lastBackupUtc < TimeSpan.FromSeconds(15)) return;
                var steamService = _accountManager.GetSteamService();
                if (steamService.IsSteamRunning()) return;
                if (string.IsNullOrWhiteSpace(steamService.SteamPath)) return;

                var (service, entries) = await DiscoverEntriesAsync(cancellationToken);
                var selected = entries.Where(entry =>
                        entry.IsDetected ||
                        (entry.IsCustom
                            ? entry.SteamId == "custom" || string.Equals(entry.SteamId, steamId, StringComparison.Ordinal)
                            : string.Equals(entry.SteamId, steamId, StringComparison.Ordinal)))
                    .ToList();

                _lastBackupUtc = DateTime.UtcNow;
                var resultSummary = await BackupEntriesAsync(service, selected, "Auto-backup", cancellationToken);
                BackupCompleted?.Invoke(this, resultSummary);
            }
            finally
            {
                _backupGate.Release();
            }
        }

        private async Task<(GameSaveBackupService Service, List<GameSaveEntry> Entries)> DiscoverEntriesAsync(
            CancellationToken cancellationToken)
        {
            var steamService = _accountManager.GetSteamService();
            if (string.IsNullOrWhiteSpace(steamService.SteamPath))
                throw new DirectoryNotFoundException("未找到 Steam 安装目录。");
            var settings = _settingsProvider();
            var customFolders = (settings.CustomGameSaveFolders ?? new List<CustomGameSaveDefinition>())
                .Select(item => new CustomGameSaveDefinition
                {
                    Id = item.Id,
                    GameName = item.GameName,
                    FolderPath = item.FolderPath,
                    AccountSteamId = item.AccountSteamId
                })
                .ToList();
            var service = new GameSaveBackupService(
                steamService.SteamPath,
                manualRetention: settings.GameSaveManualRetention,
                safetyRetention: settings.GameSaveSafetyRetention);
            var entries = await service.DiscoverAsync(
                _accountManager.Accounts.ToList(),
                await Task.Run(steamService.GetInstalledGames, cancellationToken),
                customFolders,
                settings.SmartSaveDiscoveryEnabled,
                cancellationToken);
            return (service, entries);
        }

        private static async Task<GameSaveAutoBackupResult> BackupEntriesAsync(
            GameSaveBackupService service,
            IReadOnlyCollection<GameSaveEntry> selected,
            string logPrefix,
            CancellationToken cancellationToken)
        {
            var created = 0;
            var skipped = 0;
            var failed = 0;
            var unavailable = selected.Count(entry => !entry.IsAvailable);
            foreach (var entry in selected.Where(entry => entry.IsAvailable))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await service.BackupAsync(
                        entry,
                        requireExclusiveAccess: true,
                        cancellationToken: cancellationToken);
                    if (result.WasSkipped) skipped++;
                    else created++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    AppLogger.Error($"{logPrefix} failed: {entry.AccountDisplayName} / {entry.GameName}", ex);
                }
            }
            var summary = new GameSaveAutoBackupResult(created, skipped, failed, unavailable, DateTime.Now);
            AppLogger.Info($"{logPrefix} completed: created={created}, skipped={skipped}, failed={failed}, unavailable={unavailable}");
            return summary;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            StopMonitor();
            GC.SuppressFinalize(this);
        }
    }
}
