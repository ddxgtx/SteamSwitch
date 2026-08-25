using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    public class GameBinding
    {
        public int AppId { get; set; }
        public string GameName { get; set; } = string.Empty;
        public string? AccountSteamId { get; set; }
        public string? AccountName { get; set; }
        public bool AutoSwitch { get; set; } = true;
        public DateTime? LastPlayed { get; set; }
        public string LaunchArguments { get; set; } = string.Empty;
        public bool BackupBeforeLaunch { get; set; }
        public bool BackupAfterExit { get; set; }
        public bool CloseSteamAfterExit { get; set; }
        public bool StartInBigPicture { get; set; }
    }

    public class GameAccountBinding
    {
        private readonly string _configPath;
        private readonly ConcurrentDictionary<int, GameBinding> _bindings = new();
        private readonly object _saveLock = new();
        private readonly SemaphoreSlim _saveGate = new(1, 1);

        public event EventHandler? BindingsChanged;
        public string ConfigPath => _configPath;

        public GameAccountBinding()
        {
            _configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SteamSwitch",
                "gamebindings.json");
        }

        public async Task LoadAsync()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var loaded = await AtomicJsonFile.ReadAsync<Dictionary<int, GameBinding>>(_configPath)
                               ?? new Dictionary<int, GameBinding>();
                    _bindings.Clear();
                    foreach (var kvp in loaded)
                    {
                        _bindings.TryAdd(kvp.Key, kvp.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to load game bindings.", ex);
                _bindings.Clear();
            }
        }

        public async Task SaveAsync()
        {
            try
            {
                Dictionary<int, GameBinding> snapshot;
                lock (_saveLock)
                {
                    snapshot = new Dictionary<int, GameBinding>(_bindings);
                }

                await _saveGate.WaitAsync();
                try
                {
                    await AtomicJsonFile.WriteAsync(
                        _configPath,
                        snapshot,
                        new JsonSerializerOptions { WriteIndented = true });
                }
                finally
                {
                    _saveGate.Release();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to save game bindings.", ex);
            }
        }

        public GameBinding? GetBinding(int appId)
        {
            return _bindings.TryGetValue(appId, out var binding) ? binding : null;
        }

        public Dictionary<int, GameBinding> GetAllBindings()
        {
            return new Dictionary<int, GameBinding>(_bindings);
        }

        public async Task SetBindingAsync(int appId, string gameName, string accountSteamId, string accountName)
        {
            var previous = GetBinding(appId);
            _bindings[appId] = new GameBinding
            {
                AppId = appId,
                GameName = gameName,
                AccountSteamId = accountSteamId,
                AccountName = accountName,
                AutoSwitch = true,
                LastPlayed = previous?.LastPlayed,
                LaunchArguments = previous?.LaunchArguments ?? string.Empty,
                BackupBeforeLaunch = previous?.BackupBeforeLaunch ?? false,
                BackupAfterExit = previous?.BackupAfterExit ?? false,
                CloseSteamAfterExit = previous?.CloseSteamAfterExit ?? false,
                StartInBigPicture = previous?.StartInBigPicture ?? false
            };
            await SaveAsync();
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task RemoveBindingAsync(int appId)
        {
            _bindings.TryRemove(appId, out _);
            await SaveAsync();
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task RecordPlayAsync(int appId, string accountSteamId, string accountName)
        {
            if (_bindings.TryGetValue(appId, out var binding))
            {
                binding.AccountSteamId = accountSteamId;
                binding.AccountName = accountName;
                binding.LastPlayed = DateTime.Now;
            }
            else
            {
                _bindings[appId] = new GameBinding
                {
                    AppId = appId,
                    AccountSteamId = accountSteamId,
                    AccountName = accountName,
                    AutoSwitch = true,
                    LastPlayed = DateTime.Now
                };
            }
            await SaveAsync();
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task SetAutoSwitchAsync(int appId, bool autoSwitch)
        {
            if (_bindings.TryGetValue(appId, out var binding))
            {
                binding.AutoSwitch = autoSwitch;
                await SaveAsync();
            }
        }

        public async Task SetLaunchWorkflowAsync(
            int appId,
            string launchArguments,
            bool backupBeforeLaunch,
            bool backupAfterExit,
            bool closeSteamAfterExit,
            bool startInBigPicture)
        {
            if (!_bindings.TryGetValue(appId, out var binding))
                throw new InvalidOperationException("请先为该游戏保存账号绑定。");

            binding.LaunchArguments = launchArguments.Trim();
            binding.BackupBeforeLaunch = backupBeforeLaunch;
            binding.BackupAfterExit = backupAfterExit;
            binding.CloseSteamAfterExit = closeSteamAfterExit;
            binding.StartInBigPicture = startInBigPicture;
            await SaveAsync();
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        }

    }
}
