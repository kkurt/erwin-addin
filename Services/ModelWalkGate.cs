#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// True while the add-in is running a synchronous whole-model SCAPI walk on the UI
    /// thread. Every add-in UI-thread timer tick returns immediately while it is, exactly
    /// like <see cref="AlterWizardGate"/> does for erwin's modal wizards and
    /// <see cref="MartSaveGate"/> does for the native Mart commit.
    ///
    /// <para><b>Why this exists (measured 2026-07-27).</b> The approval blocking-rule walk
    /// took <b>45 m 56 s</b> over 8,401 columns of one model (328 ms per column) for a
    /// single Regexp rule reading a single string property. The same session had walked
    /// ~10,000 SCAPI properties in 734 ms minutes earlier, so the walk's own work was worth
    /// seconds, not three quarters of an hour.</para>
    ///
    /// <para>The mechanism is the one this codebase has now hit three times. A synchronous
    /// walk does NOT stop the message loop: the outbound SCAPI/COM calls pump it themselves,
    /// so <c>WM_TIMER</c> keeps being dispatched and every ungated tick runs its own SCAPI
    /// work in the middle of the walk. Proof from the live log: between the walk's first and
    /// last line, the ONLY traffic was 46 glossary refreshes at exactly 60 s apart -
    /// <c>_glossaryRefreshTimer</c> is a <see cref="System.Windows.Forms.Timer"/>, whose
    /// Tick can only be delivered by a pumping loop. A blocked thread would have coalesced
    /// all 46 into one burst at the end. It did not.</para>
    ///
    /// <para><b>Not reusing the existing two gates is deliberate.</b>
    /// <c>ErwinInputBlock.ShouldBlock</c> consumes <see cref="AlterWizardGate.IsOpen"/> and
    /// <see cref="MartSaveGate.IsActive"/> as inputs to erwin's input-block policy, so
    /// raising either one to suspend timers would silently re-enable erwin's frame
    /// mid-walk. This flag is fed to the timer ticks ONLY, via
    /// <see cref="AddinTickGate.ShouldSkip"/>.</para>
    /// </summary>
    internal static class ModelWalkGate
    {
        // Ref count rather than a bare bool so nested / overlapping walks never lower the
        // gate early, the same reason MartSaveGate counts.
        private static int _depth;

        // Skipped-tick census, kept only for the duration of one walk. This is the
        // measurement that decides whether reentrant tick work really is where the missing
        // time went: it counts the ticks the gate PREVENTED, per call site.
        private static readonly object CensusLock = new object();
        private static Dictionary<string, int> _skippedTicks = new Dictionary<string, int>(StringComparer.Ordinal);
        private static Stopwatch? _clock;

        /// <summary>True while at least one whole-model walk is in flight.</summary>
        public static bool IsActive => Volatile.Read(ref _depth) > 0;

        /// <summary>
        /// Raise the gate for the duration of a whole-model walk. Disposing the returned
        /// scope (end of a <c>using</c>) lowers it and logs how long the walk took and how
        /// many timer ticks were suspended, per site.
        /// </summary>
        /// <param name="reason">User-facing name of the walk, e.g. "Approval blocking gate".</param>
        public static IDisposable Enter(string reason)
        {
            if (Interlocked.Increment(ref _depth) == 1)
            {
                lock (CensusLock)
                {
                    _skippedTicks = new Dictionary<string, int>(StringComparer.Ordinal);
                    _clock = Stopwatch.StartNew();
                }
                // The thread is on the line permanently, not as a probe: a whole-model walk that
                // runs on anything other than the STA that owns the SCAPI objects pays a
                // cross-apartment round trip PER CALL (measured at 400x for a direct member
                // read), and that is invisible in every other symptom.
                var thread = System.Threading.Thread.CurrentThread;
                AddinLogger.Log($"[WALK-GATE] {reason} - add-in timer ticks paused " +
                                $"(thread #{Environment.CurrentManagedThreadId} {thread.GetApartmentState()})");
            }
            return new Scope(reason);
        }

        /// <summary>
        /// Record that one UI-thread tick was suspended by this gate. Called from
        /// <see cref="AddinTickGate.ShouldSkip"/> only while the gate is up, so the census
        /// stays bounded by the walk and costs nothing the rest of the time.
        /// </summary>
        public static void CountSkippedTick(string site)
        {
            if (string.IsNullOrEmpty(site)) return;
            lock (CensusLock)
            {
                _skippedTicks.TryGetValue(site, out int count);
                _skippedTicks[site] = count + 1;
            }
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
                if (Interlocked.Decrement(ref _depth) != 0) return;

                long elapsedMs;
                string census;
                int total;
                lock (CensusLock)
                {
                    elapsedMs = _clock?.ElapsedMilliseconds ?? 0;
                    _clock = null;
                    total = _skippedTicks.Values.Sum();
                    census = _skippedTicks.Count == 0
                        ? "none"
                        : string.Join(", ", _skippedTicks
                            .OrderByDescending(kv => kv.Value)
                            .Select(kv => $"{kv.Key}={kv.Value}"));
                    _skippedTicks = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                AddinLogger.Log(
                    $"[WALK-GATE] {_reason} done in {elapsedMs} ms - add-in timer ticks resumed; " +
                    $"{total} tick(s) suspended [{census}]");
            }
        }
    }
}
