# Elite Soft Erwin Add-In — Install & Register Script (Production)
# Registers COM DLL and configures erwin Add-In Manager auto-load.
# Auto-elevates to Administrator if needed.
#
# Usage:
#   .\install.ps1                  — Install from default path
#   .\install.ps1 -SourceDir "X:\" — Install from custom source
#   .\install.ps1 -Uninstall       — Remove add-in completely

param(
    [string]$SourceDir,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

# ── Config ──────────────────────────────────────────────────────
$addInName      = "Elite Soft Erwin Addin"
$progId         = "EliteSoft.Erwin.AddIn"
$invokeMethod   = "Execute"
$dllName        = "EliteSoft.Erwin.AddIn.dll"
$installDir     = "C:\Program Files\EliteSoft\ErwinAddIn"
$regasm         = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"

# erwin Add-In Manager registry path
$erwinRegBase   = "HKCU:\SOFTWARE\erwin\Data Modeler"

# ── Auto-elevate ────────────────────────────────────────────────
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    $elevateArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
    if ($SourceDir) { $elevateArgs += " -SourceDir `"$SourceDir`"" }
    if ($Uninstall) { $elevateArgs += " -Uninstall" }
    Start-Process powershell.exe -ArgumentList $elevateArgs -Verb RunAs
    exit
}

# ── Detect erwin version from registry ──────────────────────────
function Get-ErwinVersion {
    if (-not (Test-Path $erwinRegBase)) {
        return $null
    }
    $versions = Get-ChildItem $erwinRegBase -ErrorAction SilentlyContinue | Select-Object -ExpandProperty PSChildName
    if ($versions) {
        # Return the latest version (highest number)
        return ($versions | Sort-Object { [version]($_ + ".0") } -Descending | Select-Object -First 1)
    }
    return $null
}

$erwinVersion = Get-ErwinVersion
if (-not $erwinVersion) {
    Write-Host "ERROR: erwin Data Modeler not found in registry." -ForegroundColor Red
    Write-Host "Please install erwin Data Modeler first, or run it at least once." -ForegroundColor Yellow
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

$addInsRegPath = "$erwinRegBase\$erwinVersion\Add-Ins\$addInName"

Write-Host "=== Elite Soft Erwin Add-In ===" -ForegroundColor Cyan
Write-Host "  erwin version : $erwinVersion" -ForegroundColor Gray
Write-Host "  Install dir   : $installDir" -ForegroundColor Gray
Write-Host ""

# ── Uninstall ───────────────────────────────────────────────────
if ($Uninstall) {
    Write-Host "[UNINSTALL MODE]" -ForegroundColor Yellow
    Write-Host ""

    # 1. Remove auto-start Scheduled Task
    $taskName = "EliteSoft Erwin AddIn AutoStart"
    $existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($existingTask) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "  Removed Scheduled Task" -ForegroundColor Green
    } else {
        Write-Host "  Scheduled Task not found (OK)" -ForegroundColor Gray
    }

    # 2. Remove Add-In Manager entry
    if (Test-Path $addInsRegPath) {
        Remove-Item -Path $addInsRegPath -Force
        Write-Host "  Removed Add-In Manager entry" -ForegroundColor Green
    } else {
        Write-Host "  Add-In Manager entry not found (OK)" -ForegroundColor Gray
    }

    # 3. Unregister COM
    $installedDll = Join-Path $installDir $dllName
    if (Test-Path $installedDll) {
        Write-Host "  Unregistering COM..." -ForegroundColor Yellow
        & $regasm $installedDll /unregister 2>&1 | Out-Null
        Write-Host "  COM unregistered" -ForegroundColor Green
    }

    # 4. Remove install directory
    if (Test-Path $installDir) {
        Remove-Item -Path $installDir -Recurse -Force
        Write-Host "  Removed $installDir" -ForegroundColor Green
    }

    Write-Host "`nUninstall complete." -ForegroundColor Green
    Write-Host "Restart erwin to apply changes." -ForegroundColor Yellow
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 0
}

# ── Determine source directory ──────────────────────────────────
if (-not $SourceDir) {
    # Default: look for build output relative to script location
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot = Split-Path -Parent $scriptDir
    $SourceDir = Join-Path $repoRoot "bin\Release\net48"
}

if (-not (Test-Path (Join-Path $SourceDir $dllName))) {
    Write-Host "ERROR: $dllName not found in $SourceDir" -ForegroundColor Red
    Write-Host "Build the project first or specify -SourceDir." -ForegroundColor Yellow
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

# ── Check if erwin is running ───────────────────────────────────
$erwinProcess = Get-Process -Name "erwin" -ErrorAction SilentlyContinue
if ($erwinProcess) {
    Write-Host "WARNING: erwin is running. It must be closed to install." -ForegroundColor Yellow
    $choice = Read-Host "Close erwin now? (Y/N)"
    if ($choice -eq 'Y' -or $choice -eq 'y') {
        $erwinProcess | Stop-Process -Force
        Start-Sleep -Seconds 2
        Write-Host "  erwin closed." -ForegroundColor Green
    } else {
        Write-Host "Aborted. Close erwin and try again." -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }
}

# ── Step 1: Unregister old COM (if exists) ──────────────────────
Write-Host "[1/5] Unregistering old version..." -ForegroundColor Yellow
$oldDll = Join-Path $installDir $dllName
if (Test-Path $oldDll) {
    & $regasm $oldDll /unregister 2>&1 | Out-Null
    Write-Host "  Old version unregistered" -ForegroundColor Green
} else {
    Write-Host "  No previous installation (OK)" -ForegroundColor Gray
}

# ── Step 2: Copy files ──────────────────────────────────────────
Write-Host "[2/5] Copying files to $installDir ..." -ForegroundColor Yellow
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

$filesToCopy = Get-ChildItem -Path $SourceDir -Include "*.dll","*.tlb","*.pdb","*.config" -Recurse
foreach ($file in $filesToCopy) {
    Copy-Item -Path $file.FullName -Destination $installDir -Force
}
Write-Host "  Copied $($filesToCopy.Count) files" -ForegroundColor Green

# Set read permissions for all users
$acl = Get-Acl $installDir
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule("Users", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($rule)
Set-Acl $installDir $acl

# ── Step 3: Register COM ────────────────────────────────────────
Write-Host "[3/5] Registering COM..." -ForegroundColor Yellow
$installedDll = Join-Path $installDir $dllName
$installedTlb = Join-Path $installDir "EliteSoft.Erwin.AddIn.tlb"
& $regasm $installedDll /codebase /tlb:$installedTlb

if ($LASTEXITCODE -ne 0) {
    Write-Host "COM registration failed!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}
Write-Host "  COM registered" -ForegroundColor Green

# ── Step 4: Add-In Manager registry entry ───────────────────────
Write-Host "[4/5] Configuring erwin Add-In Manager..." -ForegroundColor Yellow

if (-not (Test-Path $addInsRegPath)) {
    New-Item -Path $addInsRegPath -Force | Out-Null
}

Set-ItemProperty -Path $addInsRegPath -Name "Menu Identifier" -Value 1 -Type DWord
Set-ItemProperty -Path $addInsRegPath -Name "ProgID"          -Value $progId -Type String
Set-ItemProperty -Path $addInsRegPath -Name "Invoke Method"   -Value $invokeMethod -Type String
Set-ItemProperty -Path $addInsRegPath -Name "Invoke EXE"      -Value 0 -Type DWord

Write-Host "  Add-In Manager entry created" -ForegroundColor Green

# ── Step 5: Auto-start watcher (WMI event-driven) ────────────────
Write-Host "[5/5] Configuring auto-start watcher..." -ForegroundColor Yellow

$taskName = "EliteSoft Erwin AddIn AutoStart"
$watcherSource = Join-Path $scriptDir "autostart-watcher.ps1"
$watcherTarget = Join-Path $installDir "autostart-watcher.ps1"

if (Test-Path $watcherSource) {
    Copy-Item $watcherSource $watcherTarget -Force
    Write-Host "  Copied autostart-watcher.ps1" -ForegroundColor Green
}

# Remove old task if exists
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

# Create Scheduled Task - runs at logon, hidden
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$watcherTarget`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Settings $settings -Description "Auto-starts Elite Soft Erwin Add-In when erwin opens (WMI event)" | Out-Null

Write-Host "  Scheduled Task '$taskName' created" -ForegroundColor Green

# Start watcher immediately
Start-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue

# ── Summary ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Installation Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "  Files      : $installDir" -ForegroundColor Cyan
Write-Host "  COM ProgID : $progId" -ForegroundColor Cyan
Write-Host "  erwin Menu : Tools > Add-Ins > $addInName" -ForegroundColor Cyan
Write-Host "  Auto-start : Enabled (Scheduled Task)" -ForegroundColor Cyan
Write-Host ""
Write-Host "erwin acildiginda add-in otomatik baslatilacak." -ForegroundColor Yellow
Write-Host ""
Write-Host "Press any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
