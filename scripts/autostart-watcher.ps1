# Elite Soft Erwin Add-In - WMI Event-Driven Auto-Start Watcher
# Detects erwin.exe start via WMI event, waits for model to open,
# then activates add-in via DLL injection.
# Runs as a hidden Scheduled Task at user logon.

$erwinName = "erwin.exe"
$mySessionId = (Get-Process -Id $PID).SessionId
$modelCheckIntervalSec = 1
$modelTimeoutSec = 300  # Give up after 5 minutes if no model opened
$fallbackPollSec = 30

$installDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn"
$logFile = Join-Path $installDir "autostart.log"
$injectorExe = Join-Path $installDir "ErwinInjector.exe"
$triggerDll = Join-Path $installDir "TriggerDll.dll"

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

# Kill other watcher instances (prevent duplicates)
try {
    Get-WmiObject Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match "autostart-watcher" -and $_.ProcessId -ne $PID } |
        ForEach-Object {
            Write-Log "Killing duplicate watcher PID=$($_.ProcessId)"
            $_.Terminate() | Out-Null
        }
} catch { }

# Validate injector files exist
if (-not (Test-Path $injectorExe)) {
    Write-Log "ERROR: Injector not found at $injectorExe"
    exit 1
}
if (-not (Test-Path $triggerDll)) {
    Write-Log "ERROR: TriggerDll not found at $triggerDll"
    exit 1
}

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

    # --- Remember which erwin PID we're injecting into ---
    $targetErwin = Get-MyErwin | Where-Object { $_.MainWindowTitle -match "erwin.*\[.+\]" } | Select-Object -First 1
    $targetPid = $targetErwin.Id
    Write-Log "Target erwin PID=$targetPid"

    # Model is already detected via window title - no extra delay needed

    # --- Activate add-in via DLL injection ---
    try {
        Write-Log "Launching injector: $injectorExe"
        $proc = Start-Process -FilePath $injectorExe -ArgumentList "`"$triggerDll`"" -PassThru -WindowStyle Hidden -Wait
        if ($proc.ExitCode -eq 0) {
            Write-Log "Injector completed successfully (exit code 0)"
        } else {
            Write-Log "Injector exited with code $($proc.ExitCode)"
        }
    } catch {
        Write-Log "Injection failed: $_"
    }

    # --- Wait for THIS specific erwin to close (PID-based) ---
    Write-Log "Monitoring erwin PID=$targetPid..."
    while ($true) {
        Start-Sleep -Seconds 3
        $stillRunning = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if (-not $stillRunning) {
            Write-Log "erwin PID=$targetPid closed - resuming watch"
            break
        }
    }

    Start-Sleep -Seconds 2
}
