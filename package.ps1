# Elite Soft Erwin Add-In - Package for Distribution
#
# Usage:
#   .\package.ps1                                                Publish to folder (no compression)
#   .\package.ps1 -Zip                                           ZIP package
#   .\package.ps1 -?                                             Show help

param(
    [switch]$Zip,
    [string]$License,
    [Alias('?')]
    [switch]$Help
)

if ($Help) {
    Write-Host ""
    Write-Host "Elite Soft Erwin Add-In - Packaging Script" -ForegroundColor Cyan
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  .\package.ps1                              Publish to folder (no compression)"
    Write-Host "  .\package.ps1 -Zip                         Create ZIP package"
    Write-Host "  .\package.ps1 -Zip -License HWID           Embed hardware license"
    Write-Host ""
    exit 0
}

# --- Auto-elevate to Administrator (required for writing to C:\EliteSoft\) ---
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    $elevateArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
    if ($Zip) { $elevateArgs += " -Zip" }
    if ($License) { $elevateArgs += " -License `"$License`"" }
    Start-Process powershell.exe -ArgumentList $elevateArgs -Verb RunAs
    exit
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

$publishDir = "C:\EliteSoft\ErwinAddIn"
$distDir = Join-Path $scriptDir "dist"

if ($Zip) { $format = "ZIP" } else { $format = "FOLDER" }

Write-Host "=== Elite Soft Erwin Add-In - Package ($format) ===" -ForegroundColor Cyan

# STEP 1: Publish
Write-Host "`n[1] Publishing release build..." -ForegroundColor Yellow
dotnet clean ErwinAddIn.csproj -c Release 2>&1 | Out-Null
dotnet publish ErwinAddIn.csproj -c Release -r win-x64 --self-contained false -p:PackagedBuild=true -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}

# Build and publish DdlHelper tool
$ddlHelperProject = Join-Path $scriptDir "tools\DdlHelper\DdlHelper.csproj"
if (Test-Path $ddlHelperProject) {
    Write-Host "  Publishing DdlHelper..." -ForegroundColor Gray
    $ddlHelperOutput = Join-Path $publishDir "tools\DdlHelper"
    dotnet publish $ddlHelperProject -c Release -r win-x64 --self-contained false -o $ddlHelperOutput 2>&1 | Out-Null
    if ($?) { Write-Host "  DdlHelper published!" -ForegroundColor Green }
    else { Write-Host "  DdlHelper publish failed (non-critical)" -ForegroundColor Yellow }
}

$fileCount = (Get-ChildItem -Path $publishDir -Recurse -File).Count
Write-Host "  Published $fileCount files" -ForegroundColor Green

# STEP 2: Embed license (optional)
if ($License) {
    Write-Host "`n[2] Generating license..." -ForegroundColor Yellow

    $keyGenProject = Join-Path $scriptDir "..\x-hw-licensing\KeyGen\KeyGen.csproj"
    $privateKeySource = Join-Path $scriptDir "..\x-hw-licensing\rsa_private_key.xml"

    if (-not (Test-Path $keyGenProject)) {
        Write-Host "  KeyGen not found: $keyGenProject" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }
    if (-not (Test-Path $privateKeySource)) {
        Write-Host "  Private key not found: $privateKeySource" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }

    $keyGenDir = Split-Path $keyGenProject -Parent
    $privateKeyTarget = Join-Path $keyGenDir "rsa_private_key.xml"
    $licenseOutput = Join-Path $publishDir "license.lic"

    Copy-Item $privateKeySource $privateKeyTarget -Force
    Push-Location $keyGenDir
    dotnet run --project $keyGenProject -c Debug -- genlicense --hwid $License --licensee "ErwinAddIn" --features "ErwinAddIn" -o $licenseOutput
    $exitCode = $LASTEXITCODE
    Pop-Location
    Remove-Item $privateKeyTarget -Force -ErrorAction SilentlyContinue

    if ($exitCode -ne 0) {
        Write-Host "  License generation failed!" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }
    Write-Host "  License embedded" -ForegroundColor Green
}

