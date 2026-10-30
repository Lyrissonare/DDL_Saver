using System.Windows;
using DDLSaver.Interop;

namespace DDLSaver.Services;

/// <summary>
/// 全屏检测（规格 3.5）：
/// - 主方案：shell32 SHQueryUserNotificationState —— D3D 独占全屏 / 演示模式；
/// - 兜底方案：SHQuery 返回"无法判断"时，用"前台窗口矩形 ≈ 屏幕矩形"判断；
/// - 轮询间隔 2 秒；本应用自身窗口在前台时一律视为非全屏。
/// </summary>
public class FullscreenDetector
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    /// <summary>全屏状态变化（进入 / 退出）。</summary>
    public event Action<bool>? Changed;

    public bool IsFullscreenNow { get; private set; }

    private readonly Func<IntPtr> _selfWindowHandle;

    public FullscreenDetector(Func<IntPtr> selfWindowHandle)
    {
        _selfWindowHandle = selfWindowHandle;
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        _timer.Start();
        Tick();
    }

    public void Stop() => _timer.Stop();

    private void Tick()
    {
        var fs = DetectFullscreen(_selfWindowHandle());
        if (fs != IsFullscreenNow)
        {
            IsFullscreenNow = fs;
            Changed?.Invoke(fs);
        }
    }

    public static bool DetectFullscreen(IntPtr selfHandle = default)
    {
        try
        {
            var state = NativeMethods.QueryUserNotificationState.QUNS_ACCEPTS_NOTIFICATIONS;
            NativeMethods.SHQueryUserNotificationState(out state);

            // 主方案：D3D 全屏 / 演示 / 忙
            if (state is NativeMethods.QueryUserNotificationState.QUNS_BUSY
                or NativeMethods.QueryUserNotificationState.QUNS_RUNNING_D3D_FULL_SCREEN
                or NativeMethods.QueryUserNotificationState.QUNS_PRESENTATION_MODE)
            {
                return true;
            }

            // 兜底方案：仅在 shell 无法判断时使用几何检测
            if (state == NativeMethods.QueryUserNotificationState.QUNS_NOT_PRESENT)
                return GeometryCheck(selfHandle);

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool GeometryCheck(IntPtr selfHandle)
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return false;

            // 本应用自己的窗口在前台时不算全屏
            if (hwnd == selfHandle)
                return false;

            // 桌面 / 任务栏窗口不参与判定
            var name = new System.Text.StringBuilder(64);
            NativeMethods.GetWindowText(hwnd, name, 64);
            var text = name.ToString();
            if (text is "Program Manager" or "Program Manager1" or "Windows 任务管理器")
                return false;

            var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            var style = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_STYLE);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                return false;
            if ((style & (NativeMethods.WS_CAPTION | NativeMethods.WS_CHILD)) == 0)
                return false; // 无边框窗口视为可疑，避免误判最大化浏览器

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
                return false;

            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref mi))
                return false;

            var w = mi.rcMonitor.Right - mi.rcMonitor.Left;
            var h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            if (w <= 0 || h <= 0)
                return false;

            var fw = rect.Right - rect.Left;
            var fh = rect.Bottom - rect.Top;
            // 前台窗口矩形 ≈ 屏幕矩形（≥99.5% 覆盖）
            return fw >= w * 0.995 && fh >= h * 0.995
                   && rect.Left <= mi.rcMonitor.Left + 2
                   && rect.Top <= mi.rcMonitor.Top + 2;
        }
        catch
        {
            return false;
        }
    }
}
