using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.WinUI.Notifications;
using DDLSaver.Core;
using DDLSaver.Models;
using DDLSaver.Services;
using DDLSaver.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace DDLSaver;

/// <summary>
/// 应用宿主：单实例守护 + 全局协调（托盘/浮窗/提醒/热键/早报/全屏）。
/// </summary>
public partial class App : Application
{
    public static App Instance => (App)Current;

    public DataStore Store { get; } = new();
    public SettingsService SettingsService { get; }
    public ToastService Toast { get; } = new();
    public ReminderEngine Engine { get; }
    public HotkeyService Hotkey { get; } = new();
    public FullscreenDetector Fullscreen { get; }

    public FloatWindow? Float { get; private set; }
    public MainWindow? MainWin { get; private set; }
    public QuickAddWindow? QuickAdd { get; private set; }

    private TaskbarIcon? _tray;
    private Mutex? _mutex;
    private bool _isPrimary;
    private bool _exiting;
    private Task? _pipeLoop;

    private const string PipeName = "DdlSaver.Activate";
    private const string MutexName = "DdlSaver.SingleInstance";

    public App()
    {
        SettingsService = new SettingsService(Store.DataDir);
        Engine = new ReminderEngine(Store);
        Fullscreen = new FullscreenDetector(GetSelfWindowHandle);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 虚拟机/远程桌面等环境下 WPF 硬件加速可能导致窗口内容渲染为空白，
        // 尝试软件渲染（环境中无效时自动回到硬件加速）
        try
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;
        }
        catch { }

        // ---- 单实例 ----
        _mutex = new Mutex(true, MutexName, out _isPrimary);
        if (!_isPrimary)
        {
            // 第二实例：通知主实例显示窗口后立即退出。
            // 注意：此路径下 OnExit 必须跳过一切写盘（见 OnExit）。
            TrySignalPrimary();
            Shutdown();
            return;
        }

        // ---- Toast 激活必须最早订阅（未打包应用 COM 激活回调） ----
        Toast.HookActivation();
        Toast.Activated += args => HandleActivation(args);

        // ---- 数据与设置 ----
        Store.Load();
        SettingsService.Load();
        Store.DataChanged += OnDataChanged;

        // ---- 主题（需在窗口创建前应用） ----
        ThemeService.Apply(SettingsService.Settings.ThemeMode);
        ThemeService.ApplyBackground(SettingsService.Settings.Background);
        ThemeService.Changed += () =>
        {
            try
            {
                Float?.ApplySettings();
                // 任务行的紧急度画刷是刷新时解析的静态引用，主题切换后必须重建列表，
                // 否则徽章/标题会残留上一个主题的颜色
                MainWin?.RefreshList();
                MainWin?.RefreshStats();
            }
            catch
            {
                // 窗口未就绪时忽略
            }
        };

        // ---- Toast AUMID 快捷方式 ----
        Toast.EnsureRegistered();

        // ---- 浮窗 ----
        Float = new FloatWindow(this);
        Float.Launch();

        // ---- 托盘 ----
        CreateTray();

        // ---- 提醒引擎 ----
        Engine.RemindersDue += OnRemindersDue;
        Engine.Start();

        // ---- 全屏检测 ----
        Fullscreen.Changed += OnFullscreenChanged;
        Fullscreen.Start();

        // ---- 全局热键 ----
        Hotkey.Pressed += OnHotkeyPressed;
        var hotkeyOk = Hotkey.Register(SettingsService.Settings);
        if (SettingsService.Settings.HotkeyEnabled && !hotkeyOk)
        {
            _tray?.ShowBalloonTip("热键冲突",
                "Ctrl+Alt+D 已被其他程序占用。打开主窗口「设置 → 快捷键」可改键。",
                BalloonIcon.Warning);
        }

        // ---- 跨进程激活管道（第二实例 / Toast 冷启动） ----
        StartPipeServer();

