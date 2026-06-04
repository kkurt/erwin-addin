# Elite Soft Erwin Add-In - Shared watcher control helpers.
#
# Provides Stop-AddinWatcher + Start-AddinWatcher used by both the
# end-user installer (installer/install-impl.ps1) and the dev loop
# (build-and-run.ps1). Lives in installer/ because it is shipped to
# end users alongside install-impl.ps1 (package.ps1 copies it next
# to install-impl.ps1).
#
# Why this exists:
#   1. The kill/restart sequence around the watcher Scheduled Task
#      has a known race - after the PS process is killed, Task
#      Scheduler can keep state="Running" for up to 30 s, so a
#      subsequent Start-ScheduledTask silently no-ops
#      (MultipleInstancesPolicy=IgnoreNew). Root cause traced
#      2026-05-15: 8+ install runs left the watcher dead because
#      the old install-impl.ps1 hit this race and swallowed the
#      start failure via -ErrorAction SilentlyContinue.
#   2. Three call sites (uninstall path, install path, dev rebuild)
#      had drifted into slightly different kill patterns (WMI
#      Terminate vs Stop-Process; with/without verify poll). One
#      shared helper closes the drift.
#
# SEP note: do NOT use WMI Win32_Process.Terminate to kill another
# powershell.exe - Symantec Endpoint flags it as AGR.Terminate!g2
# "Compromised Application" and force-restarts the parent. See memory
# reference_sep_agr_terminate_pattern for the 2026-05-25 incident.
# Stop-Process via PS cmdlet is safe.

function Stop-AddinWatcher {
    <#
    .SYNOPSIS
        Stops every running autostart-watcher PowerShell process and
        resyncs Task Scheduler state to Ready.
    .DESCRIPTION
        Three-stage stop:
          1. Signal the named event 'EliteSoft.ErwinAddIn.Watcher.Shutdown'
             so a watcher that polls WaitOne() can exit gracefully.
             Today's watcher uses Start-Sleep and ignores this, but
             keeping the signal is cheap future-proofing.
          2. Wait up to 3 s for the watcher PIDs to disappear.
          3. Force Stop-Process on any straggler (SEP-clean: native
             PS cmdlet, not WMI Terminate).
        Then issues Stop-ScheduledTask to resync SCM Running->Ready.
        Without step (3) -> (4) ordering, Start-ScheduledTask later
        silently no-ops.
    .PARAMETER TaskName
        Primary scheduled task name (per-user form expected).
    .PARAMETER LegacyTaskName
        Optional pre-per-user task name; also resynced if non-empty
        and different from TaskName.
    .OUTPUTS
        [int] count of PS processes that were running when the call
        started. 0 is the no-op case.
    #>
    param(
        [Parameter(Mandatory)][string] $TaskName,
        [string] $LegacyTaskName
    )

    $shutdownEventName = 'EliteSoft.ErwinAddIn.Watcher.Shutdown'
    $foundCount = 0
    try {
        $watcherPids = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -match 'autostart-watcher' } |
            Select-Object -ExpandProperty ProcessId)
        $foundCount = $watcherPids.Count

        if ($foundCount -gt 0) {
            Write-Host "  Found $foundCount running watcher PID(s): $($watcherPids -join ', ')" -ForegroundColor Gray

            $evt = $null
            try {
                $created = $false
                $evt = [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, $shutdownEventName, [ref]$created)
                [void]$evt.Set()
                Write-Host "  Signaled '$shutdownEventName'; waiting up to 3s for graceful exit" -ForegroundColor Gray
            }
            catch {
                # Cross-session named-event creation can fail on RDP hosts
                # when an existing handle is owned by another session; fall
                # straight through to Stop-Process. Logged at DarkGray so
                # the noise doesn't drown out real warnings.
                Write-Host "  (named-event signal failed: $($_.Exception.Message); falling back to Stop-Process)" -ForegroundColor DarkGray
            }

            $waitDeadline = (Get-Date).AddSeconds(3)
            while ((Get-Date) -lt $waitDeadline) {
                $stillAlive = @($watcherPids | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
                if ($stillAlive.Count -eq 0) { break }
                Start-Sleep -Milliseconds 200
            }

            $stragglers = @($watcherPids | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
            foreach ($pidVal in $stragglers) {
                try {
                    Stop-Process -Id $pidVal -Force -ErrorAction Stop
                    Write-Host "  Force-stopped straggler watcher PID=$pidVal (event did not reach it)" -ForegroundColor Yellow
                }
                catch {
                    Write-Host "  WARNING: could not stop watcher PID=${pidVal}: $($_.Exception.Message)" -ForegroundColor Yellow
                }
            }

            if ($evt) { try { $evt.Dispose() } catch {} }
        }

        # Resync SCM state Running->Ready so the subsequent
        # Start-ScheduledTask is actually honoured. Missing tasks are the
        # legitimate first-install case; SilentlyContinue swallows that.
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($LegacyTaskName -and $LegacyTaskName -ne $TaskName) {
            Stop-ScheduledTask -TaskName $LegacyTaskName -ErrorAction SilentlyContinue
        }
    }
    catch {
        # Outer guard: WMI / SCM transient glitch should not abort the
        # caller. Surface the exception type+message so a recurring
        # failure can still be diagnosed.
        Write-Host "  WARN: Stop-AddinWatcher outer error: $($_.Exception.GetType().Name): $($_.Exception.Message)" -ForegroundColor Yellow
    }
    return $foundCount
}

