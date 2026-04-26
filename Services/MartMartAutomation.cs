using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Programmatically drives erwin's Complete Compare wizard to produce a
    /// Mart-to-Mart alter DDL with zero user interaction. All CC / Resolve
    /// Differences dialogs are hidden via WS_EX_LAYERED + alpha=0. Apply-to-Right
    /// arrow is clicked via UI Automation Invoke pattern (DPI/resolution-proof,
    /// no pixel math). Final DDL capture reuses the proven OnFE orchestrator.
    ///
    /// Flow:
    ///   1. WM_COMMAND 1082 to erwin main frame -> CC wizard opens.
    ///   2. Navigate to Right Model page (Back-to-Overview + Next*2).
    ///   3. From Mart radio (WM_COMMAND 1081) + Load (WM_COMMAND 1082).
    ///   4. Mart picker dialog: UIA-select target version, press Open.
    ///   5. WM_COMMAND 12325 (CC_COMPARE) -> Compare.
    ///   6. Wait for Resolve Differences. UIA-find "Copy to Right" arrow.
    ///   7. UIA Invoke arrow on Model row. EDR hook captures v1 ms.
    ///   8. WM_COMMAND 1056 (RD_ALTER_SCRIPT) -> Alter wizard opens.
    ///   9. Existing OnFE orchestrator captures DDL, closes everything.
    /// </summary>
    internal static class MartMartAutomation
    {
        // Win32 / window enumeration
        [DllImport("user32.dll")]
        private static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref RECT lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEM
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
            public int iIndent;
            public int iGroupId;
            public uint cColumns;
            public IntPtr puColumns;
            public IntPtr piColFmt;
            public int iGroup;
        }

        private const uint LVM_FIRST = 0x1000;
        private const uint LVM_GETSUBITEMRECT = LVM_FIRST + 56;   // 0x1038
        private const uint LVM_SETITEMSTATE   = LVM_FIRST + 43;   // 0x102B
        private const uint LVM_GETITEMCOUNT   = LVM_FIRST + 4;    // 0x1004
        private const int LVIR_BOUNDS = 0;
        private const uint LVIF_STATE = 0x0008;
        private const uint LVIS_FOCUSED = 0x0001;
        private const uint LVIS_SELECTED = 0x0002;

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageLVItem(IntPtr hWnd, uint Msg, IntPtr wParam, ref LVITEM lParam);

        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private const uint MOUSEEVENTF_MOVE     = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP   = 0x0004;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        // SendInput (modern API, more reliable under RDP/UIPI than mouse_event).
        // MOUSEINPUT on x64: dx(4)+dy(4)+mouseData(4)+dwFlags(4)+time(4)+4pad
        // + dwExtraInfo(8) = 32 bytes.
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // INPUT on x64: type(4) + 4pad to align union + MOUSEINPUT(32) = 40 bytes.
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct INPUT
        {
            [FieldOffset(0)] public uint type;           // 0 = INPUT_MOUSE
            [FieldOffset(8)] public MOUSEINPUT mi;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);


        /// <summary>
        /// Atomic MOVE+DOWN+UP sequence via single SendInput call using
        /// absolute coordinates (MOUSEEVENTF_ABSOLUTE). The OS delivers the
        /// sequence atomically - no external process (including RDP cursor
        /// sync) can interrupt between steps. Coordinates are normalised
        /// to 0..65535 as required by MOUSEEVENTF_ABSOLUTE.
        /// </summary>
        private static void SendMouseClickAt(int screenX, int screenY)
        {
            int cx = GetSystemMetrics(SM_CXSCREEN);
            int cy = GetSystemMetrics(SM_CYSCREEN);
            if (cx <= 0) cx = 1920;
            if (cy <= 0) cy = 1080;
            int absX = (int)((screenX * 65535L + cx / 2) / cx);
            int absY = (int)((screenY * 65535L + cy / 2) / cy);

            var inputs = new INPUT[3];
            inputs[0].type = 0;
            inputs[0].mi.dx = absX;
            inputs[0].mi.dy = absY;
            inputs[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
            inputs[1].type = 0;
            inputs[1].mi.dx = absX;
            inputs[1].mi.dy = absY;
            inputs[1].mi.dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE;
            inputs[2].type = 0;
            inputs[2].mi.dx = absX;
            inputs[2].mi.dy = absY;
            inputs[2].mi.dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE;

            uint sent = SendInput(3, inputs, Marshal.SizeOf<INPUT>());
            if (sent != 3)
            {
                // Legacy fallback.
                SetCursorPos(screenX, screenY);
                Thread.Sleep(30);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(40);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            }
        }

        // Messages + command IDs (from old WizardAutomationService reverse-engineering)
        private const uint WM_COMMAND = 0x0111;
        private const int CMD_COMPLETE_COMPARE = 1082;   // main frame menu: open CC wizard
        private const int CC_BACK   = 12323;             // CC wizard Back
        private const int CC_NEXT   = 12324;             // CC wizard Next
        private const int CC_COMPARE = 12325;            // CC wizard Compare
        private const int CC_CLOSE  = 12327;             // CC wizard Close
        private const int CMD_FROM_MART_RADIO = 1081;    // Right Model page: From Mart radio
        private const int CMD_LOAD_BUTTON     = 1082;    // Right Model page: Load button
        private const int CMD_RD_ALTER_SCRIPT = 1056;    // Resolve Differences toolbar: Alter Script
        private const int IDCANCEL = 2;
        private const int IDOK     = 1;

        private static IntPtr MakeWParam(int low, int high) =>
            new IntPtr(((high & 0xFFFF) << 16) | (low & 0xFFFF));

        /// <summary>
        /// Holds the CC-wizard + Resolve-Differences window handles produced
        /// by a successful DriveCCAndApply run. Caller is responsible for
        /// passing the session back to <see cref="CloseSession"/> for cleanup.
        /// </summary>
        internal sealed class CCSession
        {
            public IntPtr CCWizard;
            public IntPtr ResolveDifferences;
            public bool Applied;
        }

        /// <summary>
        /// Debug entry: drive the CC wizard end-to-end and then close
        /// everything (for the Debug Log "Mart-Mart Auto Discovery" button).
        /// Also dumps the Mart picker UIA tree once so we retain the
        /// discovery artifact in logs.
        /// </summary>
        public static async Task<string> RunDiscoveryAsync(int martVersion, string catalogPath, Action<string> log)
        {
            log?.Invoke("");
            log?.Invoke("=== Mart-Mart automation: DISCOVERY phase ===");
            log?.Invoke($"Target: Mart v{martVersion} at '{catalogPath}'");

            return await Task.Run(() =>
            {
                CCSession s = DriveCCAndApply(martVersion, catalogPath, dumpPickerTree: true, log);
                CloseSession(s, log);
                return (string)null;
            });
        }

        /// <summary>
        /// Diagnostic entry: drive CC to Compare, leave Resolve Differences
        /// VISIBLE + enable EDR stack trace so the bridge logs the call chain
        /// when the user manually clicks the Apply-to-Right arrow. Waits up to
        /// 45 seconds for user interaction, then disables stack trace and
        /// closes dialogs. The captured stack (in erwin-native-bridge.log)
        /// identifies which internal function the arrow actually invokes -
        /// the input for replacing mouse synthesis with a direct native call.
        /// </summary>
        public static async Task CaptureApplyStackAsync(int martVersion, string catalogPath, Action<string> log)
        {
            await Task.Run(() =>
            {
                // Step 1: enable EDR stack trace + install the CMP-Apply hook
                // Install BOTH hooks (CMP+0x13920 listview click dispatcher,
                // and CMP+0x1EA11 MFC message map entry which is the highest
                // erwin frame from mfc140 dispatcher). MsgMap is the one we
                // prefer for replay because its args are MFC-standard.
                NativeBridgeService.SetEdrStackTrace(true);
                log?.Invoke("  SetEdrStackTrace(true)");
                int cmpRc = NativeBridgeService.HookCmpApply();
                log?.Invoke($"  HookCmpApply() -> {cmpRc}");
                int mmRc = NativeBridgeService.HookMsgMap();
                log?.Invoke($"  HookMsgMap() -> {mmRc}");

                CCSession s = null;
                try
                {
                    // Step 2: drive CC UI up to Resolve Differences. We pass
                    // dumpPickerTree=false and - crucially - DO NOT hide RD
                    // at the end. Caller wants to interact with RD manually.
                    s = DriveCCToResolveDifferencesVisible(martVersion, catalogPath, log);
                    if (s == null || s.ResolveDifferences == IntPtr.Zero)
                    {
                        log?.Invoke("  RD did not open - aborting diag.");
                        return;
                    }

                    // Step 3: wait for user. Poll the hook counter and
                    // early-exit 1s after the first hit (gives the Apply
                    // chain a moment to finish before we tear down RD).
                    // Hard cap at 20s in case user doesn't click.
                    log?.Invoke("");
                    log?.Invoke("  RD is VISIBLE. Click the right-pointing arrow on Model row NOW.");
                    const int hardCapMs = 20000;
                    const int pollIntervalMs = 200;
                    int waited = 0;
                    int postHitWait = 0;
                    bool hitSeen = false;
                    while (waited < hardCapMs)
                    {
                        Thread.Sleep(pollIntervalMs);
                        waited += pollIntervalMs;
                        bool latched = NativeBridgeService.GetMsgMapArgs(out _, out _, out _, out _)
                                    || NativeBridgeService.GetCmpApplyArgs(out _, out _, out _, out _);
                        if (latched && !hitSeen)
                        {
                            hitSeen = true;
                            log?.Invoke("  click detected - giving Apply 1s to settle");
                        }
                        if (hitSeen)
                        {
                            postHitWait += pollIntervalMs;
                            if (postHitWait >= 1000) break;
                        }
                        else if (waited % 5000 == 0)
                        {
                            log?.Invoke($"    {(hardCapMs - waited) / 1000}s remaining (no click yet)...");
                        }
                    }
                    if (!hitSeen) log?.Invoke("  timed out waiting for click.");

                    // Step 4: dump the latched args from BOTH hooks so we can
                    // see which level is the best replay target.
                    log?.Invoke("");
                    if (NativeBridgeService.GetMsgMapArgs(out IntPtr m1, out IntPtr m2, out IntPtr m3, out IntPtr m4))
                    {
                        log?.Invoke("  MsgMap (EM_CMP+0x1EA11) args LATCHED:");
                        log?.Invoke($"    a1 (RCX) = 0x{m1.ToInt64():X16}");
                        log?.Invoke($"    a2 (RDX) = 0x{m2.ToInt64():X16}");
                        log?.Invoke($"    a3 (R8)  = 0x{m3.ToInt64():X16}");
                        log?.Invoke($"    a4 (R9)  = 0x{m4.ToInt64():X16}");
                    }
                    else log?.Invoke("  MsgMap hook did NOT fire.");

                    if (NativeBridgeService.GetCmpApplyArgs(out IntPtr c1, out IntPtr c2, out IntPtr c3, out IntPtr c4))
                    {
                        log?.Invoke("  CMP (EM_CMP+0x13920) args LATCHED:");
                        log?.Invoke($"    a1 (RCX) = 0x{c1.ToInt64():X16}");
                        log?.Invoke($"    a2 (RDX) = 0x{c2.ToInt64():X16}");
                        log?.Invoke($"    a3 (R8)  = 0x{c3.ToInt64():X16}");
                        log?.Invoke($"    a4 (R9)  = 0x{c4.ToInt64():X16}");
                    }
                    else log?.Invoke("  CMP hook did NOT fire.");
                }
                finally
                {
                    NativeBridgeService.SetEdrStackTrace(false);
                    log?.Invoke("  SetEdrStackTrace(false)");
                    CloseSession(s, log);
                }
            });
        }

        /// <summary>
        /// Variant of <see cref="DriveCCAndApply"/> that stops after RD opens
        /// and does NOT hide RD (user needs to see + click it). Does not
        /// perform any Apply-to-Right action itself.
        /// </summary>
        private static CCSession DriveCCToResolveDifferencesVisible(int martVersion, string catalogPath, Action<string> log)
        {
            var session = new CCSession();
            try
            {
                IntPtr erwinMain = FindErwinMain();
                if (erwinMain == IntPtr.Zero) { log?.Invoke("  erwin main window not found."); return null; }

                var dialogsBefore = EnumerateVisibleDialogs();
                log?.Invoke("  [1] posting CMD_COMPLETE_COMPARE (1082)");
                PostMessage(erwinMain, WM_COMMAND, MakeWParam(CMD_COMPLETE_COMPARE, 0), IntPtr.Zero);
                IntPtr ccWizard = WaitForNewDialog(dialogsBefore, "CC wizard", 5000, log);
                if (ccWizard == IntPtr.Zero) return null;
                HideWindow(ccWizard);
                session.CCWizard = ccWizard;

                for (int i = 0; i < 12; i++)
                {
                    string t = GetTitle(ccWizard);
                    if (t.StartsWith("Wizard Overview", StringComparison.OrdinalIgnoreCase)
                        || t.StartsWith("Overview", StringComparison.OrdinalIgnoreCase)) break;
                    PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_BACK, 0), IntPtr.Zero);
                    Thread.Sleep(100);
                }
                log?.Invoke("  [2] Overview + Next x2");
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero); Thread.Sleep(200);
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero); Thread.Sleep(400);

                log?.Invoke("  [3] From Mart + Load");
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CMD_FROM_MART_RADIO, 0), IntPtr.Zero); Thread.Sleep(200);
                var dialogsBeforeLoad = EnumerateVisibleDialogs();
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CMD_LOAD_BUTTON, 0), IntPtr.Zero);

                IntPtr martDlg = WaitForNewDialog(dialogsBeforeLoad, "Mart picker", 5000, log);
                if (martDlg != IntPtr.Zero)
                {
                    HideWindow(martDlg);
                    if (!SelectMartVersionInPicker(martDlg, martVersion, catalogPath, log))
                    {
                        log?.Invoke("  [WARN] Mart picker select failed");
                        PostMessage(martDlg, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
                        return null;
                    }
                }

                Thread.Sleep(500);
                log?.Invoke("  [5] Compare");
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_COMPARE, 0), IntPtr.Zero);
                Thread.Sleep(800);

                IntPtr popup = WaitForDialog("erwin Data Modeler", 1500);
                if (popup != IntPtr.Zero && popup != ccWizard)
                {
                    try
                    {
                        var pop = AutomationElement.FromHandle(popup);
                        if (pop != null) ClickButtonByName(pop, new[] { "No", "Hayır", "Cancel", "İptal" });
                    } catch { }
                    log?.Invoke("  compare-to-itself popup dismissed; aborting");
                    return null;
                }

                IntPtr resolveDlg = WaitForDialog("Resolve Differences", 10000);
                if (resolveDlg == IntPtr.Zero) { log?.Invoke("  RD did not open"); return null; }
                log?.Invoke($"  RD = 0x{resolveDlg.ToInt64():X} (kept VISIBLE for diag)");
                session.ResolveDifferences = resolveDlg;
                // Intentionally NOT hiding RD.
                return session;
            }
            catch (Exception ex)
            {
                log?.Invoke($"  diag flow threw: {ex.GetType().Name}: {ex.Message}");
                return session.CCWizard != IntPtr.Zero ? session : null;
            }
        }

        /// <summary>
        /// Production entry: drive the CC wizard programmatically up to and
        /// including Apply-to-Right on the Model row. On success returns the
        /// <see cref="CCSession"/> with CC wizard + Resolve Differences window
        /// handles still alive. Caller MUST pass the returned session to
        /// <see cref="CloseSession"/> after consuming EDR state.
        /// <paramref name="overlayToggle"/> is invoked with <c>false</c>
        /// immediately before the synthesized mouse click and with
        /// <c>true</c> immediately after - lets the UI hide/restore a
        /// "please wait" overlay so it doesn't cover the RD dialog that
        /// needs to receive the click.
        /// </summary>
        public static async Task<CCSession> DriveCCAndApplyAsync(int martVersion, string catalogPath,
            Action<string> log, Action<bool> overlayToggle = null)
        {
            return await Task.Run(() => DriveCCAndApply(martVersion, catalogPath,
                dumpPickerTree: false, log, overlayToggle));
        }

        /// <summary>
        /// Closes the CC wizard + Resolve Differences dialogs captured in a
        /// <see cref="CCSession"/>. Plain Cancel - no Reset attempt (we
        /// proved Reset toolbar IDs have no effect on erwin's internal
        /// engine cache). Safe to call with null / zero handles.
        /// </summary>
        public static void CloseSession(CCSession s, Action<string> log)
        {
            if (s == null) return;

            // Install the bridge-level WinEvent hook BEFORE we post any close
            // commands. The hook fires on EVENT_OBJECT_CREATE for every new
            // window in erwin's process, hiding (#32770 / Afx) dialogs before
            // they paint - so the entire dismissal cascade (CC close ->
            // Close Model checklist -> Mart Offline -> Save As pickers ->
            // re-shown Mart Offline) is invisible to the user even though
            // each dialog is dismissed programmatically. The CC wizard
            // itself stays hidden as set by HideWindow on session start; we
            // intentionally do NOT UnhideWindow it (previous attempts at
            // unhide caused a brief visible flash before the next dialog
            // could be hidden in turn).
            int instRc = NativeBridgeService.CleanupHookInstall(log);
            log?.Invoke($"  cleanup: WinEvent hook install rc={instRc}");

            try
            {
                if (s.ResolveDifferences != IntPtr.Zero)
                {
                    log?.Invoke("  cleanup: Cancel Resolve Differences");
                    PostMessage(s.ResolveDifferences, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
                    Thread.Sleep(300);
                }
                if (s.CCWizard != IntPtr.Zero)
                {
                    log?.Invoke("  cleanup: Close CC wizard");
                    PostMessage(s.CCWizard, WM_COMMAND, MakeWParam(CC_CLOSE, 0), IntPtr.Zero);
                    // CC wizard's Close button pops up the "Close Model"
                    // dialog when it has dirty models opened by the wizard
                    // (e.g. the right-side Mart PU that got dirtied by
                    // Apply-to-Right). We must drive that dialog and the
                    // potential "Mart Offline" sub-dialog to avoid leaving
                    // a stale dirty PU in erwin's session (which causes the
                    // 2nd-run "compare-to-itself" cache issue).
                    HandleCloseModelDialogChain(log);
                }

                // Final safety net: erwin variants may not show the "Close
                // Model" checklist (suppressed by user setting) and instead
                // pop a per-model "Save changes to <model>?" message box, or
                // the CC wizard itself may still be alive because a hidden
                // modal blocked CC_CLOSE. Sweep the erwin process for any
                // residual #32770 dialogs and dismiss them so the user is
                // never left staring at a frozen erwin frame.
                ForceCleanupResidualDialogs(s, log);
            }
            catch (Exception ex) { log?.Invoke($"  CloseSession err: {ex.Message}"); }
            finally
            {
                int hidCount = NativeBridgeService.CleanupHookUninstall(log);
                log?.Invoke($"  cleanup: WinEvent hook uninstall, hid {hidCount} dialog(s)");
            }
        }

        /// <summary>
        /// 5-second sweep that finds any #32770 dialogs in erwin's process
        /// (visible OR hidden via WS_EX_LAYERED) and dismisses them based on
        /// title:
        ///   - "Save Changes" / "erwin Data Modeler" with Yes/No buttons -> click No
        ///   - "Close Model" -> drive the existing checklist chain
        ///   - "Mart Offline" -> set Save-to=Close + OK
        ///   - any leftover Wizard frame -> WM_CLOSE
        /// All matched dialogs are first un-hidden so the user can SEE what
        /// happened in the rare case our dismissal path doesn't fully apply.
        /// Logs every dialog inspected, regardless of action taken.
        /// </summary>
        private static void ForceCleanupResidualDialogs(CCSession s, Action<string> log)
        {
            try
            {
                uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                IntPtr myFormHwnd = IntPtr.Zero;
                try { myFormHwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; } catch { }

                // 15s deadline: a single Mart Offline cascade can spawn up
                // to 2 Save As pickers PLUS another Mart Offline pass after
                // each cancel. 5s was too tight to ride out the full cycle.
                int deadline = Environment.TickCount + 15000;
                int sweepCount = 0;
                int totalDismissed = 0;
                int idleSweepsAfterDismiss = 0;
                while (Environment.TickCount < deadline)
                {
                    int dismissedThisSweep = 0;
                    sweepCount++;
                    var residuals = new List<(IntPtr h, string title, string cls)>();
                    EnumWindows((hWnd, lp) =>
                    {
                        GetWindowThreadProcessId(hWnd, out uint pid);
                        if (pid != myPid) return true;
                        var clsBuf = new StringBuilder(64);
                        GetClassName(hWnd, clsBuf, clsBuf.Capacity);
                        string cls = clsBuf.ToString();
                        // Inspect dialog frames + Afx wizard frames + any
                        // window title containing the wizard names.
                        if (cls != "#32770" && !cls.StartsWith("Afx", StringComparison.Ordinal))
                            return true;
                        // Skip our own form / known erwin main frame.
                        if (hWnd == myFormHwnd) return true;
                        string t = GetTitle(hWnd);
                        residuals.Add((hWnd, t, cls));
                        return true;
                    }, IntPtr.Zero);

                    foreach (var r in residuals)
                    {
                        // Do NOT UnhideWindow here. The bridge's WinEvent
                        // hook already aggressively hid each new dialog the
                        // moment it was created (alpha=0 + off-screen),
                        // and we want them to stay hidden while we dispatch
                        // dismissal messages. Unhiding would defeat the
                        // zero-flash design.

                        string t = r.title ?? "";
                        bool dismissed = false;
                        try
                        {
                            if (t.IndexOf("Close Model", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                log?.Invoke($"  [residual] Close Model dialog 0x{r.h.ToInt64():X} - driving checklist");
                                HandleCloseModelDialogChain(log);
                                dismissed = true;
                            }
                            else if (t.IndexOf("Mart Offline", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                log?.Invoke($"  [residual] Mart Offline dialog 0x{r.h.ToInt64():X} - driving Save-to=Close + OK");
                                DismissMartOfflineDialog(r.h, log);
                                dismissed = true;
                            }
                            else if (t.Equals("Save As", StringComparison.OrdinalIgnoreCase)
                                  || t.Equals("Farklı Kaydet", StringComparison.OrdinalIgnoreCase))
                            {
                                // Standard OS Save As file picker - typically
                                // spawned when Mart Offline's per-row Save-to
                                // is left at "Save As" and OK is pressed.
                                // Cancel discards the file picker; the Mart
                                // Offline dialog should re-appear and our
                                // toolbar-Close path will handle it next.
                                log?.Invoke($"  [residual] Save As file picker 0x{r.h.ToInt64():X} - Cancel");
                                var root = AutomationElement.FromHandle(r.h);
                                if (root != null)
                                    dismissed = ClickButtonByName(root, new[] { "Cancel", "İptal" });
                                if (!dismissed)
                                {
                                    PostMessage(r.h, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
                                    dismissed = true;
                                }
                            }
                            else if (t.IndexOf("Save", StringComparison.OrdinalIgnoreCase) >= 0
                                  || t.IndexOf("erwin Data Modeler", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                // Save-changes prompt: discard so the v1 PU
                                // we modified for diff is dropped, not saved
                                // back to Mart.
                                log?.Invoke($"  [residual] Save/popup dialog 0x{r.h.ToInt64():X} title='{t}' - clicking No");
                                var root = AutomationElement.FromHandle(r.h);
                                if (root != null)
                                    dismissed = ClickButtonByName(root, new[] { "No", "Hayır", "Don't Save", "Discard", "Cancel", "İptal" });
                                if (!dismissed)
                                {
                                    PostMessage(r.h, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
                                    dismissed = true;
                                }
                            }
                            else if (s != null && (r.h == s.CCWizard || r.h == s.ResolveDifferences))
                            {
                                log?.Invoke($"  [residual] CC/RD frame 0x{r.h.ToInt64():X} still alive - WM_CLOSE");
                                PostMessage(r.h, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
                                dismissed = true;
                            }
                            else if (t.IndexOf("Wizard", StringComparison.OrdinalIgnoreCase) >= 0
                                  || t.IndexOf("Resolve Differences", StringComparison.OrdinalIgnoreCase) >= 0
                                  || t.IndexOf("Complete Compare", StringComparison.OrdinalIgnoreCase) >= 0
                                  || t.IndexOf("Forward Engineer", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                log?.Invoke($"  [residual] wizard 0x{r.h.ToInt64():X} title='{t}' - WM_CLOSE");
                                PostMessage(r.h, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
                                dismissed = true;
                            }
                            else
                            {
                                // Quietly log unidentified dialogs for diag.
                                log?.Invoke($"  [residual] (skip) cls='{r.cls}' title='{t}' hwnd=0x{r.h.ToInt64():X}");
                            }
                        }
                        catch (Exception ex)
                        {
                            log?.Invoke($"  [residual] dismiss err on 0x{r.h.ToInt64():X}: {ex.Message}");
                        }

                        if (dismissed) dismissedThisSweep++;
                    }

                    if (dismissedThisSweep > 0)
                    {
                        totalDismissed += dismissedThisSweep;
                        idleSweepsAfterDismiss = 0;
                        Thread.Sleep(250);
                    }
                    else
                    {
                        // Stop early when we've already dismissed something
                        // and the next 600ms passed without any new dialogs.
                        // If we never dismissed anything, just exit on first
                        // empty sweep (nothing to clean up).
                        if (totalDismissed == 0) break;
                        idleSweepsAfterDismiss++;
                        if (idleSweepsAfterDismiss >= 3) break;
                        Thread.Sleep(200);
                    }
                }

                log?.Invoke($"  cleanup sweep: {sweepCount} pass(es) complete, {totalDismissed} dialog(s) dismissed");
            }
            catch (Exception ex) { log?.Invoke($"  ForceCleanupResidualDialogs err: {ex.Message}"); }
        }

        /// <summary>
        /// Drives the two-step dialog chain that erwin shows when closing a
        /// wizard that has dirty opened models: (1) "Close Model" dialog with
        /// a ListView of dirty models - uncheck the Mart-opened row whose
        /// name ends in '*' so it's discarded instead of saved, then OK.
        /// (2) "Mart Offline" dialog (may be skipped if user has previously
        /// ticked "don't show this in future") - switch the "Save to" combo
        /// to "Close" and OK. Mirrors the exact manual user flow verified by
        /// screenshots so there's no hidden erwin-internal state divergence.
        /// </summary>
        private static void HandleCloseModelDialogChain(Action<string> log)
        {
            try
            {
                IntPtr closeDlg = WaitForDialog("Close Model", 3000);
                if (closeDlg == IntPtr.Zero)
                {
                    log?.Invoke("  cleanup: no 'Close Model' dialog appeared (wizard closed directly)");
                    return;
                }
                log?.Invoke($"  cleanup: 'Close Model' dialog = 0x{closeDlg.ToInt64():X}");

                var root = AutomationElement.FromHandle(closeDlg);
                if (root == null)
                {
                    log?.Invoke("  cleanup: UIA FromHandle returned null on Close Model dialog");
                    return;
                }

                int uncheckedCount = 0;
                try
                {
                    // Find DataItems under the dialog - each is a row in the
                    // Close Model ListView. The row's TogglePattern is on
                    // the row itself (not on a child checkbox cell).
                    var itemsCond = new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                    var items = root.FindAll(TreeScope.Descendants, itemsCond);
                    log?.Invoke($"  cleanup: Close Model has {items.Count} row(s)");
                    foreach (AutomationElement row in items)
                    {
                        string rowName = "";
                        try { rowName = row.Current.Name ?? ""; } catch { }
                        log?.Invoke($"    row name='{rowName}'");

                        // Target: Mart-opened model whose name contains '*'
                        // (dirty indicator). The Name property of the row
                        // usually concatenates all column texts, so it
                        // carries both the model-name asterisk and the
                        // Mart path.
                        bool isMart = rowName.IndexOf("Mart://", StringComparison.OrdinalIgnoreCase) >= 0
                                   || rowName.IndexOf("Mart:\\", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool isDirty = rowName.Contains("*");
                        if (!(isMart && isDirty))
                        {
                            log?.Invoke("    (skip: not a Mart-opened dirty row)");
                            continue;
                        }

                        if (row.TryGetCurrentPattern(TogglePattern.Pattern, out object togObj))
                        {
                            var tog = (TogglePattern)togObj;
                            var st = tog.Current.ToggleState;
                            log?.Invoke($"    current ToggleState={st}");
                            if (st == ToggleState.On)
                            {
                                tog.Toggle();
                                uncheckedCount++;
                                log?.Invoke("    unchecked via row TogglePattern");
                            }
                            else
                            {
                                log?.Invoke("    already off - skipping toggle");
                            }
                            continue;
                        }

                        // Fallback: search for a child CheckBox and toggle it.
                        var cbCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox);
                        var cb = row.FindFirst(TreeScope.Descendants, cbCond);
                        if (cb != null && cb.TryGetCurrentPattern(TogglePattern.Pattern, out object cbTog))
                        {
                            var tog = (TogglePattern)cbTog;
                            if (tog.Current.ToggleState == ToggleState.On)
                            {
                                tog.Toggle();
                                uncheckedCount++;
                                log?.Invoke("    unchecked via child CheckBox TogglePattern");
                            }
                        }
                        else
                        {
                            log?.Invoke("    WARN: no TogglePattern on row or child CheckBox");
                        }
                    }
                }
                catch (Exception ex) { log?.Invoke($"  cleanup: iterate rows err: {ex.Message}"); }

                log?.Invoke($"  cleanup: unchecked {uncheckedCount} Mart-dirty row(s), clicking OK");
                if (!ClickButtonByName(root, new[] { "OK", "Tamam" }))
                    log?.Invoke("  cleanup: WARN could not find OK button on Close Model dialog");

                // Step 2: optional "Mart Offline" dialog.
                Thread.Sleep(400);
                IntPtr martOffline = WaitForDialog("Mart Offline", 2000);
                if (martOffline == IntPtr.Zero)
                {
                    log?.Invoke("  cleanup: 'Mart Offline' dialog did not appear (may be suppressed)");
                    return;
                }
                DismissMartOfflineDialog(martOffline, log);
            }
            catch (Exception ex) { log?.Invoke($"  HandleCloseModelDialogChain err: {ex.Message}"); }
        }

        /// <summary>
        /// Drives the "Mart Offline" dialog: sets Save-to to "Close" on the
        /// toolbar combo AND on each listview row (erwin keeps a per-row
        /// override that the toolbar combo does not always propagate to),
        /// then tries OK via UIA, falling back to WM_COMMAND IDOK if the
        /// click doesn't take the dialog down. Also brings the dialog to the
        /// foreground first since cleanup runs after dialogs were hidden via
        /// WS_EX_LAYERED and OK clicks don't always route on a window the OS
        /// has marked invisible.
        /// </summary>
        private static void DismissMartOfflineDialog(IntPtr martOffline, Action<string> log)
        {
            if (martOffline == IntPtr.Zero) return;
            var offRoot = AutomationElement.FromHandle(martOffline);
            if (offRoot == null)
            {
                log?.Invoke("  cleanup: UIA FromHandle returned null on Mart Offline dialog");
                return;
            }

            // Make sure the dialog can actually receive input. UnhideWindow
            // already cleared WS_EX_LAYERED, but the OS may still consider
            // the window non-foreground; SetForegroundWindow restores routing.
            try { SetForegroundWindow(martOffline); } catch { }

            // NOTE: we deliberately do NOT toggle the "Close models, don't
            // show this dialog in future" checkbox. Empirically that flag
            // does not alter the current OK semantics (per-row Save-to is
            // still honored, so OK still triggers Save As file pickers when
            // a row defaults to Save), AND persisting the user's dialog
            // preference would silently change the user's experience for
            // their own manual CC operations - a side-effect we can't ship.

            // STRATEGY 1: dispatch toolbar button via Win32 WM_COMMAND with
            // the button's command ID extracted from TB_GETBUTTON. This is
            // mouse-position-free and DPI/multi-monitor agnostic. erwin's
            // XTPToolbar filters UIA InvokePattern but routes WM_COMMAND
            // through standard MFC dispatch, so the per-row Save-to mode
            // actually changes (unlike UIA Invoke which silently no-ops).
            bool toolbarPathWorked = TryClickMartOfflineToolbarViaWmCommand(martOffline, offRoot, log);

            // STRATEGY 1b (UIA fallback if Win32 toolbar discovery fails):
            // visual btn[2] via UIA Invoke. Less reliable but backup.
            if (!toolbarPathWorked)
                toolbarPathWorked = TryClickMartOfflineToolbarClose(offRoot, log);

            // STRATEGY 2 (fallback): iterate every per-row ComboBox and try
            // to switch it to "Close" via UIA. Diagnostic-heavy: logs each
            // combo's current value AND its full dropdown list so we can see
            // (in next runs) what items erwin actually exposes.
            if (!toolbarPathWorked)
            {
                try
                {
                    var comboCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox);
                    var combos = offRoot.FindAll(TreeScope.Descendants, comboCond);
                    log?.Invoke($"  cleanup: Mart Offline toolbar path failed - falling back to {combos.Count} combo(s)");
                    int setCount = 0;
                    int idx = 0;
                    foreach (AutomationElement combo in combos)
                    {
                        if (TrySetComboBoxToClose(combo, idx++, log)) setCount++;
                    }
                    log?.Invoke($"    set {setCount}/{combos.Count} combo(s)");
                }
                catch (Exception ex) { log?.Invoke($"  cleanup: combo err: {ex.Message}"); }
            }

            Thread.Sleep(200);

            // First try: UIA Invoke on OK / Tamam button.
            bool clicked = ClickButtonByName(offRoot, new[] { "OK", "Tamam" });
            if (!clicked)
                log?.Invoke("  cleanup: WARN could not find OK button on Mart Offline dialog");

            // Second try if dialog is still alive 500ms later: WM_COMMAND IDOK.
            Thread.Sleep(500);
            if (IsWindow(martOffline))
            {
                log?.Invoke("  cleanup: Mart Offline still alive after OK click - posting WM_COMMAND IDOK");
                PostMessage(martOffline, WM_COMMAND, MakeWParam(IDOK, 0), IntPtr.Zero);
                Thread.Sleep(400);
            }

            log?.Invoke(IsWindow(martOffline)
                ? "  cleanup: WARN Mart Offline did NOT close - manual intervention may be required"
                : "  cleanup: Mart Offline dismissed");
        }

        /// <summary>
        /// Walks the Mart Offline dialog's toolbar (ControlType.ToolBar) and
        /// invokes the button whose accessible name signals "Close" / "Set
        /// to Close" / "Discard". Logs every button's name so the next run
        /// reveals the exact label if our keyword match misses. Returns true
        /// when a matching button was clicked, false otherwise.
        /// </summary>
        /// <summary>
        /// Win32 path: find the toolbar HWND inside the Mart Offline dialog,
        /// enumerate its buttons via TB_GETBUTTON to obtain command IDs,
        /// then PostMessage WM_COMMAND with the 3rd button's command ID
        /// (visual "Set All to Close" position per user's manual sequence).
        /// Mouse-free, DPI-agnostic, no UIA InvokePattern - this works
        /// because XTPToolbar routes button clicks through standard MFC
        /// WM_COMMAND dispatch even though it filters UIA Invoke.
        /// </summary>
        private static bool TryClickMartOfflineToolbarViaWmCommand(
            IntPtr martOffline, AutomationElement offRoot, Action<string> log)
        {
            try
            {
                IntPtr toolbarHwnd = FindToolbarChild(martOffline);
                if (toolbarHwnd == IntPtr.Zero)
                {
                    log?.Invoke("  cleanup: no ToolbarWindow32 child found in Mart Offline");
                    return false;
                }
                log?.Invoke($"  cleanup: toolbar HWND = 0x{toolbarHwnd.ToInt64():X}");

                IntPtr countRes = SendMessage(toolbarHwnd, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
                int count = countRes.ToInt32();
                log?.Invoke($"    TB_BUTTONCOUNT = {count}");
                if (count <= 0) return false;

                // Walk all buttons, log their command IDs, pick visual
                // index 2 (3rd from left) as the "Close" action.
                int chosenCmd = 0;
                for (int i = 0; i < count; i++)
                {
                    var tbb = new TBBUTTON();
                    SendMessageTbButton(toolbarHwnd, TB_GETBUTTON, new IntPtr(i), ref tbb);
                    log?.Invoke($"    btn[{i}] cmdId={tbb.idCommand} state=0x{tbb.fsState:X2} style=0x{tbb.fsStyle:X2}");
                    if (i == 2) chosenCmd = tbb.idCommand;
                }
                if (chosenCmd == 0)
                {
                    log?.Invoke("    no btn[2] command id captured");
                    return false;
                }

                // Dispatch WM_COMMAND with the toolbar button's command ID.
                // wParam high word = BN_CLICKED (0). lParam = toolbar HWND
                // (per MFC convention - identifies the source control).
                IntPtr wParam = MakeWParam(chosenCmd, BN_CLICKED);
                PostMessage(martOffline, WM_COMMAND, wParam, toolbarHwnd);
                log?.Invoke($"    posted WM_COMMAND cmdId={chosenCmd} to dialog (3rd toolbar button = Close action)");
                return true;
            }
            catch (Exception ex) { log?.Invoke($"  toolbar WM_COMMAND err: {ex.Message}"); return false; }
        }

        private static IntPtr FindToolbarChild(IntPtr parent)
        {
            IntPtr result = IntPtr.Zero;
            EnumChildWindows(parent, (hWnd, lp) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(hWnd, cls, cls.Capacity);
                string c = cls.ToString();
                // Standard Win32 toolbar (MFC wraps XTP on top of this).
                if (c == "ToolbarWindow32" || c.IndexOf("ToolBar", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static bool TryClickMartOfflineToolbarClose(AutomationElement offRoot, Action<string> log)
        {
            try
            {
                // First try: scope buttons to a ToolBar element. XTPToolBar
                // sometimes registers as ControlType.ToolBar via UIAutomationCore.
                var toolbarCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar);
                var toolbars = offRoot.FindAll(TreeScope.Descendants, toolbarCond);
                log?.Invoke($"  cleanup: Mart Offline has {toolbars.Count} toolbar(s)");

                // Collect candidate buttons and their bounding rects so we
                // can sort them by visual X position. UIA enumeration order
                // does NOT match left-to-right paint order on XTP toolbars,
                // so positional fallback was clicking the wrong button.
                var btnCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);

                // Get the dialog bounds; we'll filter to buttons inside the
                // top strip (above the listview) which is where the toolbar
                // sits visually. This protects us from picking up the OK /
                // Cancel buttons at the bottom.
                System.Windows.Rect dialogRect = default;
                try { dialogRect = offRoot.Current.BoundingRectangle; } catch { }
                double topStripCutoff = dialogRect.Top + dialogRect.Height * 0.40;

                var allBtns = offRoot.FindAll(TreeScope.Descendants, btnCond);
                var candidates = new List<(AutomationElement el, string name, string help, System.Windows.Rect rect)>();
                for (int i = 0; i < allBtns.Count; i++)
                {
                    AutomationElement b = allBtns[i];
                    string n = "";
                    string h = "";
                    System.Windows.Rect r = default;
                    try { n = b.Current.Name ?? ""; } catch { }
                    try { h = b.Current.HelpText ?? ""; } catch { }
                    try { r = b.Current.BoundingRectangle; } catch { }
                    candidates.Add((b, n, h, r));
                }
                log?.Invoke($"    found {candidates.Count} button(s) total in dialog (will filter to toolbar strip):");
                foreach (var c in candidates)
                {
                    log?.Invoke($"      btn name='{c.name}' help='{c.help}' rect=({c.rect.Left:0},{c.rect.Top:0},{c.rect.Right:0},{c.rect.Bottom:0})");
                }

                // Filter to top-strip toolbar buttons: small (icon-sized),
                // high up (Y < topStripCutoff), and NOT title-bar window
                // chrome (Minimize/Maximize/Close X). Title-bar buttons sit
                // higher on the dialog and have explicit Name values that
                // match the chrome - excluding them prevents Strategy 1
                // from picking the dialog's X (which Cancels the dialog,
                // leaving the CC close path aborted and v1 still loaded).
                var toolbarBtns = candidates
                    .Where(c => c.rect.Width > 0 && c.rect.Height > 0
                             && c.rect.Top < topStripCutoff
                             && c.rect.Width < 60
                             && !IsTitleBarChromeButton(c.name))
                    .OrderBy(c => c.rect.Left)
                    .ToList();
                log?.Invoke($"    after top-strip filter + visual sort: {toolbarBtns.Count} button(s)");

                // STRATEGY 1: keyword match (close / discard) AFTER title-
                // bar filter, so we never pick the dialog's X button.
                foreach (var c in toolbarBtns)
                {
                    string blob = (c.name + " " + c.help).ToLowerInvariant();
                    if (blob.Contains("close") || blob.Contains("discard"))
                    {
                        if (TryInvokeButton(c.el, $"keyword '{c.name}'", log)) return true;
                    }
                }

                // STRATEGY 2: positional - the user manually clicks the 3rd
                // button from the left, so pick visual index 2.
                if (toolbarBtns.Count >= 3)
                {
                    var c = toolbarBtns[2];
                    if (TryInvokeButton(c.el, $"visual btn[2] '{c.name}' rect.Left={c.rect.Left:0}", log)) return true;
                }

                // STRATEGY 3: if filter discarded everything, fall back to
                // raw 3rd button by raw enumeration order.
                if (toolbarBtns.Count == 0 && candidates.Count >= 3)
                {
                    var c = candidates[2];
                    if (TryInvokeButton(c.el, $"raw btn[2] '{c.name}'", log)) return true;
                }
            }
            catch (Exception ex) { log?.Invoke($"  toolbar enum err: {ex.Message}"); }
            return false;
        }

        private static bool IsTitleBarChromeButton(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.Equals("Close", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Minimize", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Maximize", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Restore", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Kapat", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Simge Durumuna Küçült", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Ekranı Kapla", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryInvokeButton(AutomationElement el, string label, Action<string> log)
        {
            if (el == null) return false;
            try
            {
                if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object inv))
                {
                    ((InvokePattern)inv).Invoke();
                    log?.Invoke($"    invoked toolbar button: {label}");
                    return true;
                }
            }
            catch (Exception ex) { log?.Invoke($"    invoke err on {label}: {ex.Message}"); }
            return false;
        }


        /// <summary>
        /// Diagnostic-heavy combo setter: dumps current value and dropdown
        /// items, then tries to select an item matching "Close" / "Discard"
        /// / "Don't Save" via Expand+Select; falls back to ValuePattern if
        /// no list match. Returns true on any successful select.
        /// </summary>
        private static bool TrySetComboBoxToClose(AutomationElement combo, int idx, Action<string> log)
        {
            if (combo == null) return false;
            string current = "?";
            try
            {
                if (combo.TryGetCurrentPattern(ValuePattern.Pattern, out object vpRead))
                    current = ((ValuePattern)vpRead).Current.Value ?? "?";
            }
            catch { }
            log?.Invoke($"    combo[{idx}] current='{current}'");

            try
            {
                if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object ec))
                {
                    try
                    {
                        ((ExpandCollapsePattern)ec).Expand();
                        Thread.Sleep(120);
                        var itemCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
                        var items = combo.FindAll(TreeScope.Descendants, itemCond);
                        AutomationElement target = null;
                        var available = new List<string>();
                        foreach (AutomationElement it in items)
                        {
                            string nm = "";
                            try { nm = it.Current.Name ?? ""; } catch { }
                            available.Add(nm);
                            if (target == null)
                            {
                                string lo = nm.ToLowerInvariant();
                                if (lo.Contains("close") || lo.Contains("discard") || lo.Contains("don't save"))
                                    target = it;
                            }
                        }
                        log?.Invoke($"      items=[{string.Join(", ", available)}]");
                        if (target != null && target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sip))
                        {
                            ((SelectionItemPattern)sip).Select();
                            log?.Invoke($"      selected '{target.Current.Name}'");
                            return true;
                        }
                        ((ExpandCollapsePattern)ec).Collapse();
                    }
                    catch (Exception ex) { log?.Invoke($"      expand err: {ex.Message}"); }
                }

                if (combo.TryGetCurrentPattern(ValuePattern.Pattern, out object vp))
                {
                    try
                    {
                        ((ValuePattern)vp).SetValue("Close");
                        log?.Invoke("      ValuePattern.SetValue('Close') ok");
                        return true;
                    }
                    catch (Exception ex) { log?.Invoke($"      ValuePattern err: {ex.Message}"); }
                }
            }
            catch (Exception ex) { log?.Invoke($"    combo set err: {ex.Message}"); }
            return false;
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        /// <summary>
        /// Synchronous worker that walks the CC wizard from nothing → Apply-to-Right.
        /// Leaves CC wizard and Resolve Differences alive (hidden) on success.
        /// </summary>
        private static CCSession DriveCCAndApply(int martVersion, string catalogPath,
            bool dumpPickerTree, Action<string> log, Action<bool> overlayToggle = null)
        {
            var session = new CCSession();
            try
            {
                IntPtr erwinMain = FindErwinMain();
                if (erwinMain == IntPtr.Zero)
                {
                    log?.Invoke("  erwin main window not found.");
                    return null;
                }
                log?.Invoke($"  erwin main = 0x{erwinMain.ToInt64():X}");

                var dialogsBefore = EnumerateVisibleDialogs();
                log?.Invoke($"  baseline: {dialogsBefore.Count} visible dialogs");

                // Step 1: open CC wizard
                log?.Invoke("  [1] posting CMD_COMPLETE_COMPARE (1082) to main frame");
                PostMessage(erwinMain, WM_COMMAND, MakeWParam(CMD_COMPLETE_COMPARE, 0), IntPtr.Zero);
                IntPtr ccWizard = WaitForNewDialog(dialogsBefore, "CC wizard", 5000, log);
                if (ccWizard == IntPtr.Zero) return null;
                HideWindow(ccWizard);
                session.CCWizard = ccWizard;
                log?.Invoke($"  CC wizard = 0x{ccWizard.ToInt64():X}  title='{GetTitle(ccWizard)}'");

                // Step 2: Back to Overview, then Next x2 to land on Right Model page
                for (int i = 0; i < 12; i++)
                {
                    string t = GetTitle(ccWizard);
                    if (t.StartsWith("Wizard Overview", StringComparison.OrdinalIgnoreCase)
                        || t.StartsWith("Overview", StringComparison.OrdinalIgnoreCase)) break;
                    PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_BACK, 0), IntPtr.Zero);
                    Thread.Sleep(100);
                }
                log?.Invoke("  [2] at Overview, pressing Next x2 to reach Right Model page");
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero);
                Thread.Sleep(200);
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero);
                Thread.Sleep(400);
                log?.Invoke($"  at Right Model page. title='{GetTitle(ccWizard)}'");

                // Step 3: select From Mart radio + press Load
                log?.Invoke("  [3] posting WM_COMMAND 1081 (From Mart radio)");
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CMD_FROM_MART_RADIO, 0), IntPtr.Zero);
                Thread.Sleep(200);

                var dialogsBeforeLoad = EnumerateVisibleDialogs();
                log?.Invoke("  [3] posting WM_COMMAND 1082 (Load button)");
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CMD_LOAD_BUTTON, 0), IntPtr.Zero);

                // Step 4: wait for Mart picker dialog, select version, press Open
                IntPtr martDlg = WaitForNewDialog(dialogsBeforeLoad, "Mart picker", 5000, log);
                if (martDlg != IntPtr.Zero)
                {
                    HideWindow(martDlg);
                    log?.Invoke($"  Mart picker = 0x{martDlg.ToInt64():X}  title='{GetTitle(martDlg)}'");
                    if (dumpPickerTree)
                    {
                        log?.Invoke("  [4] === UIA tree dump of Mart picker ===");
                        try { DumpUiaTree(AutomationElement.FromHandle(martDlg), 0, 200, log); }
                        catch (Exception ex) { log?.Invoke($"    dump err: {ex.Message}"); }
                        log?.Invoke("  === end Mart picker dump ===");
                    }
                    if (!SelectMartVersionInPicker(martDlg, martVersion, catalogPath, log))
                    {
                        log?.Invoke("  [WARN] failed to select Mart version in picker; cancelling");
                        PostMessage(martDlg, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
                        Thread.Sleep(200);
                        return null;
                    }
                }
                else
                {
                    log?.Invoke("  no Mart picker appeared (maybe defaulted?) - continuing");
                }

                // Step 5: Compare
                Thread.Sleep(500);   // let the Right Model page settle after Open
                log?.Invoke($"  [5] posting WM_COMMAND {CC_COMPARE} (Compare)");
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_COMPARE, 0), IntPtr.Zero);

                // "Compare to itself" popup - if it appears, erwin's CC
                // engine cached a stale right-side PU (modified by a prior
                // Apply-to-Right in the same session) that matches the
                // active model. Dismiss with No and abort - caller should
                // refresh state (close active model, reconnect) before
                // retrying. Auto-"Yes" would silently produce empty DDL.
                Thread.Sleep(800);
                IntPtr popup = WaitForDialog("erwin Data Modeler", 1500);
                if (popup != IntPtr.Zero)
                {
                    log?.Invoke($"  popup: '{GetTitle(popup)}' - dismissing with No (stale cache)");
                    try
                    {
                        var pop = AutomationElement.FromHandle(popup);
                        if (pop != null) ClickButtonByName(pop, new[] { "No", "Hayır", "Cancel", "İptal" });
                    }
                    catch { }
                    Thread.Sleep(400);
                    log?.Invoke("  aborting: erwin's CC engine has stale right-side PU state.");
                    return null;
                }

                // Step 6: wait for Resolve Differences
                IntPtr resolveDlg = WaitForDialog("Resolve Differences", 10000);
                if (resolveDlg == IntPtr.Zero)
                {
                    log?.Invoke("  Resolve Differences did not appear within 10s");
                    return null;
                }
                log?.Invoke($"  Resolve Differences = 0x{resolveDlg.ToInt64():X}");
                session.ResolveDifferences = resolveDlg;

                // Make the addin form click-through ONCE up-front (instead
                // of toggling around each click attempt). This is a single
                // cross-thread Invoke saving ~50-100ms vs the previous
                // pattern of toggle-off + click + toggle-on per attempt.
                // Form stays visually opaque, just routes mouse to whatever
                // is below it.
                try { overlayToggle?.Invoke(false); } catch { }

                // Poll for listview readiness. Three conditions all required:
                //   1. Listview HWND exists (FindListViewById)
                //   2. Item count > 0 (model row actually populated, not
                //      just the column header laid out - the previous
                //      version checked only column layout and exited at
                //      0ms, clicking on empty space which fired a bogus
                //      EDR tx and made OnFE produce "No schema to generate")
                //   3. Item 0's arrow column (subItem=6) has a non-zero rect
                IntPtr lv = IntPtr.Zero;
                int polled = 0;
                const int pollStep = 5;
                const int pollMaxMs = 800;
                while (polled < pollMaxMs)
                {
                    lv = FindListViewById(resolveDlg, 200, log: null);
                    if (lv != IntPtr.Zero)
                    {
                        IntPtr cnt = SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
                        if (cnt.ToInt32() > 0)
                        {
                            RECT testRc = new RECT { left = LVIR_BOUNDS, top = 6, right = 0, bottom = 0 };
                            SendMessage(lv, LVM_GETSUBITEMRECT, new IntPtr(0), ref testRc);
                            if (testRc.right > testRc.left && testRc.bottom > testRc.top)
                            {
                                log?.Invoke($"  [7] listview ready after {polled}ms (items={cnt.ToInt32()}, arrow rect ok)");
                                break;
                            }
                        }
                    }
                    Thread.Sleep(pollStep);
                    polled += pollStep;
                }
                if (lv == IntPtr.Zero)
                {
                    log?.Invoke($"  [7] listview id=200 not found within {pollMaxMs}ms - aborting");
                    try { overlayToggle?.Invoke(true); } catch { }
                    return session;
                }
                if (polled >= pollMaxMs)
                {
                    log?.Invoke($"  [7] listview found but never reached ready state ({pollMaxMs}ms timeout)");
                }

                // Arrow column for Model row: item=0, subItem=6 (per NMHDR dump).
                RECT rc = new RECT { left = LVIR_BOUNDS, top = 6, right = 0, bottom = 0 };
                SendMessage(lv, LVM_GETSUBITEMRECT, new IntPtr(0), ref rc);
                log?.Invoke($"  [7] arrow rect (client) = ({rc.left},{rc.top})-({rc.right},{rc.bottom})");

                POINT pt = new POINT { X = (rc.left + rc.right) / 2, Y = (rc.top + rc.bottom) / 2 };
                if (!ClientToScreen(lv, ref pt))
                {
                    log?.Invoke("  [7] ClientToScreen failed");
                    return session;
                }
                log?.Invoke($"  [7] click target (screen) = ({pt.X},{pt.Y})");

                int txBefore = NativeBridgeService.GetEdrTxCount();
                if (!GetCursorPos(out POINT saved))
                {
                    log?.Invoke("  [7] GetCursorPos failed");
                    return session;
                }

                // Mouse simulation is the only path that works for the
                // Apply-to-Right click. Two non-mouse alternatives proven
                // failed (2026-04-26):
                //   1. Direct CallApplyDifferencesToRight(leftMs, rightMs)
                //      via bridge trampoline -> SEH 0xC0000005 + 50 GDM-13
                //      popups (function depends on dialog-member state seeded
                //      by the listview NM_CLICK handler).
                //   2. SendSynthesizedNmClick(resolveDlg, lv, ...) -> WM_NOTIFY
                //      returns rc=0, no EDR tx (XTP listview filters
                //      synthesized notifies the same way it filters UIA Invoke
                //      on toolbar buttons; the child's hit-test handler must
                //      run on a real input event for state to propagate).
                // The mouse path uses Win32 LVM_GETSUBITEMRECT + ClientToScreen
                // so it remains DPI-aware and multi-monitor agnostic. See
                // reference_apply_diff_right_direct_call_avs.md for full
                // verdict on why a programmatic replacement isn't viable.
                // Mouse simulation is the only working path - all synthetic
                // input alternatives (direct CallApplyDifferencesToRight,
                // SendSynthesizedNmClick WM_NOTIFY, SendMessage WM_LBUTTON)
                // are filtered by XTP's custom listview hit-test layer. See
                // reference_apply_diff_right_direct_call_avs.md for the
                // verdict. We minimize the visible-RD window by hiding it
                // the moment the click registers, BEFORE waiting for EDR
                // settle - the EDR transaction commits regardless of RD
                // visibility, so there's no reason to keep it on screen.
                int txAfter = txBefore;
                try
                {
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        // Form is already click-through (overlayToggle
                        // applied once before polling). RD must be foreground
                        // for XTP to accept the synthetic click.
                        SetForegroundWindow(resolveDlg);
                        try
                        {
                            ShowCursor(false);
                            SendMouseClickAt(pt.X, pt.Y);
                            Thread.Sleep(20);
                        }
                        finally
                        {
                            SetCursorPos(saved.X, saved.Y);
                            ShowCursor(true);
                        }

                        // Hide RD immediately after the click is sent. Click
                        // is already registered with the listview's input
                        // queue; EDR tx will fire async whether RD is
                        // visible or not. This cuts the perceptible flash
                        // by ~500ms (the EDR settle window).
                        HideWindow(resolveDlg);
                        log?.Invoke($"  [7] attempt {attempt}: click fired + RD hidden, waiting for EDR tx settle");

                        txAfter = WaitForEdrTxSettle(txBefore, timeoutMs: 2500, stableMs: 350, log);
                        if (txAfter > txBefore)
                        {
                            log?.Invoke($"  [7] attempt {attempt}: tx delta = {txAfter - txBefore}");
                            break;
                        }
                        log?.Invoke($"  [7] attempt {attempt}: no tx - retrying...");
                        // Restore visibility for retry - second attempt is
                        // rare (only fires when first click missed the hot
                        // zone, e.g. listview not yet fully populated).
                        UnhideWindow(resolveDlg);
                        Thread.Sleep(100);
                    }

                    session.Applied = (txAfter - txBefore) > 0;
                    if (!session.Applied)
                        log?.Invoke("  [WARN] mouse click did not trigger EDR tx after 2 attempts.");
                }
                finally
                {
                    // Restore the addin form's interactivity. Done here so
                    // it runs even if click attempts threw or returned
                    // early - leaving the form stuck in click-through mode
                    // would silently break user interaction.
                    try { overlayToggle?.Invoke(true); } catch { }
                }
                return session;
            }
            catch (Exception ex)
            {
                log?.Invoke($"  automation threw: {ex.GetType().Name}: {ex.Message}");
                return session.CCWizard != IntPtr.Zero ? session : null;
            }
        }

        // ── UIA helpers ─────────────────────────────────────────────────────

        private static bool SelectMartVersionInPicker(IntPtr martDlg, int version, string catalogPath, Action<string> log)
        {
            try
            {
                var root = AutomationElement.FromHandle(martDlg);
                if (root == null) return false;

                // Step 0: navigate the catalog tree to our target folder. Without
                // this, the model list is empty and the version combo is disabled.
                // `catalogPath` looks like "Kursat/MetaRepo" or just "Kursat";
                // we only need the folder segments (first N-1), the last segment
                // is the model name and goes into the DataGrid.
                if (!string.IsNullOrEmpty(catalogPath))
                {
                    var tree = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "2054"));
                    if (tree != null)
                    {
                        // Walk path segments. Trailing segment is the MODEL name
                        // (not a tree node), so stop before it.
                        var segs = catalogPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                        int walkCount = Math.Max(0, segs.Length - 1);
                        AutomationElement node = tree;
                        for (int i = 0; i < walkCount; i++)
                        {
                            var child = node.FindFirst(TreeScope.Descendants,
                                new AndCondition(
                                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
                                    new PropertyCondition(AutomationElement.NameProperty, segs[i])));
                            if (child == null) { log?.Invoke($"    catalog node '{segs[i]}' not found"); break; }
                            try
                            {
                                if (child.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object ec))
                                    ((ExpandCollapsePattern)ec).Expand();
                                if (child.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object si))
                                    ((SelectionItemPattern)si).Select();
                                else
                                    child.SetFocus();
                                log?.Invoke($"    catalog navigated: '{segs[i]}'");
                            }
                            catch (Exception ex) { log?.Invoke($"    catalog '{segs[i]}' select err: {ex.Message}"); }
                            Thread.Sleep(250);
                            node = child;
                        }
                    }
                    else
                    {
                        log?.Invoke("    catalog tree (id=2054) not found");
                    }
                }

                // Step 1: ensure a model is selected in the DataGrid (SysListView32 id=30270).
                // After catalog navigation the grid should be populated.
                var grid = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "30270"));
                if (grid != null)
                {
                    var firstRow = grid.FindFirst(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
                    if (firstRow == null)
                        firstRow = grid.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
                    if (firstRow != null)
                    {
                        string rowName = "";
                        try { rowName = firstRow.Current.Name ?? ""; } catch { }
                        log?.Invoke($"    model list row[0] = '{rowName}'");
                        try
                        {
                            if (firstRow.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sip))
                                ((SelectionItemPattern)sip).Select();
                            else
                                firstRow.SetFocus();
                            Thread.Sleep(200);
                        }
                        catch (Exception ex) { log?.Invoke($"    row select err: {ex.Message}"); }
                    }
                    else
                    {
                        log?.Invoke("    model list has no DataItem rows");
                    }
                }

                // Step 2: locate the Open Version ComboBox (AutomationId=2111), expand it,
                // enumerate its ListItems, pick the one matching our target version.
                var versionCombo = root.FindFirst(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "2111"),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)));
                if (versionCombo == null)
                {
                    log?.Invoke("    [WARN] Open Version combo (id=2111) not found");
                    PostMessage(martDlg, WM_COMMAND, MakeWParam(IDOK, 0), IntPtr.Zero);
                    return false;
                }

                try
                {
                    if (versionCombo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object ecp))
                    {
                        ((ExpandCollapsePattern)ecp).Expand();
                        Thread.Sleep(300);
                        log?.Invoke("    version combo expanded");
                    }
                }
                catch (Exception ex) { log?.Invoke($"    expand err: {ex.Message}"); }

                // Enumerate ListItems now-visible inside the combo's dropdown.
                var items = versionCombo.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                log?.Invoke($"    version combo: {items.Count} items");

                string vs = version.ToString();
                AutomationElement target = null;
                int idx = 0;
                foreach (AutomationElement el in items)
                {
                    string n = "";
                    try { n = el.Current.Name ?? ""; } catch { }
                    log?.Invoke($"      version[{idx++}] name='{n}'");
                    if (string.IsNullOrEmpty(n)) continue;
                    bool match =
                        System.Text.RegularExpressions.Regex.IsMatch(n,
                            @"(?i)\bversion\s+" + System.Text.RegularExpressions.Regex.Escape(vs) + @"\b")
                        || n.Equals(vs, StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith($"v{vs}", StringComparison.OrdinalIgnoreCase);
                    if (match) { target = el; break; }
                }

                if (target == null)
                {
                    log?.Invoke($"    no match for version '{vs}' among {items.Count} combo items");
                    // Cancel rather than Open a wrong version, to avoid compare-to-self.
                    return false;
                }

                try
                {
                    string tn = target.Current.Name ?? "";
                    log?.Invoke($"    selecting '{tn}'");
                    if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sip2))
                        ((SelectionItemPattern)sip2).Select();
                    else
                        target.SetFocus();
                    Thread.Sleep(200);
                }
                catch (Exception ex) { log?.Invoke($"    select err: {ex.Message}"); }

                // Collapse the combo to commit the selection.
                try
                {
                    if (versionCombo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object ecp2))
                        ((ExpandCollapsePattern)ecp2).Collapse();
                }
                catch { }
                Thread.Sleep(150);

                // Step 3: click Open (AutomationId=2059) or fall back to WM_COMMAND IDOK.
                var openBtn = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "2059"));
                if (openBtn != null && openBtn.TryGetCurrentPattern(InvokePattern.Pattern, out object ip))
                {
                    ((InvokePattern)ip).Invoke();
                    log?.Invoke("    Open button invoked via UIA");
                }
                else
                {
                    log?.Invoke("    Open button not found via UIA, sending WM_COMMAND IDOK");
                    PostMessage(martDlg, WM_COMMAND, MakeWParam(IDOK, 0), IntPtr.Zero);
                }
                Thread.Sleep(600);
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"    SelectMartVersionInPicker threw: {ex.Message}");
                return false;
            }
        }

        private static bool ClickButtonByName(AutomationElement root, IEnumerable<string> names)
        {
            if (root == null) return false;
            try
            {
                var btnCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
                var buttons = root.FindAll(TreeScope.Descendants, btnCond);
                foreach (AutomationElement b in buttons)
                {
                    string bn = "";
                    try { bn = b.Current.Name ?? ""; } catch { }
                    foreach (var want in names)
                    {
                        if (bn.Equals(want, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                if (b.TryGetCurrentPattern(InvokePattern.Pattern, out object inv))
                                {
                                    ((InvokePattern)inv).Invoke();
                                    return true;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static void DumpUiaTree(AutomationElement el, int depth, int budget, Action<string> log)
        {
            if (el == null || depth > 10 || budget <= 0) return;
            try
            {
                string type = "";
                try { type = el.Current.ControlType?.ProgrammaticName ?? ""; } catch { }
                string name = "";
                try { name = el.Current.Name ?? ""; } catch { }
                string aid = "";
                try { aid = el.Current.AutomationId ?? ""; } catch { }
                string cls = "";
                try { cls = el.Current.ClassName ?? ""; } catch { }
                string indent = new string(' ', depth * 2);
                log?.Invoke($"  {indent}[{type}] name='{name}' id='{aid}' cls='{cls}'");
            }
            catch { }
            try
            {
                var children = el.FindAll(TreeScope.Children, Condition.TrueCondition);
                int used = 1;
                foreach (AutomationElement c in children)
                {
                    if (used >= budget) break;
                    DumpUiaTree(c, depth + 1, Math.Max(10, (budget - used) / Math.Max(1, children.Count - used)), log);
                    used++;
                }
            }
            catch { }
        }

        // ── Window enumeration ──────────────────────────────────────────────

        internal static IntPtr FindErwinMain()
        {
            IntPtr found = IntPtr.Zero;
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((hWnd, lp) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != myPid) return true;
                var cls = new StringBuilder(64);
                GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() == "XTPMainFrame")
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static string GetTitle(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static HashSet<IntPtr> EnumerateVisibleDialogs()
        {
            var set = new HashSet<IntPtr>();
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((hWnd, lp) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != myPid) return true;
                var cls = new StringBuilder(64);
                GetClassName(hWnd, cls, cls.Capacity);
                string c = cls.ToString();
                if (c == "#32770" || c == "XTPMainFrame" || c.StartsWith("Afx"))
                    set.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            return set;
        }

        private static IntPtr WaitForNewDialog(HashSet<IntPtr> before, string label, int timeoutMs, Action<string> log)
        {
            uint deadline = unchecked((uint)Environment.TickCount) + (uint)timeoutMs;
            while (unchecked((int)((uint)Environment.TickCount - deadline)) < 0)
            {
                Thread.Sleep(80);
                var now = EnumerateVisibleDialogs();
                foreach (var h in now)
                {
                    if (!before.Contains(h)) return h;
                }
            }
            log?.Invoke($"  [{label}] did not appear within {timeoutMs}ms");
            return IntPtr.Zero;
        }

        /// <summary>
        /// Spawns a background worker that polls for an "erwin Data Modeler"
        /// popup for up to <paramref name="timeoutMs"/> milliseconds. When
        /// the popup shows up, it clicks No/Hayır/Cancel/İptal via UIA,
        /// logs it, then terminates. Used around a blocking
        /// <c>pu.Close()</c> call that can raise a "Save changes?" prompt
        /// for a dirty PU: caller starts the watcher, invokes Close() on
        /// the UI thread, then awaits the watcher Task to settle.
        /// </summary>
        public static Task DismissErwinPopupInBackground(int timeoutMs, Action<string> log)
        {
            return Task.Run(() =>
            {
                int deadline = Environment.TickCount + timeoutMs;
                while (unchecked(Environment.TickCount - deadline) < 0)
                {
                    try
                    {
                        IntPtr popup = WaitForDialog("erwin Data Modeler", 200);
                        if (popup != IntPtr.Zero)
                        {
                            string title = GetTitle(popup);
                            log?.Invoke($"  [CLEANUP] popup '{title}' detected - dismissing with No");
                            try
                            {
                                var pop = AutomationElement.FromHandle(popup);
                                if (pop != null)
                                    ClickButtonByName(pop, new[] { "No", "Hayır", "Cancel", "İptal" });
                            }
                            catch (Exception ex)
                            {
                                log?.Invoke($"  [CLEANUP] dismiss err: {ex.Message}");
                            }
                            return;
                        }
                    }
                    catch { }
                    Thread.Sleep(100);
                }
            });
        }

        private static IntPtr WaitForDialog(string titleContains, int timeoutMs)
        {
            IntPtr result = IntPtr.Zero;
            uint deadline = unchecked((uint)Environment.TickCount) + (uint)timeoutMs;
            while (unchecked((int)((uint)Environment.TickCount - deadline)) < 0)
            {
                Thread.Sleep(100);
                uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                EnumWindows((hWnd, lp) =>
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid != myPid) return true;
                    string t = GetTitle(hWnd);
                    if (!string.IsNullOrEmpty(t) && t.IndexOf(titleContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        result = hWnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
                if (result != IntPtr.Zero) return result;
            }
            return IntPtr.Zero;
        }

        // ── Hide-wizard via LAYERED + alpha=0 ───────────────────────────────

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_LAYERED = 0x00080000;
        private const uint LWA_ALPHA = 0x00000002;

        internal static void HideWindow(IntPtr hWnd)
        {
            try
            {
                IntPtr ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(ex.ToInt64() | WS_EX_LAYERED));
                SetLayeredWindowAttributes(hWnd, 0, 0, LWA_ALPHA);
            }
            catch { }
        }

        /// <summary>
        /// Reverses <see cref="HideWindow"/>: clears the WS_EX_LAYERED bit so
        /// the window paints normally. Used during cleanup so any modal child
        /// dialog (e.g. a "Save changes?" prompt) that the wizard pops as
        /// part of closing is NOT hidden by inherited transparency, which
        /// would leave erwin's UI thread blocked behind an invisible modal
        /// for the user to discover via ESC.
        /// </summary>
        internal static void UnhideWindow(IntPtr hWnd)
        {
            try
            {
                IntPtr ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(ex.ToInt64() & ~WS_EX_LAYERED));
            }
            catch { }
        }

        /// <summary>
        /// Near-invisible window (alpha=1). Unlike <see cref="HideWindow"/>
        /// (alpha=0) which makes the window fully transparent and causes
        /// mouse input to pass through, alpha=1 keeps the window in the OS
        /// input dispatch table so synthetic mouse events still land on it.
        /// User sees essentially nothing (1% opacity).
        /// </summary>
        internal static void NearInvisibleWindow(IntPtr hWnd)
        {
            try
            {
                IntPtr ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE);
                SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(ex.ToInt64() | WS_EX_LAYERED));
                SetLayeredWindowAttributes(hWnd, 0, 1, LWA_ALPHA);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern int ShowCursor(bool bShow);

        /// <summary>
        /// Moves a window off-screen at (-20000, -20000) keeping its size and
        /// <c>WS_VISIBLE</c> style intact. Unlike <see cref="HideWindow"/> this
        /// does NOT use <c>WS_EX_LAYERED + alpha=0</c> - many XTP custom
        /// controls (listview hot-track arrows) only update their hit-test /
        /// paint state when the window is genuinely visible to the OS.
        /// </summary>
        internal static void MoveOffScreen(IntPtr hWnd)
        {
            try
            {
                if (!GetWindowRect(hWnd, out RECT r)) return;
                int w = r.right - r.left;
                int h = r.bottom - r.top;
                MoveWindow(hWnd, -20000, -20000, w, h, true);
            }
            catch { }
        }

        // ── ListView subitem click (for XTP arrow hot-track regions) ────────

        // Toolbar command query messages
        private const uint TB_COMMANDTOINDEX = 0x0419;   // WM_USER + 25
        private const uint TB_BUTTONCOUNT    = 0x0418;   // WM_USER + 24
        private const uint TB_GETBUTTON      = 0x0417;   // WM_USER + 23
        private const uint TB_GETRECT        = 0x0433;   // WM_USER + 51
        private const uint WM_NOTIFY         = 0x004E;
        private const int  NM_CLICK          = -2;
        private const int  BN_CLICKED        = 0;

        // x64 TBBUTTON layout: 4 ints + IntPtr (dwData) + IntPtr (iString)
        // = 8 + 8 + 8 + 4 + 4 = 32 bytes (with alignment).
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct TBBUTTON
        {
            public int    iBitmap;
            public int    idCommand;
            public byte   fsState;
            public byte   fsStyle;
            public byte   bReserved0;
            public byte   bReserved1;
            public byte   bReserved2;
            public byte   bReserved3;
            public byte   bReserved4;
            public byte   bReserved5;
            public IntPtr dwData;
            public IntPtr iString;
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageTbButton(IntPtr hWnd, uint Msg, IntPtr wParam, ref TBBUTTON lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NMITEMACTIVATE
        {
            public IntPtr hwndFrom;   // 8
            public IntPtr idFrom;     // 8
            public int    code;       // 4 (+4 pad)
            public int    pad0;
            public int    iItem;      // 4
            public int    iSubItem;   // 4
            public int    uNewState;
            public int    uOldState;
            public int    uChanged;
            public int    ptActionX;
            public int    ptActionY;
            public IntPtr lParam;
            public int    uKeyFlags;
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageNm(IntPtr hWnd, uint Msg, IntPtr wParam, ref NMITEMACTIVATE lParam);

        /// <summary>
        /// Synthesizes a listview NM_CLICK WM_NOTIFY on a specific row/col
        /// and sends it synchronously to <paramref name="targetDialog"/>.
        /// The exact struct the user's physical click generates (per hex
        /// dump analysis in bridge log).
        /// </summary>
        private static bool SendSynthesizedNmClick(
            IntPtr targetDialog, IntPtr listView,
            int idFrom, int iItem, int iSubItem, Action<string> log)
        {
            try
            {
                var nm = new NMITEMACTIVATE
                {
                    hwndFrom = listView,
                    idFrom = new IntPtr(idFrom),
                    code = NM_CLICK,
                    iItem = iItem,
                    iSubItem = iSubItem,
                };
                IntPtr rc = SendMessageNm(targetDialog, WM_NOTIFY, new IntPtr(idFrom), ref nm);
                log?.Invoke($"    WM_NOTIFY rc=0x{rc.ToInt64():X}");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"    SendSynthesizedNmClick threw: {ex.Message}");
                return false;
            }
        }
        private const uint WM_MOUSEMOVE      = 0x0200;
        private const uint WM_LBUTTONDOWN    = 0x0201;
        private const uint WM_LBUTTONUP      = 0x0202;
        private const int  MK_LBUTTON        = 0x0001;

        /// <summary>
        /// Finds the first ToolbarWindow32 descendant of <paramref name="parent"/>
        /// that contains a button whose command ID is <paramref name="cmdId"/>.
        /// Uses TB_COMMANDTOINDEX (returns index or -1 if not present).
        /// </summary>
        private static IntPtr FindToolbarContaining(IntPtr parent, int cmdId, Action<string> log)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, (h, _) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() == "ToolbarWindow32")
                {
                    IntPtr idx = SendMessage(h, TB_COMMANDTOINDEX, new IntPtr(cmdId), IntPtr.Zero);
                    if (idx.ToInt64() >= 0)
                    {
                        log?.Invoke($"    toolbar hwnd=0x{h.ToInt64():X} has cmd {cmdId} at index {idx.ToInt64()}");
                        found = h;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr hWnd);

        /// <summary>
        /// Finds the first SysListView32 descendant of <paramref name="parent"/>
        /// whose dialog control ID matches <paramref name="wantId"/>.
        /// </summary>
        private static IntPtr FindListViewById(IntPtr parent, int wantId, Action<string> log)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, (h, _) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() == "SysListView32")
                {
                    int id = GetDlgCtrlID(h);
                    if (log != null) log($"    candidate lv=0x{h.ToInt64():X} id={id}");
                    if (id == wantId)
                    {
                        found = h;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Finds the first SysListView32 child (recursively) under <paramref name="parent"/>.
        /// </summary>
        private static IntPtr FindListView(IntPtr parent)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, (h, _) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() == "SysListView32")
                {
                    found = h;
                    return false;   // stop enumeration
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Polls the native EDR transaction counter until it increments above
        /// <paramref name="txBefore"/> and then stays unchanged for at least
        /// <paramref name="stableMs"/> milliseconds, or until the overall
        /// timeout expires. Returns the final counter value.
        /// </summary>
        private static int WaitForEdrTxSettle(int txBefore, int timeoutMs, int stableMs, Action<string> log)
        {
            int deadline = Environment.TickCount + timeoutMs;
            int lastSeen = txBefore;
            int lastChangeTick = Environment.TickCount;

            while (unchecked(Environment.TickCount - deadline) < 0)
            {
                Thread.Sleep(80);
                int now = NativeBridgeService.GetEdrTxCount();
                if (now != lastSeen)
                {
                    lastSeen = now;
                    lastChangeTick = Environment.TickCount;
                }
                // Require AT LEAST one tx after the click, THEN stability.
                if (lastSeen > txBefore &&
                    unchecked(Environment.TickCount - lastChangeTick) >= stableMs)
                {
                    log?.Invoke($"    EDR tx settled: {txBefore} -> {lastSeen} (+{lastSeen - txBefore})");
                    return lastSeen;
                }
            }
            log?.Invoke($"    EDR tx wait TIMEOUT: {txBefore} -> {lastSeen} (no settle within {timeoutMs}ms)");
            return lastSeen;
        }

        /// <summary>
        /// Clicks an XTP custom-drawn listview subitem using OS-level mouse
        /// events. XTP hot-track regions ignore synthetic WM_LBUTTON* sent via
        /// PostMessage; they require a real <c>mouse_event</c> so the OS
        /// delivers a complete WM_MOUSEMOVE + button sequence and
        /// GetAsyncKeyState-style queries return the expected button state.
        ///
        /// Flow:
        ///   1. Get listview's subitem rect (LVM_GETSUBITEMRECT, client coords)
        ///   2. ClientToScreen -> screen coords (works even if window is
        ///      moved off-screen)
        ///   3. Save cursor, SetCursorPos to target (invisible if off-screen)
        ///   4. mouse_event LEFTDOWN + LEFTUP
        ///   5. Restore cursor
        /// </summary>
        private static bool ClickListViewSubItem(IntPtr dlgHwnd, int item, int subItem, int dx, int dy, Action<string> log)
        {
            IntPtr lv = FindListView(dlgHwnd);
            if (lv == IntPtr.Zero)
            {
                log?.Invoke("    ClickListViewSubItem: SysListView32 not found");
                return false;
            }
            log?.Invoke($"    ClickListViewSubItem: lv=0x{lv.ToInt64():X}");

            RECT rc = new RECT { left = LVIR_BOUNDS, top = subItem, right = 0, bottom = 0 };
            IntPtr rv = SendMessage(lv, LVM_GETSUBITEMRECT, new IntPtr(item), ref rc);
            if (rv == IntPtr.Zero)
            {
                log?.Invoke("    LVM_GETSUBITEMRECT returned 0");
                return false;
            }
            log?.Invoke($"    subitem rect (client) = ({rc.left},{rc.top})-({rc.right},{rc.bottom})");

            POINT pt = new POINT { X = rc.left + dx, Y = rc.top + dy };
            if (!ClientToScreen(lv, ref pt))
            {
                log?.Invoke("    ClientToScreen failed");
                return false;
            }
            log?.Invoke($"    click target (screen) = ({pt.X},{pt.Y})");

            if (!GetCursorPos(out POINT saved))
            {
                log?.Invoke("    GetCursorPos failed");
                return false;
            }

            try
            {
                SetCursorPos(pt.X, pt.Y);
                Thread.Sleep(30);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(40);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(80);
            }
            finally
            {
                SetCursorPos(saved.X, saved.Y);
            }
            log?.Invoke("    mouse_event LEFTDOWN/UP fired, cursor restored");
            return true;
        }
    }
}
