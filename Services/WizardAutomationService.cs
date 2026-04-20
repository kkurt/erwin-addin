using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace EliteSoft.Erwin.AddIn.Services
{
    public enum RightModelSource { Mart, Database, OpenModel }

    public class AlterWizardConfig
    {
        /// <summary>Source for the Right side of Complete Compare.</summary>
        public RightModelSource Right;
        /// <summary>For RightModelSource.OpenModel: display name of the loaded PU to pick.</summary>
        public string OpenModelName;
        /// <summary>For RightModelSource.Mart: the Mart version number to compare against
        /// (parsed from the DDL Generation tab's cmbRightModel, e.g. "v2 (name)" -> 2).
        /// When > 0 we UIA-drive the Right Model Selection dialog to click the
        /// Mart radio and pick the matching version before pressing Load.</summary>
        public int MartVersion;
        // Database source relies on the last-session state erwin remembers.
    }

    /// <summary>
    /// Zero-click automation of erwin's Complete Compare + Forward Engineer Alter Script
    /// Schema Generation Wizard. Works by:
    ///   1. Posting WM_COMMAND 1082 to erwin's XTPMainFrame to open CC.
    ///   2. Using UIA to drive the Right/Left Model Selection dialogs and CC wizard.
    ///   3. Posting WM_COMMAND 61631 from Resolve Differences to open the Alter Script Wizard.
    ///   4. Posting WM_COMMAND 1766 (Next) through wizard pages until Preview.
    ///   5. Reading the CodejockSyntaxEditor's Name property to get the DDL text.
    ///   6. Posting WM_COMMAND 2 (Cancel) to clean up all dialogs.
    ///
    /// Command IDs discovered from erwin.exe's MFC string table:
    ///   1082  = Invoke Complete Compare
    ///   1161  = Alter Script (Forward Engineer flyout)
    ///   1766  = Next > button (dialog template)
    ///   2     = IDCANCEL
    ///   61631 = Forward Engineer Alter Script (toolbar)
    /// </summary>
    public class WizardAutomationService
    {
        public const uint WM_COMMAND = 0x0111;
        public const int IDOK = 1;
        public const int IDCANCEL = 2;

        public const int CMD_COMPLETE_COMPARE = 1082;
        public const int CMD_NEXT = 1766;
        public const int CMD_BACK = 1767;
        public const int CMD_GENERATE = 1760;
        public const int CMD_ALTER_SCRIPT_FE = 61631;
        public const int CMD_ALTER_SCRIPT_MENU = 1161;
        // Resolve Differences toolbar buttons
        // cmd=1056 is the ACTUAL "Alter Script" button on Resolve Differences' toolbar
        // (discovered via TB_GETBUTTON enumeration — works after transfer).
        // 32785/32786 are "Left/Right Alter Script/Schema Generation" items from
        // EM_CMP.dll string table — they did NOT open the wizard in practice.
        public const int CMD_RD_ALTER_SCRIPT = 1056;      // Resolve Differences: Alter Script
        public const int CMD_LEFT_ALTER_SCRIPT = 32785;   // (string-table id; not the actual button)
        public const int CMD_RIGHT_ALTER_SCRIPT = 32786;  // (string-table id; not the actual button)
        // Complete Compare wizard button IDs (from UIA AutomationId dump)
        public const int CC_BACK = 12323;
        public const int CC_NEXT = 12324;
        public const int CC_COMPARE = 12325;
        public const int CC_CLOSE = 2;

        private readonly Action<string> _log;

        public WizardAutomationService(Action<string> log) { _log = log ?? (_ => { }); }

        #region Win32 P/Invoke

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageIntPtr(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int LoadString(IntPtr hInstance, uint uID, StringBuilder lpBuffer, int cchBufferMax);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);
        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr hModule);
        private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // SetWinEventHook - works cross-thread/cross-process without DLL injection.
        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint idEventThread, uint dwmsEventTime);
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint EVENT_OBJECT_INVOKED = 0x8013;
        private const uint EVENT_SYSTEM_CAPTURESTART = 0x0008;
        private const uint EVENT_OBJECT_STATECHANGE = 0x800A;

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOSIZE    = 0x0001;
        private const uint SWP_NOZORDER  = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        // Toggle: when true, wizard + dialog windows are shoved off-screen so
        // the user never sees them flicker. DDL capture still works because
        // it's done via native detour on FEProcessor::GenerateAlterScript.
        // Toggle OFF temporarily for debugging if the automation goes wrong.
        public static bool HideWizards = true;

        /// <summary>Move the given window off-screen so the user doesn't see it.
        /// No-op if HideWizards is false or the handle is invalid.</summary>
        private void HideOffscreen(IntPtr hwnd, string label)
        {
            if (!HideWizards || hwnd == IntPtr.Zero) return;
            try
            {
                SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                _log($"  hid {label} off-screen");
            }
            catch (Exception ex) { _log($"  hide {label} err: {ex.Message}"); }
        }

        // Low-level mouse hook - captures clicks system-wide.
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const uint TB_HITTEST = 0x0445;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate newProc);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtrRestore(IntPtr hWnd, int nIndex, IntPtr newProc);
        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        private const int GWLP_WNDPROC = -4;

        /// <summary>
        /// Subclasses the given dialog's WndProc to capture WM_COMMAND messages.
        /// XTP toolbars are custom-painted on the dialog client area (no child HWND),
        /// so clicks route WM_COMMAND directly to the dialog. Subclassing lets us
        /// intercept these cross-thread safely within the same process.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct LVHITTESTINFO
        {
            public POINT pt;
            public uint flags;
            public int iItem;
            public int iSubItem;
            public int iGroup;
        }
        private const uint LVM_FIRST = 0x1000;
        private const uint LVM_SUBITEMHITTEST = LVM_FIRST + 57; // 0x1039
        private const uint LVM_GETSUBITEMRECT = LVM_FIRST + 56; // 0x1038
        private const uint WM_LBUTTONDOWN_MSG = 0x0201;
        private const uint WM_LBUTTONUP_MSG = 0x0202;

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>Captures the first mouse click inside the dialog's listview. Returns null on timeout.</summary>
        public (int item, int subItem, int dx, int dy)? CaptureListViewClick(IntPtr targetDialog, int timeoutMs)
        {
            (int, int, int, int)? result = null;
            IntPtr listView = FindDescendantByClass(targetDialog, "SysListView32");
            if (listView == IntPtr.Zero) { _log("    no SysListView32 found"); return null; }
            _log($"    listview HWND = 0x{listView.ToInt64():X}");

            var done = new ManualResetEventSlim(false);
            var listenThread = new Thread(() =>
            {
                LowLevelMouseProc proc = null;
                IntPtr hook = IntPtr.Zero;
                proc = (nCode, wParam, lParam) =>
                {
                    if (nCode >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN_MSG && !result.HasValue)
                    {
                        var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        IntPtr hit = WindowFromPoint(ms.pt);
                        if (hit == listView || IsDescendant(listView, hit))
                        {
                            POINT cpt = ms.pt;
                            ScreenToClient(listView, ref cpt);
                            IntPtr htInfoBuf = Marshal.AllocHGlobal(Marshal.SizeOf<LVHITTESTINFO>());
                            try
                            {
                                var ht = new LVHITTESTINFO { pt = cpt };
                                Marshal.StructureToPtr(ht, htInfoBuf, false);
                                IntPtr r = SendMessageIntPtr(listView, LVM_SUBITEMHITTEST, IntPtr.Zero, htInfoBuf);
                                int idx = r.ToInt32();
                                if (idx >= 0)
                                {
                                    ht = Marshal.PtrToStructure<LVHITTESTINFO>(htInfoBuf);
                                    // Get subitem rect to compute offset WITHIN the subitem
                                    IntPtr rectBuf = Marshal.AllocHGlobal(16);
                                    try
                                    {
                                        Marshal.WriteInt32(rectBuf, 0, 0);        // RECT.left = subitem index (in-param)
                                        Marshal.WriteInt32(rectBuf, 4, ht.iSubItem); // RECT.top = iSubItem
                                        Marshal.WriteInt32(rectBuf, 8, 0);
                                        Marshal.WriteInt32(rectBuf, 12, 0);
                                        // LVM_GETSUBITEMRECT: wParam=iItem, lParam=RECT* with in-params
                                        Marshal.WriteInt32(rectBuf, 0, 0); // code: LVIR_BOUNDS=0
                                        Marshal.WriteInt32(rectBuf, 4, ht.iSubItem);
                                        SendMessageIntPtr(listView, LVM_GETSUBITEMRECT, new IntPtr(ht.iItem), rectBuf);
                                        int subL = Marshal.ReadInt32(rectBuf, 0);
                                        int subT = Marshal.ReadInt32(rectBuf, 4);
                                        int dx = cpt.X - subL;
                                        int dy = cpt.Y - subT;
                                        result = (ht.iItem, ht.iSubItem, dx, dy);
                                        done.Set();
                                    }
                                    finally { Marshal.FreeHGlobal(rectBuf); }
                                }
                            }
                            finally { Marshal.FreeHGlobal(htInfoBuf); }
                        }
                    }
                    return CallNextHookEx(hook, nCode, wParam, lParam);
                };
                hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle("user32.dll"), 0);
                if (hook == IntPtr.Zero) { done.Set(); return; }
                int waited = 0;
                while (!result.HasValue && waited < timeoutMs)
                {
                    System.Windows.Forms.Application.DoEvents();
                    Thread.Sleep(50); waited += 50;
                }
                UnhookWindowsHookEx(hook);
                GC.KeepAlive(proc);
                done.Set();
            });
            listenThread.IsBackground = true;
            listenThread.SetApartmentState(ApartmentState.STA);
            listenThread.Start();
            done.Wait(timeoutMs + 2000);
            return result;
        }

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        /// <summary>
        /// Resolves the (item, subItem, dx, dy) saved descriptor to a real screen
        /// coordinate at runtime, then simulates a physical mouse click. XTP's
        /// custom hit regions require actual mouse_event (not synthetic WM messages).
        /// Resolution/DPI independent because subitem rect is recomputed each run.
        /// </summary>
        public bool ReplayListViewClick(IntPtr targetDialog, int item, int subItem, int dx, int dy)
        {
            IntPtr lv = FindDescendantByClass(targetDialog, "SysListView32");
            if (lv == IntPtr.Zero) { _log("    [ERR] no listview"); return false; }

            IntPtr rectBuf = Marshal.AllocHGlobal(16);
            try
            {
                // LVM_GETSUBITEMRECT: lParam RECT's left=LVIR_BOUNDS(0), top=iSubItem
                Marshal.WriteInt32(rectBuf, 0, 0);
                Marshal.WriteInt32(rectBuf, 4, subItem);
                Marshal.WriteInt32(rectBuf, 8, 0);
                Marshal.WriteInt32(rectBuf, 12, 0);
                SendMessageIntPtr(lv, LVM_GETSUBITEMRECT, new IntPtr(item), rectBuf);
                int left = Marshal.ReadInt32(rectBuf, 0);
                int top = Marshal.ReadInt32(rectBuf, 4);
                POINT clientPt = new POINT { X = left + dx, Y = top + dy };
                POINT screenPt = clientPt;
                if (!ClientToScreen(lv, ref screenPt)) { _log("    [ERR] ClientToScreen"); return false; }
                _log($"    click target screen=({screenPt.X},{screenPt.Y}) client=({clientPt.X},{clientPt.Y})");

                // Save cursor position, move, click, restore
                GetCursorPos(out POINT savedCursor);
                SetCursorPos(screenPt.X, screenPt.Y);
                Thread.Sleep(20);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(30);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(60);
                SetCursorPos(savedCursor.X, savedCursor.Y);
                return true;
            }
            finally { Marshal.FreeHGlobal(rectBuf); }
        }

        private static IntPtr FindDescendantByClass(IntPtr parent, string clsName)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, (hwnd, lp) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(hwnd, cls, 64);
                if (cls.ToString() == clsName) { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static (int, int, int, int)? LoadLvClick(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return null;
                var parts = System.IO.File.ReadAllText(path).Trim().Split(',');
                if (parts.Length != 4) return null;
                return (int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
            }
            catch { return null; }
        }

        private static void SaveLvClick(string path, int item, int subItem, int dx, int dy)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, $"{item},{subItem},{dx},{dy}");
            }
            catch { }
        }

        public int ListenForCommand(IntPtr targetDialog, int timeoutMs)
        {
            int captured = 0;
            var diagLog = new List<string>();
            // Subclass BOTH the dialog and erwin main frame - XTP custom-painted
            // toolbars may route WM_COMMAND to either target.
            IntPtr mainFrame = FindErwinMain();
            IntPtr oldProcDlg = IntPtr.Zero;
            IntPtr oldProcMain = IntPtr.Zero;

            WndProcDelegate procDlg = null;
            procDlg = (hWnd, uMsg, wParam, lParam) =>
            {
                if (captured == 0 && IsInteresting(uMsg))
                {
                    if (uMsg == WM_COMMAND)
                    {
                        int cmdId = wParam.ToInt32() & 0xFFFF;
                        int notify = (wParam.ToInt32() >> 16) & 0xFFFF;
                        diagLog.Add($"  DLG WM_COMMAND cmd={cmdId} (0x{cmdId:X}) notify={notify}");
                        if (cmdId >= 32768) captured = cmdId;
                    }
                    else if (uMsg == 0x004E /*WM_NOTIFY*/ && lParam != IntPtr.Zero)
                    {
                        // NMHDR: hwndFrom(IntPtr) + idFrom(UINT_PTR) + code(UINT)
                        // On x64: hwndFrom=8, idFrom=8, code=4 (packed)
                        try
                        {
                            IntPtr hwndFrom = Marshal.ReadIntPtr(lParam, 0);
                            long idFrom = Marshal.ReadInt64(lParam, 8);
                            int code = Marshal.ReadInt32(lParam, 16);
                            int wp = wParam.ToInt32();
                            diagLog.Add($"  DLG WM_NOTIFY from=0x{hwndFrom.ToInt64():X} id={idFrom} code={code} (0x{code:X8})  wp={wp}");
                            // If code is TBN_DROPDOWN or NM_CLICK on a toolbar, lParam has extra data
                            // TBN_DROPDOWN = -710 = 0xFFFFFD3A
                            // NM_CLICK = -2 = 0xFFFFFFFE
                            // For Codejock XTP toolbars, the NMTOOLBAR struct (after NMHDR) has
                            // iItem(int) which is the button's idCommand
                            if ((code == -2 || code == -710 || code == -3 /*NM_RCLICK*/) &&
                                (idFrom == 102 || idFrom == 721 || idFrom == 1014 || idFrom == 1016 || idFrom == 1093))
                            {
                                // NMTOOLBAR.iItem at offset sizeof(NMHDR)=24 on x64
                                int iItem = Marshal.ReadInt32(lParam, 24);
                                diagLog.Add($"    NMTOOLBAR.iItem = {iItem} (0x{iItem:X})");
                                if (iItem >= 32768) captured = iItem;
                            }
                        }
                        catch (Exception ex) { diagLog.Add($"  NMHDR parse err: {ex.Message}"); }
                    }
                }
                return CallWindowProc(oldProcDlg, hWnd, uMsg, wParam, lParam);
            };
            WndProcDelegate procMain = null;
            procMain = (hWnd, uMsg, wParam, lParam) =>
            {
                if (captured == 0 && uMsg == WM_COMMAND)
                {
                    int cmdId = wParam.ToInt32() & 0xFFFF;
                    int notify = (wParam.ToInt32() >> 16) & 0xFFFF;
                    diagLog.Add($"  MAIN WM_COMMAND cmd={cmdId} (0x{cmdId:X}) notify={notify}");
                    if (cmdId >= 32768) captured = cmdId;
                }
                return CallWindowProc(oldProcMain, hWnd, uMsg, wParam, lParam);
            };

            oldProcDlg = SetWindowLongPtr(targetDialog, GWLP_WNDPROC, procDlg);
            if (oldProcDlg == IntPtr.Zero) { _log("    SetWindowLongPtr dialog FAILED"); return 0; }
            diagLog.Add($"Subclassed dialog (old=0x{oldProcDlg.ToInt64():X}) + main (hwnd=0x{mainFrame.ToInt64():X})");
            if (mainFrame != IntPtr.Zero)
                oldProcMain = SetWindowLongPtr(mainFrame, GWLP_WNDPROC, procMain);

            try
            {
                int waited = 0;
                while (captured == 0 && waited < timeoutMs)
                {
                    Thread.Sleep(100);
                    waited += 100;
                }
            }
            finally
            {
                SetWindowLongPtrRestore(targetDialog, GWLP_WNDPROC, oldProcDlg);
                if (oldProcMain != IntPtr.Zero)
                    SetWindowLongPtrRestore(mainFrame, GWLP_WNDPROC, oldProcMain);
                GC.KeepAlive(procDlg); GC.KeepAlive(procMain);
            }

            foreach (var line in diagLog) _log("    " + line);
            return captured;
        }

        private static bool IsInteresting(uint msg)
            => msg == WM_COMMAND || msg == 0x004E /*WM_NOTIFY*/ || msg == 0x0111 || msg == 0x0202 /*LBUTTONUP*/;

        private static void LogMsg(string tag, uint uMsg, IntPtr wParam, List<string> log)
        {
            int cmdId = wParam.ToInt32() & 0xFFFF;
            int notify = (wParam.ToInt32() >> 16) & 0xFFFF;
            string name = uMsg == WM_COMMAND ? "WM_COMMAND" : (uMsg == 0x004E ? "WM_NOTIFY" : uMsg == 0x0202 ? "LBUTTONUP" : "MSG_" + uMsg);
            log.Add($"  {tag} {name} cmd={cmdId} (0x{cmdId:X}) notify={notify}");
        }

        /// <summary>Gets the list of idCommand values for a single ToolbarWindow32 HWND, in button-index order.</summary>
        public List<int> EnumerateSingleToolbarCommands(IntPtr toolbarHwnd)
        {
            var cmds = new List<int>();
            IntPtr cnt = SendMessageIntPtr(toolbarHwnd, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
            int count = cnt.ToInt32();
            for (int i = 0; i < count; i++)
            {
                IntPtr buf = Marshal.AllocHGlobal(32);
                try
                {
                    IntPtr ok = SendMessageIntPtr(toolbarHwnd, TB_GETBUTTON, new IntPtr(i), buf);
                    if (ok != IntPtr.Zero) cmds.Add(Marshal.ReadInt32(buf, 4));
                    else cmds.Add(0);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return cmds;
        }

        private static bool IsDescendant(IntPtr ancestor, IntPtr descendant)
        {
            IntPtr cur = descendant;
            int hops = 0;
            while (cur != IntPtr.Zero && hops++ < 20)
            {
                IntPtr parent = GetParent(cur);
                if (parent == ancestor) return true;
                if (parent == IntPtr.Zero) return false;
                cur = parent;
            }
            return false;
        }

        /// <summary>Looks up an MFC command ID in erwin.exe's resource string table (tooltip/description).</summary>
        public static string LookupCommandString(int cmdId)
        {
            IntPtr hMod = IntPtr.Zero;
            foreach (var p in new[]
            {
                @"C:\Program Files\erwin\Data Modeler r10\erwin.exe",
                @"C:\Program Files (x86)\erwin\Data Modeler r10\erwin.exe"
            })
            {
                if (System.IO.File.Exists(p)) { hMod = LoadLibraryEx(p, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE); if (hMod != IntPtr.Zero) break; }
            }
            if (hMod == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(512);
                int n = LoadString(hMod, (uint)cmdId, sb, 512);
                return n > 0 ? sb.ToString() : null;
            }
            finally { FreeLibrary(hMod); }
        }

        // Toolbar messages
        private const uint TB_BUTTONCOUNT = 0x0418;
        private const uint TB_GETBUTTON = 0x0417;

        /// <summary>
        /// Enumerates all ToolbarWindow32 child controls of the given parent dialog
        /// and returns the list of distinct idCommand values of their buttons.
        /// The commands are what you send via WM_COMMAND to fire each button.
        /// </summary>
        public List<int> EnumerateToolbarCommands(IntPtr parent)
        {
            var commands = new List<int>();
            var toolbars = new List<IntPtr>();
            EnumChildWindows(parent, (hwnd, lp) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(hwnd, cls, 64);
                if (cls.ToString() == "ToolbarWindow32") toolbars.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            foreach (var tb in toolbars)
            {
                IntPtr cnt = SendMessageIntPtr(tb, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
                int count = cnt.ToInt32();
                for (int i = 0; i < count; i++)
                {
                    IntPtr buf = Marshal.AllocHGlobal(32); // sizeof(TBBUTTON) on x64 = 32
                    try
                    {
                        IntPtr ok = SendMessageIntPtr(tb, TB_GETBUTTON, new IntPtr(i), buf);
                        if (ok != IntPtr.Zero)
                        {
                            // TBBUTTON: INT iBitmap (4), INT idCommand (4), BYTE fsState, fsStyle, bReserved[6], dwData(8), iString(8)
                            int idCommand = Marshal.ReadInt32(buf, 4);
                            if (idCommand != 0) commands.Add(idCommand);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            return commands;
        }

        private static IntPtr MakeWParam(int low, int high)
            => new IntPtr(((high & 0xFFFF) << 16) | (low & 0xFFFF));

        #endregion

        /// <summary>
        /// Full automation: produces the alter DDL text from erwin's own wizard,
        /// without any user interaction. All dialog windows are opened and closed
        /// programmatically through WM_COMMAND messages and UIA pattern calls.
        /// Returns the alter DDL script text, or throws on failure.
        /// </summary>
        public string CaptureAlterScript(int timeoutMs = 30000) => CaptureAlterScript(null, timeoutMs);

        public string CaptureAlterScript(AlterWizardConfig config, int timeoutMs = 30000)
        {
            IntPtr mainHwnd = FindErwinMain();
            if (mainHwnd == IntPtr.Zero) throw new InvalidOperationException("erwin XTPMainFrame window not found");
            _log($"erwin main HWND = 0x{mainHwnd.ToInt64():X}");

            // Clear any stale DDL from a prior capture - we want to read the
            // NEW one produced by this wizard run, not a leftover.
            EliteSoft.Erwin.AddIn.Services.NativeBridgeService.ClearCapturedDdl();

            // Snapshot open dialogs BEFORE opening CC so we can find the new one
            var dialogsBefore = EnumerateVisibleDialogs();

            // Step 1: Trigger Complete Compare via WM_COMMAND 1082
            _log("-> Posting WM_COMMAND 1082 (Invoke Complete Compare)");
            PostMessage(mainHwnd, WM_COMMAND, MakeWParam(CMD_COMPLETE_COMPARE, 0), IntPtr.Zero);

            // Step 2: Wait for the CC wizard dialog to appear. The wizard is ONE dialog
            // whose title changes per page (Overview, Left Model, Right Model, Type Selection,
            // Left Object Selection, Right Object Selection, Advanced Options).
            // Track the FIRST new dialog hwnd that appears.
            IntPtr ccWizard = WaitForNewDialog(dialogsBefore, 5000);
            if (ccWizard == IntPtr.Zero) throw new InvalidOperationException("CC wizard dialog did not appear after WM_COMMAND 1082");
            _log($"CC wizard HWND = 0x{ccWizard.ToInt64():X}  title='{GetWindowTitle(ccWizard)}'");
            HideOffscreen(ccWizard, "CC wizard");

            // Phase-4a: Navigate to Right Model page and select a model different from Left.
            // Default selection on Right Model page is the ACTIVE model (same as Left),
            // which causes the "compare to itself" warning. We pick the first list item
            // whose name does not match the currently-active model.
            ApplyRightModelSelection(ccWizard, config);

            // TODO(phase-4b): TYPE SELECTION page - Compare Level checkboxes, Option Set,
            // Object Filter. Currently using whatever was set last session.

            // Step 3: Click Compare button directly via WM_COMMAND (fast).
            // UIA FindFirst on this wizard takes ~10s; WM_COMMAND with the button's
            // known control ID is instant.
            _log($"  -> WM_COMMAND CC_COMPARE ({CC_COMPARE}) to wizard");
            PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_COMPARE, 0), IntPtr.Zero);

            // Step 3b: auto-dismiss "compare to itself?" popup if it appears.
            // This popup indicates Left and Right are the same model - we abort
            // the run so the user can fix the Right Model selection.
            Thread.Sleep(500);
            IntPtr selfPopup = WaitForDialog(new[] { "erwin Data Modeler" }, 1500);
            if (selfPopup != IntPtr.Zero)
            {
                string popupText = GetWindowTitle(selfPopup);
                _log($"  Popup detected: '{popupText}' - likely 'compare to itself' warning");
                // Click No to abort the empty compare, then throw clear error
                ClickButtonByName(selfPopup, new[] { "No" });
                Thread.Sleep(400);
                CancelDialog(ccWizard, "CC wizard");
                throw new InvalidOperationException(
                    "Left and Right models are the same (erwin popped 'compare to itself' warning). " +
                    "Phase-4a needed: select a different Right Model before compare.");
            }

            // Step 4: Wait for Resolve Differences
            IntPtr resolveDlg = WaitForDialog(new[] { "Resolve Differences" }, timeoutMs);
            if (resolveDlg == IntPtr.Zero) throw new InvalidOperationException("Resolve Differences did not appear");
            _log($"Resolve Differences HWND = 0x{resolveDlg.ToInt64():X}");
            // NOTE: do NOT hide Resolve Differences. It contains a hot-track XTP
            // "Copy to Left" arrow that only responds to a real mouse_event hit
            // on a visible on-screen window. Hiding RD would break the apply-
            // differences step. Everything else (CC wizard + Alter wizard) IS
            // hidden because they're navigated purely via WM_COMMAND.

            // Step 5: Enumerate the Resolve Differences toolbar buttons to find the
            // actual command ID for "Alter Script". Each ToolbarWindow32 child sends
            // its button commands via WM_COMMAND to its PARENT (the dialog). We query
            // TB_BUTTONCOUNT + TB_GETBUTTON to learn the real idCommands.
            var toolbarCommands = EnumerateToolbarCommands(resolveDlg);
            _log($"  Resolve Differences toolbar exposes {toolbarCommands.Count} command IDs:");
            for (int ci = 0; ci < toolbarCommands.Count; ci++)
            {
                int cid = toolbarCommands[ci];
                string desc = LookupCommandString(cid);
                if (string.IsNullOrEmpty(desc)) desc = "(no resource string)";
                _log($"    [{ci}] cmd={cid} (0x{cid:X}) -> {desc.Replace("\n", " | ")}");
            }

            // Step 5b: apply all differences (Copy Item(s) to Left) on Model row.
            // The arrow is a custom hot-tracked region inside a listview item.
            // Calibration: listen IMMEDIATELY when Resolve Differences opens (before
            // any log display delay can mislead the user). Capture via LVM_SUBITEMHITTEST
            // to get a LISTVIEW-RELATIVE descriptor (item + subitem + offset) which is
            // stable against dialog resize / DPI changes.
            string calibPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EliteSoft", "ErwinAddIn", "transfer_click_lv.txt");
            (int item, int subItem, int dx, int dy)? saved = LoadLvClick(calibPath);

            if (saved.HasValue)
            {
                var s = saved.Value;
                _log($"  Replaying LV click item={s.item} subItem={s.subItem} offset=({s.dx},{s.dy})");
                if (!ReplayListViewClick(resolveDlg, s.item, s.subItem, s.dx, s.dy))
                    throw new InvalidOperationException("Failed to replay listview click");
                Thread.Sleep(400);

                // After a full "Copy to Left" on the Model row, erwin may pop an info
                // dialog "There are no more differences in the tree." — auto-dismiss it.
                IntPtr noDiffPopup = WaitForDialog(new[] { "erwin Data Modeler" }, 800);
                if (noDiffPopup != IntPtr.Zero)
                {
                    _log($"  dismissing info popup HWND=0x{noDiffPopup.ToInt64():X}");
                    PostMessage(noDiffPopup, WM_COMMAND, MakeWParam(IDOK, 0), IntPtr.Zero);
                    Thread.Sleep(200);
                }
            }
            else
            {
                _log("  === FIRST-RUN CALIBRATION (auto-listen started) ===");
                _log("  Now click the Model row's LEFT-arrow in Resolve Differences (no need to alt-tab)");
                System.Media.SystemSounds.Asterisk.Play();
                var cap = CaptureListViewClick(resolveDlg, 30000);
                if (!cap.HasValue)
                    throw new InvalidOperationException("No listview click captured in 30s");
                var c = cap.Value;
                _log($"  CAPTURED listview click: item={c.item} subItem={c.subItem} offset=({c.dx},{c.dy})");
                SaveLvClick(calibPath, c.item, c.subItem, c.dx, c.dy);
                _log($"  Saved to {calibPath}");
                // User already performed the click, no need to replay
                Thread.Sleep(1500);
            }

            // Trigger Alter Script Wizard.
            // After a transfer, erwin recomputes and re-renders Resolve Differences.
            // Hitting WM_COMMAND mid-refresh is a no-op because the toolbar button
            // is momentarily disabled. Wait for the dialog to settle, then post
            // cmd=1056 (the actual toolbar idCommand, discovered via TB_GETBUTTON).
            Thread.Sleep(1200);
            IntPtr wizHwnd = IntPtr.Zero;
            _log($"-> Posting WM_COMMAND {CMD_RD_ALTER_SCRIPT} (RD Alter Script, actual toolbar id) to Resolve Differences");
            PostMessage(resolveDlg, WM_COMMAND, MakeWParam(CMD_RD_ALTER_SCRIPT, 0), IntPtr.Zero);
            wizHwnd = WaitForDialog(new[] { "Alter Script Schema Generation Wizard" }, 4000);

            if (wizHwnd == IntPtr.Zero)
            {
                _log("  primary 1056 did not open wizard; trying fallback matrix");
                var candidateCommands = new List<int> {
                    CMD_RD_ALTER_SCRIPT, CMD_LEFT_ALTER_SCRIPT, CMD_RIGHT_ALTER_SCRIPT,
                    CMD_ALTER_SCRIPT_FE, CMD_ALTER_SCRIPT_MENU
                };
                candidateCommands.AddRange(toolbarCommands);
                var seen = new HashSet<int>();
                candidateCommands.RemoveAll(c => !seen.Add(c));
                var candidateTargets = new (string name, IntPtr hwnd)[]
                {
                    ("Resolve Differences", resolveDlg),
                    ("erwin XTPMainFrame", mainHwnd)
                };
                foreach (var cmd in candidateCommands)
                {
                    foreach (var (tname, thwnd) in candidateTargets)
                    {
                        PostMessage(thwnd, WM_COMMAND, MakeWParam(cmd, 0), IntPtr.Zero);
                        wizHwnd = WaitForDialog(new[] { "Alter Script Schema Generation Wizard" }, 1500);
                        if (wizHwnd != IntPtr.Zero) { _log($"  fallback hit: cmd={cmd} -> {tname}"); break; }
                    }
                    if (wizHwnd != IntPtr.Zero) break;
                }
            }

            if (wizHwnd == IntPtr.Zero) throw new InvalidOperationException("Alter Script wizard did not open");
            _log($"Alter Script Wizard HWND = 0x{wizHwnd.ToInt64():X}");
            HideOffscreen(wizHwnd, "Alter Script wizard");

            // Step 7: Press Next until the Next button is disabled (we are on the last page = Preview)
            //          There are typically 5-6 pages in this wizard.
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(400);
                bool nextEnabled = IsButtonEnabledByName(wizHwnd, "Next >");
                if (!nextEnabled)
                {
                    _log("Next button is disabled - on Preview page");
                    break;
                }
                _log($"  Page {i + 1}: posting WM_COMMAND 1766 (Next >)");
                PostMessage(wizHwnd, WM_COMMAND, MakeWParam(CMD_NEXT, 0), IntPtr.Zero);
            }
            // Final settle
            Thread.Sleep(800);

            // Step 8: Read the DDL from the native bridge (our detour on
            // FEProcessor::GenerateAlterScript + GetScript captured it the
            // moment erwin produced the Preview text). UIA scraping removed -
            // see NativeBridgeService + scripts/native-bridge/native-bridge.cpp.
            string script = EliteSoft.Erwin.AddIn.Services.NativeBridgeService.ConsumeLastCapturedDdl();
            if (string.IsNullOrEmpty(script))
            {
                _log("  native-bridge DDL empty, falling back to UIA Preview scrape");
                script = ExtractPreviewText(wizHwnd);
            }
            _log($"DDL length = {script?.Length ?? 0} chars (source = " +
                 (!string.IsNullOrEmpty(script) ? "native bridge" : "none") + ")");

            // Step 9: Cleanup - Cancel all open dialogs in reverse order.
            // CC wizard stays behind Resolve Differences; close it too.
            CancelDialog(wizHwnd, "Alter Script Wizard");
            Thread.Sleep(300);
            CancelDialog(resolveDlg, "Resolve Differences");
            Thread.Sleep(200);
            // Close CC wizard if still visible
            PostMessage(ccWizard, WM_COMMAND, MakeWParam(CC_CLOSE, 0), IntPtr.Zero);
            _log("-> Posting CC_CLOSE to Complete Compare wizard");

            return script;
        }

        /// <summary>Returns the visible erwin.exe main window (XTPMainFrame class).</summary>
        public static IntPtr FindErwinMain()
        {
            IntPtr found = IntPtr.Zero;
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((hwnd, lp) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != myPid) return true;
                var cls = new StringBuilder(64);
                GetClassName(hwnd, cls, 64);
                if (cls.ToString() == "XTPMainFrame") { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Returns a set of all visible top-level window handles in the current process.</summary>
        public static HashSet<IntPtr> EnumerateVisibleDialogs()
        {
            var set = new HashSet<IntPtr>();
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((hwnd, lp) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == myPid) set.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return set;
        }

        /// <summary>Waits for a new visible window (not in the baseline set) to appear.</summary>
        public IntPtr WaitForNewDialog(HashSet<IntPtr> baseline, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                var current = EnumerateVisibleDialogs();
                foreach (var h in current)
                {
                    if (baseline.Contains(h)) continue;
                    // Filter out tooltips and trivial windows by class
                    var cls = new StringBuilder(64);
                    GetClassName(h, cls, 64);
                    string cn = cls.ToString();
                    if (cn.Contains("tooltip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (cn == "XTPTrayIcon" || cn == "MSCTFIME UI" || cn == "IME") continue;
                    // Must have a non-empty title
                    var t = new StringBuilder(256);
                    GetWindowText(h, t, 256);
                    if (string.IsNullOrWhiteSpace(t.ToString())) continue;
                    return h;
                }
                Thread.Sleep(100);
                waited += 100;
            }
            return IntPtr.Zero;
        }

        /// <summary>Waits (polling every 100ms) for any visible dialog whose title contains any of the given substrings.</summary>
        public IntPtr WaitForDialog(string[] titleSubstrs, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                IntPtr found = IntPtr.Zero;
                EnumWindows((hwnd, lp) =>
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    var t = new StringBuilder(512);
                    GetWindowText(hwnd, t, 512);
                    string title = t.ToString();
                    if (string.IsNullOrEmpty(title)) return true;
                    foreach (var s in titleSubstrs)
                    {
                        if (title.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found = hwnd;
                            return false;
                        }
                    }
                    return true;
                }, IntPtr.Zero);
                if (found != IntPtr.Zero) return found;
                Thread.Sleep(100);
                waited += 100;
            }
            return IntPtr.Zero;
        }

        public static string GetWindowTitle(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, 512);
            return sb.ToString();
        }

        /// <summary>Clicks a button by name inside the given dialog. Returns true on success.</summary>
        public bool ClickButtonByName(IntPtr dialogHwnd, string[] namesInPreferredOrder)
        {
            var root = AutomationElement.FromHandle(dialogHwnd);
            if (root == null) return false;
            foreach (var name in namesInPreferredOrder)
            {
                var cond = new PropertyCondition(AutomationElement.NameProperty, name);
                var btn = root.FindFirst(TreeScope.Descendants, cond);
                if (btn != null)
                {
                    try
                    {
                        if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out object ip))
                        {
                            ((InvokePattern)ip).Invoke();
                            _log($"  clicked '{name}' button");
                            return true;
                        }
                    }
                    catch (Exception ex) { _log($"  click '{name}' failed: {ex.Message}"); }
                }
            }
            _log($"  no button found matching {string.Join("/", namesInPreferredOrder)}");
            return false;
        }

        public bool IsButtonEnabledByName(IntPtr dialogHwnd, string name)
        {
            try
            {
                var root = AutomationElement.FromHandle(dialogHwnd);
                if (root == null) return false;
                var cond = new PropertyCondition(AutomationElement.NameProperty, name);
                var btn = root.FindFirst(TreeScope.Descendants, cond);
                return btn != null && btn.Current.IsEnabled;
            }
            catch { return false; }
        }

        /// <summary>
        /// Reads the alter-script text from the wizard's Preview page.
        /// The editor is a Codejock control whose UIA class name can vary; we try
        /// several candidate class names, then a Win32 fallback using WM_GETTEXT
        /// on any Scintilla/Edit/RichEdit descendant, then a global keyboard
        /// "focus editor + Ctrl+A + Ctrl+C" clipboard fallback.
        /// </summary>
        public string ExtractPreviewText(IntPtr wizardHwnd)
        {
            var root = AutomationElement.FromHandle(wizardHwnd);
            if (root == null) return null;

            // Candidate Codejock/Scintilla class prefixes to look for
            string[] editorClassHints =
            {
                "CodejockSyntaxEditor", "CXTPSyntaxEdit", "XTPSyntax",
                "Scintilla", "RICHEDIT50W", "RichEdit20", "RICHEDIT60W"
            };

            // 1) Try UIA enumeration - we log every classname we see so we can
            //    discover the real one on this erwin build.
            AutomationElement editor = null;
            var seenClasses = new HashSet<string>();
            try
            {
                var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                foreach (AutomationElement e in all)
                {
                    string cls = null;
                    try { cls = e.Current.ClassName; } catch { }
                    if (string.IsNullOrEmpty(cls)) continue;
                    seenClasses.Add(cls);
                    foreach (var hint in editorClassHints)
                    {
                        if (cls.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                        { editor = e; break; }
                    }
                    if (editor != null) break;
                }
            }
            catch (Exception ex) { _log($"  UIA enum err: {ex.Message}"); }

            if (editor == null)
            {
                _log("  editor element not found via UIA. Classnames seen in wizard:");
                foreach (var c in seenClasses) _log("    " + c);
            }
            else
            {
                _log($"  found editor class='{editor.Current.ClassName}'");
                try
                {
                    string text = editor.Current.Name;
                    if (!string.IsNullOrEmpty(text)) return text;
                }
                catch { }
                // Try ValuePattern on the editor
                try
                {
                    if (editor.TryGetCurrentPattern(ValuePattern.Pattern, out object vp))
                    {
                        string v = ((ValuePattern)vp).Current.Value;
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
                catch { }
                // Try TextPattern on the editor
                try
                {
                    if (editor.TryGetCurrentPattern(TextPattern.Pattern, out object tp))
                    {
                        var range = ((TextPattern)tp).DocumentRange;
                        string v = range.GetText(-1);
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
                catch { }
            }

            // 2) Win32 fallback: enumerate all descendant HWNDs, read text via
            //    WM_GETTEXT / WM_GETTEXTLENGTH for anything that looks like an edit.
            string win32Text = ExtractViaWin32(wizardHwnd);
            if (!string.IsNullOrEmpty(win32Text)) return win32Text;

            // 3) Clipboard fallback via keyboard shortcuts on whatever has focus.
            //    Find a Codejock-ish HWND, focus it, then Ctrl+A+C.
            try
            {
                IntPtr editorHwnd = FindEditorHwnd(wizardHwnd);
                if (editorHwnd != IntPtr.Zero)
                {
                    _log($"  clipboard fallback: focusing editor HWND=0x{editorHwnd.ToInt64():X}");
                    SetForegroundWindow(editorHwnd);
                    Thread.Sleep(200);
                    System.Windows.Forms.SendKeys.SendWait("^a");
                    Thread.Sleep(150);
                    System.Windows.Forms.SendKeys.SendWait("^c");
                    Thread.Sleep(250);
                    string clip = System.Windows.Forms.Clipboard.GetText();
                    if (!string.IsNullOrEmpty(clip)) return clip;
                }
            }
            catch (Exception ex) { _log($"  clipboard fallback err: {ex.Message}"); }

            return null;
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_GETTEXTLENGTH = 0x000E;

        private string ExtractViaWin32(IntPtr wizardHwnd)
        {
            string best = null;
            int bestLen = 0;
            var allClasses = new List<string>();
            EnumChildWindows(wizardHwnd, (hwnd, lp) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(hwnd, cls, 64);
                string cn = cls.ToString();
                allClasses.Add($"0x{hwnd.ToInt64():X}:{cn}");
                if (cn.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0
                    || cn.IndexOf("Scintilla", StringComparison.OrdinalIgnoreCase) >= 0
                    || cn.IndexOf("Codejock", StringComparison.OrdinalIgnoreCase) >= 0
                    || cn.IndexOf("XTPSyntax", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    IntPtr lenPtr = SendMessageIntPtr(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                    int len = lenPtr.ToInt32();
                    if (len > bestLen)
                    {
                        IntPtr buf = Marshal.AllocHGlobal((len + 2) * 2);
                        try
                        {
                            SendMessageIntPtr(hwnd, WM_GETTEXT, new IntPtr(len + 1), buf);
                            string s = Marshal.PtrToStringUni(buf);
                            if (!string.IsNullOrEmpty(s)) { best = s; bestLen = len; }
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }
                return true;
            }, IntPtr.Zero);
            if (string.IsNullOrEmpty(best))
            {
                _log("  Win32 fallback: no text from any edit-like descendant. Child classes:");
                foreach (var c in allClasses) _log("    " + c);
            }
            else _log($"  Win32 fallback: extracted {bestLen} chars");
            return best;
        }

        private static IntPtr FindEditorHwnd(IntPtr parent)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, (hwnd, lp) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(hwnd, cls, 64);
                string cn = cls.ToString();
                if (cn.IndexOf("Codejock", StringComparison.OrdinalIgnoreCase) >= 0
                    || cn.IndexOf("XTPSyntax", StringComparison.OrdinalIgnoreCase) >= 0
                    || cn.IndexOf("Scintilla", StringComparison.OrdinalIgnoreCase) >= 0)
                { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Navigate the CC wizard to the "Right Model" page and select a loaded model
        /// that is different from the active (Left) model.
        /// The Right Model page contains a SysListView32 (id=1083) with all loaded PUs;
        /// the active model is typically first. We pick the first entry whose text
        /// differs from the current active model's name.
        /// </summary>
        /// <summary>
        /// Applies the user-chosen right-model source to the CC wizard's Right Model
        /// page. Radios on that page have automationIds: File=1079, Database=1080,
        /// Mart=1081. For OpenModel we explicitly click the radio and fill
        /// the loaded-models list. Mart and Database fall through to the legacy
        /// "select last-in-list" heuristic (erwin remembers session state).
        /// </summary>
        public void ApplyRightModelSelection(IntPtr wizardHwnd, AlterWizardConfig config)
        {
            // Go to Right Model page (Back to Overview, then Next×2)
            for (int i = 0; i < 12; i++)
            {
                string t = GetWindowTitle(wizardHwnd);
                if (t.StartsWith("Wizard Overview") || t.StartsWith("Overview")) break;
                PostMessage(wizardHwnd, WM_COMMAND, MakeWParam(CC_BACK, 0), IntPtr.Zero);
                Thread.Sleep(120);
            }
            PostMessage(wizardHwnd, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero);
            Thread.Sleep(200);
            PostMessage(wizardHwnd, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero);
            Thread.Sleep(400);
            _log($"  navigated to Right Model page. Title='{GetWindowTitle(wizardHwnd)}'");

            if (config == null || config.Right == RightModelSource.OpenModel)
            {
                // Legacy path OR internal OpenModel (used by DB reverse-engineer flow)
                SelectFromLoadedModels(wizardHwnd, config?.OpenModelName);
                return;
            }

            if (config.Right == RightModelSource.Mart && config.MartVersion > 0)
            {
                SelectMartVersion(wizardHwnd, config.MartVersion);
                return;
            }

            // Mart (no version) / Database: let erwin's last-session selection stand
            _log($"  Right source = {config.Right} (using erwin's session state)");
        }

        /// <summary>
        /// UIA-drives the Right Model Selection dialog:
        ///  - Posts WM_COMMAND 1081 to pick the "From Mart" radio.
        ///  - Enumerates the Mart version picker (listview/combo) and selects the
        ///    item whose text matches "Version N" / "v N" / just "N".
        ///  - Posts WM_COMMAND 1082 to press Load.
        /// Logs the full control tree on first failure so we can refine.
        /// </summary>
        private void SelectMartVersion(IntPtr wizardHwnd, int targetVersion)
        {
            _log($"  SelectMartVersion(v{targetVersion}) entering...");

            // Step 1: click the "From Mart" radio. WM_COMMAND 1081 is the documented
            // control ID (older memory note; verified empirically in comment above).
            PostMessage(wizardHwnd, WM_COMMAND, MakeWParam(1081, 0), IntPtr.Zero);
            Thread.Sleep(300);
            _log($"  posted WM_COMMAND 1081 (Mart radio). Title='{GetWindowTitle(wizardHwnd)}'");

            var root = AutomationElement.FromHandle(wizardHwnd);
            if (root == null) { _log("  [WARN] UIA root null on Right Model dialog"); return; }

            // Step 2: look for any ListItem / ComboBoxItem / TreeItem child whose Name
            // mentions the version. Erwin's version labels come in flavors:
            //   "1" / "v1" / "Version 1" / "1 - 2026-04-18 ..."
            var listCond = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
            var items = root.FindAll(TreeScope.Descendants, listCond);
            _log($"  found {items.Count} list/tree items in dialog");

            AutomationElement target = null;
            string vstr = targetVersion.ToString();
            foreach (AutomationElement e in items)
            {
                string n = "";
                try { n = e.Current.Name ?? ""; } catch { }
                if (string.IsNullOrEmpty(n)) continue;
                _log($"    item: '{n}'");
                if (n == vstr
                    || n.StartsWith($"v{vstr}", StringComparison.OrdinalIgnoreCase)
                    || n.IndexOf($"Version {vstr}", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.StartsWith($"{vstr} ") || n.StartsWith($"{vstr}\t"))
                {
                    target = e;
                    break;
                }
            }

            if (target == null)
            {
                _log($"  [WARN] no list item matches target v{targetVersion} - dumping full control tree for diagnosis");
                DumpUiaTree(root, 0, 80);
                // Fall back to pressing Load anyway - erwin may have defaulted a sensible
                // version (highest available). At worst we get a compare-to-itself popup
                // which our existing code already handles.
            }
            else
            {
                try
                {
                    string n = target.Current.Name ?? "";
                    _log($"  selecting version item '{n}'");
                    if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sip))
                        ((SelectionItemPattern)sip).Select();
                    else
                        target.SetFocus();
                    Thread.Sleep(150);
                }
                catch (Exception ex) { _log($"  select item err: {ex.Message}"); }
            }

            // Step 3: press Load (WM_COMMAND 1082)
            PostMessage(wizardHwnd, WM_COMMAND, MakeWParam(1082, 0), IntPtr.Zero);
            Thread.Sleep(500);
            _log($"  posted WM_COMMAND 1082 (Load). Title='{GetWindowTitle(wizardHwnd)}'");
        }

        /// <summary>Walks the UIA subtree and logs each element's type/name/automationId.
        /// Capped to `maxNodes` to keep the log manageable.</summary>
        private void DumpUiaTree(AutomationElement el, int depth, int maxNodes)
        {
            if (el == null || depth > 10) return;
            if (maxNodes <= 0) return;
            int remaining = maxNodes;
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
                _log($"  {new string(' ', depth * 2)}[{type}] name='{name}' id='{aid}' cls='{cls}'");
                remaining--;
            }
            catch { }
            try
            {
                var children = el.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement c in children)
                {
                    if (remaining <= 0) return;
                    DumpUiaTree(c, depth + 1, remaining);
                    remaining -= 20;   // rough budget per subtree
                }
            }
            catch { }
        }

        /// <summary>
        /// Select a specific loaded-model from the Right Model page's listview.
        /// If modelName is null, picks the last item (legacy behaviour).
        /// </summary>
        private void SelectFromLoadedModels(IntPtr wizardHwnd, string modelName)
        {
            var root = AutomationElement.FromHandle(wizardHwnd);
            if (root == null) { _log("  [WARN] cannot get wizard UIA root"); return; }
            var cond = new PropertyCondition(AutomationElement.AutomationIdProperty, "1083");
            AutomationElement listView = root.FindFirst(TreeScope.Descendants, cond);
            if (listView == null) { _log("  [WARN] loaded-models list (id=1083) not found"); return; }

            var itemCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
            var items = listView.FindAll(TreeScope.Descendants, itemCond);
            _log($"  loaded-models list has {items.Count} item(s)");
            if (items.Count == 0) { _log("  [WARN] no loaded models"); return; }

            AutomationElement target = null;
            if (!string.IsNullOrEmpty(modelName))
            {
                foreach (AutomationElement e in items)
                {
                    try
                    {
                        string n = e.Current.Name ?? "";
                        if (n.IndexOf(modelName, StringComparison.OrdinalIgnoreCase) >= 0)
                        { target = e; break; }
                    }
                    catch { }
                }
                if (target == null) _log($"  [WARN] no list item matching '{modelName}' - falling back to last");
            }
            if (target == null) target = items[items.Count - 1];

            try
            {
                string t = target.Current.Name ?? "";
                _log($"  selecting '{t}'");
                if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sip))
                    ((SelectionItemPattern)sip).Select();
                else target.SetFocus();
                Thread.Sleep(150);
                // Trigger Load... to commit selection
                PostMessage(wizardHwnd, WM_COMMAND, MakeWParam(1082, 0), IntPtr.Zero);
                Thread.Sleep(300);
            }
            catch (Exception ex) { _log($"  select err: {ex.Message}"); }
        }

        public void SelectDifferentRightModel(IntPtr wizardHwnd)
        {
            // Step 1: go back to Overview via WM_COMMAND (fast)
            for (int i = 0; i < 12; i++)
            {
                string t = GetWindowTitle(wizardHwnd);
                if (t.StartsWith("Wizard Overview") || t.StartsWith("Overview")) break;
                PostMessage(wizardHwnd, WM_COMMAND, MakeWParam(CC_BACK, 0), IntPtr.Zero);
                Thread.Sleep(120);
            }
            _log($"  back to first page. Title='{GetWindowTitle(wizardHwnd)}'");

            // Step 2: Next twice -> Right Model
            PostMessage(wizardHwnd, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero);
            Thread.Sleep(200);
            PostMessage(wizardHwnd, WM_COMMAND, MakeWParam(CC_NEXT, 0), IntPtr.Zero);
            Thread.Sleep(300);
            _log($"  navigated forward. Title='{GetWindowTitle(wizardHwnd)}'");

            // Step 3: Find the SysListView32 with loaded models (AutomationId=1083)
            var root = AutomationElement.FromHandle(wizardHwnd);
            if (root == null) { _log("  [WARN] cannot get wizard UIA root"); return; }

            AutomationElement listView = null;
            var cond = new PropertyCondition(AutomationElement.AutomationIdProperty, "1083");
            listView = root.FindFirst(TreeScope.Descendants, cond);
            if (listView == null)
            {
                _log("  [WARN] could not find loaded-models list (id=1083)");
                return;
            }

            // Step 4: enumerate items, pick the first one that's different from Left selection.
            // MFC ListView items expose via UIA with ControlType=ListItem.
            var itemCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
            var items = listView.FindAll(TreeScope.Descendants, itemCond);
            _log($"  loaded-models list has {items.Count} item(s)");
            for (int i = 0; i < items.Count; i++)
            {
                try
                {
                    var item = items[i];
                    string name = item.Current.Name ?? "";
                    _log($"    [{i}] '{name}'");
                }
                catch { }
            }

            if (items.Count == 0)
            {
                _log("  [WARN] no items in loaded-models list. User must load a second model into erwin first.");
                return;
            }

            // Heuristic: pick the LAST item (most-recently-loaded, likely the baseline
            // the user just opened). If only 1 item exists we skip selection - Compare
            // will hit the self-compare popup and we'll throw a clear error.
            if (items.Count < 2)
            {
                _log("  [WARN] only 1 model loaded in erwin. Load a second model to pick as Right Model.");
                return;
            }
            try
            {
                var target = items[items.Count - 1];
                string targetName = target.Current.Name ?? "";
                _log($"  selecting item [{items.Count - 1}] '{targetName}' as Right Model");

                if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sip))
                {
                    ((SelectionItemPattern)sip).Select();
                    _log("  selected via SelectionItemPattern");
                }
                else
                {
                    // Fallback: set focus then send Ctrl+Space or click
                    target.SetFocus();
                    Thread.Sleep(150);
                    _log("  selected via SetFocus (SelectionItemPattern unavailable)");
                }

                // Click Load... button (id=1082) to commit selection as right model.
                // Many erwin builds auto-select on click but some need the Load button.
                Thread.Sleep(200);
                if (ClickButtonByName(wizardHwnd, new[] { "Load..." }))
                {
                    Thread.Sleep(300);
                }
            }
            catch (Exception ex) { _log($"  selection error: {ex.Message}"); }
        }

        public void CancelDialog(IntPtr hwnd, string description)
        {
            if (hwnd == IntPtr.Zero) return;
            _log($"-> Posting IDCANCEL to {description}");
            PostMessage(hwnd, WM_COMMAND, MakeWParam(IDCANCEL, 0), IntPtr.Zero);
        }
    }
}
