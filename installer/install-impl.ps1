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
#   install.bat -Silent         Unattended install, never waits for a key press
#   uninstall.bat -Silent       Unattended uninstall (both .bat files forward %*)
#   .\install-impl.ps1 -?       Show this help (CLI use)
#
# Exit codes: 0 clean, 1 at least one step failed, 2 bad command line (nothing
# was changed). A mistyped switch is REJECTED, not ignored: see the sanity gate
# right below the param() block for why that needed spelling out.
#
# Every run prints a "build:" line under the opening banner (and under -?), so a
# support screenshot identifies the build without anyone diffing files.
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
    # Uninstall-only, and a no-op switch kept for CLI compatibility. Removal
    # of HKCU\Software\EliteSoft\MetaRepo\Bootstrap (DB connection: host,
    # port, database, DPAPI-encrypted user/password) and Extension (UI prefs)
    # is UNCONDITIONAL since the question-free uninstall decision of
    # 2026-07-14, so passing this switch changes nothing.
    #
    # An earlier version of this comment claimed the entries were preserved by
    # default and that the user was asked interactively. That has not been
    # true since 2026-07-14; it is documented here because the mismatch made a
    # destructive uninstall look safe on inspection.
    #
    # Because the removal is unconditional AND install deletes
    # bootstrap.seed.json after the first successful write, an uninstall used
    # to leave the user with no copy of the credentials anywhere. The
    # uninstall path now exports the Bootstrap key to
    # %LOCALAPPDATA%\EliteSoft\ErwinAddIn-Backup first. The DPAPI values in
    # that export stay CurrentUser-encrypted, so it is only restorable by this
    # same user on this same machine.
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
    # Unattended install for mass deployment. The script never waits for a key
    # press: every "press any key to exit" pause is skipped, and the two places
    # that genuinely need an answer stop the run instead of blocking on it.
    #
    # Works for -Uninstall too, and composes with everything else, so the usual
    # deployment call is
    #   install.bat -Silent
    # with a bootstrap.seed.json shipped next to it (or -DBHost/-DBName/... on
    # the command line).
    #
    # The exit code is what a deployment tool reads: 0 clean, 1 when any step
    # failed, and the closing banner lists what failed. Without -Silent the
    # pause is still skipped automatically when stdin is redirected; -Silent
    # covers the case where a deployment agent runs the script with a real
    # console attached, where that auto-detection cannot see anything wrong.
    [switch]$Silent,
    [Alias("?")]
    [switch]$Help
)

$ErrorActionPreference = "Stop"

# --- Build identity ---------------------------------------------------------
# package.ps1 rewrites the placeholder below when it copies this script into a
# package, so every run answers "which build is this machine running?" from its
# own console output. A run straight out of the repo keeps the placeholder and
# says so, which is the truth: an unpackaged working tree has no build identity.
#
# Added 2026-07-31. Diagnosing a field report cost an extra round trip purely
# because nothing in the output identified the build, so a plausible "the
# package predates the fix" theory could neither be confirmed nor killed from
# the screenshot the customer sent. One line under the banner ends that class of
# question. The stamp travels into the install folder with the rest of the
# source copy, so an uninstall reports the build that installed the machine.
$script:BuildStampRaw = '@@BUILD_STAMP@@'

function Get-BuildStamp {
    <#
    .SYNOPSIS
        Build identity of this script, or the working-tree marker when unstamped.
    #>
    if ($script:BuildStampRaw -like '@@*@@') { return 'working tree (unpackaged)' }
    return $script:BuildStampRaw
}

function Write-BuildStamp {
    Write-Host "  build: $(Get-BuildStamp)" -ForegroundColor DarkGray
}

# --- Command-line sanity gate ----------------------------------------------
# The param() block above is deliberately a SIMPLE one. Adding [CmdletBinding()]
# would reject junk for free, but it also makes PowerShell intercept "-?" and
# print nothing at all, and "-?" is the documented help switch (both measured
# 2026-07-31). So the rejection is done here by hand instead.
#
# What a simple param() block does with junk, measured, exit code 0 every time:
#   install.bat -Slient   unknown NAME, lands in $args and is IGNORED
#   install.bat /Silent   slash form, binds POSITIONALLY to $DBHost
# Either way the whole install or uninstall runs, reports success, and then
# still stops at "Press any key to exit...". That is how one mistyped letter
# looked exactly like a broken -Silent switch (report 2026-07-31), with the
# script itself saying nothing. Swallowing an argument the operator actually
# typed is the "no silent fallback" rule with the volume turned to zero.
$knownParams = @('Uninstall', 'RemoveConnectionInfo', 'DBHost', 'DBPort', 'DBName',
                 'DBUserName', 'DBPassword', 'DBType', 'DdlGenerator', 'Silent', 'Help')

function Get-ArgSuggestion([string]$token) {
    <#
    .SYNOPSIS
        Best guess at the parameter the operator meant, or "" when there is none.
    .DESCRIPTION
        Two cheap checks, no fuzzy scoring. Exact name after stripping the
        leading - or / catches the Windows "/Silent" habit. Comparing sorted
        letters catches transposition typos ("Slient" and "Silent" are the same
        multiset), which is the case that actually cost a support round trip.
    #>
    $t = $token.TrimStart('-', '/')
    if (-not $t) { return "" }
    foreach ($k in $knownParams) {
        if ($k -ieq $t) { return "-$k (use a hyphen, not a slash)" }
    }
    $key = (($t.ToLowerInvariant().ToCharArray() | Sort-Object) -join '')
    foreach ($k in $knownParams) {
        if ((($k.ToLowerInvariant().ToCharArray() | Sort-Object) -join '') -eq $key) { return "-$k" }
    }
    return ""
}

$badArgs = New-Object System.Collections.ArrayList
foreach ($a in $args) { [void]$badArgs.Add([string]$a) }
# A stray positional token always fills the FIRST unbound string parameter, so
# checking these five catches it wherever it landed. $DBPassword is deliberately
# NOT checked: a password may legitimately start with - or /, and positional
# binding can only reach it once all five below were supplied by name.
foreach ($pn in @('DBHost', 'DBPort', 'DBName', 'DBUserName', 'DBType')) {
    $pv = [string](Get-Variable -Name $pn -ValueOnly -ErrorAction SilentlyContinue)
    if ($pv -and ($pv.StartsWith('-') -or $pv.StartsWith('/'))) { [void]$badArgs.Add($pv) }
}
if ($badArgs.Count -gt 0) {
    Write-Host ""
    foreach ($b in $badArgs) {
        Write-Host "  ERROR: unrecognized argument: $b" -ForegroundColor Red
        $hint = Get-ArgSuggestion $b
        if ($hint) { Write-Host "         Did you mean $hint ?" -ForegroundColor Yellow }
    }
    Write-Host ""
    Write-Host "  Nothing was installed, uninstalled or changed." -ForegroundColor Yellow
    Write-Host "  Valid switches: $(($knownParams | ForEach-Object { "-$_" }) -join ' ')" -ForegroundColor Gray
    Write-Host "  Run with -? for the full usage." -ForegroundColor Gray
    Write-Host ""
    exit 2
}

# Shared watcher control helpers (Stop-AddinWatcher / Start-AddinWatcher).
# Dot-sourced here so both the Uninstall and Install paths can use them
# without conditional re-loading. The file lives next to install-impl.ps1
# both in the repo (installer/) and in the packaged install (publish dir).
. (Join-Path $PSScriptRoot 'watcher-control.ps1')

