# Elite Soft Erwin Add-In - Install Script
# Usage:
#   .\install.ps1                                                    Install (Machine scope, default - HKLM, needs Admin)
#   .\install.ps1 -Scope User                                        Install (User scope - HKCU, current user only)
#   .\install.ps1 -Uninstall                                         Uninstall
#   .\install.ps1 -?                                                 Show this help
#
param(
    [switch]$Uninstall,
    [ValidateSet("User", "Machine")]
    [string]$Scope = "Machine",
    [Alias("?")]
    [switch]$Help
)

$ErrorActionPreference = "Stop"

if ($Help) {
    Write-Host ""
    Write-Host "Elite Soft Erwin Add-In - Installer" -ForegroundColor Cyan
    Write-Host "===================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  .\install.ps1                     " -NoNewline; Write-Host "Install machine-wide (HKLM, default, requires Admin)" -ForegroundColor Gray
    Write-Host "  .\install.ps1 -Scope User         " -NoNewline; Write-Host "Install for current user only (HKCU)" -ForegroundColor Gray
    Write-Host "  .\install.ps1 -Uninstall          " -NoNewline; Write-Host "Uninstall (auto-detects scope from registry.scope file)" -ForegroundColor Gray
    Write-Host "  .\install.ps1 -?                  " -NoNewline; Write-Host "Show this help" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Notes:" -ForegroundColor Yellow
    Write-Host "  - Default is -Scope Machine (production install for all users)" -ForegroundColor Gray
    Write-Host "  - -Scope Machine writes to HKLM\SOFTWARE\erwin\... (all users)" -ForegroundColor Gray
    Write-Host "  - -Scope User writes to HKCU\SOFTWARE\erwin\... (current user only)" -ForegroundColor Gray
    Write-Host "  - Stale entries in the opposite hive are cleaned automatically" -ForegroundColor Gray
    Write-Host "  - Machine scope requires Admin privileges and erwin to be installed machine-wide" -ForegroundColor Gray
    Write-Host ""
    exit 0
}

# Install dir depends on scope:
#   User    -> %LOCALAPPDATA%\EliteSoft\ErwinAddIn  (current user only, no admin)
#   Machine -> %ProgramFiles%\EliteSoft\ErwinAddIn  (all users, Admin required)
$userInstallDir    = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn"
$machineInstallDir = Join-Path $env:ProgramFiles "EliteSoft\ErwinAddIn"
$installDir = if ($Scope -eq "Machine") { $machineInstallDir } else { $userInstallDir }
$comHostDll = "EliteSoft.Erwin.AddIn.comhost.dll"
$progId = "EliteSoft.Erwin.AddIn"
$erwinRegBase = "SOFTWARE\erwin\Data Modeler"

