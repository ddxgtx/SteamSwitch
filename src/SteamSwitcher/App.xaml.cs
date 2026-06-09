using System;
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

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"发生未处理的异常:\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
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
                    $"发生严重错误:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "Steam Switch - 错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
