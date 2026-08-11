using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamSwitcher.Core
{
    public static class MemoryLimiter
    {
        private const long MinimumPrivateBytes = 160L * 1024 * 1024;
        private static readonly TimeSpan MinimumCleanupInterval = TimeSpan.FromMinutes(5);
        private static DateTime _lastCleanupUtc = DateTime.MinValue;

        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        public static void CleanMemory()
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now - _lastCleanupUtc < MinimumCleanupInterval)
                    return;

                using var process = Process.GetCurrentProcess();
                if (process.PrivateMemorySize64 < MinimumPrivateBytes)
                    return;

                _lastCleanupUtc = now;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(process.Handle);
            }
            catch
            {
                // Ignore exceptions
            }
        }
    }
}
