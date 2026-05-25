# Spike: PostMessage WM_COMMAND 1179 (cmd id for "Launch the Manage Add-ins
# dialog") to erwin's XTPMainFrame from EXTERNAL PowerShell process.
#
# PostMessage with scalar args is documented as cross-process safe. No
# pointers, no buffers, no allocation. The classic "PostMessage to other
# window" usage from any tool.
#
# Snapshots windows before/after to identify what (if anything) opened.

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
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public const uint WM_COMMAND = 0x0111;
    // Build wParam = MAKEWPARAM(cmdId, notify=0)
    public static IntPtr MakeWParam(int cmdId, int notify) {
        return new IntPtr((uint)cmdId | (((uint)notify) << 16));
    }
}
'@

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running" -ForegroundColor Red; exit 1 }
$erwinPid = $erwin.Id
$main = $erwin.MainWindowHandle
Write-Host "erwin PID=$erwinPid MainHwnd=0x$('{0:X}' -f $main.ToInt64())" -ForegroundColor Cyan

function Get-ErwinWindows {
    $set = New-Object System.Collections.Generic.HashSet[long]
    $proc = [W+EnumWindowsProc] {
        param([IntPtr]$h, [IntPtr]$lp)
        $procId = 0
        [void][W]::GetWindowThreadProcessId($h, [ref]$procId)
        if ($procId -eq $erwinPid) {
            [void]$set.Add($h.ToInt64())
            [void][W]::EnumChildWindows($h, $proc, [IntPtr]::Zero)
        }
        return $true
    }
    [void][W]::EnumWindows($proc, [IntPtr]::Zero)
    return $set
}

function Describe-Hwnd {
    param([long]$hl)
    $h = [IntPtr]$hl
    $c = New-Object System.Text.StringBuilder 256
    [void][W]::GetClassName($h, $c, 256)
    $t = New-Object System.Text.StringBuilder 256
    [void][W]::GetWindowText($h, $t, 256)
    $v = [W]::IsWindowVisible($h)
    return [pscustomobject]@{
        Hwnd = '0x{0:X}' -f $hl
        Class = $c.ToString()
        Title = $t.ToString()
        Vis = $v
    }
}

Write-Host "Capturing baseline windows..." -ForegroundColor Gray
$baseline = Get-ErwinWindows
Write-Host "Baseline: $($baseline.Count) windows" -ForegroundColor Gray

$cmdId = 1179
Write-Host ""
Write-Host "Posting WM_COMMAND id=$cmdId (0x49B) to XTPMainFrame..." -ForegroundColor Yellow
$wParam = [W]::MakeWParam($cmdId, 0)
$ok = [W]::PostMessageW($main, [W]::WM_COMMAND, $wParam, [IntPtr]::Zero)
if (-not $ok) {
    $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Write-Host "PostMessage failed err=$err" -ForegroundColor Red
    exit 2
}
Write-Host "PostMessage returned OK" -ForegroundColor Green

Write-Host ""
Write-Host "Polling for new windows (3s)..." -ForegroundColor Gray
$deadline = (Get-Date).AddSeconds(3)
$newOnes = @()
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 200
    $now = Get-ErwinWindows
    $diff = @($now | Where-Object { -not $baseline.Contains($_) })
    if ($diff.Count -gt 0) {
        $newOnes = $diff
        # don't break - collect more in case popup-then-dialog cascade
    }
}

Write-Host ""
Write-Host "=== RESULT ===" -ForegroundColor Cyan
if ($newOnes.Count -eq 0) {
    Write-Host "No new windows appeared in 3s. PostMessage probably went into void." -ForegroundColor Yellow
    Write-Host "Possible reasons:" -ForegroundColor Gray
    Write-Host "  - erwin not in foreground / inactive (some commands check focus)" -ForegroundColor Gray
    Write-Host "  - cmd id 1179 is enabled only in certain contexts" -ForegroundColor Gray
    Write-Host "  - we need to post to a different window than MainWindowHandle" -ForegroundColor Gray
    exit 3
}
Write-Host "$($newOnes.Count) new windows appeared:" -ForegroundColor Green
foreach ($h in $newOnes) {
    $info = Describe-Hwnd -hl $h
    $color = if ($info.Vis) { 'Green' } else { 'DarkGray' }
    Write-Host ("  {0,-10} cls='{1,-25}' vis={2,-5} title='{3}'" -f $info.Hwnd, $info.Class, $info.Vis, $info.Title) -ForegroundColor $color
}

# Highlight standout window types
$standard = @($newOnes | ForEach-Object { Describe-Hwnd -hl $_ } | Where-Object { $_.Class -eq '#32770' -and $_.Vis })
$popup    = @($newOnes | ForEach-Object { Describe-Hwnd -hl $_ } | Where-Object { $_.Class -match 'Popup|#32768' })
if ($standard.Count -gt 0) {
    Write-Host ""
    Write-Host "STANDARD #32770 DIALOG opened - UIA-friendly!" -ForegroundColor Green
    $standard | Format-Table -AutoSize | Out-String | Write-Host
}
if ($popup.Count -gt 0) {
    Write-Host ""
    Write-Host "Popup-like window opened - menu navigation path." -ForegroundColor Cyan
    $popup | Format-Table -AutoSize | Out-String | Write-Host
}
