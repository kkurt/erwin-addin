# Spike: directly test whether 'Review' is findable+invokable via UIA
# (because the addin's existing code says it works), then test 'Add-Ins'.
# Side-by-side reproduction.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Language CSharp @'
using System;
using System.Runtime.InteropServices;
public static class W {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@

$ae   = [System.Windows.Automation.AutomationElement]
$tc   = [System.Windows.Automation.TreeScope]
$cond = [System.Windows.Automation.Condition]
$inv  = [System.Windows.Automation.InvokePattern]

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
[void][W]::SetForegroundWindow($erwin.MainWindowHandle)
Start-Sleep -Milliseconds 300

$root = $ae::FromHandle($erwin.MainWindowHandle)
$prop = [System.Windows.Automation.AutomationElement]::NameProperty

function Find-And-Report {
    param([string]$searchName)
    Write-Host ""
    Write-Host "=== Searching descendants for Name='$searchName' ===" -ForegroundColor Cyan
    $nameCond = New-Object System.Windows.Automation.PropertyCondition($script:prop, $searchName)
    $found = $root.FindFirst($script:tc::Descendants, $nameCond)
    if (-not $found) {
        Write-Host "  NOT FOUND with exact match" -ForegroundColor Yellow
        # try partial
        $all = $root.FindAll($script:tc::Descendants, $script:cond::TrueCondition)
        $partials = @($all | Where-Object {
            try { $_.Current.Name -and $_.Current.Name.IndexOf($searchName, [StringComparison]::OrdinalIgnoreCase) -ge 0 } catch { $false }
        })
        Write-Host "  Partial matches: $($partials.Count)" -ForegroundColor Gray
        foreach ($p in $partials | Select-Object -First 5) {
            try {
                $ct = $p.Current.ControlType.ProgrammaticName
                Write-Host "    ct=$ct name='$($p.Current.Name)'" -ForegroundColor Gray
            } catch {}
        }
        return
    }
    $ct = $found.Current.ControlType.ProgrammaticName
    $aid = $found.Current.AutomationId
    Write-Host "  FOUND: ct=$ct AId='$aid'" -ForegroundColor Green
    $patterns = $found.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }
    Write-Host "  Supported patterns: $($patterns -join ', ')" -ForegroundColor Gray
    $pat = $null
    if ($found.TryGetCurrentPattern($script:inv::Pattern, [ref]$pat)) {
        Write-Host "  Has InvokePattern - would Invoke" -ForegroundColor Green
    } else {
        Write-Host "  NO InvokePattern" -ForegroundColor Yellow
    }
}

Find-And-Report -searchName 'Review'
Find-And-Report -searchName 'Add-Ins'
Find-And-Report -searchName 'Catalog Manager'
Find-And-Report -searchName 'Session Manager'
