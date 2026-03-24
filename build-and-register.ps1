# Elite Soft Erwin Add-In Build and Register Script
# Auto-elevates to Administrator if needed
# Installs to C:\Program Files\EliteSoft\ErwinAddIn (accessible by all users)
#
# Usage:
#   .\build-and-register.ps1              # Build + install + register (dev workflow)
#   .\build-and-register.ps1 -Package     # Self-contained publish + create installer

param(
    [switch]$Package,
    [Alias('?')]
    [switch]$Help
)

if ($Help) {
    Write-Host ""
    Write-Host "Elite Soft Erwin Add-In Build Script" -ForegroundColor Cyan
    Write-Host "====================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  .\build-and-register.ps1              Build + install + COM register (dev workflow)"
    Write-Host "  .\build-and-register.ps1 -Package     Self-contained publish + create installer"
    Write-Host "  .\build-and-register.ps1 -?           Show this help"
    Write-Host ""
    Write-Host "Parameters:" -ForegroundColor Yellow
    Write-Host "  -Package    Publish self-contained (.NET 10 runtime included) and"
    Write-Host "              create standalone installer via Inno Setup 6."
    Write-Host "              Output: dist\ErwinAddIn-Setup-1.0.0.exe"
    Write-Host ""
    Write-Host "Requirements:" -ForegroundColor Yellow
    Write-Host "  - .NET 10 SDK"
    Write-Host "  - Administrator privileges"
    Write-Host "  - Inno Setup 6 (only for -Package)"
    Write-Host ""
    exit 0
}

$ErrorActionPreference = "Stop"

# Auto-elevate to Administrator if not already
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    $elevateArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
    if ($Package) { $elevateArgs += " -Package" }
    Start-Process powershell.exe -ArgumentList $elevateArgs -Verb RunAs
    exit
}

Write-Host "=== Elite Soft Erwin Add-In Build & Register ===" -ForegroundColor Cyan
Write-Host "Running as Administrator" -ForegroundColor Green

# Get script directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

