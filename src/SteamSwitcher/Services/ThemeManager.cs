using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SteamSwitcher.Services
{
    public static class ThemeManager
    {
        public static void ApplyTheme(string theme)
        {
            var app = Application.Current;
            if (app == null) return;

            var r = app.Resources;
            var d = theme == "Dark";

            // Synchronize WPF-UI controls before applying Steam Switch color tokens.
            var applicationTheme = d ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(applicationTheme, WindowBackdropType.None, updateAccent: false);
            ApplicationAccentColorManager.Apply(C(0x0A, 0x84, 0xFF), applicationTheme, false);

            // Window / Card backgrounds
            Set(r, "WindowBgBrush", d ? C(0x0F, 0x16, 0x20) : C(0xF3, 0xF6, 0xFB));
            Set(r, "CardBgBrush", d ? C(0x19, 0x23, 0x30) : C(0xFF, 0xFF, 0xFF));
            Set(r, "CardBgAltBrush", d ? C(0x14, 0x1C, 0x27) : C(0xF7, 0xF9, 0xFC));

            // Sidebar
            Set(r, "SidebarBgBrush", d ? C(0x12, 0x1A, 0x25) : C(0xF8, 0xFA, 0xFD));
            Set(r, "SidebarActiveBgBrush", d ? C(0x1C, 0x34, 0x50) : C(0xEA, 0xF4, 0xFF));
            Set(r, "SidebarActiveFgBrush", d ? C(0x55, 0xAA, 0xFF) : C(0x0A, 0x84, 0xFF));
            Set(r, "SidebarInactiveFgBrush", d ? C(0xA8, 0xB3, 0xC2) : C(0x4B, 0x55, 0x65));

            // Text
            Set(r, "TextPrimaryBrush", d ? C(0xF5, 0xF8, 0xFC) : C(0x10, 0x14, 0x1B));
            Set(r, "TextSecondaryBrush", d ? C(0xAC, 0xB7, 0xC5) : C(0x64, 0x70, 0x83));
            Set(r, "TextTertiaryBrush", d ? C(0x9A, 0xA7, 0xB8) : C(0x75, 0x81, 0x96));

            // Accent
            Set(r, "AccentBrush", C(0x0A, 0x84, 0xFF));
            Set(r, "AccentBgBrush", d ? CA(48, 10, 132, 255) : CA(22, 10, 132, 255));
            Set(r, "AccentBorderBrush", d ? CA(118, 67, 160, 255) : CA(82, 10, 132, 255));
            Set(r, "AccentHoverBrush", d ? C(0x28, 0x96, 0xFF) : C(0x00, 0x76, 0xE8));
            Set(r, "FocusRingBrush", d ? CA(130, 90, 174, 255) : CA(102, 10, 132, 255));

            // Border / Separator
            Set(r, "BorderBrush", d ? C(0x2A, 0x38, 0x49) : C(0xDC, 0xE4, 0xEF));
            Set(r, "SeparatorBrush", d ? C(0x23, 0x30, 0x40) : C(0xE8, 0xED, 0xF4));
            Set(r, "SurfaceHighlightBrush", d ? C(0x21, 0x2C, 0x3A) : C(0xFF, 0xFF, 0xFF));
            Set(r, "SurfaceTintBrush", d ? CA(246, 25, 35, 48) : CA(245, 255, 255, 255));

            // Input / Control backgrounds
            Set(r, "InputBgBrush", d ? C(0x16, 0x1D, 0x28) : C(0xFF, 0xFF, 0xFF));

            // Search
            Set(r, "SearchPlaceholderFgBrush", d ? C(0x7F, 0x8A, 0x99) : C(0x96, 0x9D, 0xA8));
            Set(r, "SearchTextFgBrush", d ? C(0xFF, 0xFF, 0xFF) : C(0x11, 0x13, 0x18));

            // Hover / Selected states (semi-transparent)
            Set(r, "HoverBgBrush", d ? C(0x23, 0x2D, 0x3B) : C(0xF0, 0xF4, 0xFA));
            Set(r, "SelectedBgBrush", d ? C(0x1E, 0x3C, 0x5E) : C(0xEB, 0xF5, 0xFF));
            Set(r, "SelectedBgBrush2", d ? C(0x20, 0x43, 0x6C) : C(0xDD, 0xEC, 0xFF));

            // Success
            Set(r, "SuccessBgBrush", d ? CA(30, 48, 209, 88) : CA(24, 48, 209, 88));
            Set(r, "SuccessFgBrush", d ? C(0x30, 0xD1, 0x58) : C(0x30, 0xD1, 0x58));
            Set(r, "WarningBgBrush", d ? CA(40, 255, 159, 10) : CA(24, 255, 159, 10));
            Set(r, "WarningFgBrush", d ? C(0xFF, 0xB1, 0x40) : C(0xD9, 0x78, 0x00));
            Set(r, "DangerBgBrush", d ? CA(42, 255, 69, 58) : CA(22, 255, 69, 58));
            Set(r, "DangerFgBrush", d ? C(0xFF, 0x69, 0x61) : C(0xE7, 0x38, 0x2F));

            // Game icon / Avatar placeholder
            Set(r, "IconBgBrush", d ? C(0x18, 0x2C, 0x45) : C(0xEB, 0xF5, 0xFF));
            Set(r, "AvatarBgBrush", d ? C(0x3A, 0x3A, 0x3C) : C(0xE2, 0xEB, 0xF5));

            // Toggle button backgrounds
            Set(r, "ToggleBgBrush", d ? C(0x17, 0x17, 0x1B) : C(0xE2, 0xE7, 0xEE));
            Set(r, "ToggleHoverBgBrush", d ? C(0x2F, 0x3D, 0x4A) : C(0xD5, 0xDD, 0xE6));
            Set(r, "ToggleCheckedBgBrush", d ? C(0x1A, 0x3D, 0x5F) : C(0x30, 0xD1, 0x58));

            // Button hover
            Set(r, "BtnHoverBgBrush", d ? C(0x2C, 0x2C, 0x2E) : C(0xE0, 0xE0, 0xE2));
            Set(r, "BtnDisabledBgBrush", d ? C(0x11, 0x11, 0x11) : C(0xF0, 0xF3, 0xF7));
            Set(r, "BtnDisabledFgBrush", d ? CA(85, 255, 255, 255) : C(0x96, 0x9D, 0xA8));
            Set(r, "BtnSecondaryFgBrush", d ? C(0xFF, 0xFF, 0xFF) : C(0x11, 0x13, 0x18));

            // Toggle switch
            Set(r, "ToggleTrackBgBrush", d ? CA(38, 255, 255, 255) : C(0xD5, 0xDC, 0xE5));
            Set(r, "ToggleTrackBorderBrush", d ? CA(52, 255, 255, 255) : C(0xCC, 0xD4, 0xDE));
            Set(r, "ToggleThumbBrush", d ? C(0xF6, 0xF6, 0xF6) : C(0xFF, 0xFF, 0xFF));
            Set(r, "ToggleTrackCheckedBgBrush", d ? CA(102, 10, 132, 255) : C(0x30, 0xD1, 0x58));
            Set(r, "ToggleTrackCheckedBorderBrush", d ? CA(146, 207, 232, 255) : C(0x28, 0xB8, 0x4C));

            // Progress bar
            Set(r, "ProgressBgBrush", d ? C(0x38, 0x38, 0x3A) : C(0xE5, 0xEA, 0xF0));

            // Footer text
            Set(r, "FooterTextBrush", d ? C(0x38, 0x38, 0x3A) : C(0x6C, 0x72, 0x7D));
            Set(r, "FooterLinkBrush", d ? C(0x48, 0x48, 0x4A) : C(0x0A, 0x84, 0xFF));

            // Scrollbar
            Set(r, "ScrollThumbBrush", d ? CA(68, 255, 255, 255) : CA(48, 20, 31, 48));
            Set(r, "ScrollThumbHoverBrush", d ? CA(102, 255, 255, 255) : CA(80, 20, 31, 48));
            Set(r, "ScrollThumbDragBrush", d ? CA(136, 152, 152, 157) : CA(112, 20, 31, 48));
        }

        private static Color C(byte rr, byte gg, byte bb) => Color.FromRgb(rr, gg, bb);
        private static Color CA(byte aa, byte rr, byte gg, byte bb) => Color.FromArgb(aa, rr, gg, bb);

        private static void Set(ResourceDictionary r, string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            r[key] = brush;
        }
    }
}
