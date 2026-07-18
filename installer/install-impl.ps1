# Elite Soft Erwin Add-In - Install Script (implementation)
#
# End users should double-click install.bat / uninstall.bat next to this
# file - those .bat wrappers forward to this script with the right
# -NoProfile -ExecutionPolicy Bypass switches. install-impl.ps1 can also be
# invoked directly from PowerShell for CLI overrides (-DBHost X -DBName Y).
#
# Usage:
#   double-click install.bat    Install per-user (LOCALAPPDATA, no UAC needed)
#   double-click uninstall.bat  Uninstall (removes the per-user install)
#   .\install-impl.ps1 -?       Show this help (CLI use)
#
# Install is always per-user. Binaries land in %LOCALAPPDATA%\EliteSoft\ErwinAddIn,
# COM is registered under HKCU\Software\Classes, the auto-start Scheduled Task
# fires only for the installing user. The script never elevates and never asks
# for UAC.
#
# Add-In Manager registry entries are written to HKCU\SOFTWARE\erwin\Data Modeler\10.10
# because erwin DM r10 only discovers Add-Ins from HKCU (HKLM Add-Ins are
# invisible in the Tools menu - empirically verified).
#
# Bootstrap (MetaRepo DB connection) decision tree, in priority order. HKCU-only
# (HKLM support removed 2026-07-02 - the add-in reads HKCU only):
#   1. bootstrap.seed.json next to install-impl.ps1 contains DBHost+DBName -> write
#      HKCU silently, DPAPI CurrentUser, delete the seed file.
#   2. HKCU already has Bootstrap + new values supplied -> overwrite silently.
#   3. Otherwise -> interactive Read-Host prompts, write HKCU.
#
# See docs/INSTALL.md for the complete reference.
#
# Pre-flight: if a legacy Machine-scope install is detected (Program Files
# binaries OR HKLM COM CLSID), the script aborts with an actionable message
# instructing the user to run the old uninstaller as Admin first.
#
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'DBPassword',
    Justification='Plaintext required for DPAPI ProtectedData.Protect; the value is encrypted before any persistence and the seed file is deleted on success.')]
param(
    [switch]$Uninstall,
    # Uninstall-only. When set, the uninstall path also removes
    # HKCU\Software\EliteSoft\MetaRepo\Bootstrap (DB connection: host, port,
    # database, DPAPI-encrypted user/password) and Extension (UI prefs). When
    # absent, uninstall preserves these entries by default and asks
    # interactively; this matches the user expectation that a normal
    # uninstall / reinstall cycle should NOT silently wipe the DB
    # credentials they entered on first install. Set this only for a true
    # "wipe everything" uninstall (e.g. machine handoff, support case).
    [switch]$RemoveConnectionInfo,
    # MetaRepo bootstrap seed. Param names (-DBHost, -DBPort, -DBName, -DBUserName,
    # -DBPassword, -DBType) match the registry value names under
    # SOFTWARE\EliteSoft\MetaRepo\Bootstrap. PowerShell parameter binding is
    # case-insensitive so `$DBHost` accepts `-DBHost`, `-dbhost`, etc. $DBHost
    # is also distinct from PowerShell's $Host automatic variable, so no shadow
    # concern. When any of these are passed OR a bootstrap.seed.json file
    # ships next to install-impl.ps1, the values are written to
    # HKCU\Software\EliteSoft\MetaRepo\Bootstrap with DBUserName/DBPassword
    # DPAPI-encrypted under CurrentUser scope.
    [string]$DBHost,
    [string]$DBPort,
    [string]$DBName,
    [string]$DBUserName,
    [string]$DBPassword,
    [string]$DBType,
    # DDL-generator flavor install. When set (package.ps1 -DdlGenerator forwards
    # it), the watcher is configured to launch erwin with the bootstrap model
    # and this instance is the dedicated DDL queue worker. When NOT set (normal
    # install), the watcher's DdlGeneratorMode flag is explicitly cleared to 0,
    # so a machine that once ran the DDL-gen flavor does not leave the flag set
    # and start closing a real user's dialogs (bug 2026-07-14).
    [switch]$DdlGenerator,
    [Alias("?")]
    [switch]$Help
)

$ErrorActionPreference = "Stop"

# Shared watcher control helpers (Stop-AddinWatcher / Start-AddinWatcher).
# Dot-sourced here so both the Uninstall and Install paths can use them
# without conditional re-loading. The file lives next to install-impl.ps1
# both in the repo (installer/) and in the packaged install (publish dir).
. (Join-Path $PSScriptRoot 'watcher-control.ps1')

if ($Help) {
    Write-Host ""
    Write-Host "Elite Soft Erwin Add-In - Installer" -ForegroundColor Cyan
    Write-Host "===================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Usage (end user):" -ForegroundColor Yellow
    Write-Host "  Double-click install.bat   " -NoNewline; Write-Host "Install per-user (LOCALAPPDATA, no UAC)" -ForegroundColor Gray
    Write-Host "  Double-click uninstall.bat " -NoNewline; Write-Host "Uninstall (removes the per-user install)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Usage (CLI):" -ForegroundColor Yellow
    Write-Host "  .\install-impl.ps1                              " -NoNewline; Write-Host "Same as install.bat (no admin needed)" -ForegroundColor Gray
    Write-Host "  .\install-impl.ps1 -Uninstall                   " -NoNewline; Write-Host "Same as uninstall.bat (always wipes HKCU MetaRepo; no prompt)" -ForegroundColor Gray
    Write-Host "  .\install-impl.ps1 -Uninstall -RemoveConnectionInfo " -NoNewline; Write-Host "Same (switch kept for compat; wipe is now the default)" -ForegroundColor Gray
    Write-Host "  .\install-impl.ps1 -?                           " -NoNewline; Write-Host "Show this help" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Notes:" -ForegroundColor Yellow
    Write-Host "  - Install is always per-user. Binaries to LOCALAPPDATA, COM to HKCU\Software\Classes," -ForegroundColor Gray
    Write-Host "    scheduled task per-user. No UAC at any step." -ForegroundColor Gray
    Write-Host "  - Add-In Manager entry is always HKCU\SOFTWARE\erwin\Data Modeler\10.10 (erwin r10" -ForegroundColor Gray
    Write-Host "    reads HKCU only)." -ForegroundColor Gray
    Write-Host "  - Bootstrap (DB connection) is read from HKCU only at runtime; the installer" -ForegroundColor Gray
    Write-Host "    writes HKCU only (DPAPI CurrentUser). HKLM is no longer read." -ForegroundColor Gray
    Write-Host "  - Connection-secret encryption uses a FIXED key baked into the binaries (no -DataKey,"  -ForegroundColor Gray
    Write-Host "    no provisioning). The add-in reads its product license from the repository database." -ForegroundColor Gray
    Write-Host "  - A pre-existing Machine-scope install (Program Files + HKLM COM) aborts the script;" -ForegroundColor Gray
    Write-Host "    run the old uninstaller as Admin first, then re-run this." -ForegroundColor Gray
    Write-Host ""
    exit 0
}

# Per-user install paths.
$installDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn"
$comHostDll = "EliteSoft.Erwin.AddIn.comhost.dll"
# ProgID + menu display name. Both renamed 2026-05-25 from
# "EliteSoft.Erwin.AddIn" + "Elite Soft Erwin Addin" to the names below.
# Install script aggressively cleans up the OLD names to prevent erwin
# from showing two entries in Tools > Add-Ins or routing CLSIDFromProgID
# through stale registration.
$progId = "EliteSoft.Meta.AddIn"
$addInDisplayName = "Elite Soft Meta Addin"
$legacyProgId = "EliteSoft.Erwin.AddIn"
$legacyAddInDisplayName = "Elite Soft Erwin Addin"
# CLSID must mirror the [Guid(...)] attribute on ErwinAddIn class in
# ErwinAddIn.cs:17. Used by Register-ComUserScope which writes HKCU\Software\Classes
# directly (avoids regsvr32's HKLM-default and the resulting UAC prompt).
$clsid = '{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}'
$legacyMachineInstallDir = Join-Path $env:ProgramFiles "EliteSoft\ErwinAddIn"
$erwinRegBase = "SOFTWARE\erwin\Data Modeler"

