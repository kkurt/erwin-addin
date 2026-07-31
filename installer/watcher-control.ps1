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

function Wait-ForWatcherProcess {
    <#
    .SYNOPSIS
        Polls for a powershell.exe host running autostart-watcher.ps1.
    .DESCRIPTION
        Cold PowerShell start on a typical RDP host takes 3-10 s (verified
        2026-05-14 build-and-run telemetry), so callers must poll rather
        than check once.

        Used by the Scheduled Task route only. That route never learns the
        pid of the process Task Scheduler spawned, so it has to match on
        the command line, and that match is dangerously loose: it also hits
        another user's watcher on an RDP host (their task is named
        "... - <theirname>", so Stop-AddinWatcher can neither stop their
        task nor Stop-Process their host) and any straggler this install
        failed to kill. Latching onto one of those prints a green "Watcher
        running" and, since install-impl.ps1 now turns this result into the
        process exit code, would report a successful install to a user who
        has no watcher. ExcludePids is how the caller rules that out.
    .PARAMETER MaxWaitSec
        How long to poll. Default 20.
    .PARAMETER ExcludePids
        Pids that were already running before the caller tried to start
        one, and therefore cannot be the process it is waiting for.
    .OUTPUTS
        Microsoft.Management.Infrastructure.CimInstance | $null
    #>
    param(
        [int] $MaxWaitSec = 20,
        [int[]] $ExcludePids = @()
    )

    $proc = $null
    $waited = 0
    while ($waited -lt $MaxWaitSec -and -not $proc) {
        Start-Sleep -Seconds 1
        $waited++
        $proc = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -match 'autostart-watcher' -and $ExcludePids -notcontains $_.ProcessId } |
            Select-Object -First 1
    }
    if ($proc) {
        Write-Host "  Watcher running (PID=$($proc.ProcessId), startup took ${waited}s)" -ForegroundColor Green
    }
    return $proc
}

function Get-WatcherProcessIds {
    <#
    .SYNOPSIS
        Pids of every powershell.exe currently hosting autostart-watcher.ps1.
    .OUTPUTS
        [int[]] possibly empty.
    #>
    return @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -match 'autostart-watcher' } |
            Select-Object -ExpandProperty ProcessId)
}

