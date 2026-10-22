using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SteamSwitcher.Services;

namespace SteamSwitcher
{
    public partial class App : Application
    {
        private SingleInstanceManager? _singleInstance;
        private MainWindow? _applicationMainWindow;
        private int _recentUnhandledCount;
        private long _recentUnhandledWindowStartTick;
        private readonly bool _uiPreview;

        public static bool IsAutorunLaunch { get; private set; }
        public static bool SuppressFatalDialogs { get; set; }

        public App() : this(uiPreview: false)
        {
        }

        internal App(bool uiPreview)
        {
            _uiPreview = uiPreview;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                base.OnStartup(e);
                if (_uiPreview) return;

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

                var isUpdatedLaunch = e.Args.Any(arg => string.Equals(
                    arg, "--updated", StringComparison.OrdinalIgnoreCase));
                // A silent in-app update is followed by the installer launching
                // --updated. Keep that restart in the tray when the checkpoint
                // proves it came from an update, so the upgrade remains
                // unobtrusive. A normal first install has no checkpoint and
                // still opens the main window as usual.
                IsAutorunLaunch = e.Args.Any(arg => string.Equals(
                    arg, "--autorun", StringComparison.OrdinalIgnoreCase)) ||
                    (isUpdatedLaunch && UpdateService.HasPendingUpdateCheckpoint());
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

                // 上次导入被中断留下的恢复记录如果本身损坏，恢复过程会抛异常。
                // 异常绝不能冒泡到外层 catch：那会 Shutdown(-1) 并弹致命错误，
                // 用户此后每次启动都进不去。清掉无法采信的记录后照常启动。
                try
                {
                    ConfigurationBundleService.RecoverInterruptedImport(SettingsService.SettingsDirectory);
                }
                catch (Exception recoverEx)
                {
                    AppLogger.Error("Interrupted configuration import could not be recovered; starting with the current configuration.", recoverEx);
                    ConfigurationBundleService.DiscardInterruptedImport(SettingsService.SettingsDirectory);
                }
                // 上次中断的下载/未完成的安装会留下陈旧安装包与 .download 残留，
                // 只在安装成功后清理是不够的——这里每次启动兜一次底。
                try { UpdateService.CleanupOldUpdatePackages(); }
                catch (Exception cleanupEx) { AppLogger.Warn($"Unable to clean stale update packages: {cleanupEx.Message}"); }

                var settings = SettingsService.Load();
                ThemeManager.ApplyTheme(settings.Theme);

                var mainWindow = new MainWindow(IsAutorunLaunch, e.Args);
                _applicationMainWindow = mainWindow;
                MainWindow = mainWindow;
                _singleInstance.StartListening(arguments =>
                {
                    try
                    {
                        if (!mainWindow.Dispatcher.HasShutdownStarted &&
                            !mainWindow.Dispatcher.HasShutdownFinished)
                            mainWindow.Dispatcher.BeginInvoke(
                                new Action(() => mainWindow.HandleExternalRequest(arguments)));
                    }
                    catch (InvalidOperationException)
                    {
                        // Ignore activation requests arriving during shutdown.
                    }
                });
                if (IsAutorunLaunch)
                    mainWindow.StartInBackground();
                else
                    mainWindow.Show();

                // The installer leaves a checkpoint before replacing files. Only
                // clear it after the new process has reached a usable window;
                // a startup crash therefore remains visible to diagnostics.
                if (isUpdatedLaunch)
                    UpdateService.CompletePendingUpdateCheckpoint();
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
            // Shutdown can originate from a fatal error, the installer or the OS,
            // without going through the tray's explicit Exit command.
            ExitCleanup.Run(
                ("main window final cleanup", () => _applicationMainWindow?.CleanupForApplicationExit()),
                ("single-instance listener", () => _singleInstance?.Dispose()),
                ("application exception subscriptions", () =>
                {
                    DispatcherUnhandledException -= OnDispatcherUnhandledException;
                    AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
                    TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
                }));
            AppLogger.Info($"Application exit completed. ExitCode={e.ApplicationExitCode}");
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

            // Recoverable strategy (changed from the previous immediate-Exit
            // policy): this is a long-lived tray utility, so a single unhandled
            // UI exception should not tear down the process. Mark handled and
            // keep running. Only exit when failures cluster (5 within one
            // minute), which usually means a broken dispatcher/UI loop.
            e.Handled = true;

            var now = Environment.TickCount64;
            if (now - _recentUnhandledWindowStartTick > 60_000)
            {
                _recentUnhandledWindowStartTick = now;
                _recentUnhandledCount = 0;
            }

            _recentUnhandledCount++;
            if (_recentUnhandledCount >= 5)
            {
                Dispatcher.BeginInvoke(new Action(() => Shutdown(-1)));
            }
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

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            // Background media, backup and update tasks may outlive a window.
            // Observe the exception so finalizer-time escalation cannot take
            // down the process, while retaining a diagnostic record.
            e.SetObserved();
            if (e.Exception is Exception ex)
                WriteCrashLog(ex);
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
