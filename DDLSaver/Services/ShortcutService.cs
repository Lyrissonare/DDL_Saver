using System.IO;
using System.Runtime.InteropServices;

namespace DDLSaver.Services;

/// <summary>
/// 未打包桌面应用 Toast 的 AUMID 注册（规格 3.3 硬性要求）：
/// 在开始菜单创建带 AppUserModelID 的快捷方式，Windows 才会展示该 AUMID 的 Toast，
/// 否则会被系统静默丢弃。本服务也负责清理指向旧位置的失效快捷方式。
/// </summary>
internal static class ShortcutService
{
    // ---------- COM 互操作 ----------

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCOM
    {
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")] // IID_IPersistFile
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig] void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        [PreserveSig] void Load(string pszFileName, uint dwMode);
        [PreserveSig] void Save(string pszFileName, bool fRemember);
        [PreserveSig] void SaveCompleted(string pszFileName);
        [PreserveSig] void GetCurFile(out string ppszFileName);
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")] // IID_IShellLinkW
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        // 特别注意：COM 接口方法必须按 vtable 顺序声明
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, ref WIN32_FIND_DATAW pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    /// <summary>PROPVARIANT（仅需 VT_LPWSTR 的紧凑布局，x64 下与原生 16 字节一致）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
    }

    private const ushort VT_LPWSTR = 31;

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    /// <summary>PKEY_AppUserModel_ID：{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5</summary>
    private static readonly PROPERTYKEY PkeyAppUserModelId =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    // ---------- 对外逻辑 ----------

    /// <summary>快捷方式文件名。</summary>
    public const string ShortcutFileName = "DDL拯救器.lnk";

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        ShortcutFileName);

    /// <summary>
    /// 计算与 CommunityToolkit 运行时一致的 AUMID（规则与其 Win32AppInfo 相同）：
    /// 进程 AppUserModelID = exe 路径（反斜杠转正斜杠），超长则 SHA1 十六进制。
    /// </summary>
    public static string ComputeAumid()
    {
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "DDLSaver.exe";
        var aumid = exe.Replace('\\', '/');
        if (aumid.Length > 129)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            aumid = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(aumid)));
        }
        return aumid;
    }

    /// <summary>开始菜单快捷方式是否已正确存在（指针匹配当前 exe 与 AUMID）。</summary>
    public static bool IsRegistered()
    {
        var lnk = ShortcutPath;
        if (!File.Exists(lnk))
            return false;

        try
        {
            var link = (IShellLinkW)new ShellLinkCOM();
            var file = (IPersistFile)link;
            file.Load(lnk, 0);

            var pathBuf = new System.Text.StringBuilder(260);
            var fd = new WIN32_FIND_DATAW();
            link.GetPath(pathBuf, 260, ref fd, 0);

            var store = (IPropertyStore)link;
            var key = PkeyAppUserModelId;
            store.GetValue(ref key, out var pv);
            var aumid = pv.p == IntPtr.Zero ? null : Marshal.PtrToStringUni(pv.p);
            if (pv.p != IntPtr.Zero) Marshal.FreeCoTaskMem(pv.p);

            return string.Equals(pathBuf.ToString().TrimStart('"'), CurrentExePath(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(aumid, ComputeAumid(), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string CurrentExePath()
        => System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";

    /// <summary>创建 / 更新带 AUMID 的开始菜单快捷方式。</summary>
    public static void Register()
    {
        var exe = CurrentExePath();
        if (string.IsNullOrEmpty(exe))
            return;

        try
        {
            RegisterCore(exe);
        }
        catch (Exception ex)
        {
            // 注册失败不致命：Toast 失败时会走应用内弹窗兜底；
            // 把原因记录到本地日志便于排查。
            TryWriteErrorLog(ex);
        }
    }

    private static void TryWriteErrorLog(Exception ex)
    {
        try
        {
            var log = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DDLSaver", "shortcut-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n");
        }
        catch
        {
            // 日志失败可忽略
        }
    }

    private static void RegisterCore(string exe)
    {
        // 清理指向旧位置的残留快捷方式
        var dir = Path.GetDirectoryName(ShortcutPath)!;
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.GetFiles(dir, "*.lnk"))
            {
                try
                {
                    var link = (IShellLinkW)new ShellLinkCOM();
                    var file = (IPersistFile)link;
                    file.Load(f, 0);
                    var buf = new System.Text.StringBuilder(260);
                    var fd = new WIN32_FIND_DATAW();
                    link.GetPath(buf, 260, ref fd, 0);
                    if (!string.Equals(buf.ToString().TrimStart('"'), exe, StringComparison.OrdinalIgnoreCase))
                        File.Delete(f);
                }
                catch
                {
                    // 单个快捷方式解析失败可忽略
                }
            }
        }

        var link2 = (IShellLinkW)new ShellLinkCOM();
        link2.SetPath(exe);
        link2.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
        link2.SetDescription("DDL拯救器 - 启动");
        link2.SetIconLocation(exe, 0);

        var store = (IPropertyStore)link2;
        var pv = new PROPVARIANT { vt = VT_LPWSTR, p = IntPtr.Zero };
        pv.p = Marshal.StringToCoTaskMemUni(ComputeAumid());
        try
        {
            var key = PkeyAppUserModelId;
            store.SetValue(ref key, ref pv);
            store.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(pv.p);
        }

        var pers = (IPersistFile)link2;
        pers.Save(ShortcutPath, true);
    }

    /// <summary>注销（更新卸载场景使用，保持开始菜单干净）。</summary>
    public static void Unregister()
    {
        try
        {
            if (File.Exists(ShortcutPath))
                File.Delete(ShortcutPath);
        }
        catch
        {
            // 忽略
        }
    }
}
