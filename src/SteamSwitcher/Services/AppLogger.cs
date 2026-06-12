using System;
using System.IO;
using System.Linq;
using System.Text;

namespace SteamSwitcher.Services
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    public static class AppLogger
    {
        private static readonly object SyncRoot = new();
        private static readonly long MaxLogFileSize = 5 * 1024 * 1024; // 5MB
        private static readonly int MaxLogFiles = 5;

        public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        private static string? _cachedLogDirectory;
        private static string? _cachedLogPath;
        private static DateTime _currentDate;
        private static int _writeCount;
        private static bool _directoryCreated;

        public static string LogDirectory => _cachedLogDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamSwitch",
            "logs");

        public static string CurrentLogPath
        {
            get
            {
                var today = DateTime.Now.Date;
                if (_cachedLogPath == null || today != _currentDate)
                {
                    _currentDate = today;
                    _cachedLogPath = Path.Combine(LogDirectory, $"steamswitch-{today:yyyyMMdd}.log");
                }
                return _cachedLogPath;
            }
        }

        public static void Debug(string message)
        {
            if (MinimumLevel <= LogLevel.Debug)
                Write("DEBUG", message);
        }

        public static void Info(string message)
        {
            if (MinimumLevel <= LogLevel.Info)
                Write("INFO", message);
        }

        public static void Warn(string message)
        {
            if (MinimumLevel <= LogLevel.Warn)
                Write("WARN", message);
        }

        public static void Error(string message, Exception? exception = null)
        {
            if (MinimumLevel <= LogLevel.Error)
                Write("ERROR", exception == null ? message : $"{message} | {exception}");
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (SyncRoot)
                {
                    if (!_directoryCreated)
                    {
                        Directory.CreateDirectory(LogDirectory);
                        _directoryCreated = true;
                    }

                    // Only run cleanup/rotation every 100 writes
                    if (_writeCount++ % 100 == 0)
                    {
                        CleanupOldLogs();
                        CheckLogRotation();
                    }

                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never break account switching or injection.
            }
        }

        private static void CheckLogRotation()
        {
            try
            {
                var currentLog = CurrentLogPath;
                if (File.Exists(currentLog))
                {
                    var fileInfo = new FileInfo(currentLog);
                    if (fileInfo.Length > MaxLogFileSize)
                {
                    var timestamp = DateTime.Now.ToString("HHmmss");
                    var rotatedPath = Path.Combine(LogDirectory,
                        $"steamswitch-{DateTime.Now:yyyyMMdd}-{timestamp}.log");
                    File.Move(currentLog, rotatedPath);
                }
                }
            }
            catch
            {
                // Rotation failure should not break logging
            }
        }

        private static void CleanupOldLogs()
        {
            try
            {
                var directory = new DirectoryInfo(LogDirectory);
                if (!directory.Exists) return;

                var logFiles = directory.GetFiles("steamswitch-*.log")
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                while (logFiles.Count > MaxLogFiles)
                {
                    var oldest = logFiles.Last();
                    oldest.Delete();
                    logFiles.Remove(oldest);
                }
            }
            catch
            {
                // Cleanup failure should not break logging
            }
        }
    }
}
