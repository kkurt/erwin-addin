# Elite Soft Erwin Add-In Build and Register Script
# Auto-elevates to Administrator if needed

$ErrorActionPreference = "Stop"

# Auto-elevate to Administrator if not already
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`"" -Verb RunAs
    exit
}

Write-Host "=== Elite Soft Erwin Add-In Build & Register ===" -ForegroundColor Cyan
Write-Host "Running as Administrator" -ForegroundColor Green

# Get script directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# Check if erwin is running and using the DLL
$erwinProcess = Get-Process -Name "erwin" -ErrorAction SilentlyContinue
if ($erwinProcess) {
    Write-Host "`nWARNING: erwin is running and may lock the DLL." -ForegroundColor Yellow
    $response = Read-Host "Close erwin before building? (Y/N)"
    if ($response -eq "Y" -or $response -eq "y") {
        Write-Host "Closing erwin..." -ForegroundColor Yellow
        $erwinProcess | Stop-Process -Force
        Start-Sleep -Seconds 2
        Write-Host "erwin closed." -ForegroundColor Green
    }
}

# Build
Write-Host "`n[1/3] Building project..." -ForegroundColor Yellow
dotnet clean erwin-addin.sln
dotnet build erwin-addin.sln -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

Write-Host "Build successful!" -ForegroundColor Green

$dllPath = Join-Path $scriptDir "bin\Release\net48\EliteSoft.Erwin.AddIn.dll"
$tlbPath = Join-Path $scriptDir "bin\Release\net48\EliteSoft.Erwin.AddIn.tlb"
$regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"

if (-not (Test-Path $dllPath)) {
    Write-Host "DLL not found: $dllPath" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

# Unregister first (if exists)
Write-Host "`n[2/3] Unregistering old version (if exists)..." -ForegroundColor Yellow
& $regasm $dllPath /unregister 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "  Old version unregistered successfully" -ForegroundColor Green
} else {
    Write-Host "  No previous registration found (OK)" -ForegroundColor Gray
}

# Register COM with Type Library
Write-Host "`n[3/3] Registering COM with Type Library..." -ForegroundColor Yellow
& $regasm $dllPath /codebase /tlb:$tlbPath

if ($LASTEXITCODE -eq 0) {
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

    Write-Host "`nFiles created:" -ForegroundColor Cyan
    Write-Host "  DLL: $dllPath"
    Write-Host "  TLB: $tlbPath"

    Write-Host "`nProgID for erwin: EliteSoft.Erwin.AddIn" -ForegroundColor Yellow
    Write-Host ""
} else {
    Write-Host "Registration failed!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit 1
}

Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