function Start-AddinWatcherDirect {
    <#
    .SYNOPSIS
        Launches the watcher as a plain background process, with no
        Scheduled Task involved.
    .DESCRIPTION
        The watcher has zero dependency on Task Scheduler: the task's
        action is nothing more than
        `powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File <watcher>`
        and autostart-watcher.ps1 never references ScheduledTask or
        schtasks. So when task registration is refused (hardened task
        store, EDR persistence rule, orphan task owned by another
        principal), the CURRENT session can still get a working watcher.
        Before this existed, a task failure also cost the user the
        session they were standing in, because the install had already
        killed the previous watcher via Stop-AddinWatcher.
    .PARAMETER WatcherPath
        Full path to autostart-watcher.ps1.
    .PARAMETER MaxWaitSec
        How long to poll for the process. Default 20.
    .OUTPUTS
        Microsoft.Management.Infrastructure.CimInstance | $null
    #>
    param(
        [Parameter(Mandatory)][string] $WatcherPath,
        [int] $MaxWaitSec = 20
    )

    if (-not (Test-Path -LiteralPath $WatcherPath)) {
        Write-Host "  Cannot start watcher directly: $WatcherPath not found." -ForegroundColor Red
        return $null
    }

    $started = $null
    try {
        $started = Start-Process -FilePath 'powershell.exe' `
            -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass', '-File', "`"$WatcherPath`"" `
            -WindowStyle Hidden -PassThru -ErrorAction Stop
    }
    catch {
        Write-Host "  ERROR: could not start the watcher process directly:" -ForegroundColor Red
        Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }

    # Verify the process WE started, by its pid. Polling for "any
    # powershell.exe whose command line matches autostart-watcher" (what
    # Wait-ForWatcherProcess does, because the Scheduled Task route never
    # learns a pid) would happily latch onto a pre-existing watcher that
    # Stop-AddinWatcher failed to kill, print it as a success, and hide the
    # fact that the process just launched had already died. Observed
    # 2026-07-29: the helper reported PID 6740 while the process it actually
    # started was 41176.
    $watcherLog = Join-Path $env:LOCALAPPDATA 'EliteSoft\ErwinAddIn-Logs\autostart.log'
    $waited = 0
    while ($waited -lt $MaxWaitSec) {
        Start-Sleep -Seconds 1
        $waited++

        # Liveness comes from the Process handle we already own, never from
        # the CIM query. Get-CimInstance with SilentlyContinue returns $null
        # both for "no such process" and for "the query failed", so letting it
        # decide would report a perfectly healthy watcher as dead whenever a
        # loaded RDP host throws a transient WMI/RPC fault, and the caller
        # would then record a false install failure. HasExited cannot be
        # ambiguous, and it carries the exit code as a bonus.
        $started.Refresh()
        if ($started.HasExited) {
            Write-Host "  ERROR: the watcher process (PID=$($started.Id)) exited immediately after launch (exit code $($started.ExitCode))." -ForegroundColor Red
            Write-Host "    Check $watcherLog for the reason." -ForegroundColor Yellow
            return $null
        }

        $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$($started.Id)" -ErrorAction SilentlyContinue
        if ($proc -and $proc.CommandLine -match 'autostart-watcher') {
            Write-Host "  Watcher running (PID=$($proc.ProcessId), startup took ${waited}s)" -ForegroundColor Green
            return $proc
        }
    }

    # Out of poll budget with the process still alive. That is a healthy
    # watcher whose command line CIM would not confirm, not a failure, so
    # report it as running and hand back the Process object. No caller reads
    # properties off this return value, they only test it for truthiness.
    $started.Refresh()
    if (-not $started.HasExited) {
        Write-Host "  Watcher running (PID=$($started.Id)); could not confirm its command line via WMI." -ForegroundColor Yellow
        return $started
    }

    Write-Host "  ERROR: the watcher process (PID=$($started.Id)) is gone after ${MaxWaitSec}s." -ForegroundColor Red
    Write-Host "    Check $watcherLog for errors." -ForegroundColor Yellow
    return $null
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

    # Snapshot the watchers that already exist BEFORE triggering the task.
    # Anything in this set cannot be the process the task is about to spawn:
    # it is another user's watcher on an RDP host (out of our reach: their
    # task carries their own username suffix and Stop-Process is denied
    # cross-user) or a straggler Stop-AddinWatcher could not kill. Matching
    # one of those would print a green "Watcher running" for a user who has
    # no watcher, and install-impl.ps1 now turns this very result into the
    # installer's exit code.
    $preExisting = Get-WatcherProcessIds
    if ($preExisting.Count -gt 0) {
        Write-Host "  Ignoring $($preExisting.Count) watcher pid(s) that predate this start: $($preExisting -join ', ')" -ForegroundColor DarkGray
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

    $watcherProc = Wait-ForWatcherProcess -MaxWaitSec $MaxWaitSec -ExcludePids $preExisting

    if (-not $watcherProc) {
        $watcherLog = Join-Path $env:LOCALAPPDATA 'EliteSoft\ErwinAddIn-Logs\autostart.log'
        Write-Host "  WARNING: Watcher did not start within ${MaxWaitSec}s." -ForegroundColor Red
        Write-Host "    Check $watcherLog for errors." -ForegroundColor Yellow
        Write-Host "    Add-in will only auto-load after your next logon, or run:" -ForegroundColor Yellow
        Write-Host "      Start-ScheduledTask -TaskName '$TaskName'" -ForegroundColor Yellow
    }
    return $watcherProc
}
