#nullable enable
using System;
using System.Runtime.InteropServices;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// DIAGNOSTIC GATE (2026-07-20, black-rectangle isolation Test 1). True while
    /// erwin's Forward Engineer Alter Script wizard is open in this process; every
    /// add-in timer tick returns immediately while it is.
    /// <para>
    /// Why: the wizard's modal message loop dispatches WM_TIMER, so our periodic
    /// SCAPI/Win32 work executes IN THE MIDDLE of erwin's wizard rendering
    /// (reentrancy into GDM/FE code that is actively drawing). The black
    /// rectangles appear ONLY when the add-in is resident and the alter wizard
    /// runs - in ANY mode (automated, manual Ctrl+Alt+T, debug-visible, full
    /// hooks or zero inline hooks) - and nondeterministically on the 1st..3rd
    /// run, which fits a timer-phase race rather than any wizard-lifecycle,
    /// compositor, or detour cause (all of those were bisected away 2026-07-19/20).
    /// If the blacks stop with this gate, the reentrant timer work is the
    /// corruptor and this gate (kept tight) is the fix; if they persist, the
    /// in-process residency itself is implicated and DDL generation moves to a
    /// worker process.
    /// </para>
    /// </summary>
    internal static class AlterWizardGate
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string? cls, string? title);

        // Top-level titles of every erwin modal whose message pump dispatches WM_TIMER
        // into our ticks. Probed by exact title because FindWindowW needs one.
        //   [0] FE alter wizard (screenshot-verified; same window for the manual
        //       Ctrl+Alt+T and the bridge-automated path).
        //   [1..2] the Complete Compare wizard - which "Mart > Merge" (WM_COMMAND 1402)
        //       and "Mart > Review" (1168) also open. Added 2026-07-26 after an
        //       ungated integrate run repainted the add-in's own window wrong: the CC
        //       wizard is the SAME hazard under a different caption, and the user can
        //       open it from the Review button with no automation involved at all, so a
        //       caller-raised scope alone would not cover it.
        private static readonly string[] WizardTitles =
        {
            "Forward Engineer Alter Script Schema Generation Wizard",
            "Right Model Selection",
            "Model Selection",
        };

        private static DateTime _lastProbeUtc = DateTime.MinValue;
        private static bool _lastResult;

        /// <summary>
        /// True while the alter wizard window exists. The Win32 probe is cached
        /// for 200 ms so even the 100 ms window-monitor tick costs at most five
        /// FindWindow calls per second.
        /// </summary>
        public static bool IsOpen
        {
            get
            {
                // An explicitly declared scope wins without probing: the caller KNOWS a
                // wizard is up, and the probe below only recognises one title.
                if (System.Threading.Volatile.Read(ref _manualDepth) > 0) return true;

                var now = DateTime.UtcNow;
                if ((now - _lastProbeUtc).TotalMilliseconds > 200)
                {
                    _lastProbeUtc = now;
                    bool open = false;
                    foreach (var title in WizardTitles)
                    {
                        if (FindWindowW(null, title) != IntPtr.Zero) { open = true; break; }
                    }
                    if (open != _lastResult)
                    {
                        AddinLogger.Log(open
                            ? "[WIZARD-GATE] alter wizard OPEN - all add-in timer ticks paused"
                            : "[WIZARD-GATE] alter wizard CLOSED - add-in timer ticks resumed");
                    }
                    _lastResult = open;
                }
                return _lastResult;
            }
        }

        // Explicitly-declared gate scopes, ref-counted like MartSaveGate. The title
        // probe above only knows the FE alter wizard; erwin has other modal wizards
        // whose pump is just as hostile to reentrant timer work - the Complete Compare
        // wizard, which "Mart > Merge" opens under a DIFFERENT title
        // ("Right Model Selection" / "Model Selection"). Driving one of those without a
        // gate reproduced the documented corruption exactly: the add-in's own window
        // came back mis-painted after an integrate run (2026-07-26).
        private static int _manualDepth;

        /// <summary>
        /// Raise the gate for an erwin modal wizard this class cannot detect by title
        /// (Complete Compare / Mart Merge). Dispose the returned scope to lower it.
        /// Ref-counted so nested/overlapping runs never lower it early.
        /// </summary>
        public static IDisposable Enter(string reason)
        {
            if (System.Threading.Interlocked.Increment(ref _manualDepth) == 1)
                AddinLogger.Log($"[WIZARD-GATE] {reason} - add-in timer ticks paused");
            return new Scope(reason);
        }

        private sealed class Scope : IDisposable
        {
            private readonly string _reason;
            private bool _disposed;

            public Scope(string reason) { _reason = reason; }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (System.Threading.Interlocked.Decrement(ref _manualDepth) == 0)
                    AddinLogger.Log($"[WIZARD-GATE] {_reason} finished - add-in timer ticks resumed");
            }
        }
    }
}
