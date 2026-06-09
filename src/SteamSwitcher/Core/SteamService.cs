using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SteamSwitcher.Core
{
    public class SteamService
    {
        private readonly RegistryHelper _registryHelper = new();

        public string? SteamPath { get; private set; }
        public string? SteamExePath { get; private set; }

        public bool DetectSteamPath()
        {
            SteamPath = _registryHelper.GetSteamPath();
            SteamExePath = _registryHelper.GetSteamExePath();

            if (!string.IsNullOrEmpty(SteamPath) && Directory.Exists(SteamPath))
                return true;

            var commonPaths = new[]
            {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam",
                @"D:\Steam",
                @"D:\Program Files (x86)\Steam",
                @"E:\Steam"
            };

            foreach (var path in commonPaths)
            {
                if (Directory.Exists(path))
                {
                    SteamPath = path;
                    SteamExePath = Path.Combine(path, "steam.exe");
                    return true;
                }
            }

            return false;
        }

        public bool IsSteamRunning()
        {
            return Process.GetProcessesByName("steam").Length > 0 ||
                   Process.GetProcessesByName("SteamService").Length > 0;
        }

        public async Task<bool> CloseSteamAsync()
        {
            if (!IsSteamRunning())
                return true;

            try
            {
                var processes = Process.GetProcessesByName("steam");
                foreach (var process in processes)
                {
                    try
                    {
                        process.Kill();
                        await process.WaitForExitAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error closing steam process: {ex.Message}");
                    }
                }

                await Task.Delay(2000);

                return !IsSteamRunning();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in CloseSteamAsync: {ex.Message}");
                return false;
            }
        }

        public bool StartSteam(string? username = null)
        {
            if (string.IsNullOrEmpty(SteamExePath) || !File.Exists(SteamExePath))
                return false;

            try
            {
                var args = string.IsNullOrEmpty(username) ? "" : $"-login {username}";
                Process.Start(SteamExePath, args);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error starting Steam: {ex.Message}");
                return false;
            }
        }

        public string GetLoginUsersPath()
        {
            return Path.Combine(SteamPath ?? "", "config", "loginusers.vdf");
        }

        public string GetAvatarCachePath()
        {
            return Path.Combine(SteamPath ?? "", "config", "avatarcache");
        }

        public string GetConfigPath()
        {
            return Path.Combine(SteamPath ?? "", "config");
        }
    }
}
