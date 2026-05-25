# Elite Soft Erwin Add-In - Build, Install & Register (Dev Workflow)
#
# Usage:
#   .\build-and-run.ps1                       Build + install + COM register (User scope, no UAC)
#   .\build-and-run.ps1 -KillAllErwinProcs    Same, but also kill other users' erwin (needs admin)
#   .\build-and-run.ps1 -?                    Show help
#
# Requires: .NET 10 SDK. NO admin needed for the default flow - everything is
#           User scope (LOCALAPPDATA + HKCU). Admin is only required when
#           -KillAllErwinProcs is passed (cross-user process termination).

param(
    [Alias('?')]
    [switch]$Help,

    # Force-kills erwin.exe / DdlHelper.exe owned by ANY user, not just
    # $env:USERNAME. Needed when a stale process from another session is
    # holding our install dir or the COM host. Off by default because
    # killing another logged-in user's editor session is destructive.
    # (ErwinInjector.exe was removed 2026-05-26 along with the
    # injection-based auto-load path.)
    [switch]$KillAllErwinProcs
)

if ($Help) {
    Write-Host ""
    Write-Host "Elite Soft Erwin Add-In - Dev Build Script" -ForegroundColor Cyan
    Write-Host "===========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Builds the project, installs to %LOCALAPPDATA%\EliteSoft\ErwinAddIn,"
    Write-Host "registers COM host in HKCU, and configures erwin Add-In Manager."
    Write-Host "Fully User-scoped: no admin / UAC needed for the default flow."
    Write-Host ""
    Write-Host "Each step is timestamp/hash gated so no-change runs finish in"
    Write-Host "seconds: native bridge skipped when .cpp is unchanged, DdlHelper"
    Write-Host "skipped when source is older than the published exe, install dir"
    Write-Host "sync via robocopy /XO, COM register skipped when HKCU already"
    Write-Host "points at the right comhost, watcher recycle skipped when the"
    Write-Host "deployed autostart-watcher.ps1 hash matches scripts/."
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  .\build-and-run.ps1                     Build + install + register"
    Write-Host "  .\build-and-run.ps1 -KillAllErwinProcs  Also kill other users' erwin"
    Write-Host "  .\build-and-run.ps1 -?                  Show this help"
    Write-Host ""
    Write-Host "For packaging (ZIP/EXE), use:" -ForegroundColor Yellow
    Write-Host "  .\package.ps1 -?"
    Write-Host ""
    exit 0
}

$ErrorActionPreference = "Stop"

