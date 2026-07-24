#nullable enable
using System;
using System.Threading;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// True while the add-in is driving erwin's native Mart Save (ribbon
    /// WM_COMMAND 1061 -&gt; "Description for ..." dialog -&gt; IDOK -&gt; erwin's
    /// Mart commit). Every add-in UI-thread timer tick returns immediately while
    /// it is, exactly like <see cref="AlterWizardGate"/> does for the alter
    /// wizard.
    /// <para>
    /// Why: erwin's cold Mart commit is long and shows a progress dialog whose
    /// modal message loop dispatches WM_TIMER, and the whole save runs under the
    /// modal DDL-review / description-dialog loops - all of which pump messages.
    /// An ungated periodic tick therefore makes live SCAPI/COM model reads
    /// (validation walk, reconnect PU scan, 100 ms window monitor) IN THE MIDDLE
    /// of the in-progress commit transaction, which reenters/deadlocks erwin
    /// (observed 2026-07-23: the description dialog was IDOK'd then never closed,
    /// erwin frozen - the same reentrancy class the alter wizard hit). The DDL
    /// generation pipeline already suspends every timer for its duration; the
    /// Mart Save path did not, so this gate closes the same window for it.
    /// </para>
    /// <para>
    /// Unlike <see cref="AlterWizardGate"/> (a Win32 title probe) the Mart Save
    /// has no stable top-level title to watch - erwin's description dialog is
    /// SW_HIDE'd and the commit progress dialog's title is not fixed - so this is
    /// an explicit, ref-counted flag the save call sites raise around the
    /// automation via <see cref="Enter"/>.
    /// </para>
    /// </summary>
    internal static class MartSaveGate
    {
        // Ref count rather than a bare bool so overlapping / nested saves never
        // lower the gate early (the classic DDL push save and the promotion save
        // share the same primitive).
        private static int _depth;

        /// <summary>True while at least one Mart Save automation run is in flight.</summary>
        public static bool IsActive => Volatile.Read(ref _depth) > 0;

        /// <summary>
        /// Raise the gate for the duration of a Mart Save. Dispose the returned
        /// scope (end of a <c>using</c>) lowers it. Ref-counted, so only the
        /// first Enter logs "paused" and only the last Dispose logs "resumed".
        /// </summary>
        public static IDisposable Enter()
        {
            if (Interlocked.Increment(ref _depth) == 1)
                AddinLogger.Log("[MARTSAVE-GATE] Mart Save active - add-in timer ticks paused");
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (Interlocked.Decrement(ref _depth) == 0)
                    AddinLogger.Log("[MARTSAVE-GATE] Mart Save done - add-in timer ticks resumed");
            }
        }
    }
}