if ($Package) {
    # ============================================================
    # PACKAGE MODE: Self-contained publish + Inno Setup installer
    # ============================================================
    Write-Host "`nMode: PACKAGE (standalone installer)" -ForegroundColor Magenta

    $publishDir = "C:\EliteSoft\ErwinAddIn-Publish"

    # Step 1: Clean & Publish
    Write-Host "`n[1/3] Publishing self-contained build..." -ForegroundColor Yellow
    dotnet clean ErwinAddIn.csproj -c Release 2>&1 | Out-Null

    dotnet publish ErwinAddIn.csproj -c Release -r win-x64 --self-contained true -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Publish failed!" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }

    $fileCount = (Get-ChildItem -Path $publishDir -Recurse -File).Count
    Write-Host "  Published $fileCount files to $publishDir" -ForegroundColor Green

    # Step 2: Find Inno Setup compiler
    Write-Host "`n[2/3] Finding Inno Setup 6..." -ForegroundColor Yellow
    $isccPaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )

    $iscc = $null
    foreach ($p in $isccPaths) {
        if (Test-Path $p) {
            $iscc = $p
            break
        }
    }

    if (-not $iscc) {
        Write-Host "  Inno Setup 6 not found! Install from https://jrsoftware.org/isinfo.php" -ForegroundColor Red
        Write-Host "  Searched:" -ForegroundColor Gray
        foreach ($p in $isccPaths) { Write-Host "    $p" -ForegroundColor Gray }
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }

    Write-Host "  Found: $iscc" -ForegroundColor Green

    # Step 3: Create installer
    Write-Host "`n[3/3] Creating installer..." -ForegroundColor Yellow
    $issFile = Join-Path $scriptDir "installer\erwin-addin-setup.iss"

    if (-not (Test-Path $issFile)) {
        Write-Host "  Inno Setup script not found: $issFile" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }

    # Create dist directory
    $distDir = Join-Path $scriptDir "dist"
    if (-not (Test-Path $distDir)) {
        New-Item -ItemType Directory -Path $distDir -Force | Out-Null
    }

    & $iscc $issFile

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Installer creation failed!" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }

    $installerFile = Get-ChildItem $distDir -Filter "ErwinAddIn-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($installerFile) {
        $sizeMB = [math]::Round($installerFile.Length / 1MB, 1)
        Write-Host "`nInstaller created successfully!" -ForegroundColor Green
        Write-Host "  $($installerFile.FullName) ($sizeMB MB)" -ForegroundColor Cyan
    }

} else {
    # ============================================================
    # DEV MODE: Build + install + register (existing behavior)
    # ============================================================

    # Install directory (accessible by all users)
    $installDir = "C:\Program Files\EliteSoft\ErwinAddIn"

    # Check if erwin is running and close it
    $erwinProcess = Get-Process -Name "erwin" -ErrorAction SilentlyContinue
    if ($erwinProcess) {
        Write-Host "`nClosing erwin..." -ForegroundColor Yellow
        $erwinProcess | Stop-Process -Force
        Start-Sleep -Seconds 2
        Write-Host "erwin closed." -ForegroundColor Green
    }

    # Build
    Write-Host "`n[1/5] Building project..." -ForegroundColor Yellow
    dotnet clean erwin-addin.sln
    dotnet build erwin-addin.sln -c Release

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }

    Write-Host "Build successful!" -ForegroundColor Green

    $buildOutputDir = Join-Path $scriptDir "bin\Release\net10.0-windows"

    if (-not (Test-Path (Join-Path $buildOutputDir "EliteSoft.Erwin.AddIn.dll"))) {
        Write-Host "DLL not found in build output!" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }

    # Unregister old version (if exists)
    Write-Host "`n[2/5] Unregistering old version (if exists)..." -ForegroundColor Yellow
    $oldComHost = Join-Path $installDir "EliteSoft.Erwin.AddIn.comhost.dll"
    $oldDll = Join-Path $installDir "EliteSoft.Erwin.AddIn.dll"
    $regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"
    if (Test-Path $oldComHost) {
        # .NET 10 COM host - unregister with regsvr32
        regsvr32.exe /u /s $oldComHost 2>&1 | Out-Null
        Write-Host "  Old .NET 10 COM host unregistered" -ForegroundColor Green
    } elseif (Test-Path $oldDll) {
        # Legacy .NET Framework - unregister with regasm
        & $regasm $oldDll /unregister 2>&1 | Out-Null
        Write-Host "  Old .NET Framework version unregistered" -ForegroundColor Green
    } else {
        Write-Host "  No previous installation found (OK)" -ForegroundColor Gray
    }

    # Copy files to shared install directory
    Write-Host "`n[3/5] Installing to $installDir ..." -ForegroundColor Yellow
    if (-not (Test-Path $installDir)) {
        New-Item -ItemType Directory -Path $installDir -Force | Out-Null
        Write-Host "  Created install directory" -ForegroundColor Green
    } else {
        # Clean old files (stale .tlb, old .config etc.) to prevent conflicts
        Remove-Item "$installDir\*.tlb" -Force -ErrorAction SilentlyContinue
        Remove-Item "$installDir\*.config" -Force -ErrorAction SilentlyContinue
        Write-Host "  Cleaned stale files from install directory" -ForegroundColor Green
    }

    # Copy entire build output preserving directory structure (runtimes/ subfolder required for native deps like SNI.dll)
    Copy-Item -Path "$buildOutputDir\*" -Destination $installDir -Recurse -Force
    $copiedCount = (Get-ChildItem -Path $installDir -Recurse -File).Count
    Write-Host "  Copied $copiedCount files to install directory (with runtimes/)" -ForegroundColor Green

    # Set read permissions for all users
    $acl = Get-Acl $installDir
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule("Users", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.SetAccessRule($rule)
    Set-Acl $installDir $acl
    Write-Host "  Set read permissions for all users" -ForegroundColor Green

    # Register COM via .NET 10 COM host (regsvr32)
    Write-Host "`n[4/5] Registering COM host..." -ForegroundColor Yellow
    $installedComHost = Join-Path $installDir "EliteSoft.Erwin.AddIn.comhost.dll"
    if (-not (Test-Path $installedComHost)) {
        Write-Host "  comhost.dll not found! Ensure <EnableComHosting>true</EnableComHosting> is in csproj." -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }
    & regsvr32.exe /s $installedComHost 2>&1 | Out-Null
    $regsvr32Exit = $LASTEXITCODE
    Write-Host "  regsvr32 exit code: $regsvr32Exit" -ForegroundColor Gray

    if ($regsvr32Exit -eq 0) {
        Write-Host "`nRegistration successful!" -ForegroundColor Green

        # Show registry info
        Write-Host "`nRegistry check:" -ForegroundColor Cyan
        $progId = "EliteSoft.Erwin.AddIn"
        $regPath = "Registry::HKEY_CLASSES_ROOT\$progId"
        if (Test-Path $regPath) {
            Write-Host "  ProgID '$progId' registered OK" -ForegroundColor Green
        } else {
            Write-Host "  ProgID '$progId' NOT FOUND in registry!" -ForegroundColor Red
        }

        Write-Host "`nInstalled to:" -ForegroundColor Cyan
        Write-Host "  $installDir"

        # Register in erwin Add-In Manager (auto-load on startup)
        Write-Host "`n[5/5] Configuring erwin Add-In Manager..." -ForegroundColor Yellow
        $erwinRegBase = "HKCU:\SOFTWARE\erwin\Data Modeler"
        if (Test-Path $erwinRegBase) {
            $erwinVersion = Get-ChildItem $erwinRegBase -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty PSChildName |
                Sort-Object { [version]($_ + ".0") } -Descending |
                Select-Object -First 1

            if ($erwinVersion) {
                $addInsPath = "$erwinRegBase\$erwinVersion\Add-Ins\Elite Soft Erwin Addin"
                if (-not (Test-Path $addInsPath)) {
                    New-Item -Path $addInsPath -Force | Out-Null
                }
                Set-ItemProperty -Path $addInsPath -Name "Menu Identifier" -Value 1 -Type DWord
                Set-ItemProperty -Path $addInsPath -Name "ProgID"          -Value $progId -Type String
                Set-ItemProperty -Path $addInsPath -Name "Invoke Method"   -Value "Execute" -Type String
                Set-ItemProperty -Path $addInsPath -Name "Invoke EXE"      -Value 0 -Type DWord
                Write-Host "  erwin $erwinVersion Add-In Manager entry OK" -ForegroundColor Green
            } else {
                Write-Host "  erwin version not found, skipping Add-In Manager" -ForegroundColor Yellow
            }
        } else {
            Write-Host "  erwin not installed, skipping Add-In Manager" -ForegroundColor Yellow
        }
        Write-Host ""
    } else {
        Write-Host "Registration failed!" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }
}

Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
