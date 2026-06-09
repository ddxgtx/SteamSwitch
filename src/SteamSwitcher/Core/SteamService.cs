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

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_MINIMIZE = 6;
        private const int SW_HIDE = 0;
        private const int SW_SHOWMINNOACTIVE = 7;
        private const int SW_FORCEMINIMIZE = 11;
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_HIDEWINDOW = 0x0080;

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
                    catch { }
                }

                await Task.Delay(2000);

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
                var args = new List<string>();
                
                if (!string.IsNullOrEmpty(username))
                    args.Add($"-login {username}");
                
                if (silent)
                    args.Add("-silent");

                Process.Start(SteamExePath, string.Join(" ", args));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void MinimizeSteamWindows()
        {
            try
            {
                // 获取Steam进程
                var steamProcesses = Process.GetProcessesByName("steam");
                var steamPids = new HashSet<uint>();
                foreach (var p in steamProcesses)
                {
                    try { steamPids.Add((uint)p.Id); } catch { }
                }

                // 枚举并最小化所有Steam窗口
                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        GetWindowThreadProcessId(hWnd, out uint pid);
                        if (steamPids.Contains(pid))
                        {
                            ShowWindow(hWnd, SW_MINIMIZE);
                            SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0, 
                                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        public void HideAllSteamWindows()
        {
            try
            {
                // 获取Steam进程ID
                var steamProcesses = Process.GetProcessesByName("steam");
                var steamPids = new HashSet<uint>();
                foreach (var p in steamProcesses)
                {
                    try { steamPids.Add((uint)p.Id); } catch { }
                }

                // 枚举所有窗口并隐藏Steam窗口
                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hWnd))
                            return true;

                        GetWindowThreadProcessId(hWnd, out uint pid);
                        
                        if (steamPids.Contains(pid))
                        {
                            ShowWindow(hWnd, SW_HIDE);
                            SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0, 
                                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
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
