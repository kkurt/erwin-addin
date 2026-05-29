using System;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Toggle for "show me what's happening" debug runs of the Generate DDL
    /// pipelines. The production pipelines drive erwin's CC wizard / Alter
    /// Script Wizard / Mart picker / RD dialog at machine speed with all
    /// child windows hidden (WinEvent hook + ShowWindow SW_HIDE + busy
    /// overlay). When debugging an empty diff / unexpected DDL / dropped
    /// command we want the opposite: keep dialogs visible AND insert 5 s
    /// breakpoints between phase transitions so a human can actually read
    /// what is on screen.
    ///
    /// Per-click, in-memory only. No env var, no registry, no persistence -
    /// the dev surface (a second "Generate DDL (debug)" button on the DDL
    /// tab, visible only in non-PACKAGED builds) flips this flag for the
    /// duration of one pipeline run by setting it from the click handler,
    /// then the production button click handler resets it to off. An
    /// env-var-based predecessor was removed 2026-05-27 after it silently
    /// failed to propagate to erwin.exe; a brief HKCU registry detour was
    /// removed the same day in favour of the two-button UX.
    ///
    /// Default OFF. Packaged builds (#if PACKAGED) have no way to flip it.
    /// </summary>
    public static class DebugMode
    {
        private const int DefaultDelayMs = 3000;

        private static bool _enabled;

        /// <summary>True while the current pipeline run should behave in debug mode.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Delay applied at every <see cref="Pause"/> call when
        /// <see cref="Enabled"/> is true. 3 000 ms by default - a consistent,
        /// equal settle between pipeline steps (user asked for even ~3s gaps,
        /// 2026-05-29) that also lets a human read the screen.
        /// </summary>
        public static int TransitionDelayMs { get; set; } = DefaultDelayMs;

        /// <summary>
        /// Whether the pipeline should keep normally-hidden dialogs visible
        /// (CC wizard, Mart picker, RD dialog, hidden alter script wizard).
        /// Mirrors <see cref="Enabled"/> today; kept as a separate property
        /// so we can split the two knobs later (e.g. "slow but still hide").
        /// </summary>
        public static bool KeepDialogsVisible => _enabled;

        /// <summary>
        /// Pause for <see cref="TransitionDelayMs"/> when debug mode is on,
        /// logging a marker so the timeline in the debug log lines up with
        /// what the user sees on screen. No-op when debug is off so the
        /// callers can litter the production code with these calls at zero
        /// cost.
        /// </summary>
        public static void Pause(string label, Action<string> log = null)
        {
            if (!_enabled) return;
            try { log?.Invoke($"[DDL-DEBUG] {label} - pausing {TransitionDelayMs} ms"); } catch { /* never let log failure abort a debug pause */ }
            try { System.Threading.Thread.Sleep(TransitionDelayMs); } catch { /* sleep interruption is benign */ }
        }
    }
}
