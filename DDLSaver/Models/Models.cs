using System.Text.Json.Serialization;

namespace DDLSaver.Models;

/// <summary>任务分类。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskCategory
{
    OnlineCourse,  // 网课
    GroupProject,  // 小组作业
    Exam,          // 考试
    Homework,      // 课程作业
    Misc,          // 杂事
}

/// <summary>重复周期。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Recurrence
{
    None,      // 不重复
    Weekly,    // 每周
    Biweekly,  // 每两周
    Monthly,   // 每月
}

/// <summary>浮窗可见性模式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VisibilityMode
{
    Full,    // 完整浮窗（默认）
    Mini,    // 极简模式：小圆点 + 24h 内到期数量
    Hidden,  // 完全隐藏（受"≤24h 到期硬规则"约束）
}

/// <summary>浮窗停靠位置。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FloatDock
{
    Top,    // 顶部居中
    Bottom, // 底部居中
    Free,   // 自由拖拽
}

/// <summary>应用主题（浅色 / 暗色）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>主窗口与对话框的背景材质。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WindowBackground
{
    Mica,     // 云母
    Acrylic,  // 亚克力
    Solid,    // 纯色：浅色纯白 / 暗色纯黑
}

/// <summary>子任务（小组作业分工记录等）。</summary>
public class SubTask
{
    public string Title { get; set; } = "";
    public bool IsDone { get; set; }
    public string? Note { get; set; }

    public SubTask Clone() => new() { Title = Title, IsDone = IsDone, Note = Note };
}

/// <summary>
/// 提醒方案：
/// 默认 = 前 7 天提醒一次 + 前 3 天起每天一次（默认 09:00）+ 24h 内每 3 小时一次。
/// </summary>
public class ReminderPlan
{
    /// <summary>前 7 天：提醒一次。</summary>
    public bool Enable7d { get; set; } = true;

    /// <summary>前 3 天起：每天一次。</summary>
    public bool Enable3dDaily { get; set; } = true;

    /// <summary>每日提醒时间（HH:mm）。</summary>
    public string DailyTime { get; set; } = "09:00";

    /// <summary>≤24 小时：每 N 小时一次。</summary>
    public bool Enable24hFrequent { get; set; } = true;

    /// <summary>高频提醒间隔（小时，2–6）。</summary>
    public int FreqHours { get; set; } = 3;

    public ReminderPlan Clone() => new()
    {
        Enable7d = Enable7d,
        Enable3dDaily = Enable3dDaily,
        DailyTime = DailyTime,
        Enable24hFrequent = Enable24hFrequent,
        FreqHours = FreqHours,
    };

    /// <summary>尝试解析 "HH:mm"；失败返回默认 09:00。</summary>
    public TimeSpan GetDailyTime()
        => TimeSpan.TryParseExact(DailyTime, new[] { "h\\:mm", "hh\\:mm" }, System.Globalization.CultureInfo.InvariantCulture, out var t)
            ? t
            : new TimeSpan(9, 0, 0);
}

