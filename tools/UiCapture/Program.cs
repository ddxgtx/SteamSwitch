using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamSwitcher;
using SteamSwitcher.Core;
using SteamSwitcher.Models;
using SteamSwitcher.Services;
using SteamSwitcher.ViewModels;
using SteamSwitcher.Views;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var outputPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath("ui-preview.png");
        var view = args.Length > 1 ? args[1].ToLowerInvariant() : "accounts";
        var width = args.Length > 2 && int.TryParse(args[2], out var parsedWidth) ? parsedWidth : 1180;
        var height = args.Length > 3 && int.TryParse(args[3], out var parsedHeight) ? parsedHeight : 800;

        App.SuppressFatalDialogs = true;
        var app = new App();
        app.InitializeComponent();
        ThemeManager.ApplyTheme(args.Length > 4 ? args[4] : "Light");

        if (view is "taskbar-band-smoke" or "taskbar-band" or "taskbar-band-hover")
        {
            var bandWindow = new TaskbarBandWindow(new AccountManager());
            bandWindow.SetLyricsSettings(true, true, false, 320);
            var previewIcon = Path.GetFullPath(Path.Combine("src", "SteamSwitcher", "Resources", "icon.png"));
            bandWindow.SetPinnedGames(new List<GameListViewModel>
            {
                new(730, "Counter-Strike 2") { IconPath = previewIcon, HasBinding = true, BindingAccountName = "主账号" }
            });
            bandWindow.SetPinnedAccounts(new List<SteamAccount>
            {
                new() { SteamId = "76561198000000001", AccountName = "main_account", PersonaName = "主账号", AvatarPath = previewIcon }
            });
            bandWindow.SetPreviewMedia(new TaskbarMediaSnapshot(true, true, "一整片海洋都在你的眼睛里", "房东的猫 · 世界/青年", "LRCLIB 同步歌词", File.ReadAllBytes(previewIcon), "下一句会随着旋律轻轻浮现", 0.42));
            var bandSize = new Size(Math.Max(1, bandWindow.Width), Math.Max(1, bandWindow.Height));
            var bandRoot = (FrameworkElement)bandWindow.Content;
            bandRoot.Measure(bandSize);
            bandRoot.Arrange(new Rect(0, 0, bandSize.Width, bandSize.Height));
            bandRoot.UpdateLayout();
            if (view == "taskbar-band-hover")
            {
                bandWindow.SetPreviewControlsVisible(true);
                bandRoot.UpdateLayout();
            }
            if (view != "taskbar-band-smoke")
            {
                var captureWidth = Math.Max(1, (int)Math.Ceiling(bandWindow.Width));
                var captureHeight = Math.Max(1, (int)Math.Ceiling(bandWindow.Height));
                var capture = new RenderTargetBitmap(captureWidth, captureHeight, 96, 96, PixelFormats.Pbgra32);
                capture.Render(bandRoot);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(capture));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using var output = File.Create(outputPath);
                png.Save(output);
            }
            bandWindow.Close();
            Console.WriteLine(view == "taskbar-band-smoke" ? "TaskbarBandWindow XAML loaded successfully." : outputPath);
            return;
        }

        if (view is "update-dialog-smoke" or "update-dialog")
        {
            using var updateService = new UpdateService();
            var dialog = new UpdateDialog(updateService, new UpdateInfo
            {
                Version = "2.7.1",
                Changelog = "界面清晰度与响应式布局优化",
                ExpectedSha256 = new string('A', 64)
            });
            var dialogRoot = (FrameworkElement)dialog.Content;
            var dialogSize = new Size(dialog.Width, dialog.Height);
            dialogRoot.Measure(dialogSize);
            dialogRoot.Arrange(new Rect(dialogSize));
            dialogRoot.UpdateLayout();
            if (view == "update-dialog")
                SaveElement(dialogRoot, (int)dialog.Width, (int)dialog.Height, outputPath);
            dialog.Close();
            Console.WriteLine(view == "update-dialog-smoke" ? "UpdateDialog XAML loaded successfully." : outputPath);
            return;
        }

        if (view is "workflow-dialog-smoke" or "workflow-dialog")
        {
            var workflowViewModel = new MainViewModel();
            var dialog = new GameLaunchWorkflowWindow(workflowViewModel, new GameListViewModel(730, "Counter-Strike 2"));
            var dialogRoot = (FrameworkElement)dialog.Content;
            var dialogSize = new Size(dialog.Width, dialog.Height);
            dialogRoot.Measure(dialogSize);
            dialogRoot.Arrange(new Rect(dialogSize));
            dialogRoot.UpdateLayout();
            if (view == "workflow-dialog")
                SaveElement(dialogRoot, (int)dialog.Width, (int)dialog.Height, outputPath);
            dialog.Close();
            workflowViewModel.Dispose();
            Console.WriteLine(view == "workflow-dialog-smoke" ? "GameLaunchWorkflowWindow XAML loaded successfully." : outputPath);
            return;
        }

        if (view == "custom-save-dialog")
        {
            var dialog = new CustomSaveFolderDialog(new[]
            {
                new SteamAccount { SteamId = "1", AccountName = "main_account", PersonaName = "主账号" },
                new SteamAccount { SteamId = "2", AccountName = "alt_blue", PersonaName = "备用账号 01" }
            });
            var dialogRoot = (FrameworkElement)dialog.Content;
            var dialogSize = new Size(dialog.Width, dialog.Height);
            dialogRoot.Measure(dialogSize);
            dialogRoot.Arrange(new Rect(dialogSize));
            dialogRoot.UpdateLayout();
            SaveElement(dialogRoot, (int)dialog.Width, (int)dialog.Height, outputPath);
            dialog.Close();
            Console.WriteLine(outputPath);
            return;
        }

        var window = new MainWindow(false)
        {
            Width = width,
            Height = height
        };

        var viewModel = (MainViewModel)window.DataContext;
        AddPreviewAccount(viewModel, "main_account", "主账号", true);
        AddPreviewAccount(viewModel, "alt_blue", "备用账号 01", false);
        AddPreviewAccount(viewModel, "alt_night", "备用账号 02", false);
        AddPreviewAccount(viewModel, "family_share", "家庭共享", false);
        AddPreviewAccount(viewModel, "work_profile", "工作账号", false);
        AddPreviewAccount(viewModel, "guest_profile", "访客账号", false);
        viewModel.SelectedAccount = viewModel.Accounts[0];
        viewModel.IsSteamRunning = true;
        AddPreviewGame(viewModel, 730, "Counter-Strike 2", "主账号", true, "今天 19:20");
        AddPreviewGame(viewModel, 578080, "PUBG: BATTLEGROUNDS", "备用账号 01", false, "昨天 22:14");
        AddPreviewGame(viewModel, 271590, "Grand Theft Auto V", "备用账号 02", false, "08-08 21:05");
        AddPreviewGame(viewModel, 1172470, "Apex Legends", "备用账号 01", true, "08-07 18:42");
        AddPreviewGame(viewModel, 1245620, "ELDEN RING", null, false, null);
        viewModel.SelectedGame = viewModel.GameList[0];
        viewModel.SelectedBindingAccount = viewModel.Accounts[0];
        if (view == "quick-populated")
        {
            AddPreviewQuickLaunch(viewModel, "Discord", @"C:\Program Files\Discord\Discord.exe", true, true);
            AddPreviewQuickLaunch(viewModel, "OBS Studio", @"C:\Program Files\obs-studio\bin\64bit\obs64.exe", false, false);
            AddPreviewQuickLaunch(viewModel, "SteamDB", @"C:\Users\Preview\Desktop\SteamDB.url", false, false);
            AddPreviewQuickLaunch(viewModel, "Visual Studio Code", @"C:\Program Files\Microsoft VS Code\Code.exe", true, false);
            AddPreviewQuickLaunch(viewModel, "Spotify", @"C:\Users\Preview\AppData\Roaming\Spotify\Spotify.exe", false, false);
            AddPreviewQuickLaunch(viewModel, "Notion", @"C:\Users\Preview\AppData\Local\Programs\Notion\Notion.exe", false, false);
        }
        viewModel.StatusText = "v2.7 · 示例数据已就绪";

        var navigationMethod = view switch
        {
            "games" => "SwitchToGames",
            "saves" => "SwitchToSaveCenter",
            "taskbar" => "SwitchToTransparentTaskbar",
            "lyrics" => "SwitchToTaskbarLyrics",
            "quick" or "quick-populated" => "SwitchToQuickLaunch",
            "settings" => "SwitchToSettings",
            _ => "SwitchToAccounts"
        };
        var minimalModeField = typeof(MainViewModel).GetField(
            "_minimalMode",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        minimalModeField?.SetValue(viewModel, true);
        if (view == "saves" && window.FindName("SaveCenterHost") is System.Windows.Controls.ContentControl saveHost)
        {
            var saveCenter = new GameSaveCenterWindow(viewModel);
            var previewAssetRoot = Path.GetFullPath(Path.Combine(".codex-qa", "assets"));
            var cs2Icon = Path.Combine(previewAssetRoot, "cs2.png");
            var pubgIcon = Path.Combine(previewAssetRoot, "pubg.png");
            var eldenRingIcon = Path.Combine(previewAssetRoot, "elden-ring.png");
            saveCenter.SetPreviewEntries(new[]
            {
                new GameSaveEntry { EntryId = "steam:1:730", SteamId = "1", AccountName = "main_account", PersonaName = "主账号", AppId = 730, GameName = "Counter-Strike 2", IconPath = cs2Icon, SavePath = @"C:\Program Files (x86)\Steam\userdata\1\730", FileCount = 42, TotalSize = 18_874_368, LastWriteTime = DateTime.Now.AddMinutes(-18), BackupCount = 4, LastBackupTime = DateTime.Now.AddHours(-2) },
                new GameSaveEntry { EntryId = "steam:2:578080", SteamId = "2", AccountName = "alt_blue", PersonaName = "备用账号 01", AppId = 578080, GameName = "PUBG: BATTLEGROUNDS", IconPath = pubgIcon, SavePath = @"C:\Program Files (x86)\Steam\userdata\2\578080", FileCount = 19, TotalSize = 7_602_176, LastWriteTime = DateTime.Now.AddDays(-1), BackupCount = 2, LastBackupTime = DateTime.Now.AddDays(-1) },
                new GameSaveEntry { EntryId = "detected:1245620", SteamId = "detected", AccountName = "本机通用存档", AppId = 1245620, GameName = "ELDEN RING", IconPath = eldenRingIcon, SavePath = @"C:\Users\Preview\AppData\Roaming\EldenRing", FileCount = 8, TotalSize = 31_457_280, LastWriteTime = DateTime.Now.AddHours(-5), IsCustom = true, IsDetected = true, DetectionRule = "AppData Saves", BackupCount = 1, LastBackupTime = DateTime.Now.AddDays(-3) }
            });
            saveHost.Content = saveCenter;
        }
        typeof(MainWindow)
            .GetMethod(navigationMethod, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null);
        minimalModeField?.SetValue(viewModel, false);
        if (view == "games" && window.FindName("GameEmptyHint") is UIElement gameEmptyHint)
            gameEmptyHint.Visibility = Visibility.Collapsed;

        var root = (FrameworkElement)window.FindName("RootBorder");
        var size = new Size(width, height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
        viewModel.Dispose();

        Console.WriteLine(outputPath);
    }

    private static void SaveElement(FrameworkElement element, int width, int height, string outputPath)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static void AddPreviewAccount(
        MainViewModel viewModel,
        string accountName,
        string personaName,
        bool mostRecent)
    {
        var account = new SteamAccount
        {
            SteamId = $"7656119800000000{viewModel.Accounts.Count + 1}",
            AccountName = accountName,
            PersonaName = personaName,
            MostRecent = mostRecent,
            Timestamp = DateTimeOffset.UtcNow.AddHours(-(viewModel.Accounts.Count * 7 + 1)).ToUnixTimeSeconds(),
            AvatarPath = Path.Combine(AppContext.BaseDirectory, "Resources", "icon.png")
        };
        viewModel.Accounts.Add(new AccountViewModel(account));
    }

    private static void AddPreviewGame(
        MainViewModel viewModel,
        int appId,
        string name,
        string? accountName,
        bool pinned,
        string? lastPlayed)
    {
        viewModel.GameList.Add(new GameListViewModel(appId, name)
        {
            IconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "icon.png"),
            SizeDisplay = appId % 2 == 0 ? "38.6 GB" : "74.2 GB",
            HasBinding = !string.IsNullOrWhiteSpace(accountName),
            BindingAccountName = accountName,
            BindingLastPlayed = lastPlayed,
            IsPinned = pinned
        });
    }

    private static void AddPreviewQuickLaunch(
        MainViewModel viewModel,
        string name,
        string executablePath,
        bool pinned,
        bool withIcon)
    {
        viewModel.QuickLaunchList.Add(new QuickLaunchViewModel
        {
            Id = $"preview-{viewModel.QuickLaunchList.Count + 1}",
            Name = name,
            ExecutablePath = executablePath,
            IconPath = withIcon ? Path.Combine(AppContext.BaseDirectory, "Resources", "icon.png") : null,
            IsPinned = pinned,
            SortOrder = viewModel.QuickLaunchList.Count
        });
    }
}
