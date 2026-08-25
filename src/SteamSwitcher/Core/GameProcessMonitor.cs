using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    internal static class GameProcessMonitor
    {
        public static HashSet<int> CaptureProcessIds() =>
            Process.GetProcesses().Select(process =>
            {
                try { return process.Id; }
                finally { process.Dispose(); }
            }).ToHashSet();

        public static async Task<bool> WaitForExitAsync(
            string installRoot,
            ISet<int> processIdsBeforeLaunch,
            CancellationToken cancellationToken)
        {
            var normalizedRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var tracked = new HashSet<int>();
            var launchDeadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < launchDeadline && tracked.Count == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tracked.UnionWith(FindProcesses(normalizedRoot).Where(id => !processIdsBeforeLaunch.Contains(id)));
                if (tracked.Count == 0)
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            if (tracked.Count == 0)
            {
                AppLogger.Warn($"Game process was not detected under {normalizedRoot}");
                return false;
            }

            var emptyChecks = 0;
            while (emptyChecks < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                var running = FindProcesses(normalizedRoot);
                tracked.UnionWith(running.Where(id => !processIdsBeforeLaunch.Contains(id)));
                tracked.RemoveWhere(id => !running.Contains(id));
                emptyChecks = tracked.Count == 0 ? emptyChecks + 1 : 0;
            }
            return true;
        }

        private static HashSet<int> FindProcesses(string installRoot)
        {
            var result = new HashSet<int>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) &&
                        Path.GetFullPath(path).StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
                        result.Add(process.Id);
                }
                catch
                {
                    // Protected and already-exited processes cannot expose MainModule.
                }
                finally
                {
                    process.Dispose();
                }
            }
            return result;
        }
    }
}
