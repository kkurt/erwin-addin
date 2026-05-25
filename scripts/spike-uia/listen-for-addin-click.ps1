# Spike: passively listen to erwin's WinEvents while user manually clicks
# the addin. Goal: capture the WM_COMMAND id (= MSAA child id) of the
# "Elite Soft Erwin Addin" popup item so we can replay it via PostMessage.
#
# SetWinEventHook with WINEVENT_OUTOFCONTEXT runs the callback in OUR
# process - NO injection into erwin. This is what AccessibleEventViewer
# and Inspect.exe use.

$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WinEv {
    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    [DllImport("oleacc.dll", PreserveSig=true)]
    public static extern int AccessibleObjectFromEvent(IntPtr hwnd, uint dwObjectID, uint dwChildID,
        [MarshalAs(UnmanagedType.Interface)] out object ppacc, out object pvarChild);

    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr h, uint min, uint max, uint remove);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x; public int y; }

    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const uint EVENT_OBJECT_CREATE        = 0x8000;
    public const uint EVENT_OBJECT_SHOW          = 0x8002;
    public const uint EVENT_OBJECT_INVOKED       = 0x8013;
    public const uint EVENT_SYSTEM_MENUSTART     = 0x0004;
    public const uint EVENT_SYSTEM_MENUEND       = 0x0005;
    public const uint EVENT_SYSTEM_MENUPOPUPSTART = 0x0006;
    public const uint EVENT_SYSTEM_MENUPOPUPEND   = 0x0007;
}
'@

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running" -ForegroundColor Red; exit 1 }
$erwinPid = [uint32]$erwin.Id
Write-Host "Listening to erwin PID=$erwinPid" -ForegroundColor Cyan
Write-Host ""

# Filter for events that matter: menu life cycle + invocations
$interesting = @{
    0x0004 = 'MENUSTART'
    0x0005 = 'MENUEND'
    0x0006 = 'POPUPSTART'
    0x0007 = 'POPUPEND'
    0x8013 = 'INVOKED'
    0x8002 = 'SHOW'
    0x8000 = 'CREATE'
}

# We keep ONE delegate alive in a script-scope var so the GC doesn't reap it.
$script:callback = [WinEv+WinEventDelegate] {
    param($hHook, $evt, $hwnd, $idObj, $idChild, $thread, $time)
    if (-not $interesting.ContainsKey([uint32]$evt)) { return }
    $evtName = $interesting[[uint32]$evt]

    # Skip noise: only menu-relevant + invocations
    if ($evtName -eq 'CREATE') { return }
    if ($evtName -eq 'SHOW') { return }

    $cls = ''
    if ($hwnd -ne [IntPtr]::Zero) {
        $sb = New-Object System.Text.StringBuilder 64
        [void][WinEv]::GetClassName($hwnd, $sb, 64)
        $cls = $sb.ToString()
    }

    $name = ''
    try {
        $acc = $null; $vc = $null
        $hr = [WinEv]::AccessibleObjectFromEvent($hwnd, [uint32]$idObj, [uint32]$idChild, [ref]$acc, [ref]$vc)
        if ($hr -eq 0 -and $acc -ne $null) {
            try {
                $n = $acc.accName([int]$idChild)
                if ($n) { $name = [string]$n }
            } catch {}
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($acc) | Out-Null
        }
    } catch {}

    $line = "{0:HH:mm:ss.fff} {1,-10} hwnd=0x{2,-8:X} cls={3,-18} idObj={4,4} idChild={5,5} name='{6}'" -f `
        (Get-Date), $evtName, $hwnd.ToInt64(), $cls, $idObj, $idChild, $name

    $color = 'Gray'
    if ($name -match 'Elite|Add\-?In|EliteSoft|Erwin\s*Addin') { $color = 'Green' }
    elseif ($evtName -eq 'INVOKED') { $color = 'Cyan' }
    elseif ($evtName -in @('POPUPSTART', 'MENUSTART')) { $color = 'Yellow' }
    Write-Host $line -ForegroundColor $color
}

# Install hook for entire erwin process (idThread=0 = all threads).
# WINEVENT_OUTOFCONTEXT means callback runs in OUR process - no injection.
$hook = [WinEv]::SetWinEventHook(
    [WinEv]::EVENT_SYSTEM_MENUSTART,
    [WinEv]::EVENT_OBJECT_INVOKED,
    [IntPtr]::Zero,
    $script:callback,
    $erwinPid, 0,
    [WinEv]::WINEVENT_OUTOFCONTEXT
)
if ($hook -eq [IntPtr]::Zero) { Write-Host "SetWinEventHook failed" -ForegroundColor Red; exit 2 }
Write-Host "Hook installed (OUTOFCONTEXT, all erwin threads)." -ForegroundColor Green
Write-Host ""
Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host " NOW: in erwin, click 'Add-Ins' on the ribbon, then click" -ForegroundColor Cyan
Write-Host " 'Elite Soft Erwin Addin' in the popup. Take your time." -ForegroundColor Cyan
Write-Host " Script listens for 60s. Press Ctrl+C when done." -ForegroundColor Cyan
Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host ""

# Pump messages so OUTOFCONTEXT callback can fire on this thread.
$deadline = (Get-Date).AddSeconds(60)
$msg = New-Object WinEv+MSG
try {
    while ((Get-Date) -lt $deadline) {
        if ([WinEv]::PeekMessage([ref]$msg, [IntPtr]::Zero, 0, 0, 1)) {
            [void][WinEv]::TranslateMessage([ref]$msg)
            [void][WinEv]::DispatchMessage([ref]$msg)
        } else {
            Start-Sleep -Milliseconds 20
        }
    }
} finally {
    [void][WinEv]::UnhookWinEvent($hook)
    Write-Host ""
    Write-Host "Hook uninstalled." -ForegroundColor Gray
}
