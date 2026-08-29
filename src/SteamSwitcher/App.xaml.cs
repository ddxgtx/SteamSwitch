using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using SteamSwitcher.Services;

namespace SteamSwitcher
{
    public partial class App : Application
    {
        private SingleInstanceManager? _singleInstance;

        public static bool IsAutorunLaunch { get; private set; }
        public static bool SuppressFatalDialogs { get; set; }

        public App()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                base.OnStartup(e);

                if (e.Args.Any(arg => string.Equals(
                        arg, "--register-autostart", StringComparison.OrdinalIgnoreCase)))
                {
                    SettingsService.SetStartWithWindows(true);
                    Shutdown();
                    return;
                }

                if (e.Args.Any(arg => string.Equals(
                        arg, "--unregister-autostart", StringComparison.OrdinalIgnoreCase)))
                {
                    SettingsService.SetStartWithWindows(false);
                    Shutdown();
                    return;
                }

                IsAutorunLaunch = e.Args.Any(arg => string.Equals(
                    arg, "--autorun", StringComparison.OrdinalIgnoreCase));
                SuppressFatalDialogs = IsAutorunLaunch;

                _singleInstance = new SingleInstanceManager();
                if (!_singleInstance.IsPrimaryInstance)
                {
                    if (!_singleInstance.SignalPrimaryInstance(e.Args))
                    {
                        AppLogger.Warn("Unable to forward the activation request to the primary instance.");
                        if (!IsAutorunLaunch)
                        {
                            MessageBox.Show(
                                "Steam Switch 已在运行，但暂时无法连接到现有窗口。\n请稍候重试；若问题持续，可在任务管理器结束旧进程后重新启动。",
                                "Steam Switch",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        }
                    }
                    Shutdown();
                    return;
                }

                if (e.Args.Any(arg => string.Equals(
                        arg, "--shutdown-for-update", StringComparison.OrdinalIgnoreCase)))
                {
                    Shutdown();
                    return;
                }

                var settings = SettingsService.Load();
                ThemeManager.ApplyTheme(settings.Theme);

                var mainWindow = new MainWindow(IsAutorunLaunch, e.Args);
                MainWindow = mainWindow;
                _singleInstance.StartListening(arguments =>
                    Dispatcher.BeginInvoke(new Action(() => mainWindow.HandleExternalRequest(arguments))));
                if (IsAutorunLaunch)
                    mainWindow.StartInBackground();
                else
                    mainWindow.Show();
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() => Shutdown(-1)));
                WriteCrashLog(ex);
                if (!SuppressFatalDialogs)
                {
                    MessageBox.Show(FormatException(ex), "Steam Switch - 启动错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstance?.Dispose();
            base.OnExit(e);
        }

        private static string FormatException(Exception ex)
        {
            var msg = ex.GetType().Name + ": " + ex.Message;
            var inner = ex.InnerException;
            while (inner != null)
            {
                msg += "\n\n→ " + inner.GetType().Name + ": " + inner.Message;
                inner = inner.InnerException;
            }
            msg += "\n\n" + ex.StackTrace;
            return msg;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrashLog(e.Exception);
            if (!SuppressFatalDialogs)
            {
                MessageBox.Show(
                    FormatException(e.Exception),
                    "Steam Switch - 错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            // Continuing after an unknown UI-thread exception can leave WPF in a
            // partially rendered state (for example a black or non-interactive window).
            // Exit cleanly so the next launch starts from a known-good state.
            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(() => Shutdown(-1)));
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                WriteCrashLog(ex);
            }

            // This callback can run on an arbitrary worker thread while the CLR is
            // already terminating. Showing a modal WPF dialog here can deadlock exit
            // or throw a second exception; the crash log is the reliable notification.
        }

        private static void WriteCrashLog(Exception ex)
        {
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SteamSwitch", "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllText(logPath, FormatException(ex));
            }
            catch
            {
                // Crash reporting must never trigger a second fatal exception.
            }
        }
    }
}
