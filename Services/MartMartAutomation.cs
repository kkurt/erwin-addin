using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using EliteSoft.Erwin.AddIn.Forms;

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
        // GetWindowText P/Invoke removed 2026-06-03: every title read on this class
        // now routes through Win32Helper.GetWindowTextNoHang (SendMessageTimeout +
        // SMTO_ABORTIFHUNG) so a hung window can never freeze erwin's UI thread.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        private const uint GA_ROOT = 2;
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
        private const uint LVM_GETITEMTEXTW   = LVM_FIRST + 115;  // 0x1073

        /// <summary>
        /// Read the text of a single listview cell (item/subitem) via
        /// LVM_GETITEMTEXTW. Used to identify which row in the Resolve
        /// Differences listview is the "Model" / "Tables" container so we
        /// click on the right one (mart-mart had item=0 as the root, but
        /// DB-mart compare typically has a deeper tree).
        /// </summary>
        private static string GetListViewItemText(IntPtr lv, int item, int subItem)
        {
            try
            {
                var lvi = new LVITEM
                {
                    iItem = item,
                    iSubItem = subItem,
                    cchTextMax = 256,
                    pszText = Marshal.AllocHGlobal(512),
                };
                try
                {
                    SendMessageLVItem(lv, LVM_GETITEMTEXTW, new IntPtr(item), ref lvi);
                    return Marshal.PtrToStringUni(lvi.pszText) ?? "";
                }
                finally
                {
                    if (lvi.pszText != IntPtr.Zero) Marshal.FreeHGlobal(lvi.pszText);
                }
            }
            catch
            {
                return "";
            }
        }
        private const int LVIR_BOUNDS = 0;
        private const uint LVIF_STATE = 0x0008;
        private const uint LVIS_FOCUSED = 0x0001;
        private const uint LVIS_SELECTED = 0x0002;

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageLVItem(IntPtr hWnd, uint Msg, IntPtr wParam, ref LVITEM lParam);

        // ----- ComboBox (CB_*) + TreeView (TVM_*) messages for the Mart version
        // picker (catalog tree id=2054, version combo id=2111). The add-in runs
        // IN-PROCESS inside erwin.exe, so a managed AllocHGlobal buffer pointer is
        // valid in the control's address space (no VirtualAllocEx - same idiom as
        // GetListViewItemText). erwin is a Unicode build, so text is read as UTF-16.
        private const uint CB_GETCOUNT     = 0x0146;
        private const uint CB_GETLBTEXTLEN = 0x0149;
        private const uint CB_GETLBTEXT    = 0x0148;
        private const uint CB_SETCURSEL    = 0x014E;
        private const uint CB_SELECTSTRING = 0x014D;
        private const int  CBN_SELCHANGE   = 1;
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageCb(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        private const uint TVM_FIRST       = 0x1100;
        private const uint TVM_EXPAND      = TVM_FIRST + 2;    // 0x1102
        private const uint TVM_GETNEXTITEM = TVM_FIRST + 10;   // 0x110A
        private const uint TVM_SELECTITEM  = TVM_FIRST + 11;   // 0x110B
        private const uint TVM_GETITEMW    = TVM_FIRST + 62;   // 0x113E
        private const int  TVGN_ROOT   = 0x0000;
        private const int  TVGN_NEXT   = 0x0001;
        private const int  TVGN_CHILD  = 0x0004;
        private const int  TVGN_CARET  = 0x0009;
        private const int  TVE_EXPAND  = 0x0002;
        private const uint TVIF_TEXT   = 0x0001;
        private const uint TVIF_HANDLE = 0x0010;
        [StructLayout(LayoutKind.Sequential)]
        private struct TVITEM
        {
            public uint mask;
            public IntPtr hItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public int iSelectedImage;
            public int cChildren;
            public IntPtr lParam;
        }
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageTvItem(IntPtr hWnd, uint Msg, IntPtr wParam, ref TVITEM lParam);

        private static string GetTreeItemText(IntPtr tree, IntPtr hItem)
        {
            IntPtr buf = Marshal.AllocHGlobal(512);
            try
            {
                var ti = new TVITEM { mask = TVIF_TEXT | TVIF_HANDLE, hItem = hItem, pszText = buf, cchTextMax = 255 };
                SendMessageTvItem(tree, TVM_GETITEMW, IntPtr.Zero, ref ti);
                return Marshal.PtrToStringUni(buf) ?? "";
            }
            catch { return ""; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static string GetComboItemText(IntPtr combo, int index)
        {
            int len = SendMessageCb(combo, CB_GETLBTEXTLEN, new IntPtr(index), IntPtr.Zero).ToInt32();
            if (len <= 0 || len > 1024) return "";
            IntPtr buf = Marshal.AllocHGlobal((len + 1) * 2);
            try
            {
                SendMessageCb(combo, CB_GETLBTEXT, new IntPtr(index), buf);
                return Marshal.PtrToStringUni(buf) ?? "";
            }
            catch { return ""; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // The FE Alter Script wizard "Option Set" combo is id=1207, a standard
        // ComboBox (verified via Ctrl+Alt+D RECON 2026-06-03). When the active model
        // carries a STALE user-selected option-set FILE xml for a different DB version,
        // the wizard's Generate raises "XML File is not compatible for Forward
        // Engineering / The Database Versions are different". That error modal
        // interrupts the hidden-wizard teardown so the WS_EX_LAYERED surface is never
        // released -> the DWM black rectangles return. This selects the BUILT-IN
        // "Alter Schema Generation" option set (no file, DB-version-agnostic, always
        // compatible) in that case. Left UNTOUCHED: our own admin cfg
        // (erwin_addin_ddl_fe_opt_cfg*.xml applied via FEModel_DDL for the correct
        // version) and an already-default selection. Pure Win32, no UIA.
        private static void EnsureCompatibleOptionSet(IntPtr wizardHwnd, Action<string> log)
        {
            try
            {
                IntPtr combo = FindDescendantById(wizardHwnd, 1207);
                if (combo == IntPtr.Zero) { log?.Invoke("  [OPT-SET] Option Set combo (id=1207) not found - skip."); return; }
                var ccls = new StringBuilder(32);
                GetClassName(combo, ccls, ccls.Capacity);
                if (ccls.ToString().IndexOf("ComboBox", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    log?.Invoke($"  [OPT-SET] id=1207 not a ComboBox (class='{ccls}') - skip.");
                    return;
                }
                string current = (GetTitle(combo) ?? "").Trim();
                if (current.IndexOf("erwin_addin_ddl_fe_opt_cfg", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    log?.Invoke($"  [OPT-SET] combo on our admin cfg ('{current}') - compatible, leaving it.");
                    return;
                }
                if (current.StartsWith("Alter Schema", StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"  [OPT-SET] combo already on built-in default ('{current}') - leaving it.");
                    return;
                }
                IntPtr strPtr = Marshal.StringToHGlobalUni("Alter Schema Generation");
                try
                {
                    IntPtr idx = SendMessageCb(combo, CB_SELECTSTRING, new IntPtr(-1), strPtr);
                    if (idx.ToInt64() < 0)
                    {
                        log?.Invoke($"  [OPT-SET] 'Alter Schema Generation' not found in combo (current='{current}') - left as-is.");
                        return;
                    }
                    IntPtr parent = GetParent(combo);
                    PostMessage(parent == IntPtr.Zero ? wizardHwnd : parent, WM_COMMAND, MakeWParam(1207, CBN_SELCHANGE), combo);
                    log?.Invoke($"  [OPT-SET] stale option '{current}' -> selected built-in 'Alter Schema Generation' (idx {idx.ToInt64()}) + CBN_SELCHANGE. Prevents incompatible-XML error / black rectangles. NO UIA.");
                }
                finally { Marshal.FreeHGlobal(strPtr); }
            }
            catch (Exception ex) { log?.Invoke($"  [OPT-SET] threw: {ex.Message} - left as-is."); }
        }

        // Depth-first search of the tree (the start node, its siblings, and ALL
        // their descendants) for an item whose text == wanted. The Mart catalog
        // tree nests libraries under a "Mart" root, so a siblings-only search at
        // the root level misses "Kursat" (a CHILD of "Mart") - mirror the old UIA
        // FindFirst(Descendants). Lazy Mart nodes populate children only on expand,
        // so expand a node before descending if it reports no children yet. `budget`
        // (shared by ref) bounds the walk against a malformed/huge tree.
        private static IntPtr FindTreeItemByTextDeep(IntPtr tree, IntPtr start, string wanted, ref int budget)
        {
            IntPtr n = start;
            while (n != IntPtr.Zero && budget > 0)
            {
                budget--;
                if (string.Equals(GetTreeItemText(tree, n).Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return n;
                IntPtr child = SendMessage(tree, TVM_GETNEXTITEM, new IntPtr(TVGN_CHILD), n);
                if (child == IntPtr.Zero)
                {
                    SendMessage(tree, TVM_EXPAND, new IntPtr(TVE_EXPAND), n);   // lazy-load children
                    child = SendMessage(tree, TVM_GETNEXTITEM, new IntPtr(TVGN_CHILD), n);
                }
                if (child != IntPtr.Zero)
                {
                    IntPtr hit = FindTreeItemByTextDeep(tree, child, wanted, ref budget);
                    if (hit != IntPtr.Zero) return hit;
                }
                n = SendMessage(tree, TVM_GETNEXTITEM, new IntPtr(TVGN_NEXT), n);
            }
            return IntPtr.Zero;
        }

        // Pure-Win32 (no UIA) click of a #32770 dialog button by its visible text.
        // Replaces AutomationElement.FromHandle(popup)+ClickButtonByName(pop,...) whose
        // oleacc IAccessible RCWs crash erwin's finalizer at teardown. Finds the first
        // "Button"-class child whose caption (minus the & accelerator) matches one of
        // `names`, then dispatches WM_COMMAND with its control id (lParam=button HWND,
        // the XTP-safe form).
        private static bool ClickDialogButtonByTextWin32(IntPtr dlg, string[] names, Action<string> log)
        {
            if (dlg == IntPtr.Zero) return false;
            IntPtr btn = IntPtr.Zero;
            EnumChildWindows(dlg, (h, _) =>
            {
                var cls = new StringBuilder(32);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString().IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0) return true;
                string t = (GetTitle(h) ?? "").Replace("&", "").Trim();
                foreach (var nm in names)
                {
                    if (string.Equals(t, nm, StringComparison.OrdinalIgnoreCase)) { btn = h; return false; }
                }
                return true;
            }, IntPtr.Zero);
            if (btn == IntPtr.Zero)
            {
                log?.Invoke($"  [POP-WIN32] no button matching [{string.Join(",", names)}] found in dialog 0x{dlg.ToInt64():X}");
                return false;
            }
            int id = GetDlgCtrlID(btn);
            string label = (GetTitle(btn) ?? "").Replace("&", "").Trim();
            PostMessage(dlg, WM_COMMAND, MakeWParam(id, 0), btn);
            log?.Invoke($"  [POP-WIN32] clicked '{label}' (id={id}) via WM_COMMAND. NO UIA.");
            return true;
        }

        // Concatenates the text of every "Static"-class child of a dialog -
        // enough to recognize a message box by its body text without touching
        // UIA (which leaves crash-prone XTP IAccessible RCWs behind).
        private static string CollectDialogStaticText(IntPtr dlg)
        {
            if (dlg == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder();
            EnumChildWindows(dlg, (h, _) =>
            {
                var cls = new StringBuilder(32);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString().IndexOf("Static", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string t = GetTitle(h);
                    if (!string.IsNullOrWhiteSpace(t)) sb.Append(t).Append(' ');
                }
                return true;
            }, IntPtr.Zero);
            return sb.ToString();
        }

        // Public Win32 trigger of Mart > Review (cmd 1168) for the addin's manual
        // Review entry point, replacing Win32Helper.InvokeToolbarButton (UIA).
        public static bool PostMartReviewCommand(IntPtr erwinMain, Action<string> log)
        {
            if (erwinMain == IntPtr.Zero) return false;
            ForceForeground(erwinMain);
            PostMessage(erwinMain, WM_COMMAND, MakeWParam(CMD_MART_REVIEW, 0), IntPtr.Zero);
            log?.Invoke($"  Mart > Review posted (WM_COMMAND {CMD_MART_REVIEW}). NO UIA.");
            return true;
        }

        // RD's toolbars are standard Win32 ToolbarWindow32 controls
        // (verified by RAS-DUMP). Existing TB_BUTTONCOUNT/TB_GETBUTTON +
        // TBBUTTON struct + SendMessageTbButton are defined further below in
        // this file. Add only the TB_GETITEMRECT message and a couple of
        // overload helpers here so we can resolve the button rect without
        // colliding with the existing definitions.
        private const uint TB_GETITEMRECT = 0x41D;

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageInt(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageRectOut(IntPtr hWnd, uint Msg, IntPtr wParam, ref RECT lParam);

        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        private const uint GW_CHILD = 5;
        private const uint GW_HWNDNEXT = 2;
        private const uint WM_MDIGETACTIVE = 0x0229;
        private const uint WM_MDIACTIVATE_MSG = 0x0222;
        private const uint WM_CLOSE_MSG = 0x0010;
        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern bool ClipCursor(ref RECT lpRect);
        [DllImport("user32.dll", EntryPoint = "ClipCursor")]
        private static extern bool ClipCursorRelease(IntPtr lpRect);

        // Robust click primitive: confine the OS cursor to a small box around
        // the target so the user physically moving the mouse during the click
        // sequence (SetCursorPos -> sleep -> mouse_event DOWN -> sleep -> UP)
        // cannot displace the cursor enough to miss the target window.
        // Released automatically on process exit, plus we Release in finally
        // so a thrown call cannot trap the cursor. Used by every direct
        // mouse_event site in this file (SendMouseClickAt, the RAS toolbar
        // click, the SM-CLOSE uncheck, ClickListViewSubItem). Tightened from
        // the prior unprotected variant after a user-reported failure mode
        // 2026-05-30: physical mouse movement during Generate DDL broke the
        // pipeline chain.
        private static bool ClipCursorAround(int screenX, int screenY, int radius = 2)
        {
            RECT r = new RECT
            {
                left   = screenX - radius,
                top    = screenY - radius,
                right  = screenX + radius + 1,
                bottom = screenY + radius + 1,
            };
            try { return ClipCursor(ref r); } catch { return false; }
        }

        private static void ReleaseCursorClip()
        {
            try { ClipCursorRelease(IntPtr.Zero); } catch { }
        }
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT pt);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

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
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

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
        /// absolute coordinates over the VIRTUAL DESKTOP. The OS delivers
        /// the sequence atomically - no external process (including RDP
        /// cursor sync) can interrupt between steps.
        ///
        /// MOUSEEVENTF_VIRTUALDESK flag (NOT plain MOUSEEVENTF_ABSOLUTE)
        /// is critical for multi-monitor setups: without it, coordinates
        /// are normalized only against the PRIMARY monitor's 0..65535 range,
        /// so a click target on a secondary monitor (screenX > primary
        /// width) gets clamped to primary's right edge - the click misses.
        /// VIRTUALDESK normalizes against the entire virtual desktop
        /// rectangle reported by SM_X/Y/CX/CYVIRTUALSCREEN, which spans
        /// all monitors in their actual layout.
        /// </summary>
        private static void SendMouseClickAt(int screenX, int screenY)
        {
            SendMouseClickAt(screenX, screenY, log: null);
        }

        private static void SendMouseClickAt(int screenX, int screenY, Action<string> log)
        {
            // RAW path (proven multi-monitor): SetCursorPos accepts raw
            // screen coords (handles negative + secondary monitors
            // natively). mouse_event then fires DOWN/UP at the CURRENT
            // cursor position (target is implicit via cursor placement).
            // No 0..65535 normalization, no monitor-bound calculations.
            //
            // Robustness against user-input interference (2026-05-30):
            // wrap the entire click sequence in ClipCursor confined to a
            // small box around the target. Without this, the user
            // physically moving the mouse between SetCursorPos and either
            // mouse_event call (the prior ~70ms vulnerable window) would
            // route the synthetic DOWN/UP to the wrong window and snap
            // the multi-step pipeline (user-reported failure mode: chain
            // breaks when mouse is moved during Generate DDL). The clip
            // is released in finally so a thrown call cannot trap the
            // cursor; ClipCursor is also auto-released when the process
            // exits, so even a hard crash recovers cleanly. The dropped
            // 30ms post-SetCursorPos sleep is unnecessary now (the OS
            // call is synchronous + the clip pins the cursor anyway).
            POINT savedCursor;
            bool savedOk = GetCursorPos(out savedCursor);
            log?.Invoke($"    [click] target screen=({screenX},{screenY}) raw mouse_event path (clipped 5x5)");

            bool clipped = ClipCursorAround(screenX, screenY);
            try
            {
                SetCursorPos(screenX, screenY);
                if (GetCursorPos(out POINT actual))
                {
                    int dx = Math.Abs(actual.X - screenX);
                    int dy = Math.Abs(actual.Y - screenY);
                    if (dx > 5 || dy > 5)
                        log?.Invoke($"    [click] WARN cursor landed at ({actual.X},{actual.Y}) - delta=({dx},{dy})");
                }
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(40);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            }
            finally
            {
                if (clipped) ReleaseCursorClip();
                if (savedOk)
                {
                    try { SetCursorPos(savedCursor.X, savedCursor.Y); } catch { }
                }
            }
        }

        /// <summary>
        /// Force a window foreground + active from a BACKGROUND thread. Plain
        /// SetForegroundWindow fails from a non-foreground thread (Win32
        /// foreground-lock), which left the Save Models XTP dialog INACTIVE so
        /// its checkbox mouse-click silently no-op'd in production (verified
        /// 2026-05-29: prod left the save box checked -> v1 not closed). We
        /// AttachThreadInput to the window's UI thread to lift the lock, then
        /// detach in finally. Same-process (addin is COM-hosted in erwin.exe)
        /// so the attach is between two threads of one process.
        /// </summary>
        private static void ForceForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            uint myTid = GetCurrentThreadId();
            uint targetTid = GetWindowThreadProcessId(hWnd, out _);
            bool attached = false;
            try
            {
                if (targetTid != 0 && targetTid != myTid)
                    attached = AttachThreadInput(myTid, targetTid, true);
                try { UnhideWindow(hWnd); } catch { }
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
                SetActiveWindow(hWnd);
            }
            catch { }
            finally
            {
                if (attached) { try { AttachThreadInput(myTid, targetTid, false); } catch { } }
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
        private const int CMD_RD_ALTER_SCRIPT = 1056;    // Resolve Differences toolbar: LEFT Alter Script (From-DB direction)
        private const int CMD_RD_RIGHT_ALTER_SCRIPT = 1057; // RD toolbar: RIGHT Alter Script (enabled after Apply-to-Right; Review direction). 1056 is LEFT and DISABLED post-apply - verified by RD toolbar dump 2026-05-29
        // Mart ribbon command ids captured via Ctrl+Alt+C [RECON-CMD] (2026-06-03):
        // Mart > Open (open a version as its own MDI child) = 1060; Mart > Review
        // (active-dirty vs the open version) = 1168. Posting WM_COMMAND with these
        // replaces Win32Helper.InvokeToolbarButton, whose AutomationElement.FromHandle
        // scan of the XTP ribbon created CXTPAccessible IAccessible RCWs that, abandoned
        // to GC, crashed erwin's finalizer ~6s after teardown (dump erwin.exe.50436).
        private const int CMD_MART_OPEN_VERSION = 1060;
        private const int CMD_MART_REVIEW       = 1168;
        private const int IDCANCEL = 2;
        private const int IDOK     = 1;
        private const int IDYES    = 6;
        private const int IDNO     = 7;

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
            // The MDI child that was active when the pipeline started (the
            // user's dirty compare-LEFT model). Teardown re-activates it so
            // the main-window title agrees with the add-in's bound model
            // again (a wrong active tab reads as a user tab-switch).
            public IntPtr OriginalChild;
            // The Mart version the pipeline opened as compare RIGHT. Lets
            // teardown find a LATE-arriving version child by title when the
            // open-wait timed out before the child was detected (then
            // VersionChild is Zero but the window exists anyway).
            public int RightVersion;
            // True once the picker's Open was actually clicked - the only
            // state in which a late version child can exist at all. Teardown
            // must not title-scan otherwise (it could grab a child the USER
            // opened, not the pipeline).
            public bool OpenPosted;
            // True when erwin answered Mart > Review with its "There have
            // been no changes to model since it was checked out." info box
            // instead of the wizard (clean checked-out model). The caller
            // surfaces a precise error instead of the generic "did not reach
            // Resolve Differences".
            public bool ReviewRefusedNoChanges;
            // True once the Review/CC launch command was posted. Combined
            // with CCWizard==Zero it means the wizard may still appear LATE
            // (erwin builds the ;Duplicate=YES copy of the LEFT model before
            // showing the wizard - 6-15 s on a slow machine, observed
            // 2026-06-10 10:28/10:43). Teardown then watches for the late
            // wizard and cancels it so the Duplicate is released under our
            // control instead of dangling behind an abandoned wizard.
            public bool WizardLaunchPosted;
            // Dialog snapshot taken right before the wizard launch post; the
            // late-wizard watch diffs against it.
            public System.Collections.Generic.HashSet<IntPtr> DialogsBeforeWizard;
            // MDI children that existed BEFORE the pipeline opened anything.
            // The late-child title scan excludes these so a version tab the
            // user had open all along is never mistaken for pipeline residue.
            public System.Collections.Generic.HashSet<IntPtr> PreexistingChildren;
            // UI-Open path (Faz 2): the prior-version model opened as its own
            // MDI child to serve as the compare RIGHT side. Closed via
            // CloseMartMdiChild in cleanup (graceful WM_CLOSE - no PUs.Remove).
            public IntPtr VersionChild;
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

                IntPtr resolveDlg = WaitForResolveDifferencesHandlingTypeResolution(10000, log);
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
        /// Phase 2 (active-vs-older version): drive erwin's Mart &gt; REVIEW
        /// wizard to Resolve Differences. Review is used instead of Complete
        /// Compare because ONLY Review puts the active model WITH its unsaved
        /// (dirty) changes on the left; Complete Compare uses the last-saved
        /// state. Right side = the chosen older Mart version, selected via the
        /// same <see cref="SelectMartVersionInPicker"/> the CC flow uses.
        ///
        /// Mirrors <see cref="DriveCCToResolveDifferencesVisible"/> exactly
        /// except the LAUNCH step: instead of WM_COMMAND CMD_COMPLETE_COMPARE
        /// it clicks the ribbon "Review" button (the only entry that captures
        /// the dirty buffer). The post-launch wizard pages, command ids
        /// (Back/Next/Compare = 12323/12324/12325, Mart radio 1081, Load 1082)
        /// and the Mart picker are identical (verified via 2026-05-28 recon).
        ///
        /// Requires the active model to have changes - if it is clean, Review
        /// does not open and this returns null (caller should fall back to
        /// Complete Compare per the agreed routing).
        ///
        /// Returns a <see cref="CCSession"/> with the Resolve Differences
        /// handle on success, or null. Caller MUST pass the result to
        /// <see cref="CloseSession"/> for the Close Model + Mart Offline
        /// teardown that releases the loaded version.
        /// </summary>
        /// <summary>
        /// Drives erwin to a "Resolve Differences" comparing the CURRENT model
        /// (LEFT) against an older Mart version (RIGHT), then returns the
        /// CCSession (RD + wizard + opened version child). Two entry modes:
        /// useCompleteCompare=false -> "Review" toolbar (LEFT = the active model
        /// WITH its unsaved/dirty buffer; Faz 2); useCompleteCompare=true ->
        /// Actions "Complete Compare" via WM_COMMAND 1082 (LEFT = the current
        /// model's LAST-SAVED baseline; Faz 3). Everything else (open the RIGHT
        /// version as an MDI child, re-activate the current child so it is the
        /// compare LEFT, navigate to the Right Model page, select the version
        /// from the in-memory list, Compare) is identical for both.
        /// </summary>
        public static CCSession DriveCompareToResolveDifferences(
            int rightVersion, string catalogPath, bool keepVisible, bool useCompleteCompare, Action<string> log)
        {
            var session = new CCSession();
            try
            {
                IntPtr erwinMain = FindErwinMain();
                if (erwinMain == IntPtr.Zero) { log?.Invoke("  [REVIEW] erwin main window not found."); return null; }

                IntPtr mdiClient = FindMdiClientOf(erwinMain);
                if (mdiClient == IntPtr.Zero) { log?.Invoke("  [REVIEW] MDIClient not found."); return null; }

                // Capture the active dirty model (the compare LEFT) so we can
                // flip back to it after opening the version child.
                IntPtr dirtyChild = GetActiveMdiChild(mdiClient);
                log?.Invoke($"  [REVIEW] active dirty child = 0x{dirtyChild.ToInt64():X} title='{GetTitle(dirtyChild)}'");
                session.OriginalChild = dirtyChild;
                session.RightVersion = rightVersion;
                // ALL children, not just Mart-titled ones: the new-child wait
                // diffs handles title-independently (see WaitForNewMartMdiChild),
                // so a pre-existing local (non-Mart) child must be in the
                // snapshot or it would be misread as the freshly opened version.
                var beforeChildren = new System.Collections.Generic.HashSet<IntPtr>(EnumAllMdiChildHandles(mdiClient));
                session.PreexistingChildren = beforeChildren;

                // ---- STEP 1: open the older version as ITS OWN MDI child via
                // erwin's privileged main "Open" (not SCAPI, not the wizard
                // Load - this is what the user does manually; multiple versions
                // coexist). Reuses the catalog Open picker automation as-is.
                DebugMode.Pause($"about to open v{rightVersion} as its own MDI child (Mart > Open)", log);
                var dlgsBeforeOpen = EnumerateVisibleDialogs();
                log?.Invoke("  [REVIEW-1] Mart > Open (open older version as MDI child)");
                // Mart > Open via WM_COMMAND 1060 (RECON-captured) instead of
                // Win32Helper.InvokeToolbarButton (UIA on the XTP ribbon -> dangling
                // CXTPAccessible RCW -> finalizer crash). WaitForNewDialog below
                // verifies the picker actually opened.
                ForceForeground(erwinMain);
                PostMessage(erwinMain, WM_COMMAND, MakeWParam(CMD_MART_OPEN_VERSION, 0), IntPtr.Zero);
                log?.Invoke($"  [REVIEW] Mart > Open posted (WM_COMMAND {CMD_MART_OPEN_VERSION}). NO UIA.");
                IntPtr picker = WaitForNewDialog(dlgsBeforeOpen, "Open picker", 6000, log);
                if (picker == IntPtr.Zero) { log?.Invoke("  [REVIEW] Open picker did not appear."); return null; }
                if (!keepVisible) HideWindow(picker);
                if (!SelectMartVersionInPicker(picker, rightVersion, catalogPath, log))
                {
                    log?.Invoke($"  [REVIEW] picker select failed for v{rightVersion}.");
                    PostMessage(picker, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
                    return session;
                }
                // The picker's Open is now in flight - from here on a version
                // child may exist even if its detection below times out, so
                // teardown is allowed to title-scan for a late arrival.
                session.OpenPosted = true;
                // 30 s budget (was 8 s): on a cold erwin the Mart open of the
                // version copy exceeded 10 s and the abort left the half-loaded
                // PU behind (2026-06-10 02:36 incident). The wait returns as
                // soon as the child's Mart:// title lands, so warm runs are
                // not slowed down by the larger ceiling.
                IntPtr versionChild = WaitForNewMartMdiChild(mdiClient, beforeChildren, 30000, log);
                if (versionChild == IntPtr.Zero) { log?.Invoke("  [REVIEW] version MDI child did not appear after Open."); return session; }
                session.VersionChild = versionChild;
                log?.Invoke($"  [REVIEW-2] v{rightVersion} opened as MDI child 0x{versionChild.ToInt64():X}");
                LogChildDirty("after Open (read-only attempted if DebugMode)", session.VersionChild, log);
                DebugMode.Pause($"v{rightVersion} opened as MDI child", log);

                // ---- STEP 2: flip the active child back to the dirty model so
                // Review/CC defaults LEFT = active dirty.
                if (dirtyChild != IntPtr.Zero) ActivateMdiChildHandle(mdiClient, dirtyChild, log);
                DebugMode.Pause("re-activated dirty model as compare LEFT", log);

                // ---- STEP 3: launch the compare wizard and reach the Right
                // Model page. Review (Faz 2) uses the toolbar button; Complete
                // Compare (Faz 3) uses WM_COMMAND 1082 on the main frame. Both
                // open a wizard whose Right Model page we navigate to below.
                var dialogsBeforeReview = EnumerateVisibleDialogs();
                session.DialogsBeforeWizard = dialogsBeforeReview;
                if (useCompleteCompare)
                {
                    log?.Invoke("  [REVIEW-3] launching Complete Compare (WM_COMMAND 1082; LEFT = current model last-saved baseline)");
                    PostMessage(erwinMain, WM_COMMAND, MakeWParam(CMD_COMPLETE_COMPARE, 0), IntPtr.Zero);
                }
                else
                {
                    log?.Invoke("  [REVIEW-3] launching Mart > Review (LEFT = active dirty model)");
                    // Mart > Review via WM_COMMAND 1168 (RECON-captured) instead of
                    // UIA InvokeToolbarButton. WaitForNewDialog below verifies the
                    // wizard opened.
                    ForceForeground(erwinMain);
                    PostMessage(erwinMain, WM_COMMAND, MakeWParam(CMD_MART_REVIEW, 0), IntPtr.Zero);
                    log?.Invoke($"  [REVIEW] Mart > Review posted (WM_COMMAND {CMD_MART_REVIEW}). NO UIA.");
                }
                session.WizardLaunchPosted = true;
                // 30 s budget (was 6 s): before showing the wizard erwin first
                // builds the ;Duplicate=YES copy of the LEFT model, which took
                // 7-15 s on a cold machine (2026-06-10 10:28/10:43 runs). The
                // old 6 s timeout abandoned a wizard that then appeared late,
                // stayed open with its Duplicate PU alive, and the dangling
                // copy ultimately crashed erwin when the user closed the
                // wizard manually and the add-in later dispatched on the dead
                // PU. The refusal info box still arrives in <200 ms, so the
                // larger ceiling costs nothing on the clean-model path.
                IntPtr ccWizard = WaitForNewDialog(dialogsBeforeReview, useCompleteCompare ? "Complete Compare wizard" : "Review wizard", 30000, log);
                if (ccWizard == IntPtr.Zero)
                {
                    log?.Invoke(useCompleteCompare
                        ? "  [REVIEW] Complete Compare wizard did not open (WM_COMMAND 1082)."
                        : "  [REVIEW] wizard did not open - active model may have NO changes (Review needs dirty edits).");
                    return session;
                }

                // Validate the capture (2026-06-10): WaitForNewDialog returns
                // ANY new dialog. On a clean checked-out model erwin answers
                // Mart > Review with the "There have been no changes to model
                // since it was checked out." info box, which the old code
                // adopted as the wizard and then died 30+ s later at the Right
                // Model page probe (id=1083 not found) with a misleading
                // generic error. Recognize the box by its Static text, dismiss
                // it via its REAL OK button (an OK-only message box ignores
                // WM_COMMAND IDCANCEL - proven by the modal that survived the
                // 01:50 run's teardown and blocked the 01:51 run), then
                // RELAUNCH via Complete Compare (user requirement 2026-06-10:
                // a clean model MUST still compare against an older version).
                // erwin's refusal proves the open state equals the checked-out
                // baseline, so CC's LEFT (the current model's last-saved
                // state, no dirty precondition - the dormant Faz-3 entry,
                // built+tested 2026-05-29) is semantically the SAME compare.
                // The already-open v1 child and the whole downstream wizard
                // navigation are reused as-is.
                string staticText = CollectDialogStaticText(ccWizard);
                if (staticText.IndexOf("There have been no changes", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    log?.Invoke($"  [REVIEW] erwin REFUSED Review: '{staticText.Trim()}' (no changes since checkout) - dismissing the info box and relaunching via Complete Compare.");
                    if (!ClickDialogButtonByTextWin32(ccWizard, new[] { "OK", "Tamam" }, log))
                    {
                        IntPtr okBtn = GetDlgItem(ccWizard, IDOK);
                        PostMessage(ccWizard, WM_COMMAND, MakeWParam(IDOK, 0), okBtn);
                        log?.Invoke("  [REVIEW] OK button not found by text - posted WM_COMMAND IDOK as fallback.");
                    }
                    session.ReviewRefusedNoChanges = true;
                    Thread.Sleep(400); // let the info box finish closing before the next dialog snapshot

                    dialogsBeforeReview = EnumerateVisibleDialogs();
                    session.DialogsBeforeWizard = dialogsBeforeReview;
                    log?.Invoke("  [REVIEW-3b] launching Complete Compare (WM_COMMAND 1082; LEFT = current model last-saved baseline)");
                    ForceForeground(erwinMain);
                    PostMessage(erwinMain, WM_COMMAND, MakeWParam(CMD_COMPLETE_COMPARE, 0), IntPtr.Zero);
                    useCompleteCompare = true;
                    ccWizard = WaitForNewDialog(dialogsBeforeReview, "Complete Compare wizard", 30000, log);
                    if (ccWizard == IntPtr.Zero)
                    {
                        log?.Invoke("  [REVIEW] Complete Compare wizard did not open after the Review refusal relaunch.");
                        return session;
                    }
                }

                if (!keepVisible) HideWindow(ccWizard);
                session.CCWizard = ccWizard;
                DebugMode.Pause("Review wizard opened", log);

                for (int i = 0; i < 12; i++)
                {
                    string t = GetTitle(ccWizard);
                    if (t.StartsWith("Wizard Overview", StringComparison.OrdinalIgnoreCase)
                        || t.StartsWith("Overview", StringComparison.OrdinalIgnoreCase)) break;
                    PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_BACK, 0), IntPtr.Zero);
                    Thread.Sleep(100);
                }
                log?.Invoke("  [REVIEW-4] Overview + Next x2 -> Right Model");
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero); Thread.Sleep(200);
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero); Thread.Sleep(400);
                DebugMode.Pause("reached Right Model page", log);

                // ---- STEP 4: select the opened version from "Open Models in
                // Memory" (id=1083) - NO catalog Load, since it's already an
                // in-session MDI child (same shape as the From-DB pipeline).
                if (!SelectInMemoryModelOnRightPage(ccWizard, rightVersion, log))
                {
                    log?.Invoke($"  [REVIEW] could not select v{rightVersion} from in-memory list.");
                    return session;
                }
                LogChildDirty("after in-memory select", session.VersionChild, log);
                DebugMode.Pause($"selected v{rightVersion} from in-memory list (no Load)", log);

                // ---- STEP 5: Compare -> Resolve Differences.
                LogChildDirty("before CC_COMPARE", session.VersionChild, log);
                log?.Invoke("  [REVIEW-5] Compare");
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_COMPARE, 0), IntPtr.Zero);
                Thread.Sleep(800);

                IntPtr popup = WaitForDialog("erwin Data Modeler", 1500);
                if (popup != IntPtr.Zero && popup != ccWizard)
                {
                    try
                    {
                        ClickDialogButtonByTextWin32(popup, new[] { "No", "Hayır", "Cancel", "İptal" }, log);
                    }
                    catch { }
                    log?.Invoke("  [REVIEW] unexpected popup after Compare - dismissed (Win32); aborting.");
                    return session;
                }

                IntPtr rd = WaitForResolveDifferencesHandlingTypeResolution(10000, log);
                if (rd == IntPtr.Zero) { log?.Invoke("  [REVIEW] Resolve Differences did not open."); return session; }
                log?.Invoke($"  [REVIEW] Resolve Differences = 0x{rd.ToInt64():X}");
                session.ResolveDifferences = rd;
                LogChildDirty("after Resolve Differences", session.VersionChild, log);
                DebugMode.Pause("Resolve Differences opened (active dirty vs selected version)", log);
                return session;
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [REVIEW] threw: {ex.GetType().Name}: {ex.Message}");
                return (session.CCWizard != IntPtr.Zero || session.VersionChild != IntPtr.Zero) ? session : null;
            }
        }

        /// <summary>
        /// SHARED Apply-to-Right primitive used by both the From-DB
        /// (DriveCCDbAndApply) and Review (ApplyToRightArrowOnReviewRd)
        /// pipelines. Synthesizes a real mouse click on the RD listview's
        /// row-0 Apply-to-Right arrow (id=200, subitem 6), retries up to 2
        /// times, waits for the EDR transaction to settle, then waits for
        /// the RIGHT Alter Script toolbar button (cmd 1057 =
        /// <see cref="CMD_RD_RIGHT_ALTER_SCRIPT"/>) to report TBSTATE_ENABLED
        /// so the caller's 1057 click does not no-op on a still-disabled
        /// button. Returns true iff the click registered an EDR delta.
        ///
        /// XTP filters synthetic WM_LBUTTON*, so a raw mouse_event
        /// (SetCursorPos + down/up via <see cref="SendMouseClickAt(int, int, Action{string})"/>)
        /// is the only path; the 2-attempt retry covers a first-click miss
        /// before the listview hot-track zone settles.
        ///
        /// Caller is responsible for OUTER bracketing (splash/overlayToggle
        /// on/off, pre-call foreground/visibility prep, any final post-call
        /// hide / restore) - this helper only does per-attempt
        /// SetForegroundWindow + optional per-attempt HideWindow.
        /// </summary>
        /// <param name="rd">Resolve Differences dialog HWND.</param>
        /// <param name="logPrefix">Per-line tag (e.g. "DB-7", "REVIEW-APPLY")
        /// prepended inside square brackets to every log line.</param>
        /// <param name="log">Log sink.</param>
        /// <param name="hideAfterClick">If true, <see cref="HideWindow"/>
        /// immediately after each mouse click and <see cref="UnhideWindow"/>
        /// before retry. Cuts ~500ms of perceptible flash per attempt - the
        /// From-DB pattern. If false, leave RD as-is and let the caller hide
        /// at the end - the Review pattern.</param>
        /// <param name="dumpItems">If true, log the first 8 listview rows
        /// (col0/col4/col7 + arrow rect) before clicking. Useful for the
        /// deeper DB-vs-Mart tree (From-DB); disabled for the flat
        /// active-vs-version compare (Review).</param>
        /// <param name="pollMaxMs">Listview-ready poll cap (typical 800-1500ms).</param>
        /// <param name="pollStep">Listview-ready poll interval (typical 5-50ms).</param>
        private static bool ApplyToRightArrowAndWaitForRas(
            IntPtr rd, string logPrefix, Action<string> log,
            bool hideAfterClick, bool dumpItems,
            int pollMaxMs, int pollStep)
        {
            if (rd == IntPtr.Zero || !IsWindow(rd)) { log?.Invoke($"  [{logPrefix}] RD invalid"); return false; }

            // Poll the RD listview (id=200) until it has >=1 item AND the
            // row-0 arrow subitem rect is non-zero. Clicking too early fires
            // a bogus EDR tx that makes OnFE emit "No schema to generate".
            IntPtr lv = IntPtr.Zero;
            int polled = 0;
            while (polled < pollMaxMs)
            {
                lv = FindListViewById(rd, 200, log: null);
                if (lv != IntPtr.Zero)
                {
                    int cnt = SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    if (cnt > 0)
                    {
                        RECT testRc = new RECT { left = LVIR_BOUNDS, top = 6, right = 0, bottom = 0 };
                        SendMessage(lv, LVM_GETSUBITEMRECT, new IntPtr(0), ref testRc);
                        if (testRc.right > testRc.left && testRc.bottom > testRc.top)
                        {
                            log?.Invoke($"  [{logPrefix}] listview ready after {polled}ms (items={cnt}, arrow rect ok)");
                            break;
                        }
                    }
                }
                Thread.Sleep(pollStep);
                polled += pollStep;
            }
            if (lv == IntPtr.Zero) { log?.Invoke($"  [{logPrefix}] listview id=200 not found within {pollMaxMs}ms"); return false; }

            if (dumpItems)
            {
                int total = SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                int dumpCount = Math.Min(total, 8);
                log?.Invoke($"  [{logPrefix}] [LV-DUMP] first {dumpCount} of {total} item(s):");
                for (int i = 0; i < dumpCount; i++)
                {
                    string c0 = GetListViewItemText(lv, i, 0);
                    string c4 = GetListViewItemText(lv, i, 4);   // left model name col
                    string c7 = GetListViewItemText(lv, i, 7);   // right model name col
                    RECT rrc = new RECT { left = LVIR_BOUNDS, top = 6, right = 0, bottom = 0 };
                    SendMessage(lv, LVM_GETSUBITEMRECT, new IntPtr(i), ref rrc);
                    log?.Invoke($"    item[{i}] col0='{c0}' col4='{c4}' col7='{c7}' arrow=({rrc.left},{rrc.top})-({rrc.right},{rrc.bottom})");
                }
            }

            RECT rc = new RECT { left = LVIR_BOUNDS, top = 6, right = 0, bottom = 0 };
            SendMessage(lv, LVM_GETSUBITEMRECT, new IntPtr(0), ref rc);
            POINT pt = new POINT { X = (rc.left + rc.right) / 2, Y = (rc.top + rc.bottom) / 2 };
            if (!ClientToScreen(lv, ref pt)) { log?.Invoke($"  [{logPrefix}] ClientToScreen failed"); return false; }
            log?.Invoke($"  [{logPrefix}] click target (screen) = ({pt.X},{pt.Y})");

            int txBefore = NativeBridgeService.GetEdrTxCount();
            if (!GetCursorPos(out POINT saved)) { log?.Invoke($"  [{logPrefix}] GetCursorPos failed"); return false; }

            int txAfter = txBefore;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                SetForegroundWindow(rd);
                try
                {
                    ShowCursor(false);
                    SendMouseClickAt(pt.X, pt.Y, log);
                    Thread.Sleep(20);
                }
                finally
                {
                    SetCursorPos(saved.X, saved.Y);
                    ShowCursor(true);
                }
                if (hideAfterClick) { try { HideWindow(rd); } catch { } }
                log?.Invoke($"  [{logPrefix}] attempt {attempt}: click fired{(hideAfterClick ? " + RD hidden" : "")}, waiting for EDR tx settle");
                txAfter = WaitForEdrTxSettle(txBefore, timeoutMs: 2500, stableMs: 350, log);
                if (txAfter > txBefore) { log?.Invoke($"  [{logPrefix}] attempt {attempt}: tx delta = {txAfter - txBefore}"); break; }
                log?.Invoke($"  [{logPrefix}] attempt {attempt}: no tx - retrying...");
                if (hideAfterClick) { try { UnhideWindow(rd); } catch { } }
                Thread.Sleep(100);
            }

            if (txAfter <= txBefore)
            {
                log?.Invoke($"  [{logPrefix}] [WARN] click did not trigger EDR tx after 2 attempts.");
                return false;
            }

            // After Apply-to-Right, RIGHT Alter Script (cmd 1057) becomes
            // enabled and LEFT (1056) goes DISABLED. Wait for 1057 here so
            // the caller's 1057 click does not no-op on a still-disabled
            // toolbar button (also dumps every RD toolbar button's enabled
            // state as [RAS-WAIT] for diagnostic).
            WaitForRdAlterScriptEnabled(rd, CMD_RD_RIGHT_ALTER_SCRIPT, log);
            return true;
        }

        /// <summary>
        /// Review-path Apply-to-Right: thin wrapper around
        /// <see cref="ApplyToRightArrowAndWaitForRas"/>. Cascades the active
        /// dirty v2 (LEFT) changes onto the older version v1 (RIGHT) on the
        /// Resolve Differences dialog. Returns true iff the click fired an
        /// EDR transaction (the precondition for the caller's 1057 click).
        ///
        /// Caller-specific bracketing kept here: brings RD foreground +
        /// unhides it BEFORE the helper measures + clicks (in production
        /// RD may be hidden/layered, so an unprepped mouse_event would miss);
        /// re-hides it AFTER on success when not in debug-visible mode (the
        /// helper itself does not hide-per-attempt for the Review pattern).
        /// </summary>
        public static bool ApplyToRightArrowOnReviewRd(IntPtr rd, Action<string> log)
        {
            if (rd == IntPtr.Zero || !IsWindow(rd)) { log?.Invoke("  [REVIEW-APPLY] RD invalid"); return false; }
            try
            {
                // Pre-helper foreground prep: in production RD may be hidden
                // or layered (matches ClickRightAlterScriptInRd's unhide).
                try { BringWindowToTop(rd); SetForegroundWindow(rd); UnhideWindow(rd); } catch { }
                Thread.Sleep(60);

                bool applied = ApplyToRightArrowAndWaitForRas(
                    rd, "REVIEW-APPLY", log,
                    hideAfterClick: false, dumpItems: false,
                    pollMaxMs: 1500, pollStep: 50);

                // Re-hide RD in production (reduce flash). ClickRightAlterScript
                // re-unhides it for the 1057 click. NOTE: this now runs AFTER
                // WaitForRdAlterScriptEnabled (inside the helper) rather than
                // before, so RD stays visible an extra ~0-300ms vs the prior
                // hand-written body; acceptable trade-off for one source of
                // truth across both pipelines.
                if (applied && !DebugMode.KeepDialogsVisible) { try { HideWindow(rd); } catch { } }
                return applied;
            }
            catch (Exception ex) { log?.Invoke($"  [REVIEW-APPLY] threw: {ex.GetType().Name}: {ex.Message}"); return false; }
        }

        /// <summary>
        /// Polls the RD's toolbar(s) until the "Right Alter Script" button
        /// (cmd 1056 = <see cref="CMD_RD_ALTER_SCRIPT"/>) reports TBSTATE_ENABLED
        /// (0x04), up to ~3s. erwin leaves it disabled for ~100-200ms after an
        /// Apply-to-Right, so clicking it immediately is a visible no-op that
        /// never opens the FE Alter Script wizard. On the first poll it also
        /// DUMPS every toolbar button (cmd + state) so the log reveals the full
        /// button set + which are enabled - confirming 1056 is the right button
        /// and whether it was disabled. Returns true once 1056 is enabled,
        /// false on timeout (caller proceeds anyway). Diagnostic-only enumerate;
        /// posts nothing.
        /// </summary>
        private static bool WaitForRdAlterScriptEnabled(IntPtr rd, int cmdId, Action<string> log)
        {
            const byte TBSTATE_ENABLED = 0x04;
            const byte TBSTATE_HIDDEN = 0x08;
            const byte TBSTYLE_SEP = 0x01;
            bool dumped = false;
            for (int attempt = 0; attempt < 12; attempt++)   // ~12 x 250ms = 3s
            {
                bool found = false, enabled = false;
                EnumChildWindows(rd, (tb, _) =>
                {
                    var cls = new StringBuilder(64);
                    GetClassName(tb, cls, cls.Capacity);
                    if (cls.ToString() != "ToolbarWindow32") return true;
                    int count = SendMessage(tb, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var tbb = new TBBUTTON();
                        SendMessageTbButton(tb, TB_GETBUTTON, new IntPtr(i), ref tbb);
                        bool isEnabled = (tbb.fsState & TBSTATE_ENABLED) != 0;
                        if (!dumped)
                        {
                            bool isSep = (tbb.fsStyle & TBSTYLE_SEP) != 0;
                            bool isHidden = (tbb.fsState & TBSTATE_HIDDEN) != 0;
                            log?.Invoke($"  [RAS-WAIT] tb=0x{tb.ToInt64():X} btn[{i}] cmdId={tbb.idCommand} state=0x{tbb.fsState:X2} style=0x{tbb.fsStyle:X2}{(isSep ? " SEP" : "")}{(isHidden ? " HIDDEN" : "")}{(isEnabled ? " ENABLED" : " disabled")}");
                        }
                        if (tbb.idCommand == cmdId) { found = true; if (isEnabled) enabled = true; }
                    }
                    return true;
                }, IntPtr.Zero);
                dumped = true;
                if (found && enabled)
                {
                    log?.Invoke($"  [RAS-WAIT] cmd {cmdId} ENABLED after {attempt * 250}ms");
                    Thread.Sleep(200);   // small extra settle once enabled
                    return true;
                }
                Thread.Sleep(250);
            }
            log?.Invoke($"  [RAS-WAIT] cmd {cmdId} not enabled within 3s; proceeding anyway (see button dump above)");
            return false;
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
        /// From-DB variant of <see cref="DriveCCAndApplyAsync"/>: caller has
        /// already silent-RE'd a fresh PU (named <paramref name="reModelName"/>)
        /// into the session, and the dirty mart MDI child has been
        /// re-activated. Mirrors DriveCCAndApply exactly EXCEPT for Step 3-4:
        /// instead of "From Mart" radio + Mart picker, we pick the RE'd PU
        /// from the "Open Models in Memory" DataGrid (id=1083) on the Right
        /// Model page. Everything from Step 5 onward (Compare, RD wait,
        /// listview poll, Apply-to-Right click, RD hide, EDR settle) is
        /// identical to the Mart-Mart pipeline.
        /// </summary>
        public static async Task<CCSession> DriveCCDbAndApplyAsync(string reModelName,
            Action<string> log, Action<bool> overlayToggle = null, bool dbgPauseBeforeApply = false)
        {
            return await Task.Run(() => DriveCCDbAndApply(reModelName, log, overlayToggle, dbgPauseBeforeApply));
        }

        // ---------------------------------------------------------------------
        // Architecture 2: drive Forward Engineer Alter Script wizard directly.
        //
        // Rationale: the CC-wizard pipeline requires synthesizing a mouse click
        // on the Resolve Differences listview's transfer arrow. XTP filters
        // synthetic input intermittently (verified 2026-04-26/27 multi-monitor
        // failures). The Apply-to-Right click is the ONLY mouse simulation in
        // the entire pipeline; everything else is WM_COMMAND posts.
        //
        // The Forward Engineer Alter Script wizard, opened via menu/ribbon
        // command 1161 / 61631 on the XTPMainFrame, calls ELA::OnFE +
        // FEProcessor::GenerateAlterScript natively as part of its Next /
        // Generate flow - the bridge's existing GenerateAlterScript hook fires
        // automatically when erwin invokes the wizard. No mouse clicks
        // anywhere in the pipeline.
        //
        // Smoke test (Phase 0): open wizard, Next-loop until DDL is captured
        // by the bridge or until Next-button stalls, then IDCANCEL the wizard.
        // No baseline selection - whatever the wizard's first-page default is
        // becomes the baseline (typically "compare to last saved version" for
        // a Mart-opened model).
        // ---------------------------------------------------------------------

        private const int CMD_FE_ALTER_SCRIPT_RIBBON = 61631;
        private const int CMD_FE_ALTER_SCRIPT_MENU   = 1161;
        private const int CMD_FE_WIZARD_NEXT         = 1766;
        private const int CMD_FE_WIZARD_BACK         = 1767;
        private const int CMD_FE_WIZARD_GENERATE     = 1760;

        // Recursive UIA tree dump for discovery probes. Logs each visible
        // control with its ControlType / Name / AutomationId / ClassName /
        // IsEnabled. Used to map unknown wizards before writing automation
        // code against them. maxDepth defaults to 3 because erwin dialogs
        // sometimes have 200+ descendants and a deeper dump fills the log
        // without adding signal.
        private static void DumpUiaTree(IntPtr hwnd, string label, Action<string> log, int maxDepth = 3)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null)
                {
                    log?.Invoke($"  [UIA] {label}: AutomationElement.FromHandle returned null");
                    return;
                }
                log?.Invoke($"  [UIA] === {label} ===");
                int dumped = 0;
                DumpUiaNode(root, log, 0, maxDepth, ref dumped, hardCap: 120);
                log?.Invoke($"  [UIA] === end of {label} (dumped {dumped} node(s)) ===");
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [UIA] {label} threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // DIAGNOSTIC (2026-05-29): dump every UIA descendant of the FE Alter
        // Script wizard WITH its screen BoundingRectangle + clickable point, so
        // we can see whether the left-nav page items ("Overview" / "Option
        // Selection" / ... / "Preview") are exposed as targetable elements, and
        // if not, learn the "Navigational Pane" container rect to compute the
        // "Preview" item's pixel position for a mouse-sim jump. Read-only.
        private static void DumpWizardNavProbe(IntPtr wizardHwnd, Action<string> log)
        {
            try
            {
                var root = AutomationElement.FromHandle(wizardHwnd);
                if (root == null)
                {
                    log?.Invoke("  [NAVPROBE] FromHandle null");
                    return;
                }
                AutomationElementCollection all;
                try { all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition); }
                catch (Exception ex) { log?.Invoke($"  [NAVPROBE] FindAll threw: {ex.Message}"); return; }

                log?.Invoke($"  [NAVPROBE] === wizard descendants ({all.Count}) ===");
                int i = 0;
                foreach (AutomationElement el in all)
                {
                    if (i++ > 200) { log?.Invoke("  [NAVPROBE] cap 200 reached"); break; }
                    string ctype = "?", name = "", aid = "", cls = "";
                    bool enabled = false, offscreen = false;
                    string rectStr = "?", clickStr = "no-click";
                    try
                    {
                        var c = el.Current;
                        ctype = c.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "?";
                        name = c.Name ?? "";
                        aid = c.AutomationId ?? "";
                        cls = c.ClassName ?? "";
                        enabled = c.IsEnabled;
                        offscreen = c.IsOffscreen;
                        var r = c.BoundingRectangle;
                        rectStr = $"({(int)r.Left},{(int)r.Top} {(int)r.Width}x{(int)r.Height})";
                    }
                    catch { }
                    try
                    {
                        if (el.TryGetClickablePoint(out System.Windows.Point p))
                            clickStr = $"clk=({(int)p.X},{(int)p.Y})";
                    }
                    catch { }
                    if (name.Length > 60) name = name.Substring(0, 57) + "...";
                    log?.Invoke($"  [NAVPROBE] [{ctype}] name='{name}' aid='{aid}' cls='{cls}' rect={rectStr} {clickStr} en={enabled} off={offscreen}");
                }
                log?.Invoke("  [NAVPROBE] === end ===");
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [NAVPROBE] threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Returns true when any UIA Text descendant under the given window
        // contains the given substring (case-insensitive). Used to identify
        // confirmation popups by their static-text content rather than just
        // the window title (since erwin reuses the title "erwin Data Modeler"
        // for many distinct prompts).
        private static bool DialogTextContains(IntPtr hwnd, string substring)
        {
            if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(substring)) return false;
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return false;
                var texts = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                foreach (AutomationElement t in texts)
                {
                    string n = null;
                    try { n = t.Current.Name; } catch { }
                    if (!string.IsNullOrEmpty(n) &&
                        n.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static void DumpUiaNode(AutomationElement el, Action<string> log,
                                        int depth, int maxDepth,
                                        ref int dumped, int hardCap)
        {
            if (depth > maxDepth) return;
            if (dumped >= hardCap) return;
            string indent = new string(' ', depth * 2 + 2);
            string ctype = "?", name = "", aid = "", cls = "";
            bool enabled = false;
            try
            {
                var info = el.Current;
                ctype = info.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "?";
                name = info.Name ?? "";
                aid = info.AutomationId ?? "";
                cls = info.ClassName ?? "";
                enabled = info.IsEnabled;
            }
            catch { }
            // Truncate over-long names so the log stays readable.
            if (name.Length > 80) name = name.Substring(0, 77) + "...";
            log?.Invoke($"  [UIA]{indent}[{ctype}] name='{name}' aid='{aid}' cls='{cls}' en={enabled}");
            dumped++;
            try
            {
                foreach (AutomationElement child in el.FindAll(TreeScope.Children, Condition.TrueCondition))
                {
                    if (dumped >= hardCap) return;
                    DumpUiaNode(child, log, depth + 1, maxDepth, ref dumped, hardCap);
                }
            }
            catch { }
        }

        private static CCSession DriveCCDbAndApply(string reModelName, Action<string> log, Action<bool> overlayToggle, bool dbgPauseBeforeApply = false)
        {
            var session = new CCSession();
            // Auto-dismiss GDM-1001 popups in the background for the entire
            // pipeline. Empirically, after silent RE the CC engine validates
            // referenced objects and pops blocking popups that prevent the
            // CC wizard window from being created (we observed 15s waits
            // with 0 window CREATE events). The dismisser keeps clicking
            // OK so erwin's UI thread stays unblocked.
            var gdmCts = new System.Threading.CancellationTokenSource();
            var gdmDismisser = DismissGdmPopupsContinuous(gdmCts.Token, log);
            try
            {
                IntPtr erwinMain = FindErwinMain();
                if (erwinMain == IntPtr.Zero)
                {
                    log?.Invoke("  erwin main window not found.");
                    return null;
                }

                var dialogsBefore = EnumerateVisibleDialogs();

                // Step 1: open CC wizard. (Diagnostic monitor hook removed
                // 2026-05-07: NativeBridgeService.MonitorHookInstall /
                // Uninstall were dead-code-pruned with the Debug Log tab;
                // this path keeps full functionality without them, only
                // [MONITOR] CREATE traces in the bridge log are gone.)
                log?.Invoke("  [DB-1] posting CMD_COMPLETE_COMPARE (1082) to main frame");
                PostMessage(erwinMain, WM_COMMAND, MakeWParam(CMD_COMPLETE_COMPARE, 0), IntPtr.Zero);
                // Longer timeout: post-RE state + GDM popup cascade can
                // take 5-10s before the wizard window finally appears.
                IntPtr ccWizard = WaitForNewDialog(dialogsBefore, "CC wizard", 20000, log);
                if (ccWizard == IntPtr.Zero)
                {
                    log?.Invoke("  [DB-1] CC wizard never appeared.");
                    return null;
                }
                HideWindow(ccWizard);
                session.CCWizard = ccWizard;
                log?.Invoke($"  [DB-1] CC wizard = 0x{ccWizard.ToInt64():X}");

                // Step 2: Back to Overview, then Next x2 to reach Right Model page
                for (int i = 0; i < 12; i++)
                {
                    string t = GetTitle(ccWizard);
                    if (t.StartsWith("Wizard Overview", StringComparison.OrdinalIgnoreCase)
                        || t.StartsWith("Overview", StringComparison.OrdinalIgnoreCase)) break;
                    PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_BACK, 0), IntPtr.Zero);
                    Thread.Sleep(80);
                }
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero); Thread.Sleep(150);
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero); Thread.Sleep(300);
                log?.Invoke($"  [DB-2] at Right Model page, title='{GetTitle(ccWizard)}'");

                // Step 3: select the RE'd model in the "Open Models in Memory"
                // list (control id=1083) via PURE WIN32 (no UI Automation). That
                // list is a standard SysListView32 hosted inside the XTP wizard;
                // the old UIA path (FromHandle + FindFirst DataItem +
                // SelectionItemPattern) wrapped its items as oleacc / IAccessible
                // RCWs that, abandoned to GC, crashed erwin's finalizer at teardown
                // (full dump erwin.exe.50160.dmp = oleacc!AccWrap_Base::Release AV;
                // UIA elements are cached so the GC drain cannot release them - they
                // MUST be eliminated, not drained). Find the list by id, locate the
                // row whose text == reModelName, then select it via LVM_SETITEMSTATE
                // + a synthesized NM_CLICK - the exact Win32 technique Apply-to-Right
                // already uses on the RD listview (FindListViewById +
                // SendSynthesizedNmClick).
                IntPtr openList = FindListViewById(ccWizard, 1083, log: null);
                if (openList == IntPtr.Zero)
                {
                    IntPtr probe = FindDescendantById(ccWizard, 1083);
                    var pcls = new StringBuilder(64);
                    if (probe != IntPtr.Zero) GetClassName(probe, pcls, pcls.Capacity);
                    log?.Invoke($"  [DB-3] 'Open Models in Memory' SysListView32 (id=1083) not found via Win32 (probe id=1083 hwnd=0x{probe.ToInt64():X} class='{pcls}')");
                    return session;
                }
                int reRow = -1;
                int rowTotal = SendMessage(openList, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                for (int i = 0; i < rowTotal && reRow < 0; i++)
                {
                    // Model name may be in col 0 or a later column depending on the
                    // grid layout; scan the first few subitems for an exact match.
                    for (int s = 0; s <= 3; s++)
                    {
                        string cell = GetListViewItemText(openList, i, s)?.Trim();
                        if (!string.IsNullOrEmpty(cell)
                            && string.Equals(cell, reModelName, StringComparison.OrdinalIgnoreCase))
                        { reRow = i; break; }
                    }
                }
                if (reRow < 0)
                {
                    log?.Invoke($"  [DB-3] row name='{reModelName}' not found in Open Models list ({rowTotal} rows) - Win32 scan");
                    return session;
                }
                var lvi = new LVITEM { stateMask = LVIS_SELECTED | LVIS_FOCUSED, state = LVIS_SELECTED | LVIS_FOCUSED };
                SendMessageLVItem(openList, LVM_SETITEMSTATE, new IntPtr(reRow), ref lvi);
                SendSynthesizedNmClick(ccWizard, openList, 1083, reRow, 0, log);
                log?.Invoke($"  [DB-3] selected '{reModelName}' (row {reRow} of {rowTotal}) in Open Models list via Win32 (LVM_SETITEMSTATE + NM_CLICK). NO UIA.");
                Thread.Sleep(150);

                // Step 5 (no step 4 - we don't load from external source): Compare
                log?.Invoke($"  [DB-5] posting WM_COMMAND {CC_COMPARE} (Compare)");
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_COMPARE, 0), IntPtr.Zero);

                // "Compare to itself" popup safety check (same as Mart pipeline)
                Thread.Sleep(800);
                IntPtr popup = WaitForDialog("erwin Data Modeler", 1500);
                if (popup != IntPtr.Zero)
                {
                    log?.Invoke($"  [DB-5] popup: '{GetTitle(popup)}' - dismissing with No");
                    try
                    {
                        var pop = AutomationElement.FromHandle(popup);
                        if (pop != null) ClickButtonByName(pop, new[] { "No", "Hayır", "Cancel", "İptal" });
                    }
                    catch { }
                    return null;
                }

                // Step 6: wait for Resolve Differences (longer timeout for DB
                // compare - the RE-vs-mart compare can take more than 10s if
                // the model has many entities).
                // No Type Resolution guard here: the RIGHT side is a fresh DB
                // reverse-engineer with no UDP definitions, so erwin's Type
                // Resolution wizard cannot appear on the From-DB compare
                // (user-confirmed 2026-06-11). Mart-vs-mart routes use
                // WaitForResolveDifferencesHandlingTypeResolution instead.
                IntPtr resolveDlg = WaitForDialog("Resolve Differences", 30000);
                if (resolveDlg == IntPtr.Zero)
                {
                    log?.Invoke("  [DB-6] Resolve Differences did not appear within 30s");
                    return session;
                }
                log?.Invoke($"  [DB-6] Resolve Differences = 0x{resolveDlg.ToInt64():X}");
                session.ResolveDifferences = resolveDlg;

                // Apply-to-Right via the shared helper
                // (ApplyToRightArrowAndWaitForRas, see ~line 600). Caller-side
                // bracketing: overlayToggle makes the addin form click-through
                // so the mouse-sim reaches the RD listview; restored in finally
                // even on exception. Helper-side parameters chosen to match the
                // prior inline behaviour:
                //   hideAfterClick=true  - hide RD per attempt + unhide on
                //                          retry (cuts ~500ms flash; the
                //                          Mart-Mart / From-DB pattern).
                //   dumpItems=true       - log first 8 LV rows for tree-depth
                //                          diagnostics (DB compare is deeper
                //                          than Mart-Mart).
                //   poll 5/800           - the prior tight poll cadence.
                // On success the helper internally waits for RIGHT Alter Script
                // (cmd 1057) to be TBSTATE_ENABLED, so the next 1057 click
                // lands. (1056=LEFT is DISABLED after Apply-to-Right; clicking
                // it was the prior bug.)
                try { overlayToggle?.Invoke(false); } catch { }
                try
                {
                    bool applied = ApplyToRightArrowAndWaitForRas(
                        resolveDlg, "DB-7", log,
                        hideAfterClick: true, dumpItems: true,
                        pollMaxMs: 800, pollStep: 5);
                    session.Applied = applied;
                }
                finally
                {
                    try { overlayToggle?.Invoke(true); } catch { }
                }
                return session;
            }
            catch (Exception ex)
            {
                log?.Invoke($"  DB-CC threw: {ex.GetType().Name}: {ex.Message}");
                try { overlayToggle?.Invoke(true); } catch { }
                return session.CCWizard != IntPtr.Zero ? session : null;
            }
            finally
            {
                // Stop the GDM-1001 popup auto-dismisser. Wait briefly for
                // the task to terminate so its final log line ("dismissed N
                // popup(s)") interleaves correctly with the rest of the
                // pipeline output.
                try
                {
                    gdmCts.Cancel();
                    gdmDismisser?.Wait(500);
                    gdmCts.Dispose();
                }
                catch { }
            }
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
                // RD + CC wizards: after ELA::OnFE has run, posting
                // WM_COMMAND IDCANCEL/CC_CLOSE to these wizards triggers
                // their MFC OnCancel/OnClose handlers which dereference
                // CERwinFEData::m_xActionSummary / m_modelSet, both nulled
                // by OnFE's post-cleanup. Verified crash on From-DB pipeline
                // 2026-04-26 23:43 right after "cleanup: Cancel Resolve
                // Differences" was posted to handle 0xC5D09A6.
                //
                // Use bridge ForceDestroyWizard which calls DestroyWindow
                // directly on the wizard's owner thread (via WH_CALLWNDPROCRET
                // sentinel injection), bypassing all MFC command handlers.
                // Side effect: leaks the CDialog C++ object but never crashes.
                if (s.ResolveDifferences != IntPtr.Zero)
                {
                    log?.Invoke($"  cleanup: ForceDestroy Resolve Differences (hwnd=0x{s.ResolveDifferences.ToInt64():X})");
                    int rc = NativeBridgeService.ForceDestroyWizard(s.ResolveDifferences, log);
                    log?.Invoke($"  cleanup: ForceDestroy RD rc={rc}");
                    Thread.Sleep(150);
                }
                if (s.CCWizard != IntPtr.Zero)
                {
                    // Reverted to ForceDestroy 2026-04-27 03:00 after the
                    // IDCANCEL-revert hypothesis (matching manual close
                    // path) failed in practice: post-OnFE the bridge's
                    // OBS-FED-CLEAR has already nulled m_xActionSummary +
                    // m_modelSet, so the CC wizard's MFC OnCancel handler
                    // silent-fails when WM_COMMAND CC_CLOSE arrives. The
                    // wizard stays alive AND the v1 PU stays orphaned -
                    // worst of both worlds. ForceDestroy at least kills
                    // the wizard frame consistently. The orphan v1 leak
                    // remains but is a separate, deeper problem requiring
                    // either programmatic Alter Script wizard drive (old
                    // deleted WizardAutomationService approach) or bridge
                    // state snapshot/restore around OnFE.
                    log?.Invoke($"  cleanup: ForceDestroy CC wizard (hwnd=0x{s.CCWizard.ToInt64():X})");
                    int rc = NativeBridgeService.ForceDestroyWizard(s.CCWizard, log);
                    log?.Invoke($"  cleanup: ForceDestroy CC rc={rc}");
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
                                log?.Invoke($"  [residual] CC/RD frame 0x{r.h.ToInt64():X} still alive - ForceDestroy");
                                int rc = NativeBridgeService.ForceDestroyWizard(r.h, log);
                                log?.Invoke($"  [residual] ForceDestroy rc={rc}");
                                dismissed = (rc == 1);
                            }
                            else if (t.IndexOf("Wizard", StringComparison.OrdinalIgnoreCase) >= 0
                                  || t.IndexOf("Resolve Differences", StringComparison.OrdinalIgnoreCase) >= 0
                                  || t.IndexOf("Complete Compare", StringComparison.OrdinalIgnoreCase) >= 0
                                  || t.IndexOf("Forward Engineer", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                log?.Invoke($"  [residual] wizard 0x{r.h.ToInt64():X} title='{t}' - ForceDestroy");
                                int rc = NativeBridgeService.ForceDestroyWizard(r.h, log);
                                log?.Invoke($"  [residual] ForceDestroy rc={rc}");
                                dismissed = (rc == 1);
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
        /// Pure-Win32 (NO UI Automation) variant of <see cref="DismissMartOfflineDialog"/>
        /// for the Review / cross-version teardown. The UIA version calls
        /// AutomationElement.FromHandle, which creates an XTP IAccessible RCW that,
        /// abandoned to GC, crashes erwin's finalizer when the dialog is destroyed
        /// (the same teardown ExecutionEngineException class eliminated for From-DB).
        /// This variant creates NO AutomationElement at all: it reuses the already
        /// pure-Win32 toolbar path (<see cref="TryClickMartOfflineToolbarViaWmCommand"/>:
        /// TB_GETBUTTON command id + WM_COMMAND) to set Save-to=Close, then clicks OK
        /// via Win32 (GetDlgItem(IDOK) + WM_COMMAND IDOK with lParam = OK-button HWND -
        /// the form the XTP dialog honours; lParam=0 is ignored). If the toolbar path
        /// fails it does NOT click OK (OK while a row is still Save-to=Offline spawns a
        /// Save As picker) and leaves the dialog for the user - never a spurious save.
        /// </summary>
        private static void DismissMartOfflineDialogWin32(IntPtr martOffline, Action<string> log)
        {
            if (martOffline == IntPtr.Zero) return;
            try
            {
                ForceForeground(martOffline);
                Thread.Sleep(200);

                // Set Save-to=Close via the existing Win32 toolbar path (no UIA).
                // offRoot is unused inside that helper, so pass null.
                bool toolbarOk = TryClickMartOfflineToolbarViaWmCommand(martOffline, null, log);
                if (!toolbarOk)
                {
                    log?.Invoke("  [MO-WIN32] toolbar 'Set to Close' (Win32) failed - NOT clicking OK (OK with Save-to=Offline spawns a Save As picker). Leaving Mart Offline for manual.");
                    return;
                }

                Thread.Sleep(250);

                // OK via Win32. lParam MUST be the OK button HWND (same XTP quirk as
                // the Save Models dialog: WM_COMMAND IDOK with lParam=0 is ignored).
                IntPtr okBtn = GetDlgItem(martOffline, 1);
                if (okBtn == IntPtr.Zero)
                {
                    log?.Invoke("  [MO-WIN32] OK button (IDOK=1) not found - posting WM_COMMAND IDOK to the dialog as a fallback.");
                    PostMessage(martOffline, WM_COMMAND, MakeWParam(IDOK, 0), IntPtr.Zero);
                }
                else
                {
                    PostMessage(martOffline, WM_COMMAND, MakeWParam(IDOK, 0), okBtn);
                    log?.Invoke($"  [MO-WIN32] OK posted (WM_COMMAND IDOK, lParam=okBtn 0x{okBtn.ToInt64():X}). NO UIA used.");
                }

                Thread.Sleep(500);
                log?.Invoke(IsWindow(martOffline)
                    ? "  [MO-WIN32] WARN Mart Offline still alive after Win32 OK - may need manual close."
                    : "  [MO-WIN32] Mart Offline dismissed (pure Win32).");
            }
            catch (Exception ex) { log?.Invoke($"  [MO-WIN32] threw: {ex.Message}"); }
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

                // Walk all buttons and pick the 3rd VISIBLE one (the user's
                // "üstten 3. ikon" = Set All to Close). The toolbar's leading
                // buttons can be HIDDEN (TBSTATE_HIDDEN=0x08) and there are
                // separators (TBSTYLE_SEP=0x01) - both must be skipped, else
                // the raw index 2 lands on a hidden button whose WM_COMMAND is
                // a no-op (verified 2026-05-29: btn[0..2] were hidden, so the
                // Save-to stayed "Offline" and OK spawned a Save As dialog).
                const byte TBSTATE_HIDDEN = 0x08;
                const byte TBSTYLE_SEP    = 0x01;
                int chosenCmd = 0;
                int visibleIdx = 0;
                for (int i = 0; i < count; i++)
                {
                    var tbb = new TBBUTTON();
                    SendMessageTbButton(toolbarHwnd, TB_GETBUTTON, new IntPtr(i), ref tbb);
                    bool isSep = (tbb.fsStyle & TBSTYLE_SEP) != 0;
                    bool isHidden = (tbb.fsState & TBSTATE_HIDDEN) != 0;
                    log?.Invoke($"    btn[{i}] cmdId={tbb.idCommand} state=0x{tbb.fsState:X2} style=0x{tbb.fsStyle:X2}{(isSep ? " SEP" : "")}{(isHidden ? " HIDDEN" : "")}");
                    if (isSep || isHidden) continue;
                    if (visibleIdx == 2) chosenCmd = tbb.idCommand;   // 3rd visible
                    visibleIdx++;
                }
                if (chosenCmd == 0)
                {
                    log?.Invoke($"    no 3rd-visible toolbar button captured ({visibleIdx} visible button(s))");
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
                            SendMouseClickAt(pt.X, pt.Y, log);
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
                // Belt-and-suspenders: restore the addin form even if an
                // exception bubbles past the inner finally (e.g. polling
                // throws before we reach the click loop). Without this the
                // form stays in click-through state and modal children
                // (Configure dialog, etc.) become invisible-but-active.
                try { overlayToggle?.Invoke(true); } catch { }
                return session.CCWizard != IntPtr.Zero ? session : null;
            }
        }

        // ── UIA helpers ─────────────────────────────────────────────────────

        private static bool SelectMartVersionInPicker(IntPtr martDlg, int version, string catalogPath, Action<string> log)
        {
            // PURE WIN32 (no UIA). The old UIA path (AutomationElement.FromHandle on
            // this picker dialog + its catalog tree / model grid / version combo /
            // Open button) created oleacc AccWrap IAccessible RCWs that, abandoned to
            // GC, crashed erwin's finalizer ~6s after the picker closed (confirmed via
            // dump erwin.exe.43564 = oleacc!AccWrap_Base::Release AV on a freed
            // 0x0BADF00D IUnknown). The add-in is in-process, so control text buffers
            // are read directly. FAIL-SAFE: any failure -> IDCANCEL + return false
            // (NEVER load a wrong model; that crashes erwin).
            try
            {
                string wantModel = "";
                try
                {
                    var segs2 = (catalogPath ?? "").Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                    if (segs2.Length > 0) wantModel = segs2[segs2.Length - 1];
                }
                catch { /* wantModel stays empty -> grid step handles it */ }

                // Step 0: catalog tree (id=2054) - walk folder segments (all but the
                // last, which is the model name). Best-effort: if the tree cannot be
                // walked, proceed (the grid may already be populated for the active
                // catalog); the grid step below safe-aborts if the model is missing.
                if (!string.IsNullOrEmpty(catalogPath))
                {
                    IntPtr tree = FindDescendantById(martDlg, 2054);
                    var tcls = new StringBuilder(64);
                    if (tree != IntPtr.Zero) GetClassName(tree, tcls, tcls.Capacity);
                    if (tree != IntPtr.Zero && tcls.ToString().IndexOf("SysTreeView32", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var segs = catalogPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                        int walkCount = Math.Max(0, segs.Length - 1);
                        IntPtr scope = SendMessage(tree, TVM_GETNEXTITEM, new IntPtr(TVGN_ROOT), IntPtr.Zero);
                        for (int i = 0; i < walkCount && scope != IntPtr.Zero; i++)
                        {
                            int findBudget = 5000;
                            // FindTreeItemByTextDeep walks descendants and lazy-expands
                            // collapsed nodes on demand, so no pre-expand pass is needed.
                            IntPtr m = FindTreeItemByTextDeep(tree, scope, segs[i], ref findBudget);
                            if (m == IntPtr.Zero) { log?.Invoke($"    [PICK] catalog node '{segs[i]}' not found in tree"); break; }
                            SendMessage(tree, TVM_EXPAND, new IntPtr(TVE_EXPAND), m);
                            SendMessage(tree, TVM_SELECTITEM, new IntPtr(TVGN_CARET), m);
                            log?.Invoke($"    [PICK] catalog navigated: '{segs[i]}'");
                            Thread.Sleep(300);
                            scope = SendMessage(tree, TVM_GETNEXTITEM, new IntPtr(TVGN_CHILD), m);
                        }
                    }
                    else
                    {
                        log?.Invoke($"    [PICK] catalog tree id=2054 not a SysTreeView32 (class='{tcls}') - skipping tree nav (relying on default catalog).");
                    }
                }

                // Step 1: model grid (id=30270) - select the row whose name == wantModel.
                IntPtr modelGrid = FindListViewById(martDlg, 30270, log: null);
                if (modelGrid == IntPtr.Zero)
                {
                    IntPtr probe = FindDescendantById(martDlg, 30270);
                    var gcls = new StringBuilder(64);
                    if (probe != IntPtr.Zero) GetClassName(probe, gcls, gcls.Capacity);
                    log?.Invoke($"    [PICK] model grid id=30270 not a SysListView32 (hwnd=0x{probe.ToInt64():X} class='{gcls}') - cannot drive via Win32; ABORT (IDCANCEL).");
                    PostMessage(martDlg, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
                    return false;
                }
                int modelRow = -1;
                for (int attempt = 0; attempt < 12 && modelRow < 0; attempt++)
                {
                    int rt = SendMessage(modelGrid, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    for (int i = 0; i < rt; i++)
                    {
                        string cell = "";
                        for (int s = 0; s <= 3; s++)
                        {
                            string c = GetListViewItemText(modelGrid, i, s)?.Trim();
                            if (!string.IsNullOrEmpty(c)) { cell = c; break; }
                        }
                        if (attempt == 0) log?.Invoke($"    [PICK] model row[{i}] = '{cell}'");
                        if (!string.IsNullOrEmpty(wantModel) && !string.IsNullOrEmpty(cell)
                            && string.Equals(cell, wantModel, StringComparison.OrdinalIgnoreCase)) { modelRow = i; break; }
                    }
                    if (modelRow < 0) Thread.Sleep(250);
                }
                if (modelRow < 0)
                {
                    log?.Invoke($"    [PICK] model '{wantModel}' not found in grid - ABORT (IDCANCEL, refusing to load a different model).");
                    PostMessage(martDlg, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
                    return false;
                }
                var mlvi = new LVITEM { stateMask = LVIS_SELECTED | LVIS_FOCUSED, state = LVIS_SELECTED | LVIS_FOCUSED };
                SendMessageLVItem(modelGrid, LVM_SETITEMSTATE, new IntPtr(modelRow), ref mlvi);
                SendSynthesizedNmClick(martDlg, modelGrid, 30270, modelRow, 0, log);
                log?.Invoke($"    [PICK] selected model row '{wantModel}' (row {modelRow}) via Win32.");
                Thread.Sleep(250);

                // Step 2: version combo (id=2111) - pick the item matching `version`.
                IntPtr combo = FindDescendantById(martDlg, 2111);
                var ccls = new StringBuilder(64);
                if (combo != IntPtr.Zero) GetClassName(combo, ccls, ccls.Capacity);
                if (combo == IntPtr.Zero || ccls.ToString().IndexOf("ComboBox", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    log?.Invoke($"    [PICK] version combo id=2111 not a ComboBox (hwnd=0x{combo.ToInt64():X} class='{ccls}') - ABORT (IDCANCEL).");
                    PostMessage(martDlg, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
                    return false;
                }
                int cnt = SendMessageCb(combo, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                int sel = -1;
                string vs = version.ToString();
                for (int i = 0; i < cnt; i++)
                {
                    string it = GetComboItemText(combo, i).Trim();
                    log?.Invoke($"    [PICK] version combo[{i}] = '{it}'");
                    if (string.IsNullOrEmpty(it)) continue;
                    bool match = System.Text.RegularExpressions.Regex.IsMatch(it, @"(?i)\bversion\s+" + System.Text.RegularExpressions.Regex.Escape(vs) + @"\b")
                        || it.Equals(vs, StringComparison.OrdinalIgnoreCase)
                        || it.StartsWith($"v{vs}", StringComparison.OrdinalIgnoreCase);
                    if (match) { sel = i; break; }
                }
                if (sel < 0)
                {
                    log?.Invoke($"    [PICK] no combo item matches version '{vs}' among {cnt} - ABORT (IDCANCEL, refusing wrong version).");
                    PostMessage(martDlg, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
                    return false;
                }
                SendMessageCb(combo, CB_SETCURSEL, new IntPtr(sel), IntPtr.Zero);
                PostMessage(martDlg, WM_COMMAND, MakeWParam(2111, CBN_SELCHANGE), combo);
                log?.Invoke($"    [PICK] version combo set to index {sel} (v{vs}) via CB_SETCURSEL + CBN_SELCHANGE.");
                Thread.Sleep(200);

                // Step 3: Open (id=2059).
                IntPtr openBtn = GetDlgItem(martDlg, 2059);
                if (openBtn == IntPtr.Zero) openBtn = FindDescendantById(martDlg, 2059);
                if (openBtn != IntPtr.Zero)
                {
                    PostMessage(martDlg, WM_COMMAND, MakeWParam(2059, 0), openBtn);
                    log?.Invoke("    [PICK] Open posted (WM_COMMAND id=2059, lParam=btn). NO UIA.");
                }
                else
                {
                    log?.Invoke("    [PICK] Open button id=2059 not found - fallback WM_COMMAND IDOK.");
                    PostMessage(martDlg, WM_COMMAND, MakeWParam(IDOK, 0), IntPtr.Zero);
                }
                Thread.Sleep(600);
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"    [PICK] SelectMartVersionInPicker (Win32) threw: {ex.Message} - ABORT (IDCANCEL).");
                try { PostMessage(martDlg, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero); } catch { /* dialog may be gone */ }
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

        /// <summary>
        /// Manuel akış mimicry: RD dialog'undan "Right Alter Script/Schema
        /// Generation" toolbar butonuna mouse click yapar. Bu erwin'in iç
        /// `ELA::OnFE(ms, true, lastEdrId)` çağrısını tetikler (manuel test
        /// 11:18:38 doğrulaması: flags=0x236C == son EDR tx id 9068). Bu
        /// flag programmatic OnFE çağrısında 0x13 olarak veriliyor → AS
        /// yanlış attach → full DDL. Manuel akış doğru flag verdiği için
        /// AS doğru attach + alter DDL üretiliyor.
        ///
        /// Akış: RD'de UIA ile "Right Alter Script/Schema Generation" name
        /// match'li button bul -> BoundingRectangle al -> mouse click ->
        /// "Forward Engineer Alter Script Schema Generation Wizard" CREATE
        /// event bekle -> wizard hwnd dön. Wizard CREATE 5 saniye içinde
        /// olmazsa IntPtr.Zero dön.
        ///
        /// Returns: wizard hwnd on success, IntPtr.Zero on failure.
        /// </summary>
        public static async Task<IntPtr> ClickRightAlterScriptInRdAsync(IntPtr rdHwnd, Action<string> log)
        {
            return await Task.Run(() => ClickRightAlterScriptInRd(rdHwnd, CMD_RD_ALTER_SCRIPT, log));
        }

        /// <summary>
        /// cmdId overload. From-DB uses <see cref="CMD_RD_ALTER_SCRIPT"/> (1056 -
        /// the enabled generate for its compare direction). The Review path
        /// passes 1057 (<see cref="CMD_RD_RIGHT_ALTER_SCRIPT"/>): on the Review
        /// RD, 1056 is the LEFT Alter Script (DISABLED after Apply-to-Right) and
        /// 1057 is the RIGHT one (ENABLED) - verified by the RD toolbar dump
        /// 2026-05-29 (btn[2]=1056 disabled, btn[3]=1057 enabled).
        /// </summary>
        public static async Task<IntPtr> ClickRightAlterScriptInRdAsync(IntPtr rdHwnd, int cmdId, Action<string> log)
        {
            return await Task.Run(() => ClickRightAlterScriptInRd(rdHwnd, cmdId, log));
        }

        private static IntPtr ClickRightAlterScriptInRd(IntPtr rdHwnd, int cmdId, Action<string> log)
        {
            if (rdHwnd == IntPtr.Zero || !IsWindow(rdHwnd))
            {
                log?.Invoke("  [RAS] RD hwnd invalid");
                return IntPtr.Zero;
            }

            // 2026-04-27 verdict: WM_COMMAND 1056 PostMessage to RD parent
            // does NOT reproduce a real mouse click on the toolbar button.
            // Manual mouse click invokes OnFE with flags=lastApplyEdrId+1
            // (proper post-apply state); PostMessage variant invokes OnFE
            // with flags=0 - same handler, missing button-level state setup.
            // RD diagnostics (RAS-DUMP) confirmed RD owns 5 standard
            // ToolbarWindow32 controls; we now locate the one carrying
            // CMD_RD_ALTER_SCRIPT (1056) at runtime, fetch its item rect,
            // map to screen coords, and synthesize a real mouse click via
            // SendMouseClickAt (the same proven helper used for the
            // Apply-to-Right click). Fully dynamic - no hardcoded coords.
            (IntPtr toolbar, int buttonIdx) = FindToolbarButtonByCommand(
                rdHwnd, cmdId, log);

            var dialogsBefore = EnumerateVisibleDialogs();
            try { SetForegroundWindow(rdHwnd); UnhideWindow(rdHwnd); } catch { }

            if (toolbar == IntPtr.Zero || buttonIdx < 0)
            {
                log?.Invoke($"  [RAS] toolbar with cmd {cmdId} NOT found - falling back to WM_COMMAND PostMessage (will produce stripped DDL)");
                PostMessage(rdHwnd, WM_COMMAND, MakeWParam(cmdId, 0), IntPtr.Zero);
            }
            else
            {
                RECT btnRect = new RECT();
                IntPtr rcRes = SendMessageRectOut(toolbar, TB_GETITEMRECT, new IntPtr(buttonIdx), ref btnRect);
                if (rcRes == IntPtr.Zero || (btnRect.right == 0 && btnRect.bottom == 0))
                {
                    log?.Invoke($"  [RAS] TB_GETITEMRECT returned empty for toolbar=0x{toolbar.ToInt64():X} idx={buttonIdx} - fallback to WM_COMMAND");
                    PostMessage(rdHwnd, WM_COMMAND, MakeWParam(cmdId, 0), IntPtr.Zero);
                }
                else
                {
                    POINT center = new POINT
                    {
                        X = (btnRect.left + btnRect.right) / 2,
                        Y = (btnRect.top + btnRect.bottom) / 2,
                    };
                    if (!ClientToScreen(toolbar, ref center))
                    {
                        log?.Invoke("  [RAS] ClientToScreen failed - fallback to WM_COMMAND");
                        PostMessage(rdHwnd, WM_COMMAND, MakeWParam(cmdId, 0), IntPtr.Zero);
                    }
                    else
                    {
                        log?.Invoke($"  [RAS] real mouse click on toolbar=0x{toolbar.ToInt64():X} idx={buttonIdx} btnRect=({btnRect.left},{btnRect.top})-({btnRect.right},{btnRect.bottom}) screen=({center.X},{center.Y})");
                        // Force RD to top of Z-order so its toolbar receives
                        // the click. Without this another window (e.g. our
                        // addin form) might cover the target coordinates.
                        try { BringWindowToTop(rdHwnd); SetForegroundWindow(rdHwnd); UnhideWindow(rdHwnd); } catch { }
                        Thread.Sleep(40);
                        // Diagnostic: confirm the target screen coord actually
                        // belongs to RD's toolbar (not some overlay window).
                        IntPtr targetHwnd = WindowFromPoint(center);
                        log?.Invoke($"  [RAS] WindowFromPoint({center.X},{center.Y})=0x{targetHwnd.ToInt64():X} (expected toolbar=0x{toolbar.ToInt64():X})");
                        if (targetHwnd != toolbar && targetHwnd != IntPtr.Zero)
                        {
                            log?.Invoke($"  [RAS] WARNING: click target window mismatch - another window covers the toolbar!");
                        }
                        // XTP toolbar filters obviously-synthetic input. The
                        // Apply-to-Right click path used ShowCursor(false) +
                        // immediate down/up which XTP eats on the toolbar
                        // (verified 20:45 - mouse click logged but wizard
                        // never appeared). Use a human-like sequence here:
                        // visible cursor, settle delay before press, hold
                        // briefly, then release. Yields a brief cursor jump
                        // (~150ms) but bypasses the filter heuristic.
                        // 180ms of vulnerable sleeps below (60+80+40) - confine
                        // the cursor to a 5x5 box around the target so user
                        // mouse movement cannot route the synthetic clicks to
                        // the wrong window (user-reported failure 2026-05-30).
                        bool rasClipped = ClipCursorAround(center.X, center.Y);
                        if (GetCursorPos(out POINT savedCursor))
                        {
                            try
                            {
                                SetCursorPos(center.X, center.Y);
                                Thread.Sleep(60);   // settle before press
                                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                                Thread.Sleep(80);   // human-like hold
                                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                                Thread.Sleep(40);   // settle after release
                            }
                            finally
                            {
                                if (rasClipped) ReleaseCursorClip();
                                SetCursorPos(savedCursor.X, savedCursor.Y);
                            }
                        }
                        else
                        {
                            try
                            {
                                SetCursorPos(center.X, center.Y);
                                Thread.Sleep(60);
                                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                                Thread.Sleep(80);
                                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                                Thread.Sleep(40);
                            }
                            finally
                            {
                                if (rasClipped) ReleaseCursorClip();
                            }
                        }
                    }
                }
            }

            IntPtr wizard = WaitForNewDialog(dialogsBefore,
                "Forward Engineer Alter Script Schema Generation Wizard",
                5000, log);
            if (wizard == IntPtr.Zero)
            {
                log?.Invoke("  [RAS] FE Alter Script wizard did not appear within 5s");
                return IntPtr.Zero;
            }
            log?.Invoke($"  [RAS] Wizard appeared = 0x{wizard.ToInt64():X}");
            return wizard;
        }

        /// <summary>
        /// Walk every <c>ToolbarWindow32</c> child of <paramref name="parent"/>
        /// looking for the one whose button list contains <paramref name="cmdId"/>.
        /// Returns the toolbar handle and zero-based button index, or
        /// <c>(IntPtr.Zero, -1)</c> if no match. RD owns multiple toolbars so we
        /// must check each. Fully dynamic - no hardcoded coords or HWNDs.
        /// </summary>
        private static (IntPtr toolbar, int buttonIdx) FindToolbarButtonByCommand(
            IntPtr parent, int cmdId, Action<string> log)
        {
            IntPtr foundToolbar = IntPtr.Zero;
            int foundIdx = -1;
            EnumChildWindows(parent, (h, _) =>
            {
                StringBuilder cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "ToolbarWindow32") return true;

                int count = SendMessageInt(h, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                if (count <= 0) return true;

                for (int i = 0; i < count; i++)
                {
                    TBBUTTON tb = default;
                    IntPtr res = SendMessageTbButton(h, TB_GETBUTTON, new IntPtr(i), ref tb);
                    if (res == IntPtr.Zero) continue;
                    if (tb.idCommand == cmdId)
                    {
                        log?.Invoke($"  [RAS] toolbar=0x{h.ToInt64():X} button[{i}] idCommand={tb.idCommand} (matched cmd {cmdId})");
                        foundToolbar = h;
                        foundIdx = i;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            return (foundToolbar, foundIdx);
        }

        /// <summary>
        /// Manuel akış mimicry: FE Alter Script wizard açıldıktan sonra 1x Next
        /// (Overview -> Option Selection) atar, sonra sol nav'daki "Preview"
        /// label'ına HESAPLANMIS pikselde mouse-sim tıklar. Nav item'lari UIA'da
        /// yok (sadece "Navigational Pane" Static container var), o yuzden tek
        /// yol pixel-click. Preview sayfasi render olunca erwin GA cagrisini
        /// tetikler -> bridge GA hook DDL'i capture buffer'a yazar. Production'da
        /// <paramref name="overlayToggle"/> ile addin formu click-through yapilir
        /// (mouse wizard'a gecsin). Next-loop'a gore ~5x hizli (600ms vs ~2.5s).
        /// </summary>
        public static async Task<bool> ClickWizardPreviewTabAsync(IntPtr wizardHwnd, Action<string> log, Action<bool> overlayToggle = null, bool requireObjectFilterPass = false)
        {
            return await Task.Run(() => ClickWizardPreviewTab(wizardHwnd, log, overlayToggle, requireObjectFilterPass));
        }

        private static bool ClickWizardPreviewTab(IntPtr wizardHwnd, Action<string> log, Action<bool> overlayToggle = null, bool requireObjectFilterPass = false)
        {
            if (wizardHwnd == IntPtr.Zero || !IsWindow(wizardHwnd))
            {
                log?.Invoke("  [WPT] Wizard hwnd invalid");
                return false;
            }

            // The wizard has 6 pages: Overview -> Option Selection -> Summary ->
            // Owner Override -> Object Filter -> Preview. The Preview page
            // renders the DDL by triggering ELA -> FEProcessor::GenerateAlterScript
            // -> the bridge's GA hook -> the DDL capture buffer.
            //
            // TWO strategies, chosen by the caller per the "Only Selected Objects"
            // checkbox (chkFilterObjects on the addin form):
            //
            // 1. JUMP (default, ~1.4s): 1x Next (Overview -> Option Selection),
            //    then mouse-sim click on the "Preview" left-nav pixel. SKIPS the
            //    Object Filter page, so the wizard's "Use current diagram
            //    selections? You have N entity selected" popup NEVER fires - the
            //    bridge's SetUseDiagramSelection toggle has no effect. Use when
            //    the user does NOT want diagram-scoped filtering.
            //
            // 2. NEXT-LOOP (slower, ~2.5s): post CMD_FE_WIZARD_NEXT 6 times,
            //    visiting EVERY page including Object Filter. When entities are
            //    selected on the active diagram + the bridge's
            //    g_useDiagramSelection flag is YES, the popup fires and the
            //    bridge (DismissDiagramSelectionPopup) answers Yes -> wizard
            //    scopes the alter script to the selected entities. Use when the
            //    user explicitly enabled "Only Selected Objects".
            //
            // The jump was the regression that broke "Only Selected Objects" on
            // the Review / cross-version path (verified by user 2026-05-30); the
            // Next-loop branch fixes that without giving up the jump's speed on
            // the common no-filter case.
            if (requireObjectFilterPass)
            {
                log?.Invoke("  [WPT] Next-loop strategy: walking every page so the Object Filter page fires its popup (Only Selected Objects requested)");
                return WalkNextLoopToPreview(wizardHwnd, log, overlayToggle);
            }

            log?.Invoke("  [WPT] Jump strategy: 1x Next then mouse-click the 'Preview' nav item (Object Filter skipped - default)");
            return JumpToPreviewByPixel(wizardHwnd, log, overlayToggle);
        }

        /// <summary>
        /// Slower-but-correct fallback that walks every wizard page by posting
        /// CMD_FE_WIZARD_NEXT until the bridge captures the DDL. Critically it
        /// VISITS the Object Filter page (page 5), which is where the wizard's
        /// "Use current diagram selections?" popup fires - the only opportunity
        /// for the bridge's SetUseDiagramSelection toggle ("Only Selected
        /// Objects") to take effect.
        /// </summary>
        private static bool WalkNextLoopToPreview(IntPtr wizardHwnd, Action<string> log, Action<bool> overlayToggle)
        {
            // overlayToggle (production only): make the addin form click-through
            // so the splash does not block the bridge's popup-dismiss WinEvent
            // path. Restored in finally even on exception.
            try { overlayToggle?.Invoke(false); } catch { }
            try
            {
                for (int page = 1; page <= 8; page++)
                {
                    Thread.Sleep(500);
                    string captured = NativeBridgeService.ConsumeLastCapturedDdl();
                    if (!string.IsNullOrEmpty(captured))
                    {
                        log?.Invoke($"  [WPT] DDL captured at page {page} ({captured.Length} chars) - re-stashing for caller");
                        LastCapturedWizardDdl = captured;
                        return true;
                    }
                    // On the Option Selection page (page 2) apply the option-set
                    // setting BEFORE advancing (user requirement 2026-06-03: this
                    // page must not be skipped). Switches a stale incompatible file
                    // option to the built-in default to avoid the "XML not
                    // compatible" error that brings back the DWM black rectangles.
                    if (page == 2) { EnsureCompatibleOptionSet(wizardHwnd, log); Thread.Sleep(150); }
                    log?.Invoke($"  [WPT] page {page}: posting CMD_FE_WIZARD_NEXT (1766)");
                    PostMessage(wizardHwnd, WM_COMMAND, MakeWParam(CMD_FE_WIZARD_NEXT, 0), IntPtr.Zero);
                }

                Thread.Sleep(1500);
                string finalDdl = NativeBridgeService.ConsumeLastCapturedDdl();
                if (!string.IsNullOrEmpty(finalDdl))
                {
                    log?.Invoke($"  [WPT] DDL captured at end ({finalDdl.Length} chars)");
                    LastCapturedWizardDdl = finalDdl;
                    return true;
                }

                log?.Invoke("  [WPT] Next-loop exhausted without DDL capture");
                return false;
            }
            finally
            {
                try { overlayToggle?.Invoke(true); } catch { }
            }
        }

        /// <summary>
        /// Fast path: 1x Next (Overview -> Option Selection) then mouse-sim
        /// click on the computed "Preview" left-nav pixel. ~1.4s end-to-end.
        /// Skips the Object Filter page so the bridge's SetUseDiagramSelection
        /// toggle has no effect on this run - callers wanting "Only Selected
        /// Objects" must route through WalkNextLoopToPreview instead.
        ///
        /// The nav items are NOT in the UIA tree (verified by NAVPROBE dump
        /// 2026-05-29: the only nav element is a single Static container
        /// name='Navigational Pane' aid='1063'; the 6 page labels are
        /// custom-drawn inside it). So there is no element to UIA-Select; the
        /// only way is a mouse-sim at the "Preview" label's PIXEL position,
        /// computed from the live container rect. The wizard stays VISIBLE
        /// (hiding it hung erwin on teardown - see
        /// reference_layered_wizard_compositor_leak), so mouse-sim works.
        /// </summary>
        private static bool JumpToPreviewByPixel(IntPtr wizardHwnd, Action<string> log, Action<bool> overlayToggle)
        {
            // Step 1: Overview -> Option Selection (matches the user's flow; some
            // wizards only enable later nav items once a prior page is visited).
            PostMessage(wizardHwnd, WM_COMMAND, MakeWParam(CMD_FE_WIZARD_NEXT, 0), IntPtr.Zero);
            Thread.Sleep(600);

            // Step 1b: NOW on the Option Selection page - apply the option-set
            // setting HERE, before going to Preview (user requirement 2026-06-03:
            // this page must NOT be skipped / no direct jump to Preview). If the
            // model's retained option set is a stale incompatible file (wrong DB
            // version), switch to the built-in "Alter Schema Generation" so the
            // wizard's Generate does not throw "XML not compatible / different DB
            // versions" - that error interrupts the hidden-wizard teardown and
            // brings back the DWM black rectangles.
            EnsureCompatibleOptionSet(wizardHwnd, log);
            Thread.Sleep(150);

            // Step 2: read the live "Navigational Pane" rect (handles wherever
            // the wizard opened) and compute the "Preview" label position.
            // 2026-06-02: read the Navigational Pane (control id=1063) rect via
            // PURE WIN32 (GetDlgItem / EnumChildWindows + GetWindowRect) instead of
            // UIA AutomationElement.FromHandle + FindFirst. The UIA path created an
            // oleacc AccWrap / IAccessible RCW for the Static pane that, abandoned
            // to GC, crashed erwin's finalizer at teardown (full dump
            // erwin.exe.34340.dmp = oleacc!AccWrap_Base::Release AV; UIA elements
            // are cached so the GC drain cannot release them - they MUST be
            // eliminated, not drained). GetWindowRect returns the same screen-px
            // rect the UIA BoundingRectangle did, so the calibrated click below is
            // unchanged.
            System.Windows.Rect pane = default;
            bool gotPane = false;
            try
            {
                IntPtr paneHwnd = GetDlgItem(wizardHwnd, 1063);
                if (paneHwnd == IntPtr.Zero) paneHwnd = FindDescendantById(wizardHwnd, 1063);
                if (paneHwnd != IntPtr.Zero && GetWindowRect(paneHwnd, out RECT pr)
                    && (pr.right - pr.left) > 10 && (pr.bottom - pr.top) > 10)
                {
                    pane = new System.Windows.Rect(pr.left, pr.top, pr.right - pr.left, pr.bottom - pr.top);
                    gotPane = true;
                    log?.Invoke($"  [WPT] nav pane (id=1063) rect via Win32: ({pr.left},{pr.top},{pr.right},{pr.bottom})");
                }
                else
                {
                    log?.Invoke($"  [WPT] nav pane (id=1063) not found via Win32 (paneHwnd=0x{paneHwnd.ToInt64():X})");
                }
            }
            catch (Exception ex) { log?.Invoke($"  [WPT] nav-pane Win32 read err: {ex.Message}"); }

            if (!gotPane || pane.Width < 10 || pane.Height < 10)
            {
                log?.Invoke("  [WPT] Navigational Pane rect not found - cannot target Preview");
                return false;
            }

            // The 6 page labels (Overview .. Preview) occupy the TOP of the pane,
            // evenly spaced; Preview is the LAST (index 5). CALIBRATION 2026-05-29
            // on pane (Y 321..714, h393): a click at paneTop+0.31*h (Y=442) landed
            // on "Object Filter" (index 4, one ABOVE Preview), so Preview sits one
            // item lower. Item pitch is ~22-28px, so Preview center is ~Y 466-470;
            // paneTop+0.375*h (Y=468) targets its middle. Anchoring to a fraction
            // of the (fixed-size) pane keeps this DPI-stable. X is ~55px in from
            // the pane's left edge (over the left-aligned label - X was correct,
            // the 442 click registered as a nav row, just the wrong one).
            int clickX = (int)pane.Left + 55;
            int clickY = (int)pane.Top + (int)(pane.Height * 0.375);
            log?.Invoke($"  [WPT] pane=({(int)pane.Left},{(int)pane.Top} {(int)pane.Width}x{(int)pane.Height}) -> Preview click=({clickX},{clickY})");

            // Production: make the addin form click-through (WS_EX_TRANSPARENT)
            // + hide the "please wait" popup so the mouse-sim reaches the wizard
            // below. Null in debug (no overlay). Restored in finally - leaving
            // the form click-through would silently break user interaction.
            // Mirrors the proven Apply-to-Right pattern (DriveCCDbAndApply).
            try { overlayToggle?.Invoke(false); } catch { }
            try
            {
                // Bring the (visible) wizard foreground so XTP accepts the
                // synthetic click (same requirement as the Apply-to-Right RD
                // click), then verify the click point actually belongs to the
                // wizard (guards against something still covering it).
                ForceForeground(wizardHwnd);
                Thread.Sleep(120);
                IntPtr atPoint = WindowFromPoint(new POINT { X = clickX, Y = clickY });
                IntPtr atRoot = GetAncestor(atPoint, GA_ROOT);
                log?.Invoke($"  [WPT] WindowFromPoint=0x{atPoint.ToInt64():X} root=0x{atRoot.ToInt64():X} (wizard=0x{wizardHwnd.ToInt64():X})");
                if (atRoot != wizardHwnd && atPoint != wizardHwnd)
                    log?.Invoke("  [WPT] WARN click point is not on the wizard - something is covering it");

                // Step 3: click the Preview label (cursor hidden during the
                // jump; SendMouseClickAt saves/restores the cursor position).
                try
                {
                    ShowCursor(false);
                    SendMouseClickAt(clickX, clickY, log);
                }
                finally
                {
                    ShowCursor(true);
                }

                // Step 4: poll for the auto-generated DDL (the GA hook fires when
                // the Preview page renders). No Next-loop fallback by design - if
                // the jump does not produce DDL we surface the failure honestly.
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(200);
                    string ddl = NativeBridgeService.ConsumeLastCapturedDdl();
                    if (!string.IsNullOrEmpty(ddl))
                    {
                        log?.Invoke($"  [WPT] DDL captured after Preview jump ({ddl.Length} chars) at {(i + 1) * 200}ms");
                        LastCapturedWizardDdl = ddl;
                        return true;
                    }
                }

                log?.Invoke("  [WPT] Preview jump did not produce DDL within 4s");
                return false;
            }
            finally
            {
                try { overlayToggle?.Invoke(true); } catch { }
            }
        }

        /// <summary>
        /// Static stash for DDL captured during ClickWizardPreviewTab's
        /// Next-loop. Set by ClickWizardPreviewTab when bridge buffer has
        /// content; consumed by RunFromDbDdlPipelineAsync. Avoids
        /// double-consume race between Next-loop poll and caller poll.
        /// </summary>
        public static string LastCapturedWizardDdl { get; private set; }

        public static void ClearLastCapturedWizardDdl() { LastCapturedWizardDdl = null; }

        /// <summary>
        /// FE Alter Script wizard'in Generate/Next adimi sirasinda
        /// "Use current diagram selections? You have N entity selected"
        /// child popup'ini detect eder ve "No" ile dismiss eder. Wizard'i
        /// kapatmadan once bu modal popup kapatilmali (parent wizard child
        /// popup acikken kapanmiyor). 2026-04-27 ekran goruntusunde
        /// dogrulandi: pipeline sonunda wizard Preview tab'inda DDL
        /// gozukurken bu popup acik kaliyor.
        /// </summary>
        public static async Task DismissUseCurrentDiagramPopupAsync(Action<string> log)
        {
            await Task.Run(() =>
            {
                // 200ms is enough: when this popup appears (Generate / Next on
                // a page with diagram selections) it shows within ~100ms; the
                // jump path (1x Next + nav click on Preview) almost never raises
                // it, so we should not waste 500ms waiting on the critical path
                // before the DDL approval dialog renders.
                IntPtr popup = WaitForDialog("erwin Data Modeler", 200);
                if (popup == IntPtr.Zero) return;
                string title = GetTitle(popup);
                log?.Invoke($"  [POP] erwin popup detected hwnd=0x{popup.ToInt64():X} title='{title}' - dismissing with No");
                try
                {
                    ClickDialogButtonByTextWin32(popup, new[] { "No", "Hayir", "Hayır" }, log);
                }
                catch (Exception ex) { log?.Invoke($"  [POP] dismiss err: {ex.Message}"); }
                Thread.Sleep(300);
            });
        }

        /// <summary>
        /// FE Alter Script wizard'i WM_COMMAND IDCANCEL ile temiz kapatir.
        /// 1.5sn bekler. Hala canli ise son care olarak ForceDestroy. Bu
        /// MFC OnCancel handler path'ini kullanir; ForceDestroy yan etkili
        /// idi (FEW-CTOR cached state inconsistent -> ~3sn sonra erwin
        /// sessizce oluyor; doğrulama: 11:46:39 force-destroy + 11:46:42
        /// silent erwin death).
        /// </summary>
        public static async Task CloseFEWizardCleanAsync(IntPtr wizardHwnd, Action<string> log)
        {
            if (wizardHwnd == IntPtr.Zero || !IsWindow(wizardHwnd)) return;

            log?.Invoke($"  [WC] closing FE wizard via IDCANCEL 0x{wizardHwnd.ToInt64():X}");
            const int IDCANCEL = 2;
            try { PostMessage(wizardHwnd, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero); }
            catch (Exception ex) { log?.Invoke($"  [WC] PostMessage err: {ex.Message}"); }

            // Poll until the wizard window is gone, up to 1500ms (was a fixed
            // 1500ms sleep). Typical IDCANCEL closes the wizard in 200-500ms;
            // returning the moment it is gone shaves ~1s off the critical path
            // before the DDL approval dialog renders. If it is still alive at
            // the cap, fall through to the ForceDestroy branch below.
            for (int waited = 0; waited < 1500; waited += 100)
            {
                if (!IsWindow(wizardHwnd))
                {
                    log?.Invoke($"  [WC] wizard gone after {waited}ms");
                    break;
                }
                await Task.Delay(100);
            }

            if (IsWindow(wizardHwnd))
            {
                log?.Invoke("  [WC] wizard still alive after IDCANCEL - last-resort ForceDestroy");
                try { NativeBridgeService.ForceDestroyWizard(wizardHwnd, log); }
                catch (Exception ex) { log?.Invoke($"  [WC] ForceDestroy err: {ex.Message}"); }
            }
            else
            {
                log?.Invoke("  [WC] wizard closed cleanly via IDCANCEL");
            }
        }

        /// <summary>
        /// From-DB-ozel temiz CC + RD kapatma. Mevcut CloseSession metodu
        /// (Mart-Mart icin) ForceDestroy kullaniyor; bu CC engine'i
        /// inconsistent state'e sokup ~3sn sonra erwin'in sessizce
        /// olmesine sebep oluyor (memory: reference_cross_version_orphan
        /// _unsolved.md "erwin locks within ~3min", 13:00:43.991 force-
        /// destroy + 13:00:47 sonrasi silent erwin death).
        ///
        /// CloseFEWizardCleanAsync ile ayni pattern: WM_COMMAND IDCANCEL
        /// ile MFC OnCancel handler path'inden git, son care ForceDestroy.
        /// Mart-Mart'a dokunulmadi (CloseSession ayni kalir).
        /// </summary>
        /// <summary>
        /// Review-path teardown (2026-05-29, synchronous - call via Task.Run so
        /// erwin's STA UI thread stays free to surface its dialogs). Gracefully
        /// IDCANCELs the RD then the CC wizard (releases the ;Duplicate=YES left
        /// copy), then WM_CLOSEs the loaded version child (v1) - adopting a
        /// LATE-arriving child by title first when the open-wait had timed out.
        /// The close-dialog cascade ("Save Models" / "Close Model" for a dirty
        /// child, "Mart Offline" for a clean Mart child - possibly with NO Save
        /// Models before it) is swept in any order, then the child's death is
        /// VERIFIED (IsWindow) with one WM_CLOSE retry; an unclosable child is
        /// reported loudly so the caller's reconnect guard + user warning take
        /// over (2026-06-10 rework - the old gated chain declared "teardown
        /// clean" while the child was still open). A hard single-row guard
        /// (the dialog only ever lists the one model being closed = v1; the
        /// active dirty v2 is never in it) means v2 can never be touched; if
        /// the guard trips we leave the dialogs for the user. Finishes by
        /// re-activating the user's original MDI child. The reconnect timer's
        /// modal-guard + the pipeline flag staying true keep erwin responsive
        /// while the dialogs are up.
        /// </summary>
        public static void CloseReviewSession(CCSession s, Action<string> log)
        {
            if (s == null) return;
            const int IDCANCEL = 2;
            try
            {
                // Late-wizard adoption (2026-06-10 crash fix): when the launch
                // was posted but the wizard never got captured (the open-wait
                // timed out while erwin was still building the ;Duplicate=YES
                // copy), the wizard can appear AFTER the abort. Left alone it
                // keeps the Duplicate PU alive; the user eventually closes the
                // wizard manually, erwin releases the Duplicate, and any
                // add-in dispatch on a stale binding to it AVs natively
                // (erwin crash 10:47, coreclr 0xC0000005 in
                // ParseActivePuVersion). Watch for it briefly and adopt it so
                // the IDCANCEL below releases the Duplicate under our control.
                // Skipped on the refusal path (the captured "no changes" box
                // was already dismissed; no wizard is coming).
                if (s.WizardLaunchPosted && !s.ReviewRefusedNoChanges
                    && s.CCWizard == IntPtr.Zero && s.DialogsBeforeWizard != null)
                {
                    IntPtr lateWiz = WaitForNewDialog(s.DialogsBeforeWizard, "late compare wizard", 15000, log);
                    if (lateWiz != IntPtr.Zero)
                    {
                        // Don't adopt the no-changes info box as a wizard here
                        // either (same validation as the launch path).
                        string lateText = CollectDialogStaticText(lateWiz);
                        if (lateText.IndexOf("There have been no changes", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            log?.Invoke("  [REVIEW-CLEAN] late dialog is the 'no changes' info box - dismissing via OK, no wizard to cancel.");
                            ClickDialogButtonByTextWin32(lateWiz, new[] { "OK", "Tamam" }, log);
                        }
                        else
                        {
                            log?.Invoke($"  [REVIEW-CLEAN] adopted LATE compare wizard 0x{lateWiz.ToInt64():X} ('{GetTitle(lateWiz)}') - cancelling it so erwin releases the ;Duplicate copy.");
                            s.CCWizard = lateWiz;
                        }
                    }
                    else
                    {
                        log?.Invoke("  [REVIEW-CLEAN] no late compare wizard appeared within 15 s - if erwin shows one later, close it manually (Cancel).");
                    }
                }

                if (s.ResolveDifferences != IntPtr.Zero && IsWindow(s.ResolveDifferences))
                {
                    log?.Invoke($"  [REVIEW-CLEAN] IDCANCEL RD 0x{s.ResolveDifferences.ToInt64():X}");
                    PostMessage(s.ResolveDifferences, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
                    Thread.Sleep(800);
                }
                if (s.CCWizard != IntPtr.Zero && IsWindow(s.CCWizard))
                {
                    log?.Invoke($"  [REVIEW-CLEAN] IDCANCEL CC wizard 0x{s.CCWizard.ToInt64():X} (lets erwin release the ;Duplicate=YES left copy)");
                    PostMessage(s.CCWizard, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
                    Thread.Sleep(800);
                }

                // Late-arrival adoption (2026-06-10): when the open-wait timed
                // out, VersionChild is Zero but the version copy's window may
                // exist anyway (it appeared after the abort on a cold erwin).
                // Find it by version-token title, excluding every child that
                // already existed before the pipeline (so a tab the USER had
                // open is never touched) - and only when the pipeline really
                // clicked the picker's Open (OpenPosted). POLLED for up to
                // 10 s: this branch fires precisely because the Mart:// title
                // had not landed within the open-wait, so a single instant
                // scan would usually miss the still-loading child (review
                // finding); the poll exits as soon as the title lands.
                if (s.VersionChild == IntPtr.Zero && s.OpenPosted && s.RightVersion > 0)
                {
                    int lateWaited = 0;
                    while (lateWaited < 10000)
                    {
                        IntPtr late = FindPipelineVersionChild(s, log);
                        if (late != IntPtr.Zero)
                        {
                            log?.Invoke($"  [REVIEW-CLEAN] adopted LATE version child 0x{late.ToInt64():X} ('{GetTitle(late)}') after {lateWaited} ms - the open-wait had timed out before it appeared.");
                            s.VersionChild = late;
                            break;
                        }
                        Thread.Sleep(250);
                        lateWaited += 250;
                    }
                    if (s.VersionChild == IntPtr.Zero)
                        log?.Invoke("  [REVIEW-CLEAN] late-child poll expired (10 s) - no pipeline version child found by title; the caller's reconnect guard + warning own any surviving PU.");
                }

                // WM_CLOSE the loaded version child (v1) by HANDLE. erwin's CC
                // engine dirties v1 during the compare (verified by the
                // [REVIEW-DIRTY] checkpoints), so this raises a "Save Models"
                // prompt that the sweep below handles.
                bool childKnownGone;
                if (s.VersionChild != IntPtr.Zero && IsWindow(s.VersionChild))
                {
                    string vt = GetTitle(s.VersionChild);
                    bool wasDirty = vt.Contains("*");
                    log?.Invoke($"  [REVIEW-CLEAN] WM_CLOSE version child 0x{s.VersionChild.ToInt64():X} (dirty={(wasDirty ? "YES" : "no")}) ('{vt}')");
                    PostMessage(s.VersionChild, WM_CLOSE_MSG, IntPtr.Zero, IntPtr.Zero);
                    Thread.Sleep(600);
                    childKnownGone = !IsWindow(s.VersionChild);
                    if (childKnownGone)
                        log?.Invoke("  [REVIEW-CLEAN] version child closed cleanly (no prompt) - session back to the active model only.");
                    else
                        log?.Invoke("  [REVIEW-CLEAN] version child still alive after WM_CLOSE - sweeping the close-dialog cascade.");
                }
                else
                {
                    log?.Invoke("  [REVIEW-CLEAN] version child gone/invalid - nothing to close.");
                    childKnownGone = true;
                }

                // Close-dialog cascade sweep, in ANY order (2026-06-10 rework).
                // A version child DIRTIED by the compare raises "Save Models"
                // (or "Close Model"); a CLEAN Mart-opened child can skip both
                // and raise "Mart Offline" DIRECTLY. The old chain reached the
                // Mart Offline handler only after a successful Save Models
                // dismiss and returned "teardown clean" when no dialog showed -
                // a false positive: on 2026-06-10 the v1 child had not closed
                // at all, its PU survived, and the resumed reconnect tick
                // adopted and UDP-dirtied it. All dismissal is PURE WIN32
                // (2026-06-02): UIA on these dialogs leaves abandoned XTP
                // IAccessible RCWs that crash erwin's finalizer.
                // CloseSaveModelsWin32 keeps guardSingleRow=true so the active
                // dirty model (never listed in that dialog) cannot be touched;
                // if the guard trips we leave every dialog for the user.
                // childKnownGone trims the dead waits: Save Models / Close
                // Model are raised BEFORE the child window is destroyed, so a
                // child already observed dead can no longer raise them - only
                // the trailing Mart Offline can still arrive.
                if (!SweepVersionChildCloseDialogs(log, childKnownGone, quickRetry: false))
                {
                    return;
                }

                // VERIFY the version child is actually gone - the whole point
                // of the rework. One retry (WM_CLOSE + a shortened sweep: the
                // cascade already had one full-length pass); if it still
                // survives, say so loudly and leave it: the caller's reconnect
                // guard keeps ignoring its PU and warns the user to close it
                // manually without saving.
                if (s.VersionChild != IntPtr.Zero)
                {
                    if (!WaitForWindowGone(s.VersionChild, 5000))
                    {
                        log?.Invoke("  [REVIEW-CLEAN] version child STILL OPEN after sweep - retrying WM_CLOSE once.");
                        PostMessage(s.VersionChild, WM_CLOSE_MSG, IntPtr.Zero, IntPtr.Zero);
                        Thread.Sleep(600);
                        if (!SweepVersionChildCloseDialogs(log, childKnownGone: false, quickRetry: true))
                        {
                            return;
                        }
                        if (WaitForWindowGone(s.VersionChild, 3000))
                            log?.Invoke("  [REVIEW-CLEAN] version child closed on retry.");
                        else
                            log?.Invoke("  [REVIEW-CLEAN] version child COULD NOT be closed - leaving it open; the reconnect guard keeps its PU ignored and the caller warns the user to close it without saving.");
                    }
                    else
                    {
                        log?.Invoke("  [REVIEW-CLEAN] version child verified closed.");
                    }
                }

                // Hand focus back to the model the user was working on. After
                // the sweep the active MDI child may be the version copy (when
                // it survived) or whatever erwin promoted after the close; a
                // wrong active tab makes the main-window title disagree with
                // the add-in's bound model, which the tab-switch detector
                // would read as a user switch. (The pipeline-owned guard also
                // protects against that, but correct focus is the right end
                // state regardless.)
                if (s.OriginalChild != IntPtr.Zero && IsWindow(s.OriginalChild))
                {
                    IntPtr erwinMain = FindErwinMain();
                    IntPtr mdiClient = erwinMain != IntPtr.Zero ? FindMdiClientOf(erwinMain) : IntPtr.Zero;
                    if (mdiClient != IntPtr.Zero)
                        ActivateMdiChildHandle(mdiClient, s.OriginalChild, log);
                }
            }
            catch (Exception ex) { log?.Invoke($"  [REVIEW-CLEAN] threw: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>
        /// One pass over the close-dialog cascade a closing Mart version child
        /// can raise: "Save Models" / "Close Model" first (dirty child), then
        /// "Mart Offline" UNCONDITIONALLY (a clean Mart child raises it with no
        /// Save Models before it). Returns false when the Save Models dismiss
        /// safely aborted (single-row guard / cursor-land safety) - every
        /// dialog is then deliberately left for the user and the caller must
        /// stop sweeping. <paramref name="childKnownGone"/> skips the Save
        /// Models / Close Model waits (those prompts are raised BEFORE the
        /// child window is destroyed, so a child already observed dead cannot
        /// raise them) - only the trailing Mart Offline watch remains.
        /// <paramref name="quickRetry"/> halves the waits for the second
        /// (retry) pass, whose cascade already had one full-length watch.
        /// </summary>
        private static bool SweepVersionChildCloseDialogs(Action<string> log, bool childKnownGone, bool quickRetry)
        {
            IntPtr dlg = IntPtr.Zero;
            if (!childKnownGone)
            {
                dlg = WaitForDialog("Save Models", quickRetry ? 1500 : 3000);
                if (dlg == IntPtr.Zero) { dlg = WaitForDialog("Close Model", quickRetry ? 750 : 1500); }
                if (dlg != IntPtr.Zero)
                {
                    if (!CloseSaveModelsWin32(dlg, log, guardSingleRow: true))
                    {
                        log?.Invoke("  [REVIEW-CLEAN] Save Models Win32 dismiss aborted (guard/safety) - dialogs LEFT for the user; skipping Mart Offline.");
                        return false;
                    }
                }
            }
            // Longer watch when no Save Models showed: the clean-child path
            // goes straight to Mart Offline and the cascade may still be
            // unwinding.
            IntPtr martOff = WaitForDialog("Mart Offline", dlg == IntPtr.Zero ? (quickRetry ? 1500 : 2500) : 1500);
            if (martOff != IntPtr.Zero)
            {
                log?.Invoke($"  [REVIEW-CLEAN] Mart Offline 0x{martOff.ToInt64():X} - dismissing via pure Win32 (no UIA).");
                DismissMartOfflineDialogWin32(martOff, log);
            }
            else
            {
                log?.Invoke("  [REVIEW-CLEAN] no Mart Offline dialog in this sweep.");
            }
            return true;
        }

        /// <summary>
        /// Finds an MDI child created DURING the pipeline whose title carries
        /// the compare RIGHT version token (e.g. ": v1 :" inside
        /// "Mart://...MetaRepo :  v1  : ER_Diagram_164"). Children recorded in
        /// <see cref="CCSession.PreexistingChildren"/> are excluded so a tab
        /// the user had open before the run can never match.
        /// </summary>
        private static IntPtr FindPipelineVersionChild(CCSession s, Action<string> log)
        {
            try
            {
                IntPtr erwinMain = FindErwinMain();
                IntPtr mdiClient = erwinMain != IntPtr.Zero ? FindMdiClientOf(erwinMain) : IntPtr.Zero;
                if (mdiClient == IntPtr.Zero) return IntPtr.Zero;
                var token = new System.Text.RegularExpressions.Regex(
                    @":\s*v" + s.RightVersion + @"\s*:",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var h in EnumMartMdiChildHandles(mdiClient))
                {
                    if (h == s.OriginalChild) continue;
                    if (s.PreexistingChildren != null && s.PreexistingChildren.Contains(h)) continue;
                    if (token.IsMatch(GetTitle(h)))
                        return h;
                }
            }
            catch (Exception ex) { log?.Invoke($"  [REVIEW-CLEAN] late-child scan err: {ex.Message}"); }
            return IntPtr.Zero;
        }

        /// <summary>True once the window handle is gone/destroyed within the
        /// timeout; false when it is still alive at the deadline.</summary>
        private static bool WaitForWindowGone(IntPtr hwnd, int timeoutMs)
        {
            if (hwnd == IntPtr.Zero) return true;
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (!IsWindow(hwnd)) return true;
                Thread.Sleep(250);
                waited += 250;
            }
            return !IsWindow(hwnd);
        }

        /// <summary>
        /// Closes the "Save Models" prompt WITHOUT saving the loaded version
        /// (v1), then drives the follow-up "Mart Offline" dialog to
        /// Save-to=Close. Steps (the user's verified manual flow): (1) hard
        /// single-row guard - abort (leave for manual) unless the dialog lists
        /// EXACTLY one data row, so the active dirty v2 (never in this dialog)
        /// can never be touched; (2) uncheck the single save-row's first
        /// checkbox column via mouse simulation - the checkboxes are XTP
        /// custom-drawn so UIA TogglePattern is unavailable; the click point is
        /// the first checkbox-column HEADER rect (X) at the data-row cell rect
        /// (Y), both from UIA BoundingRectangle (physical screen px, so no
        /// ClientToScreen); (3) click OK to commit the don't-save; (4) hand the
        /// resulting "Mart Offline" dialog to the proven DismissMartOfflineDialog
        /// (toolbar 3rd button = Set All to Close + OK). The model opens
        /// "Unlocked", so there is a single (save) checkbox - no lock column.
        /// </summary>
        /// <summary>
        /// Pure-Win32 (NO UI Automation) dismissal of the From-DB "Save Models"
        /// dialog. The UIA-based CloseSaveModelsWithoutSaving creates XTP
        /// IAccessible RCWs (full dump erwin.exe.48868.dmp = ToolkitPro
        /// CXTPAccessible / Accessibility.IAccessible) that, abandoned to GC,
        /// crash erwin's finalizer at teardown. This variant avoids UIA entirely,
        /// using facts learned from a [RECON-CMD] WndProc capture of a real user
        /// click: the dialog's grid is child control id=2050, the OK button is
        /// id=1, and OK fires as WM_COMMAND(MAKEWPARAM(1,0), lParam=okButtonHWND)
        /// - the lParam=button-HWND is MANDATORY (the XTP handler ignored the old
        /// WM_COMMAND IDOK with lParam=0, which is why that attempt failed). The
        /// save checkbox has no UIA-readable / Win32-readable state, so we mouse-
        /// sim a click on column-0 of the first data row at a coord derived from
        /// the grid's OWN window rect + offsets calibrated from the captured UIA
        /// coord (checkbox col center grid.Left+12, first row center grid.Top+31;
        /// from UIA click 2619,466 vs header rect 2607,435). Keeps the SAME
        /// WindowFromPoint + landed-OK safety as the UIA version: if the click
        /// cannot be confirmed on the dialog it ABORTS WITHOUT clicking OK
        /// (returns false) so a mis-derived coord can NEVER trigger a spurious
        /// save - the caller then leaves the dialog for manual handling. Returns
        /// true if it unchecked + posted OK, false if it safely aborted.
        /// </summary>
        private static bool CloseSaveModelsWin32(IntPtr dlg, Action<string> log, bool guardSingleRow = false)
        {
            try
            {
                IntPtr grid  = GetDlgItem(dlg, 2050);   // XTP report grid (from RECON capture)
                IntPtr okBtn = GetDlgItem(dlg, 1);      // OK button (IDOK)
                if (grid == IntPtr.Zero || okBtn == IntPtr.Zero)
                {
                    log?.Invoke($"  [SM-WIN32] grid(2050)=0x{grid.ToInt64():X} ok(1)=0x{okBtn.ToInt64():X} - one missing, ABORT (leave for manual).");
                    return false;
                }

                // v2 PROTECTION (Review / cross-version): the Save Models dialog must
                // list EXACTLY the one model we are closing. The active dirty model
                // (Review's v2) is a separate, un-closed MDI child that erwin never
                // lists here - but if it ever did, unchecking only row 0 + OK would
                // SAVE it as a spurious Mart version. The old UIA path counted '*'
                // name cells; do the Win32 equivalent when the grid is a standard
                // list (LVM_GETITEMCOUNT). If the grid is the XTP report (not
                // LVM-countable) we cannot count, so we rely on the caller's SCOPED
                // close (it WM_CLOSEs only the specific child) and log the class so
                // this can be hardened later. From-DB passes guardSingleRow=false
                // (a single file-based model, no other model in the session).
                if (guardSingleRow)
                {
                    var gcls = new StringBuilder(64);
                    GetClassName(grid, gcls, gcls.Capacity);
                    string gc = gcls.ToString();
                    if (gc.IndexOf("SysListView32", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int rows = SendMessage(grid, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                        if (rows != 1)
                        {
                            log?.Invoke($"  [SM-WIN32] >>> ABORT: guardSingleRow expected exactly 1 row, found {rows} (v2 protection). Leaving for manual. <<<");
                            return false;
                        }
                        log?.Invoke("  [SM-WIN32] guardSingleRow OK: grid lists exactly 1 row.");
                    }
                    else
                    {
                        log?.Invoke($"  [SM-WIN32] guardSingleRow: grid class='{gc}' not LVM-countable (XTP report); relying on the caller's scoped single-child close (the active model is never listed here).");
                    }
                }

                if (!GetWindowRect(grid, out RECT gr))
                {
                    log?.Invoke("  [SM-WIN32] GetWindowRect(grid) failed - ABORT (leave for manual).");
                    return false;
                }
                int clickX = gr.left + 12;   // checkbox column-0 center (calibrated)
                int clickY = gr.top + 31;    // first data row center (calibrated)
                log?.Invoke($"  [SM-WIN32] grid rect=({gr.left},{gr.top},{gr.right},{gr.bottom}) -> checkbox click=({clickX},{clickY}); okBtn=0x{okBtn.ToInt64():X}");

                ForceForeground(dlg);
                Thread.Sleep(250);

                // Same safety as the UIA path: the click point MUST resolve to the
                // Save Models dialog; if something covers it, ABORT before clicking
                // OK (never risk a save). NOTE this confirms "on the dialog", not
                // "on the checkbox cell" specifically - so the landed-OK check + the
                // ABORT-on-no-land below are the real guards against a stray save.
                IntPtr atPoint = WindowFromPoint(new POINT { X = clickX, Y = clickY });
                IntPtr atRoot = GetAncestor(atPoint, GA_ROOT);
                if (atRoot != dlg && atPoint != dlg)
                {
                    log?.Invoke($"  [SM-WIN32] click point covered by another window (atRoot=0x{atRoot.ToInt64():X} dlg=0x{dlg.ToInt64():X}) - ABORT (leave for manual).");
                    return false;
                }

                if (!GetCursorPos(out POINT savedCur)) { log?.Invoke("  [SM-WIN32] GetCursorPos failed - ABORT."); return false; }
                bool landedOk = false;
                bool clipped = ClipCursorAround(clickX, clickY);
                try
                {
                    ShowCursor(false);
                    SetCursorPos(clickX, clickY);
                    Thread.Sleep(40);
                    GetCursorPos(out POINT landed);
                    landedOk = Math.Abs(landed.X - clickX) <= 3 && Math.Abs(landed.Y - clickY) <= 3;
                    if (landedOk)
                    {
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                        Thread.Sleep(40);
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                    }
                }
                finally
                {
                    if (clipped) ReleaseCursorClip();
                    try { SetCursorPos(savedCur.X, savedCur.Y); ShowCursor(true); } catch { }
                }
                if (!landedOk)
                {
                    log?.Invoke("  [SM-WIN32] cursor did not land on target - ABORT, NOT clicking OK (avoid spurious save). Leave for manual.");
                    return false;
                }

                // OK via the captured WM_COMMAND form. lParam MUST be the OK button
                // HWND - the XTP dialog ignores WM_COMMAND IDOK with lParam=0.
                Thread.Sleep(300);
                PostMessage(dlg, WM_COMMAND, MakeWParam(IDOK, 0), okBtn);
                log?.Invoke("  [SM-WIN32] OK posted (WM_COMMAND IDOK, lParam=okBtn) - save unchecked, model discarded. NO UIA used.");
                Thread.Sleep(500);
                return true;
            }
            catch (Exception ex) { log?.Invoke($"  [SM-WIN32] threw: {ex.Message} - ABORT (leave for manual)."); return false; }
        }

        private static void CloseSaveModelsWithoutSaving(IntPtr dlg, Action<string> log)
        {
            try
            {
                var root = AutomationElement.FromHandle(dlg);
                if (root == null) { log?.Invoke("  [SM-CLOSE] UIA root null"); return; }

                // (1) Single-row guard: count dirty model rows by their
                // "Model_X*" name cell (the '*' marker is on the model-name cell
                // only; one per row). This works for BOTH the Mart "Save Models"
                // (Review teardown) and the file-based "Save Models" that closing
                // the From-DB RE'd in-memory model raises - the path column
                // differs ("Mart://" vs "C:\...") but the dirty name cell is the
                // same. Exactly-one-row keeps the active dirty v2 (Review) safe.
                var customs = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom));
                int rowCount = 0;
                AutomationElement modelCell = null;
                foreach (AutomationElement c in customs)
                {
                    string n = ""; try { n = c.Current.Name ?? ""; } catch { }
                    if (n.Contains("*")) { rowCount++; if (modelCell == null) modelCell = c; }
                }
                log?.Invoke($"  [SM-CLOSE] dirty model rows ('*' name cells) = {rowCount}");
                if (rowCount != 1)
                {
                    log?.Invoke($"  [SM-CLOSE] >>> ABORT: expected exactly 1 row, found {rowCount}. Leaving dialog for manual (v2 protection). <<<");
                    return;
                }
                if (modelCell == null) { log?.Invoke("  [SM-CLOSE] could not locate model-name cell (Model_X*); ABORT (leaving for manual)"); return; }

                // (2) Geometry: first checkbox-column header X, data row Y.
                System.Windows.Rect rowRect = default;
                try { rowRect = modelCell.Current.BoundingRectangle; } catch { }
                var headers = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Header));
                System.Windows.Rect firstColRect = default; bool haveCol = false;
                int hi = 0;
                foreach (AutomationElement h in headers)
                {
                    string hn = ""; try { hn = h.Current.Name ?? ""; } catch { }
                    System.Windows.Rect hr = default; try { hr = h.Current.BoundingRectangle; } catch { }
                    log?.Invoke($"  [SM-CLOSE]   header[{hi}] name='{hn}' rect=({(int)hr.Left},{(int)hr.Top},{(int)hr.Right},{(int)hr.Bottom})");
                    if (!haveCol && string.IsNullOrEmpty(hn) && hr.Width > 2 && hr.Width < 80)
                    {
                        firstColRect = hr; haveCol = true;
                    }
                    hi++;
                }
                if (!haveCol || rowRect.Height <= 0)
                {
                    log?.Invoke("  [SM-CLOSE] could not derive checkbox column / row geometry; ABORT (leaving for manual - do NOT click OK with the save box still checked).");
                    return;
                }

                // UIA BoundingRectangle is physical screen px; SendMouseClickAt
                // expects screen coords -> no ClientToScreen (unlike the
                // LVM_GETSUBITEMRECT Apply-to-Right path).
                int clickX = (int)(firstColRect.Left + firstColRect.Width / 2);
                int clickY = (int)(rowRect.Top + rowRect.Height / 2);
                log?.Invoke($"  [SM-CLOSE] unchecking save column at screen=({clickX},{clickY})");
                // Force the dialog foreground + active from this bg thread: the
                // XTP report grid only processes the checkbox hit-test when its
                // window is active, and a bare SetForegroundWindow fails from a
                // non-foreground thread (verified 2026-05-29: prod run left the
                // save box checked -> v1 not closed, 2 PUs). ForceForeground
                // uses AttachThreadInput to lift the foreground-lock.
                ForceForeground(dlg);
                Thread.Sleep(250);

                // Confirm the checkbox screen point actually belongs to the Save
                // Models dialog (not the busy overlay / another window covering
                // it). If something else is on top, the mouse click would hit
                // THAT and silently no-op, then OK would SAVE v1 (the merge
                // prompt). So if the point is not within the dialog, ABORT
                // before clicking - leave it for manual (never risk a save).
                IntPtr atPoint = WindowFromPoint(new POINT { X = clickX, Y = clickY });
                IntPtr atRoot = GetAncestor(atPoint, GA_ROOT);
                log?.Invoke($"  [SM-CLOSE] WindowFromPoint({clickX},{clickY})=0x{atPoint.ToInt64():X} root=0x{atRoot.ToInt64():X} (dlg=0x{dlg.ToInt64():X})");
                if (atRoot != dlg && atPoint != dlg)
                {
                    log?.Invoke("  [SM-CLOSE] >>> ABORT: click point is covered by another window (not the Save Models dialog) - NOT clicking (avoid spurious save). Leaving for manual. <<<");
                    return;
                }

                // VERIFIED click: confirm the cursor actually reached the
                // checkbox cell BEFORE firing. The checkbox state itself is not
                // UIA-readable (XTP custom-drawn), so we cannot confirm the
                // toggle - but we CAN confirm the cursor landed on target. If
                // SetCursorPos clipped (DPI / multi-monitor), ABORT WITHOUT
                // clicking OK: OK with the save box still checked would SAVE v1
                // (a spurious Mart version). The dialog is left for manual.
                if (!GetCursorPos(out POINT savedCur))
                {
                    log?.Invoke("  [SM-CLOSE] GetCursorPos failed; ABORT (leaving for manual, no OK)");
                    return;
                }
                bool landedOk = false;
                // Pin the cursor to a 5x5 box around the target so user mouse
                // movement during the Sleep(40)+down+sleep+up window cannot
                // displace it (which would trip the strict landedOk check and
                // ABORT before the uncheck, leaving v1 unsaved-but-still-
                // checked - i.e. needing manual handling). User-reported
                // failure 2026-05-30: (749,202) != (2656,509) ABORT.
                bool smClipped = ClipCursorAround(clickX, clickY);
                try
                {
                    ShowCursor(false);
                    SetCursorPos(clickX, clickY);
                    Thread.Sleep(40);
                    GetCursorPos(out POINT landed);
                    landedOk = Math.Abs(landed.X - clickX) <= 3 && Math.Abs(landed.Y - clickY) <= 3;
                    if (!landedOk)
                    {
                        log?.Invoke($"  [SM-CLOSE] cursor landed ({landed.X},{landed.Y}) != target ({clickX},{clickY}) - ABORT, NOT clicking OK (avoid spurious save). Leaving for manual.");
                    }
                    else
                    {
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                        Thread.Sleep(40);
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                    }
                }
                finally
                {
                    if (smClipped) ReleaseCursorClip();
                    try { SetCursorPos(savedCur.X, savedCur.Y); ShowCursor(true); } catch { }
                }
                if (!landedOk) return;   // never click OK if the uncheck click did not land

                // (3) Click OK to commit the don't-save via a TARGETED UIA
                // Invoke on the OK button (AutomationId="1"). WM_COMMAND IDOK
                // did NOT close this XTP dialog (verified 2026-05-29 prod: the
                // dialog stayed open). UIA Invoke works (proven in debug); the
                // earlier 11s slowness was the full descendant scan in
                // ClickButtonByName - FindFirst on id=1 is fast.
                Thread.Sleep(300);
                bool okClicked = false;
                try
                {
                    var okBtn = root.FindFirst(TreeScope.Descendants,
                        new AndCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                            new PropertyCondition(AutomationElement.AutomationIdProperty, "1")));
                    if (okBtn != null && okBtn.TryGetCurrentPattern(InvokePattern.Pattern, out object okInv))
                    {
                        ((InvokePattern)okInv).Invoke();
                        okClicked = true;
                        log?.Invoke("  [SM-CLOSE] OK invoked via UIA (id=1) - save unchecked -> v1 will NOT be saved");
                    }
                }
                catch (Exception ex) { log?.Invoke($"  [SM-CLOSE] OK UIA err: {ex.Message}"); }
                if (!okClicked)
                {
                    log?.Invoke("  [SM-CLOSE] OK UIA not found - fallback WM_COMMAND IDOK");
                    PostMessage(dlg, WM_COMMAND, MakeWParam(IDOK, 0), IntPtr.Zero);
                }
                Thread.Sleep(500);

                // (4) Drive the follow-up "Mart Offline" dialog to Save-to=Close.
                // 500ms is enough: when it appears it shows within ~300ms; the
                // From-DB RE'd model is file-based and NEVER raises it (verified
                // across multiple runs - "no Mart Offline dialog (suppressed)").
                // The DDL approval dialog opens during this teardown and is
                // briefly buried behind erwin's Save Models flash; trimming this
                // dead-wait from 1500ms to 500ms shrinks that "behind" window
                // by ~1s and lets the reclaim bring it back to the front sooner.
                IntPtr martOffline = WaitForDialog("Mart Offline", 500);
                if (martOffline == IntPtr.Zero)
                {
                    log?.Invoke("  [SM-CLOSE] no Mart Offline dialog (suppressed or v1 closed directly) - done.");
                    return;
                }
                log?.Invoke($"  [SM-CLOSE] Mart Offline 0x{martOffline.ToInt64():X} - setting Save-to=Close + OK via DismissMartOfflineDialog");
                DismissMartOfflineDialog(martOffline, log);
            }
            catch (Exception ex) { log?.Invoke($"  [SM-CLOSE] threw: {ex.Message}"); }
        }

        public static async Task CloseDbCCSessionCleanAsync(CCSession s, Action<string> log)
        {
            if (s == null) return;
            const int IDCANCEL = 2;

            // Sira: ONCE RD (cocuk dialog), SONRA CC (parent wizard).
            // RD acikken CC'yi kapatmak race olusturabilir (RD CC'nin
            // child'i, parent kapanirken child'i WM_DESTROY yagmuruna
            // tutar). RD'yi IDCANCEL ile temiz kapat, sonra CC'yi.
            if (s.ResolveDifferences != IntPtr.Zero && IsWindow(s.ResolveDifferences))
            {
                log?.Invoke($"  [DB-CLEAN] closing RD via IDCANCEL 0x{s.ResolveDifferences.ToInt64():X}");
                try { PostMessage(s.ResolveDifferences, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero); }
                catch (Exception ex) { log?.Invoke($"  [DB-CLEAN] RD IDCANCEL err: {ex.Message}"); }
                await Task.Delay(800);

                if (IsWindow(s.ResolveDifferences))
                {
                    log?.Invoke("  [DB-CLEAN] RD still alive - last-resort ForceDestroy");
                    try { NativeBridgeService.ForceDestroyWizard(s.ResolveDifferences, log); }
                    catch (Exception ex) { log?.Invoke($"  [DB-CLEAN] RD ForceDestroy err: {ex.Message}"); }
                }
                else
                {
                    log?.Invoke("  [DB-CLEAN] RD closed cleanly via IDCANCEL");
                }
            }

            if (s.CCWizard != IntPtr.Zero && IsWindow(s.CCWizard))
            {
                log?.Invoke($"  [DB-CLEAN] closing CC wizard via IDCANCEL 0x{s.CCWizard.ToInt64():X}");
                try { PostMessage(s.CCWizard, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero); }
                catch (Exception ex) { log?.Invoke($"  [DB-CLEAN] CC IDCANCEL err: {ex.Message}"); }
                await Task.Delay(800);

                // Some erwin builds spawn a "Close Model?" confirmation
                // popup after CC IDCANCEL. The Mart-Mart CloseSession has
                // a HandleCloseModelDialogChain helper for this; we
                // delegate to it for consistency.
                try { HandleCloseModelDialogChain(log); } catch { }

                if (IsWindow(s.CCWizard))
                {
                    log?.Invoke("  [DB-CLEAN] CC still alive after IDCANCEL chain - last-resort ForceDestroy");
                    try { NativeBridgeService.ForceDestroyWizard(s.CCWizard, log); }
                    catch (Exception ex) { log?.Invoke($"  [DB-CLEAN] CC ForceDestroy err: {ex.Message}"); }
                }
                else
                {
                    log?.Invoke("  [DB-CLEAN] CC closed cleanly via IDCANCEL");
                }
            }
        }

        /// <summary>
        /// Switches erwin's active MDI child to the model whose title
        /// contains "Mart://" - i.e. back to the dirty mart model after a
        /// silent RE made the RE'd PU the foreground tab. Necessary so the
        /// CC wizard's "Left Model" defaults to the dirty mart (matching
        /// the Mart-Mart pipeline's direction); without this the wizard
        /// picks RE'd as left and we'd need an Apply-to-Left in RD instead
        /// of the proven Apply-to-Right.
        /// </summary>
        public static bool ActivateMartMdiChild(Action<string> log)
        {
            IntPtr erwinMain = FindErwinMain();
            if (erwinMain == IntPtr.Zero) { log?.Invoke("  [MDI-ACT] erwin main not found"); return false; }

            IntPtr mdiClient = IntPtr.Zero;
            EnumChildWindows(erwinMain, (h, _) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() == "MDIClient")
                {
                    mdiClient = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (mdiClient == IntPtr.Zero)
            {
                log?.Invoke("  [MDI-ACT] MDIClient not found - erwin not using standard MDI?");
                return false;
            }

            IntPtr martMdi = IntPtr.Zero;
            EnumChildWindows(mdiClient, (h, _) =>
            {
                string title = GetTitle(h);
                if (title.IndexOf("Mart://", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    martMdi = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (martMdi == IntPtr.Zero)
            {
                log?.Invoke("  [MDI-ACT] no MDI child with 'Mart://' in title");
                return false;
            }

            string activeTitle = GetTitle(martMdi);
            log?.Invoke($"  [MDI-ACT] activating MDI child hwnd=0x{martMdi.ToInt64():X} title='{activeTitle}'");
            const uint WM_MDIACTIVATE = 0x0222;
            SendMessage(mdiClient, WM_MDIACTIVATE, martMdi, IntPtr.Zero);
            Thread.Sleep(100);  // let the switch settle
            return true;
        }

        // ---- UI-Open version path (Faz 2) MDI helpers ----

        /// <summary>Finds erwin's MDIClient under the main frame, or Zero.</summary>
        private static IntPtr FindMdiClientOf(IntPtr main)
        {
            IntPtr mdi = IntPtr.Zero;
            if (main == IntPtr.Zero) return mdi;
            EnumChildWindows(main, (h, _) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() == "MDIClient") { mdi = h; return false; }
                return true;
            }, IntPtr.Zero);
            return mdi;
        }

        /// <summary>Direct MDI child frames whose title contains "Mart://".</summary>
        private static System.Collections.Generic.List<IntPtr> EnumMartMdiChildHandles(IntPtr mdiClient)
        {
            var list = new System.Collections.Generic.List<IntPtr>();
            if (mdiClient == IntPtr.Zero) return list;
            IntPtr child = GetWindow(mdiClient, GW_CHILD);
            while (child != IntPtr.Zero)
            {
                if (GetTitle(child).IndexOf("Mart://", StringComparison.OrdinalIgnoreCase) >= 0)
                    list.Add(child);
                child = GetWindow(child, GW_HWNDNEXT);
            }
            return list;
        }

        /// <summary>Every direct MDI child of the MDIClient, title-INDEPENDENT.
        /// Needed by the new-child wait: erwin creates the child window first
        /// and writes its final "Mart://..." title only once the model load
        /// completes, so a Mart-title filter misses a child that is still
        /// loading (2026-06-10 02:36 incident on a cold erwin).</summary>
        private static System.Collections.Generic.List<IntPtr> EnumAllMdiChildHandles(IntPtr mdiClient)
        {
            var list = new System.Collections.Generic.List<IntPtr>();
            if (mdiClient == IntPtr.Zero) return list;
            IntPtr child = GetWindow(mdiClient, GW_CHILD);
            while (child != IntPtr.Zero)
            {
                list.Add(child);
                child = GetWindow(child, GW_HWNDNEXT);
            }
            return list;
        }

        /// <summary>The currently active MDI child (WM_MDIGETACTIVE), or Zero.</summary>
        private static IntPtr GetActiveMdiChild(IntPtr mdiClient)
        {
            if (mdiClient == IntPtr.Zero) return IntPtr.Zero;
            return SendMessage(mdiClient, WM_MDIGETACTIVE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Reads erwin's GUI dirty indicator for the ACTIVE MDI child: the '*'
        /// in the child window title (e.g. "Mart://... :  v4  : ER_Diagram_1 * ").
        /// This is the signal that tracks erwin's Mart &gt; Review behavior in
        /// every logged 2026-06-10 incident (asterisk-titled runs opened
        /// Review, asterisk-free runs were refused), unlike SCAPI dirty
        /// probes: the Modified/IsDirty-style names do not exist on the
        /// r10.10 PU at all, and DirtyBit is proven to diverge from the GUI
        /// dirty state. Returns true=dirty, false=POSITIVELY clean,
        /// null=unknown (no erwin main / MDI child, or the active child is
        /// not a Mart model) - callers must treat null as "let erwin decide".
        /// </summary>
        public static bool? IsActiveMdiChildDirtyByTitle(Action<string> log)
        {
            try
            {
                IntPtr main = FindErwinMain();
                if (main == IntPtr.Zero) return null;
                IntPtr mdi = FindMdiClientOf(main);
                if (mdi == IntPtr.Zero) return null;
                IntPtr child = GetActiveMdiChild(mdi);
                if (child == IntPtr.Zero) return null;
                string t = GetTitle(child) ?? string.Empty;
                if (t.IndexOf("Mart://", StringComparison.OrdinalIgnoreCase) < 0) return null;
                return t.Contains("*");
            }
            catch (Exception ex)
            {
                log?.Invoke($"  [REVIEW] dirty-title probe threw: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Closes the From-DB silent-RE'd model's MDI tab, which otherwise
        /// lingers open after the pipeline (the active Mart model has a
        /// "Mart://" title; the RE'd in-memory model does NOT - that is how we
        /// tell them apart). WM_CLOSE the non-Mart child + click "No" on the
        /// resulting "Save changes to &lt;model&gt;?" prompt (the RE'd model is a
        /// throwaway - never save it). This is the UI close, NOT the SCAPI
        /// PUs.Remove (which invalidates the active Mart PU root and triggers a
        /// Session-lost cascade - see reference_cross_version_orphan_unsolved).
        /// Run via Task.Run (it blocks on Thread.Sleep + the dismiss watcher).
        /// </summary>
        public static void CloseReModelMdiChild(string reModelName, Action<string> log, bool leaveSaveDialogForUser = false, bool win32AutoDismiss = false)
        {
            IntPtr main = FindErwinMain();
            if (main == IntPtr.Zero) { log?.Invoke("  [DB-CLOSE] erwin main not found"); return; }
            IntPtr mdiClient = FindMdiClientOf(main);
            if (mdiClient == IntPtr.Zero) { log?.Invoke("  [DB-CLOSE] MDIClient not found"); return; }

            IntPtr reChild = IntPtr.Zero, firstNonMart = IntPtr.Zero;
            IntPtr child = GetWindow(mdiClient, GW_CHILD);
            while (child != IntPtr.Zero)
            {
                string t = GetTitle(child);
                if (!string.IsNullOrEmpty(t) && t.IndexOf("Mart://", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    if (firstNonMart == IntPtr.Zero) firstNonMart = child;
                    if (!string.IsNullOrEmpty(reModelName) && t.IndexOf(reModelName, StringComparison.OrdinalIgnoreCase) >= 0)
                    { reChild = child; break; }
                }
                child = GetWindow(child, GW_HWNDNEXT);
            }
            if (reChild == IntPtr.Zero) reChild = firstNonMart;
            if (reChild == IntPtr.Zero) { log?.Invoke("  [DB-CLOSE] no non-Mart RE'd model MDI child found - nothing to close"); return; }

            log?.Invoke($"  [DB-CLOSE] WM_CLOSE RE'd model child 0x{reChild.ToInt64():X} title='{GetTitle(reChild)}'");
            PostMessage(reChild, WM_CLOSE_MSG, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(800);

            // Closing a dirty in-memory model raises a "Save Models" grid
            // (Model_X* / <new file> / C:\...\My Models) - NOT a simple Yes/No
            // popup. Reuse the same uncheck-the-save-box + OK handler the Review
            // teardown uses (it now matches the dirty name cell, Mart or file).
            IntPtr sm = WaitForDialog("Save Models", 3000);
            if (sm == IntPtr.Zero) sm = WaitForDialog("Save Model", 1000);
            if (sm != IntPtr.Zero)
            {
                if (win32AutoDismiss)
                {
                    // 2026-06-02: dismiss the dialog with PURE WIN32 (no UI
                    // Automation) - see CloseSaveModelsWin32. This is what
                    // re-enables full auto-close without the UIA/IAccessible
                    // finalizer crash. If the Win32 path safely aborts (e.g. the
                    // calibrated checkbox coord cannot be confirmed on the dialog),
                    // it returns false and we LEAVE the dialog for the user - never
                    // risking a spurious save.
                    log?.Invoke($"  [DB-CLOSE] 'Save Models' dialog up 0x{sm.ToInt64():X} - dismissing via pure Win32 (no UIA).");
                    bool win32ok = CloseSaveModelsWin32(sm, log);
                    if (!win32ok)
                        log?.Invoke("  [DB-CLOSE] Win32 dismiss aborted safely - 'Save Models' dialog LEFT for the user to dismiss (choose Don't Save).");
                    Thread.Sleep(500);
                }
                else if (leaveSaveDialogForUser)
                {
                    // Fallback: WM_CLOSE is sent but the dialog is LEFT for the
                    // user to dismiss manually (a plain user click goes through
                    // erwin's own UI - no .NET UIA, no abandoned RCW, no crash).
                    log?.Invoke($"  [DB-CLOSE] 'Save Models' dialog up 0x{sm.ToInt64():X} - LEFT for the user to dismiss (choose Don't Save). NOT auto-handled: auto-dismiss UIA crashes erwin.");
                    return;
                }
                else
                {
                    log?.Invoke($"  [DB-CLOSE] 'Save Models' dialog up 0x{sm.ToInt64():X} - closing without saving (uncheck + OK)");
                    CloseSaveModelsWithoutSaving(sm, log);
                    Thread.Sleep(500);
                }
            }
            else
            {
                log?.Invoke("  [DB-CLOSE] no Save Models dialog - RE'd model closed silently (or a different prompt is up).");
            }
            log?.Invoke(IsWindow(reChild)
                ? "  [DB-CLOSE] RE'd model child still alive after close attempt - may need manual handling"
                : "  [DB-CLOSE] RE'd model child closed (discarded, not saved).");
        }

        /// <summary>Activate a SPECIFIC MDI child (used to flip Left back to the
        /// dirty model after opening the version child).</summary>
        private static void ActivateMdiChildHandle(IntPtr mdiClient, IntPtr child, Action<string> log)
        {
            if (mdiClient == IntPtr.Zero || child == IntPtr.Zero) return;
            log?.Invoke($"  [MDI-ACT] activating specific child 0x{child.ToInt64():X} title='{GetTitle(child)}'");
            SendMessage(mdiClient, WM_MDIACTIVATE_MSG, child, IntPtr.Zero);
            Thread.Sleep(150);
        }

        /// <summary>Poll until a NEW "Mart://" MDI child (not in <paramref name="before"/>)
        /// appears, returning its hwnd, or Zero on timeout.
        /// Two-stage wait (2026-06-10): erwin creates the MDI child window
        /// BEFORE the model finishes loading and writes the final "Mart://..."
        /// title only at the end. On a cold erwin + Mart roundtrip that gap
        /// blew the old single-stage Mart-titled scan (the v1 PU was already
        /// in the session but the child was reported missing, the pipeline
        /// aborted, and the leftover copy caused the reconnect/UDP-dirty
        /// incident). Stage 1 finds the new HANDLE title-independently;
        /// stage 2 waits for that handle's title to become a Mart locator.
        /// <paramref name="before"/> must therefore be snapshotted with
        /// EnumAllMdiChildHandles, not the Mart-filtered enumerator.</summary>
        private static IntPtr WaitForNewMartMdiChild(IntPtr mdiClient, System.Collections.Generic.HashSet<IntPtr> before, int timeoutMs, Action<string> log)
        {
            int waited = 0;
            IntPtr candidate = IntPtr.Zero;
            while (waited < timeoutMs)
            {
                if (candidate == IntPtr.Zero)
                {
                    foreach (var h in EnumAllMdiChildHandles(mdiClient))
                    {
                        if (!before.Contains(h))
                        {
                            candidate = h;
                            log?.Invoke($"  [MDI-OPEN] new MDI child handle 0x{h.ToInt64():X} (title='{GetTitle(h)}') - waiting for its Mart:// title (model still loading)");
                            break;
                        }
                    }
                }
                if (candidate != IntPtr.Zero)
                {
                    if (!IsWindow(candidate))
                    {
                        // The handle died mid-load (erwin recreates the frame in
                        // some open paths) - fall back to scanning for the next
                        // new handle instead of waiting on a corpse.
                        log?.Invoke($"  [MDI-OPEN] candidate child 0x{candidate.ToInt64():X} vanished - rescanning");
                        candidate = IntPtr.Zero;
                    }
                    else if (GetTitle(candidate).IndexOf("Mart://", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        log?.Invoke($"  [MDI-OPEN] new MDI child appeared 0x{candidate.ToInt64():X} title='{GetTitle(candidate)}' after {waited} ms");
                        return candidate;
                    }
                }
                Thread.Sleep(200);
                waited += 200;
            }
            log?.Invoke($"  [MDI-OPEN] no new Mart MDI child within {timeoutMs} ms"
                + (candidate != IntPtr.Zero
                    ? $" - a new child 0x{candidate.ToInt64():X} exists but its title never became Mart:// (current: '{GetTitle(candidate)}')"
                    : string.Empty));
            return IntPtr.Zero;
        }

        /// <summary>Gracefully close a specific MDI child via WM_CLOSE (runs
        /// erwin's normal model-close handler). For a CLEAN child this closes
        /// silently and releases the PU WITHOUT the PUs.Remove active-root
        /// invalidation (verified by the 2026-05-29 spike). Best-effort sweep of
        /// any Close Model / Mart Offline prompt that a dirty child might raise.</summary>
        public static void CloseMartMdiChild(IntPtr child, Action<string> log)
        {
            if (child == IntPtr.Zero || !IsWindow(child)) { log?.Invoke("  [MDI-CLOSE] child gone/invalid - nothing to close"); return; }
            log?.Invoke($"  [MDI-CLOSE] WM_CLOSE -> child 0x{child.ToInt64():X} title='{GetTitle(child)}'");
            PostMessage(child, WM_CLOSE_MSG, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(400);
            // A clean version child closes silently; if a prompt appears (dirty),
            // ride out the standard Close Model + Mart Offline chain.
            try
            {
                IntPtr closeDlg = WaitForDialog("Close Model", 1200);
                if (closeDlg != IntPtr.Zero) HandleCloseModelDialogChain(log);
            }
            catch (Exception ex) { log?.Invoke($"  [MDI-CLOSE] dialog sweep err: {ex.Message}"); }
        }

        /// <summary>
        /// Select the in-memory model whose version matches <paramref name="version"/>
        /// from the wizard Right Model page "Open Models in Memory" DataGrid
        /// (id=1083) - the same control the From-DB pipeline uses, so NO catalog
        /// "Load..." is needed (the version was already opened as an MDI child).
        /// Both rows are the same model ("Model_1"), so we match by the version
        /// token in the row Name (e.g. "Model_1 v1"), preferring a row WITHOUT a
        /// " (N)" duplicate suffix. All rows are logged for diagnosis. Returns
        /// false (no firstRow fallback) if no version-matched row is found.
        /// </summary>
        private static bool SelectInMemoryModelOnRightPage(IntPtr ccWizard, int version, Action<string> log)
        {
            // PURE WIN32 (no UIA): the "Open Models in Memory" list (id=1083) is a
            // SysListView32 in the wizard. The old UIA path (FromHandle + FindAll
            // DataItem + SelectionItemPattern) created oleacc IAccessible RCWs that,
            // abandoned to GC, crashed erwin's finalizer at teardown (same AccWrap AV
            // as the version picker). Mirror DriveCCDbAndApply Step 3.
            IntPtr openList = FindListViewById(ccWizard, 1083, log: null);
            if (openList == IntPtr.Zero)
            {
                IntPtr probe = FindDescendantById(ccWizard, 1083);
                var pcls = new StringBuilder(64);
                if (probe != IntPtr.Zero) GetClassName(probe, pcls, pcls.Capacity);
                log?.Invoke($"  [INMEM] id=1083 SysListView32 not found via Win32 (probe hwnd=0x{probe.ToInt64():X} class='{pcls}')");
                return false;
            }
            int matchRow = -1, dupRow = -1;
            for (int attempt = 0; attempt < 10 && matchRow < 0; attempt++)
            {
                int rowTotal = SendMessage(openList, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                for (int i = 0; i < rowTotal; i++)
                {
                    string rn = "";
                    for (int s = 0; s <= 3; s++)
                    {
                        string cell = GetListViewItemText(openList, i, s)?.Trim();
                        if (!string.IsNullOrEmpty(cell)) { rn = cell; break; }
                    }
                    if (attempt == 0) log?.Invoke($"  [INMEM] row[{i}] = '{rn}'");
                    var vm = System.Text.RegularExpressions.Regex.Match(rn, @"v(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!vm.Success || !int.TryParse(vm.Groups[1].Value, out int rv) || rv != version) continue;
                    if (rn.IndexOf('(') < 0) { matchRow = i; break; }   // prefer the non-duplicate row
                    if (dupRow < 0) dupRow = i;
                }
                if (matchRow < 0 && dupRow >= 0) matchRow = dupRow;
                if (matchRow < 0) Thread.Sleep(250);
            }
            if (matchRow < 0)
            {
                log?.Invoke($"  [INMEM] no in-memory row for v{version} - aborting (NO UIA)");
                return false;
            }
            var lvi = new LVITEM { stateMask = LVIS_SELECTED | LVIS_FOCUSED, state = LVIS_SELECTED | LVIS_FOCUSED };
            SendMessageLVItem(openList, LVM_SETITEMSTATE, new IntPtr(matchRow), ref lvi);
            SendSynthesizedNmClick(ccWizard, openList, 1083, matchRow, 0, log);
            log?.Invoke($"  [INMEM] selected in-memory v{version} (row {matchRow}) via Win32 (LVM_SETITEMSTATE + NM_CLICK). NO UIA.");
            Thread.Sleep(150);
            return true;
        }

        /// <summary>
        /// Diagnostic helper for the From-DB-via-Existing-Model spike: opens
        /// CC wizard programmatically, navigates to Right Model page, dumps
        /// the UIA tree (so we can identify the "From Existing Model" radio
        /// + dropdown by Name/AutomationId), then closes CC. RE'd PU stays
        /// loaded in the session - the production pipeline will reuse it.
        /// </summary>
        public static void DumpRightModelPageUia(Action<string> log)
        {
            log?.Invoke("");
            log?.Invoke("=== CC wizard Right Model page UIA dump (From-DB discovery) ===");
            IntPtr erwinMain = FindErwinMain();
            if (erwinMain == IntPtr.Zero) { log?.Invoke("  erwin main window not found"); return; }

            var dialogsBefore = EnumerateVisibleDialogs();
            log?.Invoke("  posting CMD_COMPLETE_COMPARE (1082) to open CC wizard");
            PostMessage(erwinMain, WM_COMMAND, MakeWParam(CMD_COMPLETE_COMPARE, 0), IntPtr.Zero);
            IntPtr ccWizard = WaitForNewDialog(dialogsBefore, "CC wizard", 5000, log);
            if (ccWizard == IntPtr.Zero) { log?.Invoke("  CC wizard did not open"); return; }
            HideWindow(ccWizard);
            log?.Invoke($"  CC wizard hwnd=0x{ccWizard.ToInt64():X} title='{GetTitle(ccWizard)}'");

            try
            {
                // Navigate Back to Overview (multiple times if needed) then
                // Next x2 to land on Right Model Selection.
                for (int i = 0; i < 12; i++)
                {
                    string t = GetTitle(ccWizard);
                    if (t.StartsWith("Wizard Overview", StringComparison.OrdinalIgnoreCase)
                        || t.StartsWith("Overview", StringComparison.OrdinalIgnoreCase)) break;
                    PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_BACK, 0), IntPtr.Zero);
                    Thread.Sleep(80);
                }
                log?.Invoke($"  at Overview, title='{GetTitle(ccWizard)}'");
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero); Thread.Sleep(150);
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero); Thread.Sleep(300);
                log?.Invoke($"  at Right Model page, title='{GetTitle(ccWizard)}'");

                // Dump full UIA tree of the wizard window so we see all
                // radios + comboboxes + buttons + their Name/AutomationId.
                var root = AutomationElement.FromHandle(ccWizard);
                if (root != null)
                {
                    log?.Invoke("  --- UIA tree (depth budget = 200) ---");
                    DumpUiaTree(root, 0, 200, log);
                    log?.Invoke("  --- end UIA tree ---");
                }

                // Also enumerate all child windows via Win32 to capture
                // dialog control IDs (GetDlgCtrlID) for each radio - these
                // are the WM_COMMAND IDs we'd PostMessage to switch route.
                log?.Invoke("  --- Win32 child windows + ctrl IDs ---");
                EnumChildWindows(ccWizard, (h, _) =>
                {
                    var clsBuf = new StringBuilder(64);
                    GetClassName(h, clsBuf, clsBuf.Capacity);
                    int id = GetDlgCtrlID(h);
                    if (id == 0) return true;
                    // Timeout-bounded read (hang class 2026-06-03): a hung child
                    // would otherwise block this UI-thread enumeration on WM_GETTEXT.
                    string txt = Win32Helper.GetWindowTextNoHang(h);
                    if (string.IsNullOrEmpty(txt) && clsBuf.ToString() != "Button"
                        && clsBuf.ToString() != "ComboBox" && clsBuf.ToString() != "ListBox") return true;
                    log?.Invoke($"    hwnd=0x{h.ToInt64():X} cls='{clsBuf}' id={id} text='{txt}'");
                    return true;
                }, IntPtr.Zero);
                log?.Invoke("  --- end Win32 children ---");
            }
            catch (Exception ex) { log?.Invoke($"  dump err: {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                log?.Invoke("  closing CC wizard via CC_CLOSE");
                PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_CLOSE, 0), IntPtr.Zero);
                Thread.Sleep(500);
                // Try to dismiss any "Close Model" / save prompts that may
                // appear - we don't want to leave stale dialogs.
                IntPtr closeDlg = WaitForDialog("Close Model", 1500);
                if (closeDlg != IntPtr.Zero)
                {
                    var croot = AutomationElement.FromHandle(closeDlg);
                    if (croot != null) ClickButtonByName(croot, new[] { "OK", "Cancel" });
                }
            }
            log?.Invoke("=== Right Model page UIA dump complete ===");
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
            // Timeout-bounded chokepoint: GetTitle backs ~all MDI / CC-wizard title
            // reads on the UI-thread pipeline. A raw GetWindowText to a hung window
            // would freeze erwin (hang dump 2026-06-03); GetWindowTextNoHang returns
            // "" on a non-pumping target instead of blocking the UI thread.
            return Win32Helper.GetWindowTextNoHang(hWnd);
        }

        /// <summary>
        /// SPIKE diagnostic (2026-05-29): log whether an MDI child's title
        /// carries the dirty marker ("*"). Called at the Review pipeline's key
        /// transitions so the debug log pinpoints EXACTLY which step dirties
        /// the loaded version (open / in-memory select / compare / RD). The
        /// investigation (medium confidence) points at erwin's CC engine
        /// marking the right model dirty during diff computation; these
        /// checkpoints turn that into hard evidence. Cheap and harmless, so
        /// left unconditional (not DebugMode-gated).
        /// </summary>
        private static void LogChildDirty(string when, IntPtr child, Action<string> log)
        {
            if (child == IntPtr.Zero || !IsWindow(child))
            {
                log?.Invoke($"  [REVIEW-DIRTY] {when}: child gone/invalid");
                return;
            }
            string t = GetTitle(child);
            log?.Invoke($"  [REVIEW-DIRTY] {when}: dirty={(t.Contains("*") ? "YES" : "no")} title='{t}'");
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

        /// <summary>
        /// Continuously polls for "erwin Data Modeler" GDM-1001 / unexpected
        /// condition popups and dismisses them with OK so erwin's UI thread
        /// stays unblocked. Critical for the From-DB silent-RE pipeline:
        /// during ShowERwinCCWiz, erwin's CC engine validates referenced
        /// objects against a "No Delete list"; when the silent-RE'd PU has
        /// FK references to tables not in the RE filter, erwin pops a
        /// blocking GDM-1001 popup that prevents the CC wizard window from
        /// being created. Without auto-dismiss the wizard never appears
        /// (we observed 15s timeouts with 0 CREATE events in the bridge
        /// monitor log). The dismisser polls every 100ms and clicks OK on
        /// any matching popup until the cancellation token signals.
        /// </summary>
        public static Task DismissGdmPopupsContinuous(System.Threading.CancellationToken token, Action<string> log)
        {
            return Task.Run(() =>
            {
                int dismissedCount = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        IntPtr popup = WaitForDialog("erwin Data Modeler", 100);
                        if (popup != IntPtr.Zero)
                        {
                            string title = GetTitle(popup);
                            try
                            {
                                var root = AutomationElement.FromHandle(popup);
                                if (root != null)
                                {
                                    bool clicked = ClickButtonByName(root, new[] { "OK", "Tamam" });
                                    if (clicked)
                                    {
                                        dismissedCount++;
                                        log?.Invoke($"  [GDM-DISMISS] popup #{dismissedCount} '{title}' dismissed with OK");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                log?.Invoke($"  [GDM-DISMISS] err: {ex.Message}");
                            }
                            // Brief pause after dismissal so we don't see
                            // the same dialog twice while it's tearing down.
                            Thread.Sleep(150);
                        }
                    }
                    catch { }
                    try { Thread.Sleep(100); } catch { }
                }
                if (dismissedCount > 0)
                    log?.Invoke($"  [GDM-DISMISS] continuous dismisser stopped, total {dismissedCount} popup(s) dismissed");
            });
        }

        private static IntPtr WaitForDialog(string titleContains, int timeoutMs)
        {
            uint deadline = unchecked((uint)Environment.TickCount) + (uint)timeoutMs;
            while (unchecked((int)((uint)Environment.TickCount - deadline)) < 0)
            {
                Thread.Sleep(100);
                IntPtr found = FindVisibleOwnTopLevel(titleContains);
                if (found != IntPtr.Zero) return found;
            }
            return IntPtr.Zero;
        }

        /// <summary>Single scan for a visible top-level window of OUR process whose title contains <paramref name="titleContains"/>.</summary>
        private static IntPtr FindVisibleOwnTopLevel(string titleContains)
        {
            IntPtr result = IntPtr.Zero;
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
            return result;
        }

        /// <summary>
        /// Waits for the Resolve Differences window while watching for erwin's
        /// "Type Resolution" wizard. That wizard interrupts the CC cascade right
        /// after Compare when the left/right models carry UDP types with the same
        /// name but different datatypes (typical for cross-version compares after
        /// a UDP definition changed). If nobody presses its Finish, Resolve
        /// Differences never opens: the pipeline times out, no DDL is produced,
        /// and the half-open wizard cascade can crash erwin during teardown
        /// (user-reported 2026-06-11, log 20:15 "Resolve Differences did not
        /// open" followed by an erwin restart). Pressing Finish accepts the
        /// wizard's default conflict resolutions - the same thing a user does
        /// manually - and lets the compare proceed.
        /// Finish is clicked via ClickDialogButtonByTextWin32 (PURE Win32, no
        /// UIA): in-proc UIA on erwin's own wizards leaves oleacc IAccessible
        /// RCWs that crash erwin minutes later at teardown - proven 2026-06-11
        /// (OLEACC.dll APPCRASH at 22:32 after a UIA-clicked Finish at 22:31,
        /// pipeline itself succeeded). Same lesson the [SM-WIN32]/[MO-WIN32]
        /// teardown paths already encode with their "NO UIA" rule.
        /// The timeout is re-armed once after the wizard is dismissed so the
        /// compare gets its full window again.
        /// </summary>
        private static IntPtr WaitForResolveDifferencesHandlingTypeResolution(int timeoutMs, Action<string> log)
        {
            const string TypeResolutionTitle = "Type Resolution";
            bool deadlineReArmed = false;
            uint lastFinishClickTick = 0;
            uint deadline = unchecked((uint)Environment.TickCount) + (uint)timeoutMs;

            while (unchecked((int)((uint)Environment.TickCount - deadline)) < 0)
            {
                Thread.Sleep(100);

                IntPtr rd = FindVisibleOwnTopLevel("Resolve Differences");
                if (rd != IntPtr.Zero) return rd;

                IntPtr tr = FindVisibleOwnTopLevel(TypeResolutionTitle);
                if (tr == IntPtr.Zero) continue;

                // Throttle re-clicks: the wizard needs a moment to process Finish
                // and close; hammering it every 100 ms just spams the log.
                uint now = unchecked((uint)Environment.TickCount);
                if (lastFinishClickTick != 0 && unchecked((int)(now - lastFinishClickTick)) < 1500)
                    continue;
                lastFinishClickTick = now;

                bool clicked = false;
                try
                {
                    clicked = ClickDialogButtonByTextWin32(tr, new[] { "Finish", "Son" }, log);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  [TYPE-RES] Finish click error on 0x{tr.ToInt64():X}: {ex.Message}");
                }
                log?.Invoke(clicked
                    ? $"  [TYPE-RES] Type Resolution wizard 0x{tr.ToInt64():X} intercepted - Finish pressed (default UDP type resolutions accepted)."
                    : $"  [TYPE-RES] Type Resolution wizard 0x{tr.ToInt64():X} present but Finish button not found - will retry.");

                if (clicked && !deadlineReArmed)
                {
                    // The wizard ate part of the wait; give the compare its full
                    // timeout again from this point (once only).
                    deadlineReArmed = true;
                    deadline = unchecked((uint)Environment.TickCount) + (uint)timeoutMs;
                }
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
        /// Finds the first descendant window (any class, recursively) under
        /// <paramref name="parent"/> whose control id == <paramref name="wantId"/>.
        /// Pure Win32 (EnumChildWindows + GetDlgCtrlID) replacement for a UIA
        /// FindFirst(AutomationIdProperty) - used to avoid the UIA AutomationElement
        /// -> oleacc/IAccessible RCWs that crash erwin's finalizer at teardown.
        /// </summary>
        private static IntPtr FindDescendantById(IntPtr parent, int wantId)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, (h, _) =>
            {
                if (GetDlgCtrlID(h) == wantId) { found = h; return false; }
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

            // 150ms of vulnerable sleeps below (30+40+80) - confine cursor to
            // a 5x5 box around the listview subitem so user mouse movement
            // cannot redirect the click (user-reported failure 2026-05-30).
            bool clipped = ClipCursorAround(pt.X, pt.Y);
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
                if (clipped) ReleaseCursorClip();
                SetCursorPos(saved.X, saved.Y);
            }
            log?.Invoke("    mouse_event LEFTDOWN/UP fired, cursor restored");
            return true;
        }
    }
}
