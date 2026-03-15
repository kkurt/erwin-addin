Write-Host "=== erwin Add-In Registry Entries ===" -ForegroundColor Cyan

$erwinRegBase = "HKCU:\SOFTWARE\erwin\Data Modeler"
if (Test-Path $erwinRegBase) {
    Get-ChildItem $erwinRegBase -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*Add-In*' -or $_.Name -like '*Elite*' } |
        ForEach-Object {
            Write-Host "`n$($_.Name)" -ForegroundColor Yellow
            $_.GetValueNames() | ForEach-Object {
                $val = $_.ToString()
                $data = (Get-ItemProperty -Path "Registry::$($_.PSParentPath)\$($_.PSChildName)" -Name $val -ErrorAction SilentlyContinue).$val
                Write-Host "  $val = $data"
            }
            $props = Get-ItemProperty -Path "Registry::$($_.Name)" -ErrorAction SilentlyContinue
            $props.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object {
                Write-Host "  $($_.Name) = $($_.Value)"
            }
        }
} else {
    Write-Host "erwin registry key not found!" -ForegroundColor Red
}

Write-Host "`n=== COM CLSID Check ===" -ForegroundColor Cyan
$clsidPath = "Registry::HKEY_CLASSES_ROOT\CLSID\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}"
if (Test-Path $clsidPath) {
    Write-Host "CLSID found" -ForegroundColor Green
    $inproc = Get-ItemProperty "$clsidPath\InprocServer32" -ErrorAction SilentlyContinue
    Write-Host "  InprocServer32: $($inproc.'(default)')"
    Write-Host "  ThreadingModel: $($inproc.ThreadingModel)"

    # Check for stale regasm entries
    if ($inproc.Assembly) { Write-Host "  WARNING: Old regasm 'Assembly' entry found: $($inproc.Assembly)" -ForegroundColor Yellow }
    if ($inproc.Class) { Write-Host "  WARNING: Old regasm 'Class' entry found: $($inproc.Class)" -ForegroundColor Yellow }
    if ($inproc.RuntimeVersion) { Write-Host "  WARNING: Old regasm 'RuntimeVersion' entry found: $($inproc.RuntimeVersion)" -ForegroundColor Yellow }
    if ($inproc.CodeBase) { Write-Host "  WARNING: Old regasm 'CodeBase' entry found: $($inproc.CodeBase)" -ForegroundColor Yellow }
} else {
    Write-Host "CLSID NOT found!" -ForegroundColor Red
}
