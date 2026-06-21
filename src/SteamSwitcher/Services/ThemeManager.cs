using System.Windows;
using System.Windows.Media;

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

            // Window / Card backgrounds
            Set(r, "WindowBgBrush", d ? C(0x0E, 0x0F, 0x16) : C(0xF5, 0xF5, 0xF7));
            Set(r, "CardBgBrush", d ? CA(16, 120, 160, 255) : C(0xFF, 0xFF, 0xFF));
            Set(r, "CardBgAltBrush", d ? CA(12, 120, 160, 255) : C(0xF0, 0xF0, 0xF2));

            // Sidebar
            Set(r, "SidebarBgBrush", d ? C(0x07, 0x08, 0x0D) : C(0xEB, 0xEB, 0xED));
            Set(r, "SidebarActiveBgBrush", d ? CA(26, 10, 132, 255) : CA(40, 10, 132, 255));
            Set(r, "SidebarActiveFgBrush", d ? C(0x0A, 0x84, 0xFF) : C(0x00, 0x66, 0xCC));
            Set(r, "SidebarInactiveFgBrush", d ? C(0x8E, 0x8E, 0x96) : C(0x6E, 0x6E, 0x76));

            // Text
            Set(r, "TextPrimaryBrush", d ? C(0xFF, 0xFF, 0xFF) : C(0x1D, 0x1D, 0x1F));
            Set(r, "TextSecondaryBrush", d ? C(0x8E, 0x8E, 0x96) : C(0x6E, 0x6E, 0x76));
            Set(r, "TextTertiaryBrush", d ? C(0x6C, 0x6C, 0x72) : C(0xAE, 0xAE, 0xB4));

            // Accent
            Set(r, "AccentBrush", d ? C(0x0A, 0x84, 0xFF) : C(0x00, 0x66, 0xCC));
            Set(r, "AccentBgBrush", d ? CA(26, 10, 132, 255) : CA(20, 0, 102, 204));

            // Border / Separator
            Set(r, "BorderBrush", d ? C(0x16, 0x17, 0x22) : C(0xDD, 0xDD, 0xDF));
            Set(r, "SeparatorBrush", d ? CA(34, 255, 255, 255) : CA(30, 0, 0, 0));

            // Input / Control backgrounds
            Set(r, "InputBgBrush", d ? CA(20, 255, 255, 255) : C(0xFF, 0xFF, 0xFF));

            // Search
            Set(r, "SearchPlaceholderFgBrush", d ? C(0x63, 0x63, 0x66) : C(0xAE, 0xAE, 0xB4));
            Set(r, "SearchTextFgBrush", d ? C(0xFF, 0xFF, 0xFF) : C(0x1D, 0x1D, 0x1F));

            // Hover / Selected states (semi-transparent)
            Set(r, "HoverBgBrush", d ? CA(26, 255, 255, 255) : CA(20, 0, 0, 0));
            Set(r, "SelectedBgBrush", d ? CA(26, 10, 132, 255) : CA(20, 0, 102, 204));
            Set(r, "SelectedBgBrush2", d ? CA(34, 10, 132, 255) : CA(30, 0, 102, 204));

            // Success
            Set(r, "SuccessBgBrush", d ? CA(30, 48, 209, 88) : CA(30, 48, 209, 88));
            Set(r, "SuccessFgBrush", d ? C(0x30, 0xD1, 0x58) : C(0x28, 0xA7, 0x45));

            // Game icon / Avatar placeholder
            Set(r, "IconBgBrush", d ? C(0x18, 0x2C, 0x45) : C(0xE8, 0xEE, 0xF4));
            Set(r, "AvatarBgBrush", d ? C(0x3A, 0x3A, 0x3C) : C(0xE0, 0xE0, 0xE2));

            // Toggle button backgrounds
            Set(r, "ToggleBgBrush", d ? C(0x17, 0x17, 0x1B) : C(0xE8, 0xE8, 0xEA));
            Set(r, "ToggleHoverBgBrush", d ? C(0x2F, 0x3D, 0x4A) : C(0xD0, 0xD8, 0xE0));
            Set(r, "ToggleCheckedBgBrush", d ? C(0x1A, 0x3D, 0x5F) : C(0xCC, 0xDD, 0xEE));

            // Button hover
            Set(r, "BtnHoverBgBrush", d ? C(0x2C, 0x2C, 0x2E) : C(0xE0, 0xE0, 0xE2));
            Set(r, "BtnDisabledBgBrush", d ? C(0x11, 0x11, 0x11) : C(0xE8, 0xE8, 0xE8));
            Set(r, "BtnDisabledFgBrush", d ? CA(85, 255, 255, 255) : CA(128, 0, 0, 0));
            Set(r, "BtnSecondaryFgBrush", d ? C(0xFF, 0xFF, 0xFF) : C(0x1D, 0x1D, 0x1F));

            // Toggle switch
            Set(r, "ToggleTrackBgBrush", d ? CA(38, 255, 255, 255) : C(0xD5, 0xD5, 0xD7));
            Set(r, "ToggleTrackBorderBrush", d ? CA(52, 255, 255, 255) : C(0xC0, 0xC0, 0xC2));
            Set(r, "ToggleThumbBrush", d ? C(0xF6, 0xF6, 0xF6) : C(0xFF, 0xFF, 0xFF));
            Set(r, "ToggleTrackCheckedBgBrush", d ? CA(102, 10, 132, 255) : C(0x00, 0x66, 0xCC));
            Set(r, "ToggleTrackCheckedBorderBrush", d ? CA(146, 207, 232, 255) : CA(146, 150, 200, 255));

            // Progress bar
            Set(r, "ProgressBgBrush", d ? C(0x38, 0x38, 0x3A) : C(0xD0, 0xD0, 0xD2));

            // Footer text
            Set(r, "FooterTextBrush", d ? C(0x38, 0x38, 0x3A) : C(0xC0, 0xC0, 0xC2));
            Set(r, "FooterLinkBrush", d ? C(0x48, 0x48, 0x4A) : C(0xA0, 0xA0, 0xA2));

            // Scrollbar
            Set(r, "ScrollThumbBrush", d ? CA(68, 255, 255, 255) : CA(60, 0, 0, 0));
            Set(r, "ScrollThumbHoverBrush", d ? CA(102, 255, 255, 255) : CA(90, 0, 0, 0));
            Set(r, "ScrollThumbDragBrush", d ? CA(136, 152, 152, 157) : CA(120, 100, 100, 100));
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