trap {
    Write-Host "`n[ERROR] $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "At line: $($_.InvocationInfo.ScriptLineNumber)" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

# Dev workflow is fully User-scoped now: files go to %LOCALAPPDATA%, COM is
# registered in HKCU\Software\Classes (not via regsvr32), Add-Ins manager
# entry is HKCU, Scheduled Task is per-user. None of these need admin so
# we no longer auto-elevate. Side effect: -KillAllErwinProcs (cross-user
# kill) needs admin and will warn-and-skip when invoked from a non-elevated
# shell. The previous regsvr32 path also wrote stale HKLM CLSID entries
# that conflicted with install-impl.ps1's User-scope COM registration; the new
# pattern keeps HKLM untouched.
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($KillAllErwinProcs -and -not $isAdmin) {
    Write-Host "WARNING: -KillAllErwinProcs needs admin to terminate other users' processes." -ForegroundColor Yellow
    Write-Host "         Continuing without it; only your own erwin/DdlHelper/Injector will be stopped." -ForegroundColor Yellow
    $KillAllErwinProcs = $false
}

Write-Host "=== Elite Soft Erwin Add-In - Build & Run ===" -ForegroundColor Cyan
if ($isAdmin) {
    Write-Host "Running as Administrator (elevated; not required)" -ForegroundColor Gray
} else {
    Write-Host "Running as standard user (User scope, no UAC needed)" -ForegroundColor Green
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

$installDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn"
# ProgID + menu display name renamed 2026-05-25. Legacy names kept for
# cleanup so a rebuild after the rename leaves a clean registry.
$progId               = "EliteSoft.Meta.AddIn"
$addInDisplayName     = "Elite Soft Meta Addin"
$legacyProgId         = "EliteSoft.Erwin.AddIn"
$legacyAddInDisplay   = "Elite Soft Erwin Addin"
# CLSID must mirror the [Guid(...)] attribute on ErwinAddIn class in
# ErwinAddIn.cs:17 (also referenced from install-impl.ps1).
$clsid      = '{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}'

# HKCU COM (un)registration helpers - same layout install-impl.ps1 uses for
# User-scope installs. Replicates what regsvr32 + .NET comhost would write
# to HKLM\Software\Classes, but in HKCU so no admin is needed. erwin's
# CLSIDFromProgID resolves through HKEY_CLASSES_ROOT (HKCU∪HKLM) so the
# addin loads identically for the current user.
function Register-ComUserScope([string]$comHostPath) {
    $clsidBase   = "HKCU:\Software\Classes\CLSID\$clsid"
    $inprocPath  = "$clsidBase\InProcServer32"
    $clsidProgId = "$clsidBase\ProgId"
    $progIdBase  = "HKCU:\Software\Classes\$progId"
    $progIdClsid = "$progIdBase\CLSID"

    New-Item -Path $inprocPath  -Force | Out-Null
    New-Item -Path $clsidProgId -Force | Out-Null
    New-Item -Path $progIdClsid -Force | Out-Null

    Set-ItemProperty -Path $clsidBase   -Name "(Default)"      -Value $progId
    Set-ItemProperty -Path $inprocPath  -Name "(Default)"      -Value $comHostPath
    Set-ItemProperty -Path $inprocPath  -Name "ThreadingModel" -Value "Both"
    Set-ItemProperty -Path $clsidProgId -Name "(Default)"      -Value $progId
    Set-ItemProperty -Path $progIdBase  -Name "(Default)"      -Value $progId
    Set-ItemProperty -Path $progIdClsid -Name "(Default)"      -Value $clsid
}
function Unregister-ComUserScope {
    $clsidBase  = "HKCU:\Software\Classes\CLSID\$clsid"
    $progIdBase = "HKCU:\Software\Classes\$progId"
    if (Test-Path -LiteralPath $clsidBase)  { Remove-Item -LiteralPath $clsidBase  -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $progIdBase) { Remove-Item -LiteralPath $progIdBase -Recurse -Force -ErrorAction SilentlyContinue }
}

# Returns $true when any source file is newer than $target (or $target is
# missing). Used by the per-step gating logic so unchanged inputs skip the
# rebuild instead of paying the full compile/publish cost on every run.
function Test-AnyNewer {
    param(
        [string[]]$Sources,
        [string]$Target
    )
    if (-not (Test-Path -LiteralPath $Target)) { return $true }
    $targetTime = (Get-Item -LiteralPath $Target).LastWriteTimeUtc
    foreach ($s in $Sources) {
        if (-not (Test-Path -LiteralPath $s)) { continue }
        $item = Get-Item -LiteralPath $s
        if ($item.PSIsContainer) {
            $files = Get-ChildItem -LiteralPath $s -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
            foreach ($f in $files) {
                if ($f.LastWriteTimeUtc -gt $targetTime) { return $true }
            }
        } else {
            if ($item.LastWriteTimeUtc -gt $targetTime) { return $true }
        }
    }
    return $false
}

# Stream-fast SHA1 of a small text file. Used to decide if the deployed copy
# of autostart-watcher.ps1 differs from scripts/, since File.Copy resets the
# mtime so timestamp compare alone is unreliable.
function Get-FileHashSafe {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA1).Hash
}

# --- Step 1: Close processes that might hold our DLLs (CURRENT USER ONLY) ---
# Use WMI GetOwner so we catch processes whose UserName PowerShell can't
# inline-resolve (happens for cross-session or stale processes started on a
# different desktop). A lingering erwin.exe from a prior session can hold
# System.Management.dll and make step 3 (Copy-Item) fail with 'file in use'.
$myUser = $env:USERNAME
function Stop-UserProcesses {
    param(
        [string]$name,
        # When set, ignore ownership and kill every match. Used by
        # -KillAllErwinProcs to wipe stale erwin sessions that another logged-in
        # user owns - destructive, opt-in only.
        [switch]$All
    )
    # Get-CimInstance returns native DateTime for CreationDate and exposes
    # methods through Invoke-CimMethod, so the legacy Get-WmiObject pattern
    # ($p.ConvertToDateTime / $p.GetOwner) - which fails as soon as the
    # object is deserialized in PS7 - is replaced here.
    $procs = Get-CimInstance Win32_Process -Filter "Name='$name.exe'" -ErrorAction SilentlyContinue
    if (-not $procs) { return 0 }
    $killed = 0
    foreach ($p in $procs) {
        $ownerUser = $null
        try {
            $ownerResult = Invoke-CimMethod -InputObject $p -MethodName GetOwner -ErrorAction Stop
            if ($ownerResult -and $ownerResult.ReturnValue -eq 0) { $ownerUser = $ownerResult.User }
        } catch { $ownerUser = $null }

        $started = if ($p.CreationDate -is [DateTime]) { $p.CreationDate.ToString('HH:mm:ss') } else { '?' }
        $ownerLabel = if ($ownerUser) { $ownerUser } else { 'unknown' }

        if ($All) {
            Write-Host "  Killing ${name}.exe PID=$($p.ProcessId) (user=$ownerLabel, FORCE all-users, started $started)" -ForegroundColor Red
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
            $killed++
        } elseif ($ownerUser -and $ownerUser -ieq $myUser) {
            Write-Host "  Killing ${name}.exe PID=$($p.ProcessId) (user=$ownerUser, started $started)" -ForegroundColor Yellow
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
            $killed++
        } else {
            # Default: never kill another user's (or owner-unknown) process.
            # Earlier versions guessed "treat as ours when owner can't be
            # resolved", which silently terminated cross-user erwin sessions
            # the moment GetOwner failed (WMI deserialize bug, missing privs).
            # Skip safely; -KillAllErwinProcs is the explicit opt-in for the
            # cross-user reset.
            Write-Host "  Skipping ${name}.exe PID=$($p.ProcessId) (user=$ownerLabel, not ours; pass -KillAllErwinProcs to override)" -ForegroundColor Gray
        }
    }
    return $killed
}

if ($KillAllErwinProcs) {
    Write-Host "`nClosing erwin / DdlHelper (ALL USERS, -KillAllErwinProcs)..." -ForegroundColor Red
} else {
    Write-Host "`nClosing erwin / DdlHelper (user=$myUser)..." -ForegroundColor Yellow
}
$killedTotal  = 0
$killedTotal += Stop-UserProcesses "erwin"          -All:$KillAllErwinProcs
$killedTotal += Stop-UserProcesses "DdlHelper"      -All:$KillAllErwinProcs
# Only wait when we actually terminated something - the OS needs a beat to
# release file handles before the copy/COM register steps below. No kill -
# no need to pay the 2s tax.
if ($killedTotal -gt 0) {
    Start-Sleep -Seconds 2
} else {
    Write-Host "  No processes were running - skipping 2s settle delay" -ForegroundColor DarkGray
}

# --- Step 2a: Build native bridge (cl.exe) ---
# Must run BEFORE dotnet build so the csproj Copy task picks up the fresh DLL.
# build-and-run.ps1 is the dev "do-it-all" entry point - bridge changes
# need to compile here too, otherwise managed-only rebuild ships a stale DLL.
# We verify success by checking the DLL timestamp advanced after the call;
# build.ps1 itself uses $ErrorActionPreference=Stop + throw on failure, so
# any compile error halts the sub-script before it returns.
$bridgeScript = Join-Path $scriptDir "scripts\native-bridge\build.ps1"
$bridgeDir    = Join-Path $scriptDir "scripts\native-bridge"
$bridgeDll    = Join-Path $bridgeDir "ErwinNativeBridge.dll"
if (Test-Path $bridgeScript) {
    Write-Host "`n[1a/5] Native bridge (cl.exe)..." -ForegroundColor Yellow
    # Recompile only when a .cpp/.h/.hpp file is newer than the DLL (or the
    # DLL is missing). cl.exe + linker takes ~3-8s on this box - skipping
    # it on no-source-change shaves the lion's share of the no-change cycle.
    # -Include needs -Recurse OR a wildcard path; -LiteralPath + a flat
    # -Include returns nothing in PS7. Enumerate the dir and filter on
    # extension explicitly so the gate works regardless of CWD.
    $bridgeSources = @(Get-ChildItem -LiteralPath $bridgeDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.cpp','.h','.hpp' } |
        ForEach-Object { $_.FullName })
    if (-not (Test-AnyNewer -Sources $bridgeSources -Target $bridgeDll)) {
        Write-Host "  Skipped - sources unchanged since DLL was built" -ForegroundColor DarkGray
    } else {
        & $bridgeScript
        if (-not (Test-Path $bridgeDll)) {
            Write-Host "Native bridge build failed - DLL not produced!" -ForegroundColor Red
            Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
        }
        Write-Host "  Native bridge rebuilt." -ForegroundColor Green
    }
} else {
    Write-Host "`n[1a/5] (skipped) native bridge script not found at $bridgeScript" -ForegroundColor DarkGray
}

# --- Step 2b: Build managed code ---
# Incremental dotnet build: no `dotnet clean`, no explicit `dotnet restore`.
# The original script wiped obj/ then restored to dodge an NETSDK1004 caused
# by clean itself - dropping the clean removes the trigger entirely. The
# implicit restore inside `dotnet build` handles legitimate csproj/lock
# changes on its own. A no-source-change rebuild now finishes in 1-2s
# instead of the old 25-40s clean+restore+build cycle.
Write-Host "`n[1b/5] Building project (incremental)..." -ForegroundColor Yellow
dotnet build erwin-addin.sln -c Release -nologo -clp:Summary

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
}
Write-Host "  Build successful!" -ForegroundColor Green