# STEP 3: Build and copy injection components
Write-Host "`n[4] Building injection components..." -ForegroundColor Yellow

$triggerDllProject = Join-Path $scriptDir "scripts\erwin-injector\TriggerDll\TriggerDll.csproj"
$injectorProject = Join-Path $scriptDir "scripts\erwin-injector\ErwinInjector.csproj"

# Add vswhere to PATH for NativeAOT linking
$vsInstallerPath = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (Test-Path $vsInstallerPath) {
    $env:PATH = "$env:PATH;$vsInstallerPath"
}

# Publish TriggerDll (NativeAOT)
Write-Host "  Publishing TriggerDll (NativeAOT)..." -ForegroundColor Gray
dotnet publish $triggerDllProject -c Release 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "  TriggerDll publish failed!" -ForegroundColor Red
    Write-Host "  Ensure Visual Studio C++ Build Tools are installed" -ForegroundColor Yellow
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}
$triggerDllSource = Join-Path $scriptDir "scripts\erwin-injector\TriggerDll\bin\Release\net10.0-windows\win-x64\publish\TriggerDll.dll"
Copy-Item $triggerDllSource (Join-Path $publishDir "TriggerDll.dll") -Force
Write-Host "  TriggerDll.dll published" -ForegroundColor Green

# Publish ErwinInjector (framework-dependent single file)
Write-Host "  Publishing ErwinInjector..." -ForegroundColor Gray
dotnet publish $injectorProject -c Release 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ErwinInjector publish failed!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}
$injectorSource = Join-Path $scriptDir "scripts\erwin-injector\bin\Release\net10.0\win-x64\publish\ErwinInjector.exe"
Copy-Item $injectorSource (Join-Path $publishDir "ErwinInjector.exe") -Force
Write-Host "  ErwinInjector.exe published" -ForegroundColor Green

# STEP 5: Copy install script + watcher
Copy-Item (Join-Path $scriptDir "installer\install.ps1") (Join-Path $publishDir "install.ps1") -Force
Copy-Item (Join-Path $scriptDir "scripts\autostart-watcher.ps1") (Join-Path $publishDir "autostart-watcher.ps1") -Force

# STEP 7: Create uninstall script in package
$uninstallScript = @'
# Elite Soft Erwin Add-In - Uninstaller
# Run: .\uninstall.ps1

$installScript = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "install.ps1"
if (Test-Path $installScript) {
    & $installScript -Uninstall
} else {
    $installDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn"
    $installed = Join-Path $installDir "install.ps1"
    if (Test-Path $installed) {
        & $installed -Uninstall
    } else {
        Write-Host "install.ps1 not found!" -ForegroundColor Red
    }
}
'@
Set-Content (Join-Path $publishDir "uninstall.ps1") $uninstallScript -Encoding UTF8

# STEP 8: Create package (or just leave folder)
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

if ($Zip) {
    Write-Host "`n[4] Creating ZIP package..." -ForegroundColor Yellow

    $zipFile = Join-Path $distDir "ErwinAddIn-1.0.0.zip"
    if (Test-Path $zipFile) { Remove-Item $zipFile -Force }

    Compress-Archive -Path "$publishDir\*" -DestinationPath $zipFile -CompressionLevel Optimal

    $sizeMB = [math]::Round((Get-Item $zipFile).Length / 1MB, 1)
    Write-Host "`nPackage ready!" -ForegroundColor Green
    Write-Host "  $zipFile ($sizeMB MB)" -ForegroundColor Cyan

} else {
    # No compression - folder output
    $fileCount = (Get-ChildItem -Path $publishDir -Recurse -File).Count
    Write-Host "`nPackage ready (folder)!" -ForegroundColor Green
    Write-Host "  $publishDir ($fileCount files)" -ForegroundColor Cyan
}

Write-Host "`nInstall on target:" -ForegroundColor Yellow
Write-Host "  1. Copy folder (or extract ZIP)" -ForegroundColor White
Write-Host "  2. PowerShell as Admin: .\install.ps1" -ForegroundColor White
Write-Host "  3. Run Admin tool to configure DB connection" -ForegroundColor White

Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
