# Spike: capture EVERY WinEvent in erwin's process while user clicks the
# addin. No filtering at the event-type level. Writes to file + stdout.
#
# Goal: find out which event(s) fire on XTP popup item click, since the
# narrow filter set in the previous spike yielded nothing.

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

    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    [DllImport("oleacc.dll", PreserveSig=true)]
    public static extern int AccessibleObjectFromEvent(IntPtr hwnd, uint dwObjectID, uint dwChildID,
        [MarshalAs(UnmanagedType.Interface)] out object ppacc, out object pvarChild);

    [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x; public int y; }

    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint EVENT_MIN = 0x00000001;
    public const uint EVENT_MAX = 0x7FFFFFFF;  // all events
}
'@

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running" -ForegroundColor Red; exit 1 }
$erwinPid = [uint32]$erwin.Id
Write-Host "Listening to ALL WinEvents from erwin PID=$erwinPid" -ForegroundColor Cyan

$logFile = Join-Path $env:TEMP "erwin-winevents-$(Get-Date -Format 'HHmmss').log"
Write-Host "Log file: $logFile" -ForegroundColor Gray

# Friendly names for known event types (subset; full list in WinUser.h).
$evtNames = @{
    0x0001='SYSTEM_SOUND'; 0x0002='SYSTEM_ALERT'; 0x0003='SYSTEM_FOREGROUND';
    0x0004='MENUSTART'; 0x0005='MENUEND'; 0x0006='POPUPSTART'; 0x0007='POPUPEND';
    0x0008='CAPTURESTART'; 0x0009='CAPTUREEND'; 0x000A='MOVESIZESTART'; 0x000B='MOVESIZEEND';
    0x000C='CONTEXTHELPSTART'; 0x000D='CONTEXTHELPEND'; 0x000E='DRAGDROPSTART'; 0x000F='DRAGDROPEND';
    0x0010='DIALOGSTART'; 0x0011='DIALOGEND'; 0x0012='SCROLLINGSTART'; 0x0013='SCROLLINGEND';
    0x0014='SWITCHSTART'; 0x0015='SWITCHEND'; 0x0016='MINIMIZESTART'; 0x0017='MINIMIZEEND';
    0x0020='SYSTEM_DESKTOPSWITCH'; 0x0030='SYSTEM_END';
    0x8000='OBJECT_CREATE'; 0x8001='OBJECT_DESTROY'; 0x8002='OBJECT_SHOW'; 0x8003='OBJECT_HIDE';
    0x8004='OBJECT_REORDER'; 0x8005='OBJECT_FOCUS'; 0x8006='OBJECT_SELECTION';
    0x8007='OBJECT_SELECTIONADD'; 0x8008='OBJECT_SELECTIONREMOVE'; 0x8009='OBJECT_SELECTIONWITHIN';
    0x800A='OBJECT_STATECHANGE'; 0x800B='OBJECT_LOCATIONCHANGE'; 0x800C='OBJECT_NAMECHANGE';
    0x800D='OBJECT_DESCRIPTIONCHANGE'; 0x800E='OBJECT_VALUECHANGE'; 0x800F='OBJECT_PARENTCHANGE';
    0x8010='OBJECT_HELPCHANGE'; 0x8011='OBJECT_DEFACTIONCHANGE'; 0x8012='OBJECT_ACCELERATORCHANGE';
    0x8013='OBJECT_INVOKED'; 0x8014='OBJECT_TEXTSELECTIONCHANGED'; 0x8015='OBJECT_CONTENTSCROLLED';
}

# Skip most-noisy events that don't help us identify clicks
$skipEvents = [System.Collections.Generic.HashSet[uint32]]@(
    0x800B  # LOCATIONCHANGE - fires on every cursor move
    0x800C  # NAMECHANGE - status bar updates
    0x8005  # FOCUS - very chatty
    0x8004  # REORDER
)

$writer = [System.IO.StreamWriter]::new($logFile, $false, [System.Text.Encoding]::UTF8)
$writer.AutoFlush = $true

$script:eventCount = 0
$script:callback = [WinEv+WinEventDelegate] {
    param($hHook, $evt, $hwnd, $idObj, $idChild, $thread, $time)
    $evU = [uint32]$evt
    if ($skipEvents.Contains($evU)) { return }
    $script:eventCount++

    $evtName = if ($evtNames.ContainsKey([uint32]$evU)) { $evtNames[[uint32]$evU] } else { '0x{0:X}' -f $evU }

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

    $line = "{0:HH:mm:ss.fff} {1,-22} hwnd=0x{2,-8:X} cls={3,-22} idObj={4,5} idChild={5,6} name='{6}'" -f `
        (Get-Date), $evtName, $hwnd.ToInt64(), $cls, $idObj, $idChild, $name

    $writer.WriteLine($line)

    # Highlight likely-interesting events on stdout
    if ($evtName -match 'INVOK|MENU|POPUP|DIALOG' -or $name -match 'Elite|Add\-?In') {
        $color = if ($name -match 'Elite|Add\-?In') { 'Green' } else { 'Yellow' }
        Write-Host $line -ForegroundColor $color
    }
}

$hook = [WinEv]::SetWinEventHook(
    [WinEv]::EVENT_MIN, [WinEv]::EVENT_MAX,
    [IntPtr]::Zero, $script:callback,
    $erwinPid, 0,
    [WinEv]::WINEVENT_OUTOFCONTEXT
)
if ($hook -eq [IntPtr]::Zero) { Write-Host "SetWinEventHook failed" -ForegroundColor Red; $writer.Close(); exit 2 }
Write-Host "Hook installed. ALL events (except cursor noise) captured." -ForegroundColor Green
Write-Host ""
Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host " NOW: click 'Add-Ins' ribbon button + 'Elite Soft Erwin Addin'" -ForegroundColor Cyan
Write-Host " (or just click the addin if popup is still in your muscle memory)" -ForegroundColor Cyan
Write-Host " Script listens for 30s." -ForegroundColor Cyan
Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host ""

$msg = New-Object WinEv+MSG
$deadline = (Get-Date).AddSeconds(30)
try {
    while ((Get-Date) -lt $deadline) {
        if ([WinEv]::PeekMessage([ref]$msg, [IntPtr]::Zero, 0, 0, 1)) {
            [void][WinEv]::TranslateMessage([ref]$msg)
            [void][WinEv]::DispatchMessage([ref]$msg)
        } else {
            Start-Sleep -Milliseconds 10
        }
    }
} finally {
    [void][WinEv]::UnhookWinEvent($hook)
    $writer.Close()
    Write-Host ""
    Write-Host "Hook removed. Captured $($script:eventCount) events total. Log: $logFile" -ForegroundColor Cyan
}
