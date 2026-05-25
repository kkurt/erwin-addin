# Elite Soft Meta Add-In - WMI Event-Driven Auto-Start Watcher
# Detects erwin.exe start via WMI event, waits for model to open,
# then activates the add-in via PostMessage(WM_COMMAND, AddinCmdId).
# Runs as a hidden Scheduled Task at user logon.

$erwinName = "erwin.exe"
$mySessionId = (Get-Process -Id $PID).SessionId
$modelCheckIntervalSec = 1
$modelTimeoutSec = 300  # Give up after 5 minutes if no model opened
$fallbackPollSec = 30

# $installDir / injector / triggerDll variables removed 2026-05-26 along
# with the injection fallback path. The watcher now auto-loads the addin
# via PostMessage WM_COMMAND only - no executables to validate at startup.

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

# Detects "a model is open in erwin" from the main window title. erwin's MFC
# MDI host renders distinct title shapes:
#   maximized child   : "erwin DM - [Model1 : ER_Diagram_164]"   <- brackets
#   restored child    : "erwin DM - Model1"                       <- no brackets
#   Mart-only state   : "erwin DM - Mart://Mart/.../MetaRepo : v17"  <- no model yet!
#   no model          : "erwin Data Modeler" / "erwin DM"          <- no dash
#
# The Mart-only state is the false-positive that bit us 2026-05-25: erwin
# shows the Mart connection path in the title BEFORE any model is actually
# loaded into the MDI client. Posting WM_COMMAND in this state is a no-op
# because erwin's command map hasn't enabled addin commands yet (no active
# MDI child = addin entry is disabled in the standard MFC UPDATE_COMMAND_UI
# cycle).
#
# Detection rules:
#   1. Bracketed form (e.g. "[... : ER_Diagram_164]") = diagram active =>
#      addin command enabled. Safe to PostMessage.
#   2. Dash followed by Mart://-prefixed URL with no brackets => Mart
#      connection only, NO model yet. SKIP.
#   3. Dash followed by anything else (e.g. "erwin DM - Model1") =>
#      restored child with loaded model. Safe.
function Test-ErwinHasModel {
    param([string]$Title)
    if ([string]::IsNullOrWhiteSpace($Title)) { return $false }
    if ($Title -notmatch '^erwin\b.*\s-\s+\S') { return $false }
    if ($Title -match '\[[^\]]+\]')           { return $true }   # bracketed
    if ($Title -match '\s-\s+Mart://')        { return $false }  # Mart-only, no model
    return $true                                                  # restored child
}

# erwin DM r10 Add-In discovery only reads from HKCU. Watcher runs at every
# user's logon (Scheduled Task with INTERACTIVE-group trigger on Machine
# installs, per-user trigger on User installs), so each user gets their
# HKCU populated exactly once at first logon and self-healed on every
# subsequent logon if anything wipes it (profile reset, group policy, manual
# deletion). Hardcoded version 10.10 matches the addin's compile-time target
# - no version discovery needed (HKLM had stale 9.98 leftovers; HKCU was
# empty for fresh users; both were unreliable).
$erwinAddInVersion = "10.10"
# ProgID + menu display name renamed 2026-05-25 from "EliteSoft.Erwin.AddIn"
# + "Elite Soft Erwin Addin" to the names below. Self-heal also removes
# the legacy entry if it lingers from a pre-rename install.
$addInProgId = "EliteSoft.Meta.AddIn"
$addInDisplayName = "Elite Soft Meta Addin"
$legacyAddInDisplayName = "Elite Soft Erwin Addin"

