using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DDLSaver.Models;

namespace DDLSaver.Services;

/// <summary>
/// 数据存储：
/// - 位置 %AppData%\DDLSaver\data.json
/// - 每次 Save 前先把 data.json 复制为 data.json.bak
/// - 启动时主文件损坏则自动从 .bak 恢复并提示
/// - 首次运行生成 2 条示例任务
/// </summary>
public class DataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string DataDir { get; }
    public string DataPath { get; }
    public string BackupPath { get; }

    /// <summary>本次启动是否从备份恢复。</summary>
    public bool RecoveredFromBackup { get; private set; }

    /// <summary>本次启动是否为首次运行（无数据文件）。</summary>
    public bool WasFirstRun { get; private set; }

    /// <summary>示例任务是否还存在（用于主窗口提示"示例可删除"）。</summary>
    public bool HasSampleTasks { get; private set; }

    public List<DdlTask> Tasks { get; } = new();
    public List<CompletionRecord> Completions { get; } = new();

    /// <summary>数据变更后触发（浮窗 / 统计 / 提醒引擎刷新用）。</summary>
    public event Action? DataChanged;

    public DataStore()
    {
        DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DDLSaver");
        DataPath = Path.Combine(DataDir, "data.json");
        BackupPath = Path.Combine(DataDir, "data.json.bak");
    }

    /// <summary>启动时加载；失败自动回退备份；仍失败则新建并填充示例。</summary>
    public void Load()
    {
        Directory.CreateDirectory(DataDir);
        HasSampleTasks = false;

        if (!File.Exists(DataPath))
        {
            if (File.Exists(BackupPath))
            {
                // 主文件丢失但备份在 → 从备份恢复
                try
                {
                    File.Copy(BackupPath, DataPath, overwrite: true);
                    RecoveredFromBackup = true;
                }
                catch
                {
                    // 备份复制失败则按首次运行处理
                }
            }

            if (!File.Exists(DataPath))
            {
                WasFirstRun = true;
                SeedSamples();
                Save();
                return;
            }
        }

        if (!TryLoadFrom(DataPath, out var data) && File.Exists(BackupPath))
        {
            // 主文件损坏 → 从备份恢复
            if (TryLoadFrom(BackupPath, out data))
            {
                RecoveredFromBackup = true;
                Save(); // 用恢复后的数据重写主文件
            }
        }

        if (data is null)
        {
            // 两份都不可用：把损坏文件改名留证，重新开始
            TryArchiveCorrupt(DataPath);
            WasFirstRun = true;
            SeedSamples();
            Save();
            return;
        }

        Tasks.Clear();
        Tasks.AddRange(data.Tasks);
        Completions.Clear();
        Completions.AddRange(data.Completions);
        HasSampleTasks = data.Tasks.Any(t => t.Id == SampleTaskAId || t.Id == SampleTaskBId);
    }

    private static readonly Guid SampleTaskAId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SampleTaskBId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>构造 2 条示例任务（帮助理解用法，可在主窗口删除）。</summary>
    private void SeedSamples()
    {
        var now = DateTime.Now;

        Tasks.Add(new DdlTask
        {
            Id = SampleTaskAId,
            Title = "学习通《大学英语》第 3 章视频",
            Category = TaskCategory.OnlineCourse,
            Deadline = now.AddDays(5).Date.AddHours(23).AddMinutes(59),
            TotalUnits = 6,
            DoneUnits = 0,
            UnitName = "节",
            Recurrence = Recurrence.None,
            Note = "示例任务：双击编辑可修改或删除。",
            Reminders = new ReminderPlan(),
            CreatedAt = now,
        });

        Tasks.Add(new DdlTask
        {
            Id = SampleTaskBId,
            Title = "高数期中复习（小组分工）",
            Category = TaskCategory.GroupProject,
            Deadline = now.AddDays(12).Date.AddHours(23).AddMinutes(59),
            SubTasks = new List<SubTask>
            {
                new() { Title = "张三-第一章PPT", IsDone = true, Note = "已发群文件" },
                new() { Title = "李四-第二章讲义", IsDone = false },
                new() { Title = "王五-汇总打印", IsDone = false },
            },
            Note = "示例任务：含子任务分工记录，可用于小组作业。",
            Reminders = new ReminderPlan(),
            CreatedAt = now,
        });

        HasSampleTasks = true;
    }

    private static void TryArchiveCorrupt(string path)
    {
        try
        {
            var corrupt = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(path, corrupt, overwrite: true);
        }
        catch
        {
            // 归档失败不致命，允许覆盖写入
        }
    }

    private static bool TryLoadFrom(string path, out AppData? data)
    {
        data = null;
        try
        {
            var json = File.ReadAllText(path);
            data = JsonSerializer.Deserialize<AppData>(json, JsonOptions);
            // 防御：极端情况下文件合法但无根对象
            if (data is null && json.Trim().Length > 0)
                data = new AppData();
            return data is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>安全写盘：data.json → data.json.bak，再原子替换主文件。</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);

            var snapshot = new AppData
            {
                Version = 1,
                Tasks = Tasks,
                Completions = Completions,
            };
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);

            if (File.Exists(DataPath))
            {
                try
                {
                    File.Copy(DataPath, BackupPath, overwrite: true);
                }
                catch
                {
                    // 备份失败不阻断主写入
                }
            }

            var tmp = DataPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, DataPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 写盘失败不能让应用崩溃（内存数据仍在）
            System.Diagnostics.Debug.WriteLine($"保存数据失败: {ex.Message}");
        }
    }

    /// <summary>数据变更入口：通知监听方并立即落盘。</summary>
    public void NotifyChanged()
    {
        Save();
        DataChanged?.Invoke();
    }

    /// <summary>记录一次完成打卡。</summary>
    public void AddCompletion(DdlTask task)
    {
        Completions.Add(new CompletionRecord
        {
            TaskId = task.Id,
            Title = task.Title,
            CompletedAt = DateTime.Now,
        });
    }
}
