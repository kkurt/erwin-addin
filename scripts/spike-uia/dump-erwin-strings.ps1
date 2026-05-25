# Spike: read erwin.exe's string table to find Add-In related WM_COMMAND IDs.
#
# Rationale (from project memory reference_alter_script_wizard_automation):
#   "All ribbon commands must be driven via WM_COMMAND with IDs harvested
#    from erwin.exe's string table."
# Known IDs already in use: 1082 (CC wizard), 1056 (RD Alter Script),
# 1161 (Alter Script menu), 61631 (Forward Engineer Alter Script ribbon).
#
# This spike enumerates ALL string table entries in erwin.exe and filters
# for tokens like "Add-In", "Add Ins", "Manage", etc. The resource ID is
# (by MFC convention) the WM_COMMAND ID.
#
# Pure read-only PE resource inspection. NO cross-process anything.
# Equivalent to opening erwin.exe in Resource Hacker.

$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hReservedNull, uint dwFlags);
    [DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="LoadStringW")]
    public static extern int LoadStringW(IntPtr hInstance, uint uID, StringBuilder lpBuffer, int cchBufferMax);

    public const uint LOAD_LIBRARY_AS_DATAFILE       = 0x00000002;
    public const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;
}
'@

$erwinExe = 'C:\Program Files\erwin\Data Modeler r10\erwin.exe'
if (-not (Test-Path $erwinExe)) { Write-Host "erwin.exe not at $erwinExe" -ForegroundColor Red; exit 1 }

$flags = [W]::LOAD_LIBRARY_AS_DATAFILE -bor [W]::LOAD_LIBRARY_AS_IMAGE_RESOURCE
$h = [W]::LoadLibraryExW($erwinExe, [IntPtr]::Zero, $flags)
if ($h -eq [IntPtr]::Zero) {
    $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Write-Host "LoadLibraryEx failed err=$err" -ForegroundColor Red; exit 2
}
Write-Host "Loaded erwin.exe as data hMod=0x$('{0:X}' -f $h.ToInt64())" -ForegroundColor Cyan

try {
    $hits = New-Object System.Collections.ArrayList
    $allCount = 0
    $sb = New-Object System.Text.StringBuilder 1024
    # MFC convention: command IDs typically live in 0x8000-0xEFFF range or
    # menu IDs in lower ranges. Sweep 1..65535 - cheap and complete.
    for ($id = 1; $id -le 65535; $id++) {
        $sb.Length = 0
        $len = [W]::LoadStringW($h, [uint32]$id, $sb, 1024)
        if ($len -le 0) { continue }
        $allCount++
        $s = $sb.ToString()
        # Filter for addin-related vocabulary.
        if ($s -match 'Add\-?In|AddIn|Add\s*Ins|Manage\s+Add') {
            [void]$hits.Add([pscustomobject]@{
                Id = $id; IdHex = ('0x{0:X}' -f $id); Text = $s
            })
        }
    }
    Write-Host "Total strings: $allCount" -ForegroundColor Gray
    Write-Host "Addin-related matches: $($hits.Count)" -ForegroundColor $(if ($hits.Count -gt 0) { 'Green' } else { 'Yellow' })
    Write-Host ""
    $hits | Sort-Object Id | Format-Table -AutoSize | Out-String | Write-Host

    if ($hits.Count -gt 0) {
        $outFile = Join-Path $env:TEMP 'erwin-addin-cmdids.txt'
        $hits | Sort-Object Id | Export-Csv -NoTypeInformation -Path $outFile -Encoding UTF8
        Write-Host "CSV dump: $outFile" -ForegroundColor Cyan
    }

    # Also dump well-known reference IDs to confirm string table is being read correctly.
    Write-Host ""
    Write-Host "Sanity check - known IDs from project memory:" -ForegroundColor Cyan
    foreach ($known in @(1082, 1056, 1161, 12323, 12324, 12325, 12327, 61631)) {
        $sb.Length = 0
        $len = [W]::LoadStringW($h, [uint32]$known, $sb, 1024)
        if ($len -gt 0) {
            Write-Host ("  {0,-5} : {1}" -f $known, $sb.ToString()) -ForegroundColor Gray
        } else {
            Write-Host ("  {0,-5} : (no string)" -f $known) -ForegroundColor DarkGray
        }
    }

} finally {
    [void][W]::FreeLibrary($h)
}
