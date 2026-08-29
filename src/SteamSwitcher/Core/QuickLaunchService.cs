using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
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
        private readonly SemaphoreSlim _saveGate = new(1, 1);

        public event EventHandler? ItemsChanged;

        public QuickLaunchService()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SteamSwitch", "quicklaunch.json"))
        {
        }

        internal QuickLaunchService(string configPath)
        {
            _configPath = configPath;
        }

        public async Task LoadAsync()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var loaded = await AtomicJsonFile.ReadAsync<List<QuickLaunchItem>>(_configPath) ?? new();
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
                await _saveGate.WaitAsync();
                try
                {
                    // Snapshot under the serialization gate so a delayed older caller cannot
                    // overwrite the file after a newer mutation has already been persisted.
                    List<QuickLaunchItem> snapshot;
                    lock (_saveLock)
                        snapshot = _items.Values.OrderBy(x => x.SortOrder).Select(CloneItem).ToList();

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
                AppLogger.Error("Failed to save quick launch items.", ex);
            }
        }

        public async Task<QuickLaunchItem> AddAsync(string name, string launchPath, string? args = null, string? iconPath = null)
        {
            var item = new QuickLaunchItem
            {
                Name = name,
                ExecutablePath = launchPath,
                Arguments = args,
                IconPath = iconPath ?? ExtractIconPath(launchPath),
                WorkingDirectory = ResolveWorkingDirectory(launchPath)
            };

            lock (_saveLock)
            {
                item.SortOrder = _items.Count;
                _items[item.Id] = item;
            }
            await SaveAsync();
            ItemsChanged?.Invoke(this, EventArgs.Empty);
            return item;
        }

        public async Task RemoveAsync(string id)
        {
            bool removed;
            lock (_saveLock) removed = _items.TryRemove(id, out _);
            if (removed)
            {
                await SaveAsync();
                ItemsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public async Task TogglePinAsync(string id)
        {
            var changed = false;
            lock (_saveLock)
            {
                if (_items.TryGetValue(id, out var item))
                {
                    item.IsPinned = !item.IsPinned;
                    changed = true;
                }
            }
            if (changed)
            {
                await SaveAsync();
                ItemsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public async Task ReorderAsync(List<string> orderedIds)
        {
            lock (_saveLock)
            {
                for (int i = 0; i < orderedIds.Count; i++)
                {
                    if (_items.TryGetValue(orderedIds[i], out var item))
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

        private static QuickLaunchItem CloneItem(QuickLaunchItem value) => new()
        {
            Id = value.Id,
            Name = value.Name,
            ExecutablePath = value.ExecutablePath,
            Arguments = value.Arguments,
            IconPath = value.IconPath,
            WorkingDirectory = value.WorkingDirectory,
            IsPinned = value.IsPinned,
            SortOrder = value.SortOrder,
            DateAdded = value.DateAdded
        };

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

                var iconSource = ResolveIconSource(exePath);
                var icon = Icon.ExtractAssociatedIcon(iconSource);
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

        private static string? ResolveWorkingDirectory(string launchPath)
        {
            var shortcut = TryReadShortcut(launchPath);
            if (!string.IsNullOrWhiteSpace(shortcut?.WorkingDirectory) &&
                Directory.Exists(shortcut.WorkingDirectory))
            {
                return shortcut.WorkingDirectory;
            }

            if (!string.IsNullOrWhiteSpace(shortcut?.TargetPath))
            {
                var targetDir = Path.GetDirectoryName(shortcut.TargetPath);
                if (!string.IsNullOrWhiteSpace(targetDir) && Directory.Exists(targetDir))
                    return targetDir;
            }

            return IsShellShortcut(launchPath) ? null : Path.GetDirectoryName(launchPath);
        }

        private static string ResolveIconSource(string launchPath)
        {
            var shortcut = TryReadShortcut(launchPath);
            var iconSource = ParseIconLocation(shortcut?.IconLocation)
                ?? shortcut?.TargetPath
                ?? launchPath;

            iconSource = Environment.ExpandEnvironmentVariables(iconSource).Trim().Trim('"');
            return File.Exists(iconSource) ? iconSource : launchPath;
        }

        private static string? ParseIconLocation(string? iconLocation)
        {
            if (string.IsNullOrWhiteSpace(iconLocation))
                return null;

            var path = Environment.ExpandEnvironmentVariables(iconLocation).Trim().Trim('"');
            var commaIndex = path.LastIndexOf(',');
            if (commaIndex > 0)
                path = path[..commaIndex].Trim().Trim('"');

            return File.Exists(path) ? path : null;
        }

        private static bool IsShortcut(string path)
        {
            return Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsShellShortcut(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase);
        }

        private static ShortcutInfo? TryReadShortcut(string path)
        {
            if (!IsShortcut(path) || !File.Exists(path))
                return null;

            object? shell = null;
            object? shortcut = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                    return null;

                shell = Activator.CreateInstance(shellType);
                if (shell == null)
                    return null;

                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { path });

                if (shortcut == null)
                    return null;

                var shortcutType = shortcut.GetType();
                return new ShortcutInfo(
                    ReadShortcutString(shortcutType, shortcut, "TargetPath"),
                    ReadShortcutString(shortcutType, shortcut, "WorkingDirectory"),
                    ReadShortcutString(shortcutType, shortcut, "IconLocation"));
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to read shortcut: {path}, {ex.Message}");
                return null;
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        private static string? ReadShortcutString(Type shortcutType, object shortcut, string propertyName)
        {
            return shortcutType.InvokeMember(
                propertyName,
                System.Reflection.BindingFlags.GetProperty,
                null,
                shortcut,
                null) as string;
        }

        private static void ReleaseComObject(object? comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
                Marshal.FinalReleaseComObject(comObject);
        }

        private sealed record ShortcutInfo(string? TargetPath, string? WorkingDirectory, string? IconLocation);

        public void Dispose()
        {
            // The semaphore intentionally lives for the process lifetime so an in-flight save
            // can complete safely while the window is closing.
        }
    }
}
