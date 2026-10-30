using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DDLSaver.Models;

namespace DDLSaver.Services;

/// <summary>
/// 导入导出（规格 3.7）：
/// - 导出 JSON / 导入 JSON（合并 = 为导入任务分配新 Id 并追加；替换 = 清空后整体导入）
/// - 导出 CSV（UTF-8 BOM，Excel 友好；列：标题/分类/截止/进度/状态）
/// - 导入 .ics（VEVENT 的 SUMMARY / DTEND，批量创建，评分加分项）
/// </summary>
public static class ImportExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public const string CategoryNamesPrefix = ""; // 分类中文名由 UI 层提供

    // ---------- JSON ----------

    public static void ExportJson(string path, DataStore store)
    {
        var data = new AppData
        {
            Version = 1,
            Tasks = store.Tasks,
            Completions = store.Completions,
        };
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    public static (int Tasks, int Completions) ImportJson(string path, DataStore store, bool replace)
    {
        var data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException("文件内容不是有效的 DDL 数据（空根对象）。");

        int importedTasks;
        if (replace)
        {
            store.Tasks.Clear();
            store.Completions.Clear();
            store.Tasks.AddRange(data.Tasks);
            store.Completions.AddRange(data.Completions);
            importedTasks = data.Tasks.Count;
        }
        else
        {
            // 合并：分配新 Id，避免与现有任务冲突（保留全部字段）
            var existingIds = store.Tasks.Select(t => t.Id).ToHashSet();
            importedTasks = 0;
            foreach (var t in data.Tasks)
            {
                var clone = t.CloneBase();
                clone.Id = Guid.NewGuid();
                clone.CreatedAt = DateTime.Now;
                store.Tasks.Add(clone);
                importedTasks++;
            }
            store.Completions.AddRange(data.Completions);
        }

        store.NotifyChanged();
        return (importedTasks, data.Completions.Count);
    }

    // ---------- CSV ----------

    public static void ExportCsv(string path, DataStore store)
    {
        var sb = new StringBuilder();
        sb.AppendLine("标题,分类,截止时间,进度,状态");
        foreach (var t in store.Tasks)
        {
            var deadline = t.Deadline.ToString("yyyy-MM-dd HH:mm");
            var progress = t.HasProgress
                ? $"{t.DoneUnits}/{t.TotalUnits} {t.UnitName}".Trim()
                : "";
            var status = t.IsCompleted ? $"已完成 {t.CompletedAt:MM-dd HH:mm}" : "未完成";
            sb.AppendLine(string.Join(",", new[]
            {
                CsvEscape(t.Title),
                CsvEscape(CategoryName(t.Category)),
                deadline,
                CsvEscape(progress),
                CsvEscape(status),
            }));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM 便于 Excel 识别中文
    }

    private static string CsvEscape(string s)
    {
        if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    public static string CategoryName(TaskCategory c) => c switch
    {
        TaskCategory.OnlineCourse => "网课",
        TaskCategory.GroupProject => "小组作业",
        TaskCategory.Exam => "考试",
        TaskCategory.Homework => "课程作业",
        _ => "杂事",
    };

    public static TaskCategory ParseCategory(string name) => name switch
    {
        "网课" => TaskCategory.OnlineCourse,
        "小组作业" => TaskCategory.GroupProject,
        "考试" => TaskCategory.Exam,
        "课程作业" => TaskCategory.Homework,
        _ => TaskCategory.Misc,
    };

    // ---------- ICS ----------

    /// <summary>
    /// 解析 .ics：提取所有 VEVENT 的 SUMMARY / DTEND（无 DTEND 用 DTSTART），
    /// 日期（YYYYMMDD）默认 23:59，带时间按原时刻；Z 结尾按 UTC 转本地。
    /// </summary>
    public static List<DdlTask> ParseIcs(string path)
    {
        var rows = IcsUnfoldAndSplit(File.ReadAllLines(path));
        var tasks = new List<DdlTask>();
        DdlTask? current = null;

        foreach (var row in rows)
        {
            var colon = row.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = row[..colon].ToUpperInvariant();
            var value = row[(colon + 1)..];

            switch (key)
            {
                case "BEGIN" when value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase):
                    current = new DdlTask { Category = TaskCategory.Misc };
                    break;

                case "END" when value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase):
                    if (current is { Title.Length: > 0 })
                    {
                        if (current.Deadline == default)
                            current.Deadline = DateTime.Today.AddHours(23).AddMinutes(59);
                        tasks.Add(current);
                    }
                    current = null;
                    break;

                case "SUMMARY":
                    if (current is not null)
                        current.Title = value.Trim();
                    break;

                case "DTEND":
                case "DUE":
                    if (current is not null && TryParseIcsDate(value, out var dt))
                        current.Deadline = dt;
                    break;

                case "DTSTART":
                    if (current is not null && current.Deadline == default && TryParseIcsDate(value, out var ds))
                        current.Deadline = ds;
                    break;
            }
        }
        return tasks;
    }

    /// <summary>ICS 折叠行展开（行首空格/制表符续行）。</summary>
    private static List<string> IcsUnfoldAndSplit(string[] lines)
    {
        var rows = new List<string>();
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            if (line[0] is ' ' or '\t')
            {
                sb.Append(line[1..]);
                continue;
            }
            if (sb.Length > 0)
            {
                rows.Add(sb.ToString());
                sb.Clear();
            }
            sb.Append(line);
        }
        if (sb.Length > 0)
            rows.Add(sb.ToString());
        return rows;
    }

    private static bool TryParseIcsDate(string value, out DateTime result)
    {
        result = default;
        var s = value.Trim();
        if (s.Length == 0) return false;

        var utc = s.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        var body = utc ? s[..^1] : s;

        if (body.Length == 8 && body.All(char.IsDigit))
        {
            // 纯日期 → 当天 23:59
            if (!DateTime.TryParseExact(body, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d))
                return false;
            result = d.AddHours(23).AddMinutes(59);
            return true;
        }

        if (body.Length >= 15 && body.Length <= 20)
        {
            var fmt = body.Contains('T') ? "yyyyMMdd'T'HHmmss" : "yyyyMMddHHmmss";
            if (!DateTime.TryParseExact(body, fmt,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return false;
            result = utc ? dt.ToLocalTime() : dt;
            return true;
        }

        return false;
    }
}
