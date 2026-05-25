using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Subclasses erwin's XTPMainFrame WindowProc to discover and persist the
    /// dynamically-assigned <c>WM_COMMAND</c> id erwin uses to dispatch our
    /// Tools&gt;Add-Ins menu entry. With that id, the autostart watcher can
    /// <c>PostMessage(erwinMain, WM_COMMAND, id, 0)</c> from outside the
    /// erwin process to auto-load the addin WITHOUT DLL injection - which
    /// Symantec SEP's SONAR.ProcHijack heuristic flags.
    ///
    /// All other discovery paths failed empirically (verified 2026-05-25):
    /// UIA Invoke from cross-process is filtered by XTP, MSAA
    /// accDoDefaultAction is not implemented, TB_GETBUTTON returns 0
    /// (XTP overrides the toolbar protocol), the XTPPopupBar has no
    /// HMENU, erwin doesn't register in the ROT, MSAA EVENT_OBJECT_INVOKED
    /// doesn't fire for ribbon clicks. The ONLY path is in-process
    /// WindowProc subclassing because erwin's command map dispatches
    /// the WM_COMMAND BEFORE calling CoCreateInstance/Execute() - so a
    /// subclass installed on the previous Execute() catches the next one.
    ///
    /// Persistence: the captured cmd id is written to
    /// <c>HKCU\Software\EliteSoft\ErwinAddIn\Watcher\AddinCmdId</c> (DWORD).
    /// The watcher reads this value at erwin startup and PostMessages it.
    /// Two-click first-time setup: click #1 installs the subclass, click #2
    /// is caught and the id is persisted. From then on, auto-load works
    /// across all future erwin sessions with no manual interaction.
    ///
    /// Stability: empirically confirmed across erwin restarts with one
    /// registered addin. If erwin re-numbers add-in command IDs (e.g.
    /// when a second add-in is registered), MarkExecuteEntry self-heals
    /// by overwriting the stored value on every invocation.
    ///
    /// Idempotent. Safe to call from every Execute().
    /// </summary>
    internal static class WmCommandLogger
    {
        private const uint WM_COMMAND = 0x0111;
        private const int  GWLP_WNDPROC = -4;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // Hold strong refs for the lifetime of the process. If the GC reaped
        // the delegate, the native call from erwin's message pump would jump
        // to freed memory and crash the host.
        private static WndProcDelegate _subclass;
        private static IntPtr          _subclassFn = IntPtr.Zero;
        private static IntPtr          _originalWndProc = IntPtr.Zero;
        private static IntPtr          _hookedHwnd = IntPtr.Zero;
        private static readonly object _gate = new object();
        private static int _installed;

        // Thread-local "last WM_COMMAND wParam" seen by the subclass. erwin's
        // command map dispatches WM_COMMAND and CoCreateInstance + Execute
        // on the SAME thread (the main UI thread), in sequence with no
        // interleaving. MarkExecuteEntry reads this value to learn which
        // cmd id triggered the current Execute() call.
        [ThreadStatic]
        private static int _lastCmdIdOnThread;

        private const string RegistryWatcherKey = @"Software\EliteSoft\ErwinAddIn\Watcher";
        private const string RegistryCmdIdValue = "AddinCmdId";

        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EliteSoft", "ErwinAddIn", "wmcmd.log");

        public static void Install(IntPtr mainHwnd)
        {
            if (mainHwnd == IntPtr.Zero) return;
            // CompareExchange so concurrent Execute() calls don't double-subclass.
            if (Interlocked.Exchange(ref _installed, 1) != 0)
            {
                // Already installed; just log a re-entry marker so we know the
                // addin was re-invoked (and on which thread).
                WriteLine($"RE-ENTRY hwnd=0x{mainHwnd.ToInt64():X} tid={Thread.CurrentThread.ManagedThreadId}");
                return;
            }

            lock (_gate)
            {
                try
                {
                    EnsureLogDir();
                    _subclass = new WndProcDelegate(SubclassProc);
                    _subclassFn = Marshal.GetFunctionPointerForDelegate(_subclass);
                    _originalWndProc = SetWindowLongPtrW(mainHwnd, GWLP_WNDPROC, _subclassFn);
                    if (_originalWndProc == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        WriteLine($"INSTALL FAILED SetWindowLongPtrW err={err}");
                        Interlocked.Exchange(ref _installed, 0);
                        return;
                    }
                    _hookedHwnd = mainHwnd;
                    WriteLine($"INSTALL OK hwnd=0x{mainHwnd.ToInt64():X} orig=0x{_originalWndProc.ToInt64():X} pid={System.Diagnostics.Process.GetCurrentProcess().Id}");
                }
                catch (Exception ex)
                {
                    WriteLine($"INSTALL EXCEPTION {ex.GetType().Name}: {ex.Message}");
                    Interlocked.Exchange(ref _installed, 0);
                }
            }
        }

        /// <summary>
        /// Records that Execute() has been entered AND persists the
        /// most-recent WM_COMMAND wParam from this thread as the addin's
        /// invocation id. Subsequent watcher runs read this from registry
        /// and replay it via PostMessage.
        ///
        /// On the very first call (subclass freshly installed, no prior
        /// WM_COMMAND seen yet), the thread-local slot is 0 and nothing
        /// is persisted - the SECOND click captures and saves it. This is
        /// the one-time two-click setup the user does after install.
        /// </summary>
        public static void MarkExecuteEntry()
        {
            int captured = _lastCmdIdOnThread;
            try
            {
                EnsureLogDir();
                WriteLine($"EXECUTE entry tid={Thread.CurrentThread.ManagedThreadId} lastCmdIdOnThread={captured}");
            }
            catch { /* never fail the addin over a log line */ }

            if (captured > 0)
            {
                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(RegistryWatcherKey))
                    {
                        if (key != null)
                        {
                            // Idempotent self-heal: only rewrite when the
                            // value actually changed (avoids registry churn
                            // on every Execute when the id is already set).
                            var current = key.GetValue(RegistryCmdIdValue, -1);
                            int currentInt = -1;
                            if (current is int i) currentInt = i;
                            if (currentInt != captured)
                            {
                                key.SetValue(RegistryCmdIdValue, captured, RegistryValueKind.DWord);
                                WriteLine($"PERSIST AddinCmdId {currentInt} -> {captured} (HKCU\\{RegistryWatcherKey})");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteLine($"PERSIST FAILED {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_COMMAND)
            {
                try
                {
                    // wParam: low word = command id, high word = notification code.
                    // Source per Win32 docs: 0=menu, 1=accelerator, otherwise
                    // a control notification code from a child control. We
                    // only persist when notify is 0 (menu) - that's how the
                    // Add-In Manager dispatches our entry. Filtering avoids
                    // capturing button clicks that happen to share an id.
                    long w = wParam.ToInt64();
                    int cmdId = (int)(w & 0xFFFF);
                    int notify = (int)((w >> 16) & 0xFFFF);
                    long l = lParam.ToInt64();
                    WriteLine($"WM_COMMAND cmdId={cmdId} (0x{cmdId:X}) notify={notify} lParam=0x{l:X}");

                    // lParam == 0 AND notify == 0 -> menu dispatch (not a
                    // control notification). This is what Add-In Manager
                    // sends. Update the thread-local capture for the
                    // MarkExecuteEntry call that's about to happen.
                    if (notify == 0 && l == 0 && cmdId > 0)
                    {
                        _lastCmdIdOnThread = cmdId;
                    }
                }
                catch { /* never throw out of a wndproc */ }
            }
            return CallWindowProcW(_originalWndProc, hWnd, msg, wParam, lParam);
        }

        private static void EnsureLogDir()
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        private static void WriteLine(string text)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {text}\r\n";
                File.AppendAllText(_logPath, line);
            }
            catch { /* swallow - log is best-effort */ }
        }
    }
}
