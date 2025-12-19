# ErwinAddIn Build and Register Script
# Run as Administrator!

$ErrorActionPreference = "Stop"

Write-Host "=== ErwinAddIn Build & Register ===" -ForegroundColor Cyan

# Get script directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# Build
Write-Host "`n[1/3] Building project..." -ForegroundColor Yellow
dotnet build -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Build successful!" -ForegroundColor Green

$dllPath = Join-Path $scriptDir "bin\Release\net48\ErwinAddIn.dll"
$tlbPath = Join-Path $scriptDir "bin\Release\net48\ErwinAddIn.tlb"
$regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"

if (-not (Test-Path $dllPath)) {
    Write-Host "DLL not found: $dllPath" -ForegroundColor Red
    exit 1
}

# Check if running as admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "WARNING: Not running as Administrator. Registration may fail." -ForegroundColor Yellow
}

# Unregister first (ignore errors)
Write-Host "`n[2/3] Unregistering old version..." -ForegroundColor Yellow
& $regasm $dllPath /unregister 2>$null

# Register COM with Type Library
Write-Host "`n[3/3] Registering COM with Type Library..." -ForegroundColor Yellow
& $regasm $dllPath /codebase /tlb:$tlbPath

if ($LASTEXITCODE -eq 0) {
    Write-Host "`nRegistration successful!" -ForegroundColor Green

    # Show registry info
    Write-Host "`nRegistry check:" -ForegroundColor Cyan
    $progId = "ErwinAddIn.TableCreator"
    $regPath = "Registry::HKEY_CLASSES_ROOT\$progId"
    if (Test-Path $regPath) {
        Write-Host "  ProgID '$progId' registered OK" -ForegroundColor Green
    } else {
        Write-Host "  ProgID '$progId' NOT FOUND in registry!" -ForegroundColor Red
    }

    Write-Host "`nFiles created:" -ForegroundColor Cyan
    Write-Host "  DLL: $dllPath"
    Write-Host "  TLB: $tlbPath"

    Write-Host "`nProgID for erwin: ErwinAddIn.TableCreator" -ForegroundColor Yellow
    Write-Host ""
} else {
    Write-Host "Registration failed!" -ForegroundColor Red
    exit 1
}
