<#
.SYNOPSIS
  One-time probe for the DDL queue-worker architecture decision.

  Linchpin 0b proved the Generate-DDL pipeline FAILS under a plain RDP DISCONNECT:
  the mouse simulation it relies on (GetCursorPos/SetCursorPos/mouse_event, the only
  way past XTP's synthetic-input filter) needs an ACTIVE interactive input desktop,
  which a disconnected RDP session does not have ("GetCursorPos failed" ->
  "Apply-to-Right did not register").

  Hypothesis: a session that lives on the physical CONSOLE keeps an active input
  desktop even with no monitor / no RDP, so the pipeline works unattended. This is the
  standard headless-UI-automation setup and the basis for the proposed permanent host
  (auto-logon console session).

  This script does NOT install anything permanent. It creates a SYSTEM scheduled task
  that runs `tscon <thisSessionId> /dest:console` (moving THIS RDP session to the
  console; your RDP drops but the session keeps running, active), lets you fire it, and
  removes it. tscon needs SeTcbPrivilege, which admins lack but SYSTEM has - hence the
  SYSTEM task (no psexec).

.USAGE  (run from an ELEVATED PowerShell, INSIDE the RDP session you want to test)
  1. .\ddl-worker-tscon-probe.ps1 -Setup
       Captures THIS session id and registers the SYSTEM task.
  2. In erwin (Mart-connected): open a cross-version model the add-in adopts
       (e.g. MetaRepo v4), then press Ctrl+Alt+B  (arms the 30s auto-fire).
  3. Within ~10s, run:  .\ddl-worker-tscon-probe.ps1 -Fire
       Your RDP DROPS immediately; the session moves to the console (active).
       ~30s after you armed, the pipeline fires on the console session.
  4. Stay out ~2-3 min, then RDP back in (reconnects to the same session).
  5. Inspect:
       %TEMP%\erwin-ddl-spike-status.json      (stage / ok)
       %TEMP%\erwin-alter-ddl-captured.sql     (should be non-empty, ~1693 chars)
       %TEMP%\erwin-addin-debug.log            (look for "DDL captured (N chars)"
                                                and NO "GetCursorPos failed")
     DDL produced under console (no RDP) -> hypothesis CONFIRMED -> adopt auto-logon
     console host. Still "GetCursorPos failed" -> console does NOT help -> pivot.
  6. .\ddl-worker-tscon-probe.ps1 -Cleanup
       Removes the task.

.NOTES
  - Requires admin (registers a SYSTEM scheduled task).
  - The session id is captured at -Setup time. Do NOT reconnect with a new session id
    between Setup and Fire (do Setup -> arm -> Fire in one sitting).
  - Fully reversible: RDP back in reconnects; -Cleanup deletes the task.
#>
[CmdletBinding()]
param(
    [switch]$Setup,
    [switch]$Fire,
    [switch]$Cleanup
)

$ErrorActionPreference = 'Stop'
$TaskName = 'DdlWorkerTsconProbe'

function Get-MySessionId {
    # SessionId of the interactive session running this script.
    return [System.Diagnostics.Process]::GetCurrentProcess().SessionId
}

if ($Setup) {
    $sid = Get-MySessionId
    Write-Host "[probe] this session id = $sid"
    if ($sid -eq 1 -or $sid -eq 0) {
        Write-Host "[probe] WARNING: session id is $sid - you may already be on the console; the probe is only meaningful from an RDP session." -ForegroundColor Yellow
    }
    $action    = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c tscon $sid /dest:console"
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Force | Out-Null
    Write-Host "[probe] SYSTEM task '$TaskName' registered: tscon $sid /dest:console"
    Write-Host "[probe] Now: arm Ctrl+Alt+B in the add-in, then run:  .\ddl-worker-tscon-probe.ps1 -Fire"
    return
}

if ($Fire) {
    Write-Host "[probe] firing tscon-to-console. RDP will drop now; the session keeps running on the console."
    Start-ScheduledTask -TaskName $TaskName
    return
}

if ($Cleanup) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "[probe] task '$TaskName' removed."
    return
}

Write-Host "Usage: .\ddl-worker-tscon-probe.ps1 -Setup | -Fire | -Cleanup   (see the comment header)"
