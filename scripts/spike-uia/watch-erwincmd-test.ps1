# Spike: watch erwin's window list while user double-clicks a .erwincmd
# test file. Reports any new windows / dialogs that appear, so we can
# tell what format erwin expects.
#
# Polls every 200ms for 60s. Snapshots erwin process list too in case
# a fresh instance is spawned.

$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
'@

$mySession = (Get-Process -Id $PID).SessionId

function Get-ErwinList {
    return Get-Process erwin -ErrorAction SilentlyContinue |
           Where-Object { $_.SessionId -eq $mySession } |
           Select-Object Id, MainWindowTitle, StartTime
}

function Get-ErwinDialogs([int]$erwinPid) {
    $list = New-Object System.Collections.ArrayList
    $proc = [W+EnumWindowsProc] {
        param([IntPtr]$h, [IntPtr]$lp)
        $procId = 0
        [void][W]::GetWindowThreadProcessId($h, [ref]$procId)
        if ($procId -eq $erwinPid -and [W]::IsWindowVisible($h)) {
            $sbC = New-Object System.Text.StringBuilder 64
            [void][W]::GetClassName($h, $sbC, 64)
            $cls = $sbC.ToString()
            $sbT = New-Object System.Text.StringBuilder 256
            [void][W]::GetWindowText($h, $sbT, 256)
            $title = $sbT.ToString()
            # Focus on dialogs/popups
            if ($cls -match '#32770|MessageBox|XTPPopup' -or $title -match 'Error|erwin' ) {
                [void]$list.Add(@{ Hwnd = '0x{0:X}' -f $h.ToInt64(); Class = $cls; Title = $title })
            }
        }
        return $true
    }
    [void][W]::EnumWindows($proc, [IntPtr]::Zero)
    return $list
}

$baselineErwin = Get-ErwinList
Write-Host "Baseline erwin instances:" -ForegroundColor Cyan
$baselineErwin | Format-Table -AutoSize | Out-String | Write-Host

$baselineDialogs = @()
foreach ($e in $baselineErwin) {
    $baselineDialogs += Get-ErwinDialogs -erwinPid $e.Id | ForEach-Object { $_.Hwnd }
}
$baselineDialogs = [System.Collections.Generic.HashSet[string]]$baselineDialogs

Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host " READY. Double-click ONE of these test files now:" -ForegroundColor Cyan
Write-Host "   C:\tmp\erwincmd-test\test1-empty.erwincmd" -ForegroundColor White
Write-Host "   C:\tmp\erwincmd-test\test2-plaintext.erwincmd" -ForegroundColor White
Write-Host "   C:\tmp\erwincmd-test\test3-xml.erwincmd" -ForegroundColor White
Write-Host "   C:\tmp\erwincmd-test\test4-vbs.erwincmd" -ForegroundColor White
Write-Host "" -ForegroundColor Cyan
Write-Host " Watch for: new erwin process, new dialogs, error messages." -ForegroundColor Cyan
Write-Host " Polling 60s." -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

$deadline = (Get-Date).AddSeconds(60)
$lastErwinCount = $baselineErwin.Count
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $now = Get-ErwinList
    if ($now.Count -ne $lastErwinCount) {
        Write-Host "ERWIN INSTANCE CHANGE: $lastErwinCount -> $($now.Count)" -ForegroundColor Yellow
        $now | Format-Table -AutoSize | Out-String | Write-Host
        $lastErwinCount = $now.Count
    }
    foreach ($e in $now) {
        $dialogs = Get-ErwinDialogs -erwinPid $e.Id
        foreach ($d in $dialogs) {
            if (-not $baselineDialogs.Contains($d.Hwnd)) {
                [void]$baselineDialogs.Add($d.Hwnd)
                Write-Host "NEW WINDOW in PID=$($e.Id): hwnd=$($d.Hwnd) cls='$($d.Class)' title='$($d.Title)'" -ForegroundColor Green
            }
        }
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Cyan
