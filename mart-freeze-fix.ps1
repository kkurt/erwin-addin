# mart-freeze-fix.ps1
# erwin DM Mart Connect/Click/Disconnect/Close freeze'leri komple cozer.
#
# Iki ayri makinada calistirilir:
#   - CLIENT (Data Modeler kurulu): Microsoft cert auto-update flag disable
#   - SERVER (Mart Portal kurulu):  Apache httpd-ssl.conf'a LocationMatch + Require all denied
#
# Auto-detect: scriptin calistigi makinada hangi rol var ise onu uygular.
#   -Role parametresi ile manuel override.
#
# Kullanim:
#   .\mart-freeze-fix.ps1 -Apply
#   .\mart-freeze-fix.ps1 -Status
#   .\mart-freeze-fix.ps1 -Revert
#
# Yonetici PowerShell gerekli.

[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Revert,
    [switch]$Status,
    [ValidateSet('Auto', 'Client', 'Server', 'Both')]
    [string]$Role = 'Auto',
    [string]$ApacheService = 'erwinApacheServer'
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] 'Administrator')
if (-not $isAdmin) { Write-Host "X Yonetici PowerShell gerekli" -ForegroundColor Red; exit 1 }

# === Role detection ===
$dmDir = 'C:\Program Files\erwin\Data Modeler r10'
$mpDir = 'C:\Program Files\erwin\Mart Portal'
$hasClient = Test-Path $dmDir
$hasServer = Test-Path $mpDir

if ($Role -eq 'Auto') {
    if ($hasClient -and $hasServer) { $Role = 'Both' }
    elseif ($hasClient) { $Role = 'Client' }
    elseif ($hasServer) { $Role = 'Server' }
    else {
        Write-Host "X Bu makinada erwin DM veya Mart Portal kurulu degil" -ForegroundColor Red
        exit 1
    }
}

Write-Host "=== Mart Freeze Fix === Role: $Role ===" -ForegroundColor Cyan
Write-Host ""

# ====================================================================
# CLIENT-SIDE: Microsoft cert auto-update disable
# ====================================================================

# Hangi registry flag'leri
$certFlags = @(
    @{ Path='HKLM:\SOFTWARE\Policies\Microsoft\SystemCertificates\AuthRoot'; Name='DisableRootAutoUpdate'; Value=1; Desc='Root CTL update' },
    @{ Path='HKLM:\SOFTWARE\Microsoft\SystemCertificates\AuthRoot\AutoUpdate'; Name='DisableRootAutoUpdate'; Value=1; Desc='Root CTL update (default key)' },
    @{ Path='HKLM:\SOFTWARE\Policies\Microsoft\SystemCertificates\Disallowed'; Name='DisableDisallowedAutoUpdate'; Value=1; Desc='Disallowed CTL update' },
    @{ Path='HKLM:\SOFTWARE\Microsoft\SystemCertificates\AuthRoot\AutoUpdate'; Name='EnableDisallowedCertAutoUpdate'; Value=0; Desc='Disallowed cert auto-update' },
    @{ Path='HKLM:\SOFTWARE\Microsoft\SystemCertificates\AuthRoot\AutoUpdate'; Name='DisablePinRulesAutoUpdate'; Value=1; Desc='PinRules CTL update' },
    @{ Path='HKLM:\SOFTWARE\Policies\Microsoft\SystemCertificates\AuthRoot'; Name='DisablePinRulesAutoUpdate'; Value=1; Desc='PinRules update (Policies)' }
)

function Invoke-ClientStatus {
    Write-Host "[CLIENT] Cert auto-update flag durumu:" -ForegroundColor Cyan
    foreach ($s in $certFlags) {
        $val = $null
        if (Test-Path $s.Path) {
            $val = (Get-ItemProperty -Path $s.Path -Name $s.Name -ErrorAction SilentlyContinue).$($s.Name)
        }
        $state = ''
        $col = 'DarkGray'
        if ($null -eq $val) { $state = 'NOT SET'; $col = 'Yellow' }
        elseif ($val -eq $s.Value) { $state = "OK ($val)"; $col = 'Green' }
        else { $state = "val=$val (target=$($s.Value))"; $col = 'Yellow' }
        Write-Host ("  $($s.Desc): $state") -ForegroundColor $col
    }
}