        // ---- Toast 冷启动：进程由点击 Toast 拉起 ----
        if (ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
        {
            Dispatcher.BeginInvoke(() => ShowMainWindow());
        }

        // ---- 开发调试开关：启动即打开"任务编辑"窗口（对用户无影响） ----
        if (e.Args.Contains("--open-edit"))
        {
            Dispatcher.BeginInvoke(() =>
            {
                ShowMainWindow();
                MainWin?.OpenNewTaskDialog();
            });
        }

        // ---- 开发调试开关：预览应用内通知外观（Toast 不可用时的兜底样式） ----
        if (e.Args.Contains("--toast-preview"))
        {
            Dispatcher.BeginInvoke(() => InAppToastWindow.Show(
                "DDL 提醒（预览）", "距离「学习通第 3 章」截止还有 1 小时（应用内通知样式预览）", "show"));
        }

        // ---- 今日早报（跨天触发） ----
        ShowMorningReportIfNeeded();

        // ---- 状态提示 ----
        if (Store.WasFirstRun)
        {
            SetStatus("首次使用：已生成 2 条示例任务，可在主窗口选中后删除或编辑");
        }
        if (Store.RecoveredFromBackup)
        {
            MessageBox.Show("检测到数据文件损坏，已自动从备份 data.json.bak 恢复最近一次保存的数据。",
                "数据已恢复", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        // 关键：非主实例（单实例竞争的失败者）从未加载数据，绝不能写盘，
        // 否则会把主实例的数据文件覆盖成空数据。
        if (_isPrimary)
        {
            try
            {
                Hotkey.Unregister();
                Engine.Stop();
                Store.Save();
                SettingsService.Save();
            }
            catch
            {
                // 退出清理失败不阻断
            }
        }
        _tray?.Dispose();
        _tray = null;
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // 忽略：非持锁线程释放会抛异常
        }
        base.OnExit(e);
    }

    #region 窗口入口

    public void ShowMainWindow()
    {
        // 用户关闭主窗口后实例失效：重建而不是复用一个已 Close 的窗口
        if (MainWin is null)
        {
            MainWin = new MainWindow(this);
            MainWin.Closed += (_, _) => MainWin = null;
        }
        MainWin.ShowAndActivate();
    }

    public void ShowQuickAdd()
    {
        // 快速添加可被 Esc 关闭：关闭后重建
        if (QuickAdd is null)
        {
            QuickAdd = new QuickAddWindow(this);
            QuickAdd.Closed += (_, _) => QuickAdd = null;
        }
        QuickAdd.ActivateQuickAdd();
    }

    public void ShowMorningReport()
    {
        var win = new MorningReportWindow(this);
        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.ShowDialog();
    }

    private void ShowMorningReportIfNeeded()
    {
        var today = DateTime.Today;
        var crossDay = SettingsService.Settings.LastRunDate != today;
        if (!crossDay)
            return;

        SettingsService.Settings.LastRunDate = today;
        SettingsService.Save();

        if (SettingsService.Settings.MorningReportEnabled)
        {
            Dispatcher.BeginInvoke(() => ShowMorningReport(), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    public void SetStatus(string message)
    {
        try
        {
            MainWin?.SetStatus(message);
        }
        catch
        {
            // 主窗口尚未创建时忽略
        }
    }

    /// <summary>处理 Toast 点击 / 应用内弹窗点击 / 第二实例信号。</summary>
    public void HandleActivation(string arguments)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => HandleActivation(arguments));
            return;
        }

        if (arguments.StartsWith("task:", StringComparison.OrdinalIgnoreCase))
        {
            var idText = arguments[5..];
            if (Guid.TryParse(idText, out var id))
            {
                ShowMainWindow();
                MainWin?.LocateTask(id);
                return;
            }
        }

        ShowMainWindow();
    }

    private void OnHotkeyPressed() => ShowQuickAdd();

    #endregion

    #region 数据变更与提醒

    private void OnDataChanged()
    {
        try
        {
            Float?.UpdateContent();
            MainWin?.RefreshList();
            MainWin?.RefreshStats();
            UpdateTrayToolTip();
        }
        catch
        {
            // 刷新失败不致命（窗口可能正在关闭）
        }
    }

    private void OnRemindersDue(IReadOnlyList<ReminderHit> hits)
    {
        foreach (var hit in hits)
        {
            var ok = Toast.TryShow(hit.Title, hit.Body, hit.Arguments);
            if (!ok)
            {
                // 兜底：提醒永远不允许静默丢失
                try
                {
                    InAppToastWindow.Show(hit.Title, hit.Body + "（系统通知不可用，使用应用内提醒）", hit.Arguments);
                }
                catch
                {
                    // 应用内弹窗也失败时：至少写入状态栏
                    SetStatus($"⚠ 提醒「{hit.Title}」{hit.Body}");
                }
            }

            ReminderEngine.MarkSent(hit.Task, hit.Kind, DateTime.Now);
        }
        Store.Save();
    }

    #endregion

    #region 全屏自动隐藏

    private IntPtr GetSelfWindowHandle()
    {
        if (Float is { IsVisible: true })
            return new WindowInteropHelper(Float).Handle;
        if (MainWin is { IsVisible: true })
            return new WindowInteropHelper(MainWin).Handle;
        return IntPtr.Zero;
    }

    private void OnFullscreenChanged(bool fullscreen)
    {
        if (!SettingsService.Settings.FullscreenAutoHide)
            return;
        Float?.SetFullscreenHidden(fullscreen);
    }

    #endregion

    #region 托盘

    private void CreateTray()
    {
        _tray = new TaskbarIcon
        {
            IconSource = CreateAppIcon(),
            ToolTipText = "DDL拯救器",
        };
        _tray.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
        _tray.ContextMenu = BuildTrayMenu();
        UpdateTrayToolTip();
    }

    private void UpdateTrayToolTip()
    {
        if (_tray is null) return;
        var urgent = UrgencyHelper.UrgentPendingCount(Store.Tasks);
        _tray.ToolTipText = urgent > 0
            ? $"DDL拯救器 —— {urgent} 个任务将在 24 小时内到期"
            : "DDL拯救器 —— 盯住每一个 DDL";
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        var itemMain = new MenuItem { Header = "打开主窗口" };
        itemMain.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(itemMain);

        var itemQuick = new MenuItem { Header = "快速添加（Ctrl+Alt+D）" };
        itemQuick.Click += (_, _) => ShowQuickAdd();
        menu.Items.Add(itemQuick);

        menu.Items.Add(new Separator());

        // 浮窗模式（打开时刷新勾选状态）
        var mFull = new MenuItem { Header = "完整浮窗" };
        var mMini = new MenuItem { Header = "极简模式（圆点）" };
        var mHidden = new MenuItem { Header = "完全隐藏" };
        mFull.Click += (_, _) => TrySetTrayMode(VisibilityMode.Full);
        mMini.Click += (_, _) => TrySetTrayMode(VisibilityMode.Mini);
        mHidden.Click += (_, _) => TrySetTrayMode(VisibilityMode.Hidden);
        menu.Opened += (_, _) =>
        {
            var eff = Float?.EffectiveMode ?? SettingsService.Settings.VisibilityMode;
            mFull.IsChecked = eff == VisibilityMode.Full;
            mMini.IsChecked = eff == VisibilityMode.Mini;
            mHidden.IsChecked = eff == VisibilityMode.Hidden;
        };
        menu.Items.Add(mFull);
        menu.Items.Add(mMini);
        menu.Items.Add(mHidden);

        var mThrough = new MenuItem { Header = "鼠标穿透（纯展示）" };
        mThrough.Click += (_, _) => ToggleTrayMouseThrough();
        menu.Opened += (_, _) => mThrough.IsChecked = SettingsService.Settings.MouseThrough;
        menu.Items.Add(mThrough);

        menu.Items.Add(new Separator());

        var mReport = new MenuItem { Header = "今日早报" };
        mReport.Click += (_, _) => ShowMorningReport();
        menu.Items.Add(mReport);

        var mExit = new MenuItem { Header = "退出" };
        mExit.Click += (_, _) => ExitApplication();
        menu.Items.Add(mExit);

        return menu;
    }

    private void TrySetTrayMode(VisibilityMode mode)
    {
        if (Float is null)
            return;
        if (!Float.TrySetMode(mode, out var reason))
        {
            MessageBox.Show(reason!, "无法完全隐藏", MessageBoxButton.OK, MessageBoxImage.Information);
            Float.TrySetMode(VisibilityMode.Mini, out _);
            SetStatus("已降级为极简模式（存在 24 小时内到期的任务）");
        }
    }

    private void ToggleTrayMouseThrough()
    {
        var s = SettingsService.Settings;
        s.MouseThrough = !s.MouseThrough;
        SettingsService.Save();
        Float?.ApplyWindowStylesPublic();
        SetStatus(s.MouseThrough ? "已开启鼠标穿透（浮窗点击将穿透到下层）" : "已关闭鼠标穿透");
    }

    public void ExitApplication()
    {
        if (_exiting)
            return;
        _exiting = true;
        try
        {
            Store.Save();
            SettingsService.Save();
        }
        catch
        {
            // 忽略
        }
        Shutdown();
    }

    #endregion

    #region 单实例管道

    private void StartPipeServer()
    {
        _pipeLoop = Task.Run(() =>
        {
            while (!_exiting)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var msg = reader.ReadLine();
                    if (!string.IsNullOrEmpty(msg) && !_exiting)
                    {
                        var m = msg;
                        Dispatcher.BeginInvoke(() => HandleActivation(m));
                    }
                }
                catch
                {
                    if (!_exiting)
                        Thread.Sleep(500);
                }
            }
        });
    }

    private void TrySignalPrimary()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("show");
        }
        catch
        {
            // 主实例可能正在退出：静默放弃
        }
    }

    #endregion

    #region 图标

    /// <summary>加载应用图标（托盘 / 主窗口）。</summary>
    public ImageSource CreateAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/app.png");
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // 图标缺失时的兜底（正常发布不会发生）
            var fallback = new DrawingImage();
            fallback.Freeze();
            return fallback;
        }
    }

    #endregion
}
