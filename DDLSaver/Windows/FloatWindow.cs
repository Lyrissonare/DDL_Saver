using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DDLSaver.Core;
using DDLSaver.Interop;
using DDLSaver.Models;
using DDLSaver.Services;
using Microsoft.Win32;

namespace DDLSaver.Windows;

/// <summary>
/// 常驻屏幕顶层浮窗（规格 3.1 / 3.5）：
/// - 无边框、置顶、不进任务栏 / Alt+Tab（WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE）
/// - 完整 / 极简 / 隐藏三种模式；极简模式 = 小圆点 + ≤24h 到期数量
/// - 鼠标穿透（WS_EX_TRANSPARENT）；可交互时双击打开主窗口、右键菜单、可拖拽
/// - 颜色分级 + ≤1 天呼吸闪烁（仅透明度 0.6↔1，禁止位移）
/// - 全屏自动隐藏：始终降级为极简圆点提示（不消失），每次刷新重申 TOPMOST 防被全屏窗口压住
/// </summary>
public class FloatWindow : Window
{
    private const double MaxWidthDip = 460;

    private readonly App _app;

    // 视觉元素（代码构建，避免模板绑定带来的复杂度）
    private readonly Grid _root;         // 根容器（重推位图用）
    private readonly Border _outer;      // 完整浮窗卡片
    private readonly StackPanel _lines;  // 任务行
    private readonly Border _mini;       // 极简模式圆点
    private readonly TextBlock _miniText;

    private readonly DispatcherTimer _refreshTimer;
    private readonly DoubleAnimation _breath;

    private bool _initialized;           // 首次显示完成
    private bool _fullscreenHidden;      // 全屏自动隐藏生效中
    private bool _breathing;             // 呼吸动画进行中
    private VisibilityMode _mode = VisibilityMode.Full;

