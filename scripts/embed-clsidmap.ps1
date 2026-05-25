# Workaround for .NET SDK 10.0.102 CreateComHostTask silently NOT embedding
# the .clsidmap resource into the project's comhost.dll. Without the
# embedded resource, comhost.dll cannot resolve CLSID -> .NET type, so
# CoCreateInstance fails with TYPE_E_CANTLOADLIBRARY (0x80029C4A) and
# erwin's Add-In Manager hides our menu entry on activation-validation.
#
# Mirrors dotnet/runtime HostWriter.CreateComHost():
#   - Copies the template comhost.dll
#   - Writes the clsidmap JSON as Win32 resource (type RT_RCDATA / id 64)
#   - Uses BeginUpdateResource / UpdateResource / EndUpdateResource
#
# References:
#   https://github.com/dotnet/runtime/blob/main/src/installer/managed/Microsoft.NET.HostModel/ComHost/HostWriter.cs
#   https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-beginupdateresourcew

param(
    [string]$Template  = 'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Host.win-x64\10.0.2\runtimes\win-x64\native\comhost.dll',
    [string]$ClsidMap  = 'c:\Users\Kursat\Repos\erwin-addin\obj\Debug\net10.0-windows\EliteSoft.Erwin.AddIn.clsidmap',
    [string]$Output    = 'c:\Users\Kursat\Repos\erwin-addin\bin\Debug\net10.0-windows\EliteSoft.Erwin.AddIn.comhost.dll',
    [string]$InstallTo = "$env:LOCALAPPDATA\EliteSoft\ErwinAddIn"
)

$ErrorActionPreference = 'Stop'

foreach ($p in $Template, $ClsidMap) {
    if (-not (Test-Path $p)) { Write-Host "MISSING: $p" -ForegroundColor Red; exit 1 }
}

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ResEdit {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr BeginUpdateResourceW(string pFileName, bool bDeleteExistingResources);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool UpdateResourceW(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cb);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool EndUpdateResourceW(IntPtr hUpdate, bool fDiscard);
}
'@

# Step 1: copy template
Write-Host "Copying template $Template -> $Output" -ForegroundColor Cyan
Copy-Item -LiteralPath $Template -Destination $Output -Force

# Step 2: read clsidmap bytes
$mapBytes = [System.IO.File]::ReadAllBytes($ClsidMap)
Write-Host "Loaded clsidmap: $($mapBytes.Length) bytes" -ForegroundColor Gray

# Step 3: open resource update handle
# bDeleteExistingResources=false (keep template's existing resources)
$hUpdate = [ResEdit]::BeginUpdateResourceW($Output, $false)
if ($hUpdate -eq [IntPtr]::Zero) {
    $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Write-Host "BeginUpdateResource failed err=$err" -ForegroundColor Red
    exit 2
}

# Step 4: embed resource. Type=RT_RCDATA(10), ID=64 (the .NET HostModel constant), lang=0
$RT_RCDATA = 10
$ClsidMapResourceName = 64
$ok = [ResEdit]::UpdateResourceW($hUpdate, [IntPtr]$RT_RCDATA, [IntPtr]$ClsidMapResourceName, 0, $mapBytes, [uint32]$mapBytes.Length)
if (-not $ok) {
    $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Write-Host "UpdateResource failed err=$err" -ForegroundColor Red
    [void][ResEdit]::EndUpdateResourceW($hUpdate, $true)   # discard
    exit 3
}

# Step 5: commit
$ok = [ResEdit]::EndUpdateResourceW($hUpdate, $false)
if (-not $ok) {
    $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Write-Host "EndUpdateResource failed err=$err" -ForegroundColor Red
    exit 4
}
Write-Host "Resource embedded into $Output" -ForegroundColor Green

# Verify size changed (template + clsidmap + PE resource overhead)
$newSize = (Get-Item $Output).Length
Write-Host "comhost.dll size after embed: $newSize bytes" -ForegroundColor Gray

# Step 6: copy to install dir
if ($InstallTo -and (Test-Path $InstallTo)) {
    Copy-Item -LiteralPath $Output -Destination $InstallTo -Force
    Write-Host "Deployed to $InstallTo\EliteSoft.Erwin.AddIn.comhost.dll" -ForegroundColor Green
}

# Step 7: verify activation works
Write-Host ""
Write-Host "Testing CoCreateInstance..." -ForegroundColor Cyan
try {
    $obj = New-Object -ComObject 'EliteSoft.Meta.AddIn'
    Write-Host "ACTIVATION SUCCESS - type: $($obj.GetType().FullName)" -ForegroundColor Green
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($obj)
} catch {
    Write-Host "ACTIVATION FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 5
}
