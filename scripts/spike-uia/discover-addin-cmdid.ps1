# Spike: discover the WM_COMMAND ID erwin assigned to "Elite Soft Erwin Addin".
#
# Background: erwin DM r10 uses XTPToolkit for its ribbon/menu UI. XTP does NOT
# expose ribbon items to UIA cleanly - direct UIA Invoke is filtered (see memory:
# reference_uia_xtptoolbar_filter, reference_alter_script_wizard_automation).
# But every menu item in a Win32 app has a numeric command ID, and the parent
# window receives WM_COMMAND(id) when the item is clicked. Sending PostMessage
# WM_COMMAND directly bypasses the UI layer entirely and IS the reliable path.
#
# This spike enumerates every window in erwin's process plus every classic menu
# attached to top-level windows, and reports anything that looks like our addin.
#
# Strategy:
#   A. EnumWindows -> filter by erwin's PID -> for each, GetMenu()
#      and walk the MenuItemInfo tree by ID + Type + Text.
#   B. EnumChildWindows recursively -> classify each by ClassName
#      (#32768=popup menu, ToolbarWindow32, Afx:*, XTPRibbonBar, etc.).
#   C. For each ToolbarWindow32 / XTPToolBar candidate, send TB_BUTTONCOUNT
#      then TB_GETBUTTON to enumerate buttons + idCommand.
#
# Output:
#   - c:\tmp\erwin-menus.txt : every menu item we can see (string + id + path)
#   - stdout: anything matching "Elite Soft" highlighted
#
# Read-only spike. Does NOT send WM_COMMAND yet. We only want the ID.

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

public static class W32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc enumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder buf, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetMenu(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr hMenu, int nPos);
    [DllImport("user32.dll")] public static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetMenuStringW(IntPtr hMenu, uint item, StringBuilder buf, int n, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, ref TBBUTTON lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct TBBUTTON {
        public int iBitmap;
        public int idCommand;
        public byte fsState;
        public byte fsStyle;
        public byte bReserved0;
        public byte bReserved1;
        public byte bReserved2;
        public byte bReserved3;
        public IntPtr dwData;
        public IntPtr iString;
    }

    public const uint MF_BYPOSITION = 0x400;
    public const uint TB_BUTTONCOUNT = 0x0418;
    public const uint TB_GETBUTTON   = 0x0417;
    public const uint TB_GETBUTTONTEXTW = 0x044B;
}
'@

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running" -ForegroundColor Red; exit 1 }
Write-Host "erwin PID=$($erwin.Id) Hwnd=0x$('{0:X}' -f $erwin.MainWindowHandle.ToInt64())" -ForegroundColor Cyan

$outFile = Join-Path ([System.IO.Path]::GetTempPath()) "erwin-menus-$(Get-Date -Format 'HHmmss').txt"
$sw = [System.IO.StreamWriter]::new($outFile, $false, [System.Text.Encoding]::UTF8)
$sw.AutoFlush = $true
$matches = New-Object System.Collections.ArrayList

function Match-AddinText {
    param([string]$text, [string]$where, [int]$id = 0)
    if ([string]::IsNullOrWhiteSpace($text)) { return $false }
    if ($text -match 'Elite\s*Soft|EliteSoft|Erwin\s*Addin|Erwin\s*Add\-In') {
        $line = "MATCH: '$text' @ $where" + ($(if ($id) { " id=$id (0x$('{0:X}' -f $id))" } else { '' }))
        Write-Host "  $line" -ForegroundColor Green
        [void]$script:matches.Add($line)
        $script:sw.WriteLine("# $line")
        return $true
    }
    return $false
}

# --- A. classic menu walk ---------------------------------------------------
function Walk-Menu {
    param([IntPtr]$hMenu, [string]$path = '')
    if ($hMenu -eq [IntPtr]::Zero) { return }
    $count = [W32]::GetMenuItemCount($hMenu)
    for ($i = 0; $i -lt $count; $i++) {
        $id = [W32]::GetMenuItemID($hMenu, $i)
        $sb = New-Object System.Text.StringBuilder 512
        [void][W32]::GetMenuStringW($hMenu, [uint32]$i, $sb, 512, [W32]::MF_BYPOSITION)
        $txt = $sb.ToString()
        $sub = [W32]::GetSubMenu($hMenu, $i)
        $where = "$path[$i]"
        $script:sw.WriteLine("MENU $where id=$id sub=$($sub -ne [IntPtr]::Zero) text='$txt'")
        [void](Match-AddinText -text $txt -where $where -id ([int]$id))
        if ($sub -ne [IntPtr]::Zero) {
            Walk-Menu -hMenu $sub -path "$where/"
        }
    }
}

