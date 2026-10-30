using DDLSaver.Core;
using DDLSaver.Models;

namespace DDLSaver.Services;

/// <summary>一条待发送的提醒。</summary>
public record ReminderHit(DdlTask Task, string Kind, string Title, string Body, string Arguments);

/// <summary>
/// 提醒引擎（规格 3.3）：
/// - 截止前第 7 天：提醒一次（该日 DailyTime 之后）；
/// - 前 3 天起：每天一次（默认 09:00，可配）；
/// - ≤24 小时：每 FreqHours 小时一次；当天 09:00 的每日提醒与高频提醒重叠时去重；
/// - 逾期后：进入逾期立刻一次，此后每天一次；
/// - 防重复：按提醒类型持久化 lastNotified 到任务数据，重启不轰炸；
/// - 轮询周期 30 秒，保证整机睡眠唤醒后也能及时补发。
/// </summary>
public class ReminderEngine
{
    private static readonly string[] Kinds = { "7d", "daily", "24h", "overdue" };

    private readonly DataStore _store;
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    /// <summary>每批到期提醒（App 负责发送 + 落盘 lastNotified）。</summary>
    public event Action<IReadOnlyList<ReminderHit>>? RemindersDue;

    /// <summary>短期去重：同一任务 10 分钟内最多一条（跨类型合并）。</summary>
    private readonly Dictionary<Guid, DateTime> _lastSent = new();

    public ReminderEngine(DataStore store)
    {
        _store = store;
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        _timer.Start();
        Tick(); // 启动立即检查一次
    }

    public void Stop() => _timer.Stop();

    /// <summary>主线程轮询：找出所有到期提醒。</summary>
    public void Tick()
    {
        var now = DateTime.Now;
        var hits = new List<ReminderHit>();

        foreach (var task in _store.Tasks)
        {
            if (task.IsCompleted)
                continue;

            foreach (var kind in Kinds)
            {
                if (!IsDue(task, kind, now))
                    continue;

                // 与其他类型同时到期时只发一条（用第一个种子的文案）
                if (_lastSent.TryGetValue(task.Id, out var last) && (now - last) < TimeSpan.FromMinutes(10))
                {
                    task.LastNotified[kind] = now;
                    continue;
                }

                // 有进度时 Toast 追加"今日应完成量"
                var body = UrgencyHelper.RelativeText(task, now);
                var suffix = UrgencyHelper.ProgressSuffix(task);
                if (suffix.Length > 0)
                {
                    var perDay = ExtractPerDay(suffix);
                    body += $"（今日应完成：{perDay}）";
                }

                hits.Add(new ReminderHit(task, kind, task.Title, body, $"task:{task.Id:N}"));
                _lastSent[task.Id] = now;
                break; // 本任务本轮只发一条
            }
        }

        if (hits.Count > 0)
            RemindersDue?.Invoke(hits);
    }

    private static int ExtractPerDay(string suffix)
    {
        // " · 还需 3节 ≈ 每天 1节" → 解析 1
        var idx = suffix.IndexOf("每天", StringComparison.Ordinal);
        if (idx < 0) return 0;
        var rest = suffix[(idx + 2)..].TrimStart();
        var num = "";
        foreach (var ch in rest)
        {
            if (char.IsDigit(ch)) num += ch;
            else break;
        }
        return int.TryParse(num, out var v) ? v : 0;
    }

    /// <summary>判断某提醒类型此刻是否到期（不修改状态）。</summary>
    private static bool IsDue(DdlTask task, string kind, DateTime now)
    {
        var r = task.Reminders;
        var dailyTime = r.GetDailyTime();
        var overdue = UrgencyHelper.IsOverdue(task, now);
        var delta = task.Deadline - now; // 剩余精确时间

        switch (kind)
        {
            case "7d":
            {
                if (!r.Enable7d || overdue) return false;
                var days = UrgencyHelper.RemainingCalendarDays(task.Deadline);
                var last = GetLast(task, "7d");
                return days == 7
                       && now.TimeOfDay >= dailyTime
                       && (last == null || last.Value.Date < now.Date);
            }
            case "daily":
            {
                if (!r.Enable3dDaily || overdue) return false;
                var days = UrgencyHelper.RemainingCalendarDays(task.Deadline);
                var last = GetLast(task, "daily");
                return days is >= 0 and <= 3
                       && now.TimeOfDay >= dailyTime
                       && (last == null || last.Value.Date < now.Date);
            }
            case "24h":
            {
                if (!r.Enable24hFrequent || overdue) return false;
                var last = GetLast(task, "24h");
                // 进入 24h 窗口：立即提醒一次；此后每 FreqHours 小时一次
                var freq = TimeSpan.FromHours(Math.Clamp(r.FreqHours, 2, 6));
                return delta <= TimeSpan.FromHours(24)
                       && delta > TimeSpan.Zero
                       && (last == null || now - last.Value >= freq);
            }
            case "overdue":
            {
                if (!overdue) return false;
                var last = GetLast(task, "overdue");
                // 初次逾期：立即提醒；此后（每日提醒时间之后的第一个 tick 起）每天一次
                if (last == null) return true;
                var today = now.Date;
                var lastDay = last.Value.Date;
                return lastDay < today && now.TimeOfDay >= dailyTime;
            }
            default:
                return false;
        }
    }

    private static DateTime? GetLast(DdlTask task, string kind)
        => task.LastNotified.TryGetValue(kind, out var v) ? v : null;

    /// <summary>标记为已发送（App 发送成功后调用，随后落盘）。</summary>
    public static void MarkSent(DdlTask task, string kind, DateTime now)
        => task.LastNotified[kind] = now;
}