function Register-HKCUAddIn {
    try {
        # First: clean up the legacy entry so we don't show two items in
        # Tools > Add-Ins after upgrade. Idempotent on fresh installs.
        $legacyPath = "HKCU:\SOFTWARE\erwin\Data Modeler\$erwinAddInVersion\Add-Ins\$legacyAddInDisplayName"
        if (Test-Path $legacyPath) {
            Remove-Item -LiteralPath $legacyPath -Recurse -Force -ErrorAction SilentlyContinue
            Write-Log "Legacy HKCU add-in entry '$legacyAddInDisplayName' removed (rename self-heal)"
        }

        $hkcuAddIn = "HKCU:\SOFTWARE\erwin\Data Modeler\$erwinAddInVersion\Add-Ins\$addInDisplayName"
        if (Test-Path $hkcuAddIn) { return }

        New-Item -Path $hkcuAddIn -Force -ErrorAction Stop | Out-Null
        Set-ItemProperty -Path $hkcuAddIn -Name "Menu Identifier" -Value 1 -Type DWord
        Set-ItemProperty -Path $hkcuAddIn -Name "ProgID" -Value $addInProgId -Type String
        Set-ItemProperty -Path $hkcuAddIn -Name "Invoke Method" -Value "Execute" -Type String
        Set-ItemProperty -Path $hkcuAddIn -Name "Invoke EXE" -Value 0 -Type DWord
        Write-Log "HKCU add-in entry written for erwin $erwinAddInVersion (per-user self-heal): '$addInDisplayName' -> $addInProgId"
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

        $erwinProc = Get-MyErwin | Where-Object { Test-ErwinHasModel $_.MainWindowTitle } | Select-Object -First 1
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

# Shared graceful-shutdown channel. installer/install-impl.ps1 signals this
# named Event to ask running watchers to exit cleanly before the install
# tears down COM/files. Replaces the old WMI Win32_Process.Terminate() call
# which triggered SEP's AGR.Terminate!g2 heuristic (`script kills another
# script host` malware pattern). Sleeps in the main loop poll this handle.
$shutdownEventName = 'Global\EliteSoft.ErwinAddIn.Watcher.Shutdown'
$shutdownEvent = $null
try {
    $created = $false
    $shutdownEvent = [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, $shutdownEventName, [ref]$created)
    Write-Log "Shutdown event '$shutdownEventName' acquired (created=$created)"
} catch {
    Write-Log "WARNING: could not open shutdown event '$shutdownEventName': $($_.Exception.Message)"
}

# Mirror HKLM add-in entries to this user's HKCU if missing (erwin DM r10
# requires per-user HKCU entry; HKLM alone does NOT make the add-in appear).
Register-HKCUAddIn

# Signal-then-exit-Stop-Process replaces the old WMI Terminate. New
# duplicates running in parallel are expected to honour the named event
# and quit on their own; only stragglers fall through to Stop-Process.
# This pattern matches install-impl.ps1's shutdown logic so SEP's
# AGR.Terminate heuristic never sees a powershell.exe -> powershell.exe
# Win32_Process.Terminate call from us.
try {
    if ($shutdownEvent) {
        [void]$shutdownEvent.Set()
        # We just signalled including OURSELVES - immediately reset so we
        # don't exit our own main loop. The other instances saw the Set
        # edge at WaitOne() and are exiting.
        Start-Sleep -Milliseconds 200
        [void]$shutdownEvent.Reset()
    }
    $duplicates = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'autostart-watcher' -and $_.ProcessId -ne $PID } |
        Select-Object -ExpandProperty ProcessId)
    foreach ($pidVal in $duplicates) {
        try {
            $alive = Get-Process -Id $pidVal -ErrorAction SilentlyContinue
            if (-not $alive) { continue }   # gracefully exited via event
            Write-Log "Killing duplicate watcher PID=$pidVal (Stop-Process; event did not reach it)"
            Stop-Process -Id $pidVal -Force -ErrorAction Stop
        } catch {
            Write-Log "WARNING: could not stop duplicate watcher PID=${pidVal}: $($_.Exception.Message)"
        }
    }
} catch { Write-Log "duplicate-watcher cleanup threw: $($_.Exception.Message)" }

# Injector pre-flight check removed 2026-05-26 - PostMessage auto-load
# path replaces the injection mechanism. No external binaries needed.