if ($Uninstall) {
    Write-Host "=== Uninstalling Elite Soft Erwin Add-In ===" -ForegroundColor Cyan

    # Detect actual install location: prefer dir with registry.scope file;
    # fall back to whichever dir exists. Machine installs write registry.scope=HKLM,
    # User installs omit the file.
    $detectedInstallDir = $null
    if (Test-Path (Join-Path $machineInstallDir "registry.scope")) {
        $detectedInstallDir = $machineInstallDir
    } elseif (Test-Path (Join-Path $userInstallDir "registry.scope")) {
        $detectedInstallDir = $userInstallDir
    } elseif (Test-Path $machineInstallDir) {
        $detectedInstallDir = $machineInstallDir
    } elseif (Test-Path $userInstallDir) {
        $detectedInstallDir = $userInstallDir
    }
    if ($detectedInstallDir) { $installDir = $detectedInstallDir }
    else { Write-Host "  No existing install found at $machineInstallDir or $userInstallDir" -ForegroundColor Yellow }

    # Detect scope from installed registry.scope file (present only for Machine installs)
    $scopeFile = Join-Path $installDir "registry.scope"
    $uninstallHive = "HKCU"
    if (Test-Path $scopeFile) {
        $content = (Get-Content $scopeFile -Raw).Trim()
        if ($content -eq "HKLM") { $uninstallHive = "HKLM" }
    }
    Write-Host "  Detected scope: $uninstallHive (installDir: $installDir)" -ForegroundColor Gray

    # Unregister COM
    $comHost = Join-Path $installDir $comHostDll
    if (Test-Path $comHost) {
        Write-Host "Unregistering COM component..." -ForegroundColor Yellow
        regsvr32.exe /u /s $comHost 2>&1 | Out-Null
        Write-Host "  COM unregistered" -ForegroundColor Green
    }

    # Remove erwin Add-In registry from BOTH hives to avoid stale entries after scope changes.
    # (Primary scope is $uninstallHive but we clean both for safety.)
    $oppositeUninstallHive = if ($uninstallHive -eq "HKLM") { "HKCU" } else { "HKLM" }
    foreach ($hive in @($uninstallHive, $oppositeUninstallHive)) {
        $base = "${hive}:\$erwinRegBase"
        if (Test-Path $base) {
            Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
                $addInPath = "$($_.PSPath)\Add-Ins\Elite Soft Erwin Addin"
                if (Test-Path $addInPath) {
                    try {
                        Remove-Item $addInPath -Recurse -Force -ErrorAction Stop
                        Write-Host "  Removed erwin Add-In entry from $hive\$($_.PSChildName)" -ForegroundColor Green
                    } catch {
                        Write-Host "  WARNING: Could not remove $hive\$($_.PSChildName) Add-In entry: $($_.Exception.Message)" -ForegroundColor Yellow
                    }
                }
            }
        }
    }

    # Remove auto-start watcher task
    $taskName = "EliteSoft Erwin AddIn AutoStart"
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "  Removed Scheduled Task '$taskName'" -ForegroundColor Green

    # Remove MetaRepo registry (both hives for clean uninstall)
    foreach ($hive in @("HKCU", "HKLM")) {
        $bootstrapPath = "${hive}:\Software\EliteSoft\MetaRepo\Bootstrap"
        $extPath = "${hive}:\Software\EliteSoft\MetaRepo\Extension"
        if (Test-Path $bootstrapPath) { Remove-Item $bootstrapPath -Recurse -Force; Write-Host "  Removed Bootstrap config ($hive)" -ForegroundColor Green }
        if (Test-Path $extPath) { Remove-Item $extPath -Recurse -Force; Write-Host "  Removed Extension config ($hive)" -ForegroundColor Green }
    }

    # Remove files from BOTH possible install dirs (safety for scope transitions / stale installs)
    foreach ($dir in @($machineInstallDir, $userInstallDir) | Select-Object -Unique) {
        if (Test-Path $dir) {
            try {
                Remove-Item $dir -Recurse -Force -ErrorAction Stop
                Write-Host "  Removed $dir" -ForegroundColor Green
            } catch {
                Write-Host "  WARNING: Could not remove ${dir}: $($_.Exception.Message)" -ForegroundColor Yellow
                if ($dir -eq $machineInstallDir) {
                    Write-Host "           Re-run uninstall as Admin to clean Program Files." -ForegroundColor Gray
                }
            }
        }
    }

    # Remove per-user watcher logs (current user only; other users' logs stay)
    $watcherLogDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn-Logs"
    if (Test-Path $watcherLogDir) {
        Remove-Item $watcherLogDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "`nUninstall complete!" -ForegroundColor Green
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 0
}

# === INSTALL ===
Write-Host "=== Installing Elite Soft Erwin Add-In (Scope: $Scope) ===" -ForegroundColor Cyan
$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Enforce admin for Machine scope (HKLM writes require elevation)
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($Scope -eq "Machine" -and -not $isAdmin) {
    Write-Host ""
    Write-Host "  ERROR: -Scope Machine requires Administrator privileges." -ForegroundColor Red
    Write-Host "         HKLM registry writes will fail without elevation." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Right-click PowerShell, 'Run as administrator', then re-run:" -ForegroundColor Yellow
    Write-Host "    .\install.ps1 -Scope Machine" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Or install for the current user only:" -ForegroundColor Yellow
    Write-Host "    .\install.ps1                  # default -Scope User" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Press any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
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
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}

