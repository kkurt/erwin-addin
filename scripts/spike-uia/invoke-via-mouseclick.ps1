# Spike: invoke addin via MSAA accLocation + synthetic mouse click.
#
# Why this is AV-safe:
#   - GetCursorPos / SetCursorPos / mouse_event are standard input APIs.
#   - Every UI automation tool (AutoHotkey, AutoIt, UI Spy, WinAppDriver)
#     uses this exact pattern.
#   - Zero process injection, zero VirtualAllocEx, zero CreateRemoteThread.
#
# Steps:
#   1. Find Add-Ins in ribbon via MSAA (same as invoke-via-msaa.ps1).
#   2. accLocation(idx) -> screen rect for the button.
#   3. Save cursor, focus erwin, click center of rect.
#   4. Poll for XTPPopupBar.
#   5. Find Elite Soft Erwin Addin in popup, click its center.
#   6. Restore cursor.
#   7. Verify Execute() side effects.

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
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("oleacc.dll", PreserveSig=true)]
    public static extern int AccessibleObjectFromWindow(
        IntPtr hwnd, uint dwObjectID, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppvObject);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }

    public const uint OBJID_CLIENT = 0xFFFFFFFC;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP   = 0x0004;
    public const int  SW_RESTORE = 9;

    public static Guid IID_IAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
}
'@

# ---------------- helpers -----------------
$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running" -ForegroundColor Red; exit 1 }
$erwinPid = $erwin.Id
Write-Host "erwin PID=$erwinPid" -ForegroundColor Cyan

$addinRoot = Join-Path $env:LOCALAPPDATA 'EliteSoft'
function Snapshot-State {
    $h = @{}
    if (Test-Path $addinRoot) {
        Get-ChildItem $addinRoot -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            $h[$_.FullName] = @{ Length = $_.Length; LWT = $_.LastWriteTime }
        }
    }
    return $h
}
$before = Snapshot-State

function Get-ErwinVisibleXtpToolbars {
    $found = New-Object System.Collections.ArrayList
    $proc = [W32+EnumWindowsProc] {
        param([IntPtr]$h, [IntPtr]$lp)
        $procId = 0
        [void][W32]::GetWindowThreadProcessId($h, [ref]$procId)
        if ($procId -eq $erwinPid -and [W32]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][W32]::GetClassName($h, $sb, 256)
            if ($sb.ToString() -eq 'XTPToolBar') {
                $tb = New-Object System.Text.StringBuilder 256
                [void][W32]::GetWindowText($h, $tb, 256)
                [void]$found.Add(@{ Hwnd = $h; Title = $tb.ToString() })
            }
            [void][W32]::EnumChildWindows($h, $proc, [IntPtr]::Zero)
        }
        return $true
    }
    [void][W32]::EnumWindows($proc, [IntPtr]::Zero)
    return $found
}

function Get-Acc {
    param([IntPtr]$hwnd)
    $iid = [W32]::IID_IAccessible
    $obj = $null
    $hr = [W32]::AccessibleObjectFromWindow($hwnd, [W32]::OBJID_CLIENT, [ref]$iid, [ref]$obj)
    if ($hr -ne 0 -or $null -eq $obj) { return $null }
    return $obj
}

function Find-AccChild {
    param($acc, [string]$pattern)
    $count = -1
    try { $count = $acc.accChildCount } catch { return $null }
    if ($count -lt 1 -or $count -gt 5000) { return $null }
    for ($i = 1; $i -le $count; $i++) {
        $n = ''
        try { $n = [string]$acc.accName($i) } catch {}
        if ($n -match $pattern) { return @{ Index = $i; Name = $n } }
    }
    return $null
}

