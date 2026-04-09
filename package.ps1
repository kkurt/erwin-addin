# Elite Soft Erwin Add-In - Package for Distribution
#
# Usage:
#   .\package.ps1                                                Publish to folder (no compression)
#   .\package.ps1 -Zip                                           ZIP package
#   .\package.ps1 -Exe                                           EXE installer (Inno Setup)
#   .\package.ps1 -Zip -MetaRepo "MetaRepoTTKOM" -DbUser "sa" -DbPass "123"
#   .\package.ps1 -?                                             Show help

param(
    [switch]$Zip,
    [switch]$Exe,
    [string]$License,
    [string]$MetaRepo,
    [string]$DbUser,
    [string]$DbPass,
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
    Write-Host "  .\package.ps1 -Exe                         Create EXE installer (Inno Setup 6)"
    Write-Host "  .\package.ps1 -Zip -License HWID           Embed hardware license"
    Write-Host "  .\package.ps1 -Zip -MetaRepo DB ...        Embed MetaRepo config"
    Write-Host ""
    Write-Host "MetaRepo Parameters:" -ForegroundColor Yellow
    Write-Host "  -MetaRepo        Database name (e.g. MetaRepoTTKOM)"
    Write-Host "  -DbUser          SQL Server username"
    Write-Host "  -DbPass          SQL Server password"
    Write-Host ""
    Write-Host "Example:" -ForegroundColor Yellow
    Write-Host '  .\package.ps1 -Zip -MetaRepo "MetaRepoTTKOM" -DbUser "sa" -DbPass "123"'
    Write-Host ""
    exit 0
}

# --- Auto-elevate to Administrator ---
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    $elevateArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
    if ($Zip) { $elevateArgs += " -Zip" }
    if ($Exe) { $elevateArgs += " -Exe" }
    if ($License) { $elevateArgs += " -License `"$License`"" }
    if ($MetaRepo) { $elevateArgs += " -MetaRepo `"$MetaRepo`" -DbUser `"$DbUser`" -DbPass `"$DbPass`"" }
    Start-Process powershell.exe -ArgumentList $elevateArgs -Verb RunAs
    exit
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

$publishDir = "C:\EliteSoft\ErwinAddIn-Publish"
$distDir = Join-Path $scriptDir "dist"

if ($Exe) { $format = "EXE" } elseif ($Zip) { $format = "ZIP" } else { $format = "FOLDER" }

Write-Host "=== Elite Soft Erwin Add-In - Package ($format) ===" -ForegroundColor Cyan

# STEP 1: Publish
Write-Host "`n[1] Publishing release build..." -ForegroundColor Yellow
dotnet clean ErwinAddIn.csproj -c Release 2>&1 | Out-Null
dotnet publish ErwinAddIn.csproj -c Release -r win-x64 --self-contained false -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
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

# STEP 3: Embed MetaRepo config (optional)
if ($MetaRepo) {
    Write-Host "`n[3] Embedding MetaRepo config..." -ForegroundColor Yellow

    if (-not $DbUser -or -not $DbPass) {
        Write-Host "  ERROR: -DbUser and -DbPass required with -MetaRepo" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }

    $config = @{
        MetaRepo = $MetaRepo
        DbUser   = $DbUser
        DbPass   = $DbPass
    }
    $config | ConvertTo-Json | Out-File (Join-Path $publishDir "metarepo.json") -Encoding UTF8

    Write-Host "  Config: localhost/$MetaRepo (MSSQL)" -ForegroundColor Green
}

# STEP 4: Copy install script + watcher
Copy-Item (Join-Path $scriptDir "installer\install.ps1") (Join-Path $publishDir "install.ps1") -Force
Copy-Item (Join-Path $scriptDir "scripts\autostart-watcher.ps1") (Join-Path $publishDir "autostart-watcher.ps1") -Force

# STEP 5: Create package (or just leave folder)
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

if ($Exe) {
    Write-Host "`n[4] Creating EXE installer (Inno Setup)..." -ForegroundColor Yellow

    $isccPaths = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
    $iscc = $null
    foreach ($p in $isccPaths) {
        if (Test-Path $p) { $iscc = $p; break }
    }

    if (-not $iscc) {
        Write-Host "  Inno Setup 6 not found!" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }

    $issFile = Join-Path $scriptDir "installer\erwin-addin-setup.iss"
    if (-not (Test-Path $issFile)) {
        Write-Host "  ISS script not found: $issFile" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }

    & $iscc $issFile
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Installer creation failed!" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }

    $installer = Get-ChildItem $distDir -Filter "ErwinAddIn-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($installer) {
        $sizeMB = [math]::Round($installer.Length / 1MB, 1)
        Write-Host "`nPackage ready!" -ForegroundColor Green
        Write-Host "  $($installer.FullName) ($sizeMB MB)" -ForegroundColor Cyan
    }

} elseif ($Zip) {
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
Write-Host "  1. Copy folder (or extract ZIP/run EXE)" -ForegroundColor White
Write-Host "  2. PowerShell as Admin: .\install.ps1" -ForegroundColor White
if ($MetaRepo) {
    Write-Host "  MetaRepo config will be applied automatically from embedded metarepo.json" -ForegroundColor Gray
}

Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
