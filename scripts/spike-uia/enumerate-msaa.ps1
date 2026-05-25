# Plan B spike: enumerate erwin's toolbar/ribbon items via MSAA (IAccessible).
#
# Why MSAA, not TB_GETBUTTON: TB_GETBUTTON cross-process needs a pointer to a
# TBBUTTON struct in the TARGET process's address space. Our prior script sent
# a pointer to OUR address space; erwin tried to deref it -> invalid memory
# write -> crash. MSAA via oleacc.dll handles cross-process marshalling
# internally (it's what screen readers use), so we touch no target memory and
# crash erwin nothing.
#
# This is READ ONLY. The script never invokes anything, never sends
# WM_COMMAND, never does accDoDefaultAction. Only enumeration.
#
# Output:
#   - stdout: per-toolbar item list (name, role, default action), MATCH lines
#     highlighted for "Elite", "Add-In", "Manage" tokens.
#   - $env:TEMP\erwin-msaa-<HHmmss>.txt : full dump.

$ErrorActionPreference = 'Stop'

# IAccessible / oleacc PInvoke. Note: AccessibleChildren's VARIANT array is
# tricky in PS - it can return IAccessible* OR a VT_I4 child id. We avoid that
# call entirely and iterate childIds 1..accChildCount instead (works for the
# toolbar-button case where every button is a "simple child").
Add-Type -Language CSharp -ReferencedAssemblies 'System.Windows.Forms' @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class W32 {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);

    [DllImport("oleacc.dll", PreserveSig=true)]
    public static extern int AccessibleObjectFromWindow(
        IntPtr hwnd, uint dwObjectID, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppvObject);

    public const uint OBJID_CLIENT = 0xFFFFFFFC;
    public const uint OBJID_WINDOW = 0;

    public static Guid IID_IAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
}
'@

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running in session $mySession" -ForegroundColor Red; exit 1 }

Write-Host "erwin PID=$($erwin.Id) Hwnd=0x$('{0:X}' -f $erwin.MainWindowHandle.ToInt64())" -ForegroundColor Cyan
Write-Host "Title: $($erwin.MainWindowTitle)" -ForegroundColor Gray
Write-Host ""

$outFile = Join-Path $env:TEMP "erwin-msaa-$(Get-Date -Format 'HHmmss').txt"
$sw = [System.IO.StreamWriter]::new($outFile, $false, [System.Text.Encoding]::UTF8)
$sw.AutoFlush = $true
$matches = New-Object System.Collections.ArrayList

# Collect every visible XTPToolBar + the main frame itself.
$candidates = New-Object System.Collections.ArrayList
[void]$candidates.Add(@{ Hwnd = $erwin.MainWindowHandle; Title = $erwin.MainWindowTitle; Class = 'XTPMainFrame' })

$collectorEnum = [W32+EnumWindowsProc] {
    param([IntPtr]$h, [IntPtr]$lp)
    try {
        $sbC = New-Object System.Text.StringBuilder 256
        [void][W32]::GetClassName($h, $sbC, 256)
        $cls = $sbC.ToString()
        if ([W32]::IsWindowVisible($h) -and ($cls -match 'XTPToolBar|XTPRibbon|XTPMenuBar|XTPCommandBars|XTPPopupBar')) {
            $sbT = New-Object System.Text.StringBuilder 256
            [void][W32]::GetWindowText($h, $sbT, 256)
            [void]$candidates.Add(@{ Hwnd = $h; Title = $sbT.ToString(); Class = $cls })
        }
    } catch { }
    # Recurse to next level - $collectorEnum closure captures itself.
    [void][W32]::EnumChildWindows($h, $collectorEnum, [IntPtr]::Zero)
    return $true
}
[void][W32]::EnumChildWindows($erwin.MainWindowHandle, $collectorEnum, [IntPtr]::Zero)

