# Spike: replicate Win32Helper.InvokeToolbarButton EXACTLY but target the
# "Add-Ins" ribbon button. If this works the same way Review does, we have
# the answer to the WHOLE auto-load problem: zero injection, zero
# WM_COMMAND discovery, zero mouse simulation.
#
# Strategy mirrors Services/Win32Helper.cs:507:
#   1. SetForegroundWindow erwin
#   2. AutomationElement.FromHandle(erwinMain)
#   3. Recursively walk children, find element where Name matches "Add-Ins"
#      AND ControlType is Button (or MenuItem - try both!)
#   4. InvokePattern.Invoke()
#
# If the result is "popup opens" we then recurse the same trick to find
# "Elite Soft Erwin Addin" inside the popup and invoke it too.

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Language CSharp @'
using System;
using System.Runtime.InteropServices;
public static class W {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    public const int SW_RESTORE = 9;
}
'@

$ae   = [System.Windows.Automation.AutomationElement]
$tc   = [System.Windows.Automation.TreeScope]
$cond = [System.Windows.Automation.Condition]
$inv  = [System.Windows.Automation.InvokePattern]
$exp  = [System.Windows.Automation.ExpandCollapsePattern]
$ct   = [System.Windows.Automation.ControlType]

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running" -ForegroundColor Red; exit 1 }

Write-Host "erwin PID=$($erwin.Id)" -ForegroundColor Cyan

# Replicate InvokeToolbarButton's FindAndInvokeButton, but ALSO try
# MenuItem ControlType because Add-Ins is a ribbon dropdown that
# probably surfaces as MenuItem not Button.
function Find-AndInvoke {
    param(
        [System.Windows.Automation.AutomationElement]$parent,
        [string]$searchText,
        [int]$depth = 0,
        [int]$maxDepth = 12
    )
    if ($depth -gt $maxDepth) { return $null }
    try {
        $children = $parent.FindAll($script:tc::Children, $script:cond::TrueCondition)
        foreach ($child in $children) {
            try {
                $name = $child.Current.Name
                $ctrlType = $child.Current.ControlType
                if ([string]::IsNullOrEmpty($name)) {
                    # still recurse - containers often have no name
                    $hit = Find-AndInvoke -parent $child -searchText $searchText -depth ($depth+1) -maxDepth $maxDepth
                    if ($hit) { return $hit }
                    continue
                }
                $nameMatches = $name.IndexOf($searchText, [StringComparison]::OrdinalIgnoreCase) -ge 0
                if ($nameMatches) {
                    $ctName = if ($ctrlType) { $ctrlType.ProgrammaticName } else { '(null)' }
                    Write-Host ("  candidate depth={0} ct={1} name='{2}'" -f $depth, $ctName, $name) -ForegroundColor Gray
                    # Try Invoke if supported (works regardless of ControlType label)
                    $pat = $null
                    if ($child.TryGetCurrentPattern($script:inv::Pattern, [ref]$pat)) {
                        Write-Host ("  ATTEMPTING InvokePattern on '{0}' (ct={1})" -f $name, $ctName) -ForegroundColor Yellow
                        $pat.Invoke()
                        Write-Host "  Invoke() returned without error" -ForegroundColor Green
                        return $child
                    } else {
                        Write-Host ("  '{0}' has NO InvokePattern; trying ExpandCollapse..." -f $name) -ForegroundColor DarkYellow
                        $epat = $null
                        if ($child.TryGetCurrentPattern($script:exp::Pattern, [ref]$epat)) {
                            Write-Host "    ATTEMPTING Expand()..." -ForegroundColor Yellow
                            $epat.Expand()
                            Write-Host "    Expand() returned without error" -ForegroundColor Green
                            return $child
                        }
                    }
                }
                $hit = Find-AndInvoke -parent $child -searchText $searchText -depth ($depth+1) -maxDepth $maxDepth
                if ($hit) { return $hit }
            } catch {}
        }
    } catch {}
    return $null
}

# Restore + focus erwin
[void][W]::ShowWindow($erwin.MainWindowHandle, [W]::SW_RESTORE)
[void][W]::SetForegroundWindow($erwin.MainWindowHandle)
Start-Sleep -Milliseconds 300

$root = $ae::FromHandle($erwin.MainWindowHandle)
if (-not $root) { Write-Host "AutomationElement null" -ForegroundColor Red; exit 2 }

Write-Host ""
Write-Host "=== STAGE 1: invoke 'Add-Ins' ribbon button ===" -ForegroundColor Cyan
$result = Find-AndInvoke -parent $root -searchText 'Add-Ins'
if (-not $result) {
    Write-Host "Did not find 'Add-Ins' as a Button/Invokable. Trying broader 'Add' search..." -ForegroundColor Yellow
    $result = Find-AndInvoke -parent $root -searchText 'Add'
}

if (-not $result) {
    Write-Host "FAILED to find/invoke Add-Ins ribbon button." -ForegroundColor Red
    exit 10
}

Write-Host ""
Write-Host "If a popup opened, give it 500ms then try to invoke 'Elite Soft Erwin Addin'..." -ForegroundColor Cyan
Start-Sleep -Milliseconds 600

Write-Host ""
Write-Host "=== STAGE 2: invoke 'Elite Soft Erwin Addin' popup item ===" -ForegroundColor Cyan
# Re-fetch root - the popup might be a top-level window in erwin's process
$root2 = $ae::FromHandle($erwin.MainWindowHandle)
$addin = Find-AndInvoke -parent $root2 -searchText 'Elite Soft Erwin Addin'
if (-not $addin) {
    Write-Host "Did not find addin in current state. Trying root walker..." -ForegroundColor Yellow
    # Walk full root including newly-opened popup windows
    $rootEl = $ae::RootElement
    $addin = Find-AndInvoke -parent $rootEl -searchText 'Elite Soft Erwin Addin' -maxDepth 6
}

if ($addin) {
    Write-Host ""
    Write-Host "BOTH STAGES SUCCEEDED - addin should be loading!" -ForegroundColor Green
    exit 0
} else {
    Write-Host ""
    Write-Host "Stage 1 invoked something but Stage 2 couldn't find addin." -ForegroundColor Yellow
    Write-Host "Check erwin: did the Add-Ins popup open? Is the addin loading?" -ForegroundColor Yellow
    exit 11
}
