# Spike: while user opens the Add-Ins ribbon popup, poll for XTPPopupBar
# windows in erwin. For each, try GetMenu() + GetMenuItemID() to see if
# the popup is backed by a real Win32 HMENU. If yes, item IDs ARE the
# WM_COMMAND IDs - including our addin's.
#
# Pure Win32 message-less calls. Zero risk.

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
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetMenu(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr hMenu, int nPos);
    [DllImport("user32.dll")] public static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetMenuStringW(IntPtr hMenu, uint item, StringBuilder buf, int n, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    public const uint MF_BYPOSITION = 0x400;
}
'@

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running" -ForegroundColor Red; exit 1 }
$erwinPid = $erwin.Id

function Get-Popups {
    $found = New-Object System.Collections.ArrayList
    $proc = [W+EnumWindowsProc] {
        param([IntPtr]$h, [IntPtr]$lp)
        $procId = 0
        [void][W]::GetWindowThreadProcessId($h, [ref]$procId)
        if ($procId -eq $erwinPid -and [W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 64
            [void][W]::GetClassName($h, $sb, 64)
            $cls = $sb.ToString()
            if ($cls -match 'XTPPopupBar|#32768') {
                [void]$found.Add(@{ Hwnd = $h; Class = $cls })
            }
            [void][W]::EnumChildWindows($h, $proc, [IntPtr]::Zero)
        }
        return $true
    }
    [void][W]::EnumWindows($proc, [IntPtr]::Zero)
    return $found
}

Write-Host "Polling for popup windows for 30s..." -ForegroundColor Cyan
Write-Host "Open the Add-Ins ribbon popup in erwin. Leave it open for at least 2 seconds." -ForegroundColor Cyan
Write-Host ""

$seenHwnds = New-Object System.Collections.Generic.HashSet[long]
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    $popups = Get-Popups
    foreach ($p in $popups) {
        $hLong = $p.Hwnd.ToInt64()
        if ($seenHwnds.Contains($hLong)) { continue }
        [void]$seenHwnds.Add($hLong)

        Write-Host "POPUP: hwnd=0x$('{0:X}' -f $hLong) cls=$($p.Class)" -ForegroundColor Yellow

        # Try GetMenu - probably 0 for XTPPopupBar but worth checking
        $hMenu = [W]::GetMenu($p.Hwnd)
        Write-Host "  GetMenu() = 0x$('{0:X}' -f $hMenu.ToInt64())" -ForegroundColor Gray
        if ($hMenu -ne [IntPtr]::Zero) {
            $count = [W]::GetMenuItemCount($hMenu)
            Write-Host "  GetMenuItemCount = $count" -ForegroundColor Green
            for ($i = 0; $i -lt $count; $i++) {
                $id = [W]::GetMenuItemID($hMenu, $i)
                $sb = New-Object System.Text.StringBuilder 256
                [void][W]::GetMenuStringW($hMenu, [uint32]$i, $sb, 256, [W]::MF_BYPOSITION)
                Write-Host "    [$i] id=$id (0x$('{0:X}' -f $id)) text='$($sb.ToString())'" -ForegroundColor Green
            }
        }

        # Try GetParent/GetWindow to find the owner that might have the menu
        $parent = [W]::GetParent($p.Hwnd)
        $owner  = [W]::GetWindow($p.Hwnd, 4)  # GW_OWNER
        Write-Host "  GetParent=0x$('{0:X}' -f $parent.ToInt64()) GW_OWNER=0x$('{0:X}' -f $owner.ToInt64())" -ForegroundColor Gray

        # Some popup classes have HMENU stored in the window itself. Try
        # iterating child windows - menu item buttons might be enumerable.
        $childCount = 0
        $childEnum = [W+EnumWindowsProc] {
            param([IntPtr]$h, [IntPtr]$lp)
            $script:childCount++
            $sb = New-Object System.Text.StringBuilder 64
            [void][W]::GetClassName($h, $sb, 64)
            Write-Host "    child hwnd=0x$('{0:X}' -f $h.ToInt64()) cls=$($sb.ToString())" -ForegroundColor Gray
            return $true
        }
        [void][W]::EnumChildWindows($p.Hwnd, $childEnum, [IntPtr]::Zero)
        Write-Host "  child window count=$childCount" -ForegroundColor Gray
        Write-Host ""
    }
    Start-Sleep -Milliseconds 200
}

Write-Host "Done. Seen $($seenHwnds.Count) unique popup-class windows." -ForegroundColor Cyan
