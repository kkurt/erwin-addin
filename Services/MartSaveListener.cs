using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Passive listener that captures the text the user types into erwin's
    /// "Description for '&lt;model&gt;' Version &lt;N&gt;" dialog when a Mart
    /// incremental save is in progress. We do NOT drive the dialog (no UI
    /// automation, no SetWindowText, not even a programmatic toolbar click) -
    /// the user opens the dialog by clicking erwin's Mart Save button
    /// themselves and types the description there. The listener polls the
    /// dialog's multi-line Edit child every ~150 ms and records the most
    /// recent text. The text captured at the moment the dialog destroyed is
    /// reported as the user-entered description.
    ///
    /// Why we cannot just call pu.Save(): on a Mart-bound PU it commits the
    /// model silently via the SCAPI Save path (MCXGDMPersister_Mart::Save),
    /// which bypasses the UI-driven MCXModelIncrementalSaveCommand and never
    /// shows the description dialog (verified 2026-05-19 11:28 against
    /// FIBA Mart model - pu.Save returned True in 36 ms with no dialog).
    /// The toolbar-driven Save is the only path that surfaces the dialog,
    /// so the user clicks it themselves; we just listen.
    ///
    /// Background dialog hosted by EM_MCX.dll (class
    /// <c>MCXIncrementalSave_VersionDescriptionDialog</c>). Save button calls
    /// <c>MCXGDMPersister_Mart::SetDescription(CString)</c> before the modal
    /// closes. No public SCAPI getter exists, so reading the Edit while the
    /// dialog is alive is the only viable path.
    /// </summary>
    public static class MartSaveListener
    {
        /// <summary>
        /// Last text seen in the description Edit. Empty when the user
        /// clicked Save without typing; null when the dialog was never
        /// observed.
        /// </summary>
        public static string CapturedDescription { get; private set; }

        /// <summary>
        /// True if the dialog was observed at any point during the listening
        /// window. Distinguishes "dialog never appeared" (false) from "user
        /// typed empty + clicked Save" (true with empty string).
        /// </summary>
        public static bool DialogObserved { get; private set; }

        private static Thread _thread;
        private static CancellationTokenSource _cts;
        private static Action<string> _log;
        private static TaskCompletionSource<bool> _completion;

        public static void Start(Action<string> log = null)
        {
            Stop(); // idempotent
            CapturedDescription = null;
            DialogObserved = false;
            _log = log;
            _cts = new CancellationTokenSource();
            _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(PollLoop) { IsBackground = true, Name = "MartSaveListener" };
            _thread.Start(_cts.Token);
        }

        /// <summary>
        /// Awaits the listener's PollLoop completion - i.e. either the
        /// description dialog opened and then closed, or the wait timeout
        /// elapsed without ever seeing the dialog. Caller should then read
        /// <see cref="CapturedDescription"/> and <see cref="DialogObserved"/>
        /// to decide whether to proceed with the queue insert.
        /// </summary>
        public static Task<bool> WaitForCompletionAsync(TimeSpan timeout)
        {
            if (_completion == null) return Task.FromResult(false);
            var t = _completion.Task;
            return t.ContinueWith(_ => true,
                TaskContinuationOptions.OnlyOnRanToCompletion |
                TaskContinuationOptions.ExecuteSynchronously)
                .WithTimeout(timeout);
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _thread?.Join(800); } catch { }
            try { _completion?.TrySetResult(false); } catch { }
            _thread = null;
            _cts = null;
            _completion = null;
        }

        private static void PollLoop(object state)
        {
            var token = (CancellationToken)state;
            try
            {
                // Phase 1: wait for the user to click Mart Save in erwin and
                // for the description dialog to appear. Cap at 5 minutes -
                // longer than 60 s because we are waiting on a human, not on
                // a programmatic call. Cancellation token aborts the wait
                // early when Stop is invoked (popup closed / user cancelled).
                IntPtr dlg = IntPtr.Zero;
                var sw = Stopwatch.StartNew();
                while (!token.IsCancellationRequested && dlg == IntPtr.Zero && sw.Elapsed.TotalMinutes < 5)
                {
                    Thread.Sleep(150);
                    dlg = FindDescriptionDialog();
                }
                if (dlg == IntPtr.Zero)
                {
                    _log?.Invoke("MartSaveListener: description dialog never observed (cancelled or 5-min timeout)");
                    return;
                }
                DialogObserved = true;
                _log?.Invoke($"MartSaveListener: dialog observed, hWnd=0x{dlg.ToInt64():X}");

                // Phase 2: poll the Edit child while the dialog is alive.
                while (!token.IsCancellationRequested && IsWindow(dlg))
                {
                    IntPtr edit = FindEditChild(dlg);
                    if (edit != IntPtr.Zero)
                    {
                        string text = GetWindowTextSafe(edit);
                        if (text != null) CapturedDescription = text;
                    }
                    Thread.Sleep(150);
                }
                _log?.Invoke($"MartSaveListener: dialog closed, captured length={CapturedDescription?.Length ?? 0}");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"MartSaveListener: poll loop threw {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { _completion?.TrySetResult(true); } catch { }
            }
        }

        #region Task timeout helper

        // .NET 6+ Task.WaitAsync(TimeSpan) is the canonical version; bundling
        // a tiny self-contained equivalent here keeps the listener decoupled
        // from a specific target framework. Returns false when the timeout
        // elapses before the underlying task completes.
        private static async Task<bool> WithTimeout(this Task<bool> task, TimeSpan timeout)
        {
            var delay = Task.Delay(timeout);
            var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
            return completed == task && task.Result;
        }

        #endregion

        #region Win32 P/Invoke

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        private static IntPtr FindDescriptionDialog()
        {
            // The dialog title is "Description for '<model>' Version <N>".
            // We match on the leading prefix (case-insensitive) to handle
            // any localisation that might alter the trailing model/version
            // formatting. Restricted to visible top-level windows owned by
            // an erwin process so we don't pick up unrelated apps.
            var erwinPids = new System.Collections.Generic.HashSet<uint>();
            try
            {
                foreach (var p in Process.GetProcessesByName("erwin"))
                    erwinPids.Add((uint)p.Id);
            }
            catch { /* best effort */ }
            if (erwinPids.Count == 0) return IntPtr.Zero;

            IntPtr result = IntPtr.Zero;
            EnumWindows((hWnd, lp) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!erwinPids.Contains(pid)) return true;
                string title = GetWindowTextSafe(hWnd);
                if (string.IsNullOrEmpty(title)) return true;
                if (title.StartsWith("Description for ", StringComparison.OrdinalIgnoreCase))
                {
                    result = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static IntPtr FindEditChild(IntPtr dialog)
        {
            // The description dialog has a single multi-line Edit. Pick the
            // first child whose class starts with "Edit" (covers "Edit" and
            // "RichEdit20W" variants) and which holds the dialog's text
            // (i.e. not a label / static). Use Z-order's natural visit:
            // EnumChildWindows returns children in z-order so the first hit
            // is the main editor in practice.
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(dialog, (hWnd, lp) =>
            {
                string cls = GetClassNameSafe(hWnd);
                if (string.IsNullOrEmpty(cls)) return true;
                if (cls.StartsWith("Edit", StringComparison.OrdinalIgnoreCase) ||
                    cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static string GetWindowTextSafe(IntPtr hWnd)
        {
            try
            {
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return string.Empty;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return null; }
        }

        private static string GetClassNameSafe(IntPtr hWnd)
        {
            try
            {
                var sb = new StringBuilder(64);
                int n = GetClassName(hWnd, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : string.Empty;
            }
            catch { return string.Empty; }
        }

        #endregion
    }
}
