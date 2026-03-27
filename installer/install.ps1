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
Write-Host "`n[1/3] Copying files..." -ForegroundColor Yellow
if (Test-Path $installDir) {
    Remove-Item "$installDir\*" -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

Copy-Item "$sourceDir\*" -Destination $installDir -Recurse -Force -Exclude "install.ps1"
$count = (Get-ChildItem $installDir -Recurse -File).Count
Write-Host "  Copied $count files to $installDir" -ForegroundColor Green

# Set permissions
icacls $installDir /grant "Users:(OI)(CI)RX" /T /Q | Out-Null
Write-Host "  Permissions set for all users" -ForegroundColor Green

# Register COM
Write-Host "`n[2/3] Registering COM component..." -ForegroundColor Yellow
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
Write-Host "`n[3/3] Registering in erwin Add-In Manager..." -ForegroundColor Yellow
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

Write-Host "`nInstallation complete!" -ForegroundColor Green
Write-Host "Restart erwin to use the add-in." -ForegroundColor Cyan
pause
