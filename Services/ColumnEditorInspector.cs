using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// TEMPORARY discovery service. While erwin's Column Editor dialog is open, log every
    /// window create/show event so we can identify the Physical Data Type dropdown's
    /// window class and message flow. Once we know what control type erwin uses, this
    /// service is replaced by an actual filter implementation and removed.
    ///
    /// Usage: instantiate and call <see cref="Start"/> from ModelConfigForm; output goes
    /// through <see cref="OnLog"/>. The hook is global (WINEVENT_OUTOFCONTEXT) but the
    /// service stays quiet until a Column Editor is detected, so day-to-day use is not
    /// affected. Call <see cref="Stop"/> when the parent form disposes.
    /// </summary>
    public sealed class ColumnEditorInspector : IDisposable
    {
        public event Action<string> OnLog;

        // ---- WinEvent constants ----
        private const uint EVENT_OBJECT_CREATE  = 0x8000;
        private const uint EVENT_OBJECT_DESTROY = 0x8001;
        private const uint EVENT_OBJECT_SHOW    = 0x8002;
        private const uint EVENT_OBJECT_FOCUS   = 0x8005;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNTHREAD = 0x0001;

        // ---- P/Invoke ----
        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        private const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // ---- State ----
        // Hold a strong reference to the delegate so the GC doesn't collect it while
        // the unmanaged side still holds the function pointer (very common WinEvent bug).
        private WinEventDelegate _callback;
        private IntPtr _hook;
        private IntPtr _editorHwnd;        // active Column Editor (when non-zero, we are logging)
        private DateTime _editorOpenedAt;
        private int _eventCount;

        public bool IsActive => _editorHwnd != IntPtr.Zero;

        public void Start()
        {
            if (_hook != IntPtr.Zero) return;
            _callback = OnWinEvent;
            // Cover create/show/destroy/focus in a single hook range so we see the full
            // lifecycle: editor opens (CREATE), gets shown (SHOW), child windows appear
            // when combo dropdown opens, focus changes between cells, editor closes (DESTROY).
            // NOTE: SKIPOWNTHREAD must NOT be set. We are hosted inside erwin's process, so
            // erwin's UI thread IS our own thread; SKIPOWNTHREAD silently dropped every event
            // and the hook never logged anything. WINEVENT_OUTOFCONTEXT alone runs the
            // callback in our process, on the receiving thread, which is what we want.
            _hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_FOCUS,
                IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
            Log("[Inspector] WinEvent hook installed (global, OUT_OF_CONTEXT, in-thread)");
        }

        public void Stop()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            _callback = null;
            Log("[Inspector] WinEvent hook removed");
        }

        public void Dispose() => Stop();

        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
            uint dwEventThread, uint dwmsEventTime)
        {
            // Capture window-level (OBJID_WINDOW=0) AND client-level (OBJID_CLIENT=-4)
            // events. Some controls (combo dropdowns, custom listboxes) emit their lifecycle
            // on OBJID_CLIENT, so the previous "only 0" filter dropped them. We filter out
            // accessibility object IDs (-1, -2, -3, ...) that would otherwise flood the log.
            if (hwnd == IntPtr.Zero) return;
            if (idObject != 0 /* OBJID_WINDOW */ && idObject != -4 /* OBJID_CLIENT */) return;

            try
            {
                // (1) Editor lifecycle: detect open / close.
                if (_editorHwnd == IntPtr.Zero)
                {
                    if (eventType == EVENT_OBJECT_CREATE || eventType == EVENT_OBJECT_SHOW)
                    {
                        // Diagnostic: log every top-level window with a caption that mentions
                        // "Editor" (or "Column"). This way if our match miss-fires we'll see
                        // the actual caption format erwin uses and can tighten the matcher.
                        string capProbe = GetCaption(hwnd);
                        if (!string.IsNullOrEmpty(capProbe)
                            && (capProbe.IndexOf("Editor", StringComparison.OrdinalIgnoreCase) >= 0
                                || capProbe.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            Log($"[Inspector] candidate window: cap='{capProbe}' class='{GetClass(hwnd)}' hwnd={hwnd.ToInt64():X8} parent={GetParent(hwnd).ToInt64():X}");
                        }

                        if (LooksLikeColumnEditor(hwnd))
                        {
                            _editorHwnd = hwnd;
                            _editorOpenedAt = DateTime.UtcNow;
                            _eventCount = 0;
                            Log($"[Inspector] === Column Editor detected hwnd={hwnd.ToInt64():X} caption='{GetCaption(hwnd)}' class='{GetClass(hwnd)}' ===");
                            DumpWindowTree(hwnd, 0, 4);
                        }
                    }
                    return;
                }

                // (2) Editor active: log everything until it goes away.
                if (eventType == EVENT_OBJECT_DESTROY && hwnd == _editorHwnd)
                {
                    var dur = DateTime.UtcNow - _editorOpenedAt;
                    Log($"[Inspector] === Column Editor closed after {dur.TotalSeconds:F1}s, {_eventCount} events recorded ===");
                    _editorHwnd = IntPtr.Zero;
                    return;
                }

                // While editor is open, log creates/shows/focus on any window. Cap at a sane
                // event count so a runaway scenario can't spam the file forever.
                if (_eventCount > 2000) return;
                _eventCount++;

                string evName = eventType switch
                {
                    EVENT_OBJECT_CREATE => "CREATE",
                    EVENT_OBJECT_SHOW => "SHOW",
                    EVENT_OBJECT_DESTROY => "DESTROY",
                    EVENT_OBJECT_FOCUS => "FOCUS",
                    _ => $"0x{eventType:X}"
                };

                if (!IsWindow(hwnd)) return;

                string cls = GetClass(hwnd);
                string cap = GetCaption(hwnd);
                IntPtr parent = GetParent(hwnd);
                IntPtr root = GetAncestor(hwnd, GA_ROOT);
                GetWindowRect(hwnd, out var r);
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;

                // Filter: only events whose root is the editor OR whose root is a top-level
                // popup that opened while editor is active (the dropdown likely is one).
                bool relatedToEditor = (root == _editorHwnd);
                bool topLevelPopup = (parent == IntPtr.Zero || parent == GetDesktopWindowSafe());

                if (!relatedToEditor && !topLevelPopup) return;

                Log($"[Inspector] {evName,-7} oid={idObject,3} hwnd={hwnd.ToInt64():X8} cls='{cls}' cap='{cap}' parent={parent.ToInt64():X} root={root.ToInt64():X} {(relatedToEditor ? "[editor-tree]" : "[topLevelPopup]")} rect={r.Left},{r.Top},{w}x{h}");
            }
            catch (Exception ex)
            {
                Log($"[Inspector] callback error: {ex.Message}");
            }
        }

        private static IntPtr _desktopHwnd;
        private static IntPtr GetDesktopWindowSafe()
        {
            if (_desktopHwnd == IntPtr.Zero) _desktopHwnd = GetDesktopWindow();
            return _desktopHwnd;
        }
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

        private static bool LooksLikeColumnEditor(IntPtr hwnd)
        {
            // Editor caption observed in screenshots:
            //   "SQL Server Table 'VPBACK...' Column 'NAME' Editor"
            // Be liberal: contains "Column" AND "Editor", and is at least a top-level dialog.
            string cap = GetCaption(hwnd);
            if (string.IsNullOrEmpty(cap)) return false;
            return cap.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0
                && cap.IndexOf("Editor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DumpWindowTree(IntPtr root, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            try
            {
                EnumChildWindows(root, (h, l) =>
                {
                    string cls = GetClass(h);
                    string cap = GetCaption(h);
                    GetWindowRect(h, out var r);
                    string indent = new string(' ', (depth + 1) * 2);
                    Log($"[Inspector]{indent}child hwnd={h.ToInt64():X8} cls='{cls}' cap='{cap}' rect={r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top}");
                    DumpWindowTree(h, depth + 1, maxDepth);
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex) { Log($"[Inspector] DumpWindowTree error: {ex.Message}"); }
        }

        private static string GetClass(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetCaption(IntPtr hwnd)
        {
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private void Log(string msg)
        {
            try { OnLog?.Invoke(msg); } catch { }
        }
    }
}
