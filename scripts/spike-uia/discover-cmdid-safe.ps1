# Spike: discover the WM_COMMAND id of erwin's ribbon "Add-Ins" button via
# cross-process TB_GETBUTTON + TB_GETBUTTONTEXTW, using a buffer allocated
# in erwin's address space with VirtualAllocEx PAGE_READWRITE.
#
# Why this is AV-safe (vs. the SONAR.ProcHijack-flagged injector):
#   - PROCESS access: ONLY VM_OPERATION|VM_READ|VM_WRITE|QUERY_INFORMATION.
#     The flagged injector used PROCESS_ALL_ACCESS (includes thread + token).
#   - VirtualAllocEx: ONLY PAGE_READWRITE. The SONAR signature flags
#     PAGE_EXECUTE_* allocations. Data buffers do not match.
#   - NO CreateRemoteThread. NO LoadLibrary. NO code injection.
#   - This is the exact protocol Spy++, Inspect.exe, Process Explorer use.
#
# Cleanup is guarded by try/finally - remote buffers are always freed even
# on script error, process handle always closed.

$ErrorActionPreference = 'Stop'

Add-Type -Language CSharp @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class W32 {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumWindowsProc fn, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr baseAddress, IntPtr buffer, IntPtr nSize, out IntPtr bytesRead);

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

    // Minimal process access - explicitly NOT including PROCESS_CREATE_THREAD
    public const uint PROCESS_VM_OPERATION      = 0x0008;
    public const uint PROCESS_VM_READ           = 0x0010;
    public const uint PROCESS_VM_WRITE          = 0x0020;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;

    // VirtualAllocEx flags. NOTE: PAGE_EXECUTE_* deliberately NOT included
    // in this file. The SONAR.ProcHijack signature watches for executable
    // memory allocation; PAGE_READWRITE is data-only and not flagged.
    public const uint MEM_COMMIT     = 0x1000;
    public const uint MEM_RESERVE    = 0x2000;
    public const uint MEM_RELEASE    = 0x8000;
    public const uint PAGE_READWRITE = 0x04;

    // Standard toolbar control messages.
    public const uint TB_BUTTONCOUNT    = 0x0418;
    public const uint TB_GETBUTTON      = 0x0417;
    public const uint TB_GETBUTTONTEXTW = 0x044B;
}
'@

# --------- locate erwin + The Ribbon ---------
$mySession = (Get-Process -Id $PID).SessionId
$erwin = Get-Process erwin -ErrorAction SilentlyContinue |
         Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
if (-not $erwin) { Write-Host "erwin not running" -ForegroundColor Red; exit 1 }
$erwinPid = $erwin.Id
Write-Host "erwin PID=$erwinPid" -ForegroundColor Cyan

$ribbonHwnd = [IntPtr]::Zero
$collector = [W32+EnumWindowsProc] {
    param([IntPtr]$h, [IntPtr]$lp)
    if ($script:ribbonHwnd -ne [IntPtr]::Zero) { return $false }
    $procId = 0
    [void][W32]::GetWindowThreadProcessId($h, [ref]$procId)
    if ($procId -ne $erwinPid) { return $true }
    if (-not [W32]::IsWindowVisible($h)) { return $true }
    $sbC = New-Object System.Text.StringBuilder 256
    [void][W32]::GetClassName($h, $sbC, 256)
    if ($sbC.ToString() -eq 'XTPToolBar') {
        $sbT = New-Object System.Text.StringBuilder 256
        [void][W32]::GetWindowText($h, $sbT, 256)
        if ($sbT.ToString() -eq 'The Ribbon') {
            $script:ribbonHwnd = $h
            return $false
        }
    }
    [void][W32]::EnumChildWindows($h, $collector, [IntPtr]::Zero)
    return $true
}
[void][W32]::EnumWindows($collector, [IntPtr]::Zero)
if ($ribbonHwnd -eq [IntPtr]::Zero) { Write-Host "'The Ribbon' XTPToolBar not found" -ForegroundColor Red; exit 2 }
Write-Host "Ribbon hwnd=0x$('{0:X}' -f $ribbonHwnd.ToInt64())" -ForegroundColor Green

# --------- TB_BUTTONCOUNT - scalar message, no buffer needed ---------
$count = [W32]::SendMessageW($ribbonHwnd, [W32]::TB_BUTTONCOUNT, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32()
Write-Host "TB_BUTTONCOUNT = $count" -ForegroundColor Cyan
if ($count -le 0 -or $count -gt 5000) {
    Write-Host "Suspicious count, abort." -ForegroundColor Yellow
    Write-Host "(MSAA reported 273 children - if this is much smaller, XTP overrides TB_*; we need different approach.)" -ForegroundColor Yellow
    exit 3
}

# --------- open erwin with MINIMAL access ---------
$access = [W32]::PROCESS_VM_OPERATION -bor [W32]::PROCESS_VM_READ -bor [W32]::PROCESS_VM_WRITE -bor [W32]::PROCESS_QUERY_INFORMATION
$hProc = [W32]::OpenProcess($access, $false, $erwinPid)
if ($hProc -eq [IntPtr]::Zero) {
    $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Write-Host "OpenProcess failed err=$err" -ForegroundColor Red
    exit 4
}
Write-Host "OpenProcess OK (PROCESS_VM_* only; no thread/all_access)" -ForegroundColor Green

$structSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][W32+TBBUTTON])
$textBufSize = 1024
$remoteStruct = [IntPtr]::Zero
$remoteText   = [IntPtr]::Zero

