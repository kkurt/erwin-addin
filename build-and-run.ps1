# Elite Soft Erwin Add-In - Build, Install & Register (Dev Workflow)
#
# Usage:
#   .\build-and-run.ps1          Build + install + COM register
#   .\build-and-run.ps1 -?       Show help
#
# Requires: .NET 10 SDK, Administrator privileges

param(
    [Alias('?')]
    [switch]$Help
)

if ($Help) {
    Write-Host ""
    Write-Host "Elite Soft Erwin Add-In - Dev Build Script" -ForegroundColor Cyan
    Write-Host "===========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Builds the project, installs to Program Files, registers COM host,"
    Write-Host "and configures erwin Add-In Manager. For daily development use."
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  .\build-and-run.ps1      Build + install + register"
    Write-Host "  .\build-and-run.ps1 -?   Show this help"
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

# --- Auto-elevate to Administrator ---
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`"" -Verb RunAs
    exit
}

Write-Host "=== Elite Soft Erwin Add-In - Build & Run ===" -ForegroundColor Cyan
Write-Host "Running as Administrator" -ForegroundColor Green

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

$installDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn"

# --- Step 1: Close processes that might hold our DLLs (CURRENT USER ONLY) ---
# Use WMI GetOwner so we catch processes whose UserName PowerShell can't
# inline-resolve (happens for cross-session or stale processes started on a
# different desktop). A lingering erwin.exe from a prior session can hold
# System.Management.dll and make step 3 (Copy-Item) fail with 'file in use'.
$myUser = $env:USERNAME
function Stop-UserProcesses {
    param([string]$name)
    $procs = Get-WmiObject Win32_Process -Filter "Name='$name.exe'" -ErrorAction SilentlyContinue
    if (-not $procs) { return }
    foreach ($p in $procs) {
        $owner = $null
        try { $owner = $p.GetOwner() } catch { $owner = $null }
        $ownerUser = if ($owner -and $owner.ReturnValue -eq 0) { $owner.User } else { $null }
        if ($ownerUser -and $ownerUser -ieq $myUser) {
            Write-Host "  Killing ${name}.exe PID=$($p.ProcessId) (user=$ownerUser, started $($p.ConvertToDateTime($p.CreationDate)))" -ForegroundColor Yellow
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        } elseif (-not $ownerUser) {
            # Ambiguous ownership - treat as ours if there's no cleaner signal.
            # Running elevated, so we have the rights. A stale process left
            # over from a prior login will otherwise hold our install dir.
            Write-Host "  Killing ${name}.exe PID=$($p.ProcessId) (owner unknown, started $($p.ConvertToDateTime($p.CreationDate)))" -ForegroundColor DarkYellow
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        } else {
            Write-Host "  Skipping ${name}.exe PID=$($p.ProcessId) (owner=$ownerUser, not ours)" -ForegroundColor Gray
        }
    }
}

Write-Host "`nClosing erwin / DdlHelper / ErwinInjector (user=$myUser)..." -ForegroundColor Yellow
Stop-UserProcesses "erwin"
Stop-UserProcesses "DdlHelper"
Stop-UserProcesses "ErwinInjector"
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
Write-Host "`n[2/5] Unregistering old version..." -ForegroundColor Yellow
$oldComHost = Join-Path $installDir "EliteSoft.Erwin.AddIn.comhost.dll"
if (Test-Path $oldComHost) {
    regsvr32.exe /u /s $oldComHost 2>&1 | Out-Null
    Write-Host "  Old COM host unregistered" -ForegroundColor Green
} else {
    Write-Host "  No previous installation (OK)" -ForegroundColor Gray
}

# --- Step 4: Copy files ---
Write-Host "`n[3/5] Installing to $installDir..." -ForegroundColor Yellow
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
} else {
    Remove-Item "$installDir\*.tlb" -Force -ErrorAction SilentlyContinue
    Remove-Item "$installDir\*.config" -Force -ErrorAction SilentlyContinue
}

Copy-Item -Path "$buildOutputDir\*" -Destination $installDir -Recurse -Force
$copiedCount = (Get-ChildItem -Path $installDir -Recurse -File).Count
Write-Host "  Copied $copiedCount files" -ForegroundColor Green

# Permissions
icacls $installDir /grant "Users:(OI)(CI)RX" /T /Q 2>&1 | Out-Null
Write-Host "  Permissions set" -ForegroundColor Green

# --- Step 5: Register COM ---
Write-Host "`n[4/5] Registering COM host..." -ForegroundColor Yellow
$comHost = Join-Path $installDir "EliteSoft.Erwin.AddIn.comhost.dll"
if (-not (Test-Path $comHost)) {
    Write-Host "  comhost.dll not found!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
}
regsvr32.exe /s $comHost
if ($LASTEXITCODE -ne 0) {
    Write-Host "  COM registration failed!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown"); exit 1
}
Write-Host "  COM registered" -ForegroundColor Green

Write-Host "`nDone! Restart erwin to use the add-in." -ForegroundColor Green
Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
