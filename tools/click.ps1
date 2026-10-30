param([int]$X, [int]$Y)
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Clicker {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
"@
[Clicker]::SetCursorPos($X, $Y) | Out-Null
Start-Sleep -Milliseconds 150
[Clicker]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 60
[Clicker]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
Write-Output "CLICKED $X,$Y"
