using System;
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
        private readonly Dictionary<string, QuickLaunchItem> _items = new(StringComparer.Ordinal);
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
            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var loaded = await AtomicJsonFile.ReadAsync<List<QuickLaunchItem>>(_configPath).ConfigureAwait(false);
                if (loaded == null && (File.Exists(_configPath) || File.Exists(_configPath + ".bak")))
                    throw new InvalidDataException("快速启动配置内容为空，请恢复配置后重新读取。");
                loaded ??= new();
                var snapshot = new Dictionary<string, QuickLaunchItem>(StringComparer.Ordinal);
                foreach (var item in loaded)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Id) || !snapshot.TryAdd(item.Id, CloneItem(item)))
                        throw new InvalidDataException("快速启动配置包含空记录或重复标识，原有条目已保留。");
                }
                ReplaceSnapshot(snapshot);
                _loadFailed = false;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to load quick launch items.", ex);
                // 读取失败时不再清空：后续任意一次增删都会触发 SaveAsync，
                // 把空列表写回磁盘，用户的项目就这样"凭空消失"了。
                _loadFailed = true;
                throw;
            }
            finally { _saveGate.Release(); }
        }

        /// <summary>加载失败后拒绝自动落盘，避免用空列表覆盖磁盘上的真实数据。</summary>
        private volatile bool _loadFailed;

        public async Task SaveAsync() => await MutateAsync(null);

        private async Task<bool> MutateAsync(Func<Dictionary<string, QuickLaunchItem>, bool>? mutation)
        {
            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_loadFailed) throw new InvalidOperationException("快速启动配置尚未成功读取，请恢复配置并重新加载后再修改。");
                Dictionary<string, QuickLaunchItem> snapshot;
                lock (_saveLock) snapshot = _items.ToDictionary(pair => pair.Key, pair => CloneItem(pair.Value), StringComparer.Ordinal);
                if (mutation != null && !mutation(snapshot)) return false;
                await AtomicJsonFile.WriteAsync(_configPath, snapshot.Values.OrderBy(item => item.SortOrder).ToList(),
                    new JsonSerializerOptions { WriteIndented = true }).ConfigureAwait(false);
                ReplaceSnapshot(snapshot);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to save quick launch items.", ex);
                throw;
            }
            finally { _saveGate.Release(); }
        }

        private void ReplaceSnapshot(Dictionary<string, QuickLaunchItem> snapshot)
        {
            lock (_saveLock)
            {
                _items.Clear();
                foreach (var pair in snapshot) _items.Add(pair.Key, CloneItem(pair.Value));
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

            await MutateAsync(snapshot =>
            {
                // Reindex first so deleting a middle item or importing sparse
                // order values cannot give the newly appended item a duplicate rank.
                var index = 0;
                foreach (var existing in snapshot.Values.OrderBy(value => value.SortOrder)) existing.SortOrder = index++;
                item.SortOrder = index;
                snapshot[item.Id] = item;
                return true;
            });
            ItemsChanged?.Invoke(this, EventArgs.Empty);
            return CloneItem(item);
        }

        public async Task RemoveAsync(string id)
        {
            if (await MutateAsync(snapshot => snapshot.Remove(id)))
            {
                ItemsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public async Task TogglePinAsync(string id)
        {
            if (await MutateAsync(snapshot =>
            {
                if (!snapshot.TryGetValue(id, out var item)) return false;
                item.IsPinned = !item.IsPinned;
                return true;
            }))
            {
                ItemsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public async Task ReorderAsync(List<string> orderedIds)
        {
            await MutateAsync(snapshot =>
            {
                for (int i = 0; i < orderedIds.Count; i++)
                {
                    if (snapshot.TryGetValue(orderedIds[i], out var item))
                        item.SortOrder = i;
                }
                return true;
            });
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }

        public List<QuickLaunchItem> GetAll()
        {
            lock (_saveLock) return _items.Values.OrderBy(x => x.SortOrder).Select(CloneItem).ToList();
        }

        public List<QuickLaunchItem> GetPinned()
        {
            lock (_saveLock) return _items.Values.Where(x => x.IsPinned).OrderBy(x => x.SortOrder).Select(CloneItem).ToList();
        }

        public QuickLaunchItem? GetById(string id)
        {
            lock (_saveLock) return _items.TryGetValue(id, out var item) ? CloneItem(item) : null;
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
                // Launch entries come from quicklaunch.json, so a stale or
                // hand-edited entry must not be handed to ShellExecute blindly.
                if (string.IsNullOrWhiteSpace(item.ExecutablePath) || !File.Exists(item.ExecutablePath))
                {
                    AppLogger.Warn(
                        $"Quick launch item '{item.Name}' targets a missing file and was not started: '{item.ExecutablePath}'");
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = item.ExecutablePath,
                    UseShellExecute = true
                };

                if (!string.IsNullOrEmpty(item.Arguments))
                    startInfo.Arguments = item.Arguments;

                if (!string.IsNullOrEmpty(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
                    startInfo.WorkingDirectory = item.WorkingDirectory;

                using var process = Process.Start(startInfo);
                return process != null;
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
                // using 保证中途抛异常（建目录/保存 PNG 失败）时也释放 GDI 句柄。
                using var icon = Icon.ExtractAssociatedIcon(iconSource);
                if (icon == null) return null;

                var iconDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SteamSwitch", "icons");
                Directory.CreateDirectory(iconDir);

                // Include a short hash of the full source path so same-named
                // executables from different folders do not overwrite each other.
                var pathHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA1.HashData(
                        System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(exePath))))
                    .Substring(0, 8);
                var iconPath = Path.Combine(iconDir, $"{Path.GetFileNameWithoutExtension(exePath)}-{pathHash}.png");
                using (var bitmap = icon.ToBitmap())
                {
                    bitmap.Save(iconPath, System.Drawing.Imaging.ImageFormat.Png);
                }
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

            var path = Environment.ExpandEnvironmentVariables(iconLocation).Trim();
            // Only strip a trailing ",index" when that suffix is purely numeric;
            // otherwise a comma inside a valid path (e.g. "C:\foo, bar\icon.ico")
            // would be truncated.
            var commaIndex = path.LastIndexOf(',');
            if (commaIndex > 0)
            {
                var suffix = path[(commaIndex + 1)..].Trim();
                if (suffix.Length > 0 && suffix.All(char.IsDigit))
                    path = path[..commaIndex].Trim();
            }

            path = path.Trim().Trim('"');
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
