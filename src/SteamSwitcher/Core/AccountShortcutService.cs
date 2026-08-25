using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SteamSwitcher.Models;

namespace SteamSwitcher.Core
{
    /// <summary>
    /// Creates password-free Windows shortcuts for Steam account switching.
    /// Feature inspiration: TcNo Account Switcher (GPL-3.0),
    /// https://github.com/TCNOco/TcNo-Acc-Switcher
    /// No TcNo source code or binaries are included or linked.
    /// </summary>
    public static class AccountShortcutService
    {
        public static string CreateDesktopShortcut(SteamAccount account) => CreateShortcut(
            account,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

        public static string CreateShortcut(SteamAccount account, string destinationDirectory)
        {
            ArgumentNullException.ThrowIfNull(account);
            if (string.IsNullOrWhiteSpace(account.SteamId) || !account.SteamId.All(char.IsDigit))
                throw new InvalidDataException("账号缺少有效的 SteamID。");

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new FileNotFoundException("无法确定 Steam Switch 程序路径。", executable);

            Directory.CreateDirectory(destinationDirectory);
            var displayName = string.IsNullOrWhiteSpace(account.PersonaName) ? account.AccountName : account.PersonaName;
            var safeName = SanitizeFileName(displayName);
            var shortcutPath = GetUniquePath(destinationDirectory, $"Steam - {safeName}.lnk");
            var iconPath = ShortcutIconService.GetOrCreateIcon(account.AvatarPath, $"account-{account.SteamId}") ?? executable;

            object? shell = null;
            object? shortcut = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("系统未提供 Windows Script Host。");
                shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("无法创建快捷方式服务。");
                dynamic dynamicShell = shell;
                shortcut = dynamicShell.CreateShortcut(shortcutPath);
                dynamic dynamicShortcut = shortcut;
                dynamicShortcut.TargetPath = executable;
                dynamicShortcut.Arguments = $"--switch-account {account.SteamId} --launch-steam";
                dynamicShortcut.WorkingDirectory = AppContext.BaseDirectory;
                dynamicShortcut.IconLocation = $"{iconPath},0";
                dynamicShortcut.Description = $"切换到 {displayName} 并启动 Steam（由 Steam Switch 创建）";
                dynamicShortcut.Save();
                return shortcutPath;
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(sanitized) ? "Steam Account" : sanitized;
        }

        private static string GetUniquePath(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) return path;
            var name = Path.GetFileNameWithoutExtension(fileName);
            for (var index = 2; index < 1000; index++)
            {
                path = Path.Combine(directory, $"{name} ({index}).lnk");
                if (!File.Exists(path)) return path;
            }
            return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}).lnk");
        }
    }
}
