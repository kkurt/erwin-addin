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

    # Force-kills erwin.exe / DdlHelper.exe / ErwinInjector.exe owned by ANY
    # user, not just $env:USERNAME. Needed when a stale process from another
    # session is holding our install dir or the COM host. Off by default
    # because killing another logged-in user's editor session is destructive.
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
$progId     = "EliteSoft.Erwin.AddIn"
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
    if (-not $procs) { return }
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
        } elseif ($ownerUser -and $ownerUser -ieq $myUser) {
            Write-Host "  Killing ${name}.exe PID=$($p.ProcessId) (user=$ownerUser, started $started)" -ForegroundColor Yellow
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
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
}

if ($KillAllErwinProcs) {
    Write-Host "`nClosing erwin / DdlHelper / ErwinInjector (ALL USERS, -KillAllErwinProcs)..." -ForegroundColor Red
} else {
    Write-Host "`nClosing erwin / DdlHelper / ErwinInjector (user=$myUser)..." -ForegroundColor Yellow
}
Stop-UserProcesses "erwin"          -All:$KillAllErwinProcs
Stop-UserProcesses "DdlHelper"      -All:$KillAllErwinProcs
Stop-UserProcesses "ErwinInjector"  -All:$KillAllErwinProcs
Start-Sleep -Seconds 2

# --- Step 2a: Build native bridge (cl.exe) ---
# Must run BEFORE dotnet build so the csproj Copy task picks up the fresh DLL.
# build-and-run.ps1 is the dev "do-it-all" entry point - bridge changes
# need to compile here too, otherwise managed-only rebuild ships a stale DLL.
# We verify success by checking the DLL timestamp advanced after the call;
# build.ps1 itself uses $ErrorActionPreference=Stop + throw on failure, so
# any compile error halts the sub-script before it returns.
$bridgeScript = Join-Path $scriptDir "scripts\native-bridge\build.ps1"
$bridgeDll    = Join-Path $scriptDir "scripts\native-bridge\ErwinNativeBridge.dll"
if (Test-Path $bridgeScript) {
    Write-Host "`n[1a/5] Building native bridge (cl.exe)..." -ForegroundColor Yellow
    $beforeMtime = if (Test-Path $bridgeDll) { (Get-Item $bridgeDll).LastWriteTime } else { [DateTime]::MinValue }
    & $bridgeScript
    if (-not (Test-Path $bridgeDll)) {
        Write-Host "Native bridge build failed - DLL not produced!" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
    }
    $afterMtime = (Get-Item $bridgeDll).LastWriteTime
    if ($afterMtime -le $beforeMtime) {
        Write-Host "  Native bridge unchanged (still cached, OK)" -ForegroundColor Green
    } else {
        Write-Host "  Native bridge built! (refreshed at $afterMtime)" -ForegroundColor Green
    }
} else {
    Write-Host "`n[1a/5] (skipped) native bridge script not found at $bridgeScript" -ForegroundColor DarkGray
}

# --- Step 2b: Build managed code ---
Write-Host "`n[1b/5] Building project..." -ForegroundColor Yellow
dotnet clean erwin-addin.sln 2>&1 | Out-Null
# Explicit restore between clean and build. `dotnet build` does implicit
# restore but it sometimes misses cross-repo ProjectReferences (e.g.
# ..\erwin-admin\MetaShared) after clean wipes their obj/ folders, producing
# NETSDK1004 "Assets file ... not found". Verified 2026-05-13.
dotnet restore erwin-addin.sln 2>&1 | Out-Null
dotnet build erwin-addin.sln -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
}
Write-Host "  Build successful!" -ForegroundColor Green

# Build DdlHelper tool
Write-Host "  Building DdlHelper..." -ForegroundColor Gray
$ddlHelperDir = Join-Path $scriptDir "tools\DdlHelper"
if (Test-Path $ddlHelperDir) {
    dotnet publish "$ddlHelperDir\DdlHelper.csproj" -c Release -o (Join-Path $scriptDir "bin\Release\net10.0-windows\tools\DdlHelper") 2>&1 | Out-Null
    if ($?) { Write-Host "  DdlHelper built!" -ForegroundColor Green }
    else { Write-Host "  DdlHelper build failed (non-critical)" -ForegroundColor Yellow }
}

$buildOutputDir = Join-Path $scriptDir "bin\Release\net10.0-windows"
if (-not (Test-Path (Join-Path $buildOutputDir "EliteSoft.Erwin.AddIn.dll"))) {
    Write-Host "DLL not found in build output!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
}

# --- Step 3: Unregister old version ---
# Strip any HKCU CLSID/ProgID entries from a previous run so the fresh
# Step 5 write below points at the just-copied comhost.dll. We do NOT touch
# HKLM here - earlier builds may have used regsvr32 (HKLM Software\Classes)
# but going forward all dev installs are HKCU-only. Leftover HKLM entries
# don't hurt because erwin reads HKCR (HKCU wins on read), but they can be
# cleaned manually with regsvr32 /u in an elevated shell if desired.
Write-Host "`n[2/5] Unregistering old COM (HKCU)..." -ForegroundColor Yellow
Unregister-ComUserScope
Write-Host "  HKCU CLSID/ProgID entries cleared" -ForegroundColor Green

