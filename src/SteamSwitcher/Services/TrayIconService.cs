using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using SteamSwitcher.Core;
using SteamSwitcher.ViewModels;

namespace SteamSwitcher.Services
{
    public class TrayIconService : IDisposable
    {
        private NotifyIcon? _notifyIcon;
        private readonly Window _ownerWindow;
        private System.Windows.Controls.ContextMenu? _popupMenu;
        private ObservableCollection<AccountViewModel>? _accounts;
        private AccountViewModel? _currentAccount;
        private bool _taskbarLyricsEnabled;

        public event EventHandler<string>? AccountSelected;
        public event EventHandler? LaunchSteamRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler<bool>? ToggleTaskbarLyricsRequested;
        public event EventHandler? EmergencyRestoreRequested;
        public event EventHandler<(string Title, string Message, int Timeout)>? NotificationRequested;

        public TrayIconService(Window ownerWindow, bool createNativeIcon = true)
        {
            _ownerWindow = ownerWindow;
            if (createNativeIcon) InitializeTrayIcon();
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new NotifyIcon();
            try
            {
                var iconUri = new Uri("pack://application:,,,/Resources/steam.ico", UriKind.Absolute);
                var resourceInfo = System.Windows.Application.GetResourceStream(iconUri);
                if (resourceInfo != null)
                {
                    using var stream = resourceInfo.Stream;
                    _notifyIcon.Icon = new Icon(stream);
                }
                else
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }

            _notifyIcon.Text = "Steam Switch";
            _notifyIcon.Visible = true;
            _notifyIcon.MouseClick += NotifyIcon_MouseClick;
            _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
        }

        private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowOwnerWindow();
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowContextMenu();
            }
        }

        private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
        {
            ShowOwnerWindow();
        }

        private void ShowOwnerWindow()
        {
            // Calling Show() again on an already-visible window throws
            // InvalidOperationException. If it is already up, just restore and focus it.
            if (_ownerWindow.IsVisible)
            {
                _ownerWindow.WindowState = WindowState.Normal;
                _ownerWindow.Activate();
            }
            else
            {
                _ownerWindow.Show();
                _ownerWindow.WindowState = WindowState.Normal;
                _ownerWindow.Activate();
            }
        }

        // 仅缓存当前账号状态，供右键菜单按需构建。真正的菜单由 SharedContextMenuBuilder
        // 在 ShowContextMenu 时构建为 WPF ContextMenu；这里不再维护第二套 WinForms 菜单
        // （那套菜单从未被显示，却每次刷新都重建并分配 GDI 字体，造成句柄抖动）。
        public void UpdateMenu(ObservableCollection<AccountViewModel> accounts, AccountViewModel? currentAccount)
        {
            _accounts = accounts;
            _currentAccount = currentAccount;
        }

        public void UpdateFeatureStates(bool taskbarLyricsEnabled)
        {
            _taskbarLyricsEnabled = taskbarLyricsEnabled;
        }

        private void ShowContextMenu()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;
            try
            {
                _ = dispatcher.BeginInvoke(new Action(() =>
                {
                    if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                        return;
                    // Close any previously opened popup so repeated right-clicks never
                    // stack multiple ContextMenu instances (each one holds a slot of
                    // native menu handles until it is garbage-collected).
                    if (_popupMenu != null)
                        _popupMenu.IsOpen = false;
                    var menu = SharedContextMenuBuilder.Build(
                    _accounts,
                    _currentAccount,
                    showMainWindow: ShowOwnerWindow,
                    accountSelected: steamId => AccountSelected?.Invoke(this, steamId),
                    launchSteam: () => LaunchSteamRequested?.Invoke(this, EventArgs.Empty),
                    refresh: null,
                    detach: null,
                    showDetach: false,
                    isTaskbarLyricsEnabled: _taskbarLyricsEnabled,
                    toggleTaskbarLyrics: enabled => ToggleTaskbarLyricsRequested?.Invoke(this, enabled),
                    emergencyRestore: () => EmergencyRestoreRequested?.Invoke(this, EventArgs.Empty),
                    exit: () => ExitRequested?.Invoke(this, EventArgs.Empty));

                    menu.Closed += (s, _) =>
                    {
                        if (ReferenceEquals(_popupMenu, menu))
                            _popupMenu = null;
                    };
                    _popupMenu = menu;
                    menu.IsOpen = true;
                }));
            }
            catch (InvalidOperationException) { }
        }

        public void ShowNotification(string title, string message, int timeout = 3000)
        {
            NotificationRequested?.Invoke(this, (title, message, timeout));
        }

        public void Dispose()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Icon?.Dispose();
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            // WPF ContextMenu is not IDisposable; drop the reference so its items
            // (which hold account data) can be reclaimed. It was already closed above.
            _popupMenu = null;
        }
    }
}
