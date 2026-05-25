# Spike: invoke addin via MSAA accDoDefaultAction (no DLL injection, no
# WM_COMMAND pointer marshalling, no mouse simulation).
#
# Steps:
#   1. Find erwin in current session.
#   2. Snapshot %LOCALAPPDATA%\EliteSoft state.
#   3. Find ribbon "Add-Ins" item via MSAA (XTPToolBar 'The Ribbon').
#   4. accDoDefaultAction on it -> hope XTP fires the popup.
#   5. Poll up to 2s for a new XTPPopupBar window.
#   6. If popup appears, find "Elite Soft Erwin Addin" item in it.
#   7. accDoDefaultAction on it.
#   8. Wait 4s and diff %LOCALAPPDATA%\EliteSoft.
#
# Outcome modes:
#   A. Everything works -> watcher refactor uses this pattern.
#   B. Step 4 throws or doesn't open popup -> MSAA invoke unsupported by XTP,
#      we need mouse simulation fallback.
#   C. Popup opens but step 7 fails -> partial - we know click-the-ribbon is
#      MSAA-fine but submenu items need mouse simulation.

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
Write-Host "erwin PID=$erwinPid" -ForegroundColor Cyan

# --- snapshot addin state ---
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
Write-Host "State snapshot: $($before.Count) files" -ForegroundColor Gray

# --- collect every visible XTPToolBar inside erwin ---
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

# --- collect all windows under erwin (for "new popup" detection) ---
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
    # also expand children-of-children
    $stack = New-Object System.Collections.Stack
    foreach ($v in @($set)) { $stack.Push([IntPtr]$v) }
    while ($stack.Count -gt 0) {
        $top = $stack.Pop()
        $child = [W32+EnumWindowsProc] {
            param([IntPtr]$h, [IntPtr]$lp)
            if ($set.Add($h.ToInt64())) { $stack.Push($h) }
            return $true
        }
        [void][W32]::EnumChildWindows($top, $child, [IntPtr]::Zero)
    }
    return $set
}

# --- MSAA helper: get IAccessible for a hwnd ---
function Get-Acc {
    param([IntPtr]$hwnd)
    $iid = [W32]::IID_IAccessible
    $obj = $null
    $hr = [W32]::AccessibleObjectFromWindow($hwnd, [W32]::OBJID_CLIENT, [ref]$iid, [ref]$obj)
    if ($hr -ne 0 -or $null -eq $obj) { return $null }
    return $obj
}

# --- find a child by name match ---
function Find-AccChild {
    param($acc, [string]$pattern)
    $count = -1
    try { $count = $acc.accChildCount } catch { return $null }
    if ($count -lt 1 -or $count -gt 5000) { return $null }
    for ($i = 1; $i -le $count; $i++) {
        $n = ''
        try { $n = [string]$acc.accName($i) } catch {}
        if ($n -match $pattern) {
            return @{ Index = $i; Name = $n }
        }
    }
    return $null
}

# --- step 1: locate ribbon Add-Ins ---
Write-Host ""
Write-Host "[1/4] Locating 'Add-Ins' in The Ribbon..." -ForegroundColor Yellow
$toolbars = Get-ErwinVisibleXtpToolbars
Write-Host "  $($toolbars.Count) visible XTPToolBars" -ForegroundColor Gray
$ribbonAcc = $null
$addInsIdx = $null
$ribbonHwnd = $null
foreach ($tb in $toolbars) {
    $acc = Get-Acc -hwnd $tb.Hwnd
    if (-not $acc) { continue }
    $hit = Find-AccChild -acc $acc -pattern '^Add\-?Ins$|^Eklentiler$'
    if ($hit) {
        $ribbonAcc = $acc
        $addInsIdx = $hit.Index
        $ribbonHwnd = $tb.Hwnd
        Write-Host "  FOUND: '$($hit.Name)' at hwnd=0x$('{0:X}' -f $tb.Hwnd.ToInt64()) title='$($tb.Title)' idx=$($hit.Index)" -ForegroundColor Green
        break
    }
}
if (-not $ribbonAcc) {
    Write-Host "  Add-Ins not found in any XTPToolBar - abort" -ForegroundColor Red
    exit 2
}

