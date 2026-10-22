using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SteamSwitcher.Services;

/// <summary>Semantic color tokens shared by pages, dialogs and compact desktop surfaces.</summary>
public static class ThemeManager
{
    public static void ApplyTheme(string theme)
    {
        var app = Application.Current;
        if (app == null) return;
        var r = app.Resources;
        var d = theme == "Dark";
        var applicationTheme = d ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(applicationTheme, WindowBackdropType.None, updateAccent: false);
        ApplicationAccentColorManager.Apply((Color)ColorConverter.ConvertFromString(d ? "#6AA7FF" : "#1762D7"), applicationTheme, false);
        Set(r, "WindowBgBrush", d ? "#101722" : "#F3F5F9");
        Set(r, "CardBgBrush", d ? "#192332" : "#FFFFFF");
        Set(r, "MenuSurfaceBrush", d ? "#FA1D2736" : "#FAFCFDFF");
        Set(r, "MenuBorderBrush", d ? "#557D91AE" : "#CCFFFFFF");
        Set(r, "MenuHoverBrush", d ? "#344D6B" : "#E5EEFC");
        r["MenuMaxHeight"] = System.Math.Max(160d, System.Math.Min(480d, SystemParameters.WorkArea.Height - 48d));
        Set(r, "CardBgAltBrush", d ? "#141E2B" : "#F6F8FB");
        Set(r, "SidebarBgBrush", d ? "#121B28" : "#F9FAFC");
        Set(r, "SidebarActiveBgBrush", d ? "#244E82" : "#1762D7");
        Set(r, "SidebarActiveFgBrush", "#FFFFFF");
        Set(r, "SidebarInactiveFgBrush", d ? "#B1C0D4" : "#4F6078");
        Set(r, "TextPrimaryBrush", d ? "#EDF3FC" : "#182438");
        Set(r, "TextSecondaryBrush", d ? "#B3C0D2" : "#53647B");
        Set(r, "TextTertiaryBrush", d ? "#9AACBF" : "#64748B");
        Set(r, "AccentBrush", d ? "#6AA7FF" : "#1762D7");
        Set(r, "AccentForegroundBrush", d ? "#102442" : "#FFFFFF");
        Set(r, "AccentBgBrush", d ? "#223957" : "#EAF1FD");
        Set(r, "AccentBorderBrush", d ? "#496B97" : "#A9C3EB");
        Set(r, "AccentHoverBrush", d ? "#8DBAFF" : "#1254BC");
        Set(r, "FocusRingBrush", d ? "#A06AA7FF" : "#661762D7");
        Set(r, "BorderBrush", d ? "#334257" : "#DCE3ED");
        Set(r, "SubtleBorderBrush", d ? "#33455D" : "#DCE3EE");
        Set(r, "SeparatorBrush", d ? "#2A394D" : "#E9EEF5");
        Set(r, "SurfaceHighlightBrush", d ? "#233247" : "#FFFFFF");
        Set(r, "SurfaceTintBrush", d ? "#172335" : "#F8FAFD");
        Set(r, "PageHeaderBrush", d ? "#EDF3FC" : "#182438");
        Set(r, "SectionLabelBrush", d ? "#B3C0D2" : "#53647B");
        Set(r, "InputBgBrush", d ? "#111C2B" : "#FFFFFF");
        Set(r, "SearchPlaceholderFgBrush", d ? "#93A5BB" : "#63748C");
        Set(r, "SearchTextFgBrush", d ? "#EDF3FC" : "#182438");
        Set(r, "HoverBgBrush", d ? "#24344B" : "#EFF3F9");
        Set(r, "SelectedBgBrush", d ? "#213A5B" : "#EDF3FD");
        Set(r, "SelectedBgBrush2", d ? "#2B486D" : "#E0EBFC");
        Set(r, "SuccessBgBrush", d ? "#173C32" : "#E6F4ED");
        Set(r, "SuccessFgBrush", d ? "#65D6A6" : "#087F5B");
        Set(r, "WarningBgBrush", d ? "#41351F" : "#FFF2D9");
        Set(r, "WarningFgBrush", d ? "#FFD080" : "#925800");
        Set(r, "DangerBgBrush", d ? "#442731" : "#FCECEE");
        Set(r, "DangerFgBrush", d ? "#FF919C" : "#B92D3B");
        Set(r, "IconBgBrush", d ? "#243B5A" : "#EAF1FD");
        Set(r, "AvatarBgBrush", d ? "#2B3D55" : "#E5ECF6");
        Set(r, "ToggleBgBrush", d ? "#26364B" : "#E5EBF4");
        Set(r, "ToggleHoverBgBrush", d ? "#334862" : "#DCE5F1");
        Set(r, "ToggleCheckedBgBrush", d ? "#6AA7FF" : "#1762D7");
        Set(r, "BtnHoverBgBrush", d ? "#293C55" : "#EFF3F9");
        Set(r, "BtnDisabledBgBrush", d ? "#202D40" : "#ECF0F5");
        Set(r, "BtnDisabledFgBrush", d ? "#8496AE" : "#7D8BA0");
        Set(r, "BtnSecondaryFgBrush", d ? "#D5E1F1" : "#263850");
        Set(r, "ToggleTrackBgBrush", d ? "#35465E" : "#CCD5E1");
        Set(r, "ToggleTrackBorderBrush", d ? "#536781" : "#BCC9D9");
        Set(r, "ToggleThumbBrush", d ? "#FFFFFF" : "#FFFFFF");
        Set(r, "ToggleTrackCheckedBgBrush", d ? "#488BEA" : "#1762D7");
        Set(r, "ToggleTrackCheckedBorderBrush", d ? "#78ABF5" : "#1254BC");
        Set(r, "ProgressBgBrush", d ? "#2D3F56" : "#E5EBF4");
        Set(r, "FooterTextBrush", d ? "#A5B6CD" : "#607086");
        Set(r, "FooterLinkBrush", d ? "#8DBAFF" : "#175EC9");
        Set(r, "ScrollThumbBrush", d ? "#668DA2C0" : "#6653647B");
        Set(r, "ScrollThumbHoverBrush", d ? "#998DA2C0" : "#9953647B");
        Set(r, "ScrollThumbDragBrush", d ? "#CC8DA2C0" : "#CC53647B");
        Set(r, "CompactSurfaceBrush", d ? "#F0182434" : "#F0182434");
        Set(r, "CompactBorderBrush", d ? "#364A63" : "#364A63");
        Set(r, "CompactTextPrimaryBrush", d ? "#F4F7FC" : "#F4F7FC");
        Set(r, "CompactTextSecondaryBrush", d ? "#B6C6DA" : "#B6C6DA");
        Set(r, "CompactHoverBrush", d ? "#293C55" : "#293C55");
        Set(r, "CompactPressedBrush", d ? "#344B69" : "#344B69");
        Set(r, "CompactAccentBrush", d ? "#6AA7FF" : "#6AA7FF");
        Set(r, "CompactSubtleBrush", d ? "#66364A63" : "#66364A63");
        // Floating surfaces have their own palette; transparent lyrics retain
        // the high-contrast compact palette when drawn directly on the taskbar.
        Set(r, "FloatingSolidBrush", d ? "#292A30" : "#F4F5F8");
        Set(r, "FloatingTextPrimaryBrush", d ? "#F5F5F7" : "#202127");
        Set(r, "FloatingTextSecondaryBrush", d ? "#C1C2CC" : "#595C69");
        Set(r, "FloatingAccentBrush", d ? "#72B4FF" : "#006AE6");
        Set(r, "FloatingHoverBrush", d ? "#30FFFFFF" : "#38FFFFFF");
        Set(r, "FloatingPressedBrush", d ? "#44000000" : "#1800275C");
        Set(r, "FloatingSubtleBrush", d ? "#26FFFFFF" : "#1C252C44");
        Set(r, "FloatingHighlightBrush", "#70FFFFFF");
        Set(r, "FloatingButtonBrush", "#14FFFFFF");
        Set(r, "GlassTextPrimaryBrush", "#FFFFFFFF");
        Set(r, "GlassTextSecondaryBrush", "#E6F3F5FA");
        Set(r, "GlassAccentBrush", "#A8D4FF");
        SetGradient(r, "FloatingSurfaceBrush", d ? "#48383B46" : "#38FFFFFF", d ? "#30202430" : "#14EAF2FF");
        SetGradient(r, "FloatingBorderBrush", "#A0FFFFFF", "#28FFFFFF");
    }

    private static void SetGradient(ResourceDictionary resources, string key, string top, string bottom)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top),
            (Color)ColorConverter.ConvertFromString(bottom), new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
        brush.Freeze();
        resources[key] = brush;
    }

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        resources[key] = brush;
    }
}
