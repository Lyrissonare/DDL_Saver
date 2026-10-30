using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DDLSaver.Models;

namespace DDLSaver.Services;

/// <summary>
/// 设置持久化：%AppData%\DDLSaver\settings.json（与 data.json 同目录）。
/// 任意属性变更后调用 Save()。
/// </summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string SettingsPath { get; }

    public AppSettings Settings { get; private set; } = new();

    public SettingsService(string dataDir)
    {
        SettingsPath = Path.Combine(dataDir, "settings.json");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (loaded is not null)
                {
                    Settings = loaded;
                    return;
                }
            }
        }
        catch
        {
            // 设置文件损坏：使用默认设置（不阻塞启动）
        }
        Settings = new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存设置失败: {ex.Message}");
        }
    }
}
