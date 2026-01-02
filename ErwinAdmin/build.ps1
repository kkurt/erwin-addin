# Elite Soft Erwin Admin - Build Script
$ErrorActionPreference = "Stop"

Write-Host "=== Elite Soft Erwin Admin Build ===" -ForegroundColor Cyan

# Get script directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# Build
Write-Host "`nBuilding project..." -ForegroundColor Yellow
dotnet clean
dotnet build -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "`nBuild failed!" -ForegroundColor Red
    exit 1
}

Write-Host "`nBuild successful!" -ForegroundColor Green

$exePath = Join-Path $scriptDir "bin\Release\net48\EliteSoft.Erwin.Admin.exe"

if (Test-Path $exePath) {
    Write-Host "`nExecutable created:" -ForegroundColor Cyan
    Write-Host "  EXE: $exePath" -ForegroundColor White

    Write-Host "`nYou can now run the admin tool:" -ForegroundColor Yellow
    Write-Host "  $exePath" -ForegroundColor White
    Write-Host ""
} else {
    Write-Host "`nWARNING: Executable not found at expected location!" -ForegroundColor Red
    Write-Host "  Expected: $exePath" -ForegroundColor Yellow
}