# Legacy-install detector. Returns the list of detected legacy artifacts; the
# caller checks .Count -gt 0. HKLM bootstrap key alone is NOT a legacy-install
# trigger - corporate IT seeds it independently and we deliberately cooperate.
#
# We do NOT use "return ,$hits" because PS 5.1 wraps an empty array as a
# single-element outer array, which the caller then sees as Count==1 (the
# same bug that produced the phantom "PID= session=" warning earlier).
# Returning a typed object with an explicit .Hits property dodges the
# wrapper ambiguity in both PS 5.1 and PS 7+.
function Test-LegacyMachineInstall {
    $hits = New-Object System.Collections.ArrayList
    if (Test-Path -LiteralPath $legacyMachineInstallDir) {
        [void]$hits.Add("  - Binary folder: $legacyMachineInstallDir")
    }
    if (Test-Path "HKLM:\Software\Classes\CLSID\$clsid") {
        [void]$hits.Add("  - HKLM COM registration: HKLM\Software\Classes\CLSID\$clsid")
    }
    return [pscustomobject]@{ Hits = @($hits) }
}

# Per-user COM registration without UAC. Replicates the layout that
# regsvr32 + .NET comhost.dll's DllRegisterServer would produce in
# HKLM\Software\Classes, but writes it to HKCU\Software\Classes instead.
# erwin's CLSIDFromProgID resolves through HKEY_CLASSES_ROOT (HKCU then HKLM)
# so the addin is discoverable for the current user without touching HKLM.
# Returns $true on success, $false on failure.
#
# Defensive cleanup first: a leftover CLSID/ProgID key from any prior install
# (especially one created by an elevated process) can carry a restrictive ACL
# that blocks New-Item with "Requested registry access is not allowed". We
# delete those keys before recreating them. Failures during the delete are
# surfaced (no silent swallow) - they tell us exactly which subkey can't be
# touched and why.
function Register-ComUserScope([string]$clsid, [string]$progId, [string]$comHostPath) {
    $clsidBase   = "HKCU:\Software\Classes\CLSID\$clsid"
    $inprocPath  = "$clsidBase\InProcServer32"
    $clsidProgId = "$clsidBase\ProgId"
    $progIdBase  = "HKCU:\Software\Classes\$progId"
    $progIdClsid = "$progIdBase\CLSID"

    # Pre-clean. Remove-Item with -ErrorAction Stop converts an ACL block into
    # a catchable exception so we can dump diagnostics instead of letting
    # New-Item fail with a less informative message later.
    foreach ($staleKey in @($clsidBase, $progIdBase)) {
        if (Test-Path -LiteralPath $staleKey) {
            try {
                Remove-Item -LiteralPath $staleKey -Recurse -Force -ErrorAction Stop
                Write-Host "  Cleared stale $staleKey" -ForegroundColor Gray
            } catch {
                Write-Host "  ERROR: could not delete stale key $staleKey" -ForegroundColor Red
                Write-Host "    Exception: $($_.Exception.GetType().FullName)" -ForegroundColor Red
                Write-Host "    Message:   $($_.Exception.Message)" -ForegroundColor Red
                try {
                    $acl = Get-Acl -LiteralPath $staleKey -ErrorAction Stop
                    Write-Host "    Owner:     $($acl.Owner)" -ForegroundColor Red
                    Write-Host "    Access (Identity / Rights / Type):" -ForegroundColor Red
                    $acl.Access | ForEach-Object {
                        Write-Host "      $($_.IdentityReference) / $($_.RegistryRights) / $($_.AccessControlType)" -ForegroundColor DarkRed
                    }
                } catch {
                    Write-Host "    (Get-Acl also failed: $($_.Exception.Message))" -ForegroundColor DarkRed
                }
                Write-Host "    Recovery: open regedit, navigate to $staleKey, take ownership of the key (right-click > Permissions > Advanced > Owner: change to your user) and grant Full Control, then re-run install.bat." -ForegroundColor Yellow
                return $false
            }
        }
    }

    try {
        New-Item -Path $inprocPath   -Force -ErrorAction Stop | Out-Null
        New-Item -Path $clsidProgId  -Force -ErrorAction Stop | Out-Null
        New-Item -Path $progIdClsid  -Force -ErrorAction Stop | Out-Null

        # CLSID label (humans see it in OLE viewers; not load-critical).
        Set-ItemProperty -Path $clsidBase   -Name "(Default)"     -Value $progId       -ErrorAction Stop
        # InProcServer32 = absolute path to the .NET comhost.dll. comhost.dll
        # finds the runtime config via sibling .runtimeconfig.json.
        Set-ItemProperty -Path $inprocPath  -Name "(Default)"      -Value $comHostPath -ErrorAction Stop
        Set-ItemProperty -Path $inprocPath  -Name "ThreadingModel" -Value "Both"       -ErrorAction Stop
        # ProgId backref under CLSID
        Set-ItemProperty -Path $clsidProgId -Name "(Default)"      -Value $progId      -ErrorAction Stop
        # ProgId -> CLSID forward lookup (used by CLSIDFromProgID)
        Set-ItemProperty -Path $progIdBase  -Name "(Default)"      -Value $progId      -ErrorAction Stop
        Set-ItemProperty -Path $progIdClsid -Name "(Default)"      -Value $clsid       -ErrorAction Stop

        return $true
    } catch {
        Write-Host "  ERROR: HKCU COM registration failed." -ForegroundColor Red
        Write-Host "    Exception: $($_.Exception.GetType().FullName)" -ForegroundColor Red
        Write-Host "    Message:   $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "    Target:    $($_.TargetObject)" -ForegroundColor Red
        Write-Host "    CLSID key: $clsidBase" -ForegroundColor Red
        Write-Host "    ProgID key: $progIdBase" -ForegroundColor Red
        # Surface the parent ACL so we can tell whether it is the corporate
        # GPO locking down HKCU\Software\Classes itself or just a stale
        # subkey ACL.
        try {
            $parentAcl = Get-Acl -LiteralPath "HKCU:\Software\Classes" -ErrorAction Stop
            Write-Host "    HKCU\Software\Classes Access:" -ForegroundColor Red
            $parentAcl.Access | ForEach-Object {
                Write-Host "      $($_.IdentityReference) / $($_.RegistryRights) / $($_.AccessControlType)" -ForegroundColor DarkRed
            }
        } catch {
            Write-Host "    (Get-Acl HKCU\Software\Classes failed: $($_.Exception.Message))" -ForegroundColor DarkRed
        }
        return $false
    }
}

