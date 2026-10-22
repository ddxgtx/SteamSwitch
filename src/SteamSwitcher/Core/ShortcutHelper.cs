using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SteamSwitcher.Core
{
    /// <summary>
    /// Shared Windows shortcut creation helpers used by the account and game
    /// shortcut services. Both services used to carry byte-identical copies of
    /// these methods; keeping them here avoids drift between the two.
    /// </summary>
    internal static class ShortcutHelper
    {
        /// <summary>
        /// Replaces characters that are invalid in file names and falls back to
        /// <paramref name="fallbackName"/> when nothing usable remains.
        /// </summary>
        public static string SanitizeFileName(string value, string fallbackName)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string((value ?? string.Empty).Trim()
                .Select(character => invalid.Contains(character) ? '_' : character)
                .ToArray()).Trim();

            return string.IsNullOrWhiteSpace(sanitized) ? fallbackName : sanitized;
        }

        /// <summary>
        /// Returns a path that does not exist yet, appending " (2)", " (3)" …
        /// to the file name before falling back to a GUID suffix.
        /// </summary>
        public static string GetUniquePath(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
                return path;

            var name = Path.GetFileNameWithoutExtension(fileName);
            for (var index = 2; index < 1000; index++)
            {
                path = Path.Combine(directory, $"{name} ({index}).lnk");
                if (!File.Exists(path))
                    return path;
            }

            return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}).lnk");
        }

        /// <summary>
        /// Finds an existing shortcut pointing at <paramref name="executable"/>
        /// with <paramref name="arguments"/> so repeated creation does not pile
        /// up duplicates. Returns null when no match exists or WScript.Shell is
        /// unavailable.
        /// </summary>
        public static string? FindExistingShortcut(
            string directory,
            string fileName,
            string executable,
            string arguments)
        {
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var candidates = new[] { Path.Combine(directory, fileName) }
                .Concat(Enumerable.Range(2, 998).Select(index => Path.Combine(directory, $"{baseName} ({index}).lnk")))
                .Where(File.Exists);

            object? shell = null;
            object? shortcut = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                    return null;

                shell = Activator.CreateInstance(shellType);
                dynamic dynamicShell = shell!;
                foreach (var candidate in candidates)
                {
                    try
                    {
                        shortcut = dynamicShell.CreateShortcut(candidate);
                        dynamic dynamicShortcut = shortcut;
                        var target = (string?)dynamicShortcut.TargetPath;
                        var currentArguments = (string?)dynamicShortcut.Arguments;
                        if (string.Equals(target, executable, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals((currentArguments ?? string.Empty).Trim(), arguments, StringComparison.OrdinalIgnoreCase))
                            return candidate;
                    }
                    catch { }
                    finally
                    {
                        if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                        shortcut = null;
                    }
                }
            }
            catch { }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }

            return null;
        }

        /// <summary>
        /// Writes a .lnk via WScript.Shell. The icon path is quoted because
        /// WScript.Shell truncates icon locations at the first comma.
        /// </summary>
        public static void SaveShortcut(
            string shortcutPath,
            string executable,
            string arguments,
            string iconPath,
            string description)
        {
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
                dynamicShortcut.Arguments = arguments;
                dynamicShortcut.WorkingDirectory = AppContext.BaseDirectory;
                dynamicShortcut.IconLocation = $"\"{iconPath}\",0";
                dynamicShortcut.Description = description;
                dynamicShortcut.Save();
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