if ($Help) {
    Write-Host ""
    Write-Host "Elite Soft Erwin Add-In - Installer" -ForegroundColor Cyan
    Write-Host "===================================" -ForegroundColor Cyan
    # Non-destructive way to ask a customer which build they have: "-?" runs
    # nothing, so this is safe to request over the phone on a live machine.
    Write-BuildStamp
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
    Write-Host "Unattended / mass deployment:" -ForegroundColor Yellow
    Write-Host "  install.bat -Silent                             " -NoNewline; Write-Host "Never waits for a key press" -ForegroundColor Gray
    Write-Host "  install.bat -Silent -DBHost srv -DBName META ... " -NoNewline; Write-Host "Same, DB config from the command line" -ForegroundColor Gray
    Write-Host "  uninstall.bat -Silent                           " -NoNewline; Write-Host "Unattended uninstall" -ForegroundColor Gray
    Write-Host "  Exit code: 0 = clean, 1 = at least one step failed (the banner lists them)," -ForegroundColor Gray
    Write-Host "             2 = bad command line, nothing was changed." -ForegroundColor Gray
    Write-Host "  Ship a bootstrap.seed.json next to install.bat, or pass -DBHost/-DBName," -ForegroundColor Gray
    Write-Host "  otherwise the DB config is skipped and the add-in reports 'No configuration found'." -ForegroundColor Gray
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

# --- Run outcome tracking --------------------------------------------------
# Every failure that leaves the machine in a partial state is recorded here
# instead of being printed and then forgotten. The closing banner reads this
# list, so neither install nor uninstall can print a green "complete!" over a
# failed step, and the exit code tells a deployment tool (SCCM, Intune) the
# truth. install.bat forwards %ERRORLEVEL% verbatim.
#
# Added 2026-07-29 after a customer install printed "Access is denied" for the
# Scheduled Task and then "Installation complete!" in green, twenty lines
# apart, and still exited 0. Shared by both paths on purpose: the uninstall
# path grew the same defect the moment it gained a red error branch of its own
# (the credential backup) that still reached "Uninstall complete!".
$script:RunFailures = New-Object System.Collections.ArrayList

function Add-RunFailure {
    <#
    .SYNOPSIS
        Records a step that failed, for the closing banner and exit code.
    .PARAMETER Step
        Short step label, e.g. "Auto-start watcher".
    .PARAMETER Detail
        One-line consequence for the user, not the raw exception text.
    #>
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][string]$Detail
    )
    [void]$script:RunFailures.Add([pscustomobject]@{ Step = $Step; Detail = $Detail })
}

function Write-ClosingBanner {
    <#
    .SYNOPSIS
        Prints the final verdict and returns the process exit code.
    .PARAMETER Verb
        "Installation" or "Uninstall".
    .PARAMETER SuccessDetail
        Extra line printed only when nothing failed.
    .OUTPUTS
        [int] 0 when clean, 1 when any step failed.
    #>
    param(
        [Parameter(Mandatory)][string]$Verb,
        [string]$SuccessDetail
    )
    if ($script:RunFailures.Count -eq 0) {
        Write-Host "`n$Verb complete!" -ForegroundColor Green
        if ($SuccessDetail) { Write-Host $SuccessDetail -ForegroundColor Cyan }
        return 0
    }

    Write-Host "`n$Verb finished WITH PROBLEMS ($($script:RunFailures.Count)):" -ForegroundColor Red
    foreach ($f in $script:RunFailures) {
        Write-Host "  - $($f.Step)" -ForegroundColor Red
        Write-Host "      $($f.Detail)" -ForegroundColor Yellow
    }
    return 1
}

