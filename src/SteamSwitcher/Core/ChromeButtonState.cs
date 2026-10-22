using System.Windows;

namespace SteamSwitcher.Core;

/// <summary>
/// 手工驱动的标题栏按钮视觉状态。
/// Win11 的贴靠布局（Snap Layouts）只在 <c>WM_NCHITTEST</c> 回报 <c>HTMAXBUTTON</c> 时
/// 才会在悬停最大化按钮时弹出，而命中测试一旦被 Win32 接管，按钮就再也收不到鼠标事件——
/// <see cref="UIElement.IsMouseOver"/> / <see cref="Primitives.ButtonBase.IsPressed"/>
/// 会永远停在 false。由主窗口的 WndProc 维护这两个附加属性，按钮模板里的
/// DataTrigger 消费它们，视觉语言与普通路径完全一致。
/// </summary>
internal static class ChromeButtonState
{
    public static readonly DependencyProperty IsHotProperty = DependencyProperty.RegisterAttached(
        "IsHot", typeof(bool), typeof(ChromeButtonState), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsPressedProperty = DependencyProperty.RegisterAttached(
        "IsPressed", typeof(bool), typeof(ChromeButtonState), new FrameworkPropertyMetadata(false));

    public static bool GetIsHot(DependencyObject target) => (bool)target.GetValue(IsHotProperty);

    public static void SetIsHot(DependencyObject target, bool value) => target.SetValue(IsHotProperty, value);

    public static bool GetIsPressed(DependencyObject target) => (bool)target.GetValue(IsPressedProperty);

    public static void SetIsPressed(DependencyObject target, bool value) => target.SetValue(IsPressedProperty, value);
}