function Start-AddinWatcher {
    <#
    .SYNOPSIS
        Triggers the watcher Scheduled Task and verifies the PowerShell
        host actually came up.
    .DESCRIPTION
        - Detects the Disabled-task state up front (e.g. SEP quarantine
          loop, manual debug) and degrades to a clear warning instead
          of the cryptic 'task is disabled' Start-ScheduledTask error.
        - Polls up to MaxWaitSec seconds for a powershell.exe whose
          CommandLine matches 'autostart-watcher'. Cold PowerShell
          start on a typical RDP host takes 3-10 s (verified
          2026-05-14 build-and-run telemetry).
        - Returns the running watcher CimInstance (or $null if it
          never spawned) so callers can chain follow-up checks.
    .PARAMETER TaskName
        Scheduled task to start.
    .PARAMETER MaxWaitSec
        How long to poll for the watcher process. Default 20.
    .OUTPUTS
        Microsoft.Management.Infrastructure.CimInstance | $null
    #>
    param(
        [Parameter(Mandatory)][string] $TaskName,
        [int] $MaxWaitSec = 20
    )

    $taskState = (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue).State
    if ($taskState -eq 'Disabled') {
        Write-Host "  Watcher task '$TaskName' is Disabled - skipping start." -ForegroundColor Yellow
        Write-Host "  Add-in will NOT auto-load until the task is re-enabled:" -ForegroundColor Yellow
        Write-Host "    Enable-ScheduledTask -TaskName '$TaskName'" -ForegroundColor Yellow
        return $null
    }

    try {
        Start-ScheduledTask -TaskName $TaskName -ErrorAction Stop
    }
    catch {
        Write-Host "  ERROR: Start-ScheduledTask '$TaskName' failed:" -ForegroundColor Red
        Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  Add-in will only auto-load after your next logon." -ForegroundColor Yellow
        return $null
    }

    $watcherProc = $null
    $waited = 0
    while ($waited -lt $MaxWaitSec -and -not $watcherProc) {
        Start-Sleep -Seconds 1
        $waited++
        $watcherProc = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -match 'autostart-watcher' } | Select-Object -First 1
    }

    if ($watcherProc) {
        Write-Host "  Watcher running (PID=$($watcherProc.ProcessId), startup took ${waited}s)" -ForegroundColor Green
    } else {
        $watcherLog = Join-Path $env:LOCALAPPDATA 'EliteSoft\ErwinAddIn-Logs\autostart.log'
        Write-Host "  WARNING: Watcher did not start within ${MaxWaitSec}s." -ForegroundColor Red
        Write-Host "    Check $watcherLog for errors." -ForegroundColor Yellow
        Write-Host "    Add-in will only auto-load after your next logon, or run:" -ForegroundColor Yellow
        Write-Host "      Start-ScheduledTask -TaskName '$TaskName'" -ForegroundColor Yellow
    }
    return $watcherProc
}