# accLocation has out-params for x, y, w, h. PowerShell can't bind out args
# to dispatch interfaces easily. We use the array form: accLocation returns
# a 4-tuple via the dispid-level invoke. Trick: use [ref] tied vars.
function Get-AccRect {
    param($acc, [int]$idx)
    $x = 0; $y = 0; $w = 0; $h = 0
    try {
        $acc.accLocation([ref]$x, [ref]$y, [ref]$w, [ref]$h, $idx)
        return @{ X = $x; Y = $y; W = $w; H = $h }
    } catch {
        Write-Host "    accLocation threw: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

function Click-At {
    param([int]$x, [int]$y, [string]$label)
    Write-Host "  CLICK '$label' @ ($x,$y)" -ForegroundColor Yellow
    [void][W32]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 80
    [W32]::mouse_event([W32]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [W32]::mouse_event([W32]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
}

function Get-ErwinAllWindows {
    $set = New-Object System.Collections.Generic.HashSet[long]
    $proc = [W32+EnumWindowsProc] {
        param([IntPtr]$h, [IntPtr]$lp)
        $procId = 0
        [void][W32]::GetWindowThreadProcessId($h, [ref]$procId)
        if ($procId -eq $erwinPid) {
            [void]$set.Add($h.ToInt64())
            [void][W32]::EnumChildWindows($h, $proc, [IntPtr]::Zero)
        }
        return $true
    }
    [void][W32]::EnumWindows($proc, [IntPtr]::Zero)
    return $set
}

# ---------------- main -----------------
$savedCursor = New-Object W32+POINT
[void][W32]::GetCursorPos([ref]$savedCursor)
Write-Host "Saved cursor pos: ($($savedCursor.X),$($savedCursor.Y))" -ForegroundColor Gray

Write-Host ""
Write-Host "[1/5] Locate Add-Ins..." -ForegroundColor Cyan
$ribbonAcc = $null; $addInsIdx = $null
foreach ($tb in Get-ErwinVisibleXtpToolbars) {
    $acc = Get-Acc -hwnd $tb.Hwnd
    if (-not $acc) { continue }
    $hit = Find-AccChild -acc $acc -pattern '^Add\-?Ins$|^Eklentiler$'
    if ($hit) {
        $ribbonAcc = $acc; $addInsIdx = $hit.Index
        Write-Host "  found at hwnd=0x$('{0:X}' -f $tb.Hwnd.ToInt64()) idx=$($hit.Index) name='$($hit.Name)'" -ForegroundColor Green
        break
    }
}
if (-not $ribbonAcc) { Write-Host "Add-Ins not found" -ForegroundColor Red; exit 2 }

Write-Host ""
Write-Host "[2/5] accLocation + click..." -ForegroundColor Cyan
$rect = Get-AccRect -acc $ribbonAcc -idx $addInsIdx
if (-not $rect) { Write-Host "no rect" -ForegroundColor Red; exit 3 }
Write-Host "  rect: x=$($rect.X) y=$($rect.Y) w=$($rect.W) h=$($rect.H)" -ForegroundColor Gray
$cx = $rect.X + [int]($rect.W / 2)
$cy = $rect.Y + [int]($rect.H / 2)

[void][W32]::ShowWindow($erwin.MainWindowHandle, [W32]::SW_RESTORE)
[void][W32]::SetForegroundWindow($erwin.MainWindowHandle)
Start-Sleep -Milliseconds 200

$baselineWin = Get-ErwinAllWindows
Click-At -x $cx -y $cy -label 'Add-Ins'

Write-Host ""
Write-Host "[3/5] Poll for XTPPopupBar..." -ForegroundColor Cyan
$popupHwnd = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds(3)
while ((Get-Date) -lt $deadline -and $popupHwnd -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 100
    foreach ($h in (Get-ErwinAllWindows)) {
        if ($baselineWin.Contains($h)) { continue }
        $hwnd = [IntPtr]$h
        $sb = New-Object System.Text.StringBuilder 256
        [void][W32]::GetClassName($hwnd, $sb, 256)
        if ($sb.ToString() -eq 'XTPPopupBar' -and [W32]::IsWindowVisible($hwnd)) {
            $popupHwnd = $hwnd; break
        }
    }
}
if ($popupHwnd -eq [IntPtr]::Zero) {
    Write-Host "  no popup appeared - clicking went to wrong place?" -ForegroundColor Red
    [void][W32]::SetCursorPos($savedCursor.X, $savedCursor.Y)
    exit 4
}
Write-Host "  popup at hwnd=0x$('{0:X}' -f $popupHwnd.ToInt64())" -ForegroundColor Green

Write-Host ""
Write-Host "[4/5] Locate addin in popup + click..." -ForegroundColor Cyan
$popupAcc = Get-Acc -hwnd $popupHwnd
$addinHit = Find-AccChild -acc $popupAcc -pattern 'Elite\s*Soft.*Erwin|Erwin\s*Addin'
if (-not $addinHit) {
    Write-Host "  addin item not in popup" -ForegroundColor Red
    [void][W32]::SetCursorPos($savedCursor.X, $savedCursor.Y)
    exit 5
}
Write-Host "  found at idx=$($addinHit.Index) name='$($addinHit.Name)'" -ForegroundColor Green
$rect2 = Get-AccRect -acc $popupAcc -idx $addinHit.Index
if (-not $rect2) {
    Write-Host "  no rect for addin item" -ForegroundColor Red
    [void][W32]::SetCursorPos($savedCursor.X, $savedCursor.Y)
    exit 6
}
$cx2 = $rect2.X + [int]($rect2.W / 2)
$cy2 = $rect2.Y + [int]($rect2.H / 2)
Click-At -x $cx2 -y $cy2 -label 'Elite Soft Erwin Addin'

[void][W32]::SetCursorPos($savedCursor.X, $savedCursor.Y)
Write-Host "  cursor restored" -ForegroundColor Gray

Write-Host ""
Write-Host "[5/5] Wait 5s + diff state..." -ForegroundColor Cyan
Start-Sleep -Seconds 5
$after = Snapshot-State
$changed = New-Object System.Collections.ArrayList
foreach ($k in $after.Keys) {
    if (-not $before.ContainsKey($k)) { [void]$changed.Add("NEW : $k") }
    elseif ($before[$k].LWT -ne $after[$k].LWT -or $before[$k].Length -ne $after[$k].Length) {
        [void]$changed.Add("UPD : $k ($($before[$k].Length)->$($after[$k].Length))")
    }
}

Write-Host ""
Write-Host "=== RESULT ===" -ForegroundColor Cyan
if ($changed.Count -eq 0) {
    Write-Host "Chain completed but NO file changes." -ForegroundColor Yellow
    Write-Host "Check erwin: is the addin UI visible / did Execute() actually run?" -ForegroundColor Yellow
    exit 30
}
Write-Host "$($changed.Count) state changes:" -ForegroundColor Green
$changed | Select-Object -First 30 | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
if ($changed.Count -gt 30) { Write-Host "  ... ($($changed.Count - 30) more)" -ForegroundColor Gray }
Write-Host ""
Write-Host "SUCCESS - full mouse-click invoke chain works." -ForegroundColor Green
exit 0
