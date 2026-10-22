using System;
using System.Windows;

namespace SteamSwitcher.Core;

/// <summary>
/// 任务栏浮窗（音量 HUD / 播放控制岛）的定位运算。纯函数，单位一律为 DIP。
/// 浮窗锚在歌词岛封面正上方，而歌词岛完全可能贴在屏幕左右边缘——用户把入口钉在
/// 最左/最右，或一堆托盘图标把乐队挤到边上——原来的"锚点居中"会把浮窗半个身子
/// 送出屏幕，视觉上直接被裁掉一截。
/// </summary>
internal static class FlyoutPlacement
{
    /// <summary>
    /// 把提议的浮窗矩形夹回 <paramref name="bounds"/> 内，四周保留 <paramref name="margin"/>。
    /// 窗口比边界还大时（理论上的极端情况）优先保证左上角落在边界内。
    /// </summary>
    public static Rect Clamp(Rect proposed, Rect bounds, double margin = 8)
    {
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return proposed;

        var minLeft = bounds.Left + margin;
        var minTop = bounds.Top + margin;
        var left = Math.Clamp(proposed.Left, minLeft, Math.Max(minLeft, bounds.Right - proposed.Width - margin));
        var top = Math.Clamp(proposed.Top, minTop, Math.Max(minTop, bounds.Bottom - proposed.Height - margin));
        return new Rect(left, top, proposed.Width, proposed.Height);
    }

    /// <summary>
    /// 当前虚拟屏幕范围（DIP）。WPF 的 SystemParameters 系列本身就是 DIP 单位。
    /// </summary>
    public static Rect VirtualScreen() => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);
}
