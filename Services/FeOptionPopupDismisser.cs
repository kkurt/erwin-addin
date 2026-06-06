using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Background watcher that auto-dismisses (clicks OK on) erwin's
    /// "&lt;path&gt;\erwin_addin_ddl_fe_opt_cfg{N}.xml was not found" modal.
    ///
    /// Why this exists (2026-06-06): erwin's FEModel_DDL persists the absolute
    /// PATH (not the content) of the DDL FE-option XML into the model's FE-options
    /// state. That path is a per-user, per-session temp path (Path.GetTempPath()
    /// resolves to %LOCALAPPDATA%\Temp\&lt;session#&gt;). When a DIFFERENT Windows user
    /// or RDP session later opens the same Mart model and runs Generate DDL, the
    /// Alter Script Wizard re-reads the STALE path baked by the original user
    /// (e.g. C:\Users\Emre\AppData\Local\Temp\3\...), which does not exist for the
    /// current user, so erwin pops a modal "... was not found" and the hidden
    /// wizard auto-open returns 0 ("failed to auto-open wizard"). Clicking OK lets
    /// erwin continue with its built-in FE defaults and produce the DDL.
    ///
    /// The watcher runs on a BACKGROUND thread (the add-in STA thread is blocked
    /// inside the native OpenAlterScriptWizardHidden call while the modal is up),
    /// and is tightly scoped by matching the dialog body text against our own
    /// "erwin_addin_ddl_fe_opt" filename + "not found", so it can never dismiss an
    /// unrelated erwin dialog. All window-text reads go through the hang-safe
    /// <see cref="Win32Helper.GetWindowTextNoHang"/> (SendMessageTimeout).
    /// </summary>
    internal static class FeOptionPopupDismisser
    {
        private const uint BM_CLICK = 0x00F5;
        private const int PollMs = 60;
        private const string FileMarker = "erwin_addin_ddl_fe_opt";
        private const string NotFoundMarker = "was not found";

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);

        [DllImport("user32.dll")]
        private static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>
        /// Start the watcher. Dispose the returned token to stop it (the watcher
        /// also stops dismissing once it has clicked OK on a matching popup, but
        /// keeps polling until disposed in case erwin re-raises it).
        /// </summary>
        public static IDisposable Start(Action<string> log)
        {
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            // Dedicated background thread (NOT a thread-pool task that could starve):
            // enumerate top-level windows, find OUR not-found modal, click its OK.
            var thread = new Thread(() => Loop(token, log))
            {
                IsBackground = true,
                Name = "FeOptionPopupDismisser",
            };
            thread.Start();
            return new Stopper(cts);
        }

        private static void Loop(CancellationToken token, Action<string> log)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    EnumWindows((hWnd, _) =>
                    {
                        if (token.IsCancellationRequested) return false;
                        if (!IsWindowVisible(hWnd)) return true;
                        if (GetClass(hWnd) != "#32770") return true;

                        IntPtr okBtn = IntPtr.Zero;
                        bool matched = false;
                        EnumChildWindows(hWnd, (child, __) =>
                        {
                            string cls = GetClass(child);
                            string txt = Win32Helper.GetWindowTextNoHang(child) ?? string.Empty;
                            if (cls == "Button" && string.Equals(txt.Trim(), "OK", StringComparison.OrdinalIgnoreCase))
                                okBtn = child;
                            if (txt.IndexOf(FileMarker, StringComparison.OrdinalIgnoreCase) >= 0 &&
                                txt.IndexOf(NotFoundMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                                matched = true;
                            return true; // keep scanning all children (need both button + text)
                        }, IntPtr.Zero);

                        if (matched && okBtn != IntPtr.Zero)
                        {
                            log?.Invoke($"FeOptionPopupDismisser: auto-clicking OK on stale FE-option 'not found' modal (hwnd=0x{hWnd.ToInt64():X}).");
                            PostMessage(okBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        }
                        return true;
                    }, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    // Never let the watcher crash the background thread silently
                    // without a trace; it is best-effort, so log and keep polling.
                    log?.Invoke($"FeOptionPopupDismisser: poll error (continuing): {ex.GetType().Name}: {ex.Message}");
                }

                if (token.IsCancellationRequested) break;
                try { Thread.Sleep(PollMs); } catch { /* sleep interrupted on dispose */ }
            }
        }

        private static string GetClass(IntPtr hWnd)
        {
            var sb = new StringBuilder(64);
            int n = GetClassName(hWnd, sb, sb.Capacity);
            return n > 0 ? sb.ToString() : string.Empty;
        }

        private sealed class Stopper : IDisposable
        {
            private CancellationTokenSource _cts;
            public Stopper(CancellationTokenSource cts) { _cts = cts; }
            public void Dispose()
            {
                var cts = Interlocked.Exchange(ref _cts, null);
                if (cts == null) return;
                try { cts.Cancel(); } catch { /* already disposed */ }
                try { cts.Dispose(); } catch { /* best-effort */ }
            }
        }
    }
}
