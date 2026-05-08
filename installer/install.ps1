# Elite Soft Erwin Add-In - Install Script
# Usage:
#   .\install.ps1 -Scope Machine                                     Install machine-wide (Program Files, auto-elevates via UAC)
#   .\install.ps1 -Scope User                                        Install per-user (LOCALAPPDATA, no UAC needed)
#   .\install.ps1 -Scope User -ReCreateBootstrapRegistry             Wipe Bootstrap key before write (clean slate)
#   .\install.ps1 -Uninstall                                         Uninstall (scope auto-detected from install dir)
#   .\install.ps1 -?                                                 Show this help
#
# -Scope is REQUIRED for install paths (no default). Either pass it on the
# command line or include a Scope field in bootstrap.seed.json next to
# install.ps1 (package.ps1 writes that automatically when packagers pass
# -Scope at packaging time). Uninstall does not need -Scope - it reads
# the registry.scope marker from the install dir.
#
# Add-In Manager registry entries are ALWAYS written to HKCU\SOFTWARE\erwin\Data Modeler\10.10
# regardless of scope, because erwin DM r10 only discovers Add-Ins from HKCU. The
# scope flag affects WHERE the binaries live (Program Files vs LOCALAPPDATA),
# WHO the auto-start Scheduled Task fires for (any interactive user vs the
# installing user), WHICH hive holds the Bootstrap config (HKLM vs HKCU), and
# HOW COM is registered (regsvr32 + HKLM vs HKCU\Software\Classes - User scope
# is fully UAC-free).
#
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'DBPassword',
    Justification='Plaintext required for DPAPI ProtectedData.Protect; the value is encrypted before any persistence and the seed file is deleted on success.')]
param(
    [switch]$Uninstall,
    # No default: caller MUST specify -Scope explicitly OR provide a
    # bootstrap.seed.json with a Scope field next to install.ps1. The seed
    # path is honored by the preflight block below; if neither is supplied
    # for an install path, the script aborts with an actionable error.
    # Uninstall does not need -Scope (scope is detected from the
    # registry.scope marker file in the install dir).
    [ValidateSet("User", "Machine")]
    [string]$Scope,
    # MetaRepo bootstrap seed. Param names (-DBHost, -DBPort, -DBName, -DBUserName,
    # -DBPassword, -DBType) match the registry value names under
    # SOFTWARE\EliteSoft\MetaRepo\Bootstrap. PowerShell parameter binding is
    # case-insensitive so `$DBHost` accepts `-DBHost`, `-dbhost`, etc. - no
    # [Alias] attributes are needed (and adding them with the same name
    # collapses to "alias conflicts with parameter" at runtime). $DBHost is
    # also distinct from PowerShell's $Host automatic variable, so no shadow
    # concern. When any of these are passed OR a bootstrap.seed.json file
    # ships next to install.ps1, the values are written to
    # HK[LM|CU]\Software\EliteSoft\MetaRepo\Bootstrap with DBUserName/DBPassword
    # DPAPI-encrypted under the matching scope (Machine = LocalMachine, User =
    # CurrentUser).
    [string]$DBHost,
    [string]$DBPort,
    [string]$DBName,
    [string]$DBUserName,
    [string]$DBPassword,
    [string]$DBType,
    # Wipe HKLM\Software\EliteSoft\MetaRepo\Bootstrap AND HKCU\...\Bootstrap
    # before writing. Use this when migrating from the legacy value names
    # (Host/Database/Username/Password) to the new DB* names, or when you want
    # to discard a stale admin/manual config and start fresh. Without this
    # switch, an existing config in either hive short-circuits Step 4
    # (intentionally - prevents accidental re-prompts on re-install).
    [switch]$ReCreateBootstrapRegistry,
    [Alias("?")]
    [switch]$Help
)

$ErrorActionPreference = "Stop"

# Preflight: Let bootstrap.seed.json override $Scope when the user did not pass
# -Scope explicitly. Without this, a User-scope package would still install to
# Program Files because $Scope defaults to Machine - the package's intent
# (chosen at packaging time via package.ps1 -Scope) would be silently lost.
# $PSBoundParameters lists only explicitly bound params, so this preserves
# manual `-Scope` overrides on the command line.
if (-not $PSBoundParameters.ContainsKey('Scope')) {
    $preflightSeed = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "bootstrap.seed.json"
    # -LiteralPath everywhere we touch $sourceDir-derived paths because the
    # package may have been extracted to a folder containing PowerShell
    # wildcard chars (e.g. "MetaAddin [TTKOM-77]") - PS otherwise treats
    # the brackets as a wildcard pattern and the cmdlet rejects the path.
    if (Test-Path -LiteralPath $preflightSeed) {
        try {
            $seedPreview = Get-Content -LiteralPath $preflightSeed -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($null -ne $seedPreview.Scope -and ($seedPreview.Scope -eq 'User' -or $seedPreview.Scope -eq 'Machine')) {
                $Scope = $seedPreview.Scope
                Write-Host "Bootstrap seed sets scope to '$Scope' (override with -Scope on the command line)." -ForegroundColor Gray
            }
        } catch {
            # Fall through; Step 4 will surface a parse error if the file is real garbage.
        }
    }
}

# Auto-elevation: relaunch this script in an elevated PowerShell when the
# requested operation needs admin rights. Two cases trigger elevation:
#   1. Install with -Scope Machine: Program Files copy + INTERACTIVE-group
#      Scheduled Task + HKLM bootstrap write all require admin.
#   2. Uninstall of a previously-Machine-scope install: detected by the
#      registry.scope marker file under %ProgramFiles%\EliteSoft\ErwinAddIn
#      (Set-Content "HKLM" by Step 1 of a Machine install).
# User-scope flows do not auto-elevate because LocalAppData + HKCU writes
# don't need it. COM registration in User scope writes HKCU\Software\Classes
# directly (Register-ComUserScope) instead of using regsvr32, so the User
# path is genuinely UAC-free end to end.
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$needsElevation = $false
if (-not $Help) {
    if ($Uninstall) {
        $machineMarker = Join-Path (Join-Path $env:ProgramFiles "EliteSoft\ErwinAddIn") "registry.scope"
        if (Test-Path $machineMarker) { $needsElevation = $true }
    } elseif ($Scope -eq "Machine") {
        $needsElevation = $true
    }
}

