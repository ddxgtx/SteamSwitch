using System.Windows;
using SteamSwitcher.Core;

namespace SteamSwitcher.Tests;

/// <summary>
/// 主窗口布局持久化的几何回归。重点覆盖多显示器与 DPI 变化后的越界场景——
/// 这类问题只在用户第二次启动时才暴露，且表现是"窗口不见了"，极难现场复现。
/// </summary>
[TestClass]
public sealed class MainWindowLayoutTests
{
    // 单屏 1920x1080，原点在 (0,0)。
    private static readonly Rect SingleScreen = new(0, 0, 1920, 1080);
    private static readonly Size MinSize = new(1120, 760);

    private static MainWindowLayout.Geometry? Geo(double left, double top, double width, double height) =>
        new(left, top, width, height);

    [TestMethod]
    public void Normalize_KeepsValidSavedGeometry()
    {
        var result = MainWindowLayout.Normalize(Geo(120, 80, 1360, 900), SingleScreen, MinSize);

        Assert.IsNotNull(result);
        Assert.AreEqual(120, result!.Value.Left);
        Assert.AreEqual(80, result.Value.Top);
        Assert.AreEqual(1360, result.Value.Width);
        Assert.AreEqual(900, result.Value.Height);
    }

    [TestMethod]
    public void Normalize_ReturnsNullWhenNothingWasSaved()
    {
        Assert.IsNull(MainWindowLayout.Normalize(null, SingleScreen, MinSize), "首次运行没有保存值，必须回退默认布局");
    }

    [TestMethod]
    public void Normalize_RejectsNonFiniteValues()
    {
        Assert.IsNull(MainWindowLayout.Normalize(Geo(double.NaN, 0, 1360, 900), SingleScreen, MinSize));
        Assert.IsNull(MainWindowLayout.Normalize(Geo(0, 0, double.PositiveInfinity, 900), SingleScreen, MinSize));
    }

    [TestMethod]
    public void Normalize_ReturnsNullWhenSavedMonitorIsGone()
    {
        // 副屏被拔掉：窗口中心 (3400, 400) 已不在当前虚拟屏幕内，布局失效。
        var result = MainWindowLayout.Normalize(Geo(2800, 100, 1200, 800), SingleScreen, MinSize);

        Assert.IsNull(result, "保存布局依赖的显示器已不存在，不能把窗口放到屏幕外");
    }

    [TestMethod]
    public void Normalize_PreservesPartiallyOffscreenDrag()
    {
        // 用户把窗口向左拖到探出 100px：中心 (580, 650) 仍在屏幕内，属于正常拖拽状态，
        // 恢复时必须原样保留——夹取位置会把用户摆好的布局改掉。
        var result = MainWindowLayout.Normalize(Geo(-100, 200, 1360, 900), SingleScreen, MinSize);

        Assert.IsNotNull(result);
        Assert.AreEqual(-100, result!.Value.Left, "部分探出屏幕的拖拽状态必须原样恢复");
        Assert.AreEqual(200, result.Value.Top);
    }

    [TestMethod]
    public void Normalize_RejectsMostlyOffscreenDrag()
    {
        // 窗口整体跑到屏幕右侧之外（中心 (2480, 1450) 不在屏幕内）：这种状态恢复出来
        // 用户根本找不到窗口，必须回退默认布局。
        var result = MainWindowLayout.Normalize(Geo(1800, 1000, 1360, 900), SingleScreen, MinSize);

        Assert.IsNull(result, "中心落在屏幕外的布局会在恢复后变成'窗口不见了'");
    }

    [TestMethod]
    public void Normalize_KeepsWindowFlushAgainstTheRightAndBottomEdge()
    {
        var result = MainWindowLayout.Normalize(Geo(1920 - 1360, 1080 - 900, 1360, 900), SingleScreen, MinSize);

        Assert.IsNotNull(result);
        Assert.AreEqual(1920 - 1360, result!.Value.Left);
        Assert.AreEqual(1080 - 900, result.Value.Top);
    }

    [TestMethod]
    public void Normalize_RejectsWindowLargerThanVirtualScreen()
    {
        // settings 被手改或保存时用了另一套显示器配置：尺寸超出当前虚拟屏幕即判失效，
        // 回退默认布局比恢复一个巨型窗口更可预期。
        Assert.IsNull(MainWindowLayout.Normalize(Geo(0, 0, 5000, 3000), SingleScreen, MinSize));
        Assert.IsNull(MainWindowLayout.Normalize(Geo(0, 0, 1360, 5000), SingleScreen, MinSize));
    }

    [TestMethod]
    public void Normalize_RejectsWindowSmallerThanMinSize()
    {
        Assert.IsNull(MainWindowLayout.Normalize(Geo(100, 100, 800, 600), SingleScreen, MinSize),
            "小于 MinWidth/MinHeight 的保存值不可能来自真实窗口布局");
    }

    [TestMethod]
    public void Normalize_HandlesSecondaryMonitorCoordinateSpace()
    {
        // 副屏在右侧：虚拟屏幕横跨两块屏，原点仍在 (0,0)。
        var dualScreen = new Rect(0, 0, 3840, 1080);
        var result = MainWindowLayout.Normalize(Geo(2600, 200, 1200, 800), dualScreen, MinSize);

        Assert.IsNotNull(result, "副屏上的合法布局必须原样保留，不能被错误判失效");
        Assert.AreEqual(2600, result!.Value.Left);
        Assert.AreEqual(200, result.Value.Top);
    }

    [TestMethod]
    public void CenterIn_CentersWithinWorkArea()
    {
        var result = MainWindowLayout.CenterIn(new Size(1360, 900), SingleScreen);

        Assert.AreEqual((1920 - 1360) / 2d, result.Left);
        Assert.AreEqual((1080 - 900) / 2d, result.Top);
        Assert.AreEqual(1360, result.Width);
        Assert.AreEqual(900, result.Height);
    }

    [TestMethod]
    public void CenterIn_WorksOnASecondaryMonitorWorkArea()
    {
        // 副屏工作区（任务栏占掉底部 40px）：居中必须相对副屏原点计算，
        // 而不是相对 (0,0)——否则窗口会被放到副屏左侧之外。
        var secondary = new Rect(1920, 0, 1920, 1040);
        var result = MainWindowLayout.CenterIn(new Size(1360, 900), secondary);

        Assert.AreEqual(1920 + (1920 - 1360) / 2d, result.Left);
        Assert.AreEqual((1040 - 900) / 2d, result.Top);
    }

    [TestMethod]
    public void CenterIn_WindowLargerThanWorkAreaSnapsToTopLeft()
    {
        var result = MainWindowLayout.CenterIn(new Size(2000, 1200), SingleScreen);

        Assert.AreEqual(0, result.Left);
        Assert.AreEqual(0, result.Top);
    }

    [TestMethod]
    public void Geometry_RoundTripsThroughRect()
    {
        var rect = new Rect(12.5, 34.5, 1360, 900);
        var geometry = MainWindowLayout.Geometry.FromRect(rect);

        Assert.AreEqual(rect, geometry.ToRect());
    }
}
