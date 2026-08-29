using System;
using System.Linq;
using Microsoft.Win32;

namespace SteamSwitcher.Core
{
    public class RegistryHelper
    {
        private const string SteamRegistryPath = @"SOFTWARE\Valve\Steam";

        public string? GetAutoLoginUser()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SteamRegistryPath);
                return key?.GetValue("AutoLoginUser") as string;
            }
            catch
            {
                return null;
            }
        }

        public void SetAutoLoginUser(string username)
        {
            object? previousAutoLogin = null;
            object? previousRememberPassword = null;
            var hadAutoLogin = false;
            var hadRememberPassword = false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SteamRegistryPath, true);
                if (key == null)
                    throw new InvalidOperationException($"无法打开注册表项 HKCU\\{SteamRegistryPath}");

                var valueNames = key.GetValueNames();
                hadAutoLogin = valueNames.Contains("AutoLoginUser", StringComparer.OrdinalIgnoreCase);
                hadRememberPassword = valueNames.Contains("RememberPassword", StringComparer.OrdinalIgnoreCase);
                previousAutoLogin = key.GetValue("AutoLoginUser", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                previousRememberPassword = key.GetValue("RememberPassword", null, RegistryValueOptions.DoNotExpandEnvironmentNames);

                try
                {
                    key.SetValue("AutoLoginUser", username, RegistryValueKind.String);
                    key.SetValue("RememberPassword", 1, RegistryValueKind.DWord);
                }
                catch
                {
                    RestoreValue(key, "AutoLoginUser", hadAutoLogin, previousAutoLogin);
                    RestoreValue(key, "RememberPassword", hadRememberPassword, previousRememberPassword);
                    throw;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法写入注册表: {ex.Message}", ex);
            }
        }

        private static void RestoreValue(RegistryKey key, string name, bool existed, object? value)
        {
            if (!existed || value == null)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                return;
            }

            var kind = value switch
            {
                int => RegistryValueKind.DWord,
                long => RegistryValueKind.QWord,
                byte[] => RegistryValueKind.Binary,
                string[] => RegistryValueKind.MultiString,
                _ => RegistryValueKind.String
            };
            key.SetValue(name, value, kind);
        }

        public string? GetSteamPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SteamRegistryPath);
                return key?.GetValue("SteamPath") as string;
            }
            catch
            {
                return null;
            }
        }

        public string? GetSteamExePath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SteamRegistryPath);
                return key?.GetValue("SteamExe") as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
