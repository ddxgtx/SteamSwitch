using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private readonly Dictionary<int, GameBinding> _bindings = new();
        private readonly object _saveLock = new();
        private readonly SemaphoreSlim _saveGate = new(1, 1);
        private volatile bool _loadFailed;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public event EventHandler? BindingsChanged;
        public string ConfigPath => _configPath;

        public GameAccountBinding() : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamSwitch", "gamebindings.json")) { }

        internal GameAccountBinding(string configPath) => _configPath = configPath;

        public async Task LoadAsync()
        {
            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var loaded = await AtomicJsonFile.ReadAsync<Dictionary<int, GameBinding>>(_configPath).ConfigureAwait(false);
                if (loaded == null && (File.Exists(_configPath) || File.Exists(_configPath + ".bak")))
                    throw new InvalidDataException("游戏绑定配置内容为空，请恢复配置后重新读取。");
                loaded ??= new();
                if (loaded.Values.Any(binding => binding == null))
                    throw new InvalidDataException("游戏绑定配置包含空记录，原有绑定已保留。");
                ReplaceSnapshot(loaded);
                _loadFailed = false;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                AppLogger.Error("Failed to load game bindings; keeping the last valid snapshot.", ex);
                throw;
            }
            finally { _saveGate.Release(); }
        }

        public async Task SaveAsync() => await MutateAsync(null);

        // Stage a complete snapshot, persist it, then publish it. Failed writes
        // cannot leave a successful-looking in-memory edit or erase known data.
        private async Task<bool> MutateAsync(Func<Dictionary<int, GameBinding>, bool>? mutation)
        {
            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_loadFailed) throw new InvalidOperationException("游戏绑定配置尚未成功读取，请恢复配置并重新加载后再修改。");
                var snapshot = GetAllBindings();
                if (mutation != null && !mutation(snapshot)) return false;
                await AtomicJsonFile.WriteAsync(_configPath, snapshot, JsonOptions).ConfigureAwait(false);
                ReplaceSnapshot(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to save game bindings; keeping the previous snapshot.", ex);
                throw;
            }
            finally { _saveGate.Release(); }
        }

        private void ReplaceSnapshot(Dictionary<int, GameBinding> snapshot)
        {
            lock (_saveLock)
            {
                _bindings.Clear();
                foreach (var pair in snapshot) _bindings.Add(pair.Key, CloneBinding(pair.Value));
            }
        }

        public GameBinding? GetBinding(int appId)
        {
            lock (_saveLock) return _bindings.TryGetValue(appId, out var binding) ? CloneBinding(binding) : null;
        }

        public Dictionary<int, GameBinding> GetAllBindings()
        {
            lock (_saveLock) return _bindings.ToDictionary(pair => pair.Key, pair => CloneBinding(pair.Value));
        }

        public async Task SetBindingAsync(int appId, string gameName, string accountSteamId, string accountName)
        {
            await MutateAsync(snapshot =>
            {
                snapshot.TryGetValue(appId, out var previous);
                var binding = previous ?? new GameBinding { AppId = appId };
                binding.AppId = appId;
                binding.GameName = gameName;
                binding.AccountSteamId = accountSteamId;
                binding.AccountName = accountName;
                binding.AutoSwitch = true;
                snapshot[appId] = binding;
                return true;
            });
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task RemoveBindingAsync(int appId)
        {
            if (await MutateAsync(snapshot => snapshot.Remove(appId))) BindingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task RecordPlayAsync(int appId, string accountSteamId, string accountName)
        {
            await MutateAsync(snapshot =>
            {
                if (!snapshot.TryGetValue(appId, out var binding)) snapshot[appId] = binding = new GameBinding { AppId = appId };
                binding.AccountSteamId = accountSteamId;
                binding.AccountName = accountName;
                binding.LastPlayed = DateTime.Now;
                return true;
            });
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task SetAutoSwitchAsync(int appId, bool autoSwitch)
        {
            if (await MutateAsync(snapshot =>
            {
                if (!snapshot.TryGetValue(appId, out var binding)) return false;
                binding.AutoSwitch = autoSwitch;
                return true;
            })) BindingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task SetLaunchWorkflowAsync(int appId, string launchArguments, bool backupBeforeLaunch,
            bool backupAfterExit, bool closeSteamAfterExit, bool startInBigPicture)
        {
            await MutateAsync(snapshot =>
            {
                if (!snapshot.TryGetValue(appId, out var binding)) throw new InvalidOperationException("请先为该游戏保存账号绑定。");
                // 调用方可能传 null（未填启动参数时），直接 .Trim() 会抛空引用
                // 并让整个"保存启动工作流"失败。
                binding.LaunchArguments = launchArguments?.Trim() ?? string.Empty;
                binding.BackupBeforeLaunch = backupBeforeLaunch;
                binding.BackupAfterExit = backupAfterExit;
                binding.CloseSteamAfterExit = closeSteamAfterExit;
                binding.StartInBigPicture = startInBigPicture;
                return true;
            });
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        }

        private static GameBinding CloneBinding(GameBinding value) => new()
        {
            AppId = value.AppId, GameName = value.GameName, AccountSteamId = value.AccountSteamId,
            AccountName = value.AccountName, AutoSwitch = value.AutoSwitch, LastPlayed = value.LastPlayed,
            LaunchArguments = value.LaunchArguments, BackupBeforeLaunch = value.BackupBeforeLaunch,
            BackupAfterExit = value.BackupAfterExit, CloseSteamAfterExit = value.CloseSteamAfterExit, StartInBigPicture = value.StartInBigPicture
        };
    }
}
