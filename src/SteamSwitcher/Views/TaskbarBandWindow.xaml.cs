using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamSwitcher.Core;
using SteamSwitcher.Models;

namespace SteamSwitcher.Views
{
    public partial class TaskbarBandWindow : Window
    {
        private readonly AccountManager _accountManager;
        private readonly TaskbarEmbedder _embedder;
        private List<SteamAccount> _pinnedAccounts = new();
        private bool _isPinned;
        private int _avatarSize = 40;
        private bool _glassEnabled = true;
        private bool _roundedMode = true;

        public event EventHandler<SteamAccount>? AccountSwitchRequested;

        public TaskbarBandWindow(AccountManager accountManager)
        {
            InitializeComponent();
            _accountManager = accountManager;
            _embedder = new TaskbarEmbedder();
            _embedder.TaskbarCreated += OnTaskbarCreated;

            Loaded += TaskbarBandWindow_Loaded;
            Closing += TaskbarBandWindow_Closing;
        }

        private void TaskbarBandWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateGlassEffect();
        }

        private void TaskbarBandWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e) => Detach();

        private void OnTaskbarCreated(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() => RefreshAvatars());
        }

        public void SetPinnedAccounts(List<SteamAccount> accounts)
        {
            _pinnedAccounts = accounts ?? new List<SteamAccount>();
            RefreshAvatars();
        }

        public void Attach()
        {
            if (_isPinned) return;
            int width = CalculateWidth();
            _embedder.EmbedWindow(this, width);
            _isPinned = _embedder.IsEmbedded;
            if (_isPinned)
            {
                UpdateGlassEffect();
                RefreshAvatars();
            }
        }

        public void Detach()
        {
            if (!_isPinned) return;
            _embedder.RemoveFromTaskbar();
            _isPinned = false;
        }

        public bool IsPinned => _isPinned;

        public void SetPosition(TaskbarPosition position) => _embedder.Position = position;
        public void SetOffset(int offset) => _embedder.OffsetX = offset;

        public void SetAvatarSize(int size)
        {
            _avatarSize = size;
            RefreshAvatars();
        }

        public void SetGlassEnabled(bool enabled)
        {
            _glassEnabled = enabled;
            UpdateGlassEffect();
        }

        public void SetRoundedMode(bool rounded)
        {
            _roundedMode = rounded;
            RefreshAvatars();
        }

        private int CalculateWidth()
        {
            int padding = 3;
            int count = _pinnedAccounts.Count;
            if (count == 0) count = 1;
            return count * (_avatarSize + padding) + 10;
        }

        private void UpdateGlassEffect()
        {
            if (GlassBorder == null) return;

            if (_glassEnabled)
            {
                GlassBorder.Background = new LinearGradientBrush(
                    Color.FromArgb(48, 255, 255, 255),
                    Color.FromArgb(24, 255, 255, 255),
                    new Point(0, 0), new Point(1, 1));
                GlassBorder.BorderBrush = new LinearGradientBrush(
                    Color.FromArgb(60, 255, 255, 255),
                    Color.FromArgb(15, 255, 255, 255),
                    new Point(0, 0), new Point(0, 1));
            }
            else
            {
                GlassBorder.Background = new SolidColorBrush(Color.FromArgb(40, 28, 28, 30));
                GlassBorder.BorderBrush = Brushes.Transparent;
            }
        }

        private void RefreshAvatars()
        {
            AvatarPanel.Children.Clear();
            foreach (var account in _pinnedAccounts)
            {
                AvatarPanel.Children.Add(CreateAvatarButton(account));
            }
            if (_isPinned) _embedder.UpdatePosition();
        }

        private Border CreateAvatarButton(SteamAccount account)
        {
            int size = _avatarSize;
            int radius = _roundedMode ? size / 4 : 6;

            var border = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(radius),
                Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)),
                Margin = new Thickness(1.5, 0, 1.5, 0),
                Cursor = Cursors.Hand,
                ToolTip = account.PersonaName,
                Tag = account
            };

            var image = new Image
            {
                Stretch = Stretch.UniformToFill
            };

            var avatarPath = account.AvatarPath;
            if (!string.IsNullOrEmpty(avatarPath) && System.IO.File.Exists(avatarPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(avatarPath);
                    bitmap.DecodePixelWidth = size;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image.Source = bitmap;
                }
                catch { }
            }

            image.Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
            border.Child = image;

            bool isCurrent = account == _accountManager.CurrentAccount;
            if (isCurrent)
            {
                border.BorderThickness = new Thickness(2.5);
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            }

            border.MouseLeftButtonDown += (s, e) => AccountSwitchRequested?.Invoke(this, account);

            border.MouseEnter += (s, e) =>
            {
                if (account != _accountManager.CurrentAccount)
                {
                    border.Background = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
                }
            };

            border.MouseLeave += (s, e) =>
            {
                if (account != _accountManager.CurrentAccount)
                {
                    border.Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
                }
            };

            return border;
        }

        public void UpdateCurrentAccount(SteamAccount? current)
        {
            foreach (var child in AvatarPanel.Children)
            {
                if (child is Border border && border.Tag is SteamAccount account)
                {
                    bool isCurrent = account == current;
                    if (isCurrent)
                    {
                        border.BorderThickness = new Thickness(2.5);
                        border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
                    }
                    else
                    {
                        border.BorderThickness = new Thickness(0);
                        border.BorderBrush = Brushes.Transparent;
                    }
                }
            }
        }
    }
}
