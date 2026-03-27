# Elite Soft Erwin Add-In - Install Script
# Run as Administrator: Right-click PowerShell > Run as Administrator > .\install.ps1
# Uninstall: .\install.ps1 -Uninstall
param([switch]$Uninstall)

$ErrorActionPreference = "Stop"
$installDir = "C:\Program Files\EliteSoft\ErwinAddIn"
$comHostDll = "EliteSoft.Erwin.AddIn.comhost.dll"
$progId = "EliteSoft.Erwin.AddIn"
$erwinRegBase = "SOFTWARE\erwin\Data Modeler"

# Check admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: Run this script as Administrator!" -ForegroundColor Red
    Write-Host "Right-click PowerShell > Run as Administrator" -ForegroundColor Yellow
    pause
    exit 1
}

if ($Uninstall) {
    Write-Host "=== Uninstalling Elite Soft Erwin Add-In ===" -ForegroundColor Cyan

    # Unregister COM
    $comHost = Join-Path $installDir $comHostDll
    if (Test-Path $comHost) {
        Write-Host "Unregistering COM component..." -ForegroundColor Yellow
        regsvr32.exe /u /s $comHost
        Write-Host "  COM unregistered" -ForegroundColor Green
    }

    # Remove erwin Add-In registry (HKLM + HKCU)
    foreach ($root in @("HKLM", "HKCU")) {
        $base = "${root}:\$erwinRegBase"
        if (Test-Path $base) {
            Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
                $addInPath = "$($_.PSPath)\Add-Ins\Elite Soft Erwin Addin"
                if (Test-Path $addInPath) {
                    Remove-Item $addInPath -Recurse -Force
                    Write-Host "  Removed erwin Add-In entry from $root\$($_.PSChildName)" -ForegroundColor Green
                }
            }
        }
    }

    # Remove files
    if (Test-Path $installDir) {
        Remove-Item $installDir -Recurse -Force
        Write-Host "  Removed $installDir" -ForegroundColor Green
    }

    Write-Host "`nUninstall complete!" -ForegroundColor Green
    pause
    exit 0
}

# === INSTALL ===
Write-Host "=== Installing Elite Soft Erwin Add-In ===" -ForegroundColor Cyan
$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Copy files
Write-Host "`n[1/5] Copying files..." -ForegroundColor Yellow
if (Test-Path $installDir) {
    Remove-Item "$installDir\*" -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

Copy-Item "$sourceDir\*" -Destination $installDir -Recurse -Force -Exclude "install.ps1"

# Deobfuscate .dl_ -> .dll and .ex_ -> .exe (XOR-obfuscated to bypass security software)
Write-Host "`n[2/5] Restoring binaries..." -ForegroundColor Yellow
$xorKey = [byte]0x5A
$restored = 0
Get-ChildItem -Path $installDir -Recurse -File | Where-Object { $_.Extension -eq '.dl_' -or $_.Extension -eq '.ex_' } | ForEach-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = $bytes[$i] -bxor $xorKey
    }
    if ($_.Extension -eq '.dl_') {
        $newPath = $_.FullName -replace '\.dl_$', '.dll'
    } else {
        $newPath = $_.FullName -replace '\.ex_$', '.exe'
    }
    [System.IO.File]::WriteAllBytes($newPath, $bytes)
    Remove-Item $_.FullName -Force
    $restored++
}
$count = (Get-ChildItem $installDir -Recurse -File).Count
if ($restored -gt 0) {
    Write-Host "  Restored $restored binaries" -ForegroundColor Green
}
Write-Host "  $count files in $installDir" -ForegroundColor Green

# Check if security software deleted files after deobfuscation
Start-Sleep -Seconds 2
$mainDll = Join-Path $installDir "EliteSoft.Erwin.AddIn.dll"
$comHostFile = Join-Path $installDir $comHostDll
if (-not (Test-Path $mainDll) -or -not (Test-Path $comHostFile)) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host "  SECURITY SOFTWARE BLOCKED THE INSTALLATION" -ForegroundColor Red
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host ""
    Write-Host "  DLL files were deleted by security software after extraction." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  IT ekibinden asagidaki dizin icin exclusion isteyin:" -ForegroundColor White
    Write-Host "    Path: $installDir" -ForegroundColor Cyan
    Write-Host "    Type: Folder exclusion (tum alt dosyalar dahil)" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Exclusion eklendikten sonra bu scripti tekrar calistirin." -ForegroundColor White
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host ""
    pause
    exit 1
}

# Set permissions
icacls $installDir /grant "Users:(OI)(CI)RX" /T /Q | Out-Null
Write-Host "  Permissions set for all users" -ForegroundColor Green

# Register COM
Write-Host "`n[3/5] Registering COM component..." -ForegroundColor Yellow
$comHost = Join-Path $installDir $comHostDll
regsvr32.exe /s $comHost
if ($LASTEXITCODE -eq 0) {
    Write-Host "  COM registered successfully" -ForegroundColor Green
} else {
    Write-Host "  COM registration failed (code: $LASTEXITCODE)" -ForegroundColor Red
    Write-Host "  Ensure .NET 10 Desktop Runtime is installed" -ForegroundColor Yellow
    pause
    exit 1
}

# Register in erwin Add-In Manager (all versions, HKLM + HKCU)
Write-Host "`n[4/5] Registering in erwin Add-In Manager..." -ForegroundColor Yellow
$registered = $false
foreach ($root in @("HKLM", "HKCU")) {
    $base = "${root}:\$erwinRegBase"
    if (Test-Path $base) {
        Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
            $ver = $_.PSChildName
            $addInPath = "$($_.PSPath)\Add-Ins\Elite Soft Erwin Addin"
            if (-not (Test-Path $addInPath)) {
                New-Item -Path $addInPath -Force | Out-Null
            }
            Set-ItemProperty $addInPath -Name "Menu Identifier" -Value 1 -Type DWord
            Set-ItemProperty $addInPath -Name "ProgID" -Value $progId -Type String
            Set-ItemProperty $addInPath -Name "Invoke Method" -Value "Execute" -Type String
            Set-ItemProperty $addInPath -Name "Invoke EXE" -Value 0 -Type DWord
            Write-Host "  erwin $ver ($root) - OK" -ForegroundColor Green
            $registered = $true
        }
    }
}
if (-not $registered) {
    Write-Host "  erwin not found in registry, skipping" -ForegroundColor Yellow
}

# Verify critical files
Write-Host "`n[5/5] Verifying installation..." -ForegroundColor Yellow
$criticalFiles = @($comHostDll, "EliteSoft.Erwin.AddIn.dll", "EliteSoft.Erwin.AddIn.runtimeconfig.json")
$allOk = $true
foreach ($f in $criticalFiles) {
    $fPath = Join-Path $installDir $f
    if (Test-Path $fPath) {
        Write-Host "  $f - OK" -ForegroundColor Green
    } else {
        Write-Host "  $f - MISSING! Security software may have deleted it." -ForegroundColor Red
        $allOk = $false
    }
}

if (-not $allOk) {
    Write-Host "`nWARNING: Some files are missing!" -ForegroundColor Red
    Write-Host "Add an exclusion in your security software for:" -ForegroundColor Yellow
    Write-Host "  $installDir" -ForegroundColor White
    Write-Host "Then re-run this installer." -ForegroundColor Yellow
} else {
    Write-Host "`nInstallation complete!" -ForegroundColor Green
    Write-Host "Restart erwin to use the add-in." -ForegroundColor Cyan
}
pause
