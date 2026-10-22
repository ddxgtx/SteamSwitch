using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamSwitcher.Services
{
    internal static class AtomicJsonFile
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
            new(StringComparer.OrdinalIgnoreCase);

        private static SemaphoreSlim GetPathLock(string path) =>
            PathLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

        /// <summary>
        /// 判断异常是否只是"暂时读不到"（被并发写入占用/文件消失），
        /// 这类情况必须如实上报为 IO 错误，不能被当成"文件损坏"去走 .bak 回退。
        /// </summary>
        private static bool IsTransientIoError(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException;

        // Constructor-time loads must not synchronously block an async method
        // that can capture the WPF dispatcher context.
        public static T? Read<T>(string path, JsonSerializerOptions? options = null)
        {
            var gate = GetPathLock(path);
            gate.Wait();
            try
            {
                return ReadCore<T>(path, options);
            }
            finally
            {
                gate.Release();
            }
        }

        private static T? ReadCore<T>(string path, JsonSerializerOptions? options = null)
        {
            Exception? primaryError = null;
            foreach (var candidate in new[] { path, path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    var value = JsonSerializer.Deserialize<T>(File.ReadAllText(candidate), options);
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
                    if (IsTransientIoError(ex))
                        throw;
                    AppLogger.Warn($"Unable to read configuration {Path.GetFileName(candidate)}: {ex.Message}");
                }
            }
            if (primaryError != null)
                throw new InvalidDataException($"配置文件及其备份均无法读取：{Path.GetFileName(path)}", primaryError);
            return default;
        }

        public static async Task<T?> ReadAsync<T>(string path, JsonSerializerOptions? options = null)
        {
            var gate = GetPathLock(path);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await ReadCoreAsync<T>(path, options).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task<T?> ReadCoreAsync<T>(string path, JsonSerializerOptions? options = null)
        {
            Exception? primaryError = null;
            foreach (var candidate in new[] { path, path + ".bak" })
            {
                if (!File.Exists(candidate))
                    continue;

                try
                {
                    var json = await File.ReadAllTextAsync(candidate).ConfigureAwait(false);
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
                    // 只是读不到（被并发写占用）就立刻如实抛出，不要误判成文件损坏
                    // 而去回退 .bak —— 那会让陈旧备份被当成权威数据写回去。
                    if (IsTransientIoError(ex))
                        throw;
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

            var gate = GetPathLock(path);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
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
                        await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        stream.Flush(flushToDisk: true);
                    }

                    if (!File.Exists(path))
                    {
                        File.Move(temporaryPath, path);
                        return;
                    }

                    var transientBackup = $"{backupPath}.{Guid.NewGuid():N}.tmp";
                    File.Replace(temporaryPath, path, transientBackup, ignoreMetadataErrors: true);
                    var rotated = false;
                    try
                    {
                        File.Move(transientBackup, backupPath, overwrite: true);
                        rotated = true;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"Configuration was saved, but its backup could not be rotated; kept transient copy at {transientBackup}: {ex.Message}");
                    }
                    finally
                    {
                        if (rotated && File.Exists(transientBackup))
                        {
                            try { File.Delete(transientBackup); } catch { }
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