function Invoke-ClientApply {
    Write-Host "[CLIENT] Cert auto-update mekanizmalari kapatiliyor..." -ForegroundColor Cyan
    foreach ($s in $certFlags) {
        if (-not (Test-Path $s.Path)) { New-Item -Path $s.Path -Force | Out-Null }
        New-ItemProperty -Path $s.Path -Name $s.Name -Value $s.Value -PropertyType DWord -Force | Out-Null
        Write-Host ("  Set: $($s.Name) = $($s.Value)  ($($s.Desc))") -ForegroundColor Green
    }
    Write-Host "  CryptSvc service restart..." -ForegroundColor Cyan
    try {
        $null = cmd /c "net stop CryptSvc /y" 2>&1
        Start-Sleep -Seconds 2
        $null = cmd /c "net start CryptSvc" 2>&1
        Start-Sleep -Seconds 2
        Write-Host "    OK" -ForegroundColor Green
    } catch {
        Write-Host ("    Warn: $($_.Exception.Message)") -ForegroundColor Yellow
    }
    $null = & ipconfig /flushdns 2>&1
    Write-Host "  DNS cache flushed" -ForegroundColor DarkGray
}

function Invoke-ClientRevert {
    Write-Host "[CLIENT] Cert auto-update flag'leri geri aliniyor..." -ForegroundColor Yellow
    foreach ($s in $certFlags) {
        if (Test-Path $s.Path) {
            $existing = Get-ItemProperty -Path $s.Path -Name $s.Name -ErrorAction SilentlyContinue
            if ($existing) {
                Remove-ItemProperty -Path $s.Path -Name $s.Name -Force -ErrorAction SilentlyContinue
                Write-Host ("  Removed: $($s.Name)") -ForegroundColor Yellow
            }
        }
    }
    try {
        $null = cmd /c "net stop CryptSvc /y" 2>&1
        Start-Sleep -Seconds 2
        $null = cmd /c "net start CryptSvc" 2>&1
    } catch {}
    $null = & ipconfig /flushdns 2>&1
}

# ====================================================================
# SERVER-SIDE: Apache httpd-ssl.conf'a LocationMatch + Require all denied
# ====================================================================

$apacheRoot = Join-Path $mpDir 'Apache'
$httpdExe = Join-Path $apacheRoot 'bin\httpd.exe'
$sslConf = Join-Path $apacheRoot 'conf\extra\httpd-ssl.conf'
$srvMarkerStart = '# === MART-AI-FAST-FAIL BEGIN ==='
$srvMarkerEnd = '# === MART-AI-FAST-FAIL END ==='
$srvBlock = @"
$srvMarkerStart
# erwin DM click freeze fix - dm-ai endpoint kurumsal firewall block ediyor (dis AI provider).
# Vault outbound 22sn timeout. LocationMatch + deny ile request Apache seviyesinde anlik 403,
# vault'a hic gitmesin -> erwin DM 'AI yok' soft fail, devam.
<LocationMatch "^/MartServerCloud/service/authenticate/dm-ai">
    Require all denied
</LocationMatch>
$srvMarkerEnd
"@

function Get-FileSharedRead($Path) {
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $sr = [System.IO.StreamReader]::new($fs)
    $text = $sr.ReadToEnd()
    $sr.Close(); $fs.Close()
    return $text
}