# True when this process carries an elevated (High integrity) token.
#
# This matters far more than it looks. A Scheduled Task registered by an
# elevated process gets BUILTIN\Administrators as its file owner, so the
# CREATOR OWNER ACE on C:\Windows\System32\Tasks grants Full Control to a
# group that is DENY-ONLY in the very same user's normal filtered token. The
# user keeps an explicit Read ACE but loses write and delete, so every later
# NON-elevated install fails at re-registration with "Access is denied".
# Verified empirically 2026-07-29: an elevated-created task file returned
# Read=GRANTED, Write=DENIED for its own non-elevated creator.
function Test-IsElevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Decides whether installing from $SourceDir into $InstallDir would destroy
# the source. Pure and side-effect free so it can be tested directly.
#
# Three fatal shapes, all reachable in practice because install.bat,
# uninstall.bat and install-impl.ps1 are deliberately copied INTO the install
# folder so uninstall keeps working after the extracted ZIP is gone:
#   same                 - running the copy in the install folder
#   source-under-install - package extracted to a SUBFOLDER of it. Step 1 runs
#                          `Remove-Item "$InstallDir\*" -Recurse`, which takes
#                          the whole subtree, so this is just as fatal as
#                          equality (verified 2026-07-29) and an equality-only
#                          check sails right past it.
#   install-under-source - $InstallDir inside $SourceDir, where the later
#                          `Copy-Item -Recurse "*"` copies the destination
#                          into itself.
#
# The trailing separator in the StartsWith comparisons is what keeps
# "...\ErwinAddInBackup" from being read as a child of "...\ErwinAddIn".
function Test-InstallPathOverlap {
    <#
    .SYNOPSIS
        Returns '' when the two paths are safe, otherwise the overlap kind.
    .OUTPUTS
        [string] '' | 'same' | 'source-under-install' | 'install-under-source'
    #>
    param(
        [Parameter(Mandatory)][string]$SourceDir,
        [Parameter(Mandatory)][string]$InstallDir
    )
    $s = ([System.IO.Path]::GetFullPath($SourceDir)).TrimEnd('\')
    $i = ([System.IO.Path]::GetFullPath($InstallDir)).TrimEnd('\')
    if ($s -ieq $i) { return 'same' }
    if ($s.StartsWith($i + '\', [System.StringComparison]::OrdinalIgnoreCase)) { return 'source-under-install' }
    if ($i.StartsWith($s + '\', [System.StringComparison]::OrdinalIgnoreCase)) { return 'install-under-source' }
    return ''
}

# True when nothing in this run may block waiting for a human.
#
# Two independent signals, and both are needed:
#   -Silent          the caller states it outright. Required because a
#                    deployment agent can run the script with a real console
#                    attached, where the auto-detection below sees nothing
#                    unusual and would happily wait forever.
#   stdin redirected a wrapper script or CI shell piped us. Kept as a safety
#                    net for callers that never learn about -Silent.
#
# [Environment]::UserInteractive is deliberately NOT used: it stays true for a
# console process whose streams have been redirected.
function Test-Unattended {
    return ($script:Silent -or [Console]::IsInputRedirected)
}

# Keeps the console window open after a double-click, but never blocks an
# unattended run. Without this the window vanishes before the user can read
# an error, and with an unconditional ReadKey the exit code this script sets
# would never reach the tool that needs it (RawUI.ReadKey reads the console
# input buffer directly, so with stdin redirected it neither returns nor
# throws: it hangs).
function Wait-ForExitKey {
    if (Test-Unattended) { return }
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    try { $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { }
}

# Positive confirmation that unattended mode is ON, printed right under the
# opening banner of both paths.
#
# Without this the only evidence -Silent took effect is the ABSENCE of a pause
# thirty lines later, so a switch that never bound and a switch that worked look
# identical until the very end of the run. That ambiguity is what made a single
# mistyped letter read as "-Silent does not work on uninstall" (report
# 2026-07-31). Says WHICH signal triggered it, because stdin redirection turns
# it on without anyone asking.
function Write-UnattendedNotice {
    if (-not (Test-Unattended)) { return }
    $why = if ($script:Silent) { "-Silent" } else { "stdin is redirected" }
    Write-Host "  Unattended run ($why): no prompts, no exit pause. The exit code carries the result." -ForegroundColor DarkGray
}

# Best-effort schtasks.exe delete that RECORDS what happened instead of
# discarding it. Returns one diagnostic line for the caller to collect.
#
# Two traps this wraps:
#   1. schtasks.exe returns exit code 1 for BOTH "task not found" (the normal
#      happy case during cleanup) and "Access is denied" (the case that
#      explains a later registration failure). The exit code alone therefore
#      proves nothing, and the output TEXT has to be kept.
#   2. Under $ErrorActionPreference='Stop', native stderr redirected with
#      2>&1 arrives as ErrorRecords and can bubble up as a terminating error,
#      so the call itself still needs a try/catch.
function Invoke-SchtasksDelete {
    param([Parameter(Mandatory)][string]$TaskPathOrName)

    $out = $null
    $rc = $null
    try {
        $out = & schtasks.exe /Delete /TN $TaskPathOrName /F 2>&1
        $rc = $LASTEXITCODE
    }
    catch {
        $out = $_.Exception.Message
        $rc = -1
    }
    # Native stderr captured with 2>&1 arrives as ErrorRecord objects, and
    # piping those straight to Out-String renders the whole PowerShell
    # decoration (the offending source line, the tildes, CategoryInfo,
    # FullyQualifiedErrorId) around a one-line message. ToString() on an
    # ErrorRecord yields just the message, which is the only part that
    # distinguishes "cannot find the file specified" from "Access is denied".
    $lines = @($out | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { [string]$_ }
        })
    $text = (($lines -join ' ').Trim() -replace '\s+', ' ')
    return "schtasks /Delete /TN '$TaskPathOrName' -> exit=$rc; $text"
}

function Test-CanRegisterScheduledTask {
    <#
    .SYNOPSIS
        Can THIS account create a Scheduled Task at all, under an unused name?
    .OUTPUTS
        [pscustomobject] Allowed / Error / Leftover
    #>
    # Registering the real task can be denied for two causes that print the same
    # "Access is denied" text and need OPPOSITE fixes:
    #   - the machine denies task creation to this account (hardened ACL on
    #     %SystemRoot%\System32\Tasks, group policy, endpoint-protection rule).
    #     Nothing to delete; re-running install.bat can never help.
    #   - only THIS NAME is blocked, by a half-removed registration. An admin
    #     has to clear it, then a plain re-run works.
    # Probing with a throwaway name separates them. Prod report 2026-07-30: the
    # cleanup diagnostics showed four clean "no match" lines and registration
    # was still denied, so the installer's stale-task advice was wrong for that
    # machine and the operator re-ran the installer for nothing.
    #
    # The probe task carries NO trigger, so it cannot fire even if the removal
    # below fails, and its name says out loud that it is disposable.
    $probeName = "EliteSoft AddIn install probe (safe to delete)"
    $verdict = [pscustomobject]@{ Allowed = $false; Error = ''; Leftover = '' }

    try {
        $probeAction = New-ScheduledTaskAction -Execute "cmd.exe" -Argument "/c exit"
        # -Force so a probe stranded by an earlier failed run is replaced rather
        # than reported as "already exists", which would mis-read as a denial.
        [void](Register-ScheduledTask -TaskName $probeName -Action $probeAction -Force -ErrorAction Stop)
        $verdict.Allowed = $true
    }
    catch {
        $verdict.Error = $_.Exception.Message
        # Nothing was created, so there is nothing to clean up.
        return $verdict
    }

    try {
        Unregister-ScheduledTask -TaskName $probeName -Confirm:$false -ErrorAction Stop
    }
    catch {
        # Reported, never swallowed: this leaves a visible stray task in the
        # user's Task Scheduler and they need to know its name to remove it.
        $verdict.Leftover = "could not remove the probe task '$probeName': $($_.Exception.Message)"
    }
    return $verdict
}

function Get-ScheduledTaskNameCollisionReport {
    <#
    .SYNOPSIS
        What the two Task Scheduler stores say about one task NAME.
    .OUTPUTS
        [string[]] one line per store; every line states a fact or says the
        store could not be read. Never empty.
    #>
    param([Parameter(Mandatory)][string]$TaskName)

    # A name can stay claimed after its task stops resolving (a partially
    # removed elevated install, or a cleanup tool that deleted the XML but not
    # the TaskCache entry). In that state Get-ScheduledTask reports "no match"
    # AND schtasks /Delete reports "cannot find the file specified", while
    # Register-ScheduledTask still fails with "Access is denied" - the blind
    # spot in the cleanup diagnostics above. Read-only on purpose: reporting a
    # remnant is the installer's job, repairing HKLM is an administrator's.
    $lines = New-Object System.Collections.ArrayList

    # NOT Test-Path on the registry provider. HKLM\...\Schedule\TaskCache is
    # readable by administrators only and this installer runs non-elevated on
    # purpose, so the provider cannot tell present from absent - and it settles
    # that by answering TRUE for EVERY child name. Measured on PS 7.6.3 /
    # Windows Server 2022 as a normal user: Test-Path on
    # ...\TaskCache\Tree\<pure garbage> -> True, while HKLM:\SOFTWARE\<garbage>
    # one level up -> False. Reporting that as "a remnant exists" would invent
    # evidence on a perfectly clean machine, which is the exact failure this
    # whole diagnosis exists to stop. Get-Item separates the three real
    # outcomes by exception type instead: found / ItemNotFound / SecurityException.
    $treeKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\$TaskName"
    try {
        [void](Get-Item -LiteralPath $treeKey -ErrorAction Stop)
        [void]$lines.Add("TaskCache registry entry EXISTS -> the name is still claimed: $treeKey")
    }
    catch {
        # Classified on the exception type, not on localized message text.
        switch ($_.Exception.GetType().Name) {
            'ItemNotFoundException' {
                [void]$lines.Add("TaskCache registry entry: absent (name not claimed there)")
            }
            'SecurityException' {
                [void]$lines.Add("TaskCache registry entry: NOT CHECKABLE without administrator rights - neither present nor absent can be concluded")
            }
            default {
                [void]$lines.Add("TaskCache registry entry unreadable: $($_.Exception.GetType().Name): $($_.Exception.Message)")
            }
        }
    }

    $taskFile = Join-Path "$env:SystemRoot\System32\Tasks" $TaskName
    try {
        if (Test-Path -LiteralPath $taskFile) {
            $owner = '(owner unreadable)'
            try { $owner = (Get-Acl -LiteralPath $taskFile -ErrorAction Stop).Owner }
            catch { $owner = "owner unreadable: $($_.Exception.Message)" }
            [void]$lines.Add("task XML EXISTS: $taskFile (owner = $owner)")
        }
        else {
            [void]$lines.Add("task XML: absent under $env:SystemRoot\System32\Tasks")
        }
    }
    catch {
        [void]$lines.Add("task XML unreadable ($taskFile): $($_.Exception.Message)")
    }

    return $lines.ToArray()
}

# Per-user install paths.
$installDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn"
# HKCU Run fallback for the auto-start watcher, used only when the Scheduled
# Task cannot be registered. Same launch line as the task action, so the
# watcher behaves identically; the only differences are a brief console flash
# at logon and the fact that the user can disable it from Task Manager's
# Startup tab.
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runKeyName = "EliteSoftErwinAddInWatcher"
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
    Write-BuildStamp
    Write-UnattendedNotice

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
    # SilentlyContinue used to hide the outcome of both calls and the green
    # line below was printed regardless. That matters most in exactly the case
    # this release is about: a task owned by another principal cannot be
    # removed by this user, and the operator would have been told it was.
    # Note that a cmdlet-level -ErrorAction SilentlyContinue suppresses the
    # error even under $ErrorActionPreference='Stop' (verified), so the old
    # code could not have thrown either.
    foreach ($tn in @($userTaskName, $sharedTaskName)) {
        try {
            Unregister-ScheduledTask -TaskName $tn -Confirm:$false -ErrorAction Stop
            Write-Host "  Removed Scheduled Task '$tn'" -ForegroundColor Green
        }
        catch {
            if ($_.FullyQualifiedErrorId -like 'CmdletizationQuery_NotFound*') {
                # The legacy shared name essentially never exists. Not a
                # failure, and not worth a line of output.
            }
            else {
                Write-Host "  Could not remove Scheduled Task '$tn': $($_.Exception.Message)" -ForegroundColor Yellow
                $diag = Invoke-SchtasksDelete -TaskPathOrName $tn
                Write-Host "    $diag" -ForegroundColor DarkGray
                # schtasks may have succeeded where the cmdlet could not.
                $stillThere = $null
                try {
                    $stillThere = @(Get-ScheduledTask -TaskName $tn -TaskPath \* -ErrorAction Stop) | Select-Object -First 1
                }
                catch { $stillThere = $null }
                if ($stillThere) {
                    Add-RunFailure -Step "Remove Scheduled Task '$tn'" `
                        -Detail "The task still exists and could not be removed by this user. It keeps launching the watcher at every logon. Delete it from an elevated console: schtasks /Delete /TN `"$tn`" /F"
                }
                else {
                    Write-Host "  Removed Scheduled Task '$tn' (via schtasks)" -ForegroundColor Green
                }
            }
        }
    }

    # The install path falls back to an HKCU Run entry when the Scheduled Task
    # cannot be registered. Uninstall has to clear that too, otherwise a
    # "removed" add-in keeps launching a watcher at every logon that then
    # points at a deleted script.
    if (Get-ItemProperty -LiteralPath $runKeyPath -Name $runKeyName -ErrorAction SilentlyContinue) {
        try {
            Remove-ItemProperty -LiteralPath $runKeyPath -Name $runKeyName -ErrorAction Stop
            Write-Host "  Removed HKCU Run autostart entry '$runKeyName'" -ForegroundColor Green
        }
        catch {
            Write-Host "  WARNING: could not remove the HKCU Run entry '$runKeyName': $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host "           Remove it from Task Manager > Startup, or from $runKeyPath." -ForegroundColor Yellow
            Add-RunFailure -Step "Remove HKCU Run autostart entry" `
                -Detail "'$runKeyName' is still under $runKeyPath and will keep launching a watcher at every logon, pointing at a script this uninstall deleted."
        }
    }

    # HKCU MetaRepo entries: Bootstrap holds the user's DB connection (DBHost,
    # DBPort, DBName, DPAPI-encrypted DBUserName/DBPassword); Extension holds
    # UI prefs. These are ALWAYS removed on uninstall (owner decision
    # 2026-07-14: question-free uninstall). HKLM bootstrap (if present) is
    # corporate IT territory and is never touched here.
    #
    # This block used to justify the unconditional wipe with "Upgrades run
    # through install.bat - which re-seeds from bootstrap.seed.json". That
    # justification is false after the very first install: the install path
    # DELETES bootstrap.seed.json once it has written HKCU successfully, so
    # from then on neither the registry nor the seed file survives an
    # uninstall, and the next install falls into interactive credential
    # prompts. The values were recoverable only by re-extracting the original
    # ZIP, and DBUserName/DBPassword not even then.
    #
    # So: export the key before deleting it. The export lands OUTSIDE
    # $installDir (which this uninstall is about to delete) and keeps the
    # DPAPI CurrentUser encryption, so it is restorable by this user on this
    # machine and useless to anyone else.
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

        if ($hasBootstrap) {
            $backupDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn-Backup"
            $backupFile = Join-Path $backupDir ("Bootstrap-{0}.reg" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
            try {
                if (-not (Test-Path -LiteralPath $backupDir)) {
                    New-Item -ItemType Directory -Path $backupDir -Force -ErrorAction Stop | Out-Null
                }
                # reg.exe wants the native hive path, not the PS drive form.
                $out = & reg.exe export "HKCU\Software\EliteSoft\MetaRepo\Bootstrap" $backupFile /y 2>&1
                if ($LASTEXITCODE -ne 0) { throw (($out | Out-String).Trim()) }
                Write-Host "  Backed up DB connection to $backupFile" -ForegroundColor Green
                Write-Host "    Restore with: reg import `"$backupFile`"" -ForegroundColor Gray
                Write-Host "    (DBUserName/DBPassword stay DPAPI-encrypted for THIS user on THIS machine.)" -ForegroundColor Gray
            }
            catch {
                # Do not delete what we could not back up: losing the DB
                # password with no copy anywhere is worse than leaving a
                # registry key behind on uninstall.
                Write-Host "  ERROR: could not back up the DB connection: $($_.Exception.Message)" -ForegroundColor Red
                Write-Host "  Leaving HKCU Bootstrap in place rather than destroying the only copy." -ForegroundColor Yellow
                Write-Host "  Remove it by hand if you really want it gone:" -ForegroundColor Yellow
                Write-Host "    Remove-Item -LiteralPath '$bootstrapPath' -Recurse -Force" -ForegroundColor Yellow
                Add-RunFailure -Step "Back up DB connection before removing it" `
                    -Detail "The export failed, so $bootstrapPath was deliberately NOT deleted. The uninstall is otherwise complete; remove that key by hand if you want a full wipe."
                $hasBootstrap = $false
            }
        }

        $pathsToRemove = @()
        if ($hasBootstrap) { $pathsToRemove += $bootstrapPath }
        if ($hasExtension) { $pathsToRemove += $extensionPath }

        foreach ($p in $pathsToRemove) {
            if (-not (Test-Path $p)) { continue }
            try {
                Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop
                Write-Host "  Removed $p" -ForegroundColor Green
            } catch {
                Write-Host "  WARNING: Could not remove ${p}: $($_.Exception.Message)" -ForegroundColor Yellow
                Add-RunFailure -Step "Remove HKCU registry key" `
                    -Detail "$p is still present. A later re-install will pick up the stale values from it."
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
            Add-RunFailure -Step "Remove install folder" `
                -Detail "$installDir is still on disk, usually because erwin or a watcher still holds a file open. Close erwin, then delete the folder by hand."
        }
    }

    # Remove per-user watcher logs.
    $watcherLogDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn-Logs"
    if (Test-Path $watcherLogDir) {
        Remove-Item $watcherLogDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    $uninstallExit = Write-ClosingBanner -Verb "Uninstall"
    Wait-ForExitKey
    exit $uninstallExit
}

# === INSTALL ===
Write-Host "=== Installing Elite Soft Erwin Add-In (User scope) ===" -ForegroundColor Cyan
Write-BuildStamp
Write-UnattendedNotice
$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Self-wipe guard. install.bat, uninstall.bat and install-impl.ps1 are copied
# INTO $installDir on purpose, so uninstall still works after the extracted
# ZIP is gone (see the Copy-Item comment in Step 1, and docs/INSTALL.md).
# Running that copy makes $sourceDir and $installDir the same folder, and
# Step 1 empties $installDir before copying "*" out of $sourceDir. The copy
# source is therefore deleted first, "Copied 0 files" is printed in GREEN, and
# every binary on the machine is destroyed by a run that reports success.
# uninstall.bat living in the same folder IS the sanctioned way to run from
# there, which puts this trap exactly one filename away from the documented
# workflow. Compare resolved full paths so a trailing slash or a different
# casing cannot slip past.
$overlap = Test-InstallPathOverlap -SourceDir $sourceDir -InstallDir $installDir
if ($overlap) {
    Write-Host ""
    switch ($overlap) {
        'install-under-source' { Write-Host "  ERROR: the install target sits INSIDE the folder this installer runs from:" -ForegroundColor Red }
        'source-under-install' { Write-Host "  ERROR: this installer is running from INSIDE the install folder:" -ForegroundColor Red }
        default { Write-Host "  ERROR: this installer is running from the install folder itself:" -ForegroundColor Red }
    }
    Write-Host "    running from  : $sourceDir" -ForegroundColor Red
    Write-Host "    install target: $installDir" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Installing would delete the very files it copies FROM and leave you with" -ForegroundColor Yellow
    Write-Host "  an empty install." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Extract the ZIP somewhere outside $installDir and run install.bat there." -ForegroundColor Yellow
    Write-Host "  (uninstall.bat IS safe to run from the install folder.)" -ForegroundColor Gray
    Write-Host ""
    Wait-ForExitKey
    exit 1
}

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
    Wait-ForExitKey
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
    Wait-ForExitKey
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
    # An unattended run cannot answer this prompt, and it is the one pause that
    # cannot simply be skipped: falling through would let Step 1 wipe a
    # directory erwin holds open. So stop, with a real exit code, before
    # anything is changed. RawUI.ReadKey would otherwise hang forever here,
    # right after Stop-AddinWatcher already killed the watcher.
    if (Test-Unattended) {
        Write-Host "  Running unattended, cannot wait for you to close erwin." -ForegroundColor Red
        Write-Host "  Close erwin and re-run the installer." -ForegroundColor Yellow
        Add-RunFailure -Step "Pre-flight (erwin running)" `
            -Detail "erwin was running in this session and the run is unattended, so the installer stopped before changing anything."
        Wait-ForExitKey
        exit 1
    }

    Write-Host "  Close YOUR erwin and press any key to continue, or Ctrl+C to cancel." -ForegroundColor Yellow
    Write-Host ""
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')

    $procs = Get-ErwinProcesses-MineOrOthers
    if ($procs.Own.Count -gt 0) {
        Write-Host "  Your erwin is still running. Aborting installation." -ForegroundColor Red
        Wait-ForExitKey
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
    Wait-ForExitKey
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
    # Not fatal (erwin often recovers on a manual restart) but it must not be
    # swallowed by a green "Installation complete!" either: a failed
    # CoCreateInstance here is the usual first symptom of a quarantined or
    # mis-built comhost.dll.
    Add-RunFailure -Step "COM pre-warm" `
        -Detail "CoCreateInstance on '$progId' failed, so erwin may not list the add-in on its first launch. Restart erwin once; if it still does not appear, the .NET 10 Desktop Runtime or comhost.dll is the cause."
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

# Windows PowerShell 5.1 (.NET Framework) does NOT auto-load System.Security.dll,
# which is where ProtectedData and DataProtectionScope live. Without this Add-Type
# every DPAPI call below fails with "Unable to find type
# [System.Security.Cryptography.DataProtectionScope]". PS 7 / .NET Core resolves it
# implicitly, so the bug only surfaces on production user machines running the
# in-box PS 5.1. Loaded once here because BOTH the read-back probe
# (Test-DpapiDecryptable) and the write path further down need it.
Add-Type -AssemblyName System.Security

# Can THIS Windows account actually DPAPI-Unprotect the given Base64 blob?
# DPAPI CurrentUser blobs are bound to the user AND the machine, so a value
# written by another account, copied in via .reg, or left behind after the
# account's master key changed (admin-forced password reset, temporary/roaming
# profile) is unreadable here even though it looks like a perfectly valid
# registry string.
function Test-DpapiDecryptable([string]$base64Cipher) {
    try {
        $bytes = [System.Convert]::FromBase64String($base64Cipher)
    } catch {
        return $false
    }
    try {
        [void][System.Security.Cryptography.ProtectedData]::Unprotect(
            $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        return $true
    } catch {
        return $false
    }
}

# Same configured-test the add-in uses: key exists AND DBHost non-empty AND
# DBName non-empty AND the encrypted credentials actually decrypt on this
# account. Legacy value names (Host/Database/Username/Password) do NOT count -
# those packages predate the DB* rename and need re-install.
function Test-BootstrapConfigured([string]$registryPath) {
    if (-not (Test-Path -LiteralPath $registryPath)) { return $false }
    $hostVal = $null; $nameVal = $null
    try { $hostVal = (Get-ItemProperty -LiteralPath $registryPath -Name "DBHost" -ErrorAction Stop).DBHost } catch { }
    try { $nameVal = (Get-ItemProperty -LiteralPath $registryPath -Name "DBName" -ErrorAction Stop).DBName } catch { }
    if ([string]::IsNullOrEmpty($hostVal) -or [string]::IsNullOrEmpty($nameVal)) { return $false }

    # Host+Name alone are NOT enough. The add-in also DPAPI-Unprotects DBUserName
    # and DBPassword with CurrentUser scope and RETHROWS on failure
    # (HkcuBootstrapReader.DpapiDecrypt), so a key whose blobs this account cannot
    # decrypt makes the add-in pop "Key not valid for use in specified state." on
    # every model open (prod report 2026-07-30). Counting such a key as
    # "configured" sent a no-arg re-run down the keep-existing path below, which
    # preserved the broken blob: the only recovery was deleting the key by hand.
    foreach ($valueName in @("DBUserName", "DBPassword")) {
        $cipher = $null
        try { $cipher = (Get-ItemProperty -LiteralPath $registryPath -Name $valueName -ErrorAction Stop).$valueName } catch { }
        # Missing or empty is legitimate: a trusted-connection install seeds "",
        # and the add-in maps "" back to "" without ever calling DPAPI.
        if ([string]::IsNullOrEmpty($cipher)) { continue }
        if (-not (Test-DpapiDecryptable $cipher)) {
            Write-Host "  HKCU bootstrap $valueName cannot be decrypted by this account ($($env:USERDOMAIN)\$($env:USERNAME))." -ForegroundColor Yellow
            Write-Host "  Cause: it was encrypted under a different Windows account or machine, or this" -ForegroundColor Yellow
            Write-Host "         account's DPAPI master key changed (admin password reset, temporary profile)." -ForegroundColor Yellow
            Write-Host "  The add-in would fail with 'Key not valid for use in specified state.' on model open," -ForegroundColor Yellow
            Write-Host "  so the bootstrap counts as NOT configured and gets re-seeded with this account's key." -ForegroundColor Yellow
            return $false
        }
    }
    return $true
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
    } elseif (Test-Unattended) {
        # Branch 4, unattended. Read-Host would block here just as surely as
        # the key prompts do, and a mass deployment would stall on every
        # machine whose seed file was forgotten. Skip the prompts and land in
        # the "Skipped bootstrap" state that already exists below: the add-in
        # installs and reports "No configuration found" on first load, which
        # is recoverable by re-running with -DBHost/-DBName or a seed file.
        #
        # This is the ONE place -Silent changes more than a pause. It only
        # fires when there is genuinely nothing to install from (no seed file,
        # no CLI values, no existing HKCU config), so a correct deployment
        # never reaches it.
        Write-Host "  No HKCU config, no CLI/seed values, and the run is unattended." -ForegroundColor Red
        Write-Host "  Skipping the DB connection prompts: there is nobody to answer them." -ForegroundColor Yellow
        Write-Host "  The add-in will report 'No configuration found' on first load." -ForegroundColor Yellow
        Write-Host "  Re-run with -DBHost/-DBName/... or ship a bootstrap.seed.json." -ForegroundColor Yellow
        Add-RunFailure -Step "MetaRepo bootstrap (DB connection)" `
            -Detail "No DB connection could be configured: nothing was supplied and the run was unattended. The add-in is installed but will report 'No configuration found' until you re-run with -DBHost/-DBName or a bootstrap.seed.json."
        $bsDBHost = ""
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
    # System.Security (ProtectedData / DataProtectionScope) is Add-Type'd once at
    # the top of this step, ahead of Test-DpapiDecryptable; see the comment there
    # for why the in-box Windows PowerShell 5.1 needs it explicitly.

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
    #
    # Every step of this cleanup collects diagnostics into $cleanupDiag rather
    # than discarding them. The previous version used SilentlyContinue, an
    # empty catch and two `$null = & schtasks.exe ... 2>&1`, which is why a
    # customer failure log could not distinguish "no task existed" from "a
    # task existed and I could neither see nor delete it" - the two cases have
    # completely different fixes. Note that schtasks.exe returns exit code 1
    # for BOTH "task not found" and "Access is denied", so the exit code alone
    # is not enough: the output TEXT has to be captured too.
    # The diagnostics are only printed if registration actually fails, so a
    # healthy install stays as quiet as before.
    $namesToClean = @($taskName)
    if ($legacyTaskName -ne $taskName) { $namesToClean += $legacyTaskName }
    $cleanupDiag = New-Object System.Collections.ArrayList

    foreach ($cleanupName in $namesToClean) {
        $foundTasks = @()
        try {
            $foundTasks = @(Get-ScheduledTask -TaskName $cleanupName -TaskPath \* -ErrorAction Stop)
            [void]$cleanupDiag.Add("Get-ScheduledTask '$cleanupName' -> $($foundTasks.Count) match(es)")
        }
        catch {
            # Two very different situations land here:
            #   - a plain no-match, the normal case on a clean machine
            #   - broken WMI/CIM state, worth reporting verbatim
            # Classified on FullyQualifiedErrorId, NOT on the message text.
            # The message is localized AND its wording is not what it looks
            # like: the real text is "No MATCHING MSFT_ScheduledTask objects
            # found BY CIM QUERY for instances of ...", so an obvious-looking
            # match on "No MSFT_ScheduledTask objects found" is dead code that
            # files every clean machine under "broken WMI" (verified live on
            # PS 5.1.20348, and it is exactly what the first version did).
            # A typed catch clause is avoided on purpose: PowerShell resolves
            # catch types at parse time, so naming the CIM exception type here
            # would break parsing on a host where that assembly is not loaded.
            if ($_.FullyQualifiedErrorId -like 'CmdletizationQuery_NotFound*') {
                [void]$cleanupDiag.Add("Get-ScheduledTask '$cleanupName' -> no match")
            }
            else {
                [void]$cleanupDiag.Add("Get-ScheduledTask '$cleanupName' THREW $($_.Exception.GetType().Name): $($_.Exception.Message)")
            }
            # Let the schtasks fallback below still have its turn either way.
        }

        foreach ($t in $foundTasks) {
            $fullPath = "$($t.TaskPath)$($t.TaskName)"
            try {
                Unregister-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath -Confirm:$false -ErrorAction Stop
                Write-Host "  Removed pre-existing task '$fullPath'" -ForegroundColor Gray
                [void]$cleanupDiag.Add("Unregister '$fullPath' -> OK")
            }
            catch {
                Write-Host "  Cmdlet unregister failed for '$fullPath' ($($_.Exception.Message)); trying schtasks" -ForegroundColor Yellow
                [void]$cleanupDiag.Add("Unregister '$fullPath' FAILED: $($_.Exception.Message)")
                # A task owned by another principal cannot be deleted by this
                # user, so record what the task file itself says about who
                # owns it. That single line is usually the whole diagnosis.
                $taskFile = Join-Path "$env:SystemRoot\System32\Tasks" ("$($t.TaskPath)$($t.TaskName)".TrimStart('\'))
                try {
                    $taskAcl = Get-Acl -LiteralPath $taskFile -ErrorAction Stop
                    [void]$cleanupDiag.Add("  task file owner = $($taskAcl.Owner)")
                }
                catch {
                    [void]$cleanupDiag.Add("  task file ACL unreadable: $($_.Exception.Message)")
                }
                [void]$cleanupDiag.Add((Invoke-SchtasksDelete -TaskPathOrName $fullPath))
            }
        }

        # Blind schtasks pass: even if Get-ScheduledTask saw nothing, force a
        # delete by name, because the cmdlet may have left a stale XML behind
        # that schtasks can still clear. "task not found" is the normal happy
        # case here, so this is best-effort - but it is now RECORDED.
        [void]$cleanupDiag.Add((Invoke-SchtasksDelete -TaskPathOrName $cleanupName))
    }

    # Registering while elevated is what plants the landmine described on
    # Test-IsElevated: the task ends up owned by BUILTIN\Administrators and
    # this same user can never replace it from a normal, non-elevated install
    # again. Warn loudly rather than silently creating the trap.
    if (Test-IsElevated) {
        Write-Host ""
        Write-Host "  WARNING: this installer is running ELEVATED." -ForegroundColor Yellow
        Write-Host "  The auto-start task will be owned by BUILTIN\Administrators, and a later" -ForegroundColor Yellow
        Write-Host "  NORMAL (non-elevated) install by this same user will then fail to replace" -ForegroundColor Yellow
        Write-Host "  it with 'Access is denied'. Prefer running install.bat WITHOUT elevation." -ForegroundColor Yellow
        Write-Host ""
    }

    $action = New-ScheduledTaskAction -Execute "powershell.exe" `
        -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$watcherTarget`""

    $registered = $null
    # Which cause the catch block diagnosed, so the closing summary can say
    # "nothing to do, the Run entry is correct here" instead of pointing at
    # recovery steps that do not apply. Empty while registration succeeds.
    $autoStartDiagnosis = ''
    try {
        # User-scope task: fire only for the installing user. Single instance
        # is fine because only this user's session ever triggers it.
        #
        # -Force added 2026-07-29. Without it, any surviving task with this
        # name that the cleanup above could not remove makes Register fail.
        # When this user owns the task that failure is a harmless
        # "Cannot create a file when that file already exists" which -Force
        # resolves outright; when another principal owns it the error is
        # "Access is denied" and -Force cannot help, which is precisely the
        # case the catch block below now explains.
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero)
        # Fully-qualified DOMAIN\user, not the bare $env:USERNAME (changed
        # 2026-07-30). A logon trigger names a PRINCIPAL, and setting one for an
        # account other than yourself needs privileges this install deliberately
        # does not have. On a domain-joined machine a bare sAMAccountName can
        # resolve to the domain account while a local account of the same name
        # exists (or the reverse), so the trigger can end up pointing at a
        # different principal and Register-ScheduledTask answers "Access is
        # denied". WindowsIdentity.Name is unambiguous. Elevation does not change
        # it, so this reads the same in both cases.
        $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentIdentity
        # The same principal the default would pick (this user, interactive, no
        # elevation), stated explicitly so the trigger and the principal cannot
        # disagree about who the task belongs to.
        #
        # Deliberately NOT retried with a plain -AtLogOn (no user) if this fails:
        # that trigger fires for EVERY user who logs on, which on a shared or
        # terminal-server machine would start this user's watcher in other
        # people's sessions. A denied task is preferable to that.
        $principal = New-ScheduledTaskPrincipal -UserId $currentIdentity `
            -LogonType Interactive -RunLevel Limited
        $registered = Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
            -Principal $principal `
            -Settings $settings -Description "Auto-starts Elite Soft Erwin Add-In when erwin opens" `
            -Force -ErrorAction Stop
        Write-Host "  Scheduled Task '$taskName' created (User: $env:USERNAME)" -ForegroundColor Green
        Write-Host "  Add-in will auto-load when erwin starts" -ForegroundColor Gray

        # A previous install may have fallen back to the HKCU Run entry. Now
        # that the task works, remove it: autostart-watcher.ps1 has no
        # startup duplicate sweep (scripts/autostart-watcher.ps1:596-608) that
        # force-kills every OTHER watcher it finds, so leaving both mechanisms
        # in place does not give two coexisting watchers: it gives a mutual
        # kill race at every logon, whose winner is whichever happened to run
        # its sweep last. Look for "Killing duplicate watcher PID=" in
        # autostart.log when a watcher disappears right after logon.
        if (Get-ItemProperty -LiteralPath $runKeyPath -Name $runKeyName -ErrorAction SilentlyContinue) {
            try {
                Remove-ItemProperty -LiteralPath $runKeyPath -Name $runKeyName -ErrorAction Stop
                Write-Host "  Removed the HKCU Run fallback left by a previous install" -ForegroundColor Gray
            }
            catch {
                Write-Host "  WARNING: could not remove the stale HKCU Run entry '$runKeyName': $($_.Exception.Message)" -ForegroundColor Yellow
                Write-Host "           Two watchers may start at your next logon. Remove it from" -ForegroundColor Yellow
                Write-Host "           Task Manager > Startup, or from $runKeyPath." -ForegroundColor Yellow
            }
        }
    }
    catch {
        Write-Host "  ERROR: Could not register Scheduled Task '$taskName':" -ForegroundColor Red
        Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
        Write-Host ""
        Write-Host "  Task cleanup diagnostics (what the installer saw beforehand):" -ForegroundColor Yellow
        $cleanupDiag | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        Write-Host ""

        # The cleanup diagnostics above can be completely clean and registration
        # still denied (prod report 2026-07-30: four "no match" lines, then
        # "Access is denied"). Printing stale-task recovery in that state sent
        # the operator to delete a task that does not exist and to re-run the
        # installer for nothing. Establish WHICH cause it is before advising.
        # @() so a single-line report stays enumerable.
        Write-Host "  Name-collision checks for '$taskName':" -ForegroundColor Yellow
        @(Get-ScheduledTaskNameCollisionReport -TaskName $taskName) |
            ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        Write-Host ""

        $probe = Test-CanRegisterScheduledTask
        if ($probe.Allowed) {
            $autoStartDiagnosis = 'task-name-blocked'
            Write-Host "  This account CAN create Scheduled Tasks here: a probe task under an unused" -ForegroundColor Yellow
            Write-Host "  name registered and was removed successfully. So it is this NAME that is" -ForegroundColor Yellow
            Write-Host "  blocked, not the permission." -ForegroundColor Yellow
            Write-Host "  Recovery, in an elevated console:" -ForegroundColor Yellow
            Write-Host "    schtasks /Delete /TN `"$taskName`" /F" -ForegroundColor Yellow
            Write-Host "  If that answers 'cannot find the file specified', the name is held by a" -ForegroundColor Yellow
            Write-Host "  TaskCache entry with no task file and an administrator must delete:" -ForegroundColor Yellow
            Write-Host "    HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\$taskName" -ForegroundColor Yellow
            Write-Host "  Then re-run install.bat AS THIS USER ($env:USERNAME) and WITHOUT elevation." -ForegroundColor Yellow
            Write-Host "  Do NOT elevate with a different administrator account: the install dir," -ForegroundColor Yellow
            Write-Host "  the HKCU config and the task name are all per-user, so the add-in would" -ForegroundColor Yellow
            Write-Host "  land in that admin's profile and this user would still have nothing." -ForegroundColor Yellow
        }
        else {
            $autoStartDiagnosis = 'task-creation-denied'
            Write-Host "  This account cannot create Scheduled Tasks on this machine AT ALL: a probe" -ForegroundColor Yellow
            Write-Host "  task under an unused name was denied as well." -ForegroundColor Yellow
            if ($probe.Error) {
                Write-Host "    probe error: $($probe.Error)" -ForegroundColor DarkGray
            }
            Write-Host "  That is a machine policy (hardened ACL on $env:SystemRoot\System32\Tasks," -ForegroundColor Yellow
            Write-Host "  group policy, or an endpoint-protection rule), not something this install" -ForegroundColor Yellow
            Write-Host "  can or should change. There is NOTHING to delete, and re-running install.bat" -ForegroundColor Yellow
            Write-Host "  will not change the outcome." -ForegroundColor Yellow
            Write-Host "  The HKCU Run fallback below is the correct mechanism on this machine: it" -ForegroundColor Yellow
            Write-Host "  auto-loads the add-in at every logon, exactly like the task would. Ask an" -ForegroundColor Yellow
            Write-Host "  administrator for task-creation rights only if you specifically need the" -ForegroundColor Yellow
            Write-Host "  Scheduled Task instead." -ForegroundColor Yellow
        }
        if ($probe.Leftover) {
            Write-Host "  WARNING: $($probe.Leftover)" -ForegroundColor Yellow
        }
        $registered = $null
    }

    if ($registered) {
        # Start watcher immediately (don't wait for next logon) AND verify the
        # PowerShell host actually came up. Helper handles the Disabled-task
        # case, the Start-ScheduledTask error path, and the 3-10s cold-start
        # poll window.
        #
        # The return value used to be discarded with | Out-Null. It carries
        # the answer to "is the watcher actually running", and the install
        # killed the previous one on the way in, so a silent failure here
        # leaves the user with no auto-load until the next logon and no
        # indication of it.
        if (-not (Start-AddinWatcher -TaskName $taskName)) {
            Add-RunFailure -Step "Auto-start watcher (current session)" `
                -Detail "The Scheduled Task was registered but the watcher process did not come up. Auto-load will start working after your next logon, or run: Start-ScheduledTask -TaskName '$taskName'"
        }
    }
    else {
        # The task is only a launcher. Its action is a plain
        # `powershell.exe -File <watcher>`, and autostart-watcher.ps1 never
        # references Task Scheduler, so auto-load does NOT actually depend on
        # the task existing. Fall back to the HKCU Run entry, which needs no
        # administrator rights and no Task Scheduler at all.
        #
        # This is deliberately loud, not a silent degradation: the user is
        # told which mechanism is now in use and how it differs.
        #
        # But first: registration can fail precisely BECAUSE a task of this
        # name survived the cleanup (owned by another principal, typically an
        # earlier elevated install). That task's action is the very same
        # `powershell.exe -File $watcherTarget` line, and $watcherTarget was
        # just refreshed above, so it still launches a working watcher. Adding
        # a Run entry on top would then launch a second watcher at every logon,
        # and each watcher force-kills the others at startup
        # (scripts/autostart-watcher.ps1:596-608), so the result is a kill race
        # rather than a harmless duplicate. Re-query before writing, exactly as
        # the success branch removes the Run entry when the task takes over.
        $survivingTask = $null
        try {
            $survivingTask = @(Get-ScheduledTask -TaskName $taskName -TaskPath \* -ErrorAction Stop |
                    Where-Object { $_.State -ne 'Disabled' }) | Select-Object -First 1
        }
        catch {
            # No match is the expected case here; anything else is recorded
            # rather than swallowed, and still means "no task to rely on".
            if ($_.FullyQualifiedErrorId -notlike 'CmdletizationQuery_NotFound*') {
                Write-Host "  (could not re-query the task: $($_.Exception.Message))" -ForegroundColor DarkGray
            }
        }

        $runValue = "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$watcherTarget`""
        $fallbackOk = $false

        if ($survivingTask) {
            Write-Host ""
            Write-Host "  A Scheduled Task named '$taskName' still exists and is enabled, even" -ForegroundColor Yellow
            Write-Host "  though this install could not replace it. It already launches the" -ForegroundColor Yellow
            Write-Host "  watcher, so NO HKCU Run entry is added: two of them would start two" -ForegroundColor Yellow
            Write-Host "  watchers at every logon." -ForegroundColor Yellow
            Write-Host "  Auto-start therefore still runs the OLD task definition. Delete it as" -ForegroundColor Yellow
            Write-Host "  shown above and re-run install.bat to get a clean one." -ForegroundColor Yellow
            # Remove a Run entry an EARLIER fallback may have left, for the
            # same two-watchers reason.
            if (Get-ItemProperty -LiteralPath $runKeyPath -Name $runKeyName -ErrorAction SilentlyContinue) {
                try {
                    Remove-ItemProperty -LiteralPath $runKeyPath -Name $runKeyName -ErrorAction Stop
                    Write-Host "  Removed the HKCU Run entry left by a previous fallback." -ForegroundColor Gray
                }
                catch {
                    Write-Host "  WARNING: could not remove the stale HKCU Run entry: $($_.Exception.Message)" -ForegroundColor Yellow
                }
            }
        }
        else {
            try {
                if (-not (Test-Path -LiteralPath $runKeyPath)) {
                    New-Item -Path $runKeyPath -Force -ErrorAction Stop | Out-Null
                }
                Set-ItemProperty -LiteralPath $runKeyPath -Name $runKeyName -Value $runValue -ErrorAction Stop
                # Read back rather than trust the write: a policy can allow the
                # write and still discard the value.
                $readBack = (Get-ItemProperty -LiteralPath $runKeyPath -Name $runKeyName -ErrorAction Stop).$runKeyName
                if ($readBack -ne $runValue) { throw "value did not survive the write (read back '$readBack')" }
                $fallbackOk = $true
                Write-Host "  Registered $runKeyPath\$runKeyName" -ForegroundColor Green
                Write-Host "  The add-in will auto-load at every logon, same as the task would." -ForegroundColor Gray
                Write-Host "  Differences: a brief console window flashes at logon, and the entry is" -ForegroundColor Gray
                Write-Host "  visible (and disableable) in Task Manager > Startup." -ForegroundColor Gray
            }
            catch {
                Write-Host "  ERROR: the HKCU Run fallback also failed:" -ForegroundColor Red
                Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
            }
        }

        # Whichever branch ran, give this session a working watcher right now.
        # The install already killed the previous one at the top of the script,
        # so without this the user loses auto-load in the session they are
        # standing in, not just in future ones.
        $liveWatcher = Start-AddinWatcherDirect -WatcherPath $watcherTarget

        # $liveWatcher is part of the verdict, not decoration: "auto-start is
        # configured for next logon" and "the watcher is running right now"
        # are different facts and the user needs both.
        $sessionNote = if ($liveWatcher) { "" } else { " The watcher is NOT running in this session either, so nothing auto-loads until your next logon." }

        if ($survivingTask) {
            Add-RunFailure -Step "Auto-start (Scheduled Task not replaced)" `
                -Detail ("This install could not replace the existing task, so auto-start still runs the OLD task definition. Delete it from an elevated console and re-run install.bat." + $sessionNote)
        }
        elseif ($fallbackOk) {
            # Two very different verdicts share this branch, and the closing
            # summary is the only part most operators read. Pointing a
            # locked-down machine at "recovery steps" it can never complete is
            # what made the prod operator re-run the installer expecting a
            # different result (2026-07-30).
            $fallbackDetail = if ($autoStartDiagnosis -eq 'task-creation-denied') {
                "This machine does not let this account create Scheduled Tasks, so auto-start runs from the HKCU Run entry instead. That auto-loads the add-in at every logon; nothing further is needed and re-running install.bat will not change it."
            }
            else {
                "Task registration was denied while the task name is still held by an older registration. Auto-start runs from the HKCU Run entry meanwhile; see the recovery steps above to restore the Scheduled Task."
            }
            Add-RunFailure -Step "Auto-start (Scheduled Task)" -Detail ($fallbackDetail + $sessionNote)
        }
        elseif ($liveWatcher) {
            Add-RunFailure -Step "Auto-start (Scheduled Task and HKCU Run)" `
                -Detail "Neither auto-start mechanism could be registered. The watcher is running NOW, but it will not come back after a logoff."
        }
        else {
            Add-RunFailure -Step "Auto-start (Scheduled Task and HKCU Run)" `
                -Detail "Neither auto-start mechanism could be registered and the watcher is not running. The add-in must be loaded manually from erwin's Tools > Add-Ins menu."
        }
    }
} else {
    Write-Host "  autostart-watcher.ps1 not found in package, skipping" -ForegroundColor Yellow
    Add-RunFailure -Step "Auto-start watcher" `
        -Detail "autostart-watcher.ps1 was missing from the package, so no auto-start was configured at all."
}

# Closing banner. This used to print "Installation complete!" in green
# unconditionally, which is how a customer machine reported success twenty
# lines after printing "Access is denied" for the auto-start task, and still
# exited 0 so no deployment tool could tell. The banner now reflects what
# actually happened and the exit code carries it out of the process.
$exitCode = Write-ClosingBanner -Verb "Installation" -SuccessDetail "Add-in will auto-load when erwin starts."
if ($exitCode -ne 0) {
    Write-Host ""
    Write-Host "The add-in itself is installed and registered: you can always load it by hand" -ForegroundColor Gray
    Write-Host "from erwin's Tools > Add-Ins menu." -ForegroundColor Gray
}

Wait-ForExitKey
exit $exitCode