# --- Step 2b': comhost.dll clsidmap embed (SDK CreateComHostTask workaround) ---
# .NET 10.0.102 SDK silently fails to embed the .clsidmap resource into
# comhost.dll even though the _CreateComHost MSBuild target reports
# success. Without the embedded resource, CoCreateInstance fails with
# TYPE_E_CANTLOADLIBRARY, erwin's Add-In Manager hides our menu entry on
# validation, and the addin appears missing. The embed-comhost.ps1
# wrapper calls our tools/comhost-embed helper (which uses
# Microsoft.NET.HostModel.ComHost.ComHost.Create directly) to do what
# the SDK should have done. Verified 2026-05-26: with this step,
# CoCreateInstance succeeds and the addin shows up in Tools > Add-Ins.
$embedScript = Join-Path $scriptDir 'scripts\embed-comhost.ps1'
if (Test-Path $embedScript) {
    Write-Host "`n[1b'/5] Embedding clsidmap into comhost.dll..." -ForegroundColor Yellow
    pwsh -NoProfile -File $embedScript -Configuration Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  WARNING: comhost embed failed (exit=$LASTEXITCODE). Addin may not load." -ForegroundColor Yellow
    } else {
        Write-Host "  comhost.dll has correct clsidmap" -ForegroundColor Green
    }
}

