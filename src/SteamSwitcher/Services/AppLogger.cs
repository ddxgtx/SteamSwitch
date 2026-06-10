using System;
using System.IO;
using System.Text;

namespace SteamSwitcher.Services
{
    public static class AppLogger
    {
        private static readonly object SyncRoot = new();

        public static string LogDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamSwitch",
            "logs");

        public static string CurrentLogPath => Path.Combine(
            LogDirectory,
            $"steamswitch-{DateTime.Now:yyyyMMdd}.log");

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Error(string message, Exception? exception = null)
        {
            Write("ERROR", exception == null ? message : $"{message} | {exception}");
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (SyncRoot)
                {
                    Directory.CreateDirectory(LogDirectory);
                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never break account switching or injection.
            }
        }
    }
}
