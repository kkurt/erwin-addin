# Elite Soft Erwin Add-In - Install Script (No Admin Required)
# Usage:
#   .\install.ps1                                                    Install
#   .\install.ps1 -Uninstall                                         Uninstall
#   .\install.ps1 -MetaRepo "MetaRepoTTKOM" -DbUser "sa" -DbPass "123"  Install + configure DB
#
param(
    [switch]$Uninstall,
    [string]$MetaRepo,
    [string]$DbUser,
    [string]$DbPass
)

$ErrorActionPreference = "Stop"

# Install to user-local directory (no admin needed)
$installDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn"
$comHostDll = "EliteSoft.Erwin.AddIn.comhost.dll"
$progId = "EliteSoft.Erwin.AddIn"
$erwinRegBase = "SOFTWARE\erwin\Data Modeler"

if ($Uninstall) {
    Write-Host "=== Uninstalling Elite Soft Erwin Add-In ===" -ForegroundColor Cyan

    # Unregister COM (per-user)
    $comHost = Join-Path $installDir $comHostDll
    if (Test-Path $comHost) {
        Write-Host "Unregistering COM component..." -ForegroundColor Yellow
        regsvr32.exe /u /s $comHost 2>&1 | Out-Null
        Write-Host "  COM unregistered" -ForegroundColor Green
    }

    # Remove erwin Add-In registry (HKCU)
    $base = "HKCU:\$erwinRegBase"
    if (Test-Path $base) {
        Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
            $addInPath = "$($_.PSPath)\Add-Ins\Elite Soft Erwin Addin"
            if (Test-Path $addInPath) {
                Remove-Item $addInPath -Recurse -Force
                Write-Host "  Removed erwin Add-In entry from HKCU\$($_.PSChildName)" -ForegroundColor Green
            }
        }
    }

    # Remove MetaRepo registry
    $bootstrapPath = "HKCU:\Software\EliteSoft\MetaRepo\Bootstrap"
    $extPath = "HKCU:\Software\EliteSoft\MetaRepo\Extension"
    if (Test-Path $bootstrapPath) { Remove-Item $bootstrapPath -Recurse -Force; Write-Host "  Removed Bootstrap config" -ForegroundColor Green }
    if (Test-Path $extPath) { Remove-Item $extPath -Recurse -Force; Write-Host "  Removed Extension config" -ForegroundColor Green }

    # Remove files
    if (Test-Path $installDir) {
        Remove-Item $installDir -Recurse -Force
        Write-Host "  Removed $installDir" -ForegroundColor Green
    }

    Write-Host "`nUninstall complete!" -ForegroundColor Green
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 0
}

# === INSTALL ===
Write-Host "=== Installing Elite Soft Erwin Add-In ===" -ForegroundColor Cyan
$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path

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

# Step 1: Copy files
Write-Host "`n[1/3] Copying files to $installDir..." -ForegroundColor Yellow
if (Test-Path $installDir) {
    Remove-Item "$installDir\*" -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

Copy-Item "$sourceDir\*" -Destination $installDir -Recurse -Force -Exclude "install.ps1","metarepo.json"
$count = (Get-ChildItem $installDir -Recurse -File).Count
Write-Host "  Copied $count files" -ForegroundColor Green

# Step 2: Register COM component
Write-Host "`n[2/3] Registering COM component..." -ForegroundColor Yellow
$comHost = Join-Path $installDir $comHostDll
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    regsvr32.exe /s $comHost
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  COM registered (admin)" -ForegroundColor Green
    } else {
        Write-Host "  COM registration failed (code: $LASTEXITCODE)" -ForegroundColor Red
        Write-Host "  Ensure .NET 10 Desktop Runtime is installed" -ForegroundColor Yellow
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }
} else {
    # Non-admin: try per-user COM registration via reg.exe
    Write-Host "  No admin privileges - attempting per-user COM registration..." -ForegroundColor Yellow
    try {
        $clsid = "A1B2C3D4-E5F6-7890-ABCD-EF1234567890"
        $regBase = "HKCU:\Software\Classes"

        # ProgID -> CLSID mapping
        $progIdPath = "$regBase\$progId\CLSID"
        if (-not (Test-Path $progIdPath)) { New-Item -Path $progIdPath -Force | Out-Null }
        Set-ItemProperty "$regBase\$progId\CLSID" -Name "(Default)" -Value "{$clsid}"

        # CLSID -> InprocServer32 mapping
        $clsidPath = "$regBase\CLSID\{$clsid}"
        if (-not (Test-Path "$clsidPath\InprocServer32")) { New-Item -Path "$clsidPath\InprocServer32" -Force | Out-Null }
        Set-ItemProperty "$clsidPath\InprocServer32" -Name "(Default)" -Value $comHost
        Set-ItemProperty "$clsidPath\InprocServer32" -Name "ThreadingModel" -Value "Both"

        # ProgID reference
        if (-not (Test-Path "$clsidPath\ProgID")) { New-Item -Path "$clsidPath\ProgID" -Force | Out-Null }
        Set-ItemProperty "$clsidPath\ProgID" -Name "(Default)" -Value $progId

        Write-Host "  COM registered (per-user HKCU)" -ForegroundColor Green
    } catch {
        Write-Host "  Per-user COM registration failed: $_" -ForegroundColor Red
        Write-Host "  Try running as Administrator for full COM registration" -ForegroundColor Yellow
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }
}

