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
            var safeName = ShortcutHelper.SanitizeFileName(displayName, "Steam Account");
            var shortcutName = $"Steam - {safeName}.lnk";
            var arguments = $"--switch-account {account.SteamId} --launch-steam";
            var shortcutPath = ShortcutHelper.FindExistingShortcut(destinationDirectory, shortcutName, executable, arguments)
                ?? ShortcutHelper.GetUniquePath(destinationDirectory, shortcutName);
            var iconPath = ShortcutIconService.GetOrCreateIcon(account.AvatarPath, $"account-{account.SteamId}") ?? executable;

            ShortcutHelper.SaveShortcut(
                shortcutPath,
                executable,
                arguments,
                iconPath,
                $"切换到 {displayName} 并启动 Steam（由 Steam Switch 创建）");
            return shortcutPath;
        }
    }
}
