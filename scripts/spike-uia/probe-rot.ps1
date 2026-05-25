# Spike: enumerate Running Object Table (ROT) entries to see if erwin
# registers its Application object. If yes, an external VBS/PS can
# GetObject() it and invoke our addin's Execute() in-process - no
# injection, no menu click, no SEP issue.

$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Collections.Generic;

public static class RotHelper {
    [DllImport("ole32.dll")] public static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);
    [DllImport("ole32.dll")] public static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

    public static List<string> EnumDisplayNames() {
        var list = new List<string>();
        IRunningObjectTable rot;
        if (GetRunningObjectTable(0, out rot) != 0) return list;
        IEnumMoniker em;
        rot.EnumRunning(out em);
        IMoniker[] monikers = new IMoniker[1];
        IntPtr fetched = IntPtr.Zero;
        IBindCtx ctx;
        CreateBindCtx(0, out ctx);
        while (em.Next(1, monikers, fetched) == 0) {
            string name;
            monikers[0].GetDisplayName(ctx, null, out name);
            list.Add(name);
            Marshal.ReleaseComObject(monikers[0]);
        }
        Marshal.ReleaseComObject(em);
        Marshal.ReleaseComObject(ctx);
        Marshal.ReleaseComObject(rot);
        return list;
    }
}
'@

Write-Host "Enumerating Running Object Table..." -ForegroundColor Cyan
$names = [RotHelper]::EnumDisplayNames()
Write-Host "Total entries: $($names.Count)" -ForegroundColor Cyan
Write-Host ""

if ($names.Count -eq 0) {
    Write-Host "ROT is empty. No app registered." -ForegroundColor Yellow
    exit 0
}

Write-Host "All entries:" -ForegroundColor Gray
$names | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }

Write-Host ""
$erwinLike = @($names | Where-Object { $_ -match 'erwin|Erwin|ER/|ER\\|MetaRepo|EliteSoft|AMDO|SCAPI' })
if ($erwinLike.Count -gt 0) {
    Write-Host "ERWIN-RELATED ENTRIES:" -ForegroundColor Green
    $erwinLike | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }
} else {
    Write-Host "No erwin-related ROT entries. erwin doesn't register itself." -ForegroundColor Yellow
}

# Also try common erwin-like ProgIDs via GetActiveObject
Write-Host ""
Write-Host "Trying GetActiveObject for common erwin ProgIDs..." -ForegroundColor Cyan
$probes = @(
    'Erwin.Application', 'erwin.Application', 'ERwin.Application',
    'Erwin9.Application', 'Erwin10.Application',
    'CAERwin.Application', 'AMDO.Application', 'SCAPI.Application',
    'ER1.Application', 'ER1', 'erwinDM.Application', 'erwinDM'
)
foreach ($p in $probes) {
    try {
        $clsid = $null
        $hr = [System.Runtime.InteropServices.Marshal]::GetTypeFromProgID($p, $false)
        if ($null -ne $hr) {
            Write-Host "  $p -> CLSID exists (typeof=$($hr.GUID))" -ForegroundColor Yellow
            try {
                $obj = [System.Runtime.InteropServices.Marshal]::GetActiveObject($p)
                Write-Host "    GetActiveObject SUCCESS! type=$($obj.GetType().FullName)" -ForegroundColor Green
                [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($obj)
            } catch {
                Write-Host "    GetActiveObject failed: $($_.Exception.Message)" -ForegroundColor DarkGray
            }
        }
    } catch { }
}

# Last: list erwin-related ProgIDs from registry
Write-Host ""
Write-Host "ProgIDs containing 'erwin' or 'AMDO' in registry:" -ForegroundColor Cyan
Get-ChildItem 'HKLM:\SOFTWARE\Classes' -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -match 'erwin|Erwin|AMDO|SCAPI|ERwin' -and $_.PSChildName -notmatch '^CLSID|^TypeLib|^Interface' } |
    Select-Object -First 30 |
    ForEach-Object { Write-Host "  $($_.PSChildName)" -ForegroundColor Gray }
