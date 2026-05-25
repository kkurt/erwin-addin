# Spike: detect any new popup that opens in erwin and MSAA-enumerate it.
#
# Workflow:
#   1. Run this script.
#   2. Script enumerates current windows in erwin -> "baseline".
#   3. Script polls every 250ms; any new visible window with class matching
#      popup/menu classes is MSAA-enumerated, items logged to stdout.
#   4. User manually clicks "Add-Ins" in erwin's ribbon -> popup opens ->
#      script captures it.
#   5. Script exits after 60s OR Ctrl+C.
#
# Read-only. Same MSAA API as enumerate-msaa.ps1 - zero pointer marshalling.

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

    [DllImport("oleacc.dll", PreserveSig=true)]
    public static extern int AccessibleObjectFromWindow(
        IntPtr hwnd, uint dwObjectID, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppvObject);

    public const uint OBJID_CLIENT = 0xFFFFFFFC;
    public static Guid IID_IAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
}
'@

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running" -ForegroundColor Red; exit 1 }

$erwinPid = $erwin.Id
Write-Host "Watching erwin PID=$erwinPid" -ForegroundColor Cyan

# Class names we treat as "interesting popup" candidates.
# XTPPopupBar = XTP's popup menu/submenu. #32768 = classic Win32 popup menu.
# XTPMenuBar/XTPCommandBars also relevant. ToolbarWindow32 sometimes hosts
# floating popup menus.
$popupClasses = @('XTPPopupBar', '#32768', 'XTPMenuBar', 'XTPCommandBars', 'XTPToolBar')

function Get-AllErwinWindows {
    $found = New-Object System.Collections.Generic.HashSet[long]
    $proc = [W32+EnumWindowsProc] {
        param([IntPtr]$h, [IntPtr]$lp)
        $procId = 0
        [void][W32]::GetWindowThreadProcessId($h, [ref]$procId)
        if ($procId -eq $erwinPid) {
            [void]$found.Add($h.ToInt64())
            [void][W32]::EnumChildWindows($h, $proc, [IntPtr]::Zero)
        }
        return $true
    }
    [void][W32]::EnumWindows($proc, [IntPtr]::Zero)

    # Also recurse from any window we already know (the closure doesn't
    # recurse on its own from EnumChildWindows; EnumChildWindows only walks
    # direct children, not grandchildren).
    $stack = New-Object System.Collections.Stack
    foreach ($h in @($found)) { $stack.Push([IntPtr]$h) }
    while ($stack.Count -gt 0) {
        $parent = $stack.Pop()
        $childProc = [W32+EnumWindowsProc] {
            param([IntPtr]$h, [IntPtr]$lp)
            if ($found.Add($h.ToInt64())) { $stack.Push($h) }
            return $true
        }
        [void][W32]::EnumChildWindows($parent, $childProc, [IntPtr]::Zero)
    }
    return $found
}

function Describe-Hwnd {
    param([IntPtr]$h)
    $sbC = New-Object System.Text.StringBuilder 256
    [void][W32]::GetClassName($h, $sbC, 256)
    $sbT = New-Object System.Text.StringBuilder 256
    [void][W32]::GetWindowText($h, $sbT, 256)
    $cls = $sbC.ToString()
    $title = $sbT.ToString()
    $vis = [W32]::IsWindowVisible($h)
    return @{ Hwnd = $h; Class = $cls; Title = $title; Visible = $vis }
}

function Probe-MsaaItems {
    param([IntPtr]$hwnd, [string]$label)
    $iid = [W32]::IID_IAccessible
    $obj = $null
    $hr = [W32]::AccessibleObjectFromWindow($hwnd, [W32]::OBJID_CLIENT, [ref]$iid, [ref]$obj)
    if ($hr -ne 0 -or $null -eq $obj) {
        Write-Host "    [$label] no IAccessible (hr=0x$('{0:X}' -f $hr))" -ForegroundColor DarkGray
        return
    }
    $count = -1
    try { $count = $obj.accChildCount } catch { Write-Host "    [$label] accChildCount threw" -ForegroundColor DarkGray; return }
    Write-Host "    [$label] $count children:" -ForegroundColor Yellow
    if ($count -lt 1 -or $count -gt 2000) { return }
    for ($i = 1; $i -le $count; $i++) {
        $n = ''; $r = ''; $a = ''; $d = ''
        try { $n = [string]$obj.accName($i) } catch {}
        try { $r = [string]$obj.accRole($i) } catch {}
        try { $a = [string]$obj.accDefaultAction($i) } catch {}
        try { $d = [string]$obj.accDescription($i) } catch {}
        if ([string]::IsNullOrWhiteSpace($n) -and [string]::IsNullOrWhiteSpace($d) -and [string]::IsNullOrWhiteSpace($a)) { continue }
        $isMatch = ($n -match 'Elite|EliteSoft|Erwin\s*Addin|Add\-?In' -or $d -match 'Elite|EliteSoft|Erwin\s*Addin|Add\-?In')
        $color = if ($isMatch) { 'Green' } else { 'Gray' }
        Write-Host ("      [$i] role=$r name='$n' action='$a' desc='$d'") -ForegroundColor $color
        if ($isMatch) { Write-Host "         >>> MATCH" -ForegroundColor Green }
    }
    try { [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($obj) | Out-Null } catch {}
}

Write-Host "Capturing baseline window set..." -ForegroundColor Gray
$baseline = Get-AllErwinWindows
Write-Host "Baseline: $($baseline.Count) windows in erwin process" -ForegroundColor Gray
Write-Host ""
Write-Host "===================================================================" -ForegroundColor Cyan
Write-Host " READY. NOW click 'Add-Ins' in erwin's ribbon." -ForegroundColor Cyan
Write-Host " Script polls for new windows. Press Ctrl+C when done." -ForegroundColor Cyan
Write-Host "===================================================================" -ForegroundColor Cyan
Write-Host ""

$start = Get-Date
$probed = New-Object System.Collections.Generic.HashSet[long]
while (((Get-Date) - $start).TotalSeconds -lt 60) {
    Start-Sleep -Milliseconds 250
    $now = Get-AllErwinWindows
    $newOnes = @($now | Where-Object { -not $baseline.Contains($_) })
    if ($newOnes.Count -eq 0) { continue }

    foreach ($hLong in $newOnes) {
        if ($probed.Contains($hLong)) { continue }
        [void]$probed.Add($hLong)
        $h = [IntPtr]$hLong
        $info = Describe-Hwnd -h $h
        if (-not $info.Visible) { continue }
        if ($popupClasses -notcontains $info.Class -and $info.Class -notmatch 'XTP') { continue }
        $label = "0x$('{0:X}' -f $hLong) cls='$($info.Class)' title='$($info.Title)'"
        Write-Host "NEW POPUP: $label" -ForegroundColor Cyan
        try {
            Probe-MsaaItems -hwnd $h -label $info.Class
        } catch {
            Write-Host "    probe threw: $($_.Exception.Message)" -ForegroundColor Red
        }
        Write-Host ""
    }
    # Add the now-seen ones to baseline so we don't re-process if they
    # stay open while user navigates further.
    foreach ($n in $newOnes) { [void]$baseline.Add($n) }
}

Write-Host "60s timeout reached. Exiting." -ForegroundColor Gray
