using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamSwitcher.ViewModels
{
    public partial class QuickLaunchViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _id = "";

        [ObservableProperty]
        private string _name = "";

        [ObservableProperty]
        private string _executablePath = "";

        [ObservableProperty]
        private string? _iconPath;

        [ObservableProperty]
        private bool _isPinned;

        [ObservableProperty]
        private int _sortOrder;
    }
}
