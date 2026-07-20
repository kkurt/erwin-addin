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

        // erwin r10.10 FE alter wizard's exact top-level title (screenshot-verified;
        // same window for the manual Ctrl+Alt+T and the bridge-automated path).
        private const string WizardTitle = "Forward Engineer Alter Script Schema Generation Wizard";

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
                var now = DateTime.UtcNow;
                if ((now - _lastProbeUtc).TotalMilliseconds > 200)
                {
                    _lastProbeUtc = now;
                    bool open = FindWindowW(null, WizardTitle) != IntPtr.Zero;
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
    }
}
