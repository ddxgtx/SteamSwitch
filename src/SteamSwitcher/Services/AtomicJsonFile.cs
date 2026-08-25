using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamSwitcher.Services
{
    internal static class AtomicJsonFile
    {
        public static async Task<T?> ReadAsync<T>(string path, JsonSerializerOptions? options = null)
        {
            Exception? primaryError = null;
            foreach (var candidate in new[] { path, path + ".bak" })
            {
                if (!File.Exists(candidate))
                    continue;

                try
                {
                    var json = await File.ReadAllTextAsync(candidate);
                    var value = JsonSerializer.Deserialize<T>(json, options);
                    if (value != null)
                    {
                        if (!string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                            AppLogger.Warn($"Recovered configuration from {Path.GetFileName(candidate)}.");
                        return value;
                    }
                }
                catch (Exception ex)
                {
                    primaryError ??= ex;
                    AppLogger.Warn($"Unable to read configuration {Path.GetFileName(candidate)}: {ex.Message}");
                }
            }

            if (primaryError != null)
                throw new InvalidDataException($"配置文件及其备份均无法读取：{Path.GetFileName(path)}", primaryError);
            return default;
        }

        public static async Task WriteAsync<T>(
            string path,
            T value,
            JsonSerializerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("无法确定配置目录。");
            Directory.CreateDirectory(directory);

            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            var backupPath = path + ".bak";
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                if (!File.Exists(path))
                {
                    File.Move(temporaryPath, path);
                    return;
                }

                var transientBackup = $"{backupPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Replace(temporaryPath, path, transientBackup, ignoreMetadataErrors: true);
                    try
                    {
                        File.Move(transientBackup, backupPath, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"Configuration was saved, but its backup could not be rotated: {ex.Message}");
                    }
                }
                finally
                {
                    try { if (File.Exists(transientBackup)) File.Delete(transientBackup); } catch { }
                }
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
    }
}