# --- step 2: invoke Add-Ins via MSAA ---
Write-Host ""
Write-Host "[2/4] Calling accDoDefaultAction($addInsIdx) on Add-Ins..." -ForegroundColor Yellow
$baselineWindows = Get-ErwinAllWindows
Write-Host "  baseline windows: $($baselineWindows.Count)" -ForegroundColor Gray
try {
    # Foreground erwin so popups land where MSAA expects them.
    [void][W32]::SetForegroundWindow($erwin.MainWindowHandle)
    Start-Sleep -Milliseconds 200

    $ribbonAcc.accDoDefaultAction($addInsIdx)
    Write-Host "  accDoDefaultAction returned without exception" -ForegroundColor Green
} catch {
    Write-Host "  THREW: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  CONCLUSION: MSAA invoke not supported on XTP ribbon - fall back to mouse sim." -ForegroundColor Yellow
    exit 10
}

# --- step 3: wait for a new XTPPopupBar window ---
Write-Host ""
Write-Host "[3/4] Polling for new XTPPopupBar (max 2s)..." -ForegroundColor Yellow
$popupHwnd = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds(2)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 100
    $now = Get-ErwinAllWindows
    foreach ($h in $now) {
        if ($baselineWindows.Contains($h)) { continue }
        $hwnd = [IntPtr]$h
        $sb = New-Object System.Text.StringBuilder 256
        [void][W32]::GetClassName($hwnd, $sb, 256)
        if ($sb.ToString() -eq 'XTPPopupBar' -and [W32]::IsWindowVisible($hwnd)) {
            $popupHwnd = $hwnd
            break
        }
    }
    if ($popupHwnd -ne [IntPtr]::Zero) { break }
}

if ($popupHwnd -eq [IntPtr]::Zero) {
    Write-Host "  No XTPPopupBar appeared in 2s." -ForegroundColor Red
    Write-Host "  accDoDefaultAction did not actually open the popup. Fall back to mouse sim." -ForegroundColor Yellow
    exit 11
}
Write-Host "  Popup appeared: hwnd=0x$('{0:X}' -f $popupHwnd.ToInt64())" -ForegroundColor Green

# --- step 4: find Elite Soft Erwin Addin in popup + invoke ---
Write-Host ""
Write-Host "[4/4] Finding 'Elite Soft Erwin Addin' in popup..." -ForegroundColor Yellow
$popupAcc = Get-Acc -hwnd $popupHwnd
if (-not $popupAcc) {
    Write-Host "  Popup has no IAccessible." -ForegroundColor Red
    exit 12
}
$addinHit = Find-AccChild -acc $popupAcc -pattern 'Elite\s*Soft.*Erwin|Erwin\s*Addin'
if (-not $addinHit) {
    Write-Host "  'Elite Soft Erwin Addin' not in popup children." -ForegroundColor Red
    exit 13
}
Write-Host "  FOUND addin in popup at idx=$($addinHit.Index) name='$($addinHit.Name)'" -ForegroundColor Green

try {
    $popupAcc.accDoDefaultAction($addinHit.Index)
    Write-Host "  accDoDefaultAction returned without exception" -ForegroundColor Green
} catch {
    Write-Host "  THREW: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  CONCLUSION: ribbon invoke worked, submenu invoke didn't. Mixed result." -ForegroundColor Yellow
    exit 20
}

# --- verify Execute() actually ran ---
Write-Host ""
Write-Host "Waiting 4s for Execute() side effects..." -ForegroundColor Gray
Start-Sleep -Seconds 4
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
    Write-Host "Invoke chain completed but NO file changes under $addinRoot" -ForegroundColor Yellow
    Write-Host "Possible: Execute() ran but writes elsewhere. Check erwin UI for addin presence." -ForegroundColor Yellow
    exit 30
}
Write-Host "$($changed.Count) state changes observed:" -ForegroundColor Green
$changed | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
Write-Host ""
Write-Host "SUCCESS: full MSAA-invoke chain works end-to-end!" -ForegroundColor Green
exit 0
