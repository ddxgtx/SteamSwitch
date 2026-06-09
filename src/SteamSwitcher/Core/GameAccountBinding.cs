using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

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
    }

    public class GameAccountBinding
    {
        private readonly string _configPath;
        private Dictionary<int, GameBinding> _bindings = new();

        public event EventHandler? BindingsChanged;

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
                    var json = await File.ReadAllTextAsync(_configPath);
                    _bindings = JsonSerializer.Deserialize<Dictionary<int, GameBinding>>(json) 
                               ?? new Dictionary<int, GameBinding>();
                }
            }
            catch
            {
                _bindings = new Dictionary<int, GameBinding>();
            }
        }

        public async Task SaveAsync()
        {
            try
            {
                var dir = Path.GetDirectoryName(_configPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);

                var json = JsonSerializer.Serialize(_bindings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                await File.WriteAllTextAsync(_configPath, json);
            }
            catch { }
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
            _bindings[appId] = new GameBinding
            {
                AppId = appId,
                GameName = gameName,
                AccountSteamId = accountSteamId,
                AccountName = accountName,
                AutoSwitch = true,
                LastPlayed = DateTime.Now
            };
            await SaveAsync();
            BindingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task RemoveBindingAsync(int appId)
        {
            _bindings.Remove(appId);
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
    }
}
