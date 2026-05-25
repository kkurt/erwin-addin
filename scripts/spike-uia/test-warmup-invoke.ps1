# TEST B: warmup with 1179 (Manage Add-Ins) then 1181 (addin invoke).
# Hypothesis: erwin's command map for addin entries is built lazily on
# first menu interaction. Sending 1179 might force the build by activating
# the Add-Ins infrastructure, then 1181 hits a now-active handler.
$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class W {
    public delegate bool EWP(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EWP fn, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
'@

$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running" -ForegroundColor Red; exit 1 }
$hwnd = $erwin.MainWindowHandle
$erwinPid = $erwin.Id
Write-Host "erwin PID=$erwinPid HWND=0x$('{0:X}' -f $hwnd.ToInt64()) Title='$($erwin.MainWindowTitle)'" -ForegroundColor Cyan

$wmcmdLog = Join-Path $env:LOCALAPPDATA 'EliteSoft\ErwinAddIn\wmcmd.log'
$sizeBefore = (Get-Item $wmcmdLog -ErrorAction SilentlyContinue).Length
Write-Host "wmcmd.log size before: $sizeBefore"

Write-Host "`n--- B1: PostMessage 1179 (Manage Add-Ins) ---" -ForegroundColor Yellow
[void][W]::PostMessageW($hwnd, 0x0111, [IntPtr]1179, [IntPtr]::Zero)
Start-Sleep -Milliseconds 1500

Write-Host "`n--- B2: find + cancel Add-In Manager dialog ---" -ForegroundColor Yellow
$mgrHwnd = [IntPtr]::Zero
$proc = [W+EWP] {
    param([IntPtr]$h, [IntPtr]$lp)
    $procId = 0
    [void][W]::GetWindowThreadProcessId($h, [ref]$procId)
    if ($procId -eq $erwinPid -and [W]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 64
        [void][W]::GetClassName($h, $sb, 64)
        if ($sb.ToString() -eq '#32770') {
            $sbT = New-Object System.Text.StringBuilder 128
            [void][W]::GetWindowText($h, $sbT, 128)
            if ($sbT.ToString() -match 'Add-In Manager') {
                $script:mgrHwnd = $h
                return $false
            }
        }
    }
    return $true
}
[void][W]::EnumWindows($proc, [IntPtr]::Zero)
Write-Host "Manager dialog: 0x$('{0:X}' -f $mgrHwnd.ToInt64())"
if ($mgrHwnd -ne [IntPtr]::Zero) {
    [void][W]::PostMessageW($mgrHwnd, 0x0111, [IntPtr]2, [IntPtr]::Zero)   # IDCANCEL
    Start-Sleep -Milliseconds 500
    Write-Host "Dialog IDCANCELed"
}

Write-Host "`n--- B3: PostMessage 1181 (addin invoke) ---" -ForegroundColor Yellow
[void][W]::PostMessageW($hwnd, 0x0111, [IntPtr]1181, [IntPtr]::Zero)
Start-Sleep -Seconds 4

$sizeAfter = (Get-Item $wmcmdLog).Length
Write-Host "`nwmcmd.log size after: $sizeAfter (delta=$($sizeAfter - $sizeBefore))"
if ($sizeAfter -gt $sizeBefore) {
    Write-Host "`nRESULT: ✅ Warmup-then-invoke WORKED" -ForegroundColor Green
    Get-Content $wmcmdLog -Tail 6
} else {
    Write-Host "`nRESULT: ❌ Still failed after warmup" -ForegroundColor Red
}
