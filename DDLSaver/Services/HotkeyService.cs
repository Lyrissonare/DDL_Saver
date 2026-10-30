using System.Windows;
using System.Windows.Interop;
using DDLSaver.Interop;
using DDLSaver.Models;

namespace DDLSaver.Services;

/// <summary>
/// 全局热键（规格 3.2）：默认 Ctrl+Alt+D 呼出"快速添加"小窗。
/// 通过一个不可见辅助窗口承载 RegisterHotKey；冲突时返回 false 由上层提示允许改键。
/// </summary>
public class HotkeyService
{
    private const int HotkeyId = 0xD35; // 自定义 ID，仅用于识别

    private Window? _sink;
    private HwndSource? _source;
    private IntPtr _hwnd;

    /// <summary>热键按下（App 负责弹出快速添加窗口）。</summary>
    public event Action? Pressed;

    public HotkeyService()
    {
    }

    /// <summary>注册（返回是否成功；失败通常意味着热键冲突）。</summary>
    public bool Register(AppSettings settings)
    {
        Unregister();

        if (!settings.HotkeyEnabled)
            return true;

        try
        {
            if (_sink is null)
                _sink = CreateSinkWindow();

            var mods = settings.HotkeyModifiers | NativeMethods.MOD_NOREPEAT;
            return NativeMethods.RegisterHotKey(_hwnd, HotkeyId, mods, settings.HotkeyKey);
        }
        catch
        {
            return false;
        }
    }

    public void Unregister()
    {
        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            }
            catch
            {
                // 忽略
            }
        }
        _source?.RemoveHook(WndProc);
        _source = null;
        _hwnd = IntPtr.Zero;
        _sink?.Close();
        _sink = null;
    }

    private Window CreateSinkWindow()
    {
        var sink = new Window
        {
            Width = 0,
            Height = 0,
            Left = -2000,
            Top = -2000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            AllowsTransparency = false,
        };
        sink.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(sink).Handle;
            var ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
        };
        sink.Show();
        return sink;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }
}