while ($true) {
  # Outer guard: any unhandled exception in the iteration body must NOT kill
  # the watcher. Watcher silently died once on 2026-05-02 and only logon-cycle
  # would have brought it back; we now log the failure and resume the loop so
  # transient WMI / Get-Process / Start-Process glitches can't take us out.
  try {
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
    $targetErwin = Get-MyErwin | Where-Object { Test-ErwinHasModel $_.MainWindowTitle } | Select-Object -First 1
    $targetPid = $targetErwin.Id
    Write-Log "Target erwin PID=$targetPid"

    # Model is already detected via window title - no extra delay needed

    # --- Activate add-in: PostMessage WM_COMMAND with saved cmd id ---
    #
    # The cmd id (1181 with one addin registered on r10.10) is dynamic per
    # registration but stable across erwin restarts. It's discovered+
    # persisted by WmCommandLogger in the addin's Execute(): on the first
    # ever manual click the subclass installs; on the second click it
    # captures the wParam and writes it to HKCU\Software\EliteSoft\
    # ErwinAddIn\Watcher\AddinCmdId. The watcher reads it from there.
    #
    # PostMessage WM_COMMAND is the SAME mechanism erwin uses internally
    # when the user clicks Tools > Add-Ins > Elite Soft Meta Addin -
    # plain cross-process Win32 message with scalar args, no memory ops,
    # no thread creation, ZERO AV heuristics.
    #
    # The legacy ErwinInjector.exe + TriggerDll.dll injection fallback was
    # removed 2026-05-26 (SEP SONAR.ProcHijack-flagged + obsolete now).
    # If savedCmdId is missing (fresh install before any manual click),
    # the watcher just logs and waits - user does one manual menu click
    # which persists the id and unblocks every subsequent session.
    $regPath = 'HKCU:\Software\EliteSoft\ErwinAddIn\Watcher'
    $savedCmdId = 0
    try {
        if (Test-Path $regPath) {
            $idVal = (Get-ItemProperty -Path $regPath -Name 'AddinCmdId' -ErrorAction SilentlyContinue).AddinCmdId
            if ($null -ne $idVal) { $savedCmdId = [int]$idVal }
        }
    } catch { Write-Log "Registry lookup failed: $($_.Exception.Message)" }

    $loaded = $false
    if ($savedCmdId -gt 0) {
        # AV-clean primary path: post WM_COMMAND to erwin's main window.
        # erwin's command map dispatches the same way as a manual menu click:
        # CoCreateInstance(EliteSoft.Erwin.AddIn) -> Invoke("Execute") -> addin
        # form appears. No injector binary involved.
        try {
            Add-Type -Language CSharp -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class WatcherWmPoster {
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
    public const uint SMTO_NORMAL          = 0x0;
    public const uint SMTO_ABORTIFHUNG     = 0x2;
    public const uint SMTO_NOTIMEOUTIFNOTHUNG = 0x8;
}
'@ -ErrorAction SilentlyContinue

            # Brief pre-flight before the first attempt. Empirically the
            # command map is ready ~1.5 s after the bracketed model
            # title appears for SMALL Mart models. For larger models
            # (verified: SQL_BUYUKMODEL took >8 s) erwin's UI thread is
            # busy parsing geometry and the message pump backlog
            # silently drops queued WM_COMMANDs. 2 s gets us the small
            # case quickly; the retry loop below covers the long tail
            # with 15 attempts (covers up to ~25 s of model-load time).
            Start-Sleep -Seconds 2
            $mainHwnd = $targetErwin.MainWindowHandle
            if ($mainHwnd -eq [IntPtr]::Zero) {
                Write-Log "MainWindowHandle is zero - giving erwin 1s to settle"
                Start-Sleep -Seconds 1
                $targetErwin.Refresh()
                $mainHwnd = $targetErwin.MainWindowHandle
            }
            if ($mainHwnd -eq [IntPtr]::Zero) {
                Write-Log "Still no MainWindowHandle - manual click needed"
            } else {
                # Snapshot the addin's wmcmd.log size BEFORE the post so
                # we can verify the addin actually loaded by observing a
                # NEW EXECUTE entry written by its Execute().
                $wmcmdLog = Join-Path $env:LOCALAPPDATA 'EliteSoft\ErwinAddIn\wmcmd.log'
                $logSizeBefore = 0
                if (Test-Path $wmcmdLog) { $logSizeBefore = (Get-Item $wmcmdLog).Length }

                # Retry loop. erwin's WindowProc + command map +
                # CoCreateInstance + Execute + WmCommandLogger.Persist
                # typically completes in well under 1 s when the command
                # map IS ready. If erwin's UI thread is busy parsing the
                # model (big Mart models can keep it busy for 15-25 s),
                # queued WM_COMMANDs are dropped silently. 15 attempts
                # x 1.5 s = ~22 s of patience covers the worst observed
                # model load (SQL_BUYUKMODEL on the dev box).
                $maxAttempts  = 15
                $perAttemptMs = 1500
                for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
                    Write-Log "PostMessage attempt ${attempt}: WM_COMMAND id=$savedCmdId to erwin PID=$targetPid hwnd=0x$('{0:X}' -f $mainHwnd.ToInt64())"
                    $ok = [WatcherWmPoster]::PostMessageW($mainHwnd, 0x0111, [IntPtr]$savedCmdId, [IntPtr]::Zero)
                    if (-not $ok) {
                        $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
                        Write-Log "  PostMessage Win32 err=$err"
                    }

                    Start-Sleep -Milliseconds $perAttemptMs

                    $logSizeNow = 0
                    if (Test-Path $wmcmdLog) { $logSizeNow = (Get-Item $wmcmdLog).Length }
                    if ($logSizeNow -gt $logSizeBefore) {
                        Write-Log "  Detected wmcmd.log growth ($logSizeBefore -> $logSizeNow) on attempt $attempt - addin loaded"
                        $loaded = $true
                        break
                    }
                    Write-Log "  No wmcmd.log change yet (size still $logSizeNow). Retrying in 2 s..."
                    # No extra inter-attempt backoff - the per-attempt wait
                    # IS the backoff (erwin had 1 s to process, didn't, retry now).
                }

                if (-not $loaded) {
                    Write-Log "PostMessage path exhausted $maxAttempts attempts ($($maxAttempts * $perAttemptMs / 1000) s) without observable addin load - falling through to monitoring + late-retry loop"
                }
            }
        } catch {
            Write-Log "PostMessage path threw: $($_.Exception.Message) - manual click needed"
        }
    } else {
        Write-Log "No saved AddinCmdId yet (HKCU\Software\EliteSoft\ErwinAddIn\Watcher\AddinCmdId missing)"
    }

    if (-not $loaded) {
        if ($savedCmdId -gt 0) {
            # We HAD a cmd id but all PostMessage attempts failed.
            # Usually means erwin's UI thread is still busy loading a
            # large model and dropped the queued messages. The late
            # retry loop in the monitoring phase below will keep trying.
            Write-Log "Addin not yet loaded; falling through to late-retry loop (model likely still mid-load)"
        } else {
            # No cmd id persisted yet -> first-ever install before any
            # manual click. Watcher cannot auto-load until
            # WmCommandLogger has captured + persisted the cmd id on
            # the user's first invocation.
            Write-Log "Addin NOT auto-loaded (no saved cmd id yet). User must click Tools > Add-Ins > Elite Soft Meta Addin manually once; WmCommandLogger persists the cmd id on the 2nd click and every subsequent session auto-loads."
        }
    }

    # --- Wait for THIS specific erwin to close (PID-based) ---
    # OR: if PostMessage path was attempted but addin didn't actually load
    # (e.g. command not enabled because title fired Test-ErwinHasModel too
    # early - false positive on transient Mart-only state), poll periodically
    # for the wmcmd.log to grow as a sign the user activated the addin via
    # menu OR for the title to become a real model state so we can retry
    # PostMessage. Otherwise the watcher just sits in this loop until erwin
    # closes, which is bad UX when the user opens a model 30 s later than
    # expected.
    Write-Log "Monitoring erwin PID=$targetPid..."
    $shouldRetryPostMessage = ($savedCmdId -gt 0 -and -not $loaded)
    $wmcmdLogPath = Join-Path $env:LOCALAPPDATA 'EliteSoft\ErwinAddIn\wmcmd.log'
    $logSizeAtMonitorStart = 0
    if (Test-Path $wmcmdLogPath) { $logSizeAtMonitorStart = (Get-Item $wmcmdLogPath).Length }

    while ($true) {
        Start-Sleep -Seconds 3
        $stillRunning = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if (-not $stillRunning) {
            Write-Log "erwin PID=$targetPid closed - resuming watch"
            break
        }

        if (-not $shouldRetryPostMessage) { continue }

        # If addin loaded itself (user clicked manually, or post message
        # fired late from earlier attempt), wmcmd.log grew. Stop retrying.
        $logSizeNow = 0
        if (Test-Path $wmcmdLogPath) { $logSizeNow = (Get-Item $wmcmdLogPath).Length }
        if ($logSizeNow -gt $logSizeAtMonitorStart) {
            Write-Log "Addin loaded externally (wmcmd.log grew $logSizeAtMonitorStart -> $logSizeNow) - no more retries"
            $shouldRetryPostMessage = $false
            continue
        }

        # Re-check title: was it a transient Mart-only state? If now real
        # (brackets present OR not a Mart:// URL), retry PostMessage.
        $erwinNow = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if (-not $erwinNow) { continue }
        $newTitle = $erwinNow.MainWindowTitle
        if (-not (Test-ErwinHasModel $newTitle)) { continue }

        Write-Log "Title now '$newTitle' looks real - retrying PostMessage WM_COMMAND id=$savedCmdId"
        $mainHwnd = $erwinNow.MainWindowHandle
        if ($mainHwnd -eq [IntPtr]::Zero) { continue }
        try {
            [void][WatcherWmPoster]::PostMessageW($mainHwnd, 0x0111, [IntPtr]$savedCmdId, [IntPtr]::Zero)
        } catch {
            Write-Log "Late retry PostMessage threw: $($_.Exception.Message)"
            continue
        }
        Start-Sleep -Seconds 2
        $logSizeNow = (Get-Item $wmcmdLogPath -ErrorAction SilentlyContinue).Length
        if ($logSizeNow -gt $logSizeAtMonitorStart) {
            Write-Log "  Late retry succeeded (wmcmd.log $logSizeAtMonitorStart -> $logSizeNow)"
            $shouldRetryPostMessage = $false
        } else {
            Write-Log "  Late retry still no growth; will poll again in 3 s"
        }
    }

    Start-Sleep -Seconds 2
  }
  catch {
    # Outer guard - swallowing here is intentional: alternative is silent script
    # death (which is exactly the bug we're fixing). Type+message logged so we
    # can still diagnose recurring failures.
    $errType = $_.Exception.GetType().Name
    $errMsg  = $_.Exception.Message
    Write-Log "Outer loop error: ${errType}: ${errMsg}"
    Start-Sleep -Seconds 2
  }
}
