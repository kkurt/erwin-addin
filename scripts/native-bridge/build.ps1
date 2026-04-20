# Build ErwinNativeBridge.dll (x64) using VS BuildTools cl.exe.
#
# Uses VS 2022 BuildTools 14.44.35207 (confirmed installed). Runs vcvars64
# in a subshell, then compiles and links in one cl.exe invocation. The
# output DLL is placed next to this script.

param(
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src       = Join-Path $scriptDir "native-bridge.cpp"
$outDir    = $scriptDir
$outDll    = Join-Path $outDir "ErwinNativeBridge.dll"
$outLib    = Join-Path $outDir "ErwinNativeBridge.lib"
$outExp    = Join-Path $outDir "ErwinNativeBridge.exp"
$outObj    = Join-Path $outDir "native-bridge.obj"

if ($Clean) {
    Get-ChildItem $outDir -Include "ErwinNativeBridge.*","native-bridge.obj" -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Write-Host "Cleaned build artifacts."
    return
}

# Find vcvars64.bat
$vsBase  = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools"
$vcvars  = Join-Path $vsBase "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) {
    throw "vcvars64.bat not found at: $vcvars"
}
if (-not (Test-Path $src)) {
    throw "source file not found: $src"
}

Write-Host "Building ErwinNativeBridge.dll (x64)..." -ForegroundColor Cyan

# Compose a cmd.exe invocation that sets up the env then runs cl.exe.
# /LD        - build a DLL
# /EHsc      - standard C++ exceptions
# /std:c++17
# /O2        - optimize (spike runs fast, debug-info not critical)
# /MD        - dynamic CRT (match erwin)
# /Fo/Fe     - output paths
$clArgs = @(
    "/nologo", "/LD", "/EHsc", "/std:c++17", "/O2", "/MD", "/W3",
    "`"$src`"",
    "/Fo`"$outObj`"",
    "/Fe`"$outDll`"",
    "/link",
    "/IMPLIB:`"$outLib`"",
    "/OUT:`"$outDll`"",
    "user32.lib"
) -join " "

$cmd = "call `"$vcvars`" >nul && cl.exe $clArgs"
Write-Host "cmd: cl.exe $clArgs" -ForegroundColor DarkGray

$p = Start-Process -FilePath "cmd.exe" -ArgumentList "/c", $cmd -NoNewWindow -Wait -PassThru
if ($p.ExitCode -ne 0) {
    throw "cl.exe failed with exit code $($p.ExitCode)"
}

if (-not (Test-Path $outDll)) {
    throw "Build reported success but $outDll is missing."
}

$dllInfo = Get-Item $outDll
Write-Host "OK: $($dllInfo.FullName)  ($([int]$dllInfo.Length) bytes)" -ForegroundColor Green
