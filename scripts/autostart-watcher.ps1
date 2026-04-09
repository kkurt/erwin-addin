# Elite Soft Erwin Add-In - WMI Event-Driven Auto-Start Watcher
# Detects erwin.exe start via WMI event, waits for model to open, then activates add-in.
# Runs as a hidden Scheduled Task at user logon.

$progId = "EliteSoft.Erwin.AddIn"
$erwinName = "erwin.exe"
$mySessionId = (Get-Process -Id $PID).SessionId
$modelCheckIntervalSec = 3
$modelTimeoutSec = 300  # Give up after 5 minutes if no model opened
$fallbackPollSec = 30
$logFile = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn\autostart.log"

function Write-Log([string]$msg) {
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] $msg"
    try {
        $dir = Split-Path $logFile -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue
    } catch { }
}

function Get-MyErwin {
    return Get-Process -Name "erwin" -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $mySessionId }
}

function Wait-ForModel {
    # Wait until erwin window title contains a model name (e.g. "erwin DM - [ModelName : ...]")
    $elapsed = 0
    while ($elapsed -lt $modelTimeoutSec) {
        if (-not (Get-MyErwin)) {
            Write-Log "erwin closed while waiting for model"
            return $false
        }

        $erwinProc = Get-MyErwin | Where-Object { $_.MainWindowTitle -match "erwin.*\[.+\]" } | Select-Object -First 1
        if ($erwinProc) {
            Write-Log "Model detected: '$($erwinProc.MainWindowTitle)'"
            return $true
        }

        Start-Sleep -Seconds $modelCheckIntervalSec
        $elapsed += $modelCheckIntervalSec
    }

    Write-Log "Timeout waiting for model ($modelTimeoutSec sec)"
    return $false
}

Write-Log "Watcher started (PID=$PID, Session=$mySessionId)"

while ($true) {
    # Skip if erwin is already running
    $existing = Get-MyErwin
    if (-not $existing) {
        # --- WMI Event: wait for erwin.exe to start ---
        $wmiQuery = "SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Process' AND TargetInstance.Name = '$erwinName'"
        $detected = $false

        try {
            $eventId = "ErwinStartEvent_$(Get-Random)"
            Register-WmiEvent -Query $wmiQuery -SourceIdentifier $eventId -ErrorAction Stop

            Write-Log "Waiting for erwin.exe (WMI event)..."

            $wmiEvent = Wait-Event -SourceIdentifier $eventId -Timeout $fallbackPollSec
            Unregister-Event -SourceIdentifier $eventId -ErrorAction SilentlyContinue
            Remove-Event -SourceIdentifier $eventId -ErrorAction SilentlyContinue

            if ($wmiEvent) {
                $myErwin = Get-MyErwin
                if ($myErwin) {
                    $detected = $true
                    Write-Log "erwin.exe detected in our session (WMI event)"
                } else {
                    Write-Log "erwin.exe started in different session - ignoring"
                }
            }
        } catch {
            Write-Log "WMI event failed: $_ - falling back to poll"
            Start-Sleep -Seconds $fallbackPollSec
            if (Get-MyErwin) { $detected = $true }
        }

        if (-not $detected) { continue }
    } else {
        Write-Log "erwin.exe already running in our session"
    }

    # --- Wait for model to be opened ---
    Write-Log "Waiting for model to open..."
    if (-not (Wait-ForModel)) {
        # No model opened or erwin closed - resume watching
        Start-Sleep -Seconds 3
        continue
    }

    # --- Activate add-in via VBScript ---
    try {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
        $vbsPath = Join-Path $scriptDir "activate-addin.vbs"
        if (-not (Test-Path $vbsPath)) {
            $vbsContent = "On Error Resume Next`r`nSet a = CreateObject(`"$progId`")`r`na.Execute`r`n"
            Set-Content -Path $vbsPath -Value $vbsContent -Encoding ASCII
        }
        Start-Process "cscript.exe" -ArgumentList "//nologo `"$vbsPath`"" -WindowStyle Hidden
        Write-Log "Add-in activation triggered"
    } catch {
        Write-Log "Activation failed: $_"
    }

    # --- Wait for erwin to close ---
    Write-Log "Monitoring erwin process..."
    while ($true) {
        Start-Sleep -Seconds 5
        if (-not (Get-MyErwin)) {
            Write-Log "erwin closed - resuming watch"
            break
        }
    }

    Start-Sleep -Seconds 3
}
