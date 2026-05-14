# Elite Soft Erwin Add-In - Package for Distribution
#
# Usage:
#   .\package.ps1                                                Publish to default folder (no compression)
#   .\package.ps1 -Zip                                           Create ZIP at <scriptDir>\dist
#   .\package.ps1 -Zip -License HWID                             Embed hardware license
#   .\package.ps1 -PackageName ErwinAddIn-TTKOM                  Folder output to C:\EliteSoft\ErwinAddIn-TTKOM
#   .\package.ps1 -PackageName ErwinAddIn-TTKOM -Zip             ZIP at C:\EliteSoft\ErwinAddIn-TTKOM\ErwinAddIn-TTKOM.zip
#   .\package.ps1 -DBHost srv -DBName MetaRepo \                 Bake DB bootstrap into the package
#                 -DBUserName sa -DBPassword Elite12345          (install-impl.ps1 writes HKCU and DPAPI-encrypts
#                                                                 at install time on the target machine)
#   .\package.ps1 -?                                             Show help
#
# Every produced package installs per-user via install-impl.ps1 (no -Scope flag).
# install-impl.ps1 picks HKCU as the target hive automatically and reads HKLM
# first at runtime, so the same package works on both personal machines and
# corporate-seeded ones. See docs/INSTALL.md for the full reference.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'DBPassword',
    Justification='Plaintext seed required because DPAPI keys are bound to the install host, not the build host. install-impl.ps1 encrypts before writing to the registry and deletes the seed file.')]
param(
    [switch]$Zip,
    [string]$License,
    # MetaRepo bootstrap seed. Param names (-DBHost, -DBPort, -DBName, -DBUserName,
    # -DBPassword, -DBType) match the registry value names under
    # SOFTWARE\EliteSoft\MetaRepo\Bootstrap. PowerShell parameter binding is
    # case-insensitive so `$DBHost` accepts `-DBHost`, `-dbhost`, etc. - no
    # [Alias] attributes are needed (and adding them with the same name
    # collapses to "alias conflicts with parameter" at runtime). $DBHost is
    # also distinct from PowerShell's $Host automatic variable, so no
    # shadowing concern.
    [string]$DBHost,
    [string]$DBPort,
    [string]$DBName,
    [string]$DBUserName,
    [string]$DBPassword,
    [string]$DBType,
    # When set, the package output goes to C:\EliteSoft\<PackageName> instead
    # of the default C:\EliteSoft\ErwinAddIn. In folder mode the binaries land
    # directly in that folder; in -Zip mode the staging happens in
    # C:\EliteSoft\<PackageName>.staging and only <PackageName>.zip survives
    # in C:\EliteSoft\<PackageName> at the end. Useful for shipping
    # customer-specific bundles (e.g. -PackageName "ErwinAddIn-TTKOM-2026.05.08").
    [string]$PackageName,
    [Alias('?')]
    [switch]$Help
)

if ($Help) {
    Write-Host ""
    Write-Host "Elite Soft Erwin Add-In - Packaging Script" -ForegroundColor Cyan
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  .\package.ps1                                    Publish to default folder (no compression)"
    Write-Host "  .\package.ps1 -Zip                               Create ZIP at <scriptDir>\dist"
    Write-Host "  .\package.ps1 -Zip -License HWID                 Embed hardware license"
    Write-Host "  .\package.ps1 -PackageName MyDevPkg              Folder output to C:\EliteSoft\MyDevPkg"
    Write-Host "  .\package.ps1 -PackageName MyDevPkg -Zip         ZIP at C:\EliteSoft\MyDevPkg\MyDevPkg.zip"
    Write-Host "  .\package.ps1 -DBHost srv -DBName MR \           Bake bootstrap config (DB connection) into"
    Write-Host "                -DBUserName sa -DBPassword Pwd       the package; install-impl.ps1 writes HKCU and"
    Write-Host "                                                     DPAPI-encrypts on the target machine."
    Write-Host ""
    Write-Host "Bootstrap params (all map 1:1 to registry values under" -ForegroundColor Yellow
    Write-Host "SOFTWARE\EliteSoft\MetaRepo\Bootstrap):" -ForegroundColor Yellow
    Write-Host "  -DBHost      DB server hostname"
    Write-Host "  -DBPort      DB server port (default 1433 if omitted)"
    Write-Host "  -DBName      Catalog name"
    Write-Host "  -DBUserName  DB user (DPAPI-encrypted at install time)"
    Write-Host "  -DBPassword  DB password (DPAPI-encrypted at install time)"
    Write-Host "  -DBType      MSSQL | ORACLE | POSTGRESQL (default MSSQL)"
    Write-Host ""
    Write-Host "Output location:" -ForegroundColor Yellow
    Write-Host "  Default               : C:\EliteSoft\ErwinAddIn (folder)  +  <scriptDir>\dist\ErwinAddIn-1.0.0.zip"
    Write-Host "  -PackageName ""X""      : C:\EliteSoft\X        (folder)  or  C:\EliteSoft\X\X.zip (-Zip mode)"
    Write-Host ""
    Write-Host "Notes:" -ForegroundColor Yellow
    Write-Host "  - Every produced package is per-user. install-impl.ps1 has no -Scope flag and never elevates."
    Write-Host "  - HKLM bootstrap (if corporate IT seeded it) wins at runtime, so the same package serves"
    Write-Host "    both personal and corporate-seeded machines without re-packaging."
    Write-Host ""
    exit 0
}

