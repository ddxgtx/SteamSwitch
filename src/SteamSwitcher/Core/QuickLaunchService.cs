using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SteamSwitcher.Models;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    public class QuickLaunchService : IDisposable
    {
        private readonly string _configPath;
        private readonly ConcurrentDictionary<string, QuickLaunchItem> _items = new();
        private readonly object _saveLock = new();

        public event EventHandler? ItemsChanged;

        public QuickLaunchService()
        {
            _configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SteamSwitch", "quicklaunch.json");
        }

        public async Task LoadAsync()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = await File.ReadAllTextAsync(_configPath);
                    var loaded = JsonSerializer.Deserialize<List<QuickLaunchItem>>(json) ?? new();
                    _items.Clear();
                    foreach (var item in loaded)
                        _items.TryAdd(item.Id, item);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to load quick launch items.", ex);
            }
        }

        public async Task SaveAsync()
        {
            try
            {
                var dir = Path.GetDirectoryName(_configPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);

                List<QuickLaunchItem> snapshot;
                lock (_saveLock)
                    snapshot = _items.Values.OrderBy(x => x.SortOrder).ToList();

                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_configPath, json);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to save quick launch items.", ex);
            }
        }

        public async Task<QuickLaunchItem> AddAsync(string name, string exePath, string? args = null, string? iconPath = null)
        {
            var item = new QuickLaunchItem
            {
                Name = name,
                ExecutablePath = exePath,
                Arguments = args,
                IconPath = iconPath ?? ExtractIconPath(exePath),
                WorkingDirectory = Path.GetDirectoryName(exePath),
                SortOrder = _items.Count
            };

            _items[item.Id] = item;
            await SaveAsync();
            ItemsChanged?.Invoke(this, EventArgs.Empty);
            return item;
        }

        public async Task RemoveAsync(string id)
        {
            if (_items.TryRemove(id, out _))
            {
                await SaveAsync();
                ItemsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public async Task TogglePinAsync(string id)
        {
            if (_items.TryGetValue(id, out var item))
            {
                item.IsPinned = !item.IsPinned;
                await SaveAsync();
                ItemsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public async Task ReorderAsync(List<string> orderedIds)
        {
            for (int i = 0; i < orderedIds.Count; i++)
            {
                if (_items.TryGetValue(orderedIds[i], out var item))
                {
                    item.SortOrder = i;
                }
            }
            await SaveAsync();
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }

        public List<QuickLaunchItem> GetAll()
        {
            return _items.Values.OrderBy(x => x.SortOrder).ToList();
        }

        public List<QuickLaunchItem> GetPinned()
        {
            return _items.Values.Where(x => x.IsPinned).OrderBy(x => x.SortOrder).ToList();
        }

        public QuickLaunchItem? GetById(string id)
        {
            return _items.TryGetValue(id, out var item) ? item : null;
        }

        public bool Launch(QuickLaunchItem item)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = item.ExecutablePath,
                    UseShellExecute = true
                };

                if (!string.IsNullOrEmpty(item.Arguments))
                    startInfo.Arguments = item.Arguments;

                if (!string.IsNullOrEmpty(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
                    startInfo.WorkingDirectory = item.WorkingDirectory;

                Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to launch {item.Name}: {ex.Message}", ex);
                return false;
            }
        }

        public static string? ExtractIconPath(string exePath)
        {
            try
            {
                if (!File.Exists(exePath)) return null;

                var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon == null) return null;

                var iconDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SteamSwitch", "icons");
                Directory.CreateDirectory(iconDir);

                var iconPath = Path.Combine(iconDir, $"{Path.GetFileNameWithoutExtension(exePath)}.png");
                using (var bitmap = icon.ToBitmap())
                {
                    bitmap.Save(iconPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                icon.Dispose();
                return iconPath;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }
}
