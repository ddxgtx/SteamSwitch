namespace SteamSwitcher.Models
{
    public enum DesktopIconHideMode
    {
        Smart,
        Timed,
        Manual
    }

    public enum DesktopIconAnimationType
    {
        Fade,
        Scale,
        None
    }

    /// <summary>Persisted options for the desktop icon experience.</summary>
    public sealed class DesktopEnhanceSettings
    {
        public bool EnableAutoHideIcons { get; set; }
        public DesktopIconHideMode HideMode { get; set; } = DesktopIconHideMode.Smart;
        public int IdleSeconds { get; set; } = 30;
        public bool RestoreByMouseMove { get; set; } = true;
        public bool RestoreByTaskbarClick { get; set; } = true;
        public bool ToggleIconsOnDesktopClick { get; set; }
        public DesktopIconAnimationType AnimationType { get; set; } = DesktopIconAnimationType.Fade;
        public int AnimationDuration { get; set; } = 300;
        public bool EnableImmersiveMode { get; set; }
    }
}
