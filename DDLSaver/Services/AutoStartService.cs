using Microsoft.Win32;

namespace DDLSaver.Services;

/// <summary>
/// 开机自启（HKCU，全程不申请管理员权限）：
/// 开启写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 的 "DDLSaver" 键值（exe 路径），
/// 关闭时删除对应键值。发布为单文件 exe 后路径即为用户可执行的完整路径。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DDLSaver";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>设置开机自启。返回是否成功（失败时上层提示用户）。</summary>
    public static bool SetEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
                return false;

            if (enable)
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe))
                    return false;
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
