param([string]$Title)
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class FocusWin {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$found = [IntPtr]::Zero
$cb = [FocusWin+EnumProc]{
    param($h, $l)
    $sb = [System.Text.StringBuilder]::new(256)
    [FocusWin]::GetWindowText($h, $sb, 256) | Out-Null
    if ($sb.ToString() -eq $Title -and [FocusWin]::IsWindowVisible($h)) {
        $script:found = $h
        return $false
    }
    return $true
}
[FocusWin]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
if ($found -eq [IntPtr]::Zero) { Write-Output "NOT-FOUND: $Title"; exit }
[FocusWin]::ShowWindow($found, 5) | Out-Null
[FocusWin]::SetForegroundWindow($found) | Out-Null
$r = New-Object FocusWin+RECT
[FocusWin]::GetWindowRect($found, [ref]$r) | Out-Null
Write-Output ("FOCUSED rect=({0},{1})-({2},{3})" -f $r.Left, $r.Top, $r.Right, $r.Bottom)
