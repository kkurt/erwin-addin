# Elite Soft Erwin Add-In - Package for Distribution
#
# Usage:
#   .\package.ps1                                                Publish to default folder (no compression)
#   .\package.ps1 -Zip                                           Create ZIP at <scriptDir>\dist
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
    # Licensing is no longer embedded in the package (2026-07): the add-in reads its product license
    # from the repository DB (applied by the admin web's apply-license step). No -License/-Expires.
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
    # Output directory (tool-driven): the .zip lands directly in here as <PackageName>.zip
    # (implies -Zip). When omitted, the legacy C:\EliteSoft\<PackageName> layout is used.
    [string]$OutDir,
    # Dev-flavored package: OMITS the PackagedBuild=true define so the build
    # compiles the DEVELOPER surfaces INTO the package. Per ErwinAddIn.csproj,
    # when PackagedBuild is unset the compiler defines DEV + DEV_DIAGNOSTICS and
    # does NOT define PACKAGED - so a -Dev package shows the "Reload Config" and
    # "Change DB" buttons on the General tab and the Debug Log tab, and runs the
    # startup MetaRepo* DB picker. Ship ONLY to developers/testers (e.g. Emre),
    # never to a customer: the startup DB picker + dev surfaces are not
    # production behavior.
    [switch]$Dev,
    # DDL-generator flavor: compiles with the DDLGENERATOR symbol - the packaged
    # add-in is a dedicated, always-on DDL queue worker (no validation surfaces,
    # General tab only). Deploy to the dedicated worker VM ONLY (same COM CLSID
    # as the normal flavor - one flavor per machine).
    [switch]$DdlGenerator,
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
    Write-Host "  .\package.ps1 -PackageName MyDevPkg              Folder output to C:\EliteSoft\MyDevPkg"
    Write-Host "  .\package.ps1 -PackageName MyDevPkg -Zip         ZIP at C:\EliteSoft\MyDevPkg\MyDevPkg.zip"
    Write-Host "  .\package.ps1 -DBHost srv -DBName MR \           Bake bootstrap config (DB connection) into"
    Write-Host "                -DBUserName sa -DBPassword Pwd       the package; install-impl.ps1 writes HKCU and"
    Write-Host "                                                     DPAPI-encrypts on the target machine."
    Write-Host ""
    Write-Host "Build flavors:" -ForegroundColor Yellow
    Write-Host "  -Dev           Include developer surfaces in the package: 'Reload Config' + 'Change DB'"
    Write-Host "                 buttons, Debug Log tab, startup MetaRepo* DB picker. Devs/testers ONLY."
    Write-Host "  -DdlGenerator  Dedicated always-on DDL queue worker flavor (General tab only)."
    Write-Host ""
    Write-Host "Bootstrap params (all map 1:1 to registry values under" -ForegroundColor Yellow
    Write-Host "SOFTWARE\EliteSoft\MetaRepo\Bootstrap):" -ForegroundColor Yellow
    Write-Host "  -DBHost      DB server hostname"
    Write-Host "  -DBPort      DB server port (if omitted, install derives it from -DBType: MSSQL 1433, Oracle 1521, PostgreSQL 5432)"
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
    if ($DBHost)                               { $elevateArgs += " -DBHost `"$DBHost`"" }
    if ($DBPort)                               { $elevateArgs += " -DBPort `"$DBPort`"" }
    if ($DBName)                               { $elevateArgs += " -DBName `"$DBName`"" }
    if ($DBUserName)                           { $elevateArgs += " -DBUserName `"$DBUserName`"" }
    if ($DBPassword)                           { $elevateArgs += " -DBPassword `"$DBPassword`"" }
    if ($DBType)                               { $elevateArgs += " -DBType `"$DBType`"" }
    if ($PackageName)                          { $elevateArgs += " -PackageName `"$PackageName`"" }
    # 2026-07-14: these three were previously NOT forwarded across the elevation
    # relaunch, so a non-admin run silently lost -OutDir (wrong output path),
    # -Dev (dev surfaces dropped), and -DdlGenerator (wrong flavor).
    if ($OutDir)                               { $elevateArgs += " -OutDir `"$OutDir`"" }
    if ($Dev)                                  { $elevateArgs += " -Dev" }
    if ($DdlGenerator)                         { $elevateArgs += " -DdlGenerator" }
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
$effectiveName = if ($PackageName) { $PackageName } else { "ErwinAddIn" }
if ($OutDir) {
    # Tool-driven: zip lands directly in OutDir as <name>.zip (staged in a sibling that is
    # removed after zipping). -OutDir always implies a zip.
    $Zip        = $true
    $targetDir  = $OutDir
    $publishDir = Join-Path $OutDir "$effectiveName.staging"
    $zipFile    = Join-Path $OutDir "$effectiveName.zip"
    $distDir    = $OutDir
} elseif ($PackageName) {
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

# STEP 0: Native bridge (cl.exe) - MUST run before publish.
# package.ps1 only COPIES the native DLL (via the csproj <Copy> task that
# copies scripts\native-bridge\ErwinNativeBridge.dll into the publish output);
# it never compiled it. So a packaged build could SILENTLY ship a stale native
# DLL whenever native-bridge.cpp changed since the last build-and-run (exactly
# the trap that shipped Emre an old DLL on 2026-06-01). Rebuild it here,
# UNCONDITIONALLY (no Test-AnyNewer gate like build-and-run uses): packaging is
# an infrequent release op where shipping the correct binary beats the ~3-8s
# cl.exe cost. build.ps1 sets $ErrorActionPreference=Stop and throws on compile
# failure, so any error halts the package before publish (never ships a half
# build). A missing build script is a loud warning, not a hard stop, so a
# package can still be cut from a committed DLL on a box without VS BuildTools.
Write-Host "`n[0] Building native bridge (cl.exe)..." -ForegroundColor Yellow
$bridgeScript = Join-Path $scriptDir "scripts\native-bridge\build.ps1"
$bridgeDll    = Join-Path $scriptDir "scripts\native-bridge\ErwinNativeBridge.dll"
if (Test-Path $bridgeScript) {
    & $bridgeScript
    if (-not (Test-Path $bridgeDll)) {
        Write-Host "Native bridge build failed - DLL not produced! Aborting package (would ship a stale/missing native DLL)." -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $(if (-not [Console]::IsOutputRedirected) { (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') })
        exit 1
    }
    Write-Host "  Native bridge rebuilt." -ForegroundColor Green
} else {
    Write-Host "  WARNING: native bridge build script not found at $bridgeScript - packaging the EXISTING scripts\native-bridge\ErwinNativeBridge.dll as-is (it may be stale)." -ForegroundColor Yellow
}

# STEP 1: Publish
Write-Host "`n[1] Publishing release build$(if ($Dev) { ' [DEV flavor - developer surfaces INCLUDED]' })$(if ($DdlGenerator) { ' [DDLGENERATOR flavor]' })..." -ForegroundColor Yellow
dotnet clean ErwinAddIn.csproj -c Release 2>&1 | Out-Null
# -Dev omits PackagedBuild=true so ErwinAddIn.csproj defines DEV + DEV_DIAGNOSTICS
# and does NOT define PACKAGED: the package then compiles the developer surfaces
# (Reload Config + Change DB buttons, Debug Log tab, startup DB picker). A normal
# (no -Dev) package keeps PackagedBuild=true and hides all of that.
$publishProps = @()
if (-not $Dev) { $publishProps += '-p:PackagedBuild=true' }
if ($DdlGenerator) { $publishProps += '-p:DdlGenerator=true' }
dotnet publish ErwinAddIn.csproj -c Release -r win-x64 --self-contained false @publishProps -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed!" -ForegroundColor Red
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = $(if (-not [Console]::IsOutputRedirected) { (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') })
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

# Workaround for .NET 10 SDK CreateComHostTask bug: re-embed clsidmap
# into the published comhost.dll. See scripts/embed-comhost.ps1 +
# tools/comhost-embed for the why/how. Without this the packaged build
# ships a template comhost.dll with no CLSID map and the addin appears
# missing from Tools > Add-Ins on every install. Verified 2026-05-26.
$packagedComHost = Join-Path $publishDir 'EliteSoft.Erwin.AddIn.comhost.dll'
if (Test-Path $packagedComHost) {
    Write-Host "  Embedding clsidmap into packaged comhost.dll..." -ForegroundColor Gray
    pwsh -NoProfile -File (Join-Path $scriptDir 'scripts\embed-comhost.ps1') -Configuration Release -ComHostPath @($packagedComHost)
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  WARNING: comhost embed failed (exit=$LASTEXITCODE). Addin WILL NOT load from this package." -ForegroundColor Red
    } else {
        Write-Host "  comhost.dll has correct clsidmap" -ForegroundColor Green
    }
}

# STEP 2 (hardware license embedding - REMOVED 2026-07): the add-in now reads its product license
# from the repository DB (applied by the admin web's apply-license step); packages ship no license.lic.

# STEP 3 (legacy injection components - REMOVED 2026-05-26):
# ErwinInjector.exe + TriggerDll.dll were superseded by the PostMessage
# WM_COMMAND auto-load path (see scripts/autostart-watcher.ps1 +
# Services/WmCommandLogger.cs). The injector triggered SEP
# SONAR.ProcHijack on unsigned builds and got quarantined in prod. The
# new path uses standard Win32 messages (zero injection, zero AV
# heuristics). Source folder scripts/erwin-injector/ is kept in the repo
# for git history; no longer built or shipped.

# STEP 5: Copy install scripts + double-click wrappers + watcher.
# The .bat files exist so the end user can extract the ZIP and double-click
# without manually opening PowerShell. They forward to install-impl.ps1 with
# -NoProfile -ExecutionPolicy Bypass (per-process, GPO-safe) and pass through
# any extra args, so packagers can still call install-impl.ps1 directly with CLI
# overrides during testing.
[System.IO.File]::Copy((Join-Path $scriptDir "installer\install-impl.ps1"),    (Join-Path $publishDir "install-impl.ps1"),    $true)
[System.IO.File]::Copy((Join-Path $scriptDir "installer\install.bat"),    (Join-Path $publishDir "install.bat"),    $true)
[System.IO.File]::Copy((Join-Path $scriptDir "installer\uninstall.bat"),  (Join-Path $publishDir "uninstall.bat"),  $true)
# watcher-control.ps1 is dot-sourced by install-impl.ps1 via $PSScriptRoot;
# it MUST sit next to install-impl.ps1 in the package or every install fails
# with "watcher-control.ps1 not found".
[System.IO.File]::Copy((Join-Path $scriptDir "installer\watcher-control.ps1"), (Join-Path $publishDir "watcher-control.ps1"), $true)
[System.IO.File]::Copy((Join-Path $scriptDir "scripts\autostart-watcher.ps1"), (Join-Path $publishDir "autostart-watcher.ps1"), $true)

# DDL-generator flavor: ship the bootstrap model next to install-impl.ps1. Its
# PRESENCE is what tells install-impl.ps1 to configure DDL-gen watcher mode
# (install.bat cannot pass -DdlGenerator). A normal package never contains it,
# so install-impl.ps1 writes DdlGeneratorMode=0 there.
if ($DdlGenerator) {
    $bootstrapSrc = Join-Path $scriptDir "installer\assets\ddlgen-bootstrap.erwin"
    if (Test-Path $bootstrapSrc) {
        [System.IO.File]::Copy($bootstrapSrc, (Join-Path $publishDir "ddlgen-bootstrap.erwin"), $true)
        Write-Host "  Bundled DDL-gen bootstrap model (flavor marker)" -ForegroundColor Cyan
    } else {
        Write-Host "  WARNING: -DdlGenerator set but bootstrap model missing at $bootstrapSrc" -ForegroundColor Yellow
    }
}

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
    # A "connection seed" (any of host/name/user/password supplied) MUST carry
    # both DBHost AND DBName. install-impl.ps1 only writes HKCU when both are
    # non-empty (install-impl.ps1:838); with one missing it falls through to
    # interactive prompts and any baked DBPassword is silently dropped
    # (Read-PlainPassword takes no default). So a half-filled connection seed is
    # a footgun and is rejected here.
    #
    # DBType/DBPort ALONE are not a connection: they are only installer prompt
    # defaults (e.g. -DBType Oracle for a POC where the target DB coordinates are
    # typed at install time). They may ship with empty host/name: install
    # pre-fills the type (install-impl.ps1:820) and prompts for the rest, and
    # HkcuBootstrapReader treats an empty host/name seed as "not configured"
    # (HkcuBootstrapReader.cs:104-107), so nothing broken ever reaches runtime.
    $hasConnectionIntent = $DBHost -or $DBName -or $DBUserName -or $DBPassword
    if ($hasConnectionIntent -and (-not $DBHost -or -not $DBName)) {
        Write-Host "`n  ERROR: Bootstrap seed requires both -DBHost and -DBName when any of -DBHost/-DBName/-DBUserName/-DBPassword is supplied (got DBHost='$DBHost', DBName='$DBName')." -ForegroundColor Red
        Write-Host "  Tip: pass -DBType/-DBPort alone to bake only installer defaults with empty host/name." -ForegroundColor Gray
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = $(if (-not [Console]::IsOutputRedirected) { (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') })
        exit 1
    }
    $seedObj = [ordered]@{
        DBType     = if ($DBType) { $DBType } else { "MSSQL" }
        DBHost     = $DBHost
        # Port is baked ONLY when -DBPort was passed. An unset port stays empty
        # rather than defaulting to 1433 here: 1433 is MSSQL's port and would be
        # wrong to bake into an Oracle/PostgreSQL package. When the seed carries
        # no port, install-impl.ps1 derives the install-time default from the
        # DBType (Oracle 1521, PostgreSQL 5432, MSSQL 1433) via Get-DefaultPort.
        DBPort     = if ($DBPort) { $DBPort } else { "" }
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
    Write-Host "  Removed stale bootstrap.seed.json (no bootstrap params supplied this run)" -ForegroundColor Gray
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
        $null = $(if (-not [Console]::IsOutputRedirected) { (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') })
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
    if ((Test-Path -LiteralPath $publishDir) -and ($publishDir -ne $targetDir)) {
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
$null = $(if (-not [Console]::IsOutputRedirected) { (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') })
