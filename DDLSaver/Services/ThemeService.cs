using System.Windows;
using System.Windows.Media;
using DDLSaver.Core;
using DDLSaver.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DDLSaver.Services;

/// <summary>
/// 主题服务（浅色 / 暗色）：
/// - 界面外观交给 WPF-UI 的 Fluent 主题字典，切换时同步所有已打开窗口的材质与明暗；
/// - 语义画刷由本服务从 Fluent 设计令牌显式重建，避免嵌套 DynamicResource 不刷新；
/// - 代码构建的窗口（浮窗/通知）通过 Get() 读取，并在 Changed 后重新取色。
/// </summary>
public static class ThemeService
{
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>所有 Fluent 窗口共用的背景材质。</summary>
    public static WindowBackground BackgroundMode { get; private set; } = WindowBackground.Mica;

    /// <summary>主题切换（App 负责重建浮窗等代码构建界面）。</summary>
    public static event Action? Changed;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var uiTheme = theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;

        // 只切换主题字典与强调色；窗口材质在本方法末尾逐一刷新，
        // 避免 WPF-UI 默认只处理 Application.MainWindow（本应用的 MainWindow 是透明浮窗）。
        ApplicationThemeManager.Apply(uiTheme, WindowBackdropType.None, updateAccent: true);

        UpdatePalette(theme == AppTheme.Dark);
        UpdateUrgencyBrushes(theme == AppTheme.Dark);

        var app = System.Windows.Application.Current;
        if (app is not null)
        {
            foreach (System.Windows.Window window in app.Windows)
            {
                if (window is FluentWindow fluent && fluent.ExtendsContentIntoTitleBar)
                {
                    // 跟随主题刷新背景材质（纯色时同时切换纯白/纯黑）
                    ApplyWindowBackground(fluent);
                }
                else if (window is not FluentWindow)
                {
                    ApplyTitleBar(window);
                }
            }
        }

