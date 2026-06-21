using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamSwitcher.Core
{
    public static class MemoryLimiter
    {
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        public static void CleanMemory()
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch
            {
                // Ignore exceptions
            }
        }
    }
}