/// <summary>DDL 任务本体。</summary>
public class DdlTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>标题（必填）。</summary>
    public string Title { get; set; } = "";

    public TaskCategory Category { get; set; } = TaskCategory.Misc;

    /// <summary>截止时间（本地时间）。</summary>
    public DateTime Deadline { get; set; }

    /// <summary>进度总量（可空 = 无进度）。</summary>
    public int? TotalUnits { get; set; }

    public int DoneUnits { get; set; }

    /// <summary>进度单位名，如"节 / 章 / 页"。</summary>
    public string? UnitName { get; set; }

    /// <summary>展示用单位名；未填写时默认"次"。</summary>
    [JsonIgnore]
    public string UnitText => string.IsNullOrWhiteSpace(UnitName) ? "次" : UnitName!;

    public Recurrence Recurrence { get; set; } = Recurrence.None;

    public List<SubTask> SubTasks { get; set; } = new();

    public string? Note { get; set; }

    public ReminderPlan Reminders { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>完成时间；非空即已完成。</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>各提醒类型最近触发时间（键：7d / daily / 24h / overdue）。</summary>
    public Dictionary<string, DateTime> LastNotified { get; set; } = new();

    [JsonIgnore]
    public bool IsCompleted => CompletedAt != null;

    [JsonIgnore]
    public bool HasProgress => TotalUnits.HasValue && TotalUnits.Value > 0;

    /// <summary>剩余进度数量（不会为负）。</summary>
    [JsonIgnore]
    public int RemainingUnits => HasProgress ? Math.Max(0, TotalUnits!.Value - DoneUnits) : 0;

    public DdlTask CloneBase() => new()
    {
        Id = Id,
        Title = Title,
        Category = Category,
        Deadline = Deadline,
        TotalUnits = TotalUnits,
        DoneUnits = DoneUnits,
        UnitName = UnitName,
        Recurrence = Recurrence,
        SubTasks = SubTasks.Select(s => s.Clone()).ToList(),
        Note = Note,
        Reminders = Reminders.Clone(),
        CreatedAt = CreatedAt,
        CompletedAt = CompletedAt,
        LastNotified = new Dictionary<string, DateTime>(LastNotified),
    };
}

/// <summary>完成打卡记录（用于统计页，含重复任务每次完成）。</summary>
public class CompletionRecord
{
    public Guid TaskId { get; set; }
    public string Title { get; set; } = "";
    public DateTime CompletedAt { get; set; }
}

/// <summary>data.json 根对象。</summary>
public class AppData
{
    public int Version { get; set; } = 1;
    public List<DdlTask> Tasks { get; set; } = new();
    public List<CompletionRecord> Completions { get; set; } = new();
}

/// <summary>settings.json：全部用户可调设置持久化于此。</summary>
public class AppSettings
{
    // ---- 浮窗 ----
    public double FloatOpacity { get; set; } = 0.9;          // 窗口整体不透明度 30%–100%
    public double FloatFontSize { get; set; } = 14;          // 10–28
    public int FloatCount { get; set; } = 3;                 // 1–5 条
    public FloatDock FloatDock { get; set; } = FloatDock.Top;
    public string? FloatScreenDeviceName { get; set; }       // 记忆所在屏幕
    public double FloatX { get; set; } = 0;
    public double FloatY { get; set; } = 0;
    public double MiniX { get; set; } = -1;                  // -1 = 使用默认（主屏右上角）
    public double MiniY { get; set; } = -1;

    // ---- 浮窗配色（独立于窗口整体不透明度） ----
    /// <summary>浮窗卡片底色透明度（0–100%，0 = 完全透明只剩文字）。</summary>
    public double FloatBackgroundOpacity { get; set; } = 0.94;
    /// <summary>浮窗文字透明度（30–100%）。</summary>
    public double FloatTextOpacity { get; set; } = 1.0;

    public VisibilityMode VisibilityMode { get; set; } = VisibilityMode.Full;
    public bool MouseThrough { get; set; }                   // 鼠标穿透模式
    public bool FullscreenAutoHide { get; set; } = true;

    // ---- 外观主题 ----
    public AppTheme ThemeMode { get; set; } = AppTheme.Light;
    public WindowBackground Background { get; set; } = WindowBackground.Mica;

    // ---- 系统 ----
    public bool AutoStart { get; set; }

    // ---- 全局热键 ----
    public bool HotkeyEnabled { get; set; } = true;
    public uint HotkeyModifiers { get; set; } = NativeHotkey.ModControl | NativeHotkey.ModAlt;
    public uint HotkeyKey { get; set; } = 0x44;              // 'D'

    // ---- 新任务默认提醒方案 ----
    public ReminderPlan DefaultReminderPlan { get; set; } = new();

    // ---- 今日早报 ----
    public bool MorningReportEnabled { get; set; } = true;
    public DateTime? LastRunDate { get; set; }
}

/// <summary>热键修饰键常量（与 Win32 对应，供设置序列化）。</summary>
public static class NativeHotkey
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
}