        Changed?.Invoke();
    }

    /// <summary>切换背景材质（云母 / 亚克力 / 纯色），对已打开的所有 Fluent 窗口即时生效。</summary>
    public static void ApplyBackground(WindowBackground mode)
    {
        BackgroundMode = mode;
        var app = System.Windows.Application.Current;
        if (app is null)
            return;
        foreach (System.Windows.Window window in app.Windows)
            ApplyWindowBackground(window);
    }

    /// <summary>把当前背景材质应用到单个 Fluent 窗口（浮窗/通知等原生窗口保持不透明外观）。</summary>
    public static void ApplyWindowBackground(System.Windows.Window window)
    {
        if (window is not FluentWindow fluent)
            return;

        var backdrop = BackgroundMode switch
        {
            WindowBackground.Acrylic => WindowBackdropType.Acrylic,
            WindowBackground.Solid => WindowBackdropType.None,
            _ => WindowBackdropType.Mica,
        };
        fluent.WindowBackdropType = backdrop;
        fluent.Background = BackgroundMode == WindowBackground.Solid
            ? C(Current == AppTheme.Dark ? Colors.Black : Colors.White)
            : Brushes.Transparent;

        if (new System.Windows.Interop.WindowInteropHelper(fluent).Handle == IntPtr.Zero)
            return;

        var uiTheme = Current == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        WindowBackgroundManager.UpdateBackground(fluent, uiTheme, backdrop);
    }

    /// <summary>按当前主题设置窗口标题栏明暗（DWM immersive dark mode；失败静默忽略）。</summary>
    public static void ApplyTitleBar(System.Windows.Window? window)
    {
        if (window is null)
            return;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            var on = Current == AppTheme.Dark ? 1 : 0;
            _ = Interop.NativeMethods.DwmSetWindowAttribute(
                hwnd, Interop.NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch
        {
            // 旧系统不支持该属性时忽略
        }
    }

    private static void UpdateUrgencyBrushes(bool dark)
    {
        var res = System.Windows.Application.Current?.Resources;
        if (res is null)
            return;

        // 与 Fluent 系统色阶一致：安全绿 / 注意黄 / 危急红，暗色使用更亮的色阶保证可读性
        Set(res, "UrgencyDoneBrush", dark ? C(0x8E, 0x8E, 0x93) : C(0x9E, 0x9E, 0x9E));
        Set(res, "UrgencySafeBrush", dark ? C(0x6C, 0xCB, 0x5F) : C(0x0F, 0x7B, 0x0F));
        Set(res, "UrgencyMediumBrush", dark ? C(0xFC, 0xE1, 0x00) : C(0x9D, 0x5D, 0x00));
        Set(res, "UrgencyUrgentBrush", dark ? C(0xFF, 0x8A, 0x65) : C(0xCA, 0x50, 0x10));
        Set(res, "UrgencyCriticalBrush", dark ? C(0xFF, 0x99, 0xA4) : C(0xC4, 0x2B, 0x1C));
        Set(res, "UrgencyOverdueBrush", dark ? C(0xFF, 0x6B, 0x6B) : C(0xA4, 0x26, 0x2C));
    }

    private static void UpdatePalette(bool dark)
    {
        var app = System.Windows.Application.Current;
        var res = app?.Resources;
        if (app is null || res is null)
            return;

        // 画刷资源内部的 DynamicResource 在首次解析后不会随主题字典替换而刷新，
        // 因此这里在每次切换时显式读取 Fluent 设计令牌并重建画刷。
        Color Token(string key, string fallback)
        {
            var value = app.TryFindResource(key);
            return value switch
            {
                Color color => color,
                SolidColorBrush brush => brush.Color,
                _ => (Color)ColorConverter.ConvertFromString(fallback),
            };
        }

        Set(res, "AppBgBrush", C(Token("ApplicationBackgroundColor", dark ? "#FF202020" : "#FFFAFAFA")));
        Set(res, "CardBgBrush", C(Token("CardBackgroundFillColorDefault", dark ? "#0DFFFFFF" : "#B3FFFFFF")));
        Set(res, "ToolbarBgBrush", C(Token("LayerFillColorDefault", dark ? "#4C3A3A3A" : "#80FFFFFF")));
        Set(res, "BorderBrush", C(Token("CardStrokeColorDefault", dark ? "#19000000" : "#0F000000")));
        Set(res, "TextPrimaryBrush", C(Token("TextFillColorPrimary", dark ? "#FFFFFFFF" : "#E4000000")));
        Set(res, "TextSecondaryBrush", C(Token("TextFillColorSecondary", dark ? "#C5FFFFFF" : "#9E000000")));
        Set(res, "TextMutedBrush", C(Token("TextFillColorTertiary", dark ? "#87FFFFFF" : "#72000000")));
        Set(res, "InputBgBrush", C(Token("ControlFillColorDefault", dark ? "#0FFFFFFF" : "#B3FFFFFF")));
        Set(res, "InputTextBrush", C(Token("TextFillColorPrimary", dark ? "#FFFFFFFF" : "#E4000000")));
        Set(res, "InputBorderBrush", C(Token("ControlStrokeColorDefault", dark ? "#12FFFFFF" : "#0F000000")));
        Set(res, "FloatCardBrush", C(Token("CardBackgroundFillColorDefault", dark ? "#0DFFFFFF" : "#B3FFFFFF")));
        Set(res, "FloatCardBorderBrush", C(Token("ControlStrokeColorDefault", dark ? "#12FFFFFF" : "#0F000000")));

        Set(res, "HoverBgBrush", C(Token("ControlFillColorSecondary", dark ? "#15FFFFFF" : "#80F9F9F9")));
        Set(res, "PressedBgBrush", C(Token("ControlFillColorTertiary", dark ? "#08FFFFFF" : "#4DF9F9F9")));
        Set(res, "ListHoverBrush", C(Token("SubtleFillColorSecondary", dark ? "#0FFFFFFF" : "#09000000")));
        Set(res, "ListSelectedBrush", C(Token("ControlFillColorSecondary", dark ? "#15FFFFFF" : "#80F9F9F9")));
        Set(res, "SliderTrackBrush", C(Token("ControlStrongFillColorDefault", dark ? "#8BFFFFFF" : "#72000000")));
        Set(res, "SliderThumbBrush", C(Token("SolidBackgroundFillColorBase", dark ? "#FF202020" : "#FFF3F3F3")));
        Set(res, "ScrollThumbBrush", C(Token("ControlStrongFillColorDefault", dark ? "#8BFFFFFF" : "#72000000")));

        // 暗色下紧急度用亮色阶，仅适合深色文字徽章；浅色下为深色底配白字
        Set(res, "BadgeTextBrush", C(dark ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White));
    }

    private static SolidColorBrush C(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush C(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static void Set(ResourceDictionary res, string key, Brush brush)
        => res[key] = brush;

    /// <summary>读取主题画刷（未注册时回退灰色，保证不空引用）。</summary>
    public static Brush Get(string key)
        => (System.Windows.Application.Current?.TryFindResource(key) as Brush) ?? Brushes.Gray;

    /// <summary>浮窗卡片底色（透明度由设置另行叠加）。</summary>
    public static Color FloatBaseColor()
        => Current == AppTheme.Dark
            ? Color.FromRgb(0x2B, 0x2B, 0x2B)
            : Color.FromRgb(0xFB, 0xFB, 0xFB);

    /// <summary>紧急度配色（供浮窗/列表/统计等所有界面使用）。</summary>
    public static Brush LevelBrush(UrgencyLevel level) => level switch
    {
        UrgencyLevel.Done => Get("UrgencyDoneBrush"),
        UrgencyLevel.Safe => Get("UrgencySafeBrush"),
        UrgencyLevel.Medium => Get("UrgencyMediumBrush"),
        UrgencyLevel.Urgent => Get("UrgencyUrgentBrush"),
        UrgencyLevel.Critical => Get("UrgencyCriticalBrush"),
        UrgencyLevel.Overdue => Get("UrgencyOverdueBrush"),
        _ => Get("TextSecondaryBrush"),
    };
}
