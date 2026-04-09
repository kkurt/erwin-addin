Add-Type @"
using System;
using System.Runtime.InteropServices;

public class Launcher {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static void KeyPress(ushort vk) {
        keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        keybd_event((byte)vk, 0, 0x0002, UIntPtr.Zero);
        System.Threading.Thread.Sleep(80);
    }
    public static void AltCombo(ushort vk) {
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        keybd_event((byte)vk, 0, 0x0002, UIntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        keybd_event(0x12, 0, 0x0002, UIntPtr.Zero);
    }
}
"@

$erwinExe = "C:\Program Files\erwin\Data Modeler r10\erwin.exe"
$mySession = (Get-Process -Id $PID).SessionId

# Step 1: Start erwin
Write-Host "Starting erwin..." -ForegroundColor Cyan
Start-Process $erwinExe
Write-Host "Waiting for erwin to open..."

# Step 2: Wait for erwin window with a model (title contains "[")
$erwinProc = $null
$hWnd = [IntPtr]::Zero
for ($i = 0; $i -lt 120; $i++) {  # 2 min timeout
    $erwinProc = Get-Process -Name erwin -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $mySession -and $_.MainWindowTitle -match "erwin.*\[.+\]" } | Select-Object -First 1
    if ($erwinProc) {
        $hWnd = $erwinProc.MainWindowHandle
        Write-Host "Model detected: '$($erwinProc.MainWindowTitle)'" -ForegroundColor Green
        break
    }
    # Also show progress for erwin without model
    $anyErwin = Get-Process -Name erwin -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $mySession } | Select-Object -First 1
    if ($anyErwin -and $i % 10 -eq 0) {
        Write-Host "  erwin running, waiting for model..." -ForegroundColor Gray
    }
    Start-Sleep -Seconds 1
}

if (-not $erwinProc) {
    Write-Host "Timeout waiting for erwin model." -ForegroundColor Red
    Start-Sleep -Seconds 3
    exit
}

# Step 3: Small delay for model to fully load
Write-Host "Model loaded. Activating add-in in 3 seconds..." -ForegroundColor Yellow
Start-Sleep -Seconds 3

# Step 4: Set erwin as foreground (we have foreground rights - user launched us)
[Launcher]::SetForegroundWindow($hWnd) | Out-Null
Start-Sleep -Milliseconds 500

# Verify
$fg = [Launcher]::GetForegroundWindow()
if ($fg -ne $hWnd) {
    Write-Host "Could not set erwin foreground. Retrying..." -ForegroundColor Yellow
    Start-Sleep -Milliseconds 500
    [Launcher]::SetForegroundWindow($hWnd) | Out-Null
    Start-Sleep -Milliseconds 500
}

# Step 5: Send Alt+T, I, Down, Down, Enter via SendInput
Write-Host "Sending keyboard commands to erwin..." -ForegroundColor Cyan

[Launcher]::AltCombo(0x54)  # Alt+T = Tools tab
Start-Sleep -Milliseconds 1200

[Launcher]::KeyPress(0x49)  # I = Add-Ins dropdown
Start-Sleep -Milliseconds 1500

# Try: just press E for "Elite" (first letter navigation in XTP dropdown)
[Launcher]::KeyPress(0x45)  # E = Elite Soft Erwin Addin
Start-Sleep -Milliseconds 500

[Launcher]::KeyPress(0x0D)  # Enter
Start-Sleep -Milliseconds 500

Write-Host "Add-in activated!" -ForegroundColor Green
Start-Sleep -Seconds 2