function Write-FileSharedWrite($Path, $Content) {
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($Content)
    $attempt = 0
    while ($true) {
        $attempt++
        try {
            $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
            $fs.Write($bytes, 0, $bytes.Length); $fs.Flush(); $fs.Close()
            return
        } catch [System.IO.IOException] {
            if ($attempt -ge 10) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Invoke-ApacheSyntax {
    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        cmd /c "`"$httpdExe`" -t > `"$tmp`" 2>&1"
        return (Get-Content $tmp -Raw -ErrorAction SilentlyContinue).Trim()
    } finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
}

function Restart-ApacheNative {
    $null = cmd /c "net stop `"$ApacheService`"" 2>&1
    Start-Sleep -Seconds 2
    $null = cmd /c "net start `"$ApacheService`"" 2>&1
    Start-Sleep -Seconds 3
    return (Get-Service -Name $ApacheService).Status.ToString()
}

function Invoke-ServerStatus {
    Write-Host "[SERVER] Apache LocationMatch durumu:" -ForegroundColor Cyan
    if (-not (Test-Path $sslConf)) { Write-Host "  httpd-ssl.conf yok!" -ForegroundColor Red; return }
    $content = Get-FileSharedRead $sslConf
    if ($content -match [regex]::Escape($srvMarkerStart)) {
        Write-Host "  Marker var (uygulanmis)" -ForegroundColor Green
    } else {
        Write-Host "  Marker yok (uygulanmamis)" -ForegroundColor Yellow
    }
    $svc = Get-Service -Name $ApacheService -ErrorAction SilentlyContinue
    if ($svc) { Write-Host ("  Apache service: $($svc.Status)") -ForegroundColor DarkCyan }
}

function Invoke-ServerApply {
    Write-Host "[SERVER] Apache config'e LocationMatch + Require denied ekleniyor..." -ForegroundColor Cyan
    if (-not (Test-Path $sslConf)) { throw "httpd-ssl.conf yok: $sslConf" }
    if (-not (Test-Path $httpdExe)) { throw "httpd.exe yok: $httpdExe" }

    $content = Get-FileSharedRead $sslConf
    if ($content -match [regex]::Escape($srvMarkerStart)) {
        Write-Host "  Zaten var, skip" -ForegroundColor Yellow
        return
    }

    $marker = '<Location /MartServerCloud>'
    $idx = $content.IndexOf($marker)
    if ($idx -lt 0) {
        throw "'<Location /MartServerCloud>' httpd-ssl.conf'ta yok"
    }
    $insertPos = $content.LastIndexOf("`n", $idx) + 1
    $newContent = $content.Substring(0, $insertPos) + $srvBlock + "`r`n" + $content.Substring($insertPos)
    Write-FileSharedWrite $sslConf $newContent
    Write-Host "  Inserted LocationMatch block" -ForegroundColor Green

    $test = Invoke-ApacheSyntax
    if ($test -notmatch 'Syntax OK') {
        Write-Host ("  X SYNTAX ERROR: $test") -ForegroundColor Red
        Write-Host "  Auto-revert..."
        Invoke-ServerRevert
        throw "Apache syntax error"
    }
    Write-Host ("  Syntax OK") -ForegroundColor Green

    $apacheStatus = Restart-ApacheNative
    if ($apacheStatus -eq 'Running') {
        Write-Host "  Apache: Running" -ForegroundColor Green
    } else {
        Write-Host ("  X Apache: $apacheStatus - manuel revert gerek") -ForegroundColor Red
    }
}

function Invoke-ServerRevert {
    Write-Host "[SERVER] Apache config marker temizleniyor..." -ForegroundColor Yellow
    if (-not (Test-Path $sslConf)) { return }
    $content = Get-FileSharedRead $sslConf
    if ($content -match [regex]::Escape($srvMarkerStart)) {
        $pattern = "(?ms)\r?\n?$([regex]::Escape($srvMarkerStart)).*?$([regex]::Escape($srvMarkerEnd))\r?\n?"
        $newContent = [regex]::Replace($content, $pattern, '')
        Write-FileSharedWrite $sslConf $newContent
        Write-Host "  Marker temizlendi" -ForegroundColor Yellow
        $test = Invoke-ApacheSyntax
        Write-Host ("  Syntax: $test") -ForegroundColor DarkGray
        $apacheStatus = Restart-ApacheNative
        Write-Host ("  Apache: $apacheStatus") -ForegroundColor Green
    } else {
        Write-Host "  Marker zaten yok" -ForegroundColor DarkGray
    }
}

# ====================================================================
# Dispatch
# ====================================================================

if ($Status) {
    if ($Role -in 'Client', 'Both') { Invoke-ClientStatus; Write-Host '' }
    if ($Role -in 'Server', 'Both') { Invoke-ServerStatus }
    exit 0
}

if ($Revert) {
    if ($Role -in 'Client', 'Both') { Invoke-ClientRevert; Write-Host '' }
    if ($Role -in 'Server', 'Both') { Invoke-ServerRevert }
    Write-Host ""
    Write-Host "Revert tamam." -ForegroundColor Green
    exit 0
}

# Default = Apply
if ($Role -in 'Client', 'Both') { Invoke-ClientApply; Write-Host '' }
if ($Role -in 'Server', 'Both') { Invoke-ServerApply }

Write-Host ""
Write-Host "=== TAMAM ===" -ForegroundColor Green
Write-Host ""
Write-Host "Test:" -ForegroundColor Cyan
Write-Host "  1. Erwin DM tamamen kapat-ac"
Write-Host "  2. Mart Connect (hizli olmali)"
Write-Host "  3. Bos alana TIKLA (hizli olmali, freeze yok)"
Write-Host "  4. Disconnect, kapat (hizli olmali)"
Write-Host ""
Write-Host "Durum: .\mart-freeze-fix.ps1 -Status"
Write-Host "Geri al: .\mart-freeze-fix.ps1 -Revert"
