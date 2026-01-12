# Erwin Forge - Build Script
$ErrorActionPreference = "Stop"

Write-Host "=== Erwin Forge Build ===" -ForegroundColor Cyan

# Get script directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# Kill running instance if exists
$processName = "ErwinForge"
$runningProcess = Get-Process -Name $processName -ErrorAction SilentlyContinue
if ($runningProcess) {
    Write-Host "`nKilling running instance..." -ForegroundColor Yellow
    Stop-Process -Name $processName -Force
    Start-Sleep -Milliseconds 500
}

# Build
Write-Host "`nBuilding project..." -ForegroundColor Yellow
dotnet clean
dotnet build -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "`nBuild failed!" -ForegroundColor Red
    exit 1
}

Write-Host "`nBuild successful!" -ForegroundColor Green

$exePath = Join-Path $scriptDir "bin\Release\net48\ErwinForge.exe"

if (Test-Path $exePath) {
    Write-Host "`nExecutable created:" -ForegroundColor Cyan
    Write-Host "  EXE: $exePath" -ForegroundColor White

    Write-Host "`nStarting Erwin Forge..." -ForegroundColor Yellow
    Write-Host ""
} else {
    Write-Host "`nWARNING: Executable not found at expected location!" -ForegroundColor Red
    Write-Host "  Expected: $exePath" -ForegroundColor Yellow
}

&$exePath