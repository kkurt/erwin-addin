# Spike: invoke the "Elite Soft Erwin Addin" menu item via UI Automation.
#
# Goal: prove we can trigger the add-in's Execute() method from OUTSIDE
# the erwin process (no DLL injection -> no SONAR.ProcHijack trigger).
# If this works on prod hardware, we can drop ErwinInjector.exe +
# TriggerDll.dll entirely.
#
# Strategy (each tried in order, first one that succeeds wins):
#   S1. Direct: walk UIA tree, find menu item by Name, InvokePattern.
#   S2. Lazy menus: find parent ("Tools" / "Add-Ins") via Name, ExpandPattern
#       to materialize children, then S1.
#   S3. Alt+T accelerator: send Alt+T to focus Tools menu, then arrow keys to
#       the item, then Enter. Crude fallback for ribbon UIs where UIA Invoke
#       is filtered (see memory: reference_uia_xtptoolbar_filter).
#
# Usage:
#   1. Open erwin DM r10, open a model (so add-in menu populates).
#   2. Run this script.
#   3. Read the report at end of stdout.
#
# This spike does ONE invoke attempt then exits. It does NOT modify any
# config or registry.

param(
    # Default matches the registered display name in install-impl.ps1.
    [string]$AddInName = 'Elite Soft Erwin Addin'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms  # for SendKeys fallback

$ae   = [System.Windows.Automation.AutomationElement]
$tc   = [System.Windows.Automation.TreeScope]
$cond = [System.Windows.Automation.Condition]
$inv  = [System.Windows.Automation.InvokePattern]
$exp  = [System.Windows.Automation.ExpandCollapsePattern]
$prop = [System.Windows.Automation.AutomationElement]::NameProperty

# Snapshot watcher/addin log directory state. Compared after invoke to
# verify Execute() actually ran (any log file mtime change OR a brand-new
# file counts as success). Trigger log was the injection signal; the
# add-in itself writes to its own log on Execute() entry.
function Snapshot-AddInState {
    $root = Join-Path $env:LOCALAPPDATA 'EliteSoft'
    $snap = @{}
    if (Test-Path $root) {
        Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
            $snap[$_.FullName] = @{ Length = $_.Length; LastWrite = $_.LastWriteTime }
        }
    }
    return $snap
}

function Diff-AddInState {
    param($before, $after)
    $changed = New-Object System.Collections.ArrayList
    foreach ($k in $after.Keys) {
        if (-not $before.ContainsKey($k)) {
            [void]$changed.Add("NEW  : $k")
        } elseif ($before[$k].LastWrite -ne $after[$k].LastWrite -or $before[$k].Length -ne $after[$k].Length) {
            [void]$changed.Add("UPD  : $k (size $($before[$k].Length)->$($after[$k].Length))")
        }
    }
    return $changed
}

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } |
         Select-Object -First 1
if (-not $erwin) {
    Write-Host "ERROR: erwin not running in session $mySession - open erwin + model first." -ForegroundColor Red
    exit 1
}

Write-Host "Target: erwin PID=$($erwin.Id) Title='$($erwin.MainWindowTitle)'" -ForegroundColor Cyan
Write-Host "Looking for menu item: '$AddInName'" -ForegroundColor Cyan
Write-Host ""

$root = $ae::FromHandle($erwin.MainWindowHandle)
if (-not $root) { Write-Host "AutomationElement::FromHandle returned null" -ForegroundColor Red; exit 2 }

$before = Snapshot-AddInState
Write-Host "State snapshot taken ($($before.Count) files under %LOCALAPPDATA%\EliteSoft)" -ForegroundColor Gray
Write-Host ""

