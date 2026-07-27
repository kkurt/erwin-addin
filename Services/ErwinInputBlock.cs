#nullable enable
using System;
using System.Threading;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Makes the add-in window behave like a modal owner over erwin (WP 329): while the
    /// add-in form is on screen, erwin's main frame is DISABLED so the user cannot click
    /// into the model behind it. To work in erwin the user minimizes the add-in (the X
    /// button and Alt+F4 already minimize instead of closing), which releases the block;
    /// restoring the window re-applies it.
    /// <para>
    /// Mechanism: <c>EnableWindow(erwinMainFrame, false)</c> - the same primitive WinForms
    /// itself applies to every top-level window of the process when the add-in opens a
    /// modal dialog (see <see cref="Win32Helper.EnableWindowPublic"/>), so a disabled erwin
    /// frame is an already-established state in this process rather than a new one.
    /// </para>
    /// <para>
    /// MUST be suspended while the add-in drives erwin itself. The DDL / compare / From-DB
    /// pipelines synthesize mouse input onto erwin windows that lie BEHIND the add-in form
    /// (that is what the WS_EX_TRANSPARENT pass-through in <c>ToggleBusyOverlay</c> is for),
    /// and a disabled window swallows synthetic mouse input - the automation would silently
    /// stall. <c>ShowBusyOverlay</c> therefore takes a <see cref="Suspend"/> scope for the
    /// whole run and releases it when the overlay is disposed. Ref-counted so overlapping
    /// pipelines cannot lower the suspension early.
    /// </para>
    /// <para>
    /// Fail-safe direction is always "erwin usable": every unexpected path (erwin window
    /// gone, form destroyed, a leaked overlay, DebugMode) leaves the frame ENABLED rather
    /// than risking a user who cannot reach erwin at all. <see cref="Sync"/> reconciles
    /// against the window's ACTUAL enabled state on every call, which also repairs the
    /// drift WinForms causes when it re-enables every top-level after one of the add-in's
    /// own modal dialogs closes.
    /// </para>
    /// </summary>
    internal static class ErwinInputBlock
    {
        // Ref count: automation scopes that need erwin operable. > 0 == no block.
        private static int _suspendDepth;

        // Non-zero == WE are the reason erwin's main frame is disabled. Read by
        // Win32Helper.IsErwinMainWindowBlockedByModal so the add-in does not mistake its
        // own input block for one of erwin's modal dialogs (which would make every timer
        // tick and the Generate DDL guard bail out forever).
        private static IntPtr _blockedHwnd;

        // Log latches so the per-tick self-heal cannot flood the log while a state persists.
        private static bool _skipLogged;
        private static bool _foreignLogged;
        private static bool _noModelLogged;

        /// <summary>
        /// The whole policy, in one pure place: may erwin's frame be disabled right now?
        ///
        /// <para><paramref name="hasOpenModel"/> is the condition that was missing until
        /// 2026-07-26. The block exists to stop the user clicking into THE MODEL behind the
        /// add-in (WP 329) - with no model open there is nothing to protect, and disabling
        /// the frame is pure obstruction: erwin's own File/Mart menus are the only way to
        /// open one, and the title-bar X stops working too. Right after an Integrate closes
        /// both models that left an erwin which was alive and pumping but ignored every
        /// click and could not even be closed.</para>
        ///
        /// <para>Every other input is a veto for the same reason it always was:
        /// <paramref name="suspended"/> because add-in automation synthesizes mouse input
        /// that a disabled window swallows; <paramref name="debugMode"/> because a
        /// developer is driving erwin by hand; the two gates because a wizard or Mart save
        /// is mid-flight.</para>
        /// </summary>
        internal static bool ShouldBlock(bool addinOnScreen, bool suspended, bool debugMode,
                                         bool wizardGateOpen, bool martSaveActive, bool hasOpenModel)
            => addinOnScreen && hasOpenModel
               && !suspended && !debugMode && !wizardGateOpen && !martSaveActive;

        /// <summary>True while erwin's main frame is disabled BY THIS BLOCK. Set ONLY when this
        /// class performed the enabled-&gt;disabled transition itself, never when the frame was
        /// found already disabled (that disable belongs to an erwin modal).</summary>
        public static bool IsApplied => Volatile.Read(ref _blockedHwnd) != IntPtr.Zero;

        /// <summary>The frame this block currently holds disabled, or Zero. Lets the add-in
        /// window notice when that frame has been raised over it.</summary>
        public static IntPtr BlockedWindow => Volatile.Read(ref _blockedHwnd);

        /// <summary>True while at least one automation scope needs erwin operable.</summary>
        public static bool IsSuspended => Volatile.Read(ref _suspendDepth) > 0;

        /// <summary>
        /// Suspend the block for the duration of an add-in-driven erwin automation run
        /// (DDL generation, version compare, From-DB, UDP sync, config reload). Releases
        /// the block immediately and keeps it off until every scope is disposed. Callers
        /// are on the UI thread (the add-in shares erwin's STA), which keeps the
        /// EnableWindow call - and the WM_ENABLE it sends to that same thread - in-thread.
        /// </summary>
        public static IDisposable Suspend()
        {
            if (Interlocked.Increment(ref _suspendDepth) == 1)
            {
                AddinLogger.Log("[ERWIN-BLOCK] suspended - add-in automation needs erwin operable");
                Sync(false);
            }
            return new SuspendScope();
        }

        /// <summary>
        /// Reconcile the block with <paramref name="wantBlock"/> (the add-in form is on
        /// screen and idle). Idempotent and driven by the window's real enabled state, so
        /// it re-applies after WinForms silently re-enabled erwin when an add-in modal
        /// closed, and never double-toggles. A suspension or DebugMode forces release.
        /// </summary>
        public static void Sync(bool wantBlock)
        {
            try
            {
                // Pinned to OUR OWN erwin process. Win32Helper.GetErwinMainWindow is
                // deliberately process-agnostic and Z-order dependent (it takes the first
                // visible "erwin DM" window of ANY erwin process), which is harmless for the
                // read-only / transient-input callers but NOT for us: we mutate persistent
                // window state, so hitting a second instance's frame would disable a window
                // this add-in is not responsible for and cannot reliably give back.
                IntPtr main = ResolveOwnErwinFrame();
                IntPtr owned = Volatile.Read(ref _blockedHwnd);

                // The frame we disabled is no longer the one we resolve (instance closed, a
                // new frame, or the previous resolve picked a different window): give the OLD
                // one back FIRST so it can never be orphaned in a disabled state - that is
                // precisely the "user cannot click anything in erwin" lockout.
                if (owned != IntPtr.Zero && owned != main)
                {
                    Win32Helper.EnableWindowPublic(owned, true);
                    Volatile.Write(ref _blockedHwnd, IntPtr.Zero);
                    owned = IntPtr.Zero;
                    AddinLogger.Log("[ERWIN-BLOCK] blocked frame changed/disappeared - previous frame re-enabled");
                }

                if (main == IntPtr.Zero) return; // no frame of ours to act on

                // Ground truth for "is there a model behind the add-in at all". Timeout
                // bounded, and Zero for a hung or non-MDI frame - so "cannot tell" feeds
                // the policy as NO MODEL, landing on ENABLED like every other fail-safe here.
                bool hasOpenModel = Win32Helper.GetActiveMdiChild(main) != IntPtr.Zero;

                bool blockWanted = ShouldBlock(wantBlock, IsSuspended, DebugMode.Enabled,
                                               AlterWizardGate.IsOpen, MartSaveGate.IsActive, hasOpenModel);
                if (wantBlock && !blockWanted && !hasOpenModel && !_noModelLogged)
                {
                    _noModelLogged = true;
                    AddinLogger.Log("[ERWIN-BLOCK] no model open - erwin left usable (nothing to block the user out of)");
                }
                if (hasOpenModel) _noModelLogged = false;
                wantBlock = blockWanted;

                bool enabled = Win32Helper.IsWindowEnabledPublic(main);
                if (wantBlock)
                {
                    if (enabled)
                    {
                        Win32Helper.EnableWindowPublic(main, false);
                        Volatile.Write(ref _blockedHwnd, main);
                        _skipLogged = false;
                        AddinLogger.Log("[ERWIN-BLOCK] erwin input blocked - minimize the add-in to use erwin");
                    }
                    else if (owned == IntPtr.Zero)
                    {
                        // Already disabled and NOT by us: erwin's own modal dialog owns this
                        // disable (Mart Save, Properties, Print, a Mart-offline popup - the
                        // user can raise one any time, because minimizing the add-in releases
                        // our block by design). We must NOT adopt it:
                        //   * IsApplied would go true and Win32Helper.IsErwinMainWindowBlockedByModal
                        //     would then report "no modal" while a real one is up, blinding the
                        //     six guards that exist to stop SCAPI work under a modal (the
                        //     verified STA deadlock and the 0xC0000005 in coreclr.dll), and
                        //   * a later release would re-enable the frame UNDERNEATH erwin's live
                        //     modal loop.
                        // Nothing to do: when erwin closes its dialog it re-enables its frame
                        // and the next Sync applies the block for real (self-healing).
                        if (!_skipLogged)
                        {
                            _skipLogged = true;
                            AddinLogger.Log("[ERWIN-BLOCK] frame already disabled by erwin itself (modal up) - NOT claiming it; block deferred");
                        }
                    }
                }
                else if (owned != IntPtr.Zero)
                {
                    // Only ever re-enable a frame WE disabled (guaranteed now that ownership is
                    // claimed exclusively on a transition we performed) AND only while no erwin
                    // modal is up: erwin can raise one asynchronously on top of our block (Mart
                    // offline / keepalive popup), and re-enabling its frame under a live MFC
                    // modal loop is the concurrent-modal state this codebase treats as a crash
                    // trigger. Dropping ownership without enabling is correct - erwin re-enables
                    // its own frame when it closes that dialog.
                    if (!enabled)
                    {
                        if (Win32Helper.HasEnabledOwnedPopup(main))
                            AddinLogger.Log("[ERWIN-BLOCK] release skipped the re-enable: an erwin modal owns the frame now (erwin will restore it)");
                        else
                            Win32Helper.EnableWindowPublic(main, true);
                    }
                    Volatile.Write(ref _blockedHwnd, IntPtr.Zero);
                    _skipLogged = false;
                    AddinLogger.Log("[ERWIN-BLOCK] erwin input released");
                }
            }
            catch (Exception ex)
            {
                // Never let this break a tick or a form event; on doubt leave erwin usable.
                AddinLogger.Log($"[ERWIN-BLOCK] Sync error ({ex.GetType().Name}: {ex.Message}) - attempting release");
                try { ReleaseNow("sync error"); } catch { /* nothing further we can do */ }
            }
        }

        /// <summary>
        /// erwin's main frame IN THIS PROCESS, or <see cref="IntPtr.Zero"/> when the resolved
        /// window belongs to another erwin instance (the add-in is registered per user, so
        /// every running erwin loads its own copy) or none is found.
        /// </summary>
        private static IntPtr ResolveOwnErwinFrame()
        {
            IntPtr main = Win32Helper.GetErwinMainWindow();
            if (main == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                uint pid = Win32Helper.GetWindowThreadProcessIdPublic(main);
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                if (pid != (uint)self.Id)
                {
                    if (!_foreignLogged)
                    {
                        _foreignLogged = true;
                        AddinLogger.Log($"[ERWIN-BLOCK] resolved erwin frame belongs to another instance (pid {pid}) - not touching it");
                    }
                    return IntPtr.Zero;
                }
                _foreignLogged = false;
                return main;
            }
            catch (Exception ex)
            {
                AddinLogger.Log($"[ERWIN-BLOCK] own-frame check failed ({ex.Message}) - not blocking");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Unconditional release, ignoring suspensions. The last-ditch path for form
        /// teardown / process exit so a crashed or force-closed add-in can never leave the
        /// user with an erwin window that refuses every click.
        /// </summary>
        public static void ReleaseNow(string reason)
        {
            IntPtr blocked = Interlocked.Exchange(ref _blockedHwnd, IntPtr.Zero);
            if (blocked == IntPtr.Zero) return;
            try
            {
                // Same modal guard as the Sync release path: never hand input back to a frame
                // that erwin's own dialog is currently holding disabled.
                if (Win32Helper.HasEnabledOwnedPopup(blocked))
                {
                    AddinLogger.Log($"[ERWIN-BLOCK] release ({reason}) skipped the re-enable: an erwin modal owns the frame");
                    return;
                }
                Win32Helper.EnableWindowPublic(blocked, true);
                AddinLogger.Log($"[ERWIN-BLOCK] erwin input released ({reason})");
            }
            catch (Exception ex)
            {
                AddinLogger.Log($"[ERWIN-BLOCK] release failed ({reason}): {ex.Message}");
            }
        }

        private sealed class SuspendScope : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (Interlocked.Decrement(ref _suspendDepth) == 0)
                    AddinLogger.Log("[ERWIN-BLOCK] automation finished - block may re-apply");
            }
        }
    }
}