# Auto-elevate to Administrator (required for writing to C:\EliteSoft\).
# Packaging output lives under C:\EliteSoft regardless of who runs it, so
# the build step still needs admin; install-impl.ps1 itself does not.
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    $elevateArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
    if ($Zip)                                  { $elevateArgs += " -Zip" }
    if ($License)                              { $elevateArgs += " -License `"$License`"" }
    if ($DBHost)                               { $elevateArgs += " -DBHost `"$DBHost`"" }
    if ($DBPort)                               { $elevateArgs += " -DBPort `"$DBPort`"" }
    if ($DBName)                               { $elevateArgs += " -DBName `"$DBName`"" }
    if ($DBUserName)                           { $elevateArgs += " -DBUserName `"$DBUserName`"" }
    if ($DBPassword)                           { $elevateArgs += " -DBPassword `"$DBPassword`"" }
    if ($DBType)                               { $elevateArgs += " -DBType `"$DBType`"" }
    if ($PackageName)                          { $elevateArgs += " -PackageName `"$PackageName`"" }
    Start-Process powershell.exe -ArgumentList $elevateArgs -Verb RunAs
    exit
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# Output layout decision tree:
#   default     -> publish to C:\EliteSoft\ErwinAddIn,  zip to <scriptDir>\dist\ErwinAddIn-1.0.0.zip
#   -PackageName -Zip    -> publish to C:\EliteSoft\<PackageName>.staging,
#                           final ZIP at C:\EliteSoft\<PackageName>\<PackageName>.zip,
#                           staging dir is removed after zipping (only the zip survives).
#   -PackageName (folder) -> publish directly to C:\EliteSoft\<PackageName> (only files,
#                            no zip artifact).
$targetRoot = "C:\EliteSoft"
if ($PackageName) {
    $targetDir   = Join-Path $targetRoot $PackageName
    if ($Zip) {
        $publishDir = Join-Path $targetRoot "$PackageName.staging"
        $zipFile    = Join-Path $targetDir "$PackageName.zip"
        $distDir    = $targetDir   # for the "ensure exists" branch below
    } else {
        $publishDir = $targetDir
        $zipFile    = $null
        $distDir    = $targetDir
    }
} else {
    $targetDir   = Join-Path $targetRoot "ErwinAddIn"
    $publishDir  = $targetDir
    $distDir     = Join-Path $scriptDir "dist"
    $zipFile     = if ($Zip) { Join-Path $distDir "ErwinAddIn-1.0.0.zip" } else { $null }
}

if ($Zip) { $format = "ZIP" } else { $format = "FOLDER" }

Write-Host "=== Elite Soft Erwin Add-In - Package ($format) ===" -ForegroundColor Cyan

# STEP 1: Publish
Write-Host "`n[1] Publishing release build..." -ForegroundColor Yellow
dotnet clean ErwinAddIn.csproj -c Release 2>&1 | Out-Null
dotnet publish ErwinAddIn.csproj -c Release -r win-x64 --self-contained false -p:PackagedBuild=true -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
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

$fileCount = (Get-ChildItem -LiteralPath $publishDir -Recurse -File).Count
Write-Host "  Published $fileCount files" -ForegroundColor Green

# STEP 2: Embed license (optional)
if ($License) {
    Write-Host "`n[2] Generating license..." -ForegroundColor Yellow

    $keyGenProject = Join-Path $scriptDir "..\x-hw-licensing\KeyGen\KeyGen.csproj"
    $privateKeySource = Join-Path $scriptDir "..\x-hw-licensing\rsa_private_key.xml"

    if (-not (Test-Path $keyGenProject)) {
        Write-Host "  KeyGen not found: $keyGenProject" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }
    if (-not (Test-Path $privateKeySource)) {
        Write-Host "  Private key not found: $privateKeySource" -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
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
$null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
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
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}
$triggerDllSource = Join-Path $scriptDir "scripts\erwin-injector\TriggerDll\bin\Release\net10.0-windows\win-x64\publish\TriggerDll.dll"
# .NET File.Copy used instead of Copy-Item because $publishDir may contain
# square brackets (e.g. "MetaAddin [TTKOM-77]") which Copy-Item's path
# resolver treats as wildcard patterns and rejects with "wildcard character
# pattern is not valid". File.Copy works on raw filesystem strings.
[System.IO.File]::Copy($triggerDllSource, (Join-Path $publishDir "TriggerDll.dll"), $true)
Write-Host "  TriggerDll.dll published" -ForegroundColor Green

# Publish ErwinInjector (framework-dependent single file)
Write-Host "  Publishing ErwinInjector..." -ForegroundColor Gray
dotnet publish $injectorProject -c Release 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ErwinInjector publish failed!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}
$injectorSource = Join-Path $scriptDir "scripts\erwin-injector\bin\Release\net10.0\win-x64\publish\ErwinInjector.exe"
[System.IO.File]::Copy($injectorSource, (Join-Path $publishDir "ErwinInjector.exe"), $true)
Write-Host "  ErwinInjector.exe published" -ForegroundColor Green

# STEP 5: Copy install scripts + double-click wrappers + watcher.
# The .bat files exist so the end user can extract the ZIP and double-click
# without manually opening PowerShell. They forward to install-impl.ps1 with
# -NoProfile -ExecutionPolicy Bypass (per-process, GPO-safe) and pass through
# any extra args, so packagers can still call install-impl.ps1 directly with CLI
# overrides during testing.
[System.IO.File]::Copy((Join-Path $scriptDir "installer\install-impl.ps1"),    (Join-Path $publishDir "install-impl.ps1"),    $true)
[System.IO.File]::Copy((Join-Path $scriptDir "installer\install.bat"),    (Join-Path $publishDir "install.bat"),    $true)
[System.IO.File]::Copy((Join-Path $scriptDir "installer\uninstall.bat"),  (Join-Path $publishDir "uninstall.bat"),  $true)
[System.IO.File]::Copy((Join-Path $scriptDir "scripts\autostart-watcher.ps1"), (Join-Path $publishDir "autostart-watcher.ps1"), $true)

# STEP 6: Bake bootstrap seed (DB connection) into the package when any of the
# bootstrap params were supplied. Plaintext is unavoidable here: DPAPI keys are
# bound to the build host and cannot survive transit. install-impl.ps1 reads this
# file on the target machine, encrypts Username/Password with DPAPI CurrentUser,
# writes HKCU, and deletes the seed file on success so the plaintext is
# short-lived. install-impl.ps1 has no -Scope flag, so the seed carries only the
# six DB* fields.
$bootstrapSeedPath = Join-Path $publishDir "bootstrap.seed.json"
$hasBootstrap = $DBHost -or $DBName -or $DBUserName -or $DBPassword -or $DBType -or $DBPort
if ($hasBootstrap) {
    if (-not $DBHost -or -not $DBName) {
        Write-Host "`n  ERROR: Bootstrap seed requires both -DBHost and -DBName (got DBHost='$DBHost', DBName='$DBName')." -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }
    $seedObj = [ordered]@{
        DBType     = if ($DBType) { $DBType } else { "MSSQL" }
        DBHost     = $DBHost
        DBPort     = if ($DBPort) { $DBPort } else { "1433" }
        DBName     = $DBName
        DBUserName = if ($null -ne $DBUserName) { $DBUserName } else { "" }
        DBPassword = if ($null -ne $DBPassword) { $DBPassword } else { "" }
    }
    $seedJson = $seedObj | ConvertTo-Json -Depth 3
    Set-Content -LiteralPath $bootstrapSeedPath -Value $seedJson -Encoding UTF8
    Write-Host "  Bootstrap seed written: $bootstrapSeedPath" -ForegroundColor Green
    Write-Host "    DBType=$($seedObj.DBType) DBHost=$DBHost DBPort=$($seedObj.DBPort) DBName=$DBName" -ForegroundColor Gray
    Write-Host "    NOTE: contains plaintext credentials; delete after install or treat the package as sensitive." -ForegroundColor Yellow
} elseif (Test-Path -LiteralPath $bootstrapSeedPath) {
    Remove-Item -LiteralPath $bootstrapSeedPath -Force
    Write-Host "  Removed stale bootstrap.seed.json (no -DBHost/-DBName supplied this run)" -ForegroundColor Gray
}

# STEP 7: (intentionally empty) - uninstall.bat from STEP 5 already wraps
# "install-impl.ps1 -Uninstall", so we no longer ship a separate uninstall-impl.ps1.
# Keeping a single source of truth for the install/uninstall logic avoids
# the two-file drift the old inline-generated uninstall.ps1 was prone to.

# STEP 8: Create package (or just leave folder)
# Ensure the directory the ZIP will land in exists. For the default flow this
# is <scriptDir>\dist; for -PackageName it is C:\EliteSoft\<PackageName>.
if ($Zip -and -not (Test-Path -LiteralPath $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

if ($Zip) {
    Write-Host "`n[4] Creating ZIP package..." -ForegroundColor Yellow

    # Compress-Archive cannot handle paths with [ ] (square brackets) - it
    # interprets them as wildcard patterns and fails internally with
    # "Cannot bind argument to parameter 'Path' because it is null" when
    # Microsoft.PowerShell.Archive's Join-Path call fails. Workaround:
    #   1. Compress to a TEMP path that is bracket-free.
    #   2. Move the temp file to the real $zipFile via [IO.File]::Move
    #      which doesn't apply PowerShell wildcard semantics.
    # Source side: pass each top-level item under $publishDir via
    # -LiteralPath array so the source bracket problem is also avoided.
    if (Test-Path -LiteralPath $zipFile) {
        Remove-Item -LiteralPath $zipFile -Force
    }
    $tempZip  = Join-Path $env:TEMP ("pkg_" + [Guid]::NewGuid().ToString('N') + ".zip")
    $srcItems = @(Get-ChildItem -LiteralPath $publishDir | ForEach-Object { $_.FullName })
    if ($srcItems.Count -eq 0) {
        Write-Host "  ERROR: $publishDir is empty - nothing to compress." -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }
    Compress-Archive -LiteralPath $srcItems -DestinationPath $tempZip -CompressionLevel Optimal -Force

    # Ensure the destination directory exists and move the temp zip into place.
    $zipParent = Split-Path -Path $zipFile -Parent
    if (-not [string]::IsNullOrEmpty($zipParent) -and -not (Test-Path -LiteralPath $zipParent)) {
        New-Item -ItemType Directory -Path $zipParent -Force | Out-Null
    }
    [System.IO.File]::Move($tempZip, $zipFile)

    $sizeMB = [math]::Round(([System.IO.FileInfo]::new($zipFile)).Length / 1MB, 1)

    # In -PackageName -Zip mode, the staging dir is throwaway: we want only
    # the .zip to survive in C:\EliteSoft\<PackageName>. Skip cleanup in the
    # default flow because $publishDir = C:\EliteSoft\ErwinAddIn is the
    # historical "I want both files and zip somewhere" output.
    if ($PackageName -and (Test-Path -LiteralPath $publishDir) -and ($publishDir -ne $targetDir)) {
        try {
            Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction Stop
            Write-Host "  Cleaned staging $publishDir" -ForegroundColor Gray
        } catch {
            Write-Host "  WARNING: Could not remove staging $publishDir : $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    Write-Host "`nPackage ready!" -ForegroundColor Green
    Write-Host "  $zipFile ($sizeMB MB)" -ForegroundColor Cyan

} else {
    # No compression - folder output
    $fileCount = (Get-ChildItem -LiteralPath $publishDir -Recurse -File).Count
    Write-Host "`nPackage ready (folder)!" -ForegroundColor Green
    Write-Host "  $publishDir ($fileCount files)" -ForegroundColor Cyan
}

Write-Host "`nInstall on target:" -ForegroundColor Yellow
Write-Host "  1. Copy folder (or extract ZIP)" -ForegroundColor White
Write-Host "  2. Double-click install.bat   (no admin needed)" -ForegroundColor White
Write-Host "     (or run .\install-impl.ps1 from PowerShell for CLI overrides)" -ForegroundColor DarkGray
Write-Host "  3. If no DB seed was baked, install.bat will prompt for credentials" -ForegroundColor White
Write-Host "  Uninstall: double-click uninstall.bat" -ForegroundColor Gray

Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
