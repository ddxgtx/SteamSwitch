using System;

namespace SteamSwitcher.Services;

/// <summary>A failed resource release must not prevent later releases or application shutdown.</summary>
internal static class ExitCleanup
{
    internal static int Run(params (string Name, Action Cleanup)[] steps)
    {
        var failures = 0;
        foreach (var (name, cleanup) in steps)
        {
            try { cleanup(); }
            catch (Exception ex)
            {
                failures++;
                // Logging must not turn a failed cleanup into another exit blocker.
                try { AppLogger.Error($"Exit cleanup failed: {name}.", ex); }
                catch { }
            }
        }
        return failures;
    }
}
