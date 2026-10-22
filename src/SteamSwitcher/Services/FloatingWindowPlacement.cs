using System.Runtime.InteropServices;
using System.Windows;

namespace SteamSwitcher.Services;

internal sealed record FloatingMonitor(string Id, Rect WorkArea, double ScaleX, double ScaleY, bool Primary = false);
internal sealed record FloatingMonitorPosition(string MonitorId, double RelativeLeft, double RelativeTop);

/// <summary>All geometry here is in physical pixels; stored positions are monitor-relative ratios.</summary>
internal static class FloatingPlacementGeometry
{
    internal static Rect Clamp(Rect window, Rect work)
    {
        var width = Math.Min(window.Width, work.Width);
        var height = Math.Min(window.Height, work.Height);
        // Clamp the size too, not just the position, so a window larger than the work
        // area does not spill outside it (the original returned the oversized size).
        return new Rect(Math.Clamp(window.Left, work.Left, work.Right - width),
            Math.Clamp(window.Top, work.Top, work.Bottom - height), width, height);
    }

    internal static FloatingMonitor Nearest(IReadOnlyList<FloatingMonitor> monitors, Rect window)
    {
        // Headless/early-startup fallback: with no monitors, treat the window rect
        // itself as the work area so callers never dereference an empty list.
        if (monitors.Count == 0)
            return new FloatingMonitor("primary", window, 1, 1, true);
        return monitors
            .OrderBy(m => DistanceSquared(m.WorkArea, window)).First();
    }

    private static double DistanceSquared(Rect work, Rect window)
    {
        var x = window.Left + window.Width / 2;
        var y = window.Top + window.Height / 2;
        var dx = x - Math.Clamp(x, work.Left, work.Right);
        var dy = y - Math.Clamp(y, work.Top, work.Bottom);
        return dx * dx + dy * dy;
    }

    internal static FloatingMonitorPosition Capture(Rect window, FloatingMonitor monitor) => new(monitor.Id,
        Fraction(window.Left - monitor.WorkArea.Left, monitor.WorkArea.Width - window.Width),
        Fraction(window.Top - monitor.WorkArea.Top, monitor.WorkArea.Height - window.Height));

    private static double Fraction(double value, double range) => range <= 0 ? 0 : Math.Clamp(value / range, 0, 1);
    private static double ValidRatio(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    internal static Rect Restore(FloatingMonitorPosition saved, IReadOnlyList<FloatingMonitor> monitors,
        double widthDips, double heightDips)
    {
        // Guard against an empty monitor list (e.g. a headless/early-startup state) so
        // we never throw IndexOutOfRangeException while restoring the window position.
        if (monitors.Count == 0)
            return new Rect(0, 0, Math.Max(1, widthDips), Math.Max(1, heightDips));

        var monitor = monitors.FirstOrDefault(m => string.Equals(m.Id, saved.MonitorId, StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
        var width = Math.Max(1, widthDips * monitor.ScaleX);
        var height = Math.Max(1, heightDips * monitor.ScaleY);
        return new Rect(monitor.WorkArea.Left + Math.Max(0, monitor.WorkArea.Width - width) * ValidRatio(saved.RelativeLeft),
            monitor.WorkArea.Top + Math.Max(0, monitor.WorkArea.Height - height) * ValidRatio(saved.RelativeTop), width, height);
    }
}

internal static class FloatingWindowMonitors
{
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    internal static IReadOnlyList<FloatingMonitor> Enumerate()
    {
        var monitors = new List<FloatingMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref NativeRect bounds, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            uint dpiX = 96, dpiY = 96;
            if (GetDpiForMonitor(monitor, 0, out var x, out var y) == 0) { dpiX = x; dpiY = y; }
            var work = info.Work;
            if (work.Right > work.Left && work.Bottom > work.Top)
                monitors.Add(new(info.Device, new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top),
                    Math.Max(1, dpiX) / 96d, Math.Max(1, dpiY) / 96d, (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    internal static Rect? GetBounds(IntPtr handle) => GetWindowRect(handle, out var rect)
        ? new Rect(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top)) : null;

    internal static bool Move(IntPtr handle, Rect rect) => SetWindowPos(handle, IntPtr.Zero,
        (int)Math.Round(rect.Left), (int)Math.Round(rect.Top), 0, 0, 0x0001 | 0x0004 | 0x0010);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
