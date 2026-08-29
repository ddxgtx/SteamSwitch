using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SteamSwitcher.Services;

namespace SteamSwitcher.Core
{
    internal static class GameProcessMonitor
    {
        private const uint Th32csSnapProcess = 0x00000002;
        private static readonly HashSet<string> SteamInfrastructureProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "steamservice", "steamwebhelper", "gameoverlayui",
            "steamerrorreporter", "steamerrorreporter64"
        };

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
            var knownExecutableNames = FindExecutableNames(normalizedRoot);
            var tracked = new HashSet<int>();
            var launchDeadline = DateTime.UtcNow.AddMinutes(5);

            while (DateTime.UtcNow < launchDeadline && tracked.Count == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = CaptureSnapshot();
                tracked.UnionWith(FindLaunchCandidates(
                    snapshot,
                    normalizedRoot,
                    knownExecutableNames,
                    processIdsBeforeLaunch));
                if (tracked.Count == 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            if (tracked.Count == 0)
            {
                AppLogger.Warn($"Game process was not detected for install root {normalizedRoot}");
                return false;
            }

            AppLogger.Info($"Monitoring game process tree: {string.Join(",", tracked.OrderBy(id => id))}");
            var emptyChecks = 0;
            var handoffDeadline = DateTime.UtcNow.AddMinutes(2);
            while (emptyChecks < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                var snapshot = CaptureSnapshot();
                var processTreeRoots = new HashSet<int>(tracked);
                tracked.UnionWith(snapshot.Values
                    .Where(item => IsDescendantOf(item.Id, processTreeRoots, snapshot))
                    .Select(item => item.Id));
                if (DateTime.UtcNow < handoffDeadline)
                {
                    tracked.UnionWith(FindLaunchCandidates(
                        snapshot,
                        normalizedRoot,
                        knownExecutableNames,
                        processIdsBeforeLaunch));
                }
                tracked.RemoveWhere(id => !snapshot.ContainsKey(id));
                emptyChecks = tracked.Count == 0 ? emptyChecks + 1 : 0;
            }
            return true;
        }

        private static HashSet<int> FindLaunchCandidates(
            IReadOnlyDictionary<int, ProcessInfo> snapshot,
            string installRoot,
            ISet<string> executableNames,
            ISet<int> processIdsBeforeLaunch)
        {
            var steamRoots = snapshot.Values
                .Where(item => string.Equals(item.Name, "steam", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Id)
                .ToHashSet();
            var result = new HashSet<int>();
            foreach (var item in snapshot.Values)
            {
                var isNew = !processIdsBeforeLaunch.Contains(item.Id);
                if (!isNew)
                    continue;
                var pathMatches = !string.IsNullOrWhiteSpace(item.Path) &&
                    Path.GetFullPath(item.Path).StartsWith(installRoot, StringComparison.OrdinalIgnoreCase);
                var isKnownSteamChild = executableNames.Contains(item.Name) &&
                    !SteamInfrastructureProcesses.Contains(item.Name) &&
                    IsDescendantOf(item.Id, steamRoots, snapshot);
                if (pathMatches || isKnownSteamChild)
                    result.Add(item.Id);
            }
            return result;
        }

        private static HashSet<string> FindExecutableNames(string installRoot)
        {
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                return Directory.Exists(installRoot)
                    ? Directory.EnumerateFiles(installRoot, "*.exe", options)
                        .Take(4096)
                        .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Unable to enumerate game executables under {installRoot}: {ex.Message}");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static bool IsDescendantOf(
            int processId,
            ISet<int> roots,
            IReadOnlyDictionary<int, ProcessInfo> snapshot)
        {
            var visited = new HashSet<int>();
            var current = processId;
            while (snapshot.TryGetValue(current, out var process) && visited.Add(current))
            {
                if (roots.Contains(process.ParentId))
                    return true;
                current = process.ParentId;
            }
            return false;
        }

        private static Dictionary<int, ProcessInfo> CaptureSnapshot()
        {
            var parentIds = CaptureParentIds();
            var result = new Dictionary<int, ProcessInfo>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch { }
                    result[process.Id] = new ProcessInfo(
                        process.Id,
                        parentIds.GetValueOrDefault(process.Id),
                        name,
                        path);
                }
                catch
                {
                    // Protected and already-exited processes can disappear during a snapshot.
                }
                finally
                {
                    process.Dispose();
                }
            }
            return result;
        }

        private static Dictionary<int, int> CaptureParentIds()
        {
            var result = new Dictionary<int, int>();
            var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == new IntPtr(-1))
                return result;

            try
            {
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                if (!Process32First(snapshot, ref entry))
                    return result;
                do
                {
                    result[unchecked((int)entry.ProcessId)] = unchecked((int)entry.ParentProcessId);
                    entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
                }
                while (Process32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return result;
        }

        private sealed record ProcessInfo(int Id, int ParentId, string Name, string? Path);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