# Build DdlHelper tool - only when the source dir is newer than the
# published exe. `dotnet publish` with no source change still costs ~3-5s of
# msbuild overhead (graph walk + Copy task), which we skip on no-change.
$ddlHelperDir    = Join-Path $scriptDir "tools\DdlHelper"
$ddlHelperOutDir = Join-Path $scriptDir "bin\Release\net10.0-windows\tools\DdlHelper"
$ddlHelperExe    = Join-Path $ddlHelperOutDir "DdlHelper.exe"
if (Test-Path $ddlHelperDir) {
    $ddlSources = @(
        (Join-Path $ddlHelperDir "DdlHelper.csproj"),
        (Join-Path $ddlHelperDir "Program.cs")
    )
    # Include any other .cs files that may be added later under DdlHelper/.
    $extraCs = Get-ChildItem -LiteralPath $ddlHelperDir -File -Filter "*.cs" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne (Join-Path $ddlHelperDir "Program.cs") } |
        ForEach-Object { $_.FullName }
    if ($extraCs) { $ddlSources += $extraCs }

    if (Test-AnyNewer -Sources $ddlSources -Target $ddlHelperExe) {
        Write-Host "  Publishing DdlHelper..." -ForegroundColor Gray
        dotnet publish "$ddlHelperDir\DdlHelper.csproj" -c Release -o $ddlHelperOutDir --nologo -v q 2>&1 | Out-Null
        if ($?) { Write-Host "  DdlHelper published" -ForegroundColor Green }
        else    { Write-Host "  DdlHelper publish failed (non-critical)" -ForegroundColor Yellow }
    } else {
        Write-Host "  DdlHelper skipped - source unchanged since publish" -ForegroundColor DarkGray
    }
}

# ErwinInjector + TriggerDll publish - REMOVED 2026-05-26.
# Both binaries are obsolete after the auto-load mechanism switched to
# PostMessage WM_COMMAND (see Services/WmCommandLogger.cs +
# scripts/autostart-watcher.ps1). The old injection path triggered SEP
# SONAR.ProcHijack on unsigned builds and got the executable quarantined
# in prod. Source folder scripts/erwin-injector/ is kept in the repo
# for git history; no longer built, no longer shipped.

$buildOutputDir = Join-Path $scriptDir "bin\Release\net10.0-windows"
if (-not (Test-Path (Join-Path $buildOutputDir "EliteSoft.Erwin.AddIn.dll"))) {
    Write-Host "DLL not found in build output!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
}

# --- Step 3: Snapshot watcher hash BEFORE we sync the install dir, so the
# tail-end recycle decision can compare "old deployed" vs "new on disk".
$watcherSrcPath = Join-Path $scriptDir "scripts\autostart-watcher.ps1"
$watcherDstPath = Join-Path $installDir "autostart-watcher.ps1"
$watcherOldHash = Get-FileHashSafe -Path $watcherDstPath
$watcherSrcHash = Get-FileHashSafe -Path $watcherSrcPath