# Stop ALL watcher processes (prevents duplicates, unlocks COM host DLL)
try {
    $taskName = "EliteSoft Erwin AddIn AutoStart"
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    # Kill ALL autostart-watcher PowerShell processes
    Get-WmiObject Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match "autostart-watcher" } |
        ForEach-Object {
            Write-Host "  Stopping watcher process PID=$($_.ProcessId)" -ForegroundColor Gray
            $_.Terminate() | Out-Null
        }
    Start-Sleep -Seconds 2
} catch { }

# Check if erwin is running for CURRENT USER (locks COM host DLL)
$currentSessionId = (Get-Process -Id $PID).SessionId
$erwinProcess = Get-Process -Name "erwin" -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $currentSessionId }
if ($erwinProcess) {
    Write-Host ""
    Write-Host "  WARNING: erwin is running! It must be closed before installation." -ForegroundColor Red
    Write-Host "  Close erwin and press any key to continue, or Ctrl+C to cancel." -ForegroundColor Yellow
    Write-Host ""
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')

    # Check again
    $erwinProcess = Get-Process -Name "erwin" -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $currentSessionId }
    if ($erwinProcess) {
        Write-Host "  erwin is still running. Aborting installation." -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }
}

# Step 1: Unregister old COM + copy files
Write-Host "`n[1/3] Copying files to $installDir..." -ForegroundColor Yellow
if (Test-Path $installDir) {
    $oldComHost = Join-Path $installDir $comHostDll
    if (Test-Path $oldComHost) {
        Write-Host "  Unregistering old COM component..." -ForegroundColor Gray
        regsvr32.exe /u /s $oldComHost 2>&1 | Out-Null
    }
    Remove-Item "$installDir\*" -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

Copy-Item "$sourceDir\*" -Destination $installDir -Recurse -Force -Exclude "install.ps1"
$count = (Get-ChildItem $installDir -Recurse -File).Count
Write-Host "  Copied $count files" -ForegroundColor Green

# Write registry.scope file
$scopeFile = Join-Path $installDir "registry.scope"
if ($Scope -eq "Machine") {
    Set-Content $scopeFile "HKLM" -Encoding UTF8
    Write-Host "  Registry scope: Machine (HKLM)" -ForegroundColor Green
} else {
    if (Test-Path $scopeFile) { Remove-Item $scopeFile -Force }
    Write-Host "  Registry scope: User (HKCU, default)" -ForegroundColor Green
}

# Step 2: Register COM component (requires admin for regsvr32)
Write-Host "`n[2/3] Registering COM component..." -ForegroundColor Yellow
$comHost = Join-Path $installDir $comHostDll
# $isAdmin already computed at script start
if ($isAdmin) {
    regsvr32.exe /s $comHost
} else {
    # Elevate just for regsvr32 (UAC prompt)
    Write-Host "  COM registration requires admin - requesting elevation..." -ForegroundColor Yellow
    $regProc = Start-Process regsvr32.exe -ArgumentList "/s `"$comHost`"" -Verb RunAs -Wait -PassThru -ErrorAction Stop
    $LASTEXITCODE = $regProc.ExitCode
}
if ($LASTEXITCODE -eq 0) {
    Write-Host "  COM registered" -ForegroundColor Green
} else {
    Write-Host "  COM registration failed (code: $LASTEXITCODE)" -ForegroundColor Red
    Write-Host "  Ensure .NET 10 Desktop Runtime is installed" -ForegroundColor Yellow
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}

# Step 3: Register in erwin Add-In Manager
$regHive = if ($Scope -eq "Machine") { "HKLM" } else { "HKCU" }
$oppositeHive = if ($regHive -eq "HKLM") { "HKCU" } else { "HKLM" }
Write-Host "`n[3/3] Registering in erwin Add-In Manager ($regHive)..." -ForegroundColor Yellow

# Step 3a: Clean stale add-in entries in the OPPOSITE hive (prevents old HKCU/HKLM
# registrations from masking the new scope — this is the root cause of "I chose
# Machine but it loads from HKCU" bugs).
$oppositeBase = "${oppositeHive}:\$erwinRegBase"
if (Test-Path $oppositeBase) {
    $cleaned = 0
    Get-ChildItem $oppositeBase -ErrorAction SilentlyContinue | ForEach-Object {
        $stalePath = "$($_.PSPath)\Add-Ins\Elite Soft Erwin Addin"
        if (Test-Path $stalePath) {
            try {
                Remove-Item $stalePath -Recurse -Force -ErrorAction Stop
                Write-Host "  Removed stale $oppositeHive\$($_.PSChildName) entry" -ForegroundColor Gray
                $cleaned++
            } catch {
                if ($oppositeHive -eq "HKLM") {
                    Write-Host "  WARNING: Could not remove stale HKLM entry (needs Admin). Run 'install.ps1 -Scope Machine' as Admin once to clean it, or delete manually." -ForegroundColor Yellow
                } else {
                    Write-Host "  WARNING: Could not remove stale ${oppositeHive} entry: $($_.Exception.Message)" -ForegroundColor Yellow
                }
            }
        }
    }
    if ($cleaned -gt 0) {
        Write-Host "  Cleaned $cleaned stale $oppositeHive add-in entr$(if ($cleaned -eq 1) {'y'} else {'ies'})" -ForegroundColor Green
    }
}

# Step 3b: Write to target hive
$base = "${regHive}:\$erwinRegBase"
if (Test-Path $base) {
    $registered = 0
    Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
        $addInPath = "$($_.PSPath)\Add-Ins\Elite Soft Erwin Addin"
        if (-not (Test-Path $addInPath)) {
            New-Item -Path $addInPath -Force | Out-Null
        }
        Set-ItemProperty $addInPath -Name "Menu Identifier" -Value 1 -Type DWord
        Set-ItemProperty $addInPath -Name "ProgID" -Value $progId -Type String
        Set-ItemProperty $addInPath -Name "Invoke Method" -Value "Execute" -Type String
        Set-ItemProperty $addInPath -Name "Invoke EXE" -Value 0 -Type DWord
        Write-Host "  erwin $($_.PSChildName) ($regHive) - OK" -ForegroundColor Green
        $registered++
    }
    if ($registered -eq 0) {
        Write-Host "  WARNING: erwin root $base exists but has no version subkeys; add-in not registered." -ForegroundColor Yellow
    }

    # Step 3c: For Machine scope, ALSO write to the installing Admin's HKCU.
    # erwin DM r10 requires a per-user HKCU Add-In entry to show the add-in in
    # its Tools menu; HKLM entries alone are not sufficient (empirically
    # verified). Admin gets their HKCU populated here; every other interactive
    # user's HKCU is populated by the watcher script (autostart-watcher.ps1)
    # on their first logon (via the Scheduled Task machine-wide trigger).
    if ($Scope -eq "Machine") {
        Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
            $ver = $_.PSChildName
            $hkcuAddIn = "HKCU:\SOFTWARE\erwin\Data Modeler\$ver\Add-Ins\Elite Soft Erwin Addin"
            if (-not (Test-Path $hkcuAddIn)) {
                New-Item -Path $hkcuAddIn -Force | Out-Null
            }
            Set-ItemProperty $hkcuAddIn -Name "Menu Identifier" -Value 1 -Type DWord
            Set-ItemProperty $hkcuAddIn -Name "ProgID" -Value $progId -Type String
            Set-ItemProperty $hkcuAddIn -Name "Invoke Method" -Value "Execute" -Type String
            Set-ItemProperty $hkcuAddIn -Name "Invoke EXE" -Value 0 -Type DWord
            Write-Host "  erwin $ver (HKCU - Admin's user) - OK" -ForegroundColor Green
        }
    }
} else {
    # Target hive has no erwin at all — this is a user mistake (wrong scope).
    Write-Host ""
    Write-Host "  ERROR: erwin is not installed in $regHive." -ForegroundColor Red
    Write-Host "         Path not found: $base" -ForegroundColor Gray
    if ($Scope -eq "Machine") {
        Write-Host "         erwin seems to be user-installed only. Re-run without -Scope Machine:" -ForegroundColor Yellow
        Write-Host "           .\install.ps1" -ForegroundColor Cyan
    } else {
        Write-Host "         erwin seems to be machine-installed only. Re-run with -Scope Machine (as Admin):" -ForegroundColor Yellow
        Write-Host "           .\install.ps1 -Scope Machine" -ForegroundColor Cyan
    }
    Write-Host ""
    Write-Host "Press any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}

# Configure auto-start watcher (DLL injection based)
Write-Host "`n[5] Configuring auto-start watcher..." -ForegroundColor Yellow
$taskName = "EliteSoft Erwin AddIn AutoStart"
$watcherSource = Join-Path $sourceDir "autostart-watcher.ps1"
$watcherTarget = Join-Path $installDir "autostart-watcher.ps1"

# Verify injection components exist in install dir
$injectorPath = Join-Path $installDir "ErwinInjector.exe"
$triggerDllPath = Join-Path $installDir "TriggerDll.dll"
if (-not (Test-Path $injectorPath)) {
    Write-Host "  WARNING: ErwinInjector.exe not found in package" -ForegroundColor Yellow
}
if (-not (Test-Path $triggerDllPath)) {
    Write-Host "  WARNING: TriggerDll.dll not found in package" -ForegroundColor Yellow
}

if (Test-Path $watcherSource) {
    Copy-Item $watcherSource $watcherTarget -Force

    # Remove old task if exists
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

    # Create Scheduled Task - runs at logon, hidden
    $action = New-ScheduledTaskAction -Execute "powershell.exe" `
        -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$watcherTarget`""

    if ($Scope -eq "Machine") {
        # Machine scope: fire for ANY interactive user at their logon, run in that user's session.
        # Group SID S-1-5-4 = NT AUTHORITY\INTERACTIVE.
        # MultipleInstances=Parallel is CRITICAL: each user's logon spawns its own watcher
        # instance in that user's session. With the default IgnoreNew, a stale instance
        # from a previous logon (whose watcher died but TS hasn't noticed) blocks new
        # users' logon-triggered instances.
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -MultipleInstances Parallel
        $trigger = New-ScheduledTaskTrigger -AtLogOn
        $principal = New-ScheduledTaskPrincipal -GroupId "S-1-5-4" -RunLevel Limited
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
            -Settings $settings -Description "Auto-starts Elite Soft Erwin Add-In for any logged-on user (Machine scope)" | Out-Null
        Write-Host "  Scheduled Task '$taskName' created (Machine: all interactive users, Parallel instances)" -ForegroundColor Green
    } else {
        # User scope: fire only for the installing user. Single instance is fine.
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero)
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
            -Settings $settings -Description "Auto-starts Elite Soft Erwin Add-In when erwin opens (User scope)" | Out-Null
        Write-Host "  Scheduled Task '$taskName' created (User: $env:USERNAME)" -ForegroundColor Green
    }
    Write-Host "  Add-in will auto-load when erwin starts" -ForegroundColor Gray

    # Start watcher immediately (don't wait for next logon). For Machine scope
    # this starts the watcher in the installing Admin's session; other users'
    # watchers start automatically at their next logon.
    Start-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
} else {
    Write-Host "  autostart-watcher.ps1 not found in package, skipping" -ForegroundColor Yellow
}

Write-Host "`nInstallation complete!" -ForegroundColor Green
Write-Host "Add-in will auto-load when erwin starts." -ForegroundColor Cyan
Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
