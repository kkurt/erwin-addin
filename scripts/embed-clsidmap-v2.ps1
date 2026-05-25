# V2: use Microsoft.NET.HostModel.dll's HostWriter.CreateComHost directly
# instead of reimplementing the resource embedding. The SDK's own task
# implementation, bypassing whatever caching/skip logic in the MSBuild
# pipeline that's preventing it from running.

$ErrorActionPreference = 'Stop'

$hostModelDll = 'C:\Program Files\dotnet\sdk\10.0.102\Microsoft.NET.HostModel.dll'
$template     = 'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Host.win-x64\10.0.2\runtimes\win-x64\native\comhost.dll'
$clsidMap     = 'c:\Users\Kursat\Repos\erwin-addin\obj\Debug\net10.0-windows\EliteSoft.Erwin.AddIn.clsidmap'
$output       = 'c:\Users\Kursat\Repos\erwin-addin\bin\Debug\net10.0-windows\EliteSoft.Erwin.AddIn.comhost.dll'
$installTo    = "$env:LOCALAPPDATA\EliteSoft\ErwinAddIn\EliteSoft.Erwin.AddIn.comhost.dll"

foreach ($p in $hostModelDll, $template, $clsidMap) {
    if (-not (Test-Path $p)) { Write-Host "MISSING: $p" -ForegroundColor Red; exit 1 }
}

Write-Host "Loading $hostModelDll..." -ForegroundColor Cyan
$asm = [System.Reflection.Assembly]::LoadFrom($hostModelDll)
Write-Host "Loaded: $($asm.FullName)" -ForegroundColor Gray

$comHost = $asm.GetType('Microsoft.NET.HostModel.ComHost.ComHost')
if (-not $comHost) { Write-Host "ComHost type not found" -ForegroundColor Red; exit 2 }

# Create(string comHostSourceFilePath, string comHostDestinationFilePath,
#        string clsidmapFilePath, IReadOnlyDictionary<int, string> typeLibraries)
$createMethod = $comHost.GetMethod('Create', [System.Reflection.BindingFlags]'Public,Static')
if (-not $createMethod) { Write-Host "Create method not found" -ForegroundColor Red; exit 3 }

# Empty typeLibraries dict (we have no .tlb). Cast to IReadOnlyDictionary
# explicitly so PowerShell doesn't wrap in PSObject when passed to Invoke.
$emptyTypeLibs = [System.Collections.Generic.IReadOnlyDictionary[int, string]] (New-Object 'System.Collections.Generic.Dictionary[int, string]')

Write-Host "Calling ComHost.Create(template, output, clsidmap, empty)..." -ForegroundColor Cyan
$invokeArgs = [object[]]@($template, $output, $clsidMap, $emptyTypeLibs)
$createMethod.Invoke($null, $invokeArgs)
Write-Host "Create returned successfully" -ForegroundColor Green

$newSize = (Get-Item $output).Length
$newDate = (Get-Item $output).LastWriteTime
Write-Host "Output: $output" -ForegroundColor Gray
Write-Host "  Size: $newSize  Date: $newDate" -ForegroundColor Gray

# Deploy
Copy-Item -LiteralPath $output -Destination $installTo -Force
Write-Host "Deployed to $installTo" -ForegroundColor Green

# Test
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
