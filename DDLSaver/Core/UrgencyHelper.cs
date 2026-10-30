using System.Windows.Media;
using DDLSaver.Models;

namespace DDLSaver.Core;

/// <summary>紧急度等级（用于着色与呼吸动画判定）。</summary>
public enum UrgencyLevel
{
    Done,      // 已完成（灰）
    Safe,      // >7 天（绿）
    Medium,    // 3–7 天（黄）
    Urgent,    // <3 天（红）
    Critical,  // ≤1 天（红 + 呼吸闪烁）
    Overdue,   // 已逾期（深红，置顶加粗）
}

/// <summary>
/// 任务"剩余时间"计算与文案的单一事实来源。
/// 规则：剩余天数 = (Deadline.Date - Today).Days，全部使用本地时间；
/// 截止日当天 00:00 起即视为「今天！」；超过 Deadline 时刻进入逾期。
/// </summary>
public static class UrgencyHelper
{
    public static int RemainingCalendarDays(DateTime deadline)
        => (deadline.Date - DateTime.Today).Days;

    /// <summary>是否已逾期（超过 Deadline 时刻且未完成）。</summary>
    public static bool IsOverdue(DdlTask task, DateTime? now = null)
        => task.CompletedAt == null && (now ?? DateTime.Now) > task.Deadline;

    public static int OverdueDays(DdlTask task, DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        return Math.Max(0, (n.Date - task.Deadline.Date).Days);
    }

    /// <summary>剩余时间文案（不含标题）："还有 3 天" / "今天！" / "还有 5 小时" / "已逾期 2 天"。</summary>
    public static string RelativeText(DdlTask task, DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        if (IsOverdue(task, n))
            return $"已逾期 {OverdueDays(task, n)} 天";

        if (task.Deadline.Date == n.Date)
            return "今天！";

        var delta = task.Deadline - n;
        if (delta <= TimeSpan.FromHours(24))
            return $"还有 {Math.Max(1, (int)Math.Ceiling(delta.TotalHours))} 小时";

        var days = RemainingCalendarDays(task.Deadline);
        return $"还有 {Math.Max(1, days)} 天";
    }

    /// <summary>
    /// 进度追加文案：" · 还需 3节 ≈ 每天 1节"。
    /// Y = ceil(剩余 ÷ max(剩余天数,1))；剩余天数为 0 时 Y = 全部剩余。
    /// </summary>
    public static string ProgressSuffix(DdlTask task)
    {
        if (!task.HasProgress)
            return "";

        var remaining = task.RemainingUnits;
        if (remaining <= 0)
            return "";

        var unit = string.IsNullOrWhiteSpace(task.UnitName) ? "" : task.UnitName;
        var days = RemainingCalendarDays(task.Deadline);
        var perDay = days <= 0 ? remaining : (int)Math.Ceiling(remaining / (double)Math.Max(days, 1));
        return $" · 还需 {remaining}{unit} ≈ 每天 {perDay}{unit}";
    }

    /// <summary>浮窗单行文案："距离 {标题} 还有 ..."。</summary>
    public static string FloatLine(DdlTask task, DateTime? now = null)
        => $"距离 {task.Title} {RelativeText(task, now)}";

    /// <summary>紧急度等级（已完成 → Done）。</summary>
    public static UrgencyLevel Level(DdlTask task, DateTime? now = null)
    {
        if (task.IsCompleted)
            return UrgencyLevel.Done;

        if (IsOverdue(task, now))
            return UrgencyLevel.Overdue;

        var days = RemainingCalendarDays(task.Deadline);
        if (days <= 1)
            return UrgencyLevel.Critical;
        if (days <= 2)
            return UrgencyLevel.Urgent;
        if (days <= 7)
            return UrgencyLevel.Medium;
        return UrgencyLevel.Safe;
    }

    /// <summary>紧急度配色（随主题切换，见 ThemeService）。</summary>
    public static Brush LevelBrush(UrgencyLevel level)
        => Services.ThemeService.LevelBrush(level);

    public static string LevelName(UrgencyLevel level) => level switch
    {
        UrgencyLevel.Done => "已完成",
        UrgencyLevel.Safe => "宽裕",
        UrgencyLevel.Medium => "注意",
        UrgencyLevel.Urgent => "紧急",
        UrgencyLevel.Critical => "危急",
        UrgencyLevel.Overdue => "已逾期",
        _ => "",
    };

    /// <summary>
    /// 紧急度排序比较：
    /// 1) 未完成在前，完成后排；
    /// 2) 逾期任务置顶，逾期越久越靠前；
    /// 3) 其余按截止时间升序（剩余时间短者优先）；
    /// 4) 再按压力值（剩余工作量 ÷ 剩余天数）降序。
    /// </summary>
    public static int CompareUrgency(DdlTask? a, DdlTask? b, DateTime? now = null)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return 1;
        if (b is null) return -1;

        var n = now ?? DateTime.Now;

        // 1) 未完成在前
        if (a.IsCompleted != b.IsCompleted)
            return a.IsCompleted ? 1 : -1;

        // 2) 逾期置顶，逾期越久越前
        var ao = IsOverdue(a, n);
        var bo = IsOverdue(b, n);
        if (ao != bo)
            return ao ? -1 : 1;
        if (ao)
        {
            var c = OverdueDays(b, n).CompareTo(OverdueDays(a, n));
            if (c != 0) return c;
        }

        // 3) 截止时间升序
        var t = a.Deadline.CompareTo(b.Deadline);
        if (t != 0) return t;

        // 4) 压力值降序（无进度视为 0，排在一侧同截止时间者之后）
        return Pressure(b).CompareTo(Pressure(a));
    }

    /// <summary>压力值 = 剩余工作量 ÷ max(剩余天数, 1)。</summary>
    public static double Pressure(DdlTask task, DateTime? now = null)
    {
        if (!task.HasProgress || task.RemainingUnits <= 0)
            return 0;
        var days = Math.Max(RemainingCalendarDays(task.Deadline), 1);
        return task.RemainingUnits / (double)days;
    }

    /// <summary>是否存在"≤24 小时到期或已逾期"的未完成任务（硬规则判定）。</summary>
    public static bool HasUrgentPending(IEnumerable<DdlTask> tasks, DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        return tasks.Any(t => !t.IsCompleted && (t.Deadline <= n.AddHours(24) || IsOverdue(t, n)));
    }

    /// <summary>≤24 小时到期（含逾期）的未完成任务数量（极简模式数字）。</summary>
    public static int UrgentPendingCount(IEnumerable<DdlTask> tasks, DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        return tasks.Count(t => !t.IsCompleted && t.Deadline <= n.AddHours(24));
    }
}
