using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DDLSaver.Interop;
using DDLSaver.Services;

namespace DDLSaver.Windows;

/// <summary>
/// 应用内自绘通知弹窗（规格 3.3 兜底）：
/// 系统 Toast 发送失败（未打包应用 AUMID 不可用等）时，提醒绝不静默丢失，
/// 一律改在屏幕右下角弹出本窗口；点击定位任务，悬停暂停倒计时。
/// </summary>
public class InAppToastWindow : Window
{
    private static readonly List<InAppToastWindow> OpenWindows = new();

    private readonly DispatcherTimer _closeTimer;
    private readonly string _arguments;

    private InAppToastWindow(string title, string body, string arguments)
    {
        _arguments = arguments;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Style = null; // 透明分层窗口使用原生模板，避免 WPF-UI 窗口样式关闭透明支持
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 380;

        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = ThemeService.Get("TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var bodyText = new TextBlock
        {
            Text = body,
            FontSize = 12.5,
            Foreground = ThemeService.Get("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var clickHint = new TextBlock
        {
            Text = "点击查看任务详情",
            FontSize = 11,
            Foreground = ThemeService.Get("TextMutedBrush"),
            Margin = new Thickness(0, 6, 0, 0),
        };

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(titleText);
        panel.Children.Add(bodyText);
        panel.Children.Add(clickHint);

        var border = new Border
        {
            Background = ThemeService.Get("CardBgBrush"),
            BorderBrush = ThemeService.Get("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = panel,
        };

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(border);
        Content = root;

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _closeTimer.Tick += (_, _) => Close();

        // 点击 → 打开主窗口定位
        root.MouseLeftButtonUp += (_, _) =>
        {
            Close();
            App.Instance.HandleActivation(_arguments);
        };

        // 悬停暂停关闭
        MouseEnter += (_, _) => _closeTimer.Stop();
        MouseLeave += (_, _) => _closeTimer.Start();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
        };
        SourceInitialized += PositionAndPlay;
    }

    public static void Show(string title, string body, string arguments)
    {
        // 清理已关闭实例
        OpenWindows.RemoveAll(w => !w.IsVisible);

        var win = new InAppToastWindow(title, body, arguments);
        OpenWindows.Add(win);
        win.Show();
    }

    private void PositionAndPlay(object? sender, EventArgs e)
    {
        // 从下往上叠放：最底部靠任务栏
        var work = SystemParameters.WorkArea;
        var index = OpenWindows.IndexOf(this);
        var offset = index * (ActualHeight + 10);

        Left = work.Right - ActualWidth - 16;
        Top = work.Bottom - ActualHeight - 16 - offset;
        if (Top < work.Top)
            Top = work.Top;

        // 轻微滑入动画（透明度），不打扰操作
        Opacity = 0;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
        BeginAnimation(OpacityProperty, anim);

        _closeTimer.Start();
    }
}
