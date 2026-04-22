# Elite Soft Erwin Add-In - WMI Event-Driven Auto-Start Watcher
# Detects erwin.exe start via WMI event, waits for model to open,
# then activates add-in via DLL injection.
# Runs as a hidden Scheduled Task at user logon.

$erwinName = "erwin.exe"
$mySessionId = (Get-Process -Id $PID).SessionId
$modelCheckIntervalSec = 1
$modelTimeoutSec = 300  # Give up after 5 minutes if no model opened
$fallbackPollSec = 30

# Install dir = watcher's own directory.
# Works for both User scope (%LOCALAPPDATA%\EliteSoft\ErwinAddIn) and
# Machine scope (%ProgramFiles%\EliteSoft\ErwinAddIn) without modification.
$installDir = $PSScriptRoot
$injectorExe = Join-Path $installDir "ErwinInjector.exe"
$triggerDll = Join-Path $installDir "TriggerDll.dll"

# Log goes to per-user LOCALAPPDATA so each user on a Machine-scope install
# has their own log (and Program Files is read-only at runtime anyway).
$logDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn-Logs"
$logFile = Join-Path $logDir "autostart.log"

function Write-Log([string]$msg) {
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] $msg"
    try {
        if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
        Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue
    } catch { }
}

function Get-MyErwin {
    return Get-Process -Name "erwin" -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $mySessionId }
}

# erwin DM r10 Add-In discovery requires HKCU entries; HKLM alone is NOT
# sufficient (empirically verified). On Machine-scope installs the admin writes
# HKLM + their own HKCU, but every OTHER interactive user has empty HKCU for
# erwin, so the add-in never appears in their Tools menu. This function
# mirrors HKLM add-in entries to the CURRENT user's HKCU at watcher startup,
# making auto-registration first-logon-and-done per user.
function Register-HKCUAddIn {
    try {
        $erwinHKLM = "HKLM:\SOFTWARE\erwin\Data Modeler"
        if (-not (Test-Path $erwinHKLM)) { return }

        Get-ChildItem $erwinHKLM -ErrorAction SilentlyContinue | ForEach-Object {
            $version = $_.PSChildName
            $hklmAddIn = "$($_.PSPath)\Add-Ins\Elite Soft Erwin Addin"
            if (Test-Path $hklmAddIn) {
                $hkcuAddIn = "HKCU:\SOFTWARE\erwin\Data Modeler\$version\Add-Ins\Elite Soft Erwin Addin"
                if (-not (Test-Path $hkcuAddIn)) {
                    New-Item -Path $hkcuAddIn -Force -ErrorAction Stop | Out-Null
                    Set-ItemProperty -Path $hkcuAddIn -Name "Menu Identifier" -Value 1 -Type DWord
                    Set-ItemProperty -Path $hkcuAddIn -Name "ProgID" -Value "EliteSoft.Erwin.AddIn" -Type String
                    Set-ItemProperty -Path $hkcuAddIn -Name "Invoke Method" -Value "Execute" -Type String
                    Set-ItemProperty -Path $hkcuAddIn -Name "Invoke EXE" -Value 0 -Type DWord
                    Write-Log "HKCU add-in entry written for erwin $version (first-time per-user registration)"
                }
            }
        }
    } catch {
        Write-Log "HKCU add-in registration failed: $($_.Exception.Message)"
    }
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

# Mirror HKLM add-in entries to this user's HKCU if missing (erwin DM r10
# requires per-user HKCU entry; HKLM alone does NOT make the add-in appear).
Register-HKCUAddIn

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
