# Read-only probe: which windows in erwin actually respond to TB_BUTTONCOUNT?
# Just classify, no allocation/read. Pure scalar message - cannot crash.

$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W32 {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public const uint TB_BUTTONCOUNT = 0x0418;
}
'@

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running"; exit 1 }
$erwinPid = $erwin.Id

$candidates = New-Object System.Collections.ArrayList
$enum = [W32+EnumWindowsProc] {
    param([IntPtr]$h, [IntPtr]$lp)
    $procId = 0
    [void][W32]::GetWindowThreadProcessId($h, [ref]$procId)
    if ($procId -ne $erwinPid) { return $true }
    $sb = New-Object System.Text.StringBuilder 256
    [void][W32]::GetClassName($h, $sb, 256)
    $cls = $sb.ToString()
    if ($cls -match 'XTPToolBar|XTPRibbon|XTPMenuBar|XTPCommandBars|XTPPopupBar|ToolbarWindow32') {
        $sbT = New-Object System.Text.StringBuilder 256
        [void][W32]::GetWindowText($h, $sbT, 256)
        $vis = [W32]::IsWindowVisible($h)
        [void]$candidates.Add(@{ Hwnd = $h; Class = $cls; Title = $sbT.ToString(); Visible = $vis })
    }
    [void][W32]::EnumChildWindows($h, $enum, [IntPtr]::Zero)
    return $true
}
[void][W32]::EnumWindows($enum, [IntPtr]::Zero)

$rows = New-Object System.Collections.ArrayList
foreach ($c in $candidates) {
    $cnt = -1
    try {
        $cnt = [W32]::SendMessageW($c.Hwnd, [W32]::TB_BUTTONCOUNT, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32()
    } catch { }
    [void]$rows.Add([pscustomobject]@{
        Hwnd = '0x{0:X}' -f $c.Hwnd.ToInt64()
        Class = $c.Class
        Title = $c.Title
        Vis = $c.Visible
        TBCount = $cnt
    })
}

Write-Host "Toolbar-like windows in erwin and their TB_BUTTONCOUNT response:" -ForegroundColor Cyan
$rows | Sort-Object TBCount -Descending | Format-Table -AutoSize | Out-String | Write-Host

$responding = @($rows | Where-Object { $_.TBCount -gt 0 })
Write-Host ""
Write-Host "Toolbars that respond to standard TB_*: $($responding.Count) of $($rows.Count)" -ForegroundColor $(if ($responding.Count -gt 0) { 'Green' } else { 'Yellow' })
