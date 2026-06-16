<#
.SYNOPSIS
  Definitive, erwin-free probe of the ONE question behind linchpin 0b:
  Does THIS machine's CONSOLE session have an ACTIVE input desktop (so GetCursorPos
  succeeds) when no RDP client is attached?

  The Generate-DDL pipeline's mouse simulation calls GetCursorPos/SetCursorPos/
  mouse_event. Under a plain RDP disconnect these fail ("GetCursorPos failed"). The
  open question is whether redirecting the session to the console (tscon /dest:console)
  restores an active input desktop on this box. On a dedicated VM with a virtual display
  it does; on a headless / shared RDP server with no display device it may NOT.

  This script measures it directly, with NO erwin involved:
    1. Captures this session id and registers a SYSTEM scheduled task that runs
       `tscon <sid> /dest:console`.
    2. Starts logging GetCursorPos() (P/Invoke) + the active console session id every 2s
       to %TEMP%\ddl-cursor-probe.log.
    3. After -TsconAfterSec seconds, fires the tscon task: your RDP DROPS and the session
       moves to the console. The logging process keeps running on the console.
    4. Keeps logging for -Minutes total, then removes the task.

  Interpretation (read %TEMP%\ddl-cursor-probe.log after reconnecting):
    - GetCursorPos stays True after the tscon moment -> the console HAS an active input
      desktop on this box -> the erwin pipeline would work there -> adopt an auto-logon
      console host.
    - GetCursorPos flips to False after tscon -> the console has NO active desktop here
      (headless, no display) -> tscon does NOT help; need a VM with a virtual display
      (or a physical dummy-display plug), or a permanently-attached RDP session.

.USAGE  (ELEVATED PowerShell, inside the RDP session under test)
  .\ddl-worker-cursor-probe.ps1                 # defaults: 4 min total, tscon after 20s
  .\ddl-worker-cursor-probe.ps1 -Minutes 4 -TsconAfterSec 20
  After it fires you will be disconnected. Wait the remaining minutes, RDP back in,
  then open %TEMP%\ddl-cursor-probe.log.

.NOTES
  - Requires admin (registers a SYSTEM scheduled task; tscon needs SeTcbPrivilege).
  - No erwin, no add-in, no model. Pure desktop/input-state measurement.
  - Reversible: RDP back in reconnects; the task is auto-removed at the end (and on -Cleanup).
#>
[CmdletBinding()]
param(
    [int]$Minutes = 4,
    [int]$TsconAfterSec = 20,
    [switch]$Cleanup
)

$ErrorActionPreference = 'Stop'
$TaskName = 'DdlWorkerCursorProbeTscon'
$LogPath  = Join-Path $env:TEMP 'ddl-cursor-probe.log'

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeCursorProbe {
    [DllImport("user32.dll", SetLastError = true)] public static extern bool GetCursorPos(out POINT p);
    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
}
"@

if ($Cleanup) {
    try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false; Write-Host "[cursor-probe] task removed." }
    catch { Write-Host "[cursor-probe] no task to remove." }
    return
}

$sid = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
Write-Host "[cursor-probe] this session id = $sid ; logging to $LogPath"

# Register the SYSTEM tscon-to-console task for THIS session.
$action    = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c tscon $sid /dest:console"
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Force | Out-Null
Write-Host "[cursor-probe] SYSTEM task registered: tscon $sid /dest:console"

"=== cursor-probe start sid=$sid minutes=$Minutes tsconAfterSec=$TsconAfterSec ===" | Set-Content -Path $LogPath
$startUtc = [DateTime]::UtcNow
$endUtc   = $startUtc.AddMinutes($Minutes)
$fired    = $false

Write-Host "[cursor-probe] logging GetCursorPos every 2s. tscon-to-console fires in $TsconAfterSec s (your RDP will drop then)."

while ([DateTime]::UtcNow -lt $endUtc) {
    $pt = New-Object NativeCursorProbe+POINT
    $ok = [NativeCursorProbe]::GetCursorPos([ref]$pt)
    $consoleSid = [NativeCursorProbe]::WTSGetActiveConsoleSessionId()
    $onConsole = ($consoleSid -eq $sid)
    $line = "{0} sid={1} activeConsoleSid={2} onConsole={3} GetCursorPos={4} pos=({5},{6}) fired={7}" -f `
        (Get-Date -Format 'HH:mm:ss'), $sid, $consoleSid, $onConsole, $ok, $pt.X, $pt.Y, $fired
    Add-Content -Path $LogPath -Value $line

    if (-not $fired -and ([DateTime]::UtcNow -ge $startUtc.AddSeconds($TsconAfterSec))) {
        Add-Content -Path $LogPath -Value ("--- {0} firing tscon $sid /dest:console (RDP will drop) ---" -f (Get-Date -Format 'HH:mm:ss'))
        try { Start-ScheduledTask -TaskName $TaskName } catch { Add-Content -Path $LogPath -Value ("tscon fire error: " + $_.Exception.Message) }
        $fired = $true
    }
    Start-Sleep -Seconds 2
}

Add-Content -Path $LogPath -Value "=== cursor-probe end ==="
try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false } catch { }
Write-Host "[cursor-probe] done. Read $LogPath (after reconnecting)."
