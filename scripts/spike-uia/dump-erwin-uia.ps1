# Spike: dump erwin's UI Automation (UIA) tree to a text file.
#
# Goal: understand where the "Elite Soft Erwin Addin" menu item lives in
# erwin's UI hierarchy so we can target it from outside the process (no
# DLL injection -> no SONAR.ProcHijack trigger).
#
# Usage:
#   1. Open erwin DM r10, open any model (so Tools menu populates).
#   2. Run this script (no params).
#   3. Inspect c:\tmp\erwin-uia-tree.txt for the menu item path.
#
# This is a READ-ONLY spike. It does NOT invoke anything and does NOT
# modify erwin or the system. Safe to run in production.

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } |
         Select-Object -First 1

if (-not $erwin) {
    Write-Host "ERROR: erwin not running in session $mySession" -ForegroundColor Red
    Write-Host "       Open erwin DM and load a model, then re-run." -ForegroundColor Yellow
    exit 1
}

Write-Host "Found erwin PID=$($erwin.Id) MainHwnd=0x$('{0:X}' -f $erwin.MainWindowHandle.ToInt64())" -ForegroundColor Cyan
Write-Host "Title: $($erwin.MainWindowTitle)" -ForegroundColor Gray

if (-not $erwin.MainWindowHandle -or $erwin.MainWindowHandle -eq [IntPtr]::Zero) {
    Write-Host "ERROR: erwin has no main window handle (still starting up?)" -ForegroundColor Red
    exit 2
}

$ae = [System.Windows.Automation.AutomationElement]
$tc = [System.Windows.Automation.TreeScope]
$cond = [System.Windows.Automation.Condition]
$root = $ae::FromHandle($erwin.MainWindowHandle)
if (-not $root) {
    Write-Host "ERROR: AutomationElement::FromHandle returned null" -ForegroundColor Red
    exit 3
}

$outDir = 'c:\tmp'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$outFile = Join-Path $outDir 'erwin-uia-tree.txt'
$sw = [System.IO.StreamWriter]::new($outFile, $false, [System.Text.Encoding]::UTF8)
$sw.AutoFlush = $true

# Depth-bounded recursive dump. Depth 8 is enough to reach ribbon items
# in tested XTP layouts; deeper trees would explode the file size.
$maxDepth = 8
$nodeCount = 0
$matchCount = 0

function Dump-Element {
    param(
        [System.Windows.Automation.AutomationElement]$el,
        [int]$depth
    )
    if ($depth -gt $script:maxDepth) { return }

    $script:nodeCount++
    $indent = '  ' * $depth
    $name = ''; $ct = ''; $aid = ''; $cls = ''; $patterns = @()

    try {
        $name = $el.Current.Name
        $ct   = $el.Current.ControlType.LocalizedControlType
        $aid  = $el.Current.AutomationId
        $cls  = $el.Current.ClassName

        # Capture which patterns this element supports - tells us whether
        # we can Invoke / Expand / Toggle it without trial-and-error.
        $supported = $el.GetSupportedPatterns()
        foreach ($p in $supported) { $patterns += $p.ProgrammaticName }
    } catch {
        $script:sw.WriteLine("$indent[ERROR reading element: $($_.Exception.Message)]")
        return
    }

    $patStr = if ($patterns.Count -gt 0) { ' patterns=' + ($patterns -join ',') } else { '' }
    $line = "$indent[$ct] Name='$name' AId='$aid' Class='$cls'$patStr"
    $script:sw.WriteLine($line)

    # Highlight likely add-in matches on stdout for fast triage.
    if ($name -match 'Elite Soft|EliteSoft|Erwin Addin' -or $aid -match 'EliteSoft|ErwinAddin') {
        Write-Host "  MATCH @ depth=$depth : $line" -ForegroundColor Green
        $script:matchCount++
    }

    try {
        $children = $el.FindAll($script:tc::Children, $script:cond::TrueCondition)
        foreach ($child in $children) {
            Dump-Element -el $child -depth ($depth + 1)
        }
    } catch {
        $script:sw.WriteLine("$indent  [could not enumerate children: $($_.Exception.Message)]")
    }
}

Write-Host "`nWalking UIA tree (max depth $maxDepth)..." -ForegroundColor Cyan
$start = Get-Date
Dump-Element -el $root -depth 0
$elapsed = (Get-Date) - $start
$sw.Close()

Write-Host ""
Write-Host "Dump complete in $([int]$elapsed.TotalSeconds)s : $nodeCount nodes, $matchCount addin matches" -ForegroundColor Cyan
Write-Host "Output: $outFile" -ForegroundColor Cyan

if ($matchCount -eq 0) {
    Write-Host ""
    Write-Host "No direct match. Add-in item is likely lazy-loaded under a closed menu." -ForegroundColor Yellow
    Write-Host "Next step: open the 'Tools' or 'Add-Ins' menu in erwin BY HAND, then re-run this script" -ForegroundColor Yellow
    Write-Host "while the menu is still open. Lazy menu items only exist in the UIA tree while visible." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Quick grep for relevant strings in the dump:" -ForegroundColor Cyan
$patterns = @('Elite Soft', 'EliteSoft', 'Erwin Addin', 'Add-In', 'Tools', 'Add-Ins')
foreach ($p in $patterns) {
    $hits = @(Select-String -Path $outFile -Pattern $p -SimpleMatch -ErrorAction SilentlyContinue)
    Write-Host ("  '{0,-15}' : {1} hits" -f $p, $hits.Count) -ForegroundColor Gray
}
