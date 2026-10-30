using CommunityToolkit.WinUI.Notifications;
using Windows.UI.Notifications;

namespace DDLSaver.Services;

/// <summary>
/// 系统 Toast 通知：
/// - 使用 CommunityToolkit.WinUI.Notifications（未打包应用无需证书）
/// - 每次发送前确保带 AUMID 的开始菜单快捷方式存在（否则系统静默丢弃）
/// - 点击 Toast 通过 OnActivated 回调携带 task:{guid} 参数定位任务
/// - 发送失败（或快捷方式不可用）返回 false，由上层降级为应用内自绘弹窗
/// </summary>
public class ToastService
{
    /// <summary>Toast 点击激活回调，参数为 Toast 上携带的 arguments 字符串。</summary>
    public event Action<string>? Activated;

    private static bool _activationHooked;

    static ToastService()
    {
        // 任何异常都不允许影响主流程；失败时上层走应用内弹窗兜底
    }

    /// <summary>获取兼容类使用的 AUMID（用于判断快捷方式是否就绪）。</summary>
    public static string Aumid => ShortcutService.ComputeAumid();

    /// <summary>确保 Toast 基础设施就绪（开始菜单快捷方式）。</summary>
    public void EnsureRegistered()
    {
        try
        {
            ShortcutService.Register();
        }
        catch
        {
            // 忽略；未注册时 ShowToast 返回 false 触发应用内兜底
        }
    }

    /// <summary>
    /// 订阅 Toast 点击事件（必须在启动早期订阅一次）。
    /// 未打包应用场景：进程运行时 COM 激活回调直达本进程；
    /// 进程未运行时由系统以 "exe -ToastActivated" 拉起，本事件同样触发。
    /// </summary>
    public void HookActivation()
    {
        if (_activationHooked)
            return;
        _activationHooked = true;

        ToastNotificationManagerCompat.OnActivated += e =>
        {
            try
            {
                Activated?.Invoke(e.Argument);
            }
            catch
            {
                // 回调内异常不得外泄
            }
        };
    }

    /// <summary>Toast 是否可用（快捷方式已注册且初始化无异常）。</summary>
    public static bool IsAvailable()
    {
        if (!ShortcutService.IsRegistered())
            return false;

        try
        {
            _ = ToastNotificationManagerCompat.History; // 触发静态初始化，失败会抛 InvalidOperationException
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 发送 Toast。返回是否成功；失败时由调用方降级到应用内弹窗。
    /// </summary>
    public bool TryShow(string title, string body, string arguments)
    {
        try
        {
            if (!ShortcutService.IsRegistered())
                return false;

            var content = new ToastContentBuilder()
                .AddText(title)
                .AddText(body)
                .AddArgument("action", arguments)
                .GetToastContent();

            var toast = new ToastNotification(content.GetXml())
            {
                // tag 最多 16 字符：同一任务的同类提醒自动替换旧 Toast，避免刷屏
                Tag = arguments.Length >= 16 ? arguments[..16] : arguments,
                Group = "DdlSaver",
            };

            ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
