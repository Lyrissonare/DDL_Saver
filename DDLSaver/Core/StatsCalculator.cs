using DDLSaver.Models;
using DDLSaver.Services;

namespace DDLSaver.Core;

/// <summary>统计页数据快照。</summary>
public class StatsSnapshot
{
    public int StreakDays { get; init; }          // 连续完成天数
    public int CompletedThisWeek { get; init; }   // 本周完成数
    public int DueThisWeek { get; init; }         // 本周到期数
    public int TotalCompleted { get; init; }      // 累计完成数
    public int OverdueCount { get; init; }        // 当前逾期数
    public List<CompletionRecord> RecentCompletions { get; init; } = new();
}

/// <summary>
/// 统计规则（规格 3.6）：
/// - 连续完成天数：当天完成 ≥1 个任务则续期；今天尚未完成时不中断（从昨天起回溯）；
/// - 本周：周一为一周开始；
/// - 当前逾期数：未完成且已过 Deadline 的任务数。
/// </summary>
public static class StatsCalculator
{
    public static StatsSnapshot Compute(DataStore store, DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        var weekStart = StartOfWeek(n);
        var weekEnd = weekStart.AddDays(7);

        var completions = store.Completions
            .OrderByDescending(c => c.CompletedAt)
            .ToList();

        return new StatsSnapshot
        {
            StreakDays = ComputeStreak(completions, n),

            CompletedThisWeek = completions.Count(c =>
                c.CompletedAt >= weekStart && c.CompletedAt < weekEnd),

            DueThisWeek = store.Tasks.Count(t =>
                !t.IsCompleted && t.Deadline >= weekStart && t.Deadline < weekEnd),

            TotalCompleted = completions.Count,

            OverdueCount = store.Tasks.Count(t => UrgencyHelper.IsOverdue(t, n)),

            RecentCompletions = completions.Take(20).ToList(),
        };
    }

    public static DateTime StartOfWeek(DateTime date)
    {
        // 周一 = 一周开始
        var diff = ((int)date.DayOfWeek + 6) % 7;
        return date.Date.AddDays(-diff);
    }

    public static int ComputeStreak(List<CompletionRecord> completions, DateTime now)
    {
        var byDay = completions
            .Select(c => c.CompletedAt.Date)
            .Distinct()
            .ToHashSet();

        var day = now.Date;
        // 今天尚未完成：从昨天起算（今天还有机会续期）
        if (!byDay.Contains(day))
            day = day.AddDays(-1);

        var streak = 0;
        while (byDay.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }
}