# --- Step 4: Copy files via robocopy ---
# robocopy /XO skips files whose destination copy is newer-or-equal-age, so
# dotnet build's preserved mtimes on unchanged DLLs (third-party + projects
# with no source changes) avoid being re-copied. /NJH /NJS /NDL /NFL /NP
# silences the verbose default output; only the summary returns. Exit codes
# 0-7 are success (0=nothing copied, 1=files copied, others=mismatch/extras).
Write-Host "`n[3/5] Syncing $installDir..." -ForegroundColor Yellow
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}
# Wipe transient TLB/config so re-registered COM picks up the fresh ones
# even when robocopy /XO would have skipped them (rare edge - dotnet build
# regenerates these but mtime sometimes does not advance past the deployed
# copy when the typelib content is identical).
Remove-Item "$installDir\*.tlb"    -Force -ErrorAction SilentlyContinue
Remove-Item "$installDir\*.config" -Force -ErrorAction SilentlyContinue

$robocopyArgs = @(
    $buildOutputDir, $installDir,
    "/E", "/XO",
    "/NJH","/NJS","/NDL","/NFL","/NP","/R:2","/W:1"
)
$robocopyOutput = & robocopy.exe @robocopyArgs
$rcExit = $LASTEXITCODE
# robocopy exit codes 0-7 are non-fatal (0 = no files copied, 1 = files
# copied OK, 2 = extra files in dest, 4 = mismatched files, 8+ = real
# failure). Anything <= 7 is success.
if ($rcExit -ge 8) {
    Write-Host "  robocopy failed (exit=$rcExit):" -ForegroundColor Red
    Write-Host $robocopyOutput -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
}

# autostart-watcher.ps1 lives under scripts/ (not bin/), so the robocopy
# above misses it. Refresh only when the SHA1 differs from the snapshotted
# pre-copy hash - File.Copy resets mtime, which made the previous always-
# copy logic fool the recycle step into thinking the watcher changed on
# every run.
if (Test-Path -LiteralPath $watcherSrcPath) {
    if ($watcherSrcHash -ne $watcherOldHash) {
        [System.IO.File]::Copy($watcherSrcPath, $watcherDstPath, $true)
        Write-Host "  Refreshed autostart-watcher.ps1 (hash changed)" -ForegroundColor Gray
    }
} else {
    Write-Host "  WARNING: $watcherSrcPath missing - keeping installed watcher" -ForegroundColor Yellow
}

# ErwinInjector + TriggerDll deploy block removed 2026-05-26 along with
# the injection-based auto-load path. PostMessage WM_COMMAND replaces
# it (see scripts/autostart-watcher.ps1 + Services/WmCommandLogger.cs).

# license.lic check: addin's CheckLicenseStatus reads `<installDir>\license.lic`
# at startup and refuses to load without it. The file is NOT in the repo (it's
# RSA-signed against the specific machine's HWID) so robocopy can't bring it
# in. If the install dir was ever wiped, the file vanishes and the user gets
# a "License file not found" popup on next erwin launch. Surface the gap
# loudly here AND print the exact KeyGen command so the fix is one paste away.
$licDst = Join-Path $installDir "license.lic"
if (-not (Test-Path -LiteralPath $licDst)) {
    Write-Host "  WARNING: license.lic missing at $licDst - addin will fail license check." -ForegroundColor Yellow
    Write-Host "  Generate one with:" -ForegroundColor Yellow
    Write-Host "    cd C:\Users\Kursat\Repos\x-hw-licensing\KeyGen" -ForegroundColor Gray
    Write-Host "    Copy-Item ..\rsa_private_key.xml ." -ForegroundColor Gray
    Write-Host "    dotnet run -c Debug -- genlicense --hwid <YOUR_HWID> --licensee `"ErwinAddIn`" --features `"ErwinAddIn`" -o `"$licDst`"" -ForegroundColor Gray
    Write-Host "    Remove-Item rsa_private_key.xml" -ForegroundColor Gray
    Write-Host "  (HWID is shown in the 'Elite Soft - License Error' dialog if you launch the addin without one.)" -ForegroundColor Gray
}

# robocopy exit 0 == nothing copied (everything was up-to-date).
if ($rcExit -eq 0) {
    Write-Host "  Install dir already in sync (no files copied)" -ForegroundColor DarkGray
} else {
    Write-Host "  Sync complete (robocopy exit=$rcExit)" -ForegroundColor Green
}
# icacls grant skipped: $installDir lives under %LOCALAPPDATA% which the
# current user owns and can read/execute by default. The grant only
# mattered when the previous version dropped binaries into Program Files.

