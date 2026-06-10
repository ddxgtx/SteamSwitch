using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SteamSwitcher
{
    public partial class App : Application
    {
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
            }
            catch (Exception ex)
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SteamSwitch", "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllText(logPath, FormatException(ex));
                MessageBox.Show(FormatException(ex), "Steam Switch - 启动错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SteamSwitch", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, FormatException(e.Exception));

            MessageBox.Show(
                FormatException(e.Exception),
                "Steam Switch - 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                MessageBox.Show(
                    FormatException(ex),
                    "Steam Switch - 严重错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