try {
    # --------- allocate ONE TBBUTTON-sized buffer in erwin's address space ---------
    $remoteStruct = [W32]::VirtualAllocEx($hProc, [IntPtr]::Zero, [IntPtr]$structSize,
        ([W32]::MEM_COMMIT -bor [W32]::MEM_RESERVE), [W32]::PAGE_READWRITE)
    if ($remoteStruct -eq [IntPtr]::Zero) {
        $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        Write-Host "VirtualAllocEx(struct) failed err=$err" -ForegroundColor Red
        exit 5
    }
    Write-Host "Allocated remote TBBUTTON buf @ 0x$('{0:X}' -f $remoteStruct.ToInt64()) (PAGE_READWRITE)" -ForegroundColor Green

    # --------- enumerate buttons ---------
    $rows = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $count; $i++) {
        $r = [W32]::SendMessageW($ribbonHwnd, [W32]::TB_GETBUTTON, [IntPtr]$i, $remoteStruct)
        if ($r.ToInt32() -eq 0) { continue }

        $localBuf = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($structSize)
        $br = [IntPtr]::Zero
        try {
            $ok = [W32]::ReadProcessMemory($hProc, $remoteStruct, $localBuf, [IntPtr]$structSize, [ref]$br)
            if (-not $ok) { continue }
            $btn = [System.Runtime.InteropServices.Marshal]::PtrToStructure($localBuf, [type][W32+TBBUTTON])
            [void]$rows.Add([pscustomobject]@{
                Idx    = $i
                IdCmd  = $btn.idCommand
                Style  = '0x{0:X}' -f $btn.fsStyle
                State  = '0x{0:X}' -f $btn.fsState
                IStr   = $btn.iString.ToInt64()
            })
        } finally {
            [System.Runtime.InteropServices.Marshal]::FreeHGlobal($localBuf)
        }
    }
    Write-Host ""
    Write-Host "Read $($rows.Count) buttons via cross-process TB_GETBUTTON" -ForegroundColor Cyan

    # --------- now resolve texts via TB_GETBUTTONTEXTW ---------
    $remoteText = [W32]::VirtualAllocEx($hProc, [IntPtr]::Zero, [IntPtr]$textBufSize,
        ([W32]::MEM_COMMIT -bor [W32]::MEM_RESERVE), [W32]::PAGE_READWRITE)
    if ($remoteText -eq [IntPtr]::Zero) {
        Write-Host "VirtualAllocEx(text) failed; only id data available" -ForegroundColor Yellow
    } else {
        Write-Host "Allocated remote text buf @ 0x$('{0:X}' -f $remoteText.ToInt64()) ($textBufSize bytes)" -ForegroundColor Green
        $named = New-Object System.Collections.ArrayList
        $matches = New-Object System.Collections.ArrayList
        foreach ($r in $rows) {
            if ($r.IdCmd -le 0) { continue }
            $len = [W32]::SendMessageW($ribbonHwnd, [W32]::TB_GETBUTTONTEXTW, [IntPtr]$r.IdCmd, $remoteText).ToInt32()
            if ($len -le 0 -or $len -gt 500) { continue }
            $byteSize = ($len + 1) * 2  # wide chars + null
            $local = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($byteSize)
            $br = [IntPtr]::Zero
            try {
                $ok = [W32]::ReadProcessMemory($hProc, $remoteText, $local, [IntPtr]$byteSize, [ref]$br)
                if (-not $ok) { continue }
                $txt = [System.Runtime.InteropServices.Marshal]::PtrToStringUni($local, $len)
                if ([string]::IsNullOrWhiteSpace($txt)) { continue }
                $row = [pscustomobject]@{ Idx = $r.Idx; IdCmd = $r.IdCmd; IdCmdHex = ('0x{0:X}' -f $r.IdCmd); Text = $txt }
                [void]$named.Add($row)
                if ($txt -match 'Add\-?In|Elite|Manage') {
                    [void]$matches.Add($row)
                }
            } finally {
                [System.Runtime.InteropServices.Marshal]::FreeHGlobal($local)
            }
        }

        Write-Host "Got text for $($named.Count) buttons; $($matches.Count) match addin-ish keywords." -ForegroundColor Cyan
        Write-Host ""
        if ($matches.Count -gt 0) {
            Write-Host "=== KEYWORD MATCHES ===" -ForegroundColor Green
            $matches | Format-Table -AutoSize | Out-String | Write-Host
        }
        Write-Host "=== ALL NAMED BUTTONS (first 100) ===" -ForegroundColor Cyan
        $named | Select-Object -First 100 | Format-Table -AutoSize | Out-String | Write-Host
        if ($named.Count -gt 100) {
            Write-Host "...$($named.Count - 100) more (omitted)" -ForegroundColor Gray
        }
    }

} finally {
    if ($remoteText   -ne [IntPtr]::Zero) { [void][W32]::VirtualFreeEx($hProc, $remoteText,   [IntPtr]::Zero, [W32]::MEM_RELEASE) }
    if ($remoteStruct -ne [IntPtr]::Zero) { [void][W32]::VirtualFreeEx($hProc, $remoteStruct, [IntPtr]::Zero, [W32]::MEM_RELEASE) }
    [void][W32]::CloseHandle($hProc)
    Write-Host ""
    Write-Host "Cleanup: remote buffers freed, process handle closed." -ForegroundColor Gray
}