# --- Step 5: Register COM (HKCU, no UAC) ---
# Read the currently-registered comhost path; skip the unregister+register
# pair when HKCU already points at our just-copied comhost. The pair is
# idempotent (Register-ComUserScope writes with -Force), so skipping is
# purely a speed win when nothing changed - no behavioral difference.
Write-Host "`n[4/5] COM host registration (HKCU\Software\Classes)..." -ForegroundColor Yellow
$comHost = Join-Path $installDir "EliteSoft.Erwin.AddIn.comhost.dll"
if (-not (Test-Path -LiteralPath $comHost)) {
    Write-Host "  comhost.dll not found at $comHost" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
}
$inprocKey   = "HKCU:\Software\Classes\CLSID\$clsid\InProcServer32"
$currentPath = $null
try {
    $currentPath = (Get-ItemProperty -LiteralPath $inprocKey -Name "(default)" -ErrorAction Stop)."(default)"
} catch { $currentPath = $null }

# The original skip logic only checked the InProcServer32 path. After the
# 2026-05-25 ProgID rename ("EliteSoft.Erwin.AddIn" -> "EliteSoft.Meta.AddIn")
# the comhost path stayed the same but the new ProgID class hadn't been
# written - erwin's Add-In Manager then surfaced
# "Cannot find EliteSoft.Meta.AddIn Component" on activation. Also verify
# the ProgID key + the CLSID's ProgId back-pointer to catch ProgID drift.
$progIdRegistered  = Test-Path -LiteralPath "HKCU:\Software\Classes\$progId\CLSID"
$clsidProgIdValue  = $null
try {
    $clsidProgIdValue = (Get-ItemProperty -LiteralPath "HKCU:\Software\Classes\CLSID\$clsid\ProgId" -Name "(default)" -ErrorAction Stop)."(default)"
} catch { $clsidProgIdValue = $null }

if ($currentPath -and ($currentPath -ieq $comHost) -and $progIdRegistered -and ($clsidProgIdValue -ieq $progId)) {
    Write-Host "  HKCU already points at $comHost and ProgID '$progId' is current - skipped" -ForegroundColor DarkGray
} else {
    if (-not $progIdRegistered) {
        Write-Host "  ProgID '$progId' not registered yet (rename or fresh install) - re-registering" -ForegroundColor Gray
    } elseif ($clsidProgIdValue -and ($clsidProgIdValue -ine $progId)) {
        Write-Host "  CLSID\ProgId is '$clsidProgIdValue', want '$progId' - re-registering" -ForegroundColor Gray
    }
    try {
        # Unregister first to wipe any stale subkeys from older layouts that
        # would otherwise survive the -Force overwrite (e.g. a leftover
        # TypeLib subkey from a prior regsvr32 path).
        Unregister-ComUserScope
        Register-ComUserScope -comHostPath $comHost
        Write-Host "  COM registered (HKCU; no UAC needed)" -ForegroundColor Green
    } catch {
        Write-Host "  COM registration failed: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
    }
}

# --- Step 5b: Write erwin Add-In Manager entry (HKCU) ---
# Register-ComUserScope only writes the COM CLSID/ProgID. erwin DM r10 ALSO
# requires a per-user Add-Ins entry in its own registry tree to surface the
# addin in the Tools menu - HKLM Add-Ins are invisible there (empirically
# verified). Without this step the COM registration succeeds but erwin
# can't discover the addin so it never loads. install-impl.ps1 writes the same
# entry; we mirror it here so the dev loop is self-sufficient.
Write-Host "`n[5b/5] erwin Add-In Manager entry (HKCU)..." -ForegroundColor Yellow

# Legacy cleanup: pre-2026-05-25 builds wrote "Elite Soft Erwin Addin" with
# ProgID "EliteSoft.Erwin.AddIn". Sweep both the menu entry and the COM
# ProgID class so a rebuild doesn't leave a duplicate menu item or a
# stale ProgID -> CLSID lookup.
$legacyAddInPath = "HKCU:\SOFTWARE\erwin\Data Modeler\10.10\Add-Ins\$legacyAddInDisplay"
if (Test-Path -LiteralPath $legacyAddInPath) {
    Remove-Item -LiteralPath $legacyAddInPath -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Removed legacy menu entry '$legacyAddInDisplay'" -ForegroundColor Gray
}
$legacyProgIdPath = "HKCU:\Software\Classes\$legacyProgId"
if (Test-Path -LiteralPath $legacyProgIdPath) {
    Remove-Item -LiteralPath $legacyProgIdPath -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Removed legacy ProgID '$legacyProgId'" -ForegroundColor Gray
}
# AddinCmdId clearing REMOVED 2026-05-26: empirically the cmd id is
# stable across erwin restarts AND across menu-entry delete+recreate
# cycles (still 1181 with one addin registered). Aggressive clearing on
# every build-and-run was forcing the user to redo the 2-click discovery
# after every dev iteration. WmCommandLogger.MarkExecuteEntry self-heals
# if the id ever DOES drift, so leaving the cached value is safe.

