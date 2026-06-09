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

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool CreateProcess(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_HIDE = 0;
        private const int SW_MINIMIZE = 6;
        private const int SW_SHOWMINNOACTIVE = 7;
        private const int SW_FORCEMINIMIZE = 11;
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint STARTF_USESHOWWINDOW = 0x00000001;
        private const uint CREATE_NEW_CONSOLE = 0x00000010;
        private const short SW_HIDE_SHORT = 0;

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
                if (silent)
                    args += " -silent";

                var startInfo = new ProcessStartInfo
                {
                    FileName = SteamExePath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = silent,  // 无感模式下不创建窗口
                    WindowStyle = silent ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
                };

                Process.Start(startInfo);
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
                var steamProcesses = Process.GetProcessesByName("steam");
                var steamPids = new HashSet<uint>();
                foreach (var p in steamProcesses)
                {
                    try { steamPids.Add((uint)p.Id); } catch { }
                }

                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        GetWindowThreadProcessId(hWnd, out uint pid);
                        if (steamPids.Contains(pid))
                        {
                            ShowWindow(hWnd, SW_MINIMIZE);
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
                var steamProcesses = Process.GetProcessesByName("steam");
                var steamPids = new HashSet<uint>();
                foreach (var p in steamProcesses)
                {
                    try { steamPids.Add((uint)p.Id); } catch { }
                }

                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        GetWindowThreadProcessId(hWnd, out uint pid);
                        if (steamPids.Contains(pid))
                        {
                            ShowWindow(hWnd, SW_HIDE);
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
