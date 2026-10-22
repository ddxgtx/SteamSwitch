using System;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SteamSwitcher.Core;

namespace SteamSwitcher.Tests;

[TestClass]
public class FlyoutPlacementTests
{
    private static readonly Rect Screen = new(0, 0, 1920, 1080);

    [TestMethod]
    public void Clamp_KeepsCenteredFlyoutUntouched()
    {
        var result = FlyoutPlacement.Clamp(new Rect(900, 1000, 180, 54), Screen);

        Assert.AreEqual(900, result.Left);
        Assert.AreEqual(1000, result.Top);
        Assert.AreEqual(180, result.Width);
        Assert.AreEqual(54, result.Height);
    }

    [TestMethod]
    public void Clamp_PullsFlyoutBackInsideTheRightEdge()
    {
        // 乐队被托盘图标挤到最右边：锚点居中会把 HUD 送出屏幕 60+ DIP。
        var result = FlyoutPlacement.Clamp(new Rect(1850, 1000, 190, 54), Screen);

        Assert.AreEqual(1920 - 8 - 190, result.Left, "右缘必须留在屏幕内并保留呼吸位");
        Assert.AreEqual(1000, result.Top);
    }

    [TestMethod]
    public void Clamp_PullsFlyoutBackInsideTheLeftEdge()
    {
        // 入口钉在最左：同样会向左溢出。
        var result = FlyoutPlacement.Clamp(new Rect(-40, 1000, 190, 54), Screen);

        Assert.AreEqual(8, result.Left);
        Assert.AreEqual(1000, result.Top);
    }

    [TestMethod]
    public void Clamp_RaisesFlyoutThatWouldEscapeAboveTheScreen()
    {
        // 任务栏停靠在屏幕顶部时，浮窗会整个跑到屏幕上方。
        var result = FlyoutPlacement.Clamp(new Rect(900, -60, 190, 54), Screen);

        Assert.AreEqual(900, result.Left);
        Assert.AreEqual(8, result.Top);
    }

    [TestMethod]
    public void Clamp_WorksOnSecondaryMonitorWithNegativeOrigin()
    {
        // 副屏在主屏左侧：虚拟屏原点为负，夹取边界必须跟着平移。
        var secondary = new Rect(-1920, 0, 1920, 1080);
        var inside = FlyoutPlacement.Clamp(new Rect(-1910, 1000, 190, 54), secondary);
        Assert.AreEqual(-1910, inside.Left, "副屏内的浮窗不应被主屏原点夹取");
        Assert.AreEqual(1000, inside.Top);

        var overflowing = FlyoutPlacement.Clamp(new Rect(-1950, 1000, 190, 54), secondary);
        Assert.AreEqual(-1920 + 8, overflowing.Left);
        Assert.AreEqual(1000, overflowing.Top);
    }

    [TestMethod]
    public void Clamp_PassesThroughDegenerateBounds()
    {
        var proposed = new Rect(100, 100, 190, 54);
        Assert.AreEqual(proposed, FlyoutPlacement.Clamp(proposed, Rect.Empty));
        Assert.AreEqual(proposed, FlyoutPlacement.Clamp(proposed, new Rect(0, 0, 0, 0)));
    }

    [TestMethod]
    public void Clamp_WindowLargerThanBoundsKeepsTopLeftVisible()
    {
        // 极端情况：浮窗比边界还大（理论上不会发生）——优先保证左上角在边界内。
        var result = FlyoutPlacement.Clamp(new Rect(-500, -500, 4000, 3000), Screen);

        Assert.AreEqual(8, result.Left);
        Assert.AreEqual(8, result.Top);
    }
}