# Step 3: Register in erwin Add-In Manager (HKCU - per user)
Write-Host "`n[3/3] Registering in erwin Add-In Manager (HKCU)..." -ForegroundColor Yellow
$base = "HKCU:\$erwinRegBase"
if (Test-Path $base) {
    Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
        $addInPath = "$($_.PSPath)\Add-Ins\Elite Soft Erwin Addin"
        if (-not (Test-Path $addInPath)) {
            New-Item -Path $addInPath -Force | Out-Null
        }
        Set-ItemProperty $addInPath -Name "Menu Identifier" -Value 1 -Type DWord
        Set-ItemProperty $addInPath -Name "ProgID" -Value $progId -Type String
        Set-ItemProperty $addInPath -Name "Invoke Method" -Value "Execute" -Type String
        Set-ItemProperty $addInPath -Name "Invoke EXE" -Value 0 -Type DWord
        Write-Host "  erwin $($_.PSChildName) (HKCU) - OK" -ForegroundColor Green
    }
} else {
    Write-Host "  erwin not found in registry, skipping" -ForegroundColor Yellow
}

# Auto-read MetaRepo config from embedded metarepo.json (if no params given)
if (-not $MetaRepo) {
    $configFile = Join-Path $sourceDir "metarepo.json"
    if (Test-Path $configFile) {
        $cfg = Get-Content $configFile -Raw | ConvertFrom-Json
        $MetaRepo = $cfg.MetaRepo
        $DbUser = $cfg.DbUser
        $DbPass = $cfg.DbPass
        Write-Host "`nFound embedded MetaRepo config: $MetaRepo" -ForegroundColor Cyan
    }
}

# Configure MetaRepo connection
if ($MetaRepo) {
    Write-Host "`n[4] Configuring MetaRepo connection..." -ForegroundColor Yellow

    # Clear existing registry keys
    $bootstrapPath = "HKCU:\Software\EliteSoft\MetaRepo\Bootstrap"
    $extPath = "HKCU:\Software\EliteSoft\MetaRepo\Extension"
    if (Test-Path $bootstrapPath) { Remove-Item $bootstrapPath -Recurse -Force; Write-Host "  Cleared old Bootstrap config" -ForegroundColor Gray }
    if (Test-Path $extPath) { Remove-Item $extPath -Recurse -Force; Write-Host "  Cleared old Extension config" -ForegroundColor Gray }

    if (-not $DbUser -or -not $DbPass) {
        Write-Host "  ERROR: -DbUser and -DbPass required with -MetaRepo" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }

    # DPAPI encrypt credentials (CurrentUser scope)
    Add-Type -AssemblyName System.Security
    function Protect-String([string]$text) {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
        $encrypted = [System.Security.Cryptography.ProtectedData]::Protect($bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        return [Convert]::ToBase64String($encrypted)
    }

    $encUser = Protect-String $DbUser
    $encPass = Protect-String $DbPass

    # Write Bootstrap config (HKCU)
    if (-not (Test-Path $bootstrapPath)) {
        New-Item -Path $bootstrapPath -Force | Out-Null
    }
    Set-ItemProperty $bootstrapPath -Name "DbType" -Value "MSSQL" -Type String
    Set-ItemProperty $bootstrapPath -Name "Host" -Value "localhost" -Type String
    Set-ItemProperty $bootstrapPath -Name "Port" -Value "1433" -Type String
    Set-ItemProperty $bootstrapPath -Name "Database" -Value $MetaRepo -Type String
    Set-ItemProperty $bootstrapPath -Name "Username" -Value $encUser -Type String
    Set-ItemProperty $bootstrapPath -Name "Password" -Value $encPass -Type String
    Set-ItemProperty $bootstrapPath -Name "IsConfigured" -Value 1 -Type DWord
    Write-Host "  Bootstrap: localhost/$MetaRepo (MSSQL) - OK" -ForegroundColor Green

    # Update CONNECTION_DEF credentials in DB (re-encrypt with this user's DPAPI)
    try {
        $connStr = "Server=localhost,1433;Database=$MetaRepo;User Id=$DbUser;Password=$DbPass;TrustServerCertificate=True;"
        $sqlConn = New-Object System.Data.SqlClient.SqlConnection($connStr)
        $sqlConn.Open()

        $updateSql = "UPDATE CONNECTION_DEF SET USERNAME = @user, PASSWORD = @pass WHERE CONNECTION_GROUP = 'OPERATIONAL' OR ID = 4"
        $cmd = $sqlConn.CreateCommand()
        $cmd.CommandText = $updateSql
        $cmd.Parameters.AddWithValue("@user", $encUser) | Out-Null
        $cmd.Parameters.AddWithValue("@pass", $encPass) | Out-Null
        $rows = $cmd.ExecuteNonQuery()
        $sqlConn.Close()

        if ($rows -gt 0) {
            Write-Host "  Glossary credentials updated ($rows connection(s)) - OK" -ForegroundColor Green
        } else {
            Write-Host "  No glossary connections found to update" -ForegroundColor Gray
        }
    } catch {
        Write-Host "  Glossary credentials update skipped: $_" -ForegroundColor Yellow
    }
}

Write-Host "`nInstallation complete!" -ForegroundColor Green
Write-Host "Restart erwin to use the add-in." -ForegroundColor Cyan
Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
