using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SteamSwitcher.Core
{
    public class SteamService
    {
        private readonly RegistryHelper _registryHelper = new();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_MINIMIZE = 6;

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
                // 先尝试用 -shutdown 关闭
                try
                {
                    if (!string.IsNullOrEmpty(SteamExePath) && File.Exists(SteamExePath))
                    {
                        Process.Start(SteamExePath, "-shutdown");
                        await Task.Delay(3000);
                    }
                }
                catch { }

                // 如果还在运行，强制结束
                if (IsSteamRunning())
                {
                    var processes = Process.GetProcessesByName("steam");
                    foreach (var process in processes)
                    {
                        try
                        {
                            process.Kill();
                            await process.WaitForExitAsync();
                        }
                        catch { }
                    }
                    await Task.Delay(2000);
                }

                return !IsSteamRunning();
            }
            catch
            {
                return false;
            }
        }

        public bool StartSteam(string? username = null, bool silent = false)
        {
            if (string.IsNullOrEmpty(SteamExePath) || !File.Exists(SteamExePath))
                return false;

            try
            {
                var args = string.Empty;
                if (!string.IsNullOrEmpty(username))
                    args = $"-login {username}";

                Process.Start(SteamExePath, args);
                return true;
            }
            catch
            {
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
