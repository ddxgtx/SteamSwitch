using System;
using System.Diagnostics;

namespace SteamSwitcher.Core
{
    public static class MemoryLimiter
    {
        private const long MinimumPrivateBytes = 160L * 1024 * 1024;
        private static readonly TimeSpan MinimumCleanupInterval = TimeSpan.FromMinutes(5);
        private static DateTime _lastCleanupUtc = DateTime.MinValue;

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
                // 仅做一次受运行时控制的回收；不再强制全代 GC，也不再调用 EmptyWorkingSet：
                // 把工作集换出会造成后续硬缺页与明显卡顿，对常驻托盘程序弊大于利。
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized);
            }
            catch
            {
                // Ignore exceptions
            }
        }
    }
}
