# Spike: close any open Add-In Manager dialog, then test multiple candidate
# WM_COMMAND IDs to discover which one opens the Add-Ins popup (the one
# with "Manage..." + addin items).

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
    public const uint WM_CLOSE   = 0x0010;
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

function Find-Dialog-ByTitle {
    param([string]$pattern)
    $hit = [IntPtr]::Zero
    $proc = [W+EnumWindowsProc] {
        param([IntPtr]$h, [IntPtr]$lp)
        $procId = 0
        [void][W]::GetWindowThreadProcessId($h, [ref]$procId)
        if ($procId -eq $erwinPid -and [W]::IsWindowVisible($h)) {
            $sbC = New-Object System.Text.StringBuilder 64
            [void][W]::GetClassName($h, $sbC, 64)
            if ($sbC.ToString() -eq '#32770') {
                $sbT = New-Object System.Text.StringBuilder 256
                [void][W]::GetWindowText($h, $sbT, 256)
                if ($sbT.ToString() -match $pattern) {
                    $script:hit = $h
                    return $false
                }
            }
        }
        return $true
    }
    [void][W]::EnumWindows($proc, [IntPtr]::Zero)
    return $hit
}

# Step 1: close any open Add-In Manager dialog
$mgr = Find-Dialog-ByTitle 'Add-In Manager'
if ($mgr -ne [IntPtr]::Zero) {
    Write-Host "Closing existing Add-In Manager (hwnd=0x$('{0:X}' -f $mgr.ToInt64()))..." -ForegroundColor Yellow
    [void][W]::PostMessageW($mgr, [W]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 800
}

# Step 2: try each candidate ID, record what windows appear
$candidates = @(
    @{ Id = 10188; Note = '"Add-Ins Options..." string-table entry' }
)

foreach ($cand in $candidates) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Testing WM_COMMAND id=$($cand.Id) ($($cand.Note))" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    $baseline = Get-ErwinWindows
    $wParam = [W]::MakeWParam($cand.Id, 0)
    $ok = [W]::PostMessageW($main, [W]::WM_COMMAND, $wParam, [IntPtr]::Zero)
    if (-not $ok) {
        Write-Host "  PostMessage failed" -ForegroundColor Red
        continue
    }

    Start-Sleep -Milliseconds 1500
    $now = Get-ErwinWindows
    $diff = @($now | Where-Object { -not $baseline.Contains($_) })
    if ($diff.Count -eq 0) {
        Write-Host "  no new windows" -ForegroundColor Yellow
        continue
    }
    Write-Host "  $($diff.Count) new windows:" -ForegroundColor Green
    foreach ($hl in $diff) {
        $h = [IntPtr]$hl
        $sbC = New-Object System.Text.StringBuilder 256
        [void][W]::GetClassName($h, $sbC, 256)
        $sbT = New-Object System.Text.StringBuilder 256
        [void][W]::GetWindowText($h, $sbT, 256)
        $vis = [W]::IsWindowVisible($h)
        $cls = $sbC.ToString()
        $title = $sbT.ToString()
        # Highlight popups + dialogs
        $color = 'Gray'
        if ($cls -match 'Popup|#32768') { $color = 'Magenta' }
        elseif ($cls -eq '#32770') { $color = 'Green' }
        elseif ($vis) { $color = 'DarkCyan' }
        Write-Host ("    0x{0,-8:X} cls='{1,-22}' vis={2,-5} title='{3}'" -f $hl, $cls, $vis, $title) -ForegroundColor $color
    }

    # Close any dialog that opened, for clean next iteration
    $mgr2 = Find-Dialog-ByTitle '.+'
    if ($mgr2 -ne [IntPtr]::Zero) {
        Write-Host "  closing dialog 0x$('{0:X}' -f $mgr2.ToInt64()) for cleanup" -ForegroundColor DarkGray
        [void][W]::PostMessageW($mgr2, [W]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 500
    }
}