# --- S1: direct subtree find -----------------------------------------------
function Try-DirectInvoke {
    param([System.Windows.Automation.AutomationElement]$root, [string]$name)
    Write-Host "[S1] Searching whole subtree for '$name'..." -ForegroundColor Yellow
    $nameCond = New-Object System.Windows.Automation.PropertyCondition($script:prop, $name)
    $found = $root.FindFirst($script:tc::Descendants, $nameCond)
    if (-not $found) {
        Write-Host "  not found in current tree" -ForegroundColor Gray
        return $false
    }
    Write-Host "  found: ControlType=$($found.Current.ControlType.LocalizedControlType) AId='$($found.Current.AutomationId)'" -ForegroundColor Green
    try {
        $pat = $null
        if ($found.TryGetCurrentPattern($script:inv::Pattern, [ref]$pat)) {
            $pat.Invoke()
            Write-Host "  InvokePattern.Invoke() called OK" -ForegroundColor Green
            return $true
        } else {
            Write-Host "  element does NOT support InvokePattern (XTP filter?)" -ForegroundColor Yellow
            return $false
        }
    } catch {
        Write-Host "  Invoke threw: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# --- S2: open parents (Tools / Add-Ins) then retry -------------------------
function Try-ExpandThenInvoke {
    param([System.Windows.Automation.AutomationElement]$root, [string]$name)
    Write-Host "[S2] Expanding likely parents (Tools, Add-Ins) to materialize menus..." -ForegroundColor Yellow
    $parentNames = @('Tools', 'Add-Ins', 'Araclar', 'Eklentiler')  # English + Turkish
    $expanded = @()
    foreach ($pn in $parentNames) {
        $c = New-Object System.Windows.Automation.PropertyCondition($script:prop, $pn)
        $hits = @($root.FindAll($script:tc::Descendants, $c))
        foreach ($h in $hits) {
            try {
                $ep = $null
                if ($h.TryGetCurrentPattern($script:exp::Pattern, [ref]$ep)) {
                    Write-Host "  expanding '$pn' (ct=$($h.Current.ControlType.LocalizedControlType))" -ForegroundColor Gray
                    $ep.Expand()
                    $expanded += @{ El = $h; Pat = $ep }
                    Start-Sleep -Milliseconds 250
                }
            } catch {
                Write-Host "  expand '$pn' threw: $($_.Exception.Message)" -ForegroundColor DarkYellow
            }
        }
    }

    $ok = Try-DirectInvoke -root $root -name $name

    # Collapse what we opened, so we don't leave erwin's menus dangling.
    foreach ($e in $expanded) {
        try { $e.Pat.Collapse() } catch { }
    }
    return $ok
}

# --- S3: keyboard accelerator (Alt+T then walk by name) --------------------
function Try-KeyboardAccelerator {
    param([System.Windows.Automation.AutomationElement]$root, [string]$name)
    Write-Host "[S3] Trying Alt+T keyboard accelerator (Tools menu)..." -ForegroundColor Yellow
    # Bring erwin to foreground first - SendKeys hits the focused window.
    $hwnd = [IntPtr]$root.Current.NativeWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) {
        Write-Host "  no NativeWindowHandle on root; cannot focus" -ForegroundColor Red
        return $false
    }
    Add-Type -Namespace W -Name U -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hwnd, int cmd);
'@ -ErrorAction SilentlyContinue
    [void][W.U]::ShowWindow($hwnd, 9)  # SW_RESTORE
    [void][W.U]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 300

    [System.Windows.Forms.SendKeys]::SendWait('%t')  # Alt+T
    Start-Sleep -Milliseconds 500

    # After Tools menu opens, look for our addin in newly visible elements.
    $nameCond = New-Object System.Windows.Automation.PropertyCondition($script:prop, $name)
    $found = $root.FindFirst($script:tc::Descendants, $nameCond)
    if (-not $found) {
        Write-Host "  '$name' not visible after Alt+T (Tools menu may not exist on ribbon UI)" -ForegroundColor Gray
        # ESC to close any partial menu we opened.
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        return $false
    }
    try {
        $pat = $null
        if ($found.TryGetCurrentPattern($script:inv::Pattern, [ref]$pat)) {
            $pat.Invoke()
            Write-Host "  Invoke OK via S3" -ForegroundColor Green
            return $true
        }
    } catch { Write-Host "  Invoke threw: $($_.Exception.Message)" -ForegroundColor Red }
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    return $false
}

# --- Run all strategies in order, stop at first success --------------------
$succeeded = $false
$strategyUsed = $null
$attempts = @(
    @{ Name = 'S1 direct'         ; Fn = { Try-DirectInvoke -root $root -name $AddInName } },
    @{ Name = 'S2 expand-then'    ; Fn = { Try-ExpandThenInvoke -root $root -name $AddInName } },
    @{ Name = 'S3 keyboard alt+t' ; Fn = { Try-KeyboardAccelerator -root $root -name $AddInName } }
)
foreach ($a in $attempts) {
    Write-Host ""
    Write-Host "=== $($a.Name) ===" -ForegroundColor Cyan
    $ok = & $a.Fn
    if ($ok) {
        $succeeded = $true
        $strategyUsed = $a.Name
        break
    }
}

Write-Host ""
Write-Host "=== RESULT ===" -ForegroundColor Cyan

if (-not $succeeded) {
    Write-Host "All strategies failed. Run dump-erwin-uia.ps1 first to see the real menu structure." -ForegroundColor Red
    Write-Host "Next: identify the actual ControlType / parent path of the addin and adjust this script." -ForegroundColor Yellow
    exit 10
}

Write-Host "Invoke claimed success via: $strategyUsed" -ForegroundColor Green
Write-Host "Waiting 4s for Execute() side effects..." -ForegroundColor Gray
Start-Sleep -Seconds 4

$after = Snapshot-AddInState
$diff  = Diff-AddInState -before $before -after $after
if ($diff.Count -eq 0) {
    Write-Host ""
    Write-Host "WARNING: invoke returned OK but NO file under %LOCALAPPDATA%\EliteSoft changed." -ForegroundColor Yellow
    Write-Host "         Possible: addin already loaded earlier, or Execute() did nothing observable." -ForegroundColor Yellow
    Write-Host "         Manual check: switch to erwin, see if addin UI (toolbar/ribbon) is present." -ForegroundColor Yellow
    exit 2
}
Write-Host ""
Write-Host "OBSERVED CHANGES after invoke ($($diff.Count)):" -ForegroundColor Green
$diff | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
Write-Host ""
Write-Host "SUCCESS: invocation triggered observable add-in activity." -ForegroundColor Green
exit 0