# Symmetric removal of the per-user CLSID/ProgID entries written by
# Register-ComUserScope. Failures are non-fatal (Remove-Item with
# -ErrorAction SilentlyContinue) so partial state from a stale install
# can't block the rest of uninstall.
function Unregister-ComUserScope([string]$clsid, [string]$progId) {
    $clsidBase  = "HKCU:\Software\Classes\CLSID\$clsid"
    $progIdBase = "HKCU:\Software\Classes\$progId"
    if (Test-Path $clsidBase) {
        Remove-Item -Path $clsidBase -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $progIdBase) {
        Remove-Item -Path $progIdBase -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Stop-WatcherProcesses extracted into installer/watcher-control.ps1 as
# Stop-AddinWatcher (which also resyncs Task Scheduler state). The helper
# is dot-sourced at the top of this script and used by both the install
# and uninstall paths below.

# Add-In is hardcoded to a single erwin DM target. Reading versions from
# HKLM was unreliable: stale 9.98 keys leaked from old installs and a fresh
# user (no first-erwin-run yet) had no HKCU subkeys, so install silently
# skipped HKCU writes. erwin DM r10 only reads Add-In entries from HKCU
# anyway (HKLM Add-Ins are invisible in Tools menu - empirically verified),
# so we standardize on HKCU + hardcoded version.
$erwinVersion = "10.10"

if ($Uninstall) {
    Write-Host "=== Uninstalling Elite Soft Erwin Add-In ===" -ForegroundColor Cyan

    # Legacy Machine-scope artifacts cannot be removed by a non-elevated
    # uninstall. Surface them so the user knows to run the OLD uninstaller
    # as Admin separately; proceed with the per-user cleanup either way so
    # User-scope state still gets torn down even when legacy state lingers.
    $legacy = Test-LegacyMachineInstall
    if ($legacy.Hits.Count -gt 0) {
        Write-Host ""
        Write-Host "  Legacy Machine-scope artifacts detected (this uninstaller does NOT touch them):" -ForegroundColor Yellow
        $legacy.Hits | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
        Write-Host "  Run the old install.ps1 -Uninstall as Administrator to clean those up." -ForegroundColor Yellow
        Write-Host ""
    }

    if (-not (Test-Path -LiteralPath $installDir)) {
        Write-Host "  No per-user install found at $installDir" -ForegroundColor Yellow
    }

    # Stop running watcher BEFORE removing files / scheduled task so the
    # orphan watcher process can't (a) lock autostart-watcher.ps1 against
    # Remove-Item, (b) keep PostMessage-ing into erwin after the addin is
    # gone, or (c) get picked up by the next install's pre-flight as a
    # stale process. Matches the install path's stop order.
    #
    # Task name vars are declared here (early) instead of at the
    # Unregister-ScheduledTask point so Stop-AddinWatcher gets the same
    # names and can resync SCM state before we Unregister.
    $userTaskName = "EliteSoft Erwin AddIn AutoStart - $env:USERNAME"
    $sharedTaskName = "EliteSoft Erwin AddIn AutoStart"
    Write-Host "Stopping autostart watcher..." -ForegroundColor Yellow
    Stop-AddinWatcher -TaskName $userTaskName -LegacyTaskName $sharedTaskName | Out-Null

    Write-Host "Unregistering COM component (HKCU)..." -ForegroundColor Yellow
    Unregister-ComUserScope -clsid $clsid -progId $progId
    # Also nuke the legacy "EliteSoft.Erwin.AddIn" ProgID key (pre-2026-05-25
    # rename). Harmless when absent on fresh installs.
    $legacyProgIdBase = "HKCU:\Software\Classes\$legacyProgId"
    if (Test-Path $legacyProgIdBase) {
        Remove-Item -LiteralPath $legacyProgIdBase -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  Legacy ProgID '$legacyProgId' removed" -ForegroundColor Gray
    }
    Write-Host "  COM unregistered" -ForegroundColor Green

    # Remove erwin Add-In Manager entry from HKCU only. HKLM Add-In entries
    # (if any exist from corporate provisioning) are invisible to erwin r10
    # anyway, so leaving them alone has no effect; touching HKLM would need
    # elevation we deliberately avoid.
    #
    # Sweep BOTH the current display name and the legacy one to guarantee
    # a clean menu (otherwise users see "Elite Soft Erwin Addin" + "Elite
    # Soft Meta Addin" side-by-side on upgrade boxes).
    $base = "HKCU:\$erwinRegBase"
    if (Test-Path $base) {
        Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
            $verKey = $_
            foreach ($entryName in @($addInDisplayName, $legacyAddInDisplayName)) {
                $addInPath = "$($verKey.PSPath)\Add-Ins\$entryName"
                if (-not (Test-Path $addInPath)) { continue }
                try {
                    Remove-Item $addInPath -Recurse -Force -ErrorAction Stop
                    Write-Host "  Removed '$entryName' from HKCU\$($verKey.PSChildName)" -ForegroundColor Green
                } catch {
                    Write-Host "  WARNING: could not remove '$entryName' from HKCU\$($verKey.PSChildName): $($_.Exception.Message)" -ForegroundColor Yellow
                }
            }
        }
    }

    # Remove the per-user Scheduled Task. The legacy shared name might also
    # exist from much older installs - try it too, harmless if absent.
    # $userTaskName / $sharedTaskName were declared earlier (Stop block) so
    # the stop/resync and unregister phases agree on the exact same task
    # names; do not redeclare here.
    Unregister-ScheduledTask -TaskName $userTaskName -Confirm:$false -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $sharedTaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "  Removed Scheduled Task(s) for autostart" -ForegroundColor Green

    # HKCU MetaRepo entries: Bootstrap holds the user's DB connection (DBHost,
    # DBPort, DBName, DPAPI-encrypted DBUserName/DBPassword); Extension holds
    # UI prefs. These are now ALWAYS removed on uninstall (owner decision
    # 2026-07-14: question-free uninstall). Upgrades run through install.bat -
    # which re-seeds from bootstrap.seed.json - not through uninstall, so the
    # old "preserve on upgrade" prompt is gone. HKLM bootstrap (if present) is
    # corporate IT territory and is never touched here.
    $metaRepoBase = "HKCU:\Software\EliteSoft\MetaRepo"
    $bootstrapPath = "$metaRepoBase\Bootstrap"
    $extensionPath = "$metaRepoBase\Extension"
    $hasBootstrap = Test-Path $bootstrapPath
    $hasExtension = Test-Path $extensionPath

    if ($hasBootstrap -or $hasExtension) {
        # Uninstall ALWAYS removes the MetaRepo HKCU entries (DB connection + UI
        # prefs). The confirm prompt was removed 2026-07-14 (owner decision:
        # question-free uninstall). Upgrades run through install.bat - which
        # re-seeds from bootstrap.seed.json - not through uninstall, so there is
        # nothing to preserve here. -RemoveConnectionInfo is kept for CLI compat
        # but removal is now unconditional (the switch is a no-op).
        Write-Host ""
        Write-Host "  Removing MetaRepo HKCU registry entries (DB connection + UI prefs):" -ForegroundColor Yellow
        if ($hasBootstrap) {
            try {
                $bs = Get-ItemProperty -LiteralPath $bootstrapPath -ErrorAction Stop
                $bsType = if ($bs.PSObject.Properties['DBType']) { $bs.DBType } else { '(missing)' }
                $bsHost = if ($bs.PSObject.Properties['DBHost']) { $bs.DBHost } else { '(missing)' }
                $bsPort = if ($bs.PSObject.Properties['DBPort']) { $bs.DBPort } else { '(missing)' }
                $bsName = if ($bs.PSObject.Properties['DBName']) { $bs.DBName } else { '(missing)' }
                Write-Host "    Bootstrap (DB connection): DBType=$bsType DBHost=$bsHost DBPort=$bsPort DBName=$bsName" -ForegroundColor Gray
            } catch {
                Write-Host "    Bootstrap (DB connection): (could not read - $($_.Exception.Message))" -ForegroundColor DarkGray
            }
        }
        if ($hasExtension) {
            Write-Host "    Extension (UI prefs / per-user state)" -ForegroundColor Gray
        }

        foreach ($p in @($bootstrapPath, $extensionPath)) {
            if (-not (Test-Path $p)) { continue }
            try {
                Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop
                Write-Host "  Removed $p" -ForegroundColor Green
            } catch {
                Write-Host "  WARNING: Could not remove ${p}: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
    }

    # Remove per-user install dir.
    if (Test-Path -LiteralPath $installDir) {
        try {
            Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction Stop
            Write-Host "  Removed $installDir" -ForegroundColor Green
        } catch {
            Write-Host "  WARNING: Could not remove ${installDir}: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    # Remove per-user watcher logs.
    $watcherLogDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn-Logs"
    if (Test-Path $watcherLogDir) {
        Remove-Item $watcherLogDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "`nUninstall complete!" -ForegroundColor Green
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 0
}

# === INSTALL ===
Write-Host "=== Installing Elite Soft Erwin Add-In (User scope) ===" -ForegroundColor Cyan
$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Legacy Machine-scope install detector. If the previous installer ran
# with -Scope Machine at some point, Program Files binaries and/or HKLM COM
# CLSID registration still live on this machine. A User-scope install on
# top would leave them dangling - HKLM CLSID would shadow our HKCU
# registration (HKCU wins, but only if both exist), Program Files binaries
# rot, and uninstall semantics get tangled. Abort cleanly with an
# actionable message rather than silently overlaying.
$legacy = Test-LegacyMachineInstall
if ($legacy.Hits.Count -gt 0) {
    Write-Host ""
    Write-Host "  Detected an existing Machine-scope install:" -ForegroundColor Red
    $legacy.Hits | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host ""
    Write-Host "  User-scope install would leave dangling Program Files binaries and HKLM COM" -ForegroundColor Yellow
    Write-Host "  registration. Please run the OLD install.ps1 -Uninstall as Administrator on this" -ForegroundColor Yellow
    Write-Host "  machine first, then re-run this script." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Press any key to exit..." -ForegroundColor Gray
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}

# Check .NET 10 Desktop Runtime
$dotnetOk = $false
try {
    $runtimes = & dotnet --list-runtimes 2>&1
    if ($runtimes -match "Microsoft\.WindowsDesktop\.App 10\.") {
        $dotnetOk = $true
    }
} catch { }

if (-not $dotnetOk) {
    Write-Host ""
    Write-Host "  ERROR: .NET 10 Desktop Runtime is not installed!" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Download from:" -ForegroundColor Yellow
    Write-Host "  https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Select: .NET Desktop Runtime 10.x (Windows x64)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  After installing, run this script again." -ForegroundColor Gray
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}

# Stop ALL watcher processes (prevents duplicates, unlocks COM host DLL).
# Kill+SCM-resync is centralised in installer/watcher-control.ps1; see
# that file's header for the race-condition rationale.
$taskName = "EliteSoft Erwin AddIn AutoStart - $env:USERNAME"
$legacyTaskName = "EliteSoft Erwin AddIn AutoStart"
Stop-AddinWatcher -TaskName $taskName -LegacyTaskName $legacyTaskName | Out-Null

# Only OUR OWN erwin (same user + same session) can lock the install dir
# (LOCALAPPDATA / Program Files) and the COM host DLL we are about to (re)
# register. Cross-user erwin processes run under a different file system
# view (their own LOCALAPPDATA) and a different HKCU hive, so they can't
# interfere with this install. We filter on both SessionId AND owner so a
# colleague's erwin on the same box (terminal-server, fast-user-switching,
# RDP) doesn't bounce the install with a false-positive warning.
# Get-CimInstance is used instead of Get-Process because the legacy
# Get-WmiObject path returns deserialized objects on PS7 whose GetOwner()
# method is stripped.
$currentSessionId = (Get-Process -Id $PID).SessionId
$myUser = $env:USERNAME

# Returns a single [pscustomobject] with .Own and .Other arrays. We used to
# return ",$own, ,$other" hoping for tuple-style destructuring, but PS 5.1
# wraps each as a single-element outer array; when $own is empty the caller
# saw $own.Count == 1 (wrapper count) and the foreach iterated once with
# $p == the empty inner array, printing "PID= session=" (a real prod
# symptom). A single typed object is unambiguous in both PS 5.1 and PS 7+.
function Get-ErwinProcesses-MineOrOthers {
    $own = New-Object System.Collections.ArrayList
    $other = New-Object System.Collections.ArrayList
    try {
        $procs = Get-CimInstance Win32_Process -Filter "Name='erwin.exe'" -ErrorAction SilentlyContinue
        foreach ($p in $procs) {
            $owner = $null
            try {
                $r = Invoke-CimMethod -InputObject $p -MethodName GetOwner -ErrorAction Stop
                if ($r -and $r.ReturnValue -eq 0) { $owner = $r.User }
            } catch { }
            # Get-Process under another user's session may return AccessDenied;
            # falling through to $sid = -1 keeps the "mine" check well-defined
            # without polluting state.
            $sid = -1
            try { $sid = (Get-Process -Id $p.ProcessId -ErrorAction Stop).SessionId } catch { }
            $mine = ($owner -and $owner -ieq $myUser -and $sid -eq $currentSessionId)
            # Surface "?" instead of an empty string when GetOwner was denied,
            # so the log line reads cleanly: "user=? session=3" beats
            # "user= session=3".
            $ownerDisplay = if ([string]::IsNullOrEmpty($owner)) { "?" } else { $owner }
            $row = [pscustomobject]@{ Pid = $p.ProcessId; Owner = $ownerDisplay; Session = $sid }
            if ($mine) { [void]$own.Add($row) } else { [void]$other.Add($row) }
        }
    } catch { }
    return [pscustomobject]@{ Own = @($own); Other = @($other) }
}

$procs = Get-ErwinProcesses-MineOrOthers
if ($procs.Other.Count -gt 0) {
    Write-Host ""
    Write-Host "  Note: $($procs.Other.Count) erwin process(es) belonging to other users on this machine are running:" -ForegroundColor Gray
    foreach ($p in $procs.Other) {
        Write-Host "    PID=$($p.Pid) user=$($p.Owner) session=$($p.Session) (ignored - different LOCALAPPDATA / HKCU)" -ForegroundColor DarkGray
    }
}

if ($procs.Own.Count -gt 0) {
    Write-Host ""
    Write-Host "  WARNING: erwin is running in YOUR session (user=$myUser) and will lock the install dir:" -ForegroundColor Red
    foreach ($p in $procs.Own) { Write-Host "    PID=$($p.Pid) session=$($p.Session)" -ForegroundColor Yellow }
    Write-Host "  Close YOUR erwin and press any key to continue, or Ctrl+C to cancel." -ForegroundColor Yellow
    Write-Host ""
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')

    $procs = Get-ErwinProcesses-MineOrOthers
    if ($procs.Own.Count -gt 0) {
        Write-Host "  Your erwin is still running. Aborting installation." -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }
}

# Step 1: Strip stale HKCU COM entries + copy files. Per-user install only
# touches HKCU and LOCALAPPDATA, so no UAC anywhere here. Unregister is now
# unconditional: HKCU\Software\Classes CLSID/ProgID keys can linger after a
# prior install even when $installDir was deleted by hand, and a leftover key
# with a restrictive ACL will then make Step 2's Register fail with
# "Requested registry access is not allowed". Calling Unregister here is the
# cheap belt-and-suspenders cleanup; Register-ComUserScope has its own retry
# on top, but pre-cleaning lets us see the actual ACL diagnostics earlier.
Write-Host "`n[1/4] Copying files to $installDir..." -ForegroundColor Yellow
Write-Host "  Unregistering any stale COM component entries..." -ForegroundColor Gray
Unregister-ComUserScope -clsid $clsid -progId $progId
if (Test-Path $installDir) {
    Remove-Item "$installDir\*" -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

# bootstrap.seed.json is consumed in-place from $sourceDir in Step 4 and
# then deleted from $sourceDir on a successful HKCU write. Copying it into
# $installDir would leak the plaintext credentials into a permanent
# location, so it stays out of the install tree.
#
# install.bat, uninstall.bat, and install-impl.ps1 ARE copied through so
# the user can still uninstall after deleting the extracted ZIP - the .bat
# wrappers will find install-impl.ps1 sitting next to them in
# %LOCALAPPDATA%\EliteSoft\ErwinAddIn.
#
# Push-Location flips cwd to $sourceDir so the "*" wildcard resolves with
# the literal path semantics that Copy-Item otherwise can't apply to paths
# containing [ ] (e.g. "MetaAddin [TTKOM-77]") - PowerShell's path resolver
# treats brackets as wildcards and rejects them.
Push-Location -LiteralPath $sourceDir
try {
    Copy-Item -Path "*" -Destination $installDir -Recurse -Force -Exclude "bootstrap.seed.json"
} finally {
    Pop-Location
}
$count = (Get-ChildItem -LiteralPath $installDir -Recurse -File).Count
Write-Host "  Copied $count files" -ForegroundColor Green
Write-Host "  Install scope: User (LOCALAPPDATA)" -ForegroundColor Green

# Step 2: Register COM in HKCU\Software\Classes. No regsvr32, no UAC.
Write-Host "`n[2/4] Registering COM component..." -ForegroundColor Yellow
$comHost = Join-Path $installDir $comHostDll
if (-not (Register-ComUserScope -clsid $clsid -progId $progId -comHostPath $comHost)) {
    Write-Host "  Ensure .NET 10 Desktop Runtime is installed and the comhost DLL is intact." -ForegroundColor Yellow
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}
Write-Host "  COM registered (HKCU\Software\Classes; no UAC needed)" -ForegroundColor Green

# Step 3: Register in erwin Add-In Manager (always HKCU + hardcoded version)
#
# erwin DM r10 only reads Add-In entries from HKCU - HKLM Add-Ins are invisible
# in the Tools menu (empirically verified, see memory reference_erwin_addin_hkcu_required).
# Reading erwin's installed version from HKLM was unreliable too (stale 9.98
# subkeys leaked from old installs; brand-new users without a first-erwin-run
# had no HKCU subkeys), so we standardize: ALWAYS write HKCU\$erwinRegBase\$erwinVersion.
# Per-user HKCU population for OTHER interactive users is the watcher's job
# (autostart-watcher.ps1 runs at each user's logon and self-heals their HKCU).
Write-Host "`n[3/4] Registering in erwin Add-In Manager (HKCU\$erwinRegBase\$erwinVersion)..." -ForegroundColor Yellow

# Sweep stale Add-In entries from BOTH hives so old installs (HKLM Add-Ins,
# HKCU 9.98 leftovers, mismatched scope, etc.) cannot mask the canonical
# HKCU\10.10 entry we are about to write.
# Sweep BOTH the current and legacy display names so an upgrade from the
# old "Elite Soft Erwin Addin" registration never leaves a duplicate
# Tools > Add-Ins entry pointing at a stale ProgID.
foreach ($staleHive in @("HKLM", "HKCU")) {
    $staleBase = "${staleHive}:\$erwinRegBase"
    if (-not (Test-Path $staleBase)) { continue }
    Get-ChildItem $staleBase -ErrorAction SilentlyContinue | ForEach-Object {
        $verKey = $_
        foreach ($entryName in @($addInDisplayName, $legacyAddInDisplayName)) {
            $stalePath = "$($verKey.PSPath)\Add-Ins\$entryName"
            if (-not (Test-Path $stalePath)) { continue }
            try {
                Remove-Item $stalePath -Recurse -Force -ErrorAction Stop
                Write-Host "  Removed stale $staleHive\$($verKey.PSChildName) entry '$entryName'" -ForegroundColor Gray
            } catch {
                if ($staleHive -eq "HKLM") {
                    Write-Host "  WARNING: Could not remove stale HKLM\$($verKey.PSChildName) '$entryName' (needs Admin); delete manually." -ForegroundColor Yellow
                } else {
                    Write-Host "  WARNING: Could not remove stale HKCU\$($verKey.PSChildName) '$entryName': $($_.Exception.Message)" -ForegroundColor Yellow
                }
            }
        }
    }
}

# Also nuke the legacy COM ProgID HKCU\Software\Classes\EliteSoft.Erwin.AddIn
# so erwin's CLSIDFromProgID for the OLD name resolves to nothing (defensive:
# even if a stale Add-In Manager entry survives somewhere, it points at a
# ProgID that no longer exists and erwin will not attempt activation).
$legacyProgIdBase = "HKCU:\Software\Classes\$legacyProgId"
if (Test-Path $legacyProgIdBase) {
    Remove-Item -LiteralPath $legacyProgIdBase -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Removed legacy COM ProgID '$legacyProgId'" -ForegroundColor Gray
}

# AddinCmdId pre-seed (2026-05-26): on r10.10 with one addin registered,
# erwin deterministically assigns WM_COMMAND id 1181 to our Tools>Add-Ins
# entry. Pre-seeding lets the watcher PostMessage immediately on the
# first session - new users get auto-load with ZERO manual clicks. If
# the user has additional add-ins registered (corporate Spy, custom etc),
# the id may differ; in that case the first PostMessage silently no-ops,
# the user does one manual menu click, and WmCommandLogger.MarkExecuteEntry
# overwrites the registry with the captured-correct id.
#
# Only seed if no value exists (don't clobber a previously-captured id
# from a prior install that might have a different addin-set in play).
$watcherRegPath = 'HKCU:\Software\EliteSoft\ErwinAddIn\Watcher'
if (-not (Test-Path $watcherRegPath)) {
    New-Item -Path $watcherRegPath -Force | Out-Null
}
$existingCmdId = (Get-ItemProperty -Path $watcherRegPath -Name 'AddinCmdId' -ErrorAction SilentlyContinue).AddinCmdId
if ($null -eq $existingCmdId) {
    Set-ItemProperty -Path $watcherRegPath -Name 'AddinCmdId' -Value 1181 -Type DWord
    Write-Host "  Pre-seeded AddinCmdId=1181 (empirically stable on r10.10; WmCommandLogger self-heals if different)" -ForegroundColor Gray
} else {
    Write-Host "  AddinCmdId=$existingCmdId already set (preserved)" -ForegroundColor Gray
}

# DDL-generator watcher mode. This flag decides whether the watcher launches
# erwin with a bootstrap model and drives its startup dialogs. It MUST be
# written on every install (not just when set): a normal install has to CLEAR
# it to 0 so a machine that previously ran the DDL-gen flavor stops the watcher
# from touching a real user's dialogs (bug 2026-07-14 - a normal user's
# "Connect to Mart" dialog was auto-closed by a leftover DDL-gen watcher).
#
# The flavor is detected by the bootstrap model shipped in the package
# (package.ps1 -DdlGenerator copies ddlgen-bootstrap.erwin next to this
# script). install.bat cannot pass -DdlGenerator, so the file IS the marker;
# an explicit -DdlGenerator switch also forces it for CLI installs.
$bootstrapSrc = Join-Path $PSScriptRoot 'ddlgen-bootstrap.erwin'
$isDdlGen = $DdlGenerator -or (Test-Path $bootstrapSrc)
if ($isDdlGen) {
    $bootstrapDst = Join-Path $installDir 'ddlgen-bootstrap.erwin'
    if (Test-Path $bootstrapSrc) {
        Copy-Item $bootstrapSrc $bootstrapDst -Force
        try { (Get-Item $bootstrapDst).IsReadOnly = $true } catch { }
    } else {
        Write-Host "  WARNING: DDL-gen bootstrap model missing at $bootstrapSrc" -ForegroundColor Yellow
    }
    $erwinExe = @('C:\Program Files\erwin\Data Modeler r10\erwin.exe',
                  'C:\Program Files (x86)\erwin\Data Modeler r10\erwin.exe') |
                Where-Object { Test-Path $_ } | Select-Object -First 1
    Set-ItemProperty -Path $watcherRegPath -Name 'DdlGeneratorMode' -Value 1 -Type DWord
    Set-ItemProperty -Path $watcherRegPath -Name 'BootstrapModelPath' -Value $bootstrapDst -Type String
    if ($erwinExe) { Set-ItemProperty -Path $watcherRegPath -Name 'ErwinExePath' -Value $erwinExe -Type String }
    Write-Host "  DDL-generator watcher mode ON (mode=1, bootstrap=$bootstrapDst)" -ForegroundColor Cyan
} else {
    Set-ItemProperty -Path $watcherRegPath -Name 'DdlGeneratorMode' -Value 0 -Type DWord
    Write-Host "  Normal watcher mode (DdlGeneratorMode=0 - watcher only auto-loads the add-in)" -ForegroundColor Gray
}

# Write the canonical HKCU entry. New-Item -Force creates every missing parent
# (Software\erwin\Data Modeler\10.10\Add-Ins\$addInDisplayName) in one
# call, so this works on a fresh user who has never opened erwin before.
$addInPath = "HKCU:\$erwinRegBase\$erwinVersion\Add-Ins\$addInDisplayName"
if (-not (Test-Path $addInPath)) {
    New-Item -Path $addInPath -Force | Out-Null
}
Set-ItemProperty $addInPath -Name "Menu Identifier" -Value 1 -Type DWord
Set-ItemProperty $addInPath -Name "ProgID"          -Value $progId -Type String
Set-ItemProperty $addInPath -Name "Invoke Method"   -Value "Execute" -Type String
Set-ItemProperty $addInPath -Name "Invoke EXE"      -Value 0 -Type DWord
Write-Host "  erwin $erwinVersion (HKCU) - OK ('$addInDisplayName' -> $progId)" -ForegroundColor Green

# Step 3b: COM pre-warm. Without this, the FIRST erwin launch after install
# often hides our menu entry: erwin's Add-In Manager enumerates registered
# add-ins, runs an internal validation that includes a CoCreateInstance probe,
# and Windows' first cold COM activation (load comhost.dll -> bootstrap
# CoreCLR -> load main DLL -> instantiate type) can race or fail in a way
# that erwin treats as "invalid, hide entry". A subsequent erwin restart
# usually works because Windows has warmed the activation cache.
#
# Doing CoCreateInstance ourselves NOW (against $progId, with all registry
# chains just freshly written) primes the cache so erwin's first launch
# finds everything pre-validated and lists the addin. We hold the COM
# object for ~0.5 s to ensure the activation completes, then release.
#
# Verified 2026-05-26: the "addin missing on first launch, present on
# restart" symptom hit a fresh-install user; this pre-warm makes the
# first launch behave like a restart.
Write-Host "  Pre-warming COM activation..." -ForegroundColor Gray
try {
    $tmpObj = New-Object -ComObject $progId -ErrorAction Stop
    Start-Sleep -Milliseconds 500
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($tmpObj) | Out-Null
    Remove-Variable tmpObj -ErrorAction SilentlyContinue
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    Write-Host "  COM pre-warm OK - addin will appear in erwin Tools > Add-Ins on next launch" -ForegroundColor Green
} catch {
    Write-Host "  COM pre-warm FAILED: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "  Hresult: 0x$('{0:X8}' -f $_.Exception.HResult)" -ForegroundColor Yellow
    Write-Host "  Possible causes: .NET 10 Desktop Runtime missing, comhost.dll not properly built, or main DLL inaccessible" -ForegroundColor Yellow
    Write-Host "  Addin may still appear if you restart erwin manually after install." -ForegroundColor Yellow
}

# Step 4: MetaRepo bootstrap (DB connection seed). HKCU-only (HKLM support
# removed 2026-07-02: the add-in reads HKCU only, so seeding or reading HKLM
# would just shadow the user's HKCU config). Decision tree, in priority order:
#   1. bootstrap.seed.json next to install-impl.ps1 has DBHost AND DBName -> write
#      HKCU silently (DPAPI CurrentUser), delete the seed file.
#   2. HKCU already has a populated Bootstrap key + new values supplied -> show
#      old vs new and overwrite silently (no prompt; owner decision 2026-07-14).
#   3. None of the above -> interactive Read-Host prompts, then write HKCU.
#
# DPAPI scope at write time is always CurrentUser (install-impl.ps1 only ever
# writes HKCU).
Write-Host "`n[4/4] Configuring MetaRepo bootstrap..." -ForegroundColor Yellow

$hkcuBootstrap = "HKCU:\Software\EliteSoft\MetaRepo\Bootstrap"

# Same configured-test the add-in uses: key exists AND DBHost non-empty AND
# DBName non-empty. Legacy value names (Host/Database/Username/Password) do
# NOT count - those packages predate the DB* rename and need re-install.
function Test-BootstrapConfigured([string]$registryPath) {
    if (-not (Test-Path -LiteralPath $registryPath)) { return $false }
    $hostVal = $null; $nameVal = $null
    try { $hostVal = (Get-ItemProperty -LiteralPath $registryPath -Name "DBHost" -ErrorAction Stop).DBHost } catch { }
    try { $nameVal = (Get-ItemProperty -LiteralPath $registryPath -Name "DBName" -ErrorAction Stop).DBName } catch { }
    return -not [string]::IsNullOrEmpty($hostVal) -and -not [string]::IsNullOrEmpty($nameVal)
}

# HKCU-only: resolve CLI args + seed file + (later) interactive prompts, then write HKCU.
$seedPath = Join-Path $sourceDir "bootstrap.seed.json"
$seedFromFile = $null
if (Test-Path -LiteralPath $seedPath) {
    try {
        $seedFromFile = Get-Content -LiteralPath $seedPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Write-Host "  Found bootstrap.seed.json (packaged seed)" -ForegroundColor Gray
    } catch {
        Write-Host "  WARNING: bootstrap.seed.json present but unreadable: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "           Delete it manually if it is corrupt; CLI args still work." -ForegroundColor Gray
    }
}

function Resolve-Field([string]$cliValue, [string]$seedKey, [string]$default) {
    if (-not [string]::IsNullOrEmpty($cliValue)) { return $cliValue }
    if ($null -ne $seedFromFile -and $null -ne $seedFromFile.$seedKey -and -not [string]::IsNullOrEmpty([string]$seedFromFile.$seedKey)) {
        return [string]$seedFromFile.$seedKey
    }
    return $default
}

# Read a password without echo. Read-Host -AsSecureString works on PS5 and
# PS7; the BSTR round-trip is needed because PS5 lacks
# ConvertFrom-SecureString -AsPlainText. Empty Enter returns "".
function Read-PlainPassword([string]$prompt) {
    $sec = Read-Host -Prompt $prompt -AsSecureString
    if ($null -eq $sec -or $sec.Length -eq 0) { return "" }
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try {
        return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    } finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Read-WithDefault([string]$prompt, [string]$default) {
    $shown = if ([string]::IsNullOrEmpty($default)) { "" } else { " [$default]" }
    $val = Read-Host -Prompt "$prompt$shown"
    if ([string]::IsNullOrEmpty($val)) { return $default }
    return $val
}

# Default DB port by type. Mirrors MetaShared DbTypes.GetDefaultPort
# (MetaShared/Models/DbType.cs) so that when neither the CLI nor the seed
# supplied a port, the prompt/HKCU value is the driver's real default for the
# chosen DBType (Oracle 1521, PostgreSQL 5432) instead of always MSSQL's 1433.
# HkcuBootstrapReader applies the same fallback at runtime; keep the two in sync.
function Get-DefaultPort([string]$dbType) {
    switch (("" + $dbType).ToUpperInvariant()) {
        "ORACLE"     { return "1521" }
        "POSTGRESQL" { return "5432" }
        default      { return "1433" }
    }
}

$bsDBHost     = Resolve-Field $DBHost     "DBHost"     ""
$bsDBName     = Resolve-Field $DBName     "DBName"     ""
$bsDBUserName = Resolve-Field $DBUserName "DBUserName" ""
$bsDBPassword = Resolve-Field $DBPassword "DBPassword" ""
$bsDBType     = Resolve-Field $DBType     "DBType"     "MSSQL"
# Port resolution: an explicit port (CLI -DBPort or a non-empty seed DBPort)
# always wins. When none was supplied, derive the default from the resolved
# DBType (Oracle 1521, PostgreSQL 5432, MSSQL 1433) instead of hardcoding 1433.
# $portExplicit is reused in the interactive branch below to re-derive the
# default if the user changes DBType at the prompt.
$portExplicit = (-not [string]::IsNullOrEmpty($DBPort)) -or ($null -ne $seedFromFile -and -not [string]::IsNullOrEmpty([string]$seedFromFile.DBPort))
$bsDBPort     = if ($portExplicit) { Resolve-Field $DBPort "DBPort" "1433" } else { Get-DefaultPort $bsDBType }

$haveHostAndName = -not [string]::IsNullOrEmpty($bsDBHost) -and -not [string]::IsNullOrEmpty($bsDBName)

if (-not $haveHostAndName) {
    # No CLI args, no seed file. Before prompting, honour the
    # decision tree at the top of Step 4: if HKCU already has a populated
    # Bootstrap from a prior install, keep it as-is. The add-in will read
    # from HKCU at runtime, and re-prompting the user every time they
    # re-run install.bat would be a regression (pre-2026-05-14 the
    # prompt fired even when HKCU was complete, see user report on
    # 2026-05-14: "dev re-install asked for credentials even though HKCU
    # was already populated").
    if (Test-BootstrapConfigured $hkcuBootstrap) {
        try {
            $hkcu = Get-ItemProperty -LiteralPath $hkcuBootstrap -ErrorAction Stop
            $hkcuType = if ($hkcu.PSObject.Properties['DBType']) { $hkcu.DBType } else { '(missing)' }
            $hkcuPort = if ($hkcu.PSObject.Properties['DBPort']) { $hkcu.DBPort } else { '(missing)' }
            Write-Host "  HKCU bootstrap already populated; keeping existing config." -ForegroundColor Green
            Write-Host "    DBType=$hkcuType DBHost=$($hkcu.DBHost) DBPort=$hkcuPort DBName=$($hkcu.DBName)" -ForegroundColor Gray
            Write-Host "    (Pass -DBHost/-DBName on the command line or drop a bootstrap.seed.json next to install.bat to overwrite.)" -ForegroundColor Gray
        } catch {
            Write-Host "  HKCU bootstrap detected; keeping existing config (could not enumerate: $($_.Exception.Message))." -ForegroundColor Green
        }
        $bsDBHost = ""  # sentinel: write block below sees empty Host/Name and skips
        $bsDBName = ""
    } else {
        # Branch 4: nothing anywhere. Ask the user.
        Write-Host "  No HKCU config; no CLI/seed values supplied." -ForegroundColor Cyan
        Write-Host "  Please enter DB connection info now (will be written to HKCU):" -ForegroundColor Cyan
        Write-Host ""
        $bsDBType     = Read-WithDefault "  DB Type (MSSQL/ORACLE/POSTGRESQL)" $bsDBType
        $bsDBHost     = Read-WithDefault "  DB Host" $bsDBHost
        # No explicit port supplied: the shown default follows the DBType the user
        # just picked (Oracle -> 1521, PostgreSQL -> 5432) rather than a stale 1433.
        if (-not $portExplicit) { $bsDBPort = Get-DefaultPort $bsDBType }
        $bsDBPort     = Read-WithDefault "  DB Port" $bsDBPort
        $bsDBName     = Read-WithDefault "  DB Name (catalog)" $bsDBName
        $bsDBUserName = Read-WithDefault "  DB UserName" $bsDBUserName
        $bsDBPassword = Read-PlainPassword "  DB Password"
        Write-Host ""

        if ([string]::IsNullOrEmpty($bsDBHost) -or [string]::IsNullOrEmpty($bsDBName)) {
            Write-Host "  Skipped bootstrap (DBHost or DBName left empty)." -ForegroundColor Yellow
            Write-Host "  The add-in will surface 'No configuration found' on first load." -ForegroundColor Gray
            Write-Host "  Re-run install.bat with the missing values to seed HKCU." -ForegroundColor Gray
            $bsDBHost = ""
            $bsDBName = ""
        }
    }
}

if (-not [string]::IsNullOrEmpty($bsDBHost) -and -not [string]::IsNullOrEmpty($bsDBName)) {
    # Windows PowerShell 5.1 (.NET Framework) does NOT auto-load
    # System.Security.dll, which is where DataProtectionScope and
    # ProtectedData live. Without this Add-Type the next line fails with
    # "Unable to find type [System.Security.Cryptography.DataProtectionScope]".
    # PS 7 / .NET Core resolves it implicitly, so the bug only surfaces on
    # production user machines running the in-box PS 5.1.
    Add-Type -AssemblyName System.Security

    # install-impl.ps1 only ever writes HKCU, so DPAPI scope is always
    # CurrentUser.
    $dpapiScope = [System.Security.Cryptography.DataProtectionScope]::CurrentUser

    function Protect-WithDpapi([string]$plaintext, [System.Security.Cryptography.DataProtectionScope]$scope) {
        if ([string]::IsNullOrEmpty($plaintext)) { return "" }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($plaintext)
        $protected = [System.Security.Cryptography.ProtectedData]::Protect($bytes, $null, $scope)
        return [System.Convert]::ToBase64String($protected)
    }

    # Existing HKCU config is OVERWRITTEN unconditionally when new DB values were
    # supplied (CLI / seed / interactive). The confirm prompt was removed
    # 2026-07-14 (owner decision: question-free install). This branch only runs
    # when new host+name are present; a no-arg re-run on an already-configured
    # machine takes the "keep existing" path above and never reaches here, so it
    # still preserves the user's config.
    $writeBootstrap = $true
    if (Test-Path -LiteralPath $hkcuBootstrap) {
        Write-Host ""
        Write-Host "  Overwriting existing Bootstrap config in HKCU\Software\EliteSoft\MetaRepo\Bootstrap:" -ForegroundColor Yellow
        try {
            $existing = Get-ItemProperty -LiteralPath $hkcuBootstrap -ErrorAction Stop
            $oldType = if ($existing.PSObject.Properties['DBType']) { $existing.DBType } else { '(missing)' }
            $oldHost = if ($existing.PSObject.Properties['DBHost']) { $existing.DBHost } else { '(missing)' }
            $oldPort = if ($existing.PSObject.Properties['DBPort']) { $existing.DBPort } else { '(missing)' }
            $oldName = if ($existing.PSObject.Properties['DBName']) { $existing.DBName } else { '(missing)' }
            Write-Host "    Old: DBType=$oldType DBHost=$oldHost DBPort=$oldPort DBName=$oldName" -ForegroundColor Gray
        } catch {
            Write-Host "    Old: (could not read - $($_.Exception.Message))" -ForegroundColor DarkGray
        }
        Write-Host "    New: DBType=$bsDBType DBHost=$bsDBHost DBPort=$bsDBPort DBName=$bsDBName" -ForegroundColor Gray
        # Wipe the whole key so legacy value names (Host/Database/...) get cleared
        # rather than masked by the new DB* names.
        try {
            Remove-Item -LiteralPath $hkcuBootstrap -Recurse -Force -ErrorAction Stop
            Write-Host "  Cleared existing HKCU\Software\EliteSoft\MetaRepo\Bootstrap" -ForegroundColor Gray
        } catch {
            Write-Host "  WARNING: Could not clear existing HKCU Bootstrap key: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host "           Proceeding to overwrite individual values; legacy names may remain." -ForegroundColor Yellow
        }
    }

    if ($writeBootstrap) {
        if (-not (Test-Path -LiteralPath $hkcuBootstrap)) {
            New-Item -Path $hkcuBootstrap -Force | Out-Null
        }

        Set-ItemProperty $hkcuBootstrap -Name "DBType"     -Value $bsDBType     -Type String
        Set-ItemProperty $hkcuBootstrap -Name "DBHost"     -Value $bsDBHost     -Type String
        Set-ItemProperty $hkcuBootstrap -Name "DBPort"     -Value $bsDBPort     -Type String
        Set-ItemProperty $hkcuBootstrap -Name "DBName"     -Value $bsDBName     -Type String
        Set-ItemProperty $hkcuBootstrap -Name "DBUserName" -Value (Protect-WithDpapi $bsDBUserName $dpapiScope) -Type String
        Set-ItemProperty $hkcuBootstrap -Name "DBPassword" -Value (Protect-WithDpapi $bsDBPassword $dpapiScope) -Type String
        # The connection-secret key is now a FIXED key baked into the binaries (owner decision 2026-07);
        # nothing is written to HKCU for it. Best-effort remove any stale DataKey left by an older installer.
        Remove-ItemProperty $hkcuBootstrap -Name "DataKey" -ErrorAction SilentlyContinue
        Write-Host "  Bootstrap written to HKCU\Software\EliteSoft\MetaRepo\Bootstrap" -ForegroundColor Green
        Write-Host "    DBType=$bsDBType DBHost=$bsDBHost DBPort=$bsDBPort DBName=$bsDBName" -ForegroundColor Gray
        Write-Host "    DBUserName/DBPassword DPAPI-encrypted (CurrentUser)" -ForegroundColor Gray

        # Best-effort delete of the plaintext seed file. Failure is
        # non-fatal (a locked file just stays on disk; admin can remove
        # it manually).
        if (Test-Path -LiteralPath $seedPath) {
            try {
                Remove-Item -LiteralPath $seedPath -Force -ErrorAction Stop
                Write-Host "  Removed plaintext bootstrap.seed.json after successful registry write" -ForegroundColor Gray
            } catch {
                Write-Host "  WARNING: Could not remove $seedPath after successful seed: $($_.Exception.Message)" -ForegroundColor Yellow
                Write-Host "           Delete it manually - it contains plaintext DB credentials." -ForegroundColor Yellow
            }
        }
    }
}

# Configure auto-start watcher (DLL injection based).
# Per-user task name. A previous install run as Admin would create a task
# owned by Administrator; a subsequent normal-PS install run as Emre then
# could not unregister or overwrite it (cross-user access denied), so
# Register kept failing with "already exists". Suffixing with $env:USERNAME
# makes each user's autostart task independent.
Write-Host "`n[Watcher] Configuring auto-start watcher..." -ForegroundColor Yellow
# $taskName / $legacyTaskName were declared earlier (Stop block) so that the
# pre-Stop and post-Start phases agree on the exact same task name; do not
# redeclare here.
$watcherSource = Join-Path $sourceDir "autostart-watcher.ps1"
$watcherTarget = Join-Path $installDir "autostart-watcher.ps1"

# Legacy injection components ErwinInjector.exe / TriggerDll.dll removed
# 2026-05-26 (SEP SONAR.ProcHijack-flagged, obsolete after PostMessage
# auto-load). No pre-flight check needed - watcher only requires
# autostart-watcher.ps1 (verified by the Test-Path check immediately
# below) and the addin COM registration (Step 2 above).

if (Test-Path -LiteralPath $watcherSource) {
    [System.IO.File]::Copy($watcherSource, $watcherTarget, $true)

    # Aggressively remove ANY existing task with this name in ANY task folder
    # before re-registering. Required because:
    #   1) Get/Unregister-ScheduledTask without -TaskPath searches the root
    #      folder only; a task that ended up under a sub-folder (legacy
    #      installer, group policy, manual import) hides from the cmdlet but
    #      still blocks Register-ScheduledTask with ERROR_FILE_EXISTS
    #      ("Cannot create a file when that file already exists").
    #   2) A stale/corrupt task XML in C:\Windows\System32\Tasks\ can also
    #      block registration while remaining invisible to the modern cmdlet;
    #      legacy schtasks.exe usually succeeds where the cmdlet fails.
    # We enumerate across all paths, try the cmdlet first, then fall back to
    # schtasks.exe per task, and finish with a blind schtasks safety net.
    $namesToClean = @($taskName)
    if ($legacyTaskName -ne $taskName) { $namesToClean += $legacyTaskName }

    foreach ($cleanupName in $namesToClean) {
        $foundTasks = @()
        try {
            $foundTasks = @(Get-ScheduledTask -TaskName $cleanupName -TaskPath \* -ErrorAction SilentlyContinue)
        }
        catch {
            # Get-ScheduledTask itself can throw on broken WMI state; ignore
            # and let the schtasks fallback below handle it.
        }

        foreach ($t in $foundTasks) {
            $fullPath = "$($t.TaskPath)$($t.TaskName)"
            try {
                Unregister-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath -Confirm:$false -ErrorAction Stop
                Write-Host "  Removed pre-existing task '$fullPath'" -ForegroundColor Gray
            }
            catch {
                Write-Host "  Cmdlet unregister failed for '$fullPath' ($($_.Exception.Message)); trying schtasks" -ForegroundColor Yellow
                # try/catch wrapper: schtasks.exe returns non-zero when the
                # task does not exist; under $ErrorActionPreference='Stop'
                # plus $PSNativeCommandUseErrorActionPreference (PS 7.3+)
                # that bubbles up as a terminating error. We treat it as a
                # best-effort cleanup, so swallow.
                try { $null = & schtasks.exe /Delete /TN $fullPath /F 2>&1 } catch { }
            }
        }

        # Blind schtasks pass: even if Get-ScheduledTask saw nothing, force
        # a delete by name. Exit code is ignored on purpose - "task not
        # found" (1) is the normal happy case here, and the cmdlet may
        # have left a stale XML behind that schtasks can still clear.
        try { $null = & schtasks.exe /Delete /TN $cleanupName /F 2>&1 } catch { }
    }

    $action = New-ScheduledTaskAction -Execute "powershell.exe" `
        -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$watcherTarget`""

    $registered = $null
    try {
        # User-scope task: fire only for the installing user. Single instance
        # is fine because only this user's session ever triggers it.
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero)
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
        $registered = Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
            -Settings $settings -Description "Auto-starts Elite Soft Erwin Add-In when erwin opens" -ErrorAction Stop
        Write-Host "  Scheduled Task '$taskName' created (User: $env:USERNAME)" -ForegroundColor Green
        Write-Host "  Add-in will auto-load when erwin starts" -ForegroundColor Gray
    }
    catch {
        Write-Host "  ERROR: Could not register Scheduled Task '$taskName':" -ForegroundColor Red
        Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  Without this task the add-in WILL NOT auto-load when erwin starts." -ForegroundColor Yellow
        Write-Host "  Recovery (run in an elevated PowerShell, then re-run install.bat):" -ForegroundColor Yellow
        Write-Host "    schtasks /Delete /TN `"$taskName`" /F" -ForegroundColor Yellow
        Write-Host "  If that still fails, open Task Scheduler, locate '$taskName' in any" -ForegroundColor Yellow
        Write-Host "  folder (root or sub-folder), delete it manually, then re-run install.bat." -ForegroundColor Yellow
        $registered = $null
    }

    # Start watcher immediately (don't wait for next logon) AND verify the
    # PowerShell host actually came up. Helper handles the Disabled-task
    # case, the Start-ScheduledTask error path, and the 3-10s cold-start
    # poll window. Skipped when registration failed - Start on a missing
    # task throws.
    if ($registered) {
        Start-AddinWatcher -TaskName $taskName | Out-Null
    }
} else {
    Write-Host "  autostart-watcher.ps1 not found in package, skipping" -ForegroundColor Yellow
}

Write-Host "`nInstallation complete!" -ForegroundColor Green
Write-Host "Add-in will auto-load when erwin starts." -ForegroundColor Cyan
Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
