using System.Runtime.InteropServices;

namespace DDLSaver.Interop;

/// <summary>
/// 本应用用到的全部 Win32 P/Invoke 声明，集中管理，便于审查。
/// 均为无副作用声明：不做任何联网、不做任何提权。
/// </summary>
internal static class NativeMethods
{
    // ---------- 窗口扩展样式 ----------

    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TOOLWINDOW = 0x00000080;      // 不进任务栏 / Alt+Tab
    public const int WS_EX_NOACTIVATE = 0x08000000;      // 点击不激活（浮窗常驻不抢焦点）
    public const int WS_EX_TRANSPARENT = 0x00000020;     // 鼠标穿透
    public const int WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ---------- 置顶维护（浮窗/圆点不被全屏应用压住） ----------

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    // ---------- 标题栏明暗（DWM immersive dark mode） ----------

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---------- 全局热键 ----------

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // ---------- 全屏检测 ----------

    public enum QueryUserNotificationState
    {
        QUNS_NOT_PRESENT = 1,             // 无法判断
        QUNS_BUSY = 2,                    // D3D 全屏 / 游戏等全屏占用
        QUNS_RUNNING_D3D_FULL_SCREEN = 3, // D3D 独占全屏
        QUNS_PRESENTATION_MODE = 4,       // 演示模式（PPT 等）
        QUNS_ACCEPTS_NOTIFICATIONS = 5,   // 正常桌面
        QUNS_QUIET_TIME = 6,              // 安静时间
        QUNS_APP = 7,                     // 前台应用自身的通知状态
    }

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out QueryUserNotificationState state);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    public const int GWL_STYLE = -16;
    public const int WS_CHILD = 0x40000000;
    public const int WS_CAPTION = 0x00C00000;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITOR_DEFAULTTONULL = 0;

    // ---------- 显示设备（多显示器记忆） ----------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