$addInPath = "HKCU:\SOFTWARE\erwin\Data Modeler\10.10\Add-Ins\$addInDisplayName"
# Skip the four Set-ItemProperty writes when the existing values already
# match. Registry SetValue is microseconds-cheap so the win is small, but
# the "Skipped" log line is the useful signal that the run is idempotent.
$addInWanted = @{
    "Menu Identifier" = 1
    "ProgID"          = $progId
    "Invoke Method"   = "Execute"
    "Invoke EXE"      = 0
}
$addInOk = Test-Path -LiteralPath $addInPath
if ($addInOk) {
    $cur = Get-ItemProperty -LiteralPath $addInPath -ErrorAction SilentlyContinue
    foreach ($k in $addInWanted.Keys) {
        if ($null -eq $cur -or $cur.$k -ne $addInWanted[$k]) { $addInOk = $false; break }
    }
}
if ($addInOk) {
    Write-Host "  HKCU Add-In entry already current - skipped" -ForegroundColor DarkGray
} else {
    if (-not (Test-Path -LiteralPath $addInPath)) {
        New-Item -Path $addInPath -Force | Out-Null
    }
    Set-ItemProperty -LiteralPath $addInPath -Name "Menu Identifier" -Value 1            -Type DWord
    Set-ItemProperty -LiteralPath $addInPath -Name "ProgID"          -Value $progId      -Type String
    Set-ItemProperty -LiteralPath $addInPath -Name "Invoke Method"   -Value "Execute"    -Type String
    Set-ItemProperty -LiteralPath $addInPath -Name "Invoke EXE"      -Value 0            -Type DWord
    Write-Host "  HKCU Add-In entry written ('$addInDisplayName' -> $progId)" -ForegroundColor Green
}

# --- Step 6: Auto-start watcher health check ---
# Develop loop'unda watcher sessizce olebilir (process kill, OOM, vs). Build
# bittiginde task var ama watcher process yok ise tetikle - addin auto-load
# kanalinin acik kalmasini garanti et. Ayrica eski install'lardan kalma
# RestartCount'suz task ayarini bir kerelik patch'le. Eskiden burada "task
# yoksa run install-impl.ps1" diyip cikiyorduk; bu dev workflow'unu yariya
# birakiyordu (HKCU Add-Ins yazildigi halde watcher yokken erwin acilinca
# DLL injection tetiklenmiyor, Execute() cagrilmiyor). Simdi yoksa burada
# yaratiyoruz - User scope, $env:USERNAME suffix'li tek kullaniciya ozel
# task. install-impl.ps1'in Step 5 logic'i ile birebir ayni.
Write-Host "`n[5/5] Watcher health check..." -ForegroundColor Yellow
$userTaskName   = "EliteSoft Erwin AddIn AutoStart - $env:USERNAME"
$sharedTaskName = "EliteSoft Erwin AddIn AutoStart"
$task = Get-ScheduledTask -TaskName $userTaskName -ErrorAction SilentlyContinue
if (-not $task) { $task = Get-ScheduledTask -TaskName $sharedTaskName -ErrorAction SilentlyContinue }
$watcherTaskName = if ($task) { $task.TaskName } else { $userTaskName }

