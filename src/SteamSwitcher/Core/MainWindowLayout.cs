using System.Windows;

namespace SteamSwitcher.Core
{
    /// <summary>
    /// 主窗口布局的持久化几何运算。全部为纯函数，坐标一律用 DIP（WPF 的 Left/Top/Width/Height
    /// 单位），不碰任何 Win32，便于单元测试。
    ///
    /// 背景：程序此前不保存主窗口布局，每次启动都是 CenterScreen + 默认尺寸；同时"重置布局"
    /// 按钮在多显示器下会把窗口跳到主屏。这里提供两件事：
    /// 1. <see cref="Normalize"/>——校验上次保存的几何在当前显示器配置下是否仍然可信；
    ///    不可信（副屏被拔掉、分辨率改小、settings 被手改）时返回 null，让调用方回退默认布局。
    /// 2. <see cref="CenterIn"/>——在指定工作区内居中，供"重置布局"使用。
    /// </summary>
    internal static class MainWindowLayout
    {
        public readonly record struct Geometry(double Left, double Top, double Width, double Height)
        {
            public Rect ToRect() => new(Left, Top, Width, Height);
            public static Geometry FromRect(Rect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);
        }

        /// <summary>
        /// 校验保存的窗口几何在当前虚拟屏幕下是否可用，可用则原样返回（含贴边停靠等
        /// 部分探出屏幕的拖拽状态——那是用户的正常用法，恢复时不应篡改）。
        /// 返回 null 表示这份布局已不可信，调用方应回退默认布局。
        /// </summary>
        /// <param name="saved">上次保存的几何；为 null 即视为未保存过。</param>
        /// <param name="virtualScreen">当前虚拟屏幕范围（DIP）。</param>
        /// <param name="minSize">窗口最小尺寸（DIP）。</param>
        public static Geometry? Normalize(
            Geometry? saved,
            Rect virtualScreen,
            Size minSize)
        {
            if (saved is null) return null;

            var value = saved.Value;
            if (!IsFinite(value.Left) || !IsFinite(value.Top) ||
                !IsFinite(value.Width) || !IsFinite(value.Height))
                return null;

            // 尺寸必须落在 [最小尺寸, 虚拟屏幕] 内。超出说明保存时所依赖的显示器配置
            // 已经变了（分辨率改小、副屏被拔掉），或 settings 被手改，布局不可信。
            if (value.Width < minSize.Width || value.Height < minSize.Height ||
                value.Width > virtualScreen.Width || value.Height > virtualScreen.Height)
                return null;

            // 窗口中心必须仍落在虚拟屏幕内。中心在屏幕外意味着窗口整体已经跑飞
            // （典型场景：保存时窗口在副屏，之后副屏被拔掉）。中心在屏幕内同时
            // 保证了至少半个窗口可见，无需再对位置做任何夹取。
            var centerX = value.Left + value.Width / 2;
            var centerY = value.Top + value.Height / 2;
            if (centerX < virtualScreen.Left || centerX > virtualScreen.Right ||
                centerY < virtualScreen.Top || centerY > virtualScreen.Bottom)
                return null;

            return value;
        }

        /// <summary>
        /// 在给定工作区内居中放置指定尺寸的窗口；窗口比工作区大时贴左上角。
        /// </summary>
        public static Geometry CenterIn(Size windowSize, Rect workArea)
        {
            var width = Math.Min(windowSize.Width, workArea.Width);
            var height = Math.Min(windowSize.Height, workArea.Height);
            var left = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
            var top = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
            return new Geometry(left, top, width, height);
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