if ($needsElevation -and -not $isAdmin) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    # Forward every explicitly-bound param so the elevated process sees the
    # same intent. Switches are forwarded only when present; strings only
    # when non-empty. $PSBoundParameters distinguishes "user passed" from
    # "default value" so we don't accidentally force defaults.
    $elevateArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
    if ($Uninstall)                                 { $elevateArgs += " -Uninstall" }
    if ($PSBoundParameters.ContainsKey('Scope'))    { $elevateArgs += " -Scope `"$Scope`"" }
    if ($DBHost)                                    { $elevateArgs += " -DBHost `"$DBHost`"" }
    if ($DBPort)                                    { $elevateArgs += " -DBPort `"$DBPort`"" }
    if ($DBName)                                    { $elevateArgs += " -DBName `"$DBName`"" }
    if ($DBUserName)                                { $elevateArgs += " -DBUserName `"$DBUserName`"" }
    if ($DBPassword)                                { $elevateArgs += " -DBPassword `"$DBPassword`"" }
    if ($DBType)                                    { $elevateArgs += " -DBType `"$DBType`"" }
    if ($ReCreateBootstrapRegistry)                 { $elevateArgs += " -ReCreateBootstrapRegistry" }
    try {
        Start-Process powershell.exe -ArgumentList $elevateArgs -Verb RunAs -ErrorAction Stop | Out-Null
    } catch {
        Write-Host ""
        Write-Host "  ERROR: Could not launch elevated PowerShell: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "         Right-click PowerShell, 'Run as administrator', then re-run this script." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Press any key to exit..." -ForegroundColor Gray
        $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }
    exit 0
}

if ($Help) {
    Write-Host ""
    Write-Host "Elite Soft Erwin Add-In - Installer" -ForegroundColor Cyan
    Write-Host "===================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  .\install.ps1 -Scope Machine                   " -NoNewline; Write-Host "Install machine-wide (Program Files; auto-elevates via UAC)" -ForegroundColor Gray
    Write-Host "  .\install.ps1 -Scope User                      " -NoNewline; Write-Host "Install for current user only (LOCALAPPDATA; no UAC)" -ForegroundColor Gray
    Write-Host "  .\install.ps1 -Scope User -ReCreateBootstrap*  " -NoNewline; Write-Host "...with HKCU/HKLM Bootstrap key wiped first" -ForegroundColor Gray
    Write-Host "  .\install.ps1 -Uninstall                       " -NoNewline; Write-Host "Uninstall (auto-detects scope from install dir)" -ForegroundColor Gray
    Write-Host "  .\install.ps1 -?                               " -NoNewline; Write-Host "Show this help" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Notes:" -ForegroundColor Yellow
    Write-Host "  - -Scope is REQUIRED for install (no default); either pass it or include a Scope" -ForegroundColor Gray
    Write-Host "    field in bootstrap.seed.json next to install.ps1 (package.ps1 writes it)." -ForegroundColor Gray
    Write-Host "  - Add-In registration is ALWAYS HKCU\SOFTWARE\erwin\Data Modeler\10.10 (erwin r10 reads HKCU only)" -ForegroundColor Gray
    Write-Host "  - -Scope Machine: binaries to Program Files, Scheduled Task fires for any interactive user" -ForegroundColor Gray
    Write-Host "  - -Scope User: binaries to LOCALAPPDATA, Scheduled Task fires for current user only" -ForegroundColor Gray
    Write-Host "  - Stale Add-In entries in HKLM and HKCU (other versions) are swept automatically" -ForegroundColor Gray
    Write-Host "  - Machine scope (and Machine-scope uninstall) auto-elevates via UAC; the script" -ForegroundColor Gray
    Write-Host "    re-launches itself in an elevated PowerShell window with all params forwarded." -ForegroundColor Gray
    Write-Host ""
    exit 0
}

# Mandatory $Scope check for install paths. -Scope is no longer defaulted, so
# install runs that aren't Uninstall must have a Scope value either from the
# command line or the seed file's preflight override above. We can't make
# $Scope a [Parameter(Mandatory)] because that triggers PowerShell's prompt
# BEFORE the preflight block reads the seed file - which would defeat the
# whole point of seed-driven scope.
if (-not $Uninstall -and [string]::IsNullOrEmpty($Scope)) {
    Write-Host ""
    Write-Host "  ERROR: -Scope is required (User or Machine)." -ForegroundColor Red
    Write-Host "         Pass -Scope on the command line, e.g.:" -ForegroundColor Yellow
    Write-Host "           .\install.ps1 -Scope User" -ForegroundColor Cyan
    Write-Host "           .\install.ps1 -Scope Machine" -ForegroundColor Cyan
    Write-Host "         Or include a 'Scope' field in bootstrap.seed.json next to this script" -ForegroundColor Yellow
    Write-Host "         (package.ps1 writes it automatically when -Scope is supplied at packaging time)." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Press any key to exit..." -ForegroundColor Gray
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}

# Install dir depends on scope:
#   User    -> %LOCALAPPDATA%\EliteSoft\ErwinAddIn  (current user only, no admin)
#   Machine -> %ProgramFiles%\EliteSoft\ErwinAddIn  (all users, Admin required)
$userInstallDir    = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn"
$machineInstallDir = Join-Path $env:ProgramFiles "EliteSoft\ErwinAddIn"
$installDir = if ($Scope -eq "Machine") { $machineInstallDir } else { $userInstallDir }
$comHostDll = "EliteSoft.Erwin.AddIn.comhost.dll"
$progId = "EliteSoft.Erwin.AddIn"
# CLSID must mirror the [Guid(...)] attribute on ErwinAddIn class in
# ErwinAddIn.cs:17. Used by the User-scope COM registration path that writes
# HKCU\Software\Classes\CLSID directly (avoids regsvr32's HKLM-default and
# the resulting UAC prompt).
$clsid = '{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}'
$erwinRegBase = "SOFTWARE\erwin\Data Modeler"

# Per-user COM registration without UAC. Replicates the layout that
# regsvr32 + .NET comhost.dll's DllRegisterServer would produce in
# HKLM\Software\Classes, but writes it to HKCU\Software\Classes instead.
# erwin's CLSIDFromProgID resolves through HKEY_CLASSES_ROOT (HKCU∪HKLM,
# HKCU wins on read) so the addin is discoverable for the current user
# without touching HKLM. Returns $true on success, $false on failure.
function Register-ComUserScope([string]$clsid, [string]$progId, [string]$comHostPath) {
    try {
        $clsidBase   = "HKCU:\Software\Classes\CLSID\$clsid"
        $inprocPath  = "$clsidBase\InProcServer32"
        $clsidProgId = "$clsidBase\ProgId"
        $progIdBase  = "HKCU:\Software\Classes\$progId"
        $progIdClsid = "$progIdBase\CLSID"

        New-Item -Path $inprocPath   -Force | Out-Null
        New-Item -Path $clsidProgId  -Force | Out-Null
        New-Item -Path $progIdClsid  -Force | Out-Null

        # CLSID label (humans see it in OLE viewers; not load-critical).
        Set-ItemProperty -Path $clsidBase  -Name "(Default)"      -Value $progId
        # InProcServer32 = absolute path to the .NET comhost.dll. comhost.dll
        # finds the runtime config via sibling .runtimeconfig.json.
        Set-ItemProperty -Path $inprocPath -Name "(Default)"      -Value $comHostPath
        Set-ItemProperty -Path $inprocPath -Name "ThreadingModel" -Value "Both"
        # ProgId backref under CLSID
        Set-ItemProperty -Path $clsidProgId -Name "(Default)"     -Value $progId
        # ProgId -> CLSID forward lookup (used by CLSIDFromProgID)
        Set-ItemProperty -Path $progIdBase  -Name "(Default)"     -Value $progId
        Set-ItemProperty -Path $progIdClsid -Name "(Default)"     -Value $clsid

        return $true
    } catch {
        Write-Host "  ERROR: HKCU COM registration failed: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Symmetric removal of the per-user CLSID/ProgID entries written by
# Register-ComUserScope. Failures are non-fatal (Remove-Item with
# -ErrorAction SilentlyContinue) so partial state from a stale install
# can't block the rest of uninstall.
function Unregister-ComUserScope([string]$clsid, [string]$progId) {
    $clsidBase  = "HKCU:\Software\Classes\CLSID\$clsid"
    $progIdBase = "HKCU:\Software\Classes\$progId"
    if (Test-Path $clsidBase) {
        Remove-Item -Path $clsidBase -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $progIdBase) {
        Remove-Item -Path $progIdBase -Recurse -Force -ErrorAction SilentlyContinue
    }
}
# Add-In is hardcoded to a single erwin DM target. Reading versions from
# HKLM was unreliable: stale 9.98 keys leaked from old installs and a fresh
# user (no first-erwin-run yet) had no HKCU subkeys, so install silently
# skipped HKCU writes. erwin DM r10 only reads Add-In entries from HKCU
# anyway (HKLM Add-Ins are invisible in Tools menu - empirically verified),
# so we standardize on HKCU + hardcoded version.
$erwinVersion = "10.10"

if ($Uninstall) {
    Write-Host "=== Uninstalling Elite Soft Erwin Add-In ===" -ForegroundColor Cyan

    # Detect actual install location: prefer dir with registry.scope file;
    # fall back to whichever dir exists. Machine installs write registry.scope=HKLM,
    # User installs omit the file.
    $detectedInstallDir = $null
    if (Test-Path (Join-Path $machineInstallDir "registry.scope")) {
        $detectedInstallDir = $machineInstallDir
    } elseif (Test-Path (Join-Path $userInstallDir "registry.scope")) {
        $detectedInstallDir = $userInstallDir
    } elseif (Test-Path $machineInstallDir) {
        $detectedInstallDir = $machineInstallDir
    } elseif (Test-Path $userInstallDir) {
        $detectedInstallDir = $userInstallDir
    }
    if ($detectedInstallDir) { $installDir = $detectedInstallDir }
    else { Write-Host "  No existing install found at $machineInstallDir or $userInstallDir" -ForegroundColor Yellow }

    # Detect scope from installed registry.scope file (present only for Machine installs)
    $scopeFile = Join-Path $installDir "registry.scope"
    $uninstallHive = "HKCU"
    if (Test-Path $scopeFile) {
        $content = (Get-Content $scopeFile -Raw).Trim()
        if ($content -eq "HKLM") { $uninstallHive = "HKLM" }
    }
    Write-Host "  Detected scope: $uninstallHive (installDir: $installDir)" -ForegroundColor Gray

    # Unregister COM. The auto-elevation block above already escalated us if
    # this is a Machine uninstall (registry.scope marker present in Program
    # Files), so regsvr32 /u runs elevated. For User uninstalls we strip the
    # HKCU entries directly without UAC. As a belt-and-suspenders cleanup we
    # also strip any HKCU CLSID/ProgID entries on Machine uninstalls in case
    # an earlier User install left them on this user's hive.
    $comHost = Join-Path $installDir $comHostDll
    if ($uninstallHive -eq "HKLM") {
        if (Test-Path $comHost) {
            Write-Host "Unregistering COM component (HKLM)..." -ForegroundColor Yellow
            regsvr32.exe /u /s $comHost 2>&1 | Out-Null
            Write-Host "  COM unregistered" -ForegroundColor Green
        }
        Unregister-ComUserScope -clsid $clsid -progId $progId
    } else {
        Write-Host "Unregistering COM component (HKCU)..." -ForegroundColor Yellow
        Unregister-ComUserScope -clsid $clsid -progId $progId
        Write-Host "  COM unregistered" -ForegroundColor Green
    }

    # Remove erwin Add-In registry from BOTH hives to avoid stale entries after scope changes.
    # (Primary scope is $uninstallHive but we clean both for safety.)
    $oppositeUninstallHive = if ($uninstallHive -eq "HKLM") { "HKCU" } else { "HKLM" }
    foreach ($hive in @($uninstallHive, $oppositeUninstallHive)) {
        $base = "${hive}:\$erwinRegBase"
        if (Test-Path $base) {
            Get-ChildItem $base -ErrorAction SilentlyContinue | ForEach-Object {
                $addInPath = "$($_.PSPath)\Add-Ins\Elite Soft Erwin Addin"
                if (Test-Path $addInPath) {
                    try {
                        Remove-Item $addInPath -Recurse -Force -ErrorAction Stop
                        Write-Host "  Removed erwin Add-In entry from $hive\$($_.PSChildName)" -ForegroundColor Green
                    } catch {
                        Write-Host "  WARNING: Could not remove $hive\$($_.PSChildName) Add-In entry: $($_.Exception.Message)" -ForegroundColor Yellow
                    }
                }
            }
        }
    }

    # Remove auto-start watcher task. Try both names: the per-user one
    # this user owns (post per-user-suffix install) and the legacy
    # shared name (pre-suffix or Machine scope). Both fail silently if
    # absent or owned by another user.
    $userTaskName = "EliteSoft Erwin AddIn AutoStart - $env:USERNAME"
    $sharedTaskName = "EliteSoft Erwin AddIn AutoStart"
    Unregister-ScheduledTask -TaskName $userTaskName -Confirm:$false -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $sharedTaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "  Removed Scheduled Task(s) for autostart" -ForegroundColor Green

    # Remove MetaRepo registry (both hives for clean uninstall)
    foreach ($hive in @("HKCU", "HKLM")) {
        $bootstrapPath = "${hive}:\Software\EliteSoft\MetaRepo\Bootstrap"
        $extPath = "${hive}:\Software\EliteSoft\MetaRepo\Extension"
        if (Test-Path $bootstrapPath) { Remove-Item $bootstrapPath -Recurse -Force; Write-Host "  Removed Bootstrap config ($hive)" -ForegroundColor Green }
        if (Test-Path $extPath) { Remove-Item $extPath -Recurse -Force; Write-Host "  Removed Extension config ($hive)" -ForegroundColor Green }
    }

    # Remove files from BOTH possible install dirs (safety for scope transitions / stale installs)
    foreach ($dir in @($machineInstallDir, $userInstallDir) | Select-Object -Unique) {
        if (Test-Path $dir) {
            try {
                Remove-Item $dir -Recurse -Force -ErrorAction Stop
                Write-Host "  Removed $dir" -ForegroundColor Green
            } catch {
                Write-Host "  WARNING: Could not remove ${dir}: $($_.Exception.Message)" -ForegroundColor Yellow
                if ($dir -eq $machineInstallDir) {
                    Write-Host "           Re-run uninstall as Admin to clean Program Files." -ForegroundColor Gray
                }
            }
        }
    }

    # Remove per-user watcher logs (current user only; other users' logs stay)
    $watcherLogDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn-Logs"
    if (Test-Path $watcherLogDir) {
        Remove-Item $watcherLogDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "`nUninstall complete!" -ForegroundColor Green
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 0
}

# === INSTALL ===
Write-Host "=== Installing Elite Soft Erwin Add-In (Scope: $Scope) ===" -ForegroundColor Cyan
$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Machine scope here means we already auto-elevated above (or the user ran the
# script from an elevated shell). $isAdmin is still useful below to decide
# whether regsvr32 needs its own UAC prompt in User-scope flows.

# Check .NET 10 Desktop Runtime
$dotnetOk = $false
try {
    $runtimes = & dotnet --list-runtimes 2>&1
    if ($runtimes -match "Microsoft\.WindowsDesktop\.App 10\.") {
        $dotnetOk = $true
    }
} catch { }

if (-not $dotnetOk) {
    Write-Host ""
    Write-Host "  ERROR: .NET 10 Desktop Runtime is not installed!" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Download from:" -ForegroundColor Yellow
    Write-Host "  https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Select: .NET Desktop Runtime 10.x (Windows x64)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  After installing, run this script again." -ForegroundColor Gray
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}

# Stop ALL watcher processes (prevents duplicates, unlocks COM host DLL)
try {
    # Try both per-user and legacy shared names; missing tasks fail silently.
    Stop-ScheduledTask -TaskName "EliteSoft Erwin AddIn AutoStart - $env:USERNAME" -ErrorAction SilentlyContinue
    Stop-ScheduledTask -TaskName "EliteSoft Erwin AddIn AutoStart" -ErrorAction SilentlyContinue
    # Kill ALL autostart-watcher PowerShell processes
    Get-WmiObject Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match "autostart-watcher" } |
        ForEach-Object {
            Write-Host "  Stopping watcher process PID=$($_.ProcessId)" -ForegroundColor Gray
            $_.Terminate() | Out-Null
        }
    Start-Sleep -Seconds 2
} catch { }

# Check if erwin is running for CURRENT USER (locks COM host DLL)
$currentSessionId = (Get-Process -Id $PID).SessionId
$erwinProcess = Get-Process -Name "erwin" -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $currentSessionId }
if ($erwinProcess) {
    Write-Host ""
    Write-Host "  WARNING: erwin is running! It must be closed before installation." -ForegroundColor Red
    Write-Host "  Close erwin and press any key to continue, or Ctrl+C to cancel." -ForegroundColor Yellow
    Write-Host ""
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')

    # Check again
    $erwinProcess = Get-Process -Name "erwin" -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $currentSessionId }
    if ($erwinProcess) {
        Write-Host "  erwin is still running. Aborting installation." -ForegroundColor Red
        Write-Host "`nPress any key to exit..." -ForegroundColor Gray
        $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }
}

# Step 1: Unregister old COM + copy files. Symmetric to Step 2: Machine
# installs use regsvr32 /u (we are elevated), User installs strip the HKCU
# entries directly (no UAC).
Write-Host "`n[1/3] Copying files to $installDir..." -ForegroundColor Yellow
if (Test-Path $installDir) {
    $oldComHost = Join-Path $installDir $comHostDll
    if (Test-Path $oldComHost) {
        Write-Host "  Unregistering old COM component..." -ForegroundColor Gray
        if ($Scope -eq "Machine") {
            regsvr32.exe /u /s $oldComHost 2>&1 | Out-Null
        } else {
            Unregister-ComUserScope -clsid $clsid -progId $progId
        }
    }
    Remove-Item "$installDir\*" -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

# Exclude install.ps1 (gets copied separately later) and bootstrap.seed.json
# (contains plaintext DB credentials and is consumed in-place from $sourceDir
# in Step 4; copying it to the install dir would leak plaintext into a
# permanent location).
# Copy from $sourceDir to $installDir using Push-Location so the wildcard
# expansion happens with the cwd set to $sourceDir. This avoids the
# "$sourceDir\*" string getting passed through Copy-Item's path resolver,
# which would treat any [ ] in $sourceDir as wildcard chars and fail when
# the package was extracted into a folder like "MetaAddin [TTKOM-77]".
Push-Location -LiteralPath $sourceDir
try {
    Copy-Item -Path "*" -Destination $installDir -Recurse -Force -Exclude "install.ps1","bootstrap.seed.json"
} finally {
    Pop-Location
}
$count = (Get-ChildItem -LiteralPath $installDir -Recurse -File).Count
Write-Host "  Copied $count files" -ForegroundColor Green

# Write registry.scope marker file. Content "HKLM" tags a Machine scope install
# (Program Files binaries, INTERACTIVE-group Scheduled Task); absence tags User
# scope. Uninstall reads this to pick the right install dir to clean. The file
# name is historical - it no longer drives any registry hive selection (Add-In
# Manager registration is always HKCU\SOFTWARE\erwin\Data Modeler\10.10).
$scopeFile = Join-Path $installDir "registry.scope"
if ($Scope -eq "Machine") {
    Set-Content $scopeFile "HKLM" -Encoding UTF8
    Write-Host "  Install scope: Machine (Program Files)" -ForegroundColor Green
} else {
    if (Test-Path $scopeFile) { Remove-Item $scopeFile -Force }
    Write-Host "  Install scope: User (LOCALAPPDATA)" -ForegroundColor Green
}

# Step 2: Register COM component.
#   Machine scope -> regsvr32 writes HKLM\Software\Classes (we are already
#                    elevated by the auto-elevation block above).
#   User scope    -> Register-ComUserScope writes HKCU\Software\Classes
#                    directly, no UAC prompt at all.
Write-Host "`n[2/3] Registering COM component..." -ForegroundColor Yellow
$comHost = Join-Path $installDir $comHostDll
$comOk = $false
if ($Scope -eq "Machine") {
    regsvr32.exe /s $comHost
    if ($LASTEXITCODE -eq 0) {
        $comOk = $true
        Write-Host "  COM registered (HKLM via regsvr32)" -ForegroundColor Green
    } else {
        Write-Host "  COM registration failed (regsvr32 code: $LASTEXITCODE)" -ForegroundColor Red
    }
} else {
    if (Register-ComUserScope -clsid $clsid -progId $progId -comHostPath $comHost) {
        $comOk = $true
        Write-Host "  COM registered (HKCU\Software\Classes; no UAC needed)" -ForegroundColor Green
    }
}
if (-not $comOk) {
    Write-Host "  Ensure .NET 10 Desktop Runtime is installed and the comhost DLL is intact." -ForegroundColor Yellow
    Write-Host "`nPress any key to exit..." -ForegroundColor Gray
    $null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}

# Step 3: Register in erwin Add-In Manager (always HKCU + hardcoded version)
#
# erwin DM r10 only reads Add-In entries from HKCU - HKLM Add-Ins are invisible
# in the Tools menu (empirically verified, see memory reference_erwin_addin_hkcu_required).
# Reading erwin's installed version from HKLM was unreliable too (stale 9.98
# subkeys leaked from old installs; brand-new users without a first-erwin-run
# had no HKCU subkeys), so we standardize: ALWAYS write HKCU\$erwinRegBase\$erwinVersion.
# Per-user HKCU population for OTHER interactive users is the watcher's job
# (autostart-watcher.ps1 runs at each user's logon and self-heals their HKCU).
Write-Host "`n[3/3] Registering in erwin Add-In Manager (HKCU\$erwinRegBase\$erwinVersion)..." -ForegroundColor Yellow

# Sweep stale Add-In entries from BOTH hives so old installs (HKLM Add-Ins,
# HKCU 9.98 leftovers, mismatched scope, etc.) cannot mask the canonical
# HKCU\10.10 entry we are about to write.
foreach ($staleHive in @("HKLM", "HKCU")) {
    $staleBase = "${staleHive}:\$erwinRegBase"
    if (-not (Test-Path $staleBase)) { continue }
    Get-ChildItem $staleBase -ErrorAction SilentlyContinue | ForEach-Object {
        $stalePath = "$($_.PSPath)\Add-Ins\Elite Soft Erwin Addin"
        if (-not (Test-Path $stalePath)) { return }
        try {
            Remove-Item $stalePath -Recurse -Force -ErrorAction Stop
            Write-Host "  Removed stale $staleHive\$($_.PSChildName) Add-In entry" -ForegroundColor Gray
        } catch {
            if ($staleHive -eq "HKLM") {
                Write-Host "  WARNING: Could not remove stale HKLM\$($_.PSChildName) Add-In entry (needs Admin); delete manually." -ForegroundColor Yellow
            } else {
                Write-Host "  WARNING: Could not remove stale HKCU\$($_.PSChildName) Add-In entry: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
    }
}

# Write the canonical HKCU entry. New-Item -Force creates every missing parent
# (Software\erwin\Data Modeler\10.10\Add-Ins\Elite Soft Erwin Addin) in one
# call, so this works on a fresh user who has never opened erwin before.
$addInPath = "HKCU:\$erwinRegBase\$erwinVersion\Add-Ins\Elite Soft Erwin Addin"
if (-not (Test-Path $addInPath)) {
    New-Item -Path $addInPath -Force | Out-Null
}
Set-ItemProperty $addInPath -Name "Menu Identifier" -Value 1 -Type DWord
Set-ItemProperty $addInPath -Name "ProgID"          -Value $progId -Type String
Set-ItemProperty $addInPath -Name "Invoke Method"   -Value "Execute" -Type String
Set-ItemProperty $addInPath -Name "Invoke EXE"      -Value 0 -Type DWord
Write-Host "  erwin $erwinVersion (HKCU) - OK" -ForegroundColor Green

# Step 4: MetaRepo bootstrap seed - write DB connection info to the registry
# hive matching $Scope. Three input paths feed this step (priority order):
#   1. CLI args passed to install.ps1 (-DBHost/-DBName/-DBUserName/-DBPassword/...)
#   2. bootstrap.seed.json shipped next to install.ps1 (written by package.ps1;
#      the file holds plaintext because DPAPI keys are machine/user-bound and
#      cannot survive transit from the build host).
#   3. Interactive Read-Host prompts when the active hive (HKLM for Machine
#      scope, HKCU for User scope - matching MetaShared's registry.scope-driven
#      single-hive read) has no DB* bootstrap values AND the user passed
#      nothing on the command line.
# When existing registry config is detected in the active hive and no CLI/seed
# override is supplied, this step is skipped so re-running the installer
# doesn't clobber an admin's prior config. Use -ReCreateBootstrapRegistry to
# bypass that protection: it wipes both hives' Bootstrap keys up front so the
# prompt will fire even when an existing config is present (useful for
# migrating away from legacy value names).
Write-Host "`n[4] Configuring MetaRepo bootstrap..." -ForegroundColor Yellow

$seedPath = Join-Path $sourceDir "bootstrap.seed.json"
$seedFromFile = $null
if (Test-Path -LiteralPath $seedPath) {
    try {
        $seedFromFile = Get-Content -LiteralPath $seedPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Write-Host "  Found bootstrap.seed.json (packaged seed)" -ForegroundColor Gray
    } catch {
        Write-Host "  WARNING: bootstrap.seed.json present but unreadable: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "           Delete it manually if it is corrupt; CLI args still work." -ForegroundColor Gray
    }
}

# Optional clean slate: when -ReCreateBootstrapRegistry is set, wipe the
# Bootstrap key from BOTH hives before going through the resolve/prompt logic.
# Both are wiped because the addin reads HKCU first and falls back to HKLM, so
# leaving a stale entry in either hive would still influence behavior. After
# this wipe the rest of Step 4 proceeds normally - CLI/seed values get written
# directly, or if neither is supplied the interactive prompt fires (since
# Test-BootstrapConfigured will now return false for both hives).
if ($ReCreateBootstrapRegistry) {
    Write-Host "  -ReCreateBootstrapRegistry: wiping existing Bootstrap keys..." -ForegroundColor Yellow
    foreach ($wipeHive in @("HKCU", "HKLM")) {
        $wipePath = "${wipeHive}:\Software\EliteSoft\MetaRepo\Bootstrap"
        if (Test-Path $wipePath) {
            try {
                Remove-Item $wipePath -Recurse -Force -ErrorAction Stop
                Write-Host "    Removed $wipeHive\Software\EliteSoft\MetaRepo\Bootstrap" -ForegroundColor Gray
            } catch {
                if ($wipeHive -eq "HKLM") {
                    Write-Host "    WARNING: Could not remove HKLM Bootstrap (needs Admin): $($_.Exception.Message)" -ForegroundColor Yellow
                } else {
                    Write-Host "    WARNING: Could not remove HKCU Bootstrap: $($_.Exception.Message)" -ForegroundColor Yellow
                }
            }
        } else {
            Write-Host "    $wipeHive Bootstrap not present (nothing to remove)" -ForegroundColor Gray
        }
    }
}

function Resolve-Field([string]$cliValue, [string]$seedKey, [string]$default) {
    if (-not [string]::IsNullOrEmpty($cliValue)) { return $cliValue }
    if ($null -ne $seedFromFile -and $null -ne $seedFromFile.$seedKey -and -not [string]::IsNullOrEmpty([string]$seedFromFile.$seedKey)) {
        return [string]$seedFromFile.$seedKey
    }
    return $default
}

# Returns $true when the given hive has a Bootstrap key with both DBHost and
# DBName populated (the same "configured" criterion the addin uses on read).
function Test-BootstrapConfigured([string]$hive) {
    $path = "${hive}:\Software\EliteSoft\MetaRepo\Bootstrap"
    if (-not (Test-Path $path)) { return $false }
    $hostVal = $null; $nameVal = $null
    try { $hostVal = (Get-ItemProperty -Path $path -Name "DBHost" -ErrorAction Stop).DBHost } catch { }
    try { $nameVal = (Get-ItemProperty -Path $path -Name "DBName" -ErrorAction Stop).DBName } catch { }
    return -not [string]::IsNullOrEmpty($hostVal) -and -not [string]::IsNullOrEmpty($nameVal)
}

# Read a password without echo. Read-Host -AsSecureString works on PS5 and PS7;
# the BSTR round-trip is needed because PS5 lacks ConvertFrom-SecureString
# -AsPlainText. Returns "" when the user just hits Enter (empty password).
function Read-PlainPassword([string]$prompt) {
    $sec = Read-Host -Prompt $prompt -AsSecureString
    if ($null -eq $sec -or $sec.Length -eq 0) { return "" }
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try {
        return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    } finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

# Read with default: shows "[default]" hint; empty Enter keeps the default.
function Read-WithDefault([string]$prompt, [string]$default) {
    $shown = if ([string]::IsNullOrEmpty($default)) { "" } else { " [$default]" }
    $val = Read-Host -Prompt "$prompt$shown"
    if ([string]::IsNullOrEmpty($val)) { return $default }
    return $val
}

$bsDBHost     = Resolve-Field $DBHost     "DBHost"     ""
$bsDBName     = Resolve-Field $DBName     "DBName"     ""
$bsDBUserName = Resolve-Field $DBUserName "DBUserName" ""
$bsDBPassword = Resolve-Field $DBPassword "DBPassword" ""
$bsDBType     = Resolve-Field $DBType     "DBType"     "MSSQL"
$bsDBPort     = Resolve-Field $DBPort     "DBPort"     "1433"

# A complete CLI/seed input means we have at minimum DBHost AND DBName.
# Anything less triggers either skip-because-already-configured or the
# interactive prompt path below.
$haveHostAndName = -not [string]::IsNullOrEmpty($bsDBHost) -and -not [string]::IsNullOrEmpty($bsDBName)

if (-not $haveHostAndName) {
    # The addin's MetaShared RegistryBootstrapService reads ONE hive at runtime
    # based on the registry.scope file - HKLM for Machine installs, HKCU for
    # User installs. So "already configured" only matters for the hive the
    # current $Scope will write to (and the addin will subsequently read from).
    # If the OTHER hive happens to have data, it is invisible to the addin
    # under this scope and we should not let it block a re-install.
    $activeHive = if ($Scope -eq "Machine") { "HKLM" } else { "HKCU" }
    $activeHas  = Test-BootstrapConfigured $activeHive

    if ($activeHas) {
        Write-Host "  Bootstrap already configured in $activeHive\Software\EliteSoft\MetaRepo\Bootstrap." -ForegroundColor Gray
        Write-Host "  Skipping seed (re-run install.ps1 with -DBHost/-DBName/... or -ReCreateBootstrapRegistry to overwrite)." -ForegroundColor Gray
        $bsDBHost = ""  # sentinel: write block below sees empty Host/Name and skips
        $bsDBName = ""
    } else {
        Write-Host "  No bootstrap config in $activeHive and none provided via CLI/seed." -ForegroundColor Cyan
        Write-Host "  Please enter DB connection info now (will be written to ${activeHive}):" -ForegroundColor Cyan
        Write-Host ""
        $bsDBType     = Read-WithDefault "  DB Type (MSSQL/ORACLE/POSTGRESQL)" $bsDBType
        $bsDBHost     = Read-WithDefault "  DB Host" $bsDBHost
        $bsDBPort     = Read-WithDefault "  DB Port" $bsDBPort
        $bsDBName     = Read-WithDefault "  DB Name (catalog)" $bsDBName
        $bsDBUserName = Read-WithDefault "  DB UserName" $bsDBUserName
        $bsDBPassword = Read-PlainPassword "  DB Password"
        Write-Host ""

        if ([string]::IsNullOrEmpty($bsDBHost) -or [string]::IsNullOrEmpty($bsDBName)) {
            Write-Host "  Skipped bootstrap (DBHost or DBName left empty)." -ForegroundColor Yellow
            Write-Host "  The add-in will surface 'No configuration found in $activeHive' on first load." -ForegroundColor Gray
            Write-Host "  Re-run install.ps1 with the missing values to seed the registry." -ForegroundColor Gray
            $bsDBHost = ""
            $bsDBName = ""
        }
    }
}

if (-not [string]::IsNullOrEmpty($bsDBHost) -and -not [string]::IsNullOrEmpty($bsDBName)) {
    $bootstrapHive = if ($Scope -eq "Machine") { "HKLM" } else { "HKCU" }
    $bootstrapBase = "${bootstrapHive}:\Software\EliteSoft\MetaRepo\Bootstrap"
    $dpapiScope    = if ($Scope -eq "Machine") {
        [System.Security.Cryptography.DataProtectionScope]::LocalMachine
    } else {
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser
    }

    # DPAPI Protect: encode plaintext to UTF-8 bytes, ProtectedData.Protect with
    # the chosen scope, base64-encode for registry storage. The addin's
    # PasswordEncryptionService.Decrypt reverses the same pipeline using the
    # same scope (matched to the source hive) on read.
    function Protect-WithDpapi([string]$plaintext, [System.Security.Cryptography.DataProtectionScope]$scope) {
        if ([string]::IsNullOrEmpty($plaintext)) { return "" }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($plaintext)
        $protected = [System.Security.Cryptography.ProtectedData]::Protect($bytes, $null, $scope)
        return [System.Convert]::ToBase64String($protected)
    }

    if (-not (Test-Path $bootstrapBase)) {
        New-Item -Path $bootstrapBase -Force | Out-Null
    }

    Set-ItemProperty $bootstrapBase -Name "DBType"     -Value $bsDBType     -Type String
    Set-ItemProperty $bootstrapBase -Name "DBHost"     -Value $bsDBHost     -Type String
    Set-ItemProperty $bootstrapBase -Name "DBPort"     -Value $bsDBPort     -Type String
    Set-ItemProperty $bootstrapBase -Name "DBName"     -Value $bsDBName     -Type String
    Set-ItemProperty $bootstrapBase -Name "DBUserName" -Value (Protect-WithDpapi $bsDBUserName $dpapiScope) -Type String
    Set-ItemProperty $bootstrapBase -Name "DBPassword" -Value (Protect-WithDpapi $bsDBPassword $dpapiScope) -Type String
    Write-Host "  Bootstrap written to $bootstrapHive\Software\EliteSoft\MetaRepo\Bootstrap" -ForegroundColor Green
    Write-Host "    DBType=$bsDBType DBHost=$bsDBHost DBPort=$bsDBPort DBName=$bsDBName" -ForegroundColor Gray
    Write-Host "    DBUserName/DBPassword DPAPI-encrypted ($dpapiScope)" -ForegroundColor Gray

    # Best-effort delete of the plaintext seed file. Failure is non-fatal
    # (a locked file just stays on disk; admin can remove it manually).
    if (Test-Path -LiteralPath $seedPath) {
        try {
            Remove-Item -LiteralPath $seedPath -Force -ErrorAction Stop
            Write-Host "  Removed plaintext bootstrap.seed.json after successful registry write" -ForegroundColor Gray
        } catch {
            Write-Host "  WARNING: Could not remove $seedPath after successful seed: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host "           Delete it manually - it contains plaintext DB credentials." -ForegroundColor Yellow
        }
    }
}

# Configure auto-start watcher (DLL injection based)
Write-Host "`n[5] Configuring auto-start watcher..." -ForegroundColor Yellow
# Per-user task name in User scope. A previous install run as Admin
# created a task owned by Administrator; a subsequent normal-PS install
# run as Emre then could not unregister or overwrite it (cross-user
# access denied), so Register kept failing with "already exists".
# Suffixing with $env:USERNAME makes each user's autostart task
# independent. Machine scope keeps the single shared name (it's owned
# by SYSTEM and runs for any interactive user via S-1-5-4).
$taskName = if ($Scope -eq "Machine") {
    "EliteSoft Erwin AddIn AutoStart"
} else {
    "EliteSoft Erwin AddIn AutoStart - $env:USERNAME"
}
$legacyTaskName = "EliteSoft Erwin AddIn AutoStart"
$watcherSource = Join-Path $sourceDir "autostart-watcher.ps1"
$watcherTarget = Join-Path $installDir "autostart-watcher.ps1"

# Verify injection components exist in install dir
$injectorPath = Join-Path $installDir "ErwinInjector.exe"
$triggerDllPath = Join-Path $installDir "TriggerDll.dll"
if (-not (Test-Path $injectorPath)) {
    Write-Host "  WARNING: ErwinInjector.exe not found in package" -ForegroundColor Yellow
}
if (-not (Test-Path $triggerDllPath)) {
    Write-Host "  WARNING: TriggerDll.dll not found in package" -ForegroundColor Yellow
}

if (Test-Path -LiteralPath $watcherSource) {
    [System.IO.File]::Copy($watcherSource, $watcherTarget, $true)

    # Remove old task if exists (current scope/name).
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

    # Best-effort cleanup of the legacy single-name task from older installs
    # (pre per-user-suffix). When User scope, also try to remove the shared
    # name in case a prior Admin install left it behind. Cross-user removal
    # may need elevation; if we can't, no harm - the new per-user task name
    # avoids the conflict regardless.
    if ($Scope -ne "Machine" -and $legacyTaskName -ne $taskName) {
        try {
            Unregister-ScheduledTask -TaskName $legacyTaskName -Confirm:$false -ErrorAction Stop
            Write-Host "  Removed legacy shared task '$legacyTaskName' (pre per-user-suffix install)" -ForegroundColor Gray
        }
        catch {
            # Legacy task absent or owned by another user - both fine.
        }
    }

    # Create Scheduled Task - runs at logon, hidden.
    # If a task with the same name already exists from a prior install (or
    # from a different scope - Machine vs User), Register-ScheduledTask
    # fails with "Cannot create a file when that file already exists" and
    # leaves the stale task pointing at the old watcher script path. The
    # previous Out-Null pipe swallowed the error and the script then
    # printed a misleading "created" message. We now unregister first
    # (best-effort) and check the register result so the user sees the
    # truth.
    try {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
        Write-Host "  Removed pre-existing Scheduled Task '$taskName' to avoid stale path" -ForegroundColor Gray
    }
    catch {
        # No pre-existing task (or different folder/scope we can't access).
        # Either way, proceed to register; the message below will surface
        # any real failure.
    }

    $action = New-ScheduledTaskAction -Execute "powershell.exe" `
        -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$watcherTarget`""

    $registered = $null
    try {
        if ($Scope -eq "Machine") {
            # Machine scope: fire for ANY interactive user at their logon, run in that user's session.
            # Group SID S-1-5-4 = NT AUTHORITY\INTERACTIVE.
            # MultipleInstances=Parallel is CRITICAL: each user's logon spawns its own watcher
            # instance in that user's session. With the default IgnoreNew, a stale instance
            # from a previous logon (whose watcher died but TS hasn't noticed) blocks new
            # users' logon-triggered instances.
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
                -MultipleInstances Parallel
            $trigger = New-ScheduledTaskTrigger -AtLogOn
            $principal = New-ScheduledTaskPrincipal -GroupId "S-1-5-4" -RunLevel Limited
            $registered = Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
                -Settings $settings -Description "Auto-starts Elite Soft Erwin Add-In for any logged-on user (Machine scope)" -ErrorAction Stop
            Write-Host "  Scheduled Task '$taskName' created (Machine: all interactive users, Parallel instances)" -ForegroundColor Green
        } else {
            # User scope: fire only for the installing user. Single instance is fine.
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero)
            $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
            $registered = Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
                -Settings $settings -Description "Auto-starts Elite Soft Erwin Add-In when erwin opens (User scope)" -ErrorAction Stop
            Write-Host "  Scheduled Task '$taskName' created (User: $env:USERNAME)" -ForegroundColor Green
        }
        Write-Host "  Add-in will auto-load when erwin starts" -ForegroundColor Gray
    }
    catch {
        Write-Host "  ERROR: Could not register Scheduled Task '$taskName':" -ForegroundColor Red
        Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  Without this task the add-in WILL NOT auto-load when erwin starts." -ForegroundColor Yellow
        Write-Host "  Try one of these:" -ForegroundColor Yellow
        Write-Host "    1. Re-run install.ps1 in an elevated (Admin) PowerShell" -ForegroundColor Yellow
        Write-Host "    2. Open Task Scheduler, delete any task named '$taskName'" -ForegroundColor Yellow
        Write-Host "       under '\\$taskName' or root, then re-run install.ps1" -ForegroundColor Yellow
        $registered = $null
    }

    # Start watcher immediately (don't wait for next logon). For Machine scope
    # this starts the watcher in the installing Admin's session; other users'
    # watchers start automatically at their next logon. Skip if registration
    # failed - Start-ScheduledTask on a missing task throws.
    if ($registered) {
        Start-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "  autostart-watcher.ps1 not found in package, skipping" -ForegroundColor Yellow
}

Write-Host "`nInstallation complete!" -ForegroundColor Green
Write-Host "Add-in will auto-load when erwin starts." -ForegroundColor Cyan
Write-Host "`nPress any key to exit..." -ForegroundColor Gray
$null = (Get-Host).UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
