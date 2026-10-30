param()
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinEnum3 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$list = [System.Collections.ArrayList]::new()
$cb = [WinEnum3+EnumProc]{
    param($h, $l)
    $sb = [System.Text.StringBuilder]::new(256)
    [WinEnum3]::GetWindowText($h, $sb, 256) | Out-Null
    $cls = [System.Text.StringBuilder]::new(64)
    [WinEnum3]::GetClassName($h, $cls, 64) | Out-Null
    $vis = [WinEnum3]::IsWindowVisible($h)
    if ($cls.ToString() -like "*DDLSaver*") {
        $r = New-Object WinEnum3+RECT
        [WinEnum3]::GetWindowRect($h, [ref]$r) | Out-Null
        $list.Add(("vis={0} title=<{1}> rect=({2},{3})-({4},{5})" -f $vis, $sb, $r.Left, $r.Top, $r.Right, $r.Bottom)) | Out-Null
    }
    return $true
}
[WinEnum3]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
if ($list.Count -eq 0) { "NO-DDLSaver-WINDOWS" } else { $list | ForEach-Object { $_ } }
