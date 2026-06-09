using System;
using System.Diagnostics;
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
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading AutoLoginUser from registry: {ex.Message}");
                return null;
            }
        }

        public void SetAutoLoginUser(string username)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SteamRegistryPath, true);
                if (key != null)
                {
                    key.SetValue("AutoLoginUser", username, RegistryValueKind.String);
                    key.SetValue("RememberPassword", 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法写入注册表: {ex.Message}", ex);
            }
        }

        public string? GetSteamPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SteamRegistryPath);
                return key?.GetValue("SteamPath") as string;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading SteamPath from registry: {ex.Message}");
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
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading SteamExe from registry: {ex.Message}");
                return null;
            }
        }
    }
}