if (-not $task) {
    Write-Host "  No auto-start task found - registering '$watcherTaskName' (User scope)..." -ForegroundColor Yellow
    $watcherTarget = Join-Path $installDir "autostart-watcher.ps1"
    if (-not (Test-Path -LiteralPath $watcherTarget)) {
        Write-Host "  ERROR: $watcherTarget missing - cannot register watcher task." -ForegroundColor Red
    } else {
        try {
            $action   = New-ScheduledTaskAction -Execute 'powershell.exe' `
                -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$watcherTarget`""
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
                -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
            $trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
            Register-ScheduledTask -TaskName $watcherTaskName -Action $action -Trigger $trigger `
                -Settings $settings -Description 'Auto-starts Elite Soft Erwin Add-In when erwin opens (User scope, dev)' -ErrorAction Stop | Out-Null
            Write-Host "  Task '$watcherTaskName' created" -ForegroundColor Green
            Start-ScheduledTask -TaskName $watcherTaskName
            Start-Sleep -Seconds 2
        } catch {
            Write-Host "  ERROR: Could not register task: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
    $task = Get-ScheduledTask -TaskName $watcherTaskName -ErrorAction SilentlyContinue
}

if (-not $task) {
    Write-Host "  Auto-start task still not configured; addin will not auto-load on model open." -ForegroundColor Red
} else {
    # One-shot upgrade: ensure RestartCount is set on already-installed tasks
    if ($task.Settings.RestartCount -lt 3) {
        Write-Host "  Patching task with restart-on-failure (3 retries, 1 min apart)..." -ForegroundColor Gray
        $newSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
        Set-ScheduledTask -TaskName $watcherTaskName -Settings $newSettings | Out-Null
    }

    # Recycle the watcher process only when the deployed script actually
    # changed (hash compare snapshotted before the copy step above). The
    # original logic always killed + restarted, which paid the 3-10s cold
    # PowerShell startup tax on every run - even when nothing about the
    # watcher had moved. PowerShell loads a script into memory once at
    # process start; if the file on disk is byte-identical to what the
    # running process loaded, the recycle is pure cost with zero benefit.
    $watcherChanged = ($watcherSrcHash -ne $watcherOldHash)
    $existingWatchers = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'autostart-watcher' })

    if (-not $watcherChanged -and $existingWatchers.Count -gt 0) {
        $first = $existingWatchers[0]
        Write-Host "  Watcher already running (PID=$($first.ProcessId)) and script unchanged - recycle skipped" -ForegroundColor DarkGray
        $watcherProc = $first
    } else {
        if ($existingWatchers.Count -gt 0) {
            foreach ($wp in $existingWatchers) {
                Write-Host "  Killing stale watcher PID=$($wp.ProcessId) (script changed or recycle requested)" -ForegroundColor Gray
                Stop-Process -Id $wp.ProcessId -Force -ErrorAction SilentlyContinue
            }
            Start-Sleep -Seconds 1
        }

        Write-Host "  Triggering ScheduledTask '$watcherTaskName'..." -ForegroundColor Gray
        Stop-ScheduledTask -TaskName $watcherTaskName -ErrorAction SilentlyContinue
        # The task may be Disabled intentionally (e.g. while debugging the
        # SEP-quarantine path or the WM_COMMAND discovery flow with no
        # injection). Detect this BEFORE Start-ScheduledTask so we degrade
        # to a clear skip instead of the cryptic "task is disabled" error.
        $taskState = (Get-ScheduledTask -TaskName $watcherTaskName -ErrorAction SilentlyContinue).State
        if ($taskState -eq 'Disabled') {
            Write-Host "  Watcher task is Disabled - skipping start." -ForegroundColor Yellow
            Write-Host "  Addin will NOT auto-load until the task is re-enabled:" -ForegroundColor Yellow
            Write-Host "    Enable-ScheduledTask -TaskName '$watcherTaskName'" -ForegroundColor Yellow
            $watcherProc = $null
        } else {
            Start-ScheduledTask  -TaskName $watcherTaskName
        }
        # Poll for the watcher to come up. Empirically takes 3-10 s on this
        # box (cold PowerShell start + script preload). The previous fixed
        # Start-Sleep -Seconds 2 tripped a false-failure path because the
        # watcher just had not spawned yet (verified 2026-05-14: build-and-run
        # reported "failed to start" 9 s before the watcher actually wrote
        # its "Watcher started" line to the log). Cap at 20 s.
        $watcherProc = $null
        $maxWaitSec = 20
        $waited = 0
        while ($waited -lt $maxWaitSec -and -not $watcherProc) {
            Start-Sleep -Seconds 1
            $waited++
            $watcherProc = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
                Where-Object { $_.CommandLine -match 'autostart-watcher' } | Select-Object -First 1
        }
    }

    if ($watcherProc) {
        Write-Host "  Watcher running (PID=$($watcherProc.ProcessId))" -ForegroundColor Green
    } else {
        # autostart-watcher.ps1 routes its log to
        # %LOCALAPPDATA%\EliteSoft\ErwinAddIn-Logs\ regardless of install
        # dir (so Machine-scope Program Files install does not need write
        # access there). The old "$installDir\autostart.log" hint was
        # stale - that file has not been written since the log relocation.
        $watcherLog = Join-Path $env:LOCALAPPDATA 'EliteSoft\ErwinAddIn-Logs\autostart.log'
        Write-Host "  Watcher failed to start within ${maxWaitSec}s - check $watcherLog" -ForegroundColor Red
    }
}

Write-Host "`nDone! Restart erwin to use the add-in." -ForegroundColor Green
Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
