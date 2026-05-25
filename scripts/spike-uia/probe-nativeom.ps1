# Spike: try to obtain erwin's Native Object Model (IDispatch) via
# AccessibleObjectFromWindow(hwnd, OBJID_NATIVEOM, IID_IDispatch).
#
# Office (Excel, Word, etc) exposes its Application object via this
# mechanism. If erwin/XTP does too, we can query CommandBars and find
# the addin's WM_COMMAND id directly without any input simulation.
#
# Tries multiple HWNDs (XTPMainFrame + visible XTPToolBars) and multiple
# OBJIDs (NATIVEOM + a few uncommon ones some apps use).
#
# Read-only probe. Pure COM marshalling via oleacc. Zero memory ops.

$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class W {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);

    [DllImport("oleacc.dll", PreserveSig=true)]
    public static extern int AccessibleObjectFromWindow(
        IntPtr hwnd, int dwObjectID, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppvObject);

    public static Guid IID_IDispatch  = new Guid("00020400-0000-0000-C000-000000000046");
    public static Guid IID_IUnknown   = new Guid("00000000-0000-0000-C000-000000000046");
    public static Guid IID_IAccessible= new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
}
'@

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running" -ForegroundColor Red; exit 1 }
$erwinPid = $erwin.Id

# Collect candidate HWNDs to probe
$targets = New-Object System.Collections.ArrayList
[void]$targets.Add(@{ Hwnd = $erwin.MainWindowHandle; Note = 'MainWindowHandle (XTPMainFrame)' })

$proc = [W+EnumWindowsProc] {
    param([IntPtr]$h, [IntPtr]$lp)
    $procId = 0
    [void][W]::GetWindowThreadProcessId($h, [ref]$procId)
    if ($procId -eq $erwinPid -and [W]::IsWindowVisible($h)) {
        $sbC = New-Object System.Text.StringBuilder 64
        [void][W]::GetClassName($h, $sbC, 64)
        $cls = $sbC.ToString()
        if ($cls -eq 'XTPToolBar') {
            $sbT = New-Object System.Text.StringBuilder 128
            [void][W]::GetWindowText($h, $sbT, 128)
            [void]$targets.Add(@{ Hwnd = $h; Note = "XTPToolBar '$($sbT.ToString())'" })
        }
        [void][W]::EnumChildWindows($h, $proc, [IntPtr]::Zero)
    }
    return $true
}
[void][W]::EnumWindows($proc, [IntPtr]::Zero)

# OBJIDs (32-bit signed; the documented values are negative numbers).
# https://learn.microsoft.com/en-us/windows/win32/winauto/object-identifiers
$objIds = @(
    @{ Id = -16; Name = 'OBJID_NATIVEOM' },         # Office Application IDispatch
    @{ Id = -4;  Name = 'OBJID_CLIENT' },           # default client area
    @{ Id = -6;  Name = 'OBJID_TITLEBAR' },
    @{ Id = -2;  Name = 'OBJID_SYSMENU' },
    @{ Id = -15; Name = 'OBJID_QUERYCLASSNAMEIDX' },
    @{ Id =  0;  Name = 'OBJID_WINDOW' }
)

$iids = @(
    @{ Guid = [W]::IID_IDispatch;   Name = 'IDispatch' },
    @{ Guid = [W]::IID_IAccessible; Name = 'IAccessible' },
    @{ Guid = [W]::IID_IUnknown;    Name = 'IUnknown' }
)

$dispatchHits = New-Object System.Collections.ArrayList

foreach ($t in $targets) {
    Write-Host ""
    Write-Host "=== Target hwnd=0x$('{0:X}' -f $t.Hwnd.ToInt64()) - $($t.Note) ===" -ForegroundColor Cyan
    foreach ($oid in $objIds) {
        foreach ($iid in $iids) {
            $obj = $null
            $guid = $iid.Guid
            $hr = -1
            try {
                $hr = [W]::AccessibleObjectFromWindow($t.Hwnd, [int]$oid.Id, [ref]$guid, [ref]$obj)
            } catch {
                Write-Host "  $($oid.Name) / $($iid.Name) threw: $($_.Exception.Message)" -ForegroundColor DarkGray
                continue
            }
            if ($hr -eq 0 -and $obj -ne $null) {
                $type = $obj.GetType().FullName
                Write-Host ("  {0,-22} / {1,-12} OK hr=0 type={2}" -f $oid.Name, $iid.Name, $type) -ForegroundColor Green
                # If it's an IDispatch (not standard IAccessible), capture it for inspection
                if ($iid.Name -eq 'IDispatch' -and $oid.Name -ne 'OBJID_CLIENT') {
                    [void]$dispatchHits.Add(@{ Target = $t; ObjId = $oid; Obj = $obj })
                }
                # Try basic introspection: get type info via IDispatch
                try {
                    $tiCount = $obj.GetType().InvokeMember('GetTypeInfoCount', [System.Reflection.BindingFlags]::InvokeMethod, $null, $obj, $null)
                    Write-Host "      -> GetTypeInfoCount = $tiCount" -ForegroundColor Gray
                } catch {}
            } elseif ($hr -ne 0) {
                # Most failures are routine (E_NOTIMPL etc); show only the unusual ones
                if (($hr -ne -0x7FFFFFFB) -and ($hr -ne -2147467263)) {  # E_NOTIMPL
                    Write-Host ("  {0,-22} / {1,-12} hr=0x{2:X}" -f $oid.Name, $iid.Name, $hr) -ForegroundColor DarkGray
                }
            }
        }
    }
}

# If we got any IDispatch hits, try to enumerate properties + methods
if ($dispatchHits.Count -gt 0) {
    Write-Host ""
    Write-Host "###################################################" -ForegroundColor Green
    Write-Host "# FOUND $($dispatchHits.Count) IDispatch hit(s) - introspecting..." -ForegroundColor Green
    Write-Host "###################################################" -ForegroundColor Green
    foreach ($hit in $dispatchHits) {
        Write-Host ""
        Write-Host "--- $($hit.Target.Note) via $($hit.ObjId.Name) ---" -ForegroundColor Cyan
        $obj = $hit.Obj
        # Try common Office/COM automation property names
        foreach ($prop in @('Application', 'Name', 'CommandBars', 'Parent', 'Controls', 'Workbooks', 'Documents', 'Models', 'ActiveModel', 'Version')) {
            try {
                $val = $obj.$prop
                if ($val -ne $null) {
                    $vt = if ($val -is [string] -or $val -is [int] -or $val -is [double]) { $val } else { $val.GetType().FullName }
                    Write-Host "  $prop = $vt" -ForegroundColor Green
                }
            } catch { }
        }
    }
} else {
    Write-Host ""
    Write-Host "No IDispatch (non-IAccessible) obtained via OBJID_NATIVEOM or related." -ForegroundColor Yellow
    Write-Host "erwin/XTP does not expose Native OM through accessibility framework." -ForegroundColor Yellow
}
