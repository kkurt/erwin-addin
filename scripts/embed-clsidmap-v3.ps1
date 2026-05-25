# V3: use Microsoft.NET.HostModel.ResourceUpdater directly (instance API).
# V2 used static ComHost.Create but appears to do BeginUpdateResource which
# fails silently on managed PE files in some scenarios. ResourceUpdater
# does cross-platform PE rewrite without Win32 BeginUpdateResource.

$ErrorActionPreference = 'Stop'

$hostModelDll = 'C:\Program Files\dotnet\sdk\10.0.102\Microsoft.NET.HostModel.dll'
$template     = 'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Host.win-x64\10.0.2\runtimes\win-x64\native\comhost.dll'
$clsidMap     = 'c:\Users\Kursat\Repos\erwin-addin\obj\Debug\net10.0-windows\EliteSoft.Erwin.AddIn.clsidmap'
$output       = 'c:\Users\Kursat\Repos\erwin-addin\bin\Debug\net10.0-windows\EliteSoft.Erwin.AddIn.comhost.dll'
$installTo    = "$env:LOCALAPPDATA\EliteSoft\ErwinAddIn\EliteSoft.Erwin.AddIn.comhost.dll"

$asm = [System.Reflection.Assembly]::LoadFrom($hostModelDll)
$ruType = $asm.GetType('Microsoft.NET.HostModel.ResourceUpdater')

Write-Host "Copying template..." -ForegroundColor Cyan
Copy-Item -LiteralPath $template -Destination $output -Force

# RT_RCDATA = 10 (Win32 standard). The HostModel ResourceUpdater takes
# IntPtr for type/name; small ints fit because they're encoded as
# MAKEINTRESOURCE(n) by the underlying Win32 call.
$mapBytes = [System.IO.File]::ReadAllBytes($clsidMap)
Write-Host "clsidmap bytes: $($mapBytes.Length)" -ForegroundColor Gray

Write-Host "Creating ResourceUpdater for $output..." -ForegroundColor Cyan
$ru = $ruType.GetConstructor([Type[]]@([string])).Invoke(@($output))

# AddResource(byte[] data, IntPtr lpType, IntPtr lpName)
$addMethod = $ruType.GetMethod('AddResource', [Type[]]@([byte[]], [IntPtr], [IntPtr]))
$RT_RCDATA = 10
$ClsidMapId = 64
Write-Host "AddResource(type=$RT_RCDATA, name=$ClsidMapId)..." -ForegroundColor Cyan
[void]$addMethod.Invoke($ru, @($mapBytes, [IntPtr]$RT_RCDATA, [IntPtr]$ClsidMapId))

Write-Host "ResourceUpdater.Update()..." -ForegroundColor Cyan
[void]$ruType.GetMethod('Update').Invoke($ru, $null)

$newSize = (Get-Item $output).Length
$newDate = (Get-Item $output).LastWriteTime
Write-Host "Output: Size=$newSize Date=$newDate" -ForegroundColor Green

# Deploy
Copy-Item -LiteralPath $output -Destination $installTo -Force
Write-Host "Deployed to $installTo" -ForegroundColor Green

Write-Host ""
Write-Host "Testing CoCreateInstance..." -ForegroundColor Cyan
try {
    $obj = New-Object -ComObject 'EliteSoft.Meta.AddIn'
    Write-Host "ACTIVATION SUCCESS - type: $($obj.GetType().FullName)" -ForegroundColor Green
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($obj)
} catch {
    Write-Host "ACTIVATION FAILED: $($_.Exception.Message)" -ForegroundColor Red
}

# Also dump the tail of the file to verify clsidmap is embedded
Write-Host ""
Write-Host "Last 500 bytes (search for 'EliteSoft'):" -ForegroundColor Cyan
$bytes = [System.IO.File]::ReadAllBytes($output)
$tail = [System.Text.Encoding]::UTF8.GetString($bytes[($bytes.Length-500)..($bytes.Length-1)])
if ($tail -match 'EliteSoft') { Write-Host "FOUND 'EliteSoft' in tail - resource likely embedded" -ForegroundColor Green } else { Write-Host "'EliteSoft' NOT in tail (embed may have failed)" -ForegroundColor Yellow }
