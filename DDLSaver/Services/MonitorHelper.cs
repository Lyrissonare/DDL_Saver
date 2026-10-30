using System.Runtime.InteropServices;
using DDLSaver.Interop;

namespace DDLSaver.Services;

/// <summary>
/// 多显示器辅助（规格 5「多显示器：浮窗记住最后所在屏幕；该屏幕断开时回主屏」）。
/// 说明：WPF 窗口 Left/Top 使用系统 DPI 下的 DIP 坐标（虚拟屏幕原点 = 主屏左上角），
/// 因此所有设备像素坐标需按系统 DPI 换算后参与比较。
/// </summary>
internal static class MonitorHelper
{
    public sealed class MonitorData
    {
        public required string DeviceName { get; init; }
        public required NativeMethods.RECT RectPx { get; init; }
        public required NativeMethods.RECT WorkPx { get; init; }
        public required IntPtr Handle { get; init; }
    }

    private delegate bool EnumMonitorsProc(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorsProc lpfnEnum, IntPtr dwData);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const int MDT_EFFECTIVE_DPI = 0;

    private static readonly object _lock = new();
    private static List<MonitorData>? _cache;

    public static List<MonitorData> GetMonitors()
    {
        lock (_lock)
        {
            if (_cache is not null)
                return _cache;

            var list = new List<MonitorData>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref NativeMethods.RECT rc, IntPtr _) =>
            {
                var info = new NativeMethods.MONITORINFOEXW { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEXW>() };
                if (NativeMethods.GetMonitorInfo(hMon, ref info))
                {
                    list.Add(new MonitorData
                    {
                        DeviceName = info.szDevice,
                        RectPx = info.rcMonitor,
                        WorkPx = info.rcWork,
                        Handle = hMon,
                    });
                }
                return true;
            }, IntPtr.Zero);

            _cache = list.Count > 0 ? list : new List<MonitorData> { BuildFallback() };
            return _cache;
        }
    }

    /// <summary>
    /// 显示器拓扑 / DPI 变化后使缓存失效（SystemEvents.DisplaySettingsChanged 时调用）。
    /// 不失效的话，开机早期或拔插屏幕后拿到的旧布局会被永久使用，浮窗会定位到屏幕外。
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cache = null;
            _sysDpi = null;
        }
    }

    private static MonitorData BuildFallback()
    {
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        return new MonitorData
        {
            DeviceName = "Primary",
            RectPx = rect,
            WorkPx = rect,
            Handle = IntPtr.Zero,
        };
    }

    /// <summary>主显示器（左上角原点所在的那个；GetMonitors 保证至少返回一个）。</summary>
    public static MonitorData PrimaryMonitor(List<MonitorData> monitors)
        => monitors.FirstOrDefault(m => m.RectPx.Left == 0 && m.RectPx.Top == 0)
           ?? monitors.OrderBy(m => m.RectPx.Left).ThenBy(m => m.RectPx.Top).First();

    /// <summary>按设备名找（断开则返回 null → 上层回主屏）。</summary>
    public static MonitorData? BestMonitor(List<MonitorData> monitors, string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return null;
        return monitors.FirstOrDefault(m =>
            string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>窗口中心落在哪个显示器上。</summary>
    public static MonitorData? MonitorAtCenter(List<MonitorData> monitors,
        double left, double top, double width, double height)
    {
        var cx = left + width / 2;
        var cy = top + height / 2;
        foreach (var m in monitors)
        {
            var origin = MonitorOriginInDip(m, monitors);
            var size = MonitorSizeInDip(m);
            if (cx >= origin.Left && cx <= origin.Left + size.Width &&
                cy >= origin.Top && cy <= origin.Top + size.Height)
                return m;
        }
        return null;
    }

    /// <summary>显示器 DIP 尺寸（用其自身 DPI 换算，坐标空间仍为系统虚拟空间）。</summary>
    public static (double Width, double Height) MonitorSizeInDip(MonitorData m)
    {
        var dpi = DpiOf(m);
        return ((m.RectPx.Right - m.RectPx.Left) * 96.0 / dpi,
                (m.RectPx.Bottom - m.RectPx.Top) * 96.0 / dpi);
    }

    /// <summary>
    /// 显示器左上角在 WPF 虚拟坐标空间中的 DIP 位置。
    /// 注意：WPF 的坐标原点 = 主显示器左上角（次显示器可在负坐标区），
    /// 因此必须以主显示器为基准换算，而不是取所有显示器的最小值。
    /// </summary>
    public static (double Left, double Top) MonitorOriginInDip(MonitorData m, List<MonitorData>? all = null)
    {
        all ??= GetMonitors();
        var sysDpi = SystemDpi(all);
        var primary = PrimaryMonitor(all);
        return ((m.RectPx.Left - primary.RectPx.Left) * 96.0 / sysDpi,
                (m.RectPx.Top - primary.RectPx.Top) * 96.0 / sysDpi);
    }

    /// <summary>显示器工作区（DIP，相对于该显示器原点）。</summary>
    public static (double Left, double Top, double Width, double Height) WorkAreaInDip(MonitorData m)
    {
        var origin = MonitorOriginInDip(m);
        var dpi = DpiOf(m);
        return (
            (m.WorkPx.Left - m.RectPx.Left) * 96.0 / dpi,
            (m.WorkPx.Top - m.RectPx.Top) * 96.0 / dpi,
            (m.WorkPx.Right - m.WorkPx.Left) * 96.0 / dpi,
            (m.WorkPx.Bottom - m.WorkPx.Top) * 96.0 / dpi);
    }

    private static double DpiOf(MonitorData m)
    {
        try
        {
            if (m.Handle != IntPtr.Zero && GetDpiForMonitor(m.Handle, MDT_EFFECTIVE_DPI, out var x, out _) == 0 && x > 0)
                return x;
        }
        catch
        {
            // shcore 不可用时退回系统 DPI
        }
        return SystemDpi(GetMonitors());
    }

    private static double? _sysDpi;

    private static double SystemDpi(List<MonitorData> monitors)
    {
        if (_sysDpi is null)
        {
            var primary = PrimaryMonitor(monitors);
            _sysDpi = primary is null ? 96.0 : DpiOf(primary);
        }
        return _sysDpi.Value;
    }
}