# --- Step 4: Copy files ---
Write-Host "`n[3/5] Installing to $installDir..." -ForegroundColor Yellow
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
} else {
    Remove-Item "$installDir\*.tlb" -Force -ErrorAction SilentlyContinue
    Remove-Item "$installDir\*.config" -Force -ErrorAction SilentlyContinue
}

Copy-Item -Path "$buildOutputDir\*" -Destination $installDir -Recurse -Force

# autostart-watcher.ps1 lives under scripts/ (not bin/), so the bin->install
# copy above misses it. Without this explicit refresh the install dir keeps
# whatever watcher version was first deployed (often the very first one),
# meaning auto-load improvements made in the repo never reach the running
# Scheduled Task. We restart the task at the end of build-and-run, so
# whatever sits at this path is what runs next - keep it in sync.
$watcherSrc = Join-Path $scriptDir "scripts\autostart-watcher.ps1"
$watcherDst = Join-Path $installDir "autostart-watcher.ps1"
if (Test-Path -LiteralPath $watcherSrc) {
    [System.IO.File]::Copy($watcherSrc, $watcherDst, $true)
    Write-Host "  Refreshed autostart-watcher.ps1 from scripts/" -ForegroundColor Gray
} else {
    Write-Host "  WARNING: $watcherSrc missing - keeping installed watcher" -ForegroundColor Yellow
}

$copiedCount = (Get-ChildItem -LiteralPath $installDir -Recurse -File).Count
Write-Host "  Copied $copiedCount files" -ForegroundColor Green
# icacls grant skipped: $installDir lives under %LOCALAPPDATA% which the
# current user owns and can read/execute by default. The grant only
# mattered when the previous version dropped binaries into Program Files.

# --- Step 5: Register COM (HKCU, no UAC) ---
Write-Host "`n[4/5] Registering COM host (HKCU\Software\Classes)..." -ForegroundColor Yellow
$comHost = Join-Path $installDir "EliteSoft.Erwin.AddIn.comhost.dll"
if (-not (Test-Path -LiteralPath $comHost)) {
    Write-Host "  comhost.dll not found at $comHost" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
}
try {
    Register-ComUserScope -comHostPath $comHost
    Write-Host "  COM registered (HKCU; no UAC needed)" -ForegroundColor Green
} catch {
    Write-Host "  COM registration failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
}

# --- Step 5b: Write erwin Add-In Manager entry (HKCU) ---
# Register-ComUserScope only writes the COM CLSID/ProgID. erwin DM r10 ALSO
# requires a per-user Add-Ins entry in its own registry tree to surface the
# addin in the Tools menu - HKLM Add-Ins are invisible there (empirically
# verified). Without this step the COM registration succeeds but erwin
# can't discover the addin so it never loads. install-impl.ps1 writes the same
# entry; we mirror it here so the dev loop is self-sufficient.
Write-Host "`n[5b/5] Registering in erwin Add-In Manager (HKCU)..." -ForegroundColor Yellow
$addInPath = "HKCU:\SOFTWARE\erwin\Data Modeler\10.10\Add-Ins\Elite Soft Erwin Addin"
if (-not (Test-Path -LiteralPath $addInPath)) {
    New-Item -Path $addInPath -Force | Out-Null
}
Set-ItemProperty -LiteralPath $addInPath -Name "Menu Identifier" -Value 1                       -Type DWord
Set-ItemProperty -LiteralPath $addInPath -Name "ProgID"          -Value "EliteSoft.Erwin.AddIn" -Type String
Set-ItemProperty -LiteralPath $addInPath -Name "Invoke Method"   -Value "Execute"               -Type String
Set-ItemProperty -LiteralPath $addInPath -Name "Invoke EXE"      -Value 0                       -Type DWord
Write-Host "  HKCU Add-In entry written" -ForegroundColor Green

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

    # ALWAYS recycle the watcher process. PowerShell loads a script into
    # memory once at process start, so a refreshed autostart-watcher.ps1
    # on disk (Step 3 just copied the new version) won't take effect
    # until the running watcher is killed and the task starts a fresh
    # PS process. Without this recycle the dev loop keeps running the
    # original watcher version that was deployed first - any subsequent
    # change to scripts/autostart-watcher.ps1 silently has no effect.
    $existingWatchers = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'autostart-watcher' })
    if ($existingWatchers.Count -gt 0) {
        foreach ($wp in $existingWatchers) {
            Write-Host "  Killing stale watcher PID=$($wp.ProcessId) (will be replaced with refreshed script)" -ForegroundColor Gray
            Stop-Process -Id $wp.ProcessId -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Seconds 1
    }

    Write-Host "  Triggering ScheduledTask '$watcherTaskName'..." -ForegroundColor Gray
    Stop-ScheduledTask -TaskName $watcherTaskName -ErrorAction SilentlyContinue
    Start-ScheduledTask  -TaskName $watcherTaskName
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
    if ($watcherProc) {
        Write-Host "  Watcher running (PID=$($watcherProc.ProcessId), startup took ${waited}s)" -ForegroundColor Green
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