Write-Host "Candidate windows for MSAA probing: $($candidates.Count)" -ForegroundColor Cyan
foreach ($c in $candidates) {
    Write-Host "  hwnd=0x$('{0:X}' -f $c.Hwnd.ToInt64()) cls='$($c.Class)' title='$($c.Title)'" -ForegroundColor Gray
}
Write-Host ""

function Probe-IAccessible {
    param([IntPtr]$hwnd, [string]$where)
    $iid = [W32]::IID_IAccessible
    $obj = $null
    $hr = [W32]::AccessibleObjectFromWindow($hwnd, [W32]::OBJID_CLIENT, [ref]$iid, [ref]$obj)
    if ($hr -ne 0 -or $null -eq $obj) {
        $script:sw.WriteLine("$where : AccessibleObjectFromWindow hr=0x$('{0:X}' -f $hr) obj=null")
        return
    }

    $count = -1
    try { $count = $obj.accChildCount } catch { $script:sw.WriteLine("$where : accChildCount threw $($_.Exception.Message)"); return }
    $script:sw.WriteLine("$where : accChildCount=$count")
    Write-Host "  $where : $count children" -ForegroundColor Yellow

    if ($count -lt 1 -or $count -gt 5000) { return }

    for ($i = 1; $i -le $count; $i++) {
        $name = ''; $role = ''; $action = ''; $desc = ''; $value = ''
        try { $n = $obj.accName($i); if ($n) { $name = [string]$n } } catch {}
        try { $r = $obj.accRole($i); if ($r) { $role = [string]$r } } catch {}
        try { $a = $obj.accDefaultAction($i); if ($a) { $action = [string]$a } } catch {}
        try { $d = $obj.accDescription($i); if ($d) { $desc = [string]$d } } catch {}
        try { $v = $obj.accValue($i); if ($v) { $value = [string]$v } } catch {}

        # Skip totally empty rows (XTPToolBar pads with separators).
        if ([string]::IsNullOrWhiteSpace($name) -and [string]::IsNullOrWhiteSpace($desc) -and [string]::IsNullOrWhiteSpace($action)) { continue }

        $line = "  [$i] role=$role name='$name' action='$action' desc='$desc' val='$value'"
        $script:sw.WriteLine($line)
        if ($name -match 'Elite|EliteSoft|Add\-?In|Manage' -or $desc -match 'Elite|EliteSoft|Add\-?In|Manage') {
            $matchLine = "MATCH @ $where : $line"
            Write-Host "    >>> $matchLine" -ForegroundColor Green
            [void]$script:matches.Add(@{ Where = $where; Index = $i; Name = $name; Action = $action; Desc = $desc })
        }
    }

    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($obj) | Out-Null
}

foreach ($c in $candidates) {
    $where = "0x$('{0:X}' -f $c.Hwnd.ToInt64())[$($c.Class) '$($c.Title)']"
    Write-Host ""
    Write-Host "=== $where ===" -ForegroundColor Cyan
    try {
        Probe-IAccessible -hwnd $c.Hwnd -where $where
    } catch {
        Write-Host "  Probe threw: $($_.Exception.Message)" -ForegroundColor Red
        $script:sw.WriteLine("$where : PROBE EXCEPTION $($_.Exception.Message)")
    }
}

$sw.Close()
Write-Host ""
Write-Host "Dump: $outFile" -ForegroundColor Cyan
Write-Host "Matches: $($matches.Count)" -ForegroundColor $(if ($matches.Count -gt 0) { 'Green' } else { 'Yellow' })
foreach ($m in $matches) {
    Write-Host "  $($m.Where) idx=$($m.Index) name='$($m.Name)' action='$($m.Action)'" -ForegroundColor Green
}

if ($matches.Count -eq 0) {
    Write-Host ""
    Write-Host "MSAA returned no name match. Two possibilities:" -ForegroundColor Yellow
    Write-Host "  1) Add-In menu lives inside a closed ribbon submenu (probe again with menu OPEN)." -ForegroundColor Yellow
    Write-Host "  2) XTPCommandBars exposes nothing to MSAA. Then last resort: native bridge route." -ForegroundColor Yellow
}