# --- B. window enumeration --------------------------------------------------
$allWindows = New-Object System.Collections.ArrayList
$enumProc = [W32+EnumWindowsProc] {
    param([IntPtr]$h, [IntPtr]$lp)
    $procId = 0
    [void][W32]::GetWindowThreadProcessId($h, [ref]$procId)
    if ($procId -eq $erwin.Id) { [void]$allWindows.Add($h) }
    return $true
}
[void][W32]::EnumWindows($enumProc, [IntPtr]::Zero)
Write-Host "Found $($allWindows.Count) top-level windows in erwin process" -ForegroundColor Cyan

# Also recursively collect child windows of erwin's main window.
$childEnum = [W32+EnumWindowsProc] {
    param([IntPtr]$h, [IntPtr]$lp)
    [void]$allWindows.Add($h)
    [void][W32]::EnumChildWindows($h, $childEnum, [IntPtr]::Zero)  # recurse
    return $true
}
foreach ($top in @($allWindows.ToArray())) {
    [void][W32]::EnumChildWindows($top, $childEnum, [IntPtr]::Zero)
}
$allWindows = $allWindows | Select-Object -Unique
Write-Host "Total windows after child recursion: $($allWindows.Count)" -ForegroundColor Cyan

# Classify + walk menus + try toolbar enumeration.
foreach ($h in $allWindows) {
    $sbCls = New-Object System.Text.StringBuilder 256
    [void][W32]::GetClassName($h, $sbCls, 256)
    $cls = $sbCls.ToString()
    $sbT = New-Object System.Text.StringBuilder 256
    [void][W32]::GetWindowText($h, $sbT, 256)
    $title = $sbT.ToString()
    $vis = [W32]::IsWindowVisible($h)
    $sw.WriteLine("WIN  hwnd=0x$('{0:X}' -f $h.ToInt64()) cls='$cls' vis=$vis title='$title'")
    [void](Match-AddinText -text $title -where "WIN cls=$cls hwnd=0x$('{0:X}' -f $h.ToInt64())")

    # Toolbar-style enumeration. ToolbarWindow32 responds to TB_BUTTONCOUNT.
    if ($cls -match 'ToolbarWindow32|XTPToolBar|XTPMenuBar|XTPRibbon|XTPCommandBars') {
        $count = [W32]::SendMessageW($h, [W32]::TB_BUTTONCOUNT, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32()
        if ($count -gt 0 -and $count -lt 1000) {
            $sw.WriteLine("  TB cls='$cls' count=$count")
            for ($i = 0; $i -lt $count; $i++) {
                $btn = New-Object W32+TBBUTTON
                $r = [W32]::SendMessageW($h, [W32]::TB_GETBUTTON, [IntPtr]$i, [ref]$btn)
                if ($r.ToInt32() -ne 0) {
                    # Try to get the button text via id (TB_GETBUTTONTEXTW).
                    $sbBt = New-Object System.Text.StringBuilder 256
                    [void][W32]::SendMessageW($h, [W32]::TB_GETBUTTONTEXTW, [IntPtr]$btn.idCommand, [IntPtr]::Zero)
                    $sw.WriteLine("    btn[$i] id=$($btn.idCommand) (0x$('{0:X}' -f $btn.idCommand)) style=0x$('{0:X}' -f $btn.fsStyle) state=0x$('{0:X}' -f $btn.fsState)")
                    # We can't safely read the iString pointer cross-process,
                    # but the id alone often matches what we're after. Manual
                    # mapping via dump file inspection.
                }
            }
        }
    }

    # Walk the classic menu if any.
    $menu = [W32]::GetMenu($h)
    if ($menu -ne [IntPtr]::Zero) {
        $sw.WriteLine("  MENU attached to hwnd 0x$('{0:X}' -f $h.ToInt64())")
        Walk-Menu -hMenu $menu -path "0x$('{0:X}' -f $h.ToInt64())/"
    }
}

$sw.Close()
Write-Host ""
Write-Host "Dump written to $outFile" -ForegroundColor Cyan
Write-Host "Matches found: $($matches.Count)" -ForegroundColor $(if ($matches.Count -gt 0) { 'Green' } else { 'Yellow' })
if ($matches.Count -gt 0) {
    $matches | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }
    Write-Host ""
    Write-Host "Next: PostMessage(WM_COMMAND, <id>, 0) to the parent window of that menu/toolbar." -ForegroundColor Cyan
} else {
    Write-Host ""
    Write-Host "No direct text match. Likely the addin entry is rendered by XTPCommandBars" -ForegroundColor Yellow
    Write-Host "with NO classic menu and NO standard toolbar API exposure - strings live in" -ForegroundColor Yellow
    Write-Host "the XTP item's own private storage. Then we need an Accessible/MSAA walk" -ForegroundColor Yellow
    Write-Host "(IAccessible) or a Spy++ session against erwin." -ForegroundColor Yellow
}