    public FloatWindow(App app)
    {
        _app = app;

        // ---- 窗口基础属性 ----
        // 分层透明窗口必须使用原生 Window 模板：显式置空样式，避免 WPF-UI 的
        // 窗口样式把 AllowsTransparency 覆盖为 False；像素对齐仍手动开启。
        Style = null;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Focusable = false;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        // ---- 完整浮窗 ----
        _lines = new StackPanel { Orientation = Orientation.Vertical };
        _outer = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x30, 0x30, 0x30)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            MaxWidth = MaxWidthDip,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            Child = _lines,
        };
        // ---- 极简模式圆点 ----
        _miniText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _mini = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xD3, 0x2F, 0x2F)),
            Child = _miniText,
            Visibility = Visibility.Collapsed,
        };

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(_outer);
        root.Children.Add(_mini);
        Content = root;
        _root = root;
        IsHitTestVisible = true;

        // ---- 呼吸动画：仅透明度 0.6↔1，禁止位移 ----
        _breath = new DoubleAnimation(1.0, 0.6, TimeSpan.FromMilliseconds(1400))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        // ---- 交互 ----
        MouseLeftButtonDown += OnMouseLeftDown;
        MouseRightButtonUp += (_, _) => ShowContextMenu();

        // ---- 定时刷新：15 秒（防止跨天/跨小时文案过期） ----
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _refreshTimer.Tick += (_, _) => UpdateContent();

        SourceInitialized += (_, _) => ApplyWindowStyles();
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        };

        // 显示器拓扑 / DPI 变化（拔插屏幕、改缩放、开机初始化）后重新枚举：
        // MonitorHelper 内部缓存坐标，不失效的话浮窗会按旧布局定位到屏幕外
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        MonitorHelper.InvalidateCache();
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                UpdateContent();
                KickRender();
            }
            catch
            {
                // 窗口可能正在关闭
            }
        });
    }

    #region 对外接口

    /// <summary>当前生效的显示模式。</summary>
    public VisibilityMode EffectiveMode => _mode;

    /// <summary>全屏自动隐藏状态变化（App 的 FullscreenDetector 回调）。</summary>
    public void SetFullscreenHidden(bool hidden)
    {
        if (_fullscreenHidden == hidden)
            return;
        _fullscreenHidden = hidden;

        if (!hidden)
        {
            // 退出全屏：恢复设置中的可见模式
            ApplyMode(_app.SettingsService.Settings.VisibilityMode, isFullscreenRecovery: true);
        }
        else
        {
            // 进入全屏：始终降级为极简圆点（圆点即"仍在运行"的提示，绝不完全消失）
            ApplyMode(VisibilityMode.Mini, isFullscreenRecovery: false);
        }
        UpdateContent();
    }

    /// <summary>应用设置变更（模式 / 穿透 / 透明度 / 尺寸 / 主题等）。</summary>
    public void ApplySettings()
    {
        Opacity = _app.SettingsService.Settings.FloatOpacity;
        ApplyCardStyle();
        ApplyWindowStyles();
        ApplyMode(_app.SettingsService.Settings.VisibilityMode, isFullscreenRecovery: false);
        UpdateContent();
    }

    /// <summary>浮窗卡片：底色（主题色 × 独立底色透明度）与描边。</summary>
    private void ApplyCardStyle()
    {
        var s = _app.SettingsService.Settings;
        var baseColor = ThemeService.FloatBaseColor();
        var a = (byte)Math.Round(Math.Clamp(s.FloatBackgroundOpacity, 0.0, 1.0) * 255);
        _outer.Background = new SolidColorBrush(Color.FromArgb(a, baseColor.R, baseColor.G, baseColor.B));
        _outer.BorderBrush = ThemeService.Get("FloatCardBorderBrush");
    }

    /// <summary>供主窗口设置页调用：穿透样式即时生效。</summary>
    public void ApplyWindowStylesPublic() => ApplyWindowStyles();

    /// <summary>设置模式（含硬规则校验；返回是否成功）。</summary>
    public bool TrySetMode(VisibilityMode mode, out string? rejectReason)    {
        rejectReason = null;
        var settings = _app.SettingsService.Settings;

        // 硬规则：存在 ≤24h 内到期（含逾期）的未完成任务时，禁止完全隐藏
        if (mode == VisibilityMode.Hidden && UrgencyHelper.HasUrgentPending(_app.Store.Tasks))
        {
            rejectReason = "存在 24 小时内到期（或已逾期）的任务，不允许完全隐藏，只能降级为极简模式。";
            return false;
        }

        settings.VisibilityMode = mode;
        _app.SettingsService.Save();
        ApplyMode(mode, isFullscreenRecovery: false);
        UpdateContent();
        return true;
    }

    /// <summary>重新计算内容（模式 / 文案 / 颜色 / 位置）。</summary>
    public void UpdateContent()
    {
        if (!_initialized)
            return;

        var settings = _app.SettingsService.Settings;
        var tasks = _app.Store.Tasks;

        // ---- 生效模式：全屏降级 > 设置模式 ----
        VisibilityMode eff;
        if (_fullscreenHidden && settings.FullscreenAutoHide)
        {
            // 全屏时始终保留极简圆点提示（≤24h 数量），绝不完全消失；
            // 圆点窗口必须压在全屏应用之上，每次刷新都重申置顶（全屏窗口常会反超 Topmost）
            eff = VisibilityMode.Mini;
        }
        else
        {
            eff = settings.VisibilityMode;
            // 安全网：即使设置非法（例如进入隐藏后任务突然变紧急）也强制降级
            if (eff == VisibilityMode.Hidden && UrgencyHelper.HasUrgentPending(tasks))
                eff = VisibilityMode.Mini;
        }

        if (eff != _mode)
            ApplyMode(eff, isFullscreenRecovery: false);
        _mode = eff;

        if (eff == VisibilityMode.Hidden)
        {
            Hide();
            return;
        }

        if (!IsVisible)
            Show();

        // 分层窗口自愈：见 KickRender 注释
        KickRender();

        if (eff == VisibilityMode.Mini)
        {
            // 极简模式：小圆点 + ≤24h 到期数量
            var count = UrgencyHelper.UrgentPendingCount(tasks);
            _miniText.Text = count.ToString();
            _outer.Visibility = Visibility.Collapsed;
            _mini.Visibility = Visibility.Visible;
            StopBreath();
            PositionMini();
            return;
        }

        // ---- 完整浮窗：最紧急的 N 条未完成任务 ----
        _outer.Visibility = Visibility.Visible;
        _mini.Visibility = Visibility.Collapsed;

        var ordered = tasks
            .Where(t => !t.IsCompleted)
            .OrderBy(t => t, new UrgencyComparer())
            .Take(Math.Clamp(settings.FloatCount, 1, 5))
            .ToList();

        _lines.Children.Clear();
        var needBreath = false;
        var textOpacity = Math.Clamp(settings.FloatTextOpacity, 0.3, 1.0);

        foreach (var t in ordered)
        {
            var level = UrgencyHelper.Level(t);
            var line = new TextBlock
            {
                Text = UrgencyHelper.FloatLine(t) + UrgencyHelper.ProgressSuffix(t),
                Foreground = UrgencyHelper.LevelBrush(level),
                FontSize = settings.FloatFontSize,
                FontWeight = level == UrgencyLevel.Overdue ? FontWeights.Bold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 1),
                Opacity = textOpacity, // 独立文字透明度
            };
            _lines.Children.Add(line);

            if (level is UrgencyLevel.Critical or UrgencyLevel.Overdue)
                needBreath = true;
        }

        if (_lines.Children.Count == 0)
        {
            _lines.Children.Add(new TextBlock
            {
                Text = "没有未完成的任务，去休息吧 ☕",
                Foreground = UrgencyHelper.LevelBrush(UrgencyLevel.Done),
                FontSize = settings.FloatFontSize,
                Opacity = textOpacity,
            });
        }

        if (needBreath)
            StartBreath();
        else
            StopBreath();

        PositionFull();
    }

    #endregion

    #region 模式与样式

    private void ApplyMode(VisibilityMode mode, bool isFullscreenRecovery)
    {
        _mode = mode;
        var settings = _app.SettingsService.Settings;

        if (mode == VisibilityMode.Hidden)
        {
            StopBreath();
            Hide();
            return;
        }

        if (!IsVisible)
            Show();
        EnsureTopmost();

        Opacity = settings.FloatOpacity;
        Width = double.NaN;
        Height = double.NaN;
        SizeToContent = SizeToContent.WidthAndHeight;

        if (mode == VisibilityMode.Mini)
        {
            _outer.Visibility = Visibility.Collapsed;
            _mini.Visibility = Visibility.Visible;
            PositionMini();
        }
        else
        {
            _outer.Visibility = Visibility.Visible;
            _mini.Visibility = Visibility.Collapsed;
            // 字号/条数变化后重新测量
            Dispatcher.BeginInvoke(PositionFull, DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 重申 TOPMOST：全屏应用进入全屏时常会后来居上压过先前的 Topmost 窗口，
    /// 每次显示/模式切换/定时刷新都重申一次，保证小圆点始终可见。
    /// </summary>
    private void EnsureTopmost()
    {
        Topmost = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        try
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
        catch
        {
            // 置顶失败不影响功能
        }
    }

    /// <summary>应用窗口扩展样式（ToolWindow / NoActivate / 穿透）。</summary>
    private void ApplyWindowStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return; // SourceInitialized 后会再次调用

        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;

        if (_app.SettingsService.Settings.MouseThrough)
            ex |= NativeMethods.WS_EX_TRANSPARENT;
        else
            ex &= ~NativeMethods.WS_EX_TRANSPARENT;

        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    #endregion

    #region 鼠标交互

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_app.SettingsService.Settings.MouseThrough)
            return; // 穿透模式下一切操作通过托盘

        if (e.ClickCount >= 2)
        {
            _app.ShowMainWindow();
            return;
        }

        // 仅完整浮窗（非极简）且自由模式允许拖拽定位
        var settings = _app.SettingsService.Settings;
        var draggable = _mode == VisibilityMode.Full
            ? settings.FloatDock == FloatDock.Free
            : _mode == VisibilityMode.Mini;

        if (!draggable)
            return;

        try
        {
            DragMove();

            var settings2 = _app.SettingsService.Settings;
            if (_mode == VisibilityMode.Mini)
            {
                settings2.MiniX = Left;
                settings2.MiniY = Top;
            }
            else
            {
                settings2.FloatDock = FloatDock.Free;
                settings2.FloatX = Left;
                settings2.FloatY = Top;
            }
            // 拖拽导致跨屏时刷新"记住的屏幕"
            var monitors = MonitorHelper.GetMonitors();
            var m = MonitorHelper.MonitorAtCenter(monitors, Left, Top, ActualWidth, ActualHeight);
            settings2.FloatScreenDeviceName = m?.DeviceName;
            _app.SettingsService.Save();
        }
        catch
        {
            // DragMove 期间丢失左键会抛异常，忽略即可
        }
    }

    private void ShowContextMenu()
    {
        if (_app.SettingsService.Settings.MouseThrough)
            return;

        var menu = new ContextMenu();
        var settings = _app.SettingsService.Settings;

        var itemMain = new MenuItem { Header = "打开主窗口" };
        itemMain.Click += (_, _) => _app.ShowMainWindow();
        menu.Items.Add(itemMain);

        var itemQuick = new MenuItem { Header = "快速添加（Ctrl+Alt+D）" };
        itemQuick.Click += (_, _) => _app.ShowQuickAdd();
        menu.Items.Add(itemQuick);

        menu.Items.Add(new Separator());

        var itemFull = new MenuItem { Header = "完整浮窗", IsCheckable = true, IsChecked = _mode == VisibilityMode.Full };
        itemFull.Click += (_, _) => RejectableTryMode(VisibilityMode.Full);
        menu.Items.Add(itemFull);

        var itemMini = new MenuItem { Header = "极简模式（圆点）", IsCheckable = true, IsChecked = _mode == VisibilityMode.Mini };
        itemMini.Click += (_, _) => RejectableTryMode(VisibilityMode.Mini);
        menu.Items.Add(itemMini);

        var itemHidden = new MenuItem { Header = "完全隐藏", IsCheckable = true, IsChecked = _mode == VisibilityMode.Hidden };
        itemHidden.Click += (_, _) => RejectableTryMode(VisibilityMode.Hidden);
        menu.Items.Add(itemHidden);

        menu.Items.Add(new Separator());

        var itemThrough = new MenuItem
        {
            Header = "鼠标穿透（纯展示）",
            IsCheckable = true,
            IsChecked = settings.MouseThrough,
        };
        itemThrough.Click += (_, _) => ToggleMouseThrough();
        menu.Items.Add(itemThrough);

        menu.Items.Add(new Separator());
        var itemExit = new MenuItem { Header = "退出" };
        itemExit.Click += (_, _) => _app.ExitApplication();
        menu.Items.Add(itemExit);

        menu.PlacementTarget = _outer;
        menu.IsOpen = true;
    }

    private void RejectableTryMode(VisibilityMode mode)
    {
        if (!TrySetMode(mode, out var reason))
        {
            MessageBox.Show(reason!, "无法完全隐藏",
                MessageBoxButton.OK, MessageBoxImage.Information);
            // 硬规则允许：自动降级为极简模式
            TrySetMode(VisibilityMode.Mini, out _);
        }
    }

    private void ToggleMouseThrough()
    {
        var settings = _app.SettingsService.Settings;
        settings.MouseThrough = !settings.MouseThrough;
        _app.SettingsService.Save();
        ApplyWindowStyles();
    }

    #endregion

    #region 位置

    /// <summary>把窗口（完整模式）摆到目标屏幕的顶部/底部居中或自由位置。</summary>
    private void PositionFull()
    {
        var settings = _app.SettingsService.Settings;
        var monitors = MonitorHelper.GetMonitors();
        var mon = MonitorHelper.BestMonitor(monitors, settings.FloatScreenDeviceName)
                  ?? MonitorHelper.PrimaryMonitor(monitors);

        // 记录所在屏幕（多显示器记忆）
        var current = MonitorHelper.MonitorAtCenter(monitors, Left, Top, ActualWidth, ActualHeight);
        settings.FloatScreenDeviceName = current?.DeviceName ?? mon.DeviceName;

        var origin = MonitorHelper.MonitorOriginInDip(mon, monitors);
        var work = MonitorHelper.WorkAreaInDip(mon);
        var w = ActualWidth > 0 ? ActualWidth : 200;
        var h = ActualHeight > 0 ? ActualHeight : 60;

        switch (settings.FloatDock)
        {
            case FloatDock.Top:
                Left = origin.Left + (work.Width - w) / 2;
                Top = origin.Top + work.Top + 8;
                break;

            case FloatDock.Bottom:
                Left = origin.Left + (work.Width - w) / 2;
                Top = origin.Top + work.Top + work.Height - h - 8;
                break;

            default: // Free
                var hasSaved = settings.FloatX != 0 || settings.FloatY != 0;
                var x = hasSaved ? settings.FloatX : origin.Left + (work.Width - w) / 2;
                var y = hasSaved ? settings.FloatY : origin.Top + work.Top + 8;
                (x, y) = ClampToWorkArea(x, y, w, h);
                Left = x;
                Top = y;
                break;
        }
    }

    private void PositionMini()
    {
        var settings = _app.SettingsService.Settings;
        var monitors = MonitorHelper.GetMonitors();
        var mon = MonitorHelper.BestMonitor(monitors, settings.FloatScreenDeviceName)
                  ?? MonitorHelper.PrimaryMonitor(monitors);
        var work = MonitorHelper.WorkAreaInDip(mon);

        double x, y;
        if (settings.MiniX >= 0 && settings.MiniY >= 0)
        {
            (x, y) = ClampToWorkArea(settings.MiniX, settings.MiniY, 34, 34);
        }
        else
        {
            // 默认：所在屏幕工作区右上角
            x = work.Left + work.Width - 34 - 10;
            y = work.Top + work.Height - 34 - 10;
        }
        Left = x;
        Top = y;
    }

    /// <summary>拖拽后保存位置（MouseUp 在 DragMove 返回后由调用处处理）。</summary>
    private (double x, double y) ClampToWorkArea(double x, double y, double w, double h)
    {
        var monitors = MonitorHelper.GetMonitors();
        // 就近吸附到某个显示器的工作区
        foreach (var m in monitors)
        {
            var work = MonitorHelper.WorkAreaInDip(m);
            if (x + w / 2 >= work.Left && x + w / 2 <= work.Left + work.Width &&
                y + h / 2 >= work.Top && y + h / 2 <= work.Top + work.Height)
            {
                var nx = Math.Clamp(x, work.Left, work.Left + work.Width - w);
                var ny = Math.Clamp(y, work.Top, work.Top + work.Height - h);
                return (nx, ny);
            }
        }

        // 位置不属于任何现有显示器（如保存时连接的外接屏已断开）：
        // 回落到记忆屏幕或主屏的工作区，绝不允许浮窗落在屏幕外不可见
        var fallback = MonitorHelper.BestMonitor(monitors, _app.SettingsService.Settings.FloatScreenDeviceName)
                       ?? MonitorHelper.PrimaryMonitor(monitors);
        var fbWork = MonitorHelper.WorkAreaInDip(fallback);
        var fx = Math.Clamp(x, fbWork.Left, Math.Max(fbWork.Left, fbWork.Left + fbWork.Width - w));
        var fy = Math.Clamp(y, fbWork.Top, Math.Max(fbWork.Top, fbWork.Top + fbWork.Height - h));
        return (fx, fy);
    }

    #endregion

    #region 动画

    private void StartBreath()
    {
        if (_breathing || _outer.Visibility != Visibility.Visible)
            return;
        _breathing = true;
        _outer.BeginAnimation(OpacityProperty, _breath, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopBreath()
    {
        if (!_breathing)
            return;
        _breathing = false;
        _outer.BeginAnimation(OpacityProperty, null);
        _outer.Opacity = 1.0;
    }

    #endregion

    /// <summary>显示并进入驻留状态。</summary>
    public void Launch()
    {
        _initialized = true;
        _refreshTimer.Start();
        ApplySettings();
        ScheduleStartupKicks();
    }

    /// <summary>
    /// 强制重推一次分层窗口位图。
    /// 开机自启时首个 ULW 推送可能发生在 DWM 就绪之前而丢失，窗口从此"存在但全透明"；
    /// 且稳态下 UpdateContent 的所有赋值都与现值相同，WPF 不会因此重绘，空白将永远无法恢复。
    /// 这里用 0.999↔1.0 的不透明度微调制造一次真实的视觉失效（肉眼不可见），让渲染线程重新推送。
    /// </summary>
    private void KickRender()
    {
        try
        {
            _root.Opacity = 0.999;
            Dispatcher.BeginInvoke(() => _root.Opacity = 1.0, DispatcherPriority.Background);
        }
        catch
        {
            // 窗口可能正在关闭
        }
    }

    /// <summary>开机后 DWM 就绪时间不可预知，前 30 秒内额外补推几次，尽快从丢失首推中恢复。</summary>
    private void ScheduleStartupKicks()
    {
        foreach (var delaySeconds in new[] { 3, 10, 30 })
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySeconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                KickRender();
            };
            timer.Start();
        }
    }
}

/// <summary>紧急度排序比较器（转发到 UrgencyHelper）。</summary>
public class UrgencyComparer : IComparer<DdlTask>
{
    public int Compare(DdlTask? x, DdlTask? y)
        => UrgencyHelper.CompareUrgency(x, y);
}
